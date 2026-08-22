using LivePhotoBox.Services.Protocols;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
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
        string tags = ReadExifTags(sourcePath, token, "-s", "-GainMapImage");
        return tags.Contains("GainMapImage", StringComparison.Ordinal);
    }

    public static bool HasAppleHeicGainMap(string sourcePath, CancellationToken token = default)
    {
        string tags = ReadExifTags(sourcePath, token, "-s", "-AuxiliaryImageType");
        return tags.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", StringComparison.Ordinal);
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
            if (!await TryExtractJpegGainMapAsync(sourcePath, isoGainMapPath, token))
            {
                throw new InvalidDataException("Source JPEG does not contain a standard Ultra HDR gain map.");
            }

            IsoGainMapMetadata iso = ReadIsoGainMapMetadata(isoGainMapPath, token);
            double headroom = Math.Pow(2.0, iso.HDRCapacityMax);

            (byte[] appleGain, uint width, uint height) = ComputeAppleGainMap(
                isoGainMapPath, iso, headroom);
            WriteGrayJpeg(appleGain, width, height, appleGainMapPath);

            await RunHeifEncTwoImagesAsync(sourcePath, appleGainMapPath, heicPath, token);
            if (!HeifAuxImageWriter.TryAddHdrGainMapAux(heicPath, out string? patchError))
            {
                throw new InvalidOperationException($"Failed to add Apple hdrgainmap auxiliary image: {patchError}");
            }

            await InjectAppleHdrGainMapXmpAsync(heicPath, token);
            InjectAppleHdrMakerNote(heicPath, headroom);

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
            ?? throw new InvalidDataException("Source HEIC does not contain Apple HDRHeadroom/HDRGain MakerNote values.");

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

        // 字节数组读取：避免 Magick.NET 不走长路径 API，深目录（≥260 字符）打不开文件。
        using var primary = new MagickImage(File.ReadAllBytes(primaryPath));
        uint width = primary.Width;
        uint height = primary.Height;
        int pixelCount = checked((int)(width * height));

        float[] sdrEncoded = ReadRgbFloat(primary);

        // 用字节数组读取，避免 Magick.NET 不走长路径 API 导致
        // 输出目录较深（路径 ≥260 字符）时 OpenBlob 报 "Not enough space"。
        using var appleGain = new MagickImage(File.ReadAllBytes(appleGainMapPath));
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
        // 字节数组读取：避免 Magick.NET 不走长路径 API，深目录（≥260 字符）打不开文件。
        using var gainImage = new MagickImage(File.ReadAllBytes(isoGainMapPath));
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
        string? exifToolPath = ExternalToolLocator.FindExifTool();
        if (string.IsNullOrEmpty(exifToolPath))
        {
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add("-GainMapImage");
        psi.ArgumentList.Add(sourcePath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start exiftool.");

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);
        await using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, token);
        }

        string stderr = await stderrTask;
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0)
        {
            return false;
        }

        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
    }

    private static async Task RunHeifEncTwoImagesAsync(
        string primaryPath, string gainMapPath, string outputPath, CancellationToken token)
    {
        string? heifEncPath = ExternalToolLocator.FindHeifEnc();
        if (string.IsNullOrEmpty(heifEncPath))
        {
            throw new InvalidOperationException(ResourceService.GetString("Error_HeifEncMissing"));
        }

        var psi = new ProcessStartInfo
        {
            FileName = heifEncPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("90");
        psi.ArgumentList.Add(primaryPath);
        psi.ArgumentList.Add(gainMapPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start heif-enc.");

        string stderr = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException(
                $"heif-enc failed (exit {process.ExitCode}): {stderr.Trim()}");
        }
    }

    private static async Task RunHeifDecWithAuxAsync(
        string sourcePath, string primaryOutputPath, CancellationToken token)
    {
        string? heifDecPath = ExternalToolLocator.FindHeifDec();
        if (string.IsNullOrEmpty(heifDecPath))
        {
            throw new InvalidOperationException(ResourceService.GetString("Error_HeifDecMissing"));
        }

        var psi = new ProcessStartInfo
        {
            FileName = heifDecPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--with-aux");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("90");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(primaryOutputPath);
        psi.ArgumentList.Add(sourcePath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start heif-dec.");

        string stderr = await process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);

        if (process.ExitCode != 0 || !File.Exists(primaryOutputPath))
        {
            throw new InvalidOperationException(
                $"heif-dec failed (exit {process.ExitCode}): {stderr.Trim()}");
        }
    }

    private static string ReadExifTags(string sourcePath, CancellationToken token, params string[] args)
    {
        string? exifToolPath = ExternalToolLocator.FindExifTool();
        if (string.IsNullOrEmpty(exifToolPath))
        {
            return string.Empty;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add(sourcePath);

        using var process = Process.Start(psi);
        if (process == null)
        {
            return string.Empty;
        }

        string stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return stdout;
    }

    private static void InjectAppleHdrMakerNote(string heicPath, double headroom)
    {
        (HdrSignedRational maker33, HdrSignedRational maker48) = HdrGainMapCodec.ComputeAppleMakerValues(headroom);
        byte[] makerNote = AppleMakerNoteWriter.BuildHdrMakerNote(maker33, maker48);
        if (!AppleMakerNoteWriter.TryInjectMakerNoteIntoHeic(heicPath, makerNote, out string? error))
        {
            throw new InvalidOperationException($"Failed to inject Apple HDR MakerNote: {error}");
        }
    }

    private static async Task InjectAppleHdrGainMapXmpAsync(string heicPath, CancellationToken token)
    {
        string? exifToolPath = ExternalToolLocator.FindExifTool();
        if (string.IsNullOrEmpty(exifToolPath))
        {
            throw new InvalidOperationException(ResourceService.GetString("Error_ExifToolMissing"));
        }

        string directory = Path.GetDirectoryName(heicPath)!;
        string xmpPath = Path.Combine(directory, $".lpb_apple_hdr_{Guid.NewGuid():N}.xmp");
        string outputPath = Path.Combine(directory, $".lpb_apple_hdr_{Guid.NewGuid():N}.heic");

        try
        {
            string xmp =
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"XMP Core 6.0.0\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" " +
                "xmlns:HDRGainMap=\"http://ns.apple.com/HDRGainMap/1.0/\">" +
                $"<HDRGainMap:HDRGainMapVersion>{AppleHdrGainMapVersionInteger}</HDRGainMap:HDRGainMapVersion>" +
                "</rdf:Description>" +
                "</rdf:RDF></x:xmpmeta>";

            await File.WriteAllTextAsync(xmpPath, xmp, new UTF8Encoding(false), token);
            await LivePhotoRepairService.RunExifToolAsync(
                token,
                $"-xmp<={xmpPath}",
                "-o", outputPath,
                heicPath);

            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("exiftool did not produce the HDR gain map XMP output.");
            }

            File.Move(outputPath, heicPath, overwrite: true);
        }
        finally
        {
            TryDelete(xmpPath);
            TryDelete(outputPath);
        }
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
