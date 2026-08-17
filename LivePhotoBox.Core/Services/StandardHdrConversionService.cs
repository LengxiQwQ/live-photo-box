using LivePhotoBox.Services.Protocols;
using ImageMagick;
using System;
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
        string sourcePath, string outputDirectory, CancellationToken token = default,
        float gainMapBoost = 8.0f)
    {
        Directory.CreateDirectory(outputDirectory);

        string gainMapPath = TempFileService.AllocateTempPath(outputDirectory, "uhdr_gainmap", "jpg");
        string heicPath = TempFileService.AllocateTempPath(outputDirectory, "uhdr", "heic");

        try
        {
            if (!await TryExtractJpegGainMapAsync(sourcePath, gainMapPath, token))
            {
                throw new InvalidDataException("Source JPEG does not contain a standard Ultra HDR gain map.");
            }

            ApplyGainMapBoost(gainMapPath, gainMapBoost);
            await RunHeifEncTwoImagesAsync(sourcePath, gainMapPath, heicPath, token);
            if (!HeifAuxImageWriter.TryAddHdrGainMapAux(heicPath, out string? patchError))
            {
                throw new InvalidOperationException($"Failed to add Apple hdrgainmap auxiliary image: {patchError}");
            }

            await InjectAppleHdrGainMapXmpAsync(heicPath, token);
            return heicPath;
        }
        catch
        {
            TryDelete(gainMapPath);
            TryDelete(heicPath);
            throw;
        }
        finally
        {
            TryDelete(gainMapPath);
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

    private static float[] ReadRgbFloat(MagickImage image)
    {
        ushort[] raw = image.GetPixels().ToShortArray(PixelMapping.RGB);
        var result = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            result[i] = raw[i] / 65535f;
        }

        return result;
    }

    private static float[] ReadGrayFloat(MagickImage image)
    {
        ushort[] raw = image.GetPixels().ToShortArray(PixelMapping.RGB);
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

    private static void ApplyGainMapBoost(string gainMapPath, float factor)
    {
        if (Math.Abs(factor - 1.0f) < 0.0001f)
        {
            return;
        }

        using var image = new MagickImage(gainMapPath);
        image.Evaluate(Channels.All, EvaluateOperator.Multiply, factor);
        image.Write(gainMapPath);
    }

    private static async Task InjectAppleHdrGainMapXmpAsync(string heicPath, CancellationToken token)
    {
        string? exifToolPath = ExternalToolLocator.FindExifTool();
        if (string.IsNullOrEmpty(exifToolPath))
        {
            return;
        }

        string directory = Path.GetDirectoryName(heicPath)!;
        string xmpPath = Path.Combine(directory, $".lpb_apple_hdr_{Guid.NewGuid():N}.xmp");
        string outputPath = Path.Combine(directory, $".lpb_apple_hdr_{Guid.NewGuid():N}.heic");

        try
        {
            string xmp =
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" " +
                "xmlns:HDRGainMap=\"http://ns.apple.com/HDRGainMap/1.0/\" " +
                "HDRGainMap:Version=\"0.2.0.0\" " +
                "HDRGainMap:Headroom=\"20.0\"/>" +
                "</rdf:RDF></x:xmpmeta>";

            await File.WriteAllTextAsync(xmpPath, xmp, new UTF8Encoding(false), token);
            await LivePhotoRepairService.RunExifToolAsync(
                token,
                $"-xmp<={xmpPath}",
                "-o", outputPath,
                heicPath);

            if (!File.Exists(outputPath))
            {
                return;
            }

            File.Move(outputPath, heicPath, overwrite: true);
        }
        catch
        {
            // XMP 注入失败不阻断转换；基础 HEIC + auxC 仍然有效。
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
