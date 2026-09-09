using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
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
    /// 统一处理单文件 / 双文件输入，并只消费 Native source facts。
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

                var pairFacts = await new SourceInspector().InspectAsync(
                    pair.Value.Image, pair.Value.Video, token).ConfigureAwait(false);
                var protocol = MapSourceProtocol(pairFacts.Protocol);
                if (pairFacts.PrimaryImage is not { IsPresent: true } ||
                    pairFacts.MotionVideo is not { IsPresent: true, SourceIndex: 1 } ||
                    protocol == LivePhotoProtocolType.Unknown)
                {
                    LogService.Info("[Cover] Resolve failed: Native facts did not confirm a dual-file live-photo pair", LogSource.System);
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

            // 1. Inspect the selected source once and consume its exact facts.
            var singleFacts = await new SourceInspector().InspectAsync(imagePath, null, token).ConfigureAwait(false);
            var singleProtocol = MapSourceProtocol(singleFacts.Protocol);
            if (singleFacts.PrimaryImage is { IsPresent: true } &&
                singleFacts.MotionVideo is { IsPresent: true, SourceIndex: 0 } &&
                singleProtocol != LivePhotoProtocolType.Unknown)
            {
                return new CoverInputResolution
                {
                    ImagePath = imagePath,
                    LivePhotoType = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                        ? LivePhotoType.SingleFileJpeg
                        : LivePhotoType.SingleFileHeic,
                    Protocol = singleProtocol
                };
            }

            // 2. 单文件检测未命中时，按双文件实况尝试配对。
            string? pairedVideo = await FindPairedVideoAsync(imagePath, token).ConfigureAwait(false);
            if (pairedVideo == null)
            {
                LogService.Info($"[Cover] Resolve failed: Native single-file facts missed, no paired video found for '{imagePath}'", LogSource.System);
                return null;
            }

            var dualFacts = await new SourceInspector().InspectAsync(imagePath, pairedVideo, token).ConfigureAwait(false);
            var dualProtocol = MapSourceProtocol(dualFacts.Protocol);
            if (dualFacts.PrimaryImage is not { IsPresent: true } ||
                dualFacts.MotionVideo is not { IsPresent: true, SourceIndex: 1 } ||
                dualProtocol == LivePhotoProtocolType.Unknown)
            {
                LogService.Info($"[Cover] Resolve failed: Native facts did not confirm a paired live photo for '{imagePath}'", LogSource.System);
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

        private static async Task<string?> FindPairedVideoAsync(string imagePath, CancellationToken token)
        {
            string dir = Path.GetDirectoryName(imagePath)!;
            // Protocol pairing is metadata-authoritative. A same-name file is
            // only a candidate for explicit user composition and cannot be
            // returned as a detected source pair here.
            var videoPaths = Directory
                .EnumerateFiles(dir, "*.*")
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            if (videoPaths.Count == 0)
                return null;

            var match = await LivePhotoMetadataMatcher.MatchAsync(
                new[] { imagePath },
                videoPaths,
                token).ConfigureAwait(false);

            return match.Pairs.FirstOrDefault()?.VideoPath;
        }

        private static LivePhotoProtocolType MapSourceProtocol(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
            SourceProtocol.GoogleMicroVideoV1 => LivePhotoProtocolType.GoogleV1,
            SourceProtocol.GoogleMotionPhotoV2 => LivePhotoProtocolType.GoogleV2,
            SourceProtocol.OppoLivePhoto => LivePhotoProtocolType.OPPO,
            SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile => LivePhotoProtocolType.Vivo,
            SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic => LivePhotoProtocolType.Samsung,
            SourceProtocol.HuaweiMovingPhoto or SourceProtocol.HonorMovingPhoto => LivePhotoProtocolType.Huawei,
            _ => LivePhotoProtocolType.Unknown
        };
    }
}
