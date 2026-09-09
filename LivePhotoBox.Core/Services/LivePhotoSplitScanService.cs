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
    public sealed class LivePhotoSplitFileInfo
    {
        public required string SourcePath { get; init; }
        public required long FileSizeBytes { get; init; }
        public long AppendedVideoLength { get; init; }
    }

    public sealed class LivePhotoSplitScanResult
    {
        public required IReadOnlyList<LivePhotoSplitFileInfo> Files { get; init; }
        public required int RecognizedCount { get; init; }
        public required int SkippedCount { get; init; }
    }

    // Split discovery is orchestration only. Protocol recognition, byte ranges,
    // and failure semantics come from the Native-backed SourceInspector.
    public static class LivePhotoSplitScanService
    {
        private const int ProgressInterval = 4;

        public static LivePhotoSplitScanResult Scan(
            string inputDirectory,
            CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null,
            IProgress<LivePhotoSplitFileInfo>? itemProgress = null)
        {
            LogService.Scan($"Split scan started. Directory: {inputDirectory}");
            progress?.Report(new WorkProgressSnapshot(0, 0));

            var candidates = new List<string>();
            int enumerated = 0;
            try
            {
                bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var path in Directory.EnumerateFiles(inputDirectory, "*.*", option))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    enumerated++;
                    if (IsSupportedImage(path)) candidates.Add(path);
                    if (enumerated == 1 || enumerated % 64 == 0)
                        progress?.Report(new WorkProgressSnapshot(0, enumerated));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException ex)
            {
                LogService.Scan($"Access denied to directory: {inputDirectory}", LogLevel.Error, ex);
                return EmptyResult();
            }
            catch (DirectoryNotFoundException ex)
            {
                LogService.Scan($"Directory not found: {inputDirectory}", LogLevel.Error, ex);
                return EmptyResult();
            }
            catch (IOException ex)
            {
                LogService.Scan($"IO error scanning directory: {inputDirectory}", LogLevel.Error, ex);
                return EmptyResult();
            }

            if (candidates.Count == 0) return EmptyResult();

            var files = new List<LivePhotoSplitFileInfo>();
            int skipped = 0;
            progress?.Report(new WorkProgressSnapshot(candidates.Count, 0));
            for (int i = 0; i < candidates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = candidates[i];
                var fileInfo = new FileInfo(path);
                if (TryInspectLivePhoto(path, out var facts))
                {
                    var info = new LivePhotoSplitFileInfo
                    {
                        SourcePath = path,
                        FileSizeBytes = fileInfo.Length,
                        AppendedVideoLength = facts.MotionVideo?.ByteLength ?? 0
                    };
                    files.Add(info);
                    itemProgress?.Report(info);
                }
                else skipped++;

                int completed = i + 1;
                if (completed == 1 || completed % ProgressInterval == 0 || completed == candidates.Count)
                    progress?.Report(new WorkProgressSnapshot(candidates.Count, completed, files.Count, skipped));
            }

            progress?.Report(new WorkProgressSnapshot(candidates.Count, candidates.Count, files.Count, skipped));
            LogService.Scan($"Split scan completed. Found {files.Count} LivePhotos, skipped {skipped} regular images");
            return new LivePhotoSplitScanResult
            {
                Files = files.OrderBy(x => Path.GetFileName(x.SourcePath), StringComparer.OrdinalIgnoreCase).ToList(),
                RecognizedCount = files.Count,
                SkippedCount = skipped
            };
        }

        private static LivePhotoSplitScanResult EmptyResult() => new()
        {
            Files = Array.Empty<LivePhotoSplitFileInfo>(),
            RecognizedCount = 0,
            SkippedCount = 0
        };

        private static bool IsSupportedImage(string path) =>
            path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        public static bool IsLikelyLivePhoto(string path, long fileSize) => TryInspectLivePhoto(path, out _);

        private static bool TryInspectLivePhoto(string path, out SourceMediaFacts facts)
        {
            facts = new SourceInspector().InspectAsync(path).GetAwaiter().GetResult();
            return facts.Protocol != SourceProtocol.NonLive &&
                facts.Protocol != SourceProtocol.Unknown && facts.MotionVideo?.IsPresent == true;
        }
    }
}
