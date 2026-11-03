using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LivePhotoBox.Services
{
    public sealed class LivePhotoSplitFileInfo
    {
        public required string SourcePath { get; init; }
        public string? SecondaryPath { get; init; }
        public required long FileSizeBytes { get; init; }
        public long AppendedVideoLength { get; init; }
    }

    public sealed class LivePhotoSplitScanResult
    {
        public required IReadOnlyList<LivePhotoSplitFileInfo> Files { get; init; }
        public required int RecognizedCount { get; init; }
        public required int SkippedCount { get; init; }
        public IReadOnlyList<ScanDiagnostic> Diagnostics { get; init; } = Array.Empty<ScanDiagnostic>();
        public ScanOperationStatus Status { get; init; }
        public bool IsPartial { get; init; }
    }

    public static class LivePhotoSplitScanService
    {
        public static LivePhotoSplitScanResult Scan(string inputDirectory, CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null, IProgress<LivePhotoSplitFileInfo>? itemProgress = null)
        {
            var discovery = LivePhotoDiscoveryService.ScanAsync(inputDirectory, DiscoveryScanMode.SplitOnly,
                cancellationToken, progress).GetAwaiter().GetResult();
            var files = discovery.Items.Where(i => i.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic || i.LivePhotoType == LivePhotoType.DualFile && i.IsImage)
                .Select(i => new LivePhotoSplitFileInfo
                {
                    SourcePath = i.FilePath, SecondaryPath = i.PairedVideoPath, FileSizeBytes = i.FileSizeBytes, AppendedVideoLength = i.AppendedVideoLength
                }).ToList();
            foreach (var file in files) itemProgress?.Report(file);
            return new LivePhotoSplitScanResult
            {
                Files = files, RecognizedCount = files.Count, SkippedCount = discovery.Items.Count - files.Count,
                Diagnostics = discovery.Diagnostics, Status = discovery.Status, IsPartial = discovery.IsPartial
            };
        }

        public static bool IsLikelyLivePhoto(string path, long fileSize)
        {
            var facts = new SourceInspector().InspectAsync(path).GetAwaiter().GetResult();
            return facts.Protocol is not SourceProtocol.NonLive and not SourceProtocol.Unknown &&
                facts.PrimaryImage.IsPresent && facts.MotionVideo is { IsPresent: true, SourceIndex: 0 };
        }
    }
}
