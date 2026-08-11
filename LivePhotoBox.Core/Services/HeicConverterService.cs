using ImageMagick;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    // HEIC/HEIF → JPEG 转码服务
    // 支持两种解码器，可在设置页面中切换：
    // 0 — Magick.NET (ImageMagick + libheif)，默认
    // 1 — heif-dec.exe（libheif + libde265，外部工具）
    // 编码统一使用 heif-enc.exe（libheif + x265，外部工具）。
    // 核心转码不依赖 WinRT / Windows 商店扩展。
    // 转换后均通过 ExifTool 复制元数据（排除 Orientation 以避免双重旋转）。
    public static class HeicConverterService
    {
        // 判断指定文件是否为 HEIC 或 HEIF 格式（仅检查扩展名，不读取文件头）。
        // path: 文件路径
        // 返回: 是 HEIC/HEIF 文件返回 true
        public static bool IsHeicFile(string path)
        {
            return path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
        }

        // 读取用户选择的解码器：0=Magick.NET, 1=heif-dec
        public static int DecoderIndex => AppSettingsService.GetValue("HeicDecoderIndex", 0);

        // ── 公开 API ──────────────────────────────────────

        public static Task<string> ConvertToJpegAsync(string heicPath, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return Task.FromResult(heicPath);

            string jpegPath = Path.Combine(
                Path.GetDirectoryName(heicPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(heicPath) + ".jpg");

            return ConvertInternalAsync(heicPath, jpegPath, quality: 100, token);
        }

        public static Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return Task.FromResult(heicPath);

            // 临时文件名由 TempFileService 分配（GUID 后缀），并发任务互不冲突。
            string tempPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "jpg");

            return ConvertInternalAsync(heicPath, tempPath, quality: 100, token);
        }

        /// <summary>
        /// 转换 HEIC 为 JPEG，可指定质量（1-100）。
        /// 用于导出等不需要 100% 质量的场景，避免文件过大。
        /// </summary>
        public static Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory, int quality, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return Task.FromResult(heicPath);

            // 临时文件名由 TempFileService 分配（GUID 后缀），并发任务互不冲突。
            string tempPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "jpg");

            return ConvertInternalAsync(heicPath, tempPath, quality, token);
        }

        /// <summary>
        /// 将 JPEG 图片转换为 HEIC 格式，用于合并导出时生成 HEIC 变体。
        /// 使用项目内置的 heif-enc.exe（libheif + x265），自包含、零 Windows 商店扩展。
        /// </summary>
        /// <param name="sourcePath">源图片文件路径（仅 JPEG）</param>
        /// <param name="outputDirectory">输出目录</param>
        /// <param name="token">取消令牌</param>
        /// <returns>转换后的 HEIC 文件路径；若输入已是 HEIC 则直接返回原路径</returns>
        public static async Task<string> ConvertToHeicAsync(
            string sourcePath, string outputDirectory, CancellationToken token = default)
        {
            if (IsHeicFile(sourcePath)) return sourcePath;

            // 临时文件名由 TempFileService 分配（GUID 后缀），并发任务互不冲突。
            string heicPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "heic");

            try
            {
                token.ThrowIfCancellationRequested();
                LogService.Merge($"Converting to HEIC (heif-enc): {Path.GetFileName(sourcePath)}");

                string? heifEncPath = ExternalToolLocator.FindHeifEnc();
                if (string.IsNullOrEmpty(heifEncPath))
                    throw new InvalidOperationException(ResourceService.GetString("Error_HeifEncMissing"));

                var psi = new ProcessStartInfo
                {
                    FileName = heifEncPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(heicPath);
                psi.ArgumentList.Add("-q");
                psi.ArgumentList.Add("90");
                psi.ArgumentList.Add(sourcePath);

                using var process = new Process { StartInfo = psi };
                process.Start();

                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    throw;
                }

                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(heicPath) || new FileInfo(heicPath).Length == 0)
                {
                    throw new InvalidOperationException(
                        $"heif-enc failed (exit {process.ExitCode}): {stderr.Trim()}");
                }

                LogService.Merge($"HEIC conversion successful: {Path.GetFileName(heicPath)}");
                return heicPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Merge($"HEIC conversion failed: {ex.Message}", LogLevel.Error, ex);
                TryDelete(heicPath);
                throw new InvalidOperationException(
                    $"HEIC encoding failed for {Path.GetFileName(sourcePath)}: {ex.Message}", ex);
            }
        }

        // ── 调度 ──────────────────────────────────────────

        // 核心转换逻辑 — 根据用户选择的解码器方案分派到不同的实现。
        // 转换完成后通过 ExifTool 复制元数据，失败时清理临时产物。
        // heicPath: 源 HEIC 文件路径
        // outputPath: 目标 JPEG 文件路径
        // token: 取消令牌
        // 返回: 转换后的 JPEG 文件路径
        private static async Task<string> ConvertInternalAsync(string heicPath, string outputPath, int quality, CancellationToken token)
        {
            // 一次读取解码器索引，避免多次调 AppSettingsService（IO 开销）
            int decoderIndex = DecoderIndex;
            var decoderName = decoderIndex == 1 ? "heif-dec" : "Magick.NET";
            LogService.Merge($"Converting HEIC to JPEG ({decoderName}, q={quality}): {heicPath}");

            try
            {
                token.ThrowIfCancellationRequested();

                if (decoderIndex == 1)
                    // heif-dec：外部进程解码，天然异步
                    await ConvertWithHeifDecAsync(heicPath, outputPath, quality, token).ConfigureAwait(false);
                else
                    // Magick.NET：CPU 密集型，放线程池
                    await Task.Run(() => ConvertWithMagickNET(heicPath, outputPath, quality), token).ConfigureAwait(false);

                await CopyTagsSafeAsync(heicPath, outputPath, token).ConfigureAwait(false);

                LogService.Merge($"HEIC conversion successful: {outputPath}");
                return outputPath;
            }
            catch (OperationCanceledException)
            {
                TryDelete(outputPath);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Merge($"HEIC conversion failed ({decoderName}): {ex.Message}", LogLevel.Error, ex);
                TryDelete(outputPath);
                throw NewHeicError(heicPath, ex.Message);
            }
        }

        // ── 方案 A：Magick.NET ─────────────────────────────

        // 使用 ImageMagick/libheif 解码 HEIC。
        // 优点：完全自包含，无需 Windows 商店扩展；瓦片网格自动拼接；
        // Display P3→sRGB 自动转换；EXIF 方向自动应用。
        private static void ConvertWithMagickNET(string heicPath, string outputPath, int quality)
        {
            using var image = new MagickImage(heicPath);

            // 自动应用 EXIF 方向并移除标签
            image.AutoOrient();

            // 强制 sRGB——Display P3 HEIC → JPEG 必须做，否则发白
            image.ColorSpace = ColorSpace.sRGB;

            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)quality;

            image.Write(outputPath);
        }

        // ── 方案 B：heif-dec ───────────────────────────────

        // 使用项目内置的 heif-dec.exe（libheif + libde265）解码 HEIC。
        // 优点：自包含，无需 Windows HEIF/HEVC 商店扩展；进程天然异步。
        private static async Task ConvertWithHeifDecAsync(string heicPath, string outputPath, int quality, CancellationToken token)
        {
            string? heifDecPath = ExternalToolLocator.FindHeifDec();
            if (string.IsNullOrEmpty(heifDecPath))
                throw new InvalidOperationException(ResourceService.GetString("Error_HeifDecMissing"));

            var psi = new ProcessStartInfo
            {
                FileName = heifDecPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(heicPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add(quality.ToString());

            using var process = new Process { StartInfo = psi };
            process.Start();

            try
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }

            if (process.ExitCode != 0)
            {
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"heif-dec failed (exit {process.ExitCode}): {stderr.Trim()}");
            }
        }

        // ── ExifTool 元数据补充 ────────────────────────────

        private static async Task CopyTagsSafeAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            try { await CopyTagsAsync(sourcePath, targetPath, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                LogService.Merge($"Copy metadata from HEIC failed: {ex.Message}", LogLevel.Warning, ex);
            }
        }

        // 复制所有标签但排除 Orientation。
        // AutoOrient / heif-dec 已把方向应用到像素上，再复制 Orientation 会导致双重旋转。
        private static async Task CopyTagsAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            // 使用 RunExifToolAsync（stdin 管道，UTF-8 编码），兼容所有语言文件名
            await LivePhotoRepairService.RunExifToolAsync(token,
                "-TagsFromFile", sourcePath,
                "-all:all",
                "-Orientation=",
                "-overwrite_original",
                "-quiet",
                targetPath);
        }

        // ── 工具方法 ──────────────────────────────────────

        private static InvalidOperationException NewHeicError(string heicPath, string detail)
        {
            return new InvalidOperationException(
                ResourceService.Format("Error_HeicConversionFailed",
                    Path.GetFileName(heicPath), detail));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
