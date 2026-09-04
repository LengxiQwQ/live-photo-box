using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// cover 命令解析后的输入信息。
    /// </summary>
    public sealed class CoverInputResolution
    {
        /// <summary>实况照片图片路径。</summary>
        public required string ImagePath { get; init; }

        /// <summary>配对视频路径；单文件实况为 null。</summary>
        public string? VideoPath { get; init; }

        /// <summary>实况照片类型。</summary>
        public required LivePhotoType LivePhotoType { get; init; }

        /// <summary>检测到的协议。</summary>
        public required LivePhotoProtocolType Protocol { get; init; }
    }

    /// <summary>
    /// cover 命令输入解析器。
    ///
    /// 统一处理单文件 / 双文件输入，并复用 Core 的协议检测与配对能力。
    /// </summary>
    public static class CoverInputResolver
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".heic", ".heif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov"
        };

        /// <summary>
        /// 解析 cover 命令输入。
        /// </summary>
        public static async Task<CoverInputResolution?> ResolveAsync(
            string[] files,
            CancellationToken token)
        {
            LogService.Info($"[Cover] Resolve input: files=['{string.Join("', '", files)}']", LogSource.System);

            if (files.Length == 2)
            {
                var pair = ResolveImageVideo(files[0], files[1]);
                if (pair == null)
                {
                    LogService.Info("[Cover] Resolve failed: input files are not a valid image+video pair", LogSource.System);
                    return null;
                }

                if (!File.Exists(pair.Value.Image) || !File.Exists(pair.Value.Video))
                {
                    LogService.Info($"[Cover] Resolve failed: paired file(s) not found (image='{pair.Value.Image}', video='{pair.Value.Video}')", LogSource.System);
                    return null;
                }

                string xmpText = LivePhotoSplitService.ReadMetadataTextSync(pair.Value.Image);
                string? explicitPairContentIdentifier = await CoverChangeService.ReadContentIdentifierAsync(
                    pair.Value.Image, token).ConfigureAwait(false);

                var protocol = LivePhotoProtocolDetector.Detect(
                    pair.Value.Image,
                    LivePhotoType.DualFile,
                    explicitPairContentIdentifier,
                    xmpText);

                if (protocol == LivePhotoProtocolType.Unknown)
                {
                    LogService.Info("[Cover] Resolve failed: dual-file protocol detection returned Unknown", LogSource.System);
                    return null;
                }

                return new CoverInputResolution
                {
                    ImagePath = pair.Value.Image,
                    VideoPath = pair.Value.Video,
                    LivePhotoType = LivePhotoType.DualFile,
                    Protocol = protocol
                };
            }

            string imagePath = files[0];
            if (!File.Exists(imagePath))
            {
                LogService.Info($"[Cover] Resolve failed: image file not found '{imagePath}'", LogSource.System);
                return null;
            }

            string ext = Path.GetExtension(imagePath);
            if (!ImageExtensions.Contains(ext))
            {
                LogService.Info($"[Cover] Resolve failed: unsupported image extension '{ext}'", LogSource.System);
                return null;
            }

            string imageXmp = LivePhotoSplitService.ReadMetadataTextSync(imagePath);

            // 1. 先尝试单文件实况检测。
            LivePhotoType singleFileType = await LivePhotoDiscoveryService.DetectSingleFileTypeAsync(
                imagePath, token).ConfigureAwait(false);

            if (singleFileType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic)
            {
                var protocol = LivePhotoProtocolDetector.Detect(
                    imagePath, singleFileType, xmpText: imageXmp);

                if (protocol != LivePhotoProtocolType.Unknown)
                {
                    return new CoverInputResolution
                    {
                        ImagePath = imagePath,
                        LivePhotoType = singleFileType,
                        Protocol = protocol
                    };
                }
            }

            // 2. 单文件检测未命中时，按双文件实况尝试配对。
            string? pairedVideo = await FindPairedVideoAsync(imagePath, imageXmp, token).ConfigureAwait(false);
            if (pairedVideo == null)
            {
                LogService.Info($"[Cover] Resolve failed: single-file detection ({singleFileType}) missed, no paired video found for '{imagePath}'", LogSource.System);
                return null;
            }

            string? contentIdentifier = await CoverChangeService.ReadContentIdentifierAsync(
                imagePath, token).ConfigureAwait(false);

            var dualProtocol = LivePhotoProtocolDetector.Detect(
                imagePath,
                LivePhotoType.DualFile,
                contentIdentifier,
                imageXmp);

            if (dualProtocol == LivePhotoProtocolType.Unknown)
            {
                LogService.Info($"[Cover] Resolve failed: paired dual-file protocol detection returned Unknown for '{imagePath}'", LogSource.System);
                return null;
            }

            return new CoverInputResolution
            {
                ImagePath = imagePath,
                VideoPath = pairedVideo,
                LivePhotoType = LivePhotoType.DualFile,
                Protocol = dualProtocol
            };
        }

        private static (string Image, string Video)? ResolveImageVideo(string path1, string path2)
        {
            bool is1Image = ImageExtensions.Contains(Path.GetExtension(path1));
            bool is2Image = ImageExtensions.Contains(Path.GetExtension(path2));
            bool is1Video = VideoExtensions.Contains(Path.GetExtension(path1));
            bool is2Video = VideoExtensions.Contains(Path.GetExtension(path2));

            if (is1Image && is2Image)
                return null;
            if (is1Video && is2Video)
                return null;
            if (is1Image && is2Video)
                return (path1, path2);
            if (is1Video && is2Image)
                return (path2, path1);
            return null;
        }

        private static async Task<string?> FindPairedVideoAsync(
            string imagePath,
            string xmpText,
            CancellationToken token)
        {
            string dir = Path.GetDirectoryName(imagePath)!;
            string baseName = Path.GetFileNameWithoutExtension(imagePath);

            // 1. 同 basename 的视频优先。
            foreach (string ext in VideoExtensions)
            {
                string candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate))
                    return candidate;
            }

            // 2. Apple ContentIdentifier 匹配目录中的视频。
            var videoPaths = Directory
                .EnumerateFiles(dir, "*.*")
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (videoPaths.Count == 0)
                return null;

            try
            {
                var match = await LivePhotoMetadataMatcher.MatchAsync(
                    new[] { imagePath },
                    videoPaths,
                    null,
                    token).ConfigureAwait(false);

                return match.Pairs.FirstOrDefault()?.VideoPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
    }
}
