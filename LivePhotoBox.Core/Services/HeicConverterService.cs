using ImageMagick;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Services.Protocols;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    // HEIC/HEIF → JPEG 转码服务
    // 优先通过 LivePhotoBox.Native 转换，回退使用 Magick.NET。
    public static class HeicConverterService
    {
        // 判断指定文件是否为 HEIC 或 HEIF 格式（仅检查扩展名，不读取文件头）。
        public static bool IsHeicFile(string path)
        {
            return path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
        }

        // 解码器索引：0=Magick.NET / Native（保持兼容属性）
        public static int DecoderIndex => 0;

        // ── 公开 API ──────────────────────────────────────

        public static async Task<string> ConvertToJpegAsync(string heicPath, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return heicPath;

            if (StandardHdrConversionService.HasAppleHeicGainMap(heicPath, token))
            {
                string dir = Path.GetDirectoryName(heicPath) ?? string.Empty;
                string? hdrPath = await TryConvertHdrToJpegAsync(heicPath, dir, token);
                if (hdrPath != null) return hdrPath;
            }

            string jpegPath = Path.Combine(
                Path.GetDirectoryName(heicPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(heicPath) + ".jpg");

            return await ConvertInternalAsync(heicPath, jpegPath, quality: 100, token);
        }

        public static async Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return heicPath;

            if (StandardHdrConversionService.HasAppleHeicGainMap(heicPath, token))
            {
                string? hdrPath = await TryConvertHdrToJpegAsync(heicPath, outputDirectory, token);
                if (hdrPath != null) return hdrPath;
            }

            // 临时文件名由 TempFileService 分配（GUID 后缀），并发任务互不冲突。
            string tempPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "jpg");

            return await ConvertInternalAsync(heicPath, tempPath, quality: 100, token);
        }

        /// <summary>
        /// 转换 HEIC 为 JPEG，可指定质量（1-100）。
        /// 用于导出等不需要 100% 质量的场景，避免文件过大。
        /// </summary>
        public static async Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory, int quality, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return heicPath;

            if (StandardHdrConversionService.HasAppleHeicGainMap(heicPath, token))
            {
                string? hdrPath = await TryConvertHdrToJpegAsync(heicPath, outputDirectory, token);
                if (hdrPath != null) return hdrPath;
            }

            // 临时文件名由 TempFileService 分配（GUID 后缀），并发任务互不冲突。
            string tempPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "jpg");

            return await ConvertInternalAsync(heicPath, tempPath, quality, token);
        }

        /// <summary>
        /// 将 JPEG 图片转换为 HEIC 格式，用于合并导出时生成 HEIC 变体。
        /// 使用 Native 媒体管线编码，自包含、零 Windows 商店扩展。
        /// </summary>
        /// <param name="sourcePath">源图片文件路径（仅 JPEG）</param>
        /// <param name="outputDirectory">输出目录</param>
        /// <param name="token">取消令牌</param>
        /// <returns>转换后的 HEIC 文件路径；若输入已是 HEIC 则直接返回原路径</returns>
        public static async Task<string> ConvertToHeicAsync(
            string sourcePath, string outputDirectory, CancellationToken token = default)
        {
            if (IsHeicFile(sourcePath)) return sourcePath;

            string resultPath;
            if (StandardHdrConversionService.HasStandardJpegGainMap(sourcePath, token))
            {
                try
                {
                    resultPath = await StandardHdrConversionService.ConvertJpegToHeicAsync(
                        sourcePath, outputDirectory, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // HDR 转换失败不应让整个合成/拆分任务失败：记录警告并回退
                    // 普通 HEIC 转换（无增益图，仍可正常导入/显示 SDR 画面）。
                    LogService.Merge(
                        $"HDR conversion failed for {Path.GetFileName(sourcePath)}: {ex.Message}; "
                        + "falling back to plain HEIC conversion (gain map dropped)",
                        LogLevel.Warning, ex);
                    resultPath = await ConvertPlainHeicAsync(sourcePath, outputDirectory, token);
                }
            }
            else
            {
                resultPath = await ConvertPlainHeicAsync(sourcePath, outputDirectory, token);
            }

            // 底层 HEIC 编码对大端源 TIFF 会把 Exif item 写成
            // [offset=0][TIFF]（缺 "Exif\0\0" 前缀），iOS 等严格解析器会读不到
            // EXIF/MakerNote 导致实况照片无法配对导入。统一规范化为标准布局。
            if (AppleMakerNoteWriter.TryNormalizeExifItem(resultPath, out string? normalizeError))
            {
                LogService.Merge(
                    $"HEIC Exif item normalized for {Path.GetFileName(resultPath)}",
                    LogLevel.Debug);
            }
            else if (normalizeError != null)
            {
                LogService.Merge(
                    $"HEIC Exif item normalization skipped for {Path.GetFileName(resultPath)}: {normalizeError}",
                    LogLevel.Warning);
            }

            // 部分编码产物对部分 JPEG 源（华为等 EXIF ColorSpace 未校准且带 ICC）只写 ICC 不写
            // nclx；iOS 导入实况照片时可能因缺少色彩属性无法解析。无 nclx 时补 sRGB nclx。
            if (HeifAuxImageWriter.TryHasNclxColr(resultPath, out bool hasNclx, out string? nclxCheckError))
            {
                string? nclxAddError = null;
                if (!hasNclx && HeifAuxImageWriter.TryAddNclxColr(resultPath, out nclxAddError))
                {
                    LogService.Merge(
                        $"HEIC nclx colr added for {Path.GetFileName(resultPath)}",
                        LogLevel.Debug);
                }
                else if (!hasNclx)
                {
                    LogService.Merge(
                        $"HEIC nclx colr add failed for {Path.GetFileName(resultPath)}: {nclxAddError}",
                        LogLevel.Warning);
                }
            }
            else
            {
                LogService.Merge(
                    $"HEIC nclx check failed for {Path.GetFileName(resultPath)}: {nclxCheckError}",
                    LogLevel.Warning);
            }

            return resultPath;
        }

        // 普通（无增益图）HEIC 编码：Native 转换。
        private static async Task<string> ConvertPlainHeicAsync(
            string sourcePath, string outputDirectory, CancellationToken token)
        {
            string heicPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "heic");

            try
            {
                token.ThrowIfCancellationRequested();
                LogService.Merge($"Converting to HEIC (native): {Path.GetFileName(sourcePath)}");

                await NativeMediaService.ConvertImageAsync(
                    sourcePath, heicPath, Media.Models.ImageContainer.Heic, 90, token).ConfigureAwait(false);

                if (!File.Exists(heicPath) || new FileInfo(heicPath).Length == 0)
                {
                    throw new InvalidOperationException("Native image conversion produced no HEIC output.");
                }

                LogService.Merge($"HEIC conversion successful: {Path.GetFileName(heicPath)}");
                return heicPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Merge($"HEIC conversion failed: {ex.Message}", LogLevel.Error, ex);
                TryDelete(heicPath);
                throw new InvalidOperationException(
                    ResourceService.Format("Error_HeicConversionFailed", Path.GetFileName(sourcePath), ex.Message), ex);
            }
        }

        // HDR 保真的 HEIC→JPEG 转换；失败时记录警告并返回 null，
        // 由调用方回退普通转换（HDR 失败不应拖垮整个合成/拆分任务）。
        private static async Task<string?> TryConvertHdrToJpegAsync(
            string heicPath, string outputDirectory, CancellationToken token)
        {
            try
            {
                return await StandardHdrConversionService.ConvertHeicToJpegAsync(
                    heicPath, outputDirectory, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Merge(
                    $"Apple HDR gain map conversion failed for {Path.GetFileName(heicPath)}: {ex.Message}; "
                    + "falling back to plain JPEG conversion (gain map dropped)",
                    LogLevel.Warning, ex);
                return null;
            }
        }

        public static Task<string> ConvertHeicToHeicPreservingAsync(
            string heicPath, string outputDirectory, CancellationToken token,
            string? exifSourcePath = null, string? metadataSourcePath = null)
        {
            // 外部工具已移除，直接返回原 HEIC 路径
            return Task.FromResult(heicPath);
        }

        // ── 调度 ──────────────────────────────────────────

        private static async Task<string> ConvertInternalAsync(string heicPath, string outputPath, int quality, CancellationToken token)
        {
            LogService.Merge($"Converting HEIC to JPEG (q={quality}): {heicPath}");

            try
            {
                token.ThrowIfCancellationRequested();

                bool converted = false;
                try
                {
                    converted = await NativeMediaService.ConvertImageAsync(
                        heicPath, outputPath, Media.Models.ImageContainer.Jpeg, quality, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Merge($"Native HEIC to JPEG conversion failed, falling back to Magick.NET: {ex.Message}", LogLevel.Warning, ex);
                }

                if (!converted || !File.Exists(outputPath))
                {
                    await Task.Run(() => ConvertWithMagickNET(heicPath, outputPath, quality), token).ConfigureAwait(false);
                }

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
                LogService.Merge($"HEIC conversion failed: {ex.Message}", LogLevel.Error, ex);
                TryDelete(outputPath);
                throw NewHeicError(heicPath, ex.Message);
            }
        }

        // ── Magick.NET 回退 ─────────────────────────────

        // 使用 ImageMagick/libheif 解码 HEIC。
        // 优点：完全自包含，无需外部工具；瓦片网格自动拼接；
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
