using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 更换实况照片封面帧所需的输入参数。
    /// </summary>
    public sealed class CoverChangeRequest
    {
        /// <summary>实况照片图片路径（单文件实况或双文件中的图片）。</summary>
        public required string ImagePath { get; init; }

        /// <summary>双文件实况照片的配对视频路径；单文件实况为 null。</summary>
        public string? VideoPath { get; init; }

        /// <summary>实况照片类型。</summary>
        public required LivePhotoType LivePhotoType { get; init; }

        /// <summary>检测到的实况照片协议。</summary>
        public required LivePhotoProtocolType Protocol { get; init; }

        /// <summary>新封面帧在视频时间轴上的位置（微秒）。</summary>
        public long TimestampUs { get; init; }

        /// <summary>新封面帧的 0-based 帧序号；提供时优先按帧序号精确抽帧。</summary>
        public int? FrameIndex { get; init; }

        /// <summary>新图片文件的输出路径。</summary>
        public required string OutputImagePath { get; init; }

        /// <summary>新配对视频文件的输出路径；不需要输出视频时为 null。</summary>
        public string? OutputVideoPath { get; init; }
    }

    /// <summary>
    /// 更换封面帧操作的结果。
    /// </summary>
    public sealed class CoverChangeResult
    {
        /// <summary>输出图片路径。</summary>
        public required string OutputImagePath { get; init; }

        /// <summary>输出视频路径；单文件协议为 null。</summary>
        public string? OutputVideoPath { get; init; }
    }

    /// <summary>
    /// 实况照片封面更换统一服务。
    ///
    /// 该服务以 GUI EditPage 中已验证的“设为封面并保存为副本”逻辑为唯一标准，
    /// 将 GUI 中各协议分支整理到 Core，供 CLI 复用。
    /// </summary>
    public static class CoverChangeService
    {
        /// <summary>
        /// 按协议更换封面帧并写出目标文件。
        /// </summary>
        public static async Task<CoverChangeResult> ChangeCoverAsync(
            CoverChangeRequest request,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.ImagePath) || !File.Exists(request.ImagePath))
                throw new FileNotFoundException("Source live photo image not found.", request.ImagePath);
            if (string.IsNullOrWhiteSpace(request.OutputImagePath))
                throw new ArgumentException("Output image path is required.", nameof(request));

            string? outputDir = Path.GetDirectoryName(request.OutputImagePath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            string? tempWorkDir = null;
            try
            {
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_cover_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                return await ChangeCoverCoreAsync(request, tempWorkDir, token).ConfigureAwait(false);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                {
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { /* best effort */ }
                }
            }
        }

        private static async Task<CoverChangeResult> ChangeCoverCoreAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            switch (request.Protocol)
            {
                case LivePhotoProtocolType.Apple:
                    return await ChangeAppleCoverAsync(request, workDir, token).ConfigureAwait(false);

                case LivePhotoProtocolType.Huawei:
                    return await ChangeHuaweiCoverAsync(request, workDir, token).ConfigureAwait(false);

                case LivePhotoProtocolType.Vivo when request.LivePhotoType == LivePhotoType.DualFile:
                    return await ChangeVivoOldCoverAsync(request, workDir, token).ConfigureAwait(false);

                case LivePhotoProtocolType.Samsung:
                case LivePhotoProtocolType.Fusion:
                    return await ChangeSamsungFamilyCoverAsync(request, workDir, token).ConfigureAwait(false);

                case LivePhotoProtocolType.GoogleV2 when request.LivePhotoType == LivePhotoType.SingleFileHeic:
                    return await ChangeGoogleV2HeicCoverAsync(request, workDir, token).ConfigureAwait(false);

                default:
                    return await ChangeXmpSingleFileCoverAsync(request, workDir, token).ConfigureAwait(false);
            }
        }

        // ── Apple 双文件实况照片 ─────────────────────────────────────

        private static async Task<CoverChangeResult> ChangeAppleCoverAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            string? pairedVideoPath = request.VideoPath;
            if (string.IsNullOrEmpty(pairedVideoPath) || !File.Exists(pairedVideoPath))
                throw new FileNotFoundException("Apple Live Photo requires a paired video file.");

            if (string.IsNullOrEmpty(request.OutputVideoPath))
                throw new ArgumentException("Apple Live Photo requires an output video path.", nameof(request));

            string heifEncPath = FindRequiredHeifEnc();

            // 1. 从 MOV 提取目标帧。
            string frameJpeg = await ExtractFrameAsync(
                request, pairedVideoPath, workDir, "apple_frame.jpg", token).ConfigureAwait(false);

            // 2. 从原 HEIC 拷贝 EXIF/MakerNote 到帧 JPEG。
            await CopyExifFromSourceAsync(request.ImagePath, frameJpeg, token).ConfigureAwait(false);

            // 3. 提前读取 ContentIdentifier，并在编码前把 Apple MakerNote 注入帧 JPEG，
            //    确保 heif-enc 与后续 exiftool 回写都能保留配对 UUID。
            string? contentIdentifier = await ReadContentIdentifierAsync(request.ImagePath, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(contentIdentifier))
            {
                AppleMakerNoteWriter.TryInjectIntoJpeg(
                    frameJpeg,
                    AppleMakerNoteWriter.BuildMakerNote(contentIdentifier),
                    out _);
            }

            // 4. 帧 JPEG 编码为 HEIC。
            string tempHeicPath = await EncodeJpegToHeicAsync(frameJpeg, workDir, token).ConfigureAwait(false);

            // 5. heif-enc 不一定保留全部标签，从 enriched JPEG 回写。
            try
            {
                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-TagsFromFile", frameJpeg,
                    "-all:all",
                    "-Orientation=",
                    "-overwrite_original",
                    "-quiet",
                    tempHeicPath).ConfigureAwait(false);
            }
            catch
            {
                // GUI 中将此步视为 non-fatal。
            }

            // 6. 写回 ContentIdentifier 到新 HEIC。
            // exiftool 无法凭空创建 Apple MakerNote；优先用字节级注入，失败时退回 exiftool。
            if (!string.IsNullOrEmpty(contentIdentifier))
            {
                bool injected = AppleMakerNoteWriter.TryInjectAppleMakerNoteIntoHeic(
                    tempHeicPath, contentIdentifier, out _);
                if (!injected)
                {
                    try
                    {
                        await WriteContentIdentifierAsync(tempHeicPath, contentIdentifier, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // GUI 将此步视为 non-fatal。
                    }
                }
            }

            File.Copy(tempHeicPath, request.OutputImagePath, overwrite: true);

            // 7. 复制配对 MOV。
            File.Copy(pairedVideoPath, request.OutputVideoPath, overwrite: true);

            // 8. 更新 MOV 中 mebx 轨的封面时间。
            try
            {
                EditTimingService.PatchAppleStillImageTime(
                    request.OutputVideoPath, request.TimestampUs / 1_000_000.0);
            }
            catch
            {
                // GUI 将此步视为 non-fatal。
            }

            // 9. 写回 ContentIdentifier 到新 MOV。
            if (!string.IsNullOrEmpty(contentIdentifier))
            {
                try
                {
                    await WriteContentIdentifierAsync(request.OutputVideoPath, contentIdentifier, token).ConfigureAwait(false);
                }
                catch
                {
                    // GUI 将此步视为 non-fatal。
                }
            }

            TrySetLastWriteTime(request.OutputImagePath);
            TrySetLastWriteTime(request.OutputVideoPath);

            return new CoverChangeResult
            {
                OutputImagePath = request.OutputImagePath,
                OutputVideoPath = request.OutputVideoPath
            };
        }

        // ── 华为/荣耀 Moving Photo ──────────────────────────────────

        private static async Task<CoverChangeResult> ChangeHuaweiCoverAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            bool isHeicOutput = HeicConverterService.IsHeicFile(request.OutputImagePath);

            if (isHeicOutput)
                FindRequiredHeifEnc();

            // 读取原尾部 PPP:QQQQ。
            int originalCoverMs = 0;
            int originalDurationMs = 0;
            var tailInfo = HuaweiMovingPhotoProtocol.ReadTail(request.ImagePath);
            if (tailInfo.HasValue)
            {
                originalCoverMs = tailInfo.Value.coverMs;
                originalDurationMs = tailInfo.Value.durationMs;
            }

            // 提取嵌入 MP4。
            string videoPath = await ExtractHuaweiVideoAsync(request.ImagePath, workDir, token).ConfigureAwait(false);

            // 提取目标帧。
            string frameJpeg = await ExtractFrameAsync(
                request, videoPath, workDir, "huawei_frame.jpg", token).ConfigureAwait(false);

            // 注入原图 EXIF。
            await CopyExifFromSourceAsync(request.ImagePath, frameJpeg, token).ConfigureAwait(false);

            // HEIC 输出时把帧 JPEG 转为 HEIC。
            string imagePath = frameJpeg;
            if (isHeicOutput)
            {
                imagePath = await EncodeJpegToHeicAsync(frameJpeg, workDir, token).ConfigureAwait(false);
            }

            // 复用 Core 的华为原生写入器：注入 covertime、patch ftyp/©too、构建尾部。
            await LivePhotoMergeService.WriteHuaweiNativeAsync(
                imagePath,
                videoPath,
                request.OutputImagePath,
                isHeicOutput,
                request.TimestampUs,
                token,
                originalCoverMs,
                originalDurationMs,
                "v6_f").ConfigureAwait(false);

            TrySetLastWriteTime(request.OutputImagePath);

            return new CoverChangeResult
            {
                OutputImagePath = request.OutputImagePath
            };
        }

        // ── Samsung / Fusion ─────────────────────────────────────────

        private static async Task<CoverChangeResult> ChangeSamsungFamilyCoverAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            // 提取嵌入视频。
            string videoPath = await ExtractSamsungVideoAsync(request.ImagePath, workDir, token).ConfigureAwait(false);

            // 提取目标帧。
            string frameJpeg = await ExtractFrameAsync(
                request, videoPath, workDir, "samsung_frame.jpg", token).ConfigureAwait(false);

            // 注入原图 EXIF。
            string workImagePath = Path.Combine(workDir, $"frame_{Guid.NewGuid():N}.jpg");
            File.Copy(frameJpeg, workImagePath, overwrite: true);
            await CopyExifFromSourceAsync(request.ImagePath, workImagePath, token).ConfigureAwait(false);

            int protocolIndex = ToProtocolIndex(request.Protocol);
            var protocol = LivePhotoProtocol.FromIndex(protocolIndex);
            string preparedImagePath = await protocol.PrepareImageAsync(workImagePath, workDir, token).ConfigureAwait(false);

            await LivePhotoMergeService.WriteLivePhotoAsync(
                preparedImagePath,
                videoPath,
                request.OutputImagePath,
                protocolIndex,
                token,
                request.TimestampUs).ConfigureAwait(false);

            TrySetLastWriteTime(request.OutputImagePath);

            return new CoverChangeResult
            {
                OutputImagePath = request.OutputImagePath
            };
        }

        // ── Google V2 HEIC 单文件实况 ──────────────────────────────

        private static async Task<CoverChangeResult> ChangeGoogleV2HeicCoverAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            string videoPath = await ExtractHeicMpvdVideoAsync(request.ImagePath, workDir, token).ConfigureAwait(false);

            string frameJpeg = await ExtractFrameAsync(
                request, videoPath, workDir, "v2heic_frame.jpg", token).ConfigureAwait(false);

            string workImagePath = Path.Combine(workDir, $"frame_{Guid.NewGuid():N}.jpg");
            File.Copy(frameJpeg, workImagePath, overwrite: true);
            await CopyExifFromSourceAsync(request.ImagePath, workImagePath, token).ConfigureAwait(false);

            string frameHeicPath = await EncodeJpegToHeicAsync(workImagePath, workDir, token).ConfigureAwait(false);

            try
            {
                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-TagsFromFile", workImagePath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    frameHeicPath).ConfigureAwait(false);
            }
            catch
            {
                // GUI 中此步属于防御性处理；失败不阻断。
            }

            await LivePhotoMergeService.WriteLivePhotoAsync(
                frameHeicPath,
                videoPath,
                request.OutputImagePath,
                selectedModeIndex: 2,
                token,
                request.TimestampUs,
                ProtocolFormatMatrix.FormatHeicMp4).ConfigureAwait(false);

            TrySetLastWriteTime(request.OutputImagePath);

            return new CoverChangeResult
            {
                OutputImagePath = request.OutputImagePath
            };
        }

        // ── 通用 XMP 单文件 JPEG（V1/V2/OPPO/vivo X300+）──────────

        private static async Task<CoverChangeResult> ChangeXmpSingleFileCoverAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            string videoPath = await ExtractXmpVideoAsync(request.ImagePath, request.Protocol, workDir, token).ConfigureAwait(false);

            string frameJpeg = await ExtractFrameAsync(
                request, videoPath, workDir, "xmp_frame.jpg", token).ConfigureAwait(false);

            string workImagePath = Path.Combine(workDir, $"frame_{Guid.NewGuid():N}.jpg");
            File.Copy(frameJpeg, workImagePath, overwrite: true);
            await CopyExifFromSourceAsync(request.ImagePath, workImagePath, token).ConfigureAwait(false);

            int protocolIndex = ToProtocolIndex(request.Protocol);
            var protocol = LivePhotoProtocol.FromIndex(protocolIndex);
            string preparedImagePath = await protocol.PrepareImageAsync(workImagePath, workDir, token).ConfigureAwait(false);

            await LivePhotoMergeService.WriteLivePhotoAsync(
                preparedImagePath,
                videoPath,
                request.OutputImagePath,
                protocolIndex,
                token,
                request.TimestampUs).ConfigureAwait(false);

            TrySetLastWriteTime(request.OutputImagePath);

            return new CoverChangeResult
            {
                OutputImagePath = request.OutputImagePath
            };
        }

        // ── vivo 旧双文件 ───────────────────────────────────────────

        private static async Task<CoverChangeResult> ChangeVivoOldCoverAsync(
            CoverChangeRequest request,
            string workDir,
            CancellationToken token)
        {
            string? pairedVideoPath = request.VideoPath;
            if (string.IsNullOrEmpty(pairedVideoPath) || !File.Exists(pairedVideoPath))
                throw new FileNotFoundException("vivo dual-file live photo requires a paired video file.");

            if (string.IsNullOrEmpty(request.OutputVideoPath))
                throw new ArgumentException("vivo dual-file live photo requires an output video path.", nameof(request));

            string frameJpeg = await ExtractFrameAsync(
                request, pairedVideoPath, workDir, "vivo_frame.jpg", token).ConfigureAwait(false);

            byte[]? vivoTail = ReadVivoTail(request.ImagePath);

            string tempJpgPath = Path.Combine(workDir, $"frame_{Guid.NewGuid():N}.jpg");
            File.Copy(frameJpeg, tempJpgPath, overwrite: true);
            await CopyExifFromSourceAsync(request.ImagePath, tempJpgPath, token).ConfigureAwait(false);

            if (vivoTail is { Length: > 0 })
            {
                await using (var dstFs = new FileStream(tempJpgPath, FileMode.Append, FileAccess.Write, FileShare.None))
                {
                    await dstFs.WriteAsync(vivoTail, 0, vivoTail.Length, token).ConfigureAwait(false);
                }
            }

            File.Copy(tempJpgPath, request.OutputImagePath, overwrite: true);
            File.Copy(pairedVideoPath, request.OutputVideoPath, overwrite: true);

            TrySetLastWriteTime(request.OutputImagePath);
            TrySetLastWriteTime(request.OutputVideoPath);

            return new CoverChangeResult
            {
                OutputImagePath = request.OutputImagePath,
                OutputVideoPath = request.OutputVideoPath
            };
        }

        // ═══════════════════════════════════════════════════════════
        //  视频提取
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 为预览/帧序号换算临时提取实况照片内嵌视频。
        /// 调用方负责创建并清理 workDir。
        /// </summary>
        public static async Task<string?> ExtractEmbeddedVideoForPreviewAsync(
            string imagePath,
            LivePhotoProtocolType protocol,
            string workDir,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (protocol == LivePhotoProtocolType.Huawei)
                return await ExtractHuaweiVideoAsync(imagePath, workDir, token).ConfigureAwait(false);

            if (protocol is LivePhotoProtocolType.Samsung or LivePhotoProtocolType.Fusion)
                return await ExtractSamsungVideoAsync(imagePath, workDir, token).ConfigureAwait(false);

            if (protocol == LivePhotoProtocolType.GoogleV2 && HeicConverterService.IsHeicFile(imagePath))
                return await ExtractHeicMpvdVideoAsync(imagePath, workDir, token).ConfigureAwait(false);

            return await ExtractXmpVideoAsync(imagePath, protocol, workDir, token).ConfigureAwait(false);
        }

        private static Task<string> ExtractHuaweiVideoAsync(
            string imagePath,
            string workDir,
            CancellationToken token)
        {
            var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(imagePath);
            if (range == null)
                throw new InvalidDataException("Cannot locate embedded MP4 in Huawei file.");

            string targetPath = Path.Combine(workDir, "video.mp4");
            CopyByteRange(imagePath, targetPath, range.Value.videoStart, range.Value.videoLength);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static Task<string> ExtractSamsungVideoAsync(
            string imagePath,
            string workDir,
            CancellationToken token)
        {
            string targetPath = Path.Combine(workDir, "video.mp4");

            if (HeicConverterService.IsHeicFile(imagePath))
            {
                long videoStart = LivePhotoMergeService.GetMpvdVideoStart(imagePath);
                long videoLength = LivePhotoMergeService.GetMpvdVideoLength(imagePath);
                if (videoStart <= 0 || videoLength <= 0)
                    throw new InvalidDataException("Cannot locate mpvd box / embedded video in Samsung HEIC file.");

                CopyByteRange(imagePath, targetPath, videoStart, videoLength);
                token.ThrowIfCancellationRequested();
                return Task.FromResult(targetPath);
            }

            // Samsung JPEG：从 MotionPhoto_Data 到文件末尾，和 GUI 保持相同输入。
            var range = LivePhotoSplitService.FindSamsungJpegVideoRange(imagePath);
            if (range == null)
                throw new InvalidDataException("Cannot locate Samsung MotionPhoto_Data video.");

            long fileSize = new FileInfo(imagePath).Length;
            long start = range.Value.videoStart;
            long length = fileSize - start;
            if (length <= 0)
                throw new InvalidDataException("Invalid Samsung embedded video range.");

            CopyByteRange(imagePath, targetPath, start, length);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static Task<string> ExtractHeicMpvdVideoAsync(
            string imagePath,
            string workDir,
            CancellationToken token)
        {
            long videoStart = LivePhotoMergeService.GetMpvdVideoStart(imagePath);
            long videoLength = LivePhotoMergeService.GetMpvdVideoLength(imagePath);
            if (videoStart <= 0 || videoLength <= 0)
                throw new InvalidDataException("Cannot locate mpvd box / embedded video in HEIC file.");

            string targetPath = Path.Combine(workDir, "video.mp4");
            CopyByteRange(imagePath, targetPath, videoStart, videoLength);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static Task<string> ExtractXmpVideoAsync(
            string imagePath,
            LivePhotoProtocolType protocol,
            string workDir,
            CancellationToken token)
        {
            string xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath);
            long appendedVideoLength = LivePhotoSplitService.GetAppendedVideoLength(xmpText);
            if (appendedVideoLength <= 0)
                throw new InvalidDataException("Cannot determine embedded video length from XMP metadata.");

            long videoLength = appendedVideoLength;

            // OPPO 原厂文件：只提取纯视频，避免把 OnePlus trailer 一起拼进新文件。
            if (protocol == LivePhotoProtocolType.OPPO)
            {
                long pureLength = LivePhotoSplitService.GetOppoPureVideoLength(xmpText);
                if (pureLength > 0 && pureLength <= videoLength)
                    videoLength = pureLength;
            }

            long fileSize = new FileInfo(imagePath).Length;
            long videoOffset = fileSize - appendedVideoLength;
            if (videoOffset < 0 || videoLength <= 0)
                throw new InvalidDataException("Invalid embedded video range in XMP live photo.");

            string targetPath = Path.Combine(workDir, "video.mp4");
            CopyByteRange(imagePath, targetPath, videoOffset, videoLength);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static void CopyByteRange(string sourcePath, string destPath, long start, long length)
        {
            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            src.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[81920];
            long remaining = length;
            while (remaining > 0)
            {
                int read = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    break;
                dst.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  帧提取 / EXIF / HEIC / XMP
        // ═══════════════════════════════════════════════════════════

        private static async Task<string> ExtractFrameAsync(
            CoverChangeRequest request,
            string videoPath,
            string workDir,
            string outputName,
            CancellationToken token)
        {
            string desiredPath = Path.Combine(workDir, outputName);

            if (request.FrameIndex is int frameIndex)
            {
                await ExtractFrameAtIndexAsync(videoPath, frameIndex, desiredPath, token).ConfigureAwait(false);
                return desiredPath;
            }

            double timestampSec = request.TimestampUs / 1_000_000.0;
            string? framePath = await LivePhotoMergeService.ExtractFrameAtTimestampAsync(
                videoPath, workDir, timestampSec, token).ConfigureAwait(false);

            if (string.IsNullOrEmpty(framePath) || !File.Exists(framePath))
                throw new InvalidDataException("Failed to extract cover frame from video.");

            if (!string.Equals(framePath, desiredPath, StringComparison.OrdinalIgnoreCase))
                File.Move(framePath, desiredPath, overwrite: true);

            return desiredPath;
        }

        private static async Task ExtractFrameAtIndexAsync(
            string videoPath,
            int frameIndex,
            string targetPath,
            CancellationToken token)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new FileNotFoundException("ffmpeg not found.");

            string frameDir = Path.Combine(Path.GetTempPath(), $"lpb_frame_{Guid.NewGuid():N}");
            Directory.CreateDirectory(frameDir);
            try
            {
                string outputPattern = Path.Combine(frameDir, "frame_%06d.jpg");
                string args = $"-i \"{videoPath}\" -vsync 0 " +
                              $"-q:v 3 -f image2 \"{outputPattern}\" -y -loglevel error";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start ffmpeg.");

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    throw;
                }

                string stderr = await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                    throw new InvalidDataException($"ffmpeg failed to extract frames: {stderr.Trim()}");

                string framePath = Path.Combine(frameDir, $"frame_{frameIndex + 1:D6}.jpg");
                if (!File.Exists(framePath))
                    throw new InvalidDataException(
                        $"Video does not contain frame {frameIndex + 1}.");

                File.Move(framePath, targetPath, overwrite: true);
            }
            finally
            {
                try { Directory.Delete(frameDir, recursive: true); } catch { /* best effort */ }
            }
        }

        private static Task CopyExifFromSourceAsync(
            string sourceImagePath,
            string targetImagePath,
            CancellationToken token)
        {
            return LivePhotoRepairService.RunExifToolAsync(token,
                "-TagsFromFile", sourceImagePath,
                "-all:all",
                "--xmp:all",
                "-Orientation=",
                "-ExifImageWidth=",
                "-ExifImageHeight=",
                "-ThumbnailImage=",
                "-overwrite_original",
                "-quiet",
                targetImagePath);
        }

        private static async Task WriteAppleTimestampXmpAsync(
            string targetJpeg,
            long timestampUs,
            string workDir,
            CancellationToken token)
        {
            string xmpFilePath = Path.Combine(workDir, "timestamp.xmp");
            string xmpContent =
                "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
                "<rdf:Description rdf:about=\"\"\n" +
                "  xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"\n" +
                $"  GCamera:MotionPhotoPresentationTimestampUs=\"{timestampUs}\"/>\n" +
                "</rdf:RDF>\n" +
                "</x:xmpmeta>\n" +
                "<?xpacket end=\"w\"?>";

            await File.WriteAllTextAsync(xmpFilePath, xmpContent, new UTF8Encoding(false), token).ConfigureAwait(false);
            await LivePhotoRepairService.RunExifToolAsync(token,
                $"-xmp<={xmpFilePath}",
                "-overwrite_original",
                "-quiet",
                targetJpeg).ConfigureAwait(false);
        }

        private static async Task<string> EncodeJpegToHeicAsync(
            string jpegPath,
            string workDir,
            CancellationToken token)
        {
            string heifEncPath = FindRequiredHeifEnc();
            string outputPath = Path.Combine(workDir, $"keyframe_{Guid.NewGuid():N}.heic");

            var psi = new ProcessStartInfo
            {
                FileName = heifEncPath,
                Arguments = $"-o \"{outputPath}\" -q 90 \"{jpegPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start heif-enc.exe.");

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException($"heif-enc failed with exit code {process.ExitCode}.");

            return outputPath;
        }

        private static string FindRequiredHeifEnc()
        {
            string? heifEncPath = ExternalToolLocator.FindHeifEnc();
            if (string.IsNullOrEmpty(heifEncPath) || !File.Exists(heifEncPath))
                throw new FileNotFoundException("heif-enc.exe not found.");
            return heifEncPath;
        }

        internal static async Task<string?> ReadContentIdentifierAsync(string filePath, CancellationToken token)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifToolPath))
                return null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-j -ContentIdentifier \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return null;

                string json = await process.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);

                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
                    return null;

                var root = document.RootElement[0];
                return root.TryGetProperty("ContentIdentifier", out var cidElement)
                    ? cidElement.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static Task WriteContentIdentifierAsync(
            string targetPath,
            string contentIdentifier,
            CancellationToken token)
        {
            return LivePhotoRepairService.RunExifToolAsync(token,
                $"-ContentIdentifier={contentIdentifier}",
                "-overwrite_original",
                "-quiet",
                targetPath);
        }

        // ═══════════════════════════════════════════════════════════
        //  辅助
        // ═══════════════════════════════════════════════════════════

        private static byte[]? ReadVivoTail(string imagePath)
        {
            try
            {
                const int tailProbe = 8192;
                using var srcFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int probeSize = (int)Math.Min(srcFs.Length, tailProbe);
                if (probeSize < 6)
                    return null;

                byte[] probe = new byte[probeSize];
                srcFs.Seek(-probeSize, SeekOrigin.End);
                srcFs.ReadExactly(probe, 0, probeSize);

                int vivoIndex = -1;
                for (int i = probeSize - 6; i >= 0; i--)
                {
                    if (probe[i] == 'v' && probe[i + 1] == 'i' &&
                        probe[i + 2] == 'v' && probe[i + 3] == 'o' &&
                        probe[i + 4] == '{')
                    {
                        vivoIndex = i;
                        break;
                    }
                }

                if (vivoIndex < 0)
                    return null;

                byte[] endMarker = "cameralbum!"u8.ToArray();
                int endIndex = -1;
                for (int i = vivoIndex; i <= probeSize - endMarker.Length; i++)
                {
                    bool matched = true;
                    for (int j = 0; j < endMarker.Length; j++)
                    {
                        if (probe[i + j] != endMarker[j])
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (matched)
                    {
                        endIndex = i + endMarker.Length;
                        break;
                    }
                }

                if (endIndex <= vivoIndex)
                    return null;

                int tailLength = probeSize - vivoIndex;
                byte[] tail = new byte[tailLength];
                Array.Copy(probe, vivoIndex, tail, 0, tailLength);
                return tail;
            }
            catch
            {
                return null;
            }
        }

        private static int ToProtocolIndex(LivePhotoProtocolType protocol)
        {
            return protocol switch
            {
                LivePhotoProtocolType.Fusion => 0,
                LivePhotoProtocolType.GoogleV1 => 1,
                LivePhotoProtocolType.GoogleV2 => 2,
                LivePhotoProtocolType.OPPO => 3,
                LivePhotoProtocolType.Vivo => 4,
                LivePhotoProtocolType.Samsung => 5,
                LivePhotoProtocolType.Huawei => 6,
                LivePhotoProtocolType.Apple => 2,
                _ => 2
            };
        }

        private static void TrySetLastWriteTime(string path)
        {
            try
            {
                File.SetLastWriteTime(path, DateTime.Now);
            }
            catch
            {
                // best effort
            }
        }
    }
}
