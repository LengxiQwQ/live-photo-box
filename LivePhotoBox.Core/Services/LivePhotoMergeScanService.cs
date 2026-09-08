using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// A dual-file pair confirmed by Native-backed metadata and pair inspection.
    /// BaseName is display-only and is never pairing authority.
    /// </summary>
    public sealed class LivePhotoFilePairInfo
    {
        public required string BaseName { get; init; }
        public required string ImagePath { get; init; }
        public required string VideoPath { get; init; }
        public required long ImageSizeBytes { get; init; }
        public required long VideoSizeBytes { get; init; }
        public required LivePhotoProtocolType Protocol { get; init; }
        public required LivePhotoDetectionMethod DetectionMethod { get; init; }
        public required long VideoByteOffset { get; init; }
        public required long VideoByteLength { get; init; }
        public required VideoContainer VideoContainer { get; init; }
        public string? PairingIdentifier { get; init; }
    }

    public sealed class LivePhotoScanResult
    {
        public required IReadOnlyList<LivePhotoFilePairInfo> Pairs { get; init; }
        public required int StandaloneImagesCount { get; init; }
        public required int StandaloneVideosCount { get; init; }
        public IReadOnlyList<string> StandaloneImagePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> StandaloneVideoPaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Discovers dual-file Live Photos through the formal metadata matcher and
    /// re-inspects every returned pair at the Native boundary. Filename
    /// similarity is retained only for display and never creates a pair.
    /// </summary>
    public static class LivePhotoMergeScanService
    {
        public static LivePhotoScanResult Scan(
            string inputDirectory,
            CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null)
        {
            LogService.Scan($"Scan started. Directory: {inputDirectory}");
            progress?.Report(new WorkProgressSnapshot(0, 0));

            bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var allFiles = Directory.EnumerateFiles(inputDirectory, "*.*", searchOption).ToList();
            int total = allFiles.Count;
            progress?.Report(new WorkProgressSnapshot(total, 0));

            var imagePaths = allFiles.Where(IsImageFile).ToList();
            var videoPaths = allFiles.Where(IsVideoFile).ToList();
            var matcher = LivePhotoMetadataMatcher.MatchAsync(imagePaths, videoPaths, cancellationToken)
                .GetAwaiter().GetResult();

            var matchedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var metadataPairs = matcher.Pairs.ToList();

            var remainingImages = imagePaths
                .Where(path => !metadataPairs.Any(pair => string.Equals(pair.ImagePath, path, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var remainingVideos = videoPaths
                .Where(path => !metadataPairs.Any(pair => string.Equals(pair.VideoPath, path, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var vivo = LivePhotoMetadataMatcher.MatchVivo(remainingImages, remainingVideos);
            metadataPairs.AddRange(vivo.Pairs);

            var pairs = new List<LivePhotoFilePairInfo>(metadataPairs.Count);
            var inspector = new SourceInspector();
            foreach (var pair in metadataPairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var facts = inspector.InspectAsync(pair.ImagePath, pair.VideoPath, cancellationToken)
                    .GetAwaiter().GetResult();
                if (facts.PrimaryImage is not { IsPresent: true } ||
                    facts.Protocol is SourceProtocol.NonLive or SourceProtocol.Unknown ||
                    facts.MotionVideo is not { IsPresent: true, SourceIndex: 1 } motion)
                {
                    throw new SourceInspectionException(
                        SourceInspectionFailureCategory.Malformed,
                        SourceInspectionStage.Pairing,
                        0,
                        $"Native facts did not confirm dual-file pair '{pair.ImagePath}' + '{pair.VideoPath}'.");
                }

                LivePhotoProtocolType protocol = MapProtocol(facts.Protocol);
                if (protocol == LivePhotoProtocolType.Unknown)
                    throw new SourceInspectionException(
                        SourceInspectionFailureCategory.Unsupported,
                        SourceInspectionStage.Pairing,
                        0,
                        $"Native facts returned an unsupported protocol for '{pair.ImagePath}'.");

                matchedImages.Add(pair.ImagePath);
                matchedVideos.Add(pair.VideoPath);
                pairs.Add(new LivePhotoFilePairInfo
                {
                    BaseName = Path.GetFileNameWithoutExtension(pair.ImagePath),
                    ImagePath = pair.ImagePath,
                    VideoPath = pair.VideoPath,
                    ImageSizeBytes = new FileInfo(pair.ImagePath).Length,
                    VideoSizeBytes = new FileInfo(pair.VideoPath).Length,
                    Protocol = protocol,
                    DetectionMethod = pair.Source == MatchSource.ContentIdentifier
                        ? LivePhotoDetectionMethod.ContentIdentifier
                        : LivePhotoDetectionMethod.VivoLivePhoto,
                    VideoByteOffset = motion.ByteOffset,
                    VideoByteLength = motion.ByteLength,
                    VideoContainer = motion.Container,
                    PairingIdentifier = facts.PairingIdentifier
                });
                progress?.Report(new WorkProgressSnapshot(total, total, pairs.Count));
            }

            var standaloneImages = imagePaths.Where(path => !matchedImages.Contains(path)).ToList();
            var standaloneVideos = videoPaths.Where(path => !matchedVideos.Contains(path)).ToList();
            LogService.Scan($"Scan completed. Found {pairs.Count} Native-confirmed pairs, " +
                $"{standaloneImages.Count} standalone images, {standaloneVideos.Count} standalone videos");
            return new LivePhotoScanResult
            {
                Pairs = pairs,
                StandaloneImagesCount = standaloneImages.Count,
                StandaloneVideosCount = standaloneVideos.Count,
                StandaloneImagePaths = standaloneImages,
                StandaloneVideoPaths = standaloneVideos
            };
        }

        private static LivePhotoProtocolType MapProtocol(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
            SourceProtocol.VivoLegacyDualFile or SourceProtocol.VivoLivePhoto => LivePhotoProtocolType.Vivo,
            _ => LivePhotoProtocolType.Unknown
        };

        private static bool IsImageFile(string path) =>
            path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }
}
