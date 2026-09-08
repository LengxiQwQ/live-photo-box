using LivePhotoBox.Services.Protocols;
using LivePhotoBox.Models;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services;

// 标准 HDR 互转：
//   JPG Ultra HDR (ISO 21496-1) -> HEIC Apple hdrgainmap
//   HEIC Apple hdrgainmap -> JPG Ultra HDR
//
// 当前阶段先打通“已经是标准增益图”的文件；华为等厂商私有格式不在这里处理。
public static class StandardHdrConversionService
{
    // Apple HDRGainMapVersion 0.1.0.0 的整数编码（0x00010000）。
    private const int AppleHdrGainMapVersionInteger = 65536;

    public static bool HasStandardJpegGainMap(string sourcePath, CancellationToken token = default)
    {
        return HasNativeGainMap(sourcePath, ImageContainer.Jpeg, token);
    }

    public static bool HasAppleHeicGainMap(string sourcePath, CancellationToken token = default)
    {
        return HasNativeGainMap(sourcePath, ImageContainer.Heic, token);
    }

    private static bool HasNativeGainMap(
        string sourcePath, ImageContainer expectedContainer, CancellationToken token)
    {
        SourceMediaFacts facts = NativeMediaService.InspectMediaAsync(
            sourcePath, null, token).GetAwaiter().GetResult();
        if (!facts.PrimaryImage.IsPresent || facts.PrimaryImage.Container != expectedContainer)
        {
            return false;
        }

        GainMapFacts? gainMap = facts.GainMap;
        if (gainMap is null || !gainMap.IsPresent)
        {
            return false;
        }
        ValidateNativeGainMapBinding(facts, gainMap, expectedContainer);
        return true;
    }

    private static void ValidateNativeGainMapBinding(
        SourceMediaFacts facts, GainMapFacts gainMap, ImageContainer expectedContainer)
    {
        MediaArtifactKind expectedOwnerRole = gainMap.Ownership == AuxiliaryOwnership.Primary
            ? MediaArtifactKind.PrimaryImage
            : MediaArtifactKind.AuxiliaryItem;
        if (gainMap.Container != expectedContainer || gainMap.ByteLength <= 0 ||
            gainMap.OwnerArtifactRole != expectedOwnerRole ||
            gainMap.AuxiliaryIndex >= (uint)facts.AuxiliaryItems.Count)
        {
            throw new InvalidDataException("Native GainMap binding is incomplete.");
        }

        AuxiliaryMediaFacts auxiliary = facts.AuxiliaryItems[(int)gainMap.AuxiliaryIndex];
        if (!auxiliary.IsPresent || auxiliary.Container != gainMap.Container ||
            auxiliary.Representation != gainMap.Representation ||
            auxiliary.Ownership != gainMap.Ownership ||
            auxiliary.ItemId != gainMap.ItemId ||
            auxiliary.ByteOffset != gainMap.ByteOffset ||
            auxiliary.ByteLength != gainMap.ByteLength ||
            !string.Equals(auxiliary.Relationship, gainMap.Relationship, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Native GainMap binding does not match its auxiliary item.");
        }
    }

    public static async Task<string> ConvertJpegToHeicAsync(
        string sourcePath, string outputDirectory, CancellationToken token = default)
    {
        Directory.CreateDirectory(outputDirectory);

        string isoGainMapPath = TempFileService.AllocateTempPath(outputDirectory, "uhdr_gainmap", "jpg");
        string appleGainMapPath = TempFileService.AllocateTempPath(outputDirectory, "apple_gainmap", "jpg");
        string heicPath = TempFileService.AllocateTempPath(outputDirectory, "uhdr", "heic");

        try
        {
            string? sourceContentId = TryReadFormalSourceContentIdentifier(sourcePath, token);
            if (!await TryExtractJpegGainMapAsync(sourcePath, isoGainMapPath, token))
            {
                throw new InvalidDataException(
                    "Native source facts did not bind a standard Ultra HDR GainMap for conversion.");
            }

            // 元数据优先从源文件主 XMP 读（hdrgm 命名空间属主容器所有），
            // 增益图 JPEG 自带的 XMP 作回退；两者都没有时 ReadIsoGainMapMetadata
            // 内部使用默认值。
            IsoGainMapMetadata iso = TryReadIsoGainMapMetadataFromXmp(sourcePath, out IsoGainMapMetadata parsed)
                ? parsed
                : ReadIsoGainMapMetadata(isoGainMapPath, token);
            double headroom = Math.Pow(2.0, iso.HDRCapacityMax);
            WarnIfHeadroomUnrepresentable(headroom);

            (byte[] appleGain, uint width, uint height) = ComputeAppleGainMap(
                isoGainMapPath, iso, headroom);
            WriteGrayJpeg(appleGain, width, height, appleGainMapPath);

            await RunHeifEncTwoImagesAsync(sourcePath, appleGainMapPath, heicPath, token);
            if (!HeifAuxImageWriter.TryAddHdrGainMapAux(heicPath, out string? patchError))
            {
                throw new InvalidOperationException($"Failed to add Apple hdrgainmap auxiliary image: {patchError}");
            }

            await InjectAppleHdrGainMapXmpAsync(heicPath, token);
            InjectAppleHdrMakerNote(
                heicPath, headroom,
                sourceContentId ?? Guid.NewGuid().ToString("D").ToUpperInvariant());

            return heicPath;
        }
        catch
        {
            TryDelete(isoGainMapPath);
            TryDelete(appleGainMapPath);
            TryDelete(heicPath);
            throw;
        }
        finally
        {
            TryDelete(isoGainMapPath);
            TryDelete(appleGainMapPath);
        }
    }

    private static string? TryReadFormalSourceContentIdentifier(
        string sourcePath, CancellationToken token)
    {
        SourceMediaFacts facts = NativeMediaService.InspectMediaAsync(
            sourcePath, null, token).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(facts.PairingIdentifier))
        {
            return null;
        }

        if (!Guid.TryParseExact(facts.PairingIdentifier, "D", out _))
        {
            throw new InvalidDataException(
                "Native source facts exposed a malformed formal ContentIdentifier.");
        }

        return facts.PairingIdentifier;
    }

    public static async Task<string> ConvertHeicToJpegAsync(
        string sourcePath, string outputDirectory, CancellationToken token = default)
    {
        Directory.CreateDirectory(outputDirectory);
        using var workspace = TempFileService.CreateWorkspace("heic_hdr_jpg", outputDirectory);

        string primaryBasePath = workspace.AllocatePath("primary", "jpg");
        await RunHeifDecWithAuxAsync(sourcePath, primaryBasePath, token);

        string directory = Path.GetDirectoryName(primaryBasePath)!;
        string baseName = Path.GetFileNameWithoutExtension(primaryBasePath);
        string appleGainMapPath = Path.Combine(
            directory,
            $"{baseName}-urn_com_apple_photo_2020_aux_hdrgainmap.jpg");

        if (!File.Exists(appleGainMapPath) || new FileInfo(appleGainMapPath).Length == 0)
        {
            throw new InvalidDataException("Source HEIC does not contain an Apple hdrgainmap auxiliary image.");
        }

        double headroom = ReadAppleHeadroom(sourcePath, token)
            ?? throw new InvalidDataException(
                "Apple HDRHeadroom/HDRGain MakerNote values could not be formally extracted by the rebuilt path.");

        string outputPath = TempFileService.AllocateTempPath(outputDirectory, "ultrahdr", "jpg");
        string computedGainMapPath = workspace.AllocatePath("iso_gainmap", "jpg");

        IsoGainMapMetadata metadata = ComputeGoogleGainMap(
            primaryBasePath, appleGainMapPath, computedGainMapPath, headroom);

        UltraHdrJpegWriter.Write(primaryBasePath, computedGainMapPath, outputPath, metadata);
        return outputPath;
    }

    private static double? ReadAppleHeadroom(string sourcePath, CancellationToken token)
    {
        string tags = ReadExifTags(sourcePath, token, "-s", "-n", "-HDRHeadroom", "-HDRGain");
        double? headroom = null;
        double? gain = null;

        foreach (string line in tags.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (name.Equals("HDRHeadroom", StringComparison.Ordinal)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
            {
                headroom = h;
            }
            else if (name.Equals("HDRGain", StringComparison.Ordinal)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double g))
            {
                gain = g;
            }
        }

        if (!headroom.HasValue || !gain.HasValue)
        {
            return null;
        }

        return HdrGainMapCodec.ComputeAppleHeadroom(headroom.Value, gain.Value);
    }

    private static IsoGainMapMetadata ComputeGoogleGainMap(
        string primaryPath,
        string appleGainMapPath,
        string outputGainMapPath,
        double headroom)
    {
        // 与 libultrahdr 的 kSdrOffset / kHdrOffset（1e-7）以及荣耀参考样张（1e-6）同一量级，
        // 避免 ISO 默认 1/64 在暗部过度压缩增益。
        const double offsetSdr = 1e-6;
        const double offsetHdr = 1e-6;

        using var primary = new MagickImage(primaryPath);
        uint width = primary.Width;
        uint height = primary.Height;
        int pixelCount = checked((int)(width * height));

        float[] sdrEncoded = ReadRgbFloat(primary);

        using var appleGain = new MagickImage(appleGainMapPath);
        appleGain.FilterType = FilterType.Lanczos;
        appleGain.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        float[] gainEncoded = ReadGrayFloat(appleGain);

        var sdrLinear = new float[pixelCount * 3];
        var hdrLinear = new float[pixelCount * 3];

        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 3;
            float sr = HdrGainMapCodec.SrgbEotf(sdrEncoded[offset]);
            float sg = HdrGainMapCodec.SrgbEotf(sdrEncoded[offset + 1]);
            float sb = HdrGainMapCodec.SrgbEotf(sdrEncoded[offset + 2]);
            float gain = HdrGainMapCodec.SrgbEotf(gainEncoded[i]);
            float scale = 1.0f + (float)(headroom - 1.0) * gain;

            sdrLinear[offset] = sr;
            sdrLinear[offset + 1] = sg;
            sdrLinear[offset + 2] = sb;
            hdrLinear[offset] = sr * scale;
            hdrLinear[offset + 1] = sg * scale;
            hdrLinear[offset + 2] = sb * scale;
        }

        float[] isoGain = HdrGainMapCodec.ComputeIsoGainMap(
            sdrLinear, hdrLinear, headroom, offsetSdr, offsetHdr, out IsoGainMapMetadata metadata);

        byte[] gray = HdrGainMapCodec.QuantizeGainMap(isoGain);
        WriteGrayJpeg(gray, width, height, outputGainMapPath);
        return metadata;
    }

    private static IsoGainMapMetadata ReadIsoGainMapMetadata(string gainMapPath, CancellationToken token)
    {
        string tags = ReadExifTags(gainMapPath, token,
            "-s", "-n", "-GainMapMin", "-GainMapMax", "-Gamma",
            "-OffsetSDR", "-OffsetHDR", "-HDRCapacityMin", "-HDRCapacityMax");

        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in tags.Split('\n'))
        {
            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                values[name] = parsed;
            }
        }

        return new IsoGainMapMetadata(
            GainMapMin: GetValue(values, "GainMapMin", 0.0),
            GainMapMax: GetValue(values, "GainMapMax", 1.0),
            Gamma: GetValue(values, "Gamma", 1.0),
            OffsetSDR: GetValue(values, "OffsetSDR", 1e-6),
            OffsetHDR: GetValue(values, "OffsetHDR", 1e-6),
            HDRCapacityMin: GetValue(values, "HDRCapacityMin", 0.0),
            HDRCapacityMax: GetValue(values, "HDRCapacityMax", 1.0));
    }

    // 逐像素把 ISO 21496-1 增益图映射成 Apple 增益图。
    // 两者都是半分辨率单通道，因此可直接按像素转换，无需上下采样。
    private static (byte[] Gray, uint Width, uint Height) ComputeAppleGainMap(
        string isoGainMapPath, IsoGainMapMetadata iso, double headroom)
    {
        using var gainImage = new MagickImage(isoGainMapPath);
        uint width = gainImage.Width;
        uint height = gainImage.Height;
        int pixelCount = checked((int)(width * height));

        ushort[] raw = gainImage.GetPixels()!.ToShortArray(PixelMapping.RGB)!;
        var gray = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            float recovery = raw[i * 3] / 65535f;
            float pixelGain = HdrGainMapCodec.DecodeIsoRecovery(recovery, iso);
            float appleGain = HdrGainMapCodec.EncodeAppleGain(pixelGain, headroom);
            gray[i] = (byte)Math.Clamp((int)MathF.Round(appleGain * 255f), 0, 255);
        }

        return (gray, width, height);
    }

    private static double GetValue(IReadOnlyDictionary<string, double> values, string key, double fallback)
        => values.TryGetValue(key, out double value) ? value : fallback;

    private static float[] ReadRgbFloat(MagickImage image)
    {
        ushort[] raw = image.GetPixels()!.ToShortArray(PixelMapping.RGB)!;
        var result = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            result[i] = raw[i] / 65535f;
        }

        return result;
    }

    private static float[] ReadGrayFloat(MagickImage image)
    {
        ushort[] raw = image.GetPixels()!.ToShortArray(PixelMapping.RGB)!;
        int pixelCount = raw.Length / 3;
        var result = new float[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            result[i] = raw[i * 3] / 65535f;
        }

        return result;
    }

    private static void WriteGrayJpeg(byte[] gray, uint width, uint height, string outputPath)
    {
        using var image = new MagickImage(MagickColors.Black, width, height);
        image.ColorSpace = ColorSpace.sRGB;

        var rgb = new byte[gray.Length * 3];
        for (int i = 0; i < gray.Length; i++)
        {
            byte value = gray[i];
            rgb[i * 3] = value;
            rgb[i * 3 + 1] = value;
            rgb[i * 3 + 2] = value;
        }

        image.GetPixels().SetBytePixels(rgb);
        image.Quality = 90;
        image.Format = MagickFormat.Jpeg;
        image.Write(outputPath);
    }

    private static async Task<bool> TryExtractJpegGainMapAsync(
        string sourcePath, string outputPath, CancellationToken token)
    {
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(
            sourcePath, null, token).ConfigureAwait(false);
        if (!facts.PrimaryImage.IsPresent || facts.PrimaryImage.Container != ImageContainer.Jpeg ||
            facts.GainMap is null || !facts.GainMap.IsPresent)
        {
            return false;
        }

        ValidateNativeGainMapBinding(facts, facts.GainMap, ImageContainer.Jpeg);
        await NativeMediaService.ExtractMediaAsync(
            sourcePath, null, facts, null, null, outputPath, token).ConfigureAwait(false);

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidDataException("Native GainMap extraction produced no JPEG auxiliary output.");
        }

        return true;
    }

    // 提取 JPEG 所有 APP1 XMP 段（以 "http://ns.adobe.com/xap/1.0/\0" 开头的段）并拼接文本。
    private static string ExtractXmpText(byte[] data)
    {
        var sb = new StringBuilder();
        int p = 2; // 跳过 SOI
        while (p + 4 <= data.Length)
        {
            if (data[p] != 0xFF)
            {
                p++;
                continue;
            }

            byte marker = data[p + 1];
            if (marker == 0x00 || marker == 0xFF)
            {
                p += 2;
                continue;
            }

            if (marker == 0xD8)
            {
                p += 2;
                continue;
            }

            if (marker == 0xD9)
            {
                break;
            }

            if (marker >= 0xD0 && marker <= 0xD7 || marker == 0x01)
            {
                p += 2;
                continue;
            }

            int segmentLength = (data[p + 2] << 8) | data[p + 3];
            if (segmentLength < 2 || p + 2 + segmentLength > data.Length)
            {
                break;
            }

            if (marker == 0xE1)
            {
                int payloadLength = segmentLength - 2;
                int payloadStart = p + 4;
                byte[] xmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
                if (payloadLength >= xmpHeader.Length
                    && data.AsSpan(payloadStart, xmpHeader.Length).SequenceEqual(xmpHeader))
                {
                    sb.Append(Encoding.UTF8.GetString(
                        data,
                        payloadStart + xmpHeader.Length,
                        payloadLength - xmpHeader.Length));
                }
            }

            p += 2 + segmentLength;
            if (marker == 0xDA)
            {
                // XMP 只存在于图像数据之前的 APP 段，进入 SOS 后停止扫描。
                break;
            }
        }

        return sb.ToString();
    }

    // 从文件的 XMP 文本解析 hdrgm 属性（GainMapMin/Max、Gamma、OffsetSDR/HDR、
    // HDRCapacityMin/Max）。XMP 是主容器的权威描述，优先于增益图 JPEG 自带的 XMP。
    private static bool TryReadIsoGainMapMetadataFromXmp(string sourcePath, out IsoGainMapMetadata metadata)
    {
        metadata = new IsoGainMapMetadata(0, 1, 1, 1e-6, 1e-6, 0, 1);
        try
        {
            byte[] data = File.ReadAllBytes(sourcePath);
            string xmp = ExtractXmpText(data);
            if (string.IsNullOrEmpty(xmp) || !xmp.Contains("hdrgm:", StringComparison.Ordinal))
            {
                return false;
            }

            double? gainMapMin = TryParseHdrgmDouble(xmp, "GainMapMin");
            double? gainMapMax = TryParseHdrgmDouble(xmp, "GainMapMax");
            double? gamma = TryParseHdrgmDouble(xmp, "Gamma");
            double? offsetSdr = TryParseHdrgmDouble(xmp, "OffsetSDR");
            double? offsetHdr = TryParseHdrgmDouble(xmp, "OffsetHDR");
            double? capacityMin = TryParseHdrgmDouble(xmp, "HDRCapacityMin");
            double? capacityMax = TryParseHdrgmDouble(xmp, "HDRCapacityMax");

            metadata = new IsoGainMapMetadata(
                GainMapMin: gainMapMin ?? 0.0,
                GainMapMax: gainMapMax ?? 1.0,
                Gamma: gamma ?? 1.0,
                OffsetSDR: offsetSdr ?? 1e-6,
                OffsetHDR: offsetHdr ?? 1e-6,
                HDRCapacityMin: capacityMin ?? 0.0,
                HDRCapacityMax: capacityMax ?? (gainMapMax ?? 1.0));

            // 只有真正读到 GainMapMax / HDRCapacityMax 才算成功；
            // 仅含 hdrgm:Version 的主 XMP（vivo/一加等）会让调用方回退到增益图自带 XMP。
            return gainMapMax.HasValue || capacityMax.HasValue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogService.Warn(
                $"Failed to parse hdrgm XMP from {Path.GetFileName(sourcePath)}: {ex.Message}",
                source: LogSource.Merge);
            return false;
        }
    }

    private static double? TryParseHdrgmDouble(string xmp, string name)
    {
        Match m = Regex.Match(
            xmp,
            $@"hdrgm:{Regex.Escape(name)}\s*=\s*""(?<v>[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)""",
            RegexOptions.CultureInvariant);
        if (m.Success
            && double.TryParse(
                m.Groups["v"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
        {
            return value;
        }

        return null;
    }

    // Apple MakerNote 可表达的 headroom 区间为 [2^1.5, 2^3]；低于下限会被
    // ComputeAppleMakerValues 钳制。这里比较读回值并记录警告，便于真机排查。
    private static void WarnIfHeadroomUnrepresentable(double headroom)
    {
        const double minStops = 1.5;
        const double maxStops = 3.0;
        double stops = Math.Log2(Math.Max(headroom, 1.0));
        if (stops < minStops)
        {
            LogService.Warn(
                $"Source HDR headroom {headroom:F3}x ({stops:F3} stops) is below the Apple MakerNote "
                + $"representable range ({minStops:F1}–{maxStops:F1} stops); it will be clamped to "
                + $"{Math.Pow(2, minStops):F3}x. HDR boost may differ from the source.",
                source: LogSource.Merge);
        }
        else if (stops > maxStops)
        {
            LogService.Warn(
                $"Source HDR headroom {headroom:F3}x ({stops:F3} stops) exceeds the Apple MakerNote "
                + $"representable range ({minStops:F1}–{maxStops:F1} stops); it will be clamped to "
                + $"{Math.Pow(2, maxStops):F3}x. HDR boost may differ from the source.",
                source: LogSource.Merge);
        }
    }

    private static Task RunHeifEncTwoImagesAsync(
        string primaryPath, string gainMapPath, string outputPath, CancellationToken token)
    {
        throw new NotSupportedException("heif-enc is not supported in the Rebuilt Native engine.");
    }

    private static async Task RunHeifDecWithAuxAsync(
        string sourcePath, string primaryOutputPath, CancellationToken token)
    {
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(
            sourcePath, null, token).ConfigureAwait(false);
        if (!facts.PrimaryImage.IsPresent || facts.PrimaryImage.Container != ImageContainer.Heic ||
            facts.GainMap is null || !facts.GainMap.IsPresent)
        {
            throw new InvalidDataException(
                "Native source facts did not bind an Apple HEIC GainMap for preservation.");
        }

        ValidateNativeGainMapBinding(facts, facts.GainMap, ImageContainer.Heic);

        string directory = Path.GetDirectoryName(primaryOutputPath)
            ?? throw new InvalidDataException("HEIC HDR workspace has no output directory.");
        string baseName = Path.GetFileNameWithoutExtension(primaryOutputPath);
        string gainMapOutputPath = Path.Combine(
            directory, $"{baseName}-urn_com_apple_photo_2020_aux_hdrgainmap.jpg");

        await NativeMediaService.ExtractMediaAsync(
            sourcePath,
            null,
            facts,
            primaryOutputPath,
            null,
            gainMapOutputPath,
            token).ConfigureAwait(false);

        if (!File.Exists(primaryOutputPath) || new FileInfo(primaryOutputPath).Length == 0 ||
            !File.Exists(gainMapOutputPath) || new FileInfo(gainMapOutputPath).Length == 0)
        {
            throw new InvalidDataException(
                "Native HEIC extraction did not preserve both primary and GainMap artifacts.");
        }
    }

    private static string ReadExifTags(string sourcePath, CancellationToken token, params string[] args)
    {
        return string.Empty;
    }

    private static void InjectAppleHdrMakerNote(
        string heicPath, double headroom, string contentId)
    {
        (HdrSignedRational maker33, HdrSignedRational maker48) = HdrGainMapCodec.ComputeAppleMakerValues(headroom);
        if (!Guid.TryParseExact(contentId, "D", out _))
        {
            throw new InvalidOperationException(
                "Apple[HDR] target ContentIdentifier is not a valid generated or formally sourced UUID.");
        }

        byte[] makerNote = AppleMakerNoteWriter.BuildHdrMakerNote(maker33, maker48, contentId);
        byte[] source = File.ReadAllBytes(heicPath);
        if (!NativeAppleMakerNoteWriter.TryInjectMakerNoteIntoHeic(
                source, makerNote, out byte[]? rewritten, out string? error)
            || rewritten is null)
        {
            throw new InvalidOperationException($"Failed to inject Apple HDR MakerNote: {error}");
        }
        File.WriteAllBytes(heicPath, rewritten);
    }

    private static Task InjectAppleHdrGainMapXmpAsync(string heicPath, CancellationToken token)
    {
        throw new NotSupportedException("ExifTool is not supported in the Rebuilt Native engine.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
