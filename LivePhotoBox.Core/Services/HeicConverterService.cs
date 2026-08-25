using ImageMagick;
using LivePhotoBox.Services.Protocols;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

            // heif-enc（libheif 定制版）对大端源 TIFF 会把 Exif item 写成
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

            // heif-enc 对部分 JPEG 源（华为等 EXIF ColorSpace 未校准且带 ICC）只写 ICC 不写
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

        // 普通（无增益图）HEIC 编码：heif-enc 单图转换。
        private static async Task<string> ConvertPlainHeicAsync(
            string sourcePath, string outputDirectory, CancellationToken token)
        {
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

                psi.RedirectStandardOutput = true;
                var run = await ExternalToolProcessGuard.RunAsync(
                    () => psi,
                    timeout: TimeSpan.FromSeconds(120),
                    operation: $"heif-enc: {Path.GetFileName(sourcePath)}",
                    cancellationToken: token,
                    prepareAttempt: _ =>
                    {
                        try { File.Delete(heicPath); } catch { }
                    }).ConfigureAwait(false);

                if (!run.IsSuccess || !File.Exists(heicPath) || new FileInfo(heicPath).Length == 0)
                {
                    throw new InvalidOperationException(
                        $"heif-enc failed (exit {run.ExitCode}, attempts={run.Attempts}, timeout={run.TimedOut}): {run.StandardError.Trim()}");
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

        /// <summary>
        /// HDR 保留的 HEIC -> HEIC 转换：像素走 heif-dec 解码为 16-bit PNG，
        /// 再用 heif-enc 以 -b 10 + 源 nclx/CLLI 重新编码，避免 JPEG 桥接导致的 8-bit 降级。
        /// 源 ICC/EXIF/XMP 随 PNG 保留；如需把另一文件的 EXIF（如含 Apple MakerNote 的
        /// JPEG 桥接）整体替换到输出，传 exifSourcePath（内部先清后拷，保证唯一 0x927C）。
        /// </summary>
        /// <param name="heicPath">源 HEIC 文件路径。</param>
        /// <param name="outputDirectory">输出目录。</param>
        /// <param name="token">取消令牌。</param>
        /// <param name="exifSourcePath">可选：EXIF 来源文件（整体替换输出 EXIF）。</param>
        /// <param name="metadataSourcePath">可选：nclx/CLLI 元数据来源（图片项 nclx 不完整时回退读取）。</param>
        /// <returns>转换后的 HEIC 文件路径。</returns>
        public static async Task<string> ConvertHeicToHeicPreservingAsync(
            string heicPath, string outputDirectory, CancellationToken token,
            string? exifSourcePath = null, string? metadataSourcePath = null)
        {
            string? heifDecPath = ExternalToolLocator.FindHeifDec();
            if (string.IsNullOrEmpty(heifDecPath))
                throw new InvalidOperationException(ResourceService.GetString("Error_HeifDecMissing"));

            string? heifEncPath = ExternalToolLocator.FindHeifEnc();
            if (string.IsNullOrEmpty(heifEncPath))
                throw new InvalidOperationException(ResourceService.GetString("Error_HeifEncMissing"));

            string tempPngPath = Path.Combine(outputDirectory, $".lpb_hdr_{Guid.NewGuid():N}.png");
            string heicOutPath = TempFileService.AllocateTempPath(outputDirectory, "heic", "heic");

            try
            {
                token.ThrowIfCancellationRequested();
                LogService.Merge(
                    $"Converting HEIC to HEIC preserving HDR (heif-dec PNG16 -> heif-enc): {Path.GetFileName(heicPath)}");

                // 1) HEIC -> PNG（源为 10-bit 时输出 16-bit PNG）；重试一次规避杀软/锁文件竞态。
                await RunWithRetryAsync(
                    () => RunHeifDecToPngAsync(heifDecPath, heicPath, tempPngPath, token), token);
                if (!File.Exists(tempPngPath))
                    throw new InvalidOperationException("heif-dec produced no PNG output.");

                // 2) 读源 nclx/CLLI 并映射为 H.273 数值；映射不全则跳过 nclx 参数（ICC 仍保留）。
                (int? primaries, int? transfer, int? matrix, int? fullRange, int? maxCll, int? maxFall) =
                    await ReadHeicHdrMetadataAsync(heicPath, token);

                var encArgs = new List<string> { "-o", heicOutPath, "-q", "90", "-b", "10" };
                bool hasNclx = primaries.HasValue && transfer.HasValue && matrix.HasValue && fullRange.HasValue;
                if (!hasNclx && !string.IsNullOrEmpty(metadataSourcePath)
                    && !string.Equals(metadataSourcePath, heicPath, StringComparison.OrdinalIgnoreCase))
                {
                    // 华为等机型图片项的 nclx 常为 Unspecified，但完整源文件（含视频轨）声明了 HDR；
                    // 回退读取原始源文件补全 nclx/CLLI。
                    var fallback = await ReadHeicHdrMetadataAsync(metadataSourcePath, token);
                    if (fallback.Primaries.HasValue && fallback.Transfer.HasValue
                        && fallback.Matrix.HasValue && fallback.FullRange.HasValue)
                    {
                        primaries = fallback.Primaries;
                        transfer = fallback.Transfer;
                        matrix = fallback.Matrix;
                        fullRange = fallback.FullRange;
                        maxCll ??= fallback.MaxCll;
                        maxFall ??= fallback.MaxFall;
                        hasNclx = true;
                        LogService.Merge(
                            "HEIC HDR nclx read from original source (image item nclx incomplete)",
                            LogLevel.Debug);
                    }
                }
                if (primaries.HasValue && transfer.HasValue && matrix.HasValue && fullRange.HasValue)
                {
                    encArgs.Add("--colour_primaries");
                    encArgs.Add(primaries.Value.ToString(CultureInfo.InvariantCulture));
                    encArgs.Add("--transfer_characteristic");
                    encArgs.Add(transfer.Value.ToString(CultureInfo.InvariantCulture));
                    encArgs.Add("--matrix_coefficients");
                    encArgs.Add(matrix.Value.ToString(CultureInfo.InvariantCulture));
                    encArgs.Add("--full_range_flag");
                    encArgs.Add(fullRange.Value.ToString(CultureInfo.InvariantCulture));
                    encArgs.Add("--enable-two-colr-boxes");
                }
                if (maxCll.HasValue && maxFall.HasValue)
                {
                    encArgs.Add("--clli");
                    encArgs.Add(
                        $"{maxCll.Value.ToString(CultureInfo.InvariantCulture)},{maxFall.Value.ToString(CultureInfo.InvariantCulture)}");
                }
                encArgs.Add(tempPngPath);

                // 3) PNG -> HEIC（10-bit 容器；8-bit 源自动保持 8-bit）。
                await RunWithRetryAsync(() => RunHeifEncAsync(heifEncPath, encArgs, token), token);
                if (!File.Exists(heicOutPath) || new FileInfo(heicOutPath).Length == 0)
                    throw new InvalidOperationException("heif-enc produced no HEIC output.");

                // 4) EXIF 整体替换为桥接文件版本（先清后拷，避免重复 0x927C MakerNote）。
                if (!string.IsNullOrEmpty(exifSourcePath) && File.Exists(exifSourcePath))
                {
                    await ReplaceExifFromAsync(exifSourcePath, heicOutPath, token);
                }

                return heicOutPath;
            }
            catch (OperationCanceledException)
            {
                TryDelete(heicOutPath);
                throw;
            }
            catch
            {
                TryDelete(heicOutPath);
                throw;
            }
            finally
            {
                TryDelete(tempPngPath);
            }
        }

        // ---- HDR preserving conversion helpers ----

        private static async Task RunWithRetryAsync(Func<Task> action, CancellationToken token)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await action();
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new InvalidOperationException("tool run failed");
        }

        private static async Task RunHeifDecToPngAsync(
            string heifDecPath, string heicPath, string pngPath, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = heifDecPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(heicPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(pngPath);
            psi.ArgumentList.Add("--quiet");

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(token);
            string stderr = await process.StandardError.ReadToEndAsync(token);
            if (process.ExitCode != 0 || !File.Exists(pngPath))
                throw new InvalidOperationException($"heif-dec failed (exit {process.ExitCode}): {stderr.Trim()}");
        }

        private static async Task RunHeifEncAsync(
            string heifEncPath, IReadOnlyList<string> args, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = heifEncPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();
            await process.WaitForExitAsync(token);
            string stderr = await process.StandardError.ReadToEndAsync(token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"heif-enc failed (exit {process.ExitCode}): {stderr.Trim()}");
        }

        private static async Task<(int? Primaries, int? Transfer, int? Matrix, int? FullRange, int? MaxCll, int? MaxFall)>
            ReadHeicHdrMetadataAsync(string heicPath, CancellationToken token)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool();

            // 字节级解析 nclx：华为等机型 HEIC 常带多组 colr（ICC + sRGB + HLG），
            // exiftool 的 VideoFullRangeFlag 会取到非 HDR 组的 full range（full=1），
            // 导致 HLG 源被错误标成全范围、HDR 显示异常。优先取 HLG(18)/PQ(16) 组。
            if (TryReadNclxFromFile(heicPath, out int bytePrim, out int byteTransfer, out int byteMatrix, out int byteFull))
            {
                (int? clliValue, int? fallValue) = ReadClliWithExifTool(exifToolPath, heicPath);
                return (bytePrim, byteTransfer, byteMatrix, byteFull, clliValue, fallValue);
            }

            if (string.IsNullOrEmpty(exifToolPath))
                return (null, null, null, null, null, null);

            var psi = new ProcessStartInfo
            {
                FileName = exifToolPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add("-ColorPrimaries");
            psi.ArgumentList.Add("-TransferCharacteristics");
            psi.ArgumentList.Add("-MatrixCoefficients");
            psi.ArgumentList.Add("-VideoFullRangeFlag");
            psi.ArgumentList.Add("-MaxContentLightLevel");
            psi.ArgumentList.Add("-MaxPicAverageLightLevel");
            psi.ArgumentList.Add(heicPath);

            string stdout;
            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                stdout = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in stdout.Split('\n'))
            {
                int idx = line.IndexOf(':');
                if (idx > 0)
                {
                    values[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                }
            }

            int? primaries = values.TryGetValue("ColorPrimaries", out var cp) ? MapPrimaries(cp) : null;
            int? transfer = values.TryGetValue("TransferCharacteristics", out var tc) ? MapTransfer(tc) : null;
            int? matrix = values.TryGetValue("MatrixCoefficients", out var mc) ? MapMatrix(mc) : null;
            int? fullRange = values.TryGetValue("VideoFullRangeFlag", out var fr)
                ? (fr.Contains("Full", StringComparison.OrdinalIgnoreCase) ? 1
                    : fr.Contains("Limited", StringComparison.OrdinalIgnoreCase) ? 0 : null)
                : null;
            int? maxCll = values.TryGetValue("MaxContentLightLevel", out var cll)
                && int.TryParse(cll, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cllVal) ? cllVal : null;
            int? maxFall = values.TryGetValue("MaxPicAverageLightLevel", out var fall)
                && int.TryParse(fall, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fallVal) ? fallVal : null;

            if (primaries is null || transfer is null || matrix is null || fullRange is null)
            {
                LogService.Merge(
                    $"HEIC HDR nclx incomplete ({string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"))}), nclx will be skipped",
                    LogLevel.Warning);
            }

            return (primaries, transfer, matrix, fullRange, maxCll, maxFall);
        }

        // 从 HEIC 容器字节解析 nclx（colr/nclx），优先返回 HDR 传输特性（HLG=18 / PQ=16）组。
        private static bool TryReadNclxFromFile(
            string path, out int primaries, out int transfer, out int matrix, out int fullRange)
        {
            primaries = transfer = matrix = fullRange = 0;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                var nclxList = new List<(int P, int T, int M, int F)>();
                WalkHeifBoxes(data, 0, data.Length, (type, body, len) =>
                {
                    if (type != "colr" || len < 11 || body + 11 > data.Length)
                    {
                        return;
                    }
                    if (!HeifBoxParser.ReadFourCc(data, body).Equals("nclx", StringComparison.Ordinal))
                    {
                        return;
                    }

                    int p = HeifBoxParser.Read16(data, body + 4);
                    int t = HeifBoxParser.Read16(data, body + 6);
                    int m = HeifBoxParser.Read16(data, body + 8);
                    int f = (data[body + 10] >> 7) & 1;
                    nclxList.Add((p, t, m, f));
                });

                if (nclxList.Count == 0)
                {
                    return false;
                }

                var hdrGroup = nclxList.FirstOrDefault(x => x.T == 18 || x.T == 16);
                var selected = hdrGroup != default ? hdrGroup : nclxList[0];
                primaries = selected.P;
                transfer = selected.T;
                matrix = selected.M;
                fullRange = selected.F;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void WalkHeifBoxes(byte[] data, int start, int end, Action<string, int, int> visit)
        {
            int p = start;
            while (p + 8 <= end)
            {
                long size = HeifBoxParser.Read32(data, p);
                string type = HeifBoxParser.ReadFourCc(data, p + 4);
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > end)
                    {
                        break;
                    }
                    size = (long)HeifBoxParser.Read64(data, p + 8);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - p;
                }

                if (size < header || p + size > end)
                {
                    break;
                }

                visit(type, p + header, (int)(size - header));
                if (type is "meta" or "iprp" or "ipco")
                {
                    int childStart = p + header;
                    if (type == "meta")
                    {
                        childStart += 4; // meta 是 FullBox
                    }
                    WalkHeifBoxes(data, childStart, p + (int)size, visit);
                }
                p += (int)size;
            }
        }

        private static (int? MaxCll, int? MaxFall) ReadClliWithExifTool(string? exifToolPath, string heicPath)
        {
            if (string.IsNullOrEmpty(exifToolPath))
            {
                return (null, null);
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add("-MaxContentLightLevel");
                psi.ArgumentList.Add("-MaxPicAverageLightLevel");
                psi.ArgumentList.Add(heicPath);

                using var process = new Process { StartInfo = psi };
                process.Start();
                string stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                int? maxCll = null;
                int? maxFall = null;
                foreach (string line in stdout.Split('\n'))
                {
                    int idx = line.IndexOf(':');
                    if (idx <= 0)
                    {
                        continue;
                    }
                    string key = line[..idx].Trim();
                    string value = line[(idx + 1)..].Trim();
                    if (key.Equals("MaxContentLightLevel", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cll))
                    {
                        maxCll = cll;
                    }
                    else if (key.Equals("MaxPicAverageLightLevel", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fall))
                    {
                        maxFall = fall;
                    }
                }
                return (maxCll, maxFall);
            }
            catch
            {
                return (null, null);
            }
        }

        private static int? MapPrimaries(string value)
        {
            if (value.Contains("2020", StringComparison.OrdinalIgnoreCase)) return 9;
            if (value.Contains("709", StringComparison.OrdinalIgnoreCase)) return 1;
            if (value.Contains("432-1", StringComparison.OrdinalIgnoreCase)) return 12;
            if (value.Contains("431-2", StringComparison.OrdinalIgnoreCase)) return 11;
            return null;
        }

        private static int? MapTransfer(string value)
        {
            if (value.Contains("HLG", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ARIB", StringComparison.OrdinalIgnoreCase)) return 18;
            if (value.Contains("2084", StringComparison.OrdinalIgnoreCase)
                || value.Contains("PQ", StringComparison.OrdinalIgnoreCase)) return 16;
            if (value.Contains("sRGB", StringComparison.OrdinalIgnoreCase)
                || value.Contains("sYCC", StringComparison.OrdinalIgnoreCase)) return 13;
            if (value.Contains("709", StringComparison.OrdinalIgnoreCase)) return 1;
            return null;
        }

        private static int? MapMatrix(string value)
        {
            if (value.Contains("2020", StringComparison.OrdinalIgnoreCase)) return 9;
            if (value.Contains("709", StringComparison.OrdinalIgnoreCase)) return 1;
            if (value.Contains("Identity", StringComparison.OrdinalIgnoreCase)
                || value.Contains("RGB", StringComparison.OrdinalIgnoreCase)) return 0;
            if (value.Contains("601", StringComparison.OrdinalIgnoreCase)) return 6;
            return null;
        }

        private static async Task ReplaceExifFromAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            try
            {
                await LivePhotoRepairService.RunExifToolAsync(
                    token, "-overwrite_original", "-EXIF:All=", targetPath);
                await LivePhotoRepairService.RunExifToolAsync(
                    token, "-overwrite_original", "-TagsFromFile", sourcePath, "-EXIF:All", targetPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Merge(
                    $"EXIF replace from {Path.GetFileName(sourcePath)} failed: {ex.Message}",
                    LogLevel.Warning);
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

            psi.RedirectStandardOutput = true;
            var run = await ExternalToolProcessGuard.RunAsync(
                () => psi,
                timeout: TimeSpan.FromSeconds(120),
                operation: $"heif-dec: {Path.GetFileName(heicPath)}",
                cancellationToken: token,
                prepareAttempt: _ =>
                {
                    try { File.Delete(outputPath); } catch { }
                }).ConfigureAwait(false);

            if (!run.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"heif-dec failed (exit {run.ExitCode}, attempts={run.Attempts}, timeout={run.TimedOut}): {run.StandardError.Trim()}");
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
