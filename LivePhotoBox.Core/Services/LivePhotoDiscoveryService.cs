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
    // Protocol discovery never interprets a filename composition request as a source-live binding.
    public static class LivePhotoDiscoveryService
    {
        public static Task<LivePhotoDiscoveryResult> ScanAsync(string inputDirectory,
            DiscoveryScanMode scanMode = DiscoveryScanMode.All, CancellationToken ct = default,
            IProgress<WorkProgressSnapshot>? progress = null, IProgress<LivePhotoDiscoveryItem>? itemProgress = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
            return ScanFilesAsync(new MergeScanOptions
            {
                InputDirectory = inputDirectory,
                Recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false)
            }, scanMode, ct, progress, itemProgress);
        }
        internal static async Task<LivePhotoDiscoveryResult> ScanFilesAsync(MergeScanOptions options,
            DiscoveryScanMode scanMode, CancellationToken ct = default, IProgress<WorkProgressSnapshot>? progress = null,
            IProgress<LivePhotoDiscoveryItem>? itemProgress = null, DiscoveryInspectionSession? session = null,
            Func<IReadOnlyList<LivePhotoDiscoveryItem>, CancellationToken, Task>? candidateBatch = null)
        {
            using var ownedSession = session == null ? new DiscoveryInspectionSession() : null;
            session ??= ownedSession!;
            var diagnostics = new List<ScanDiagnostic>();
            var items = new List<LivePhotoDiscoveryItem>();
            bool partial = false;
            LivePhotoDiscoveryResult Cancelled() => new() { Items = items, Diagnostics = diagnostics,
                Status = ScanOperationStatus.Cancelled, IsPartial = true };
            try
            {
                progress?.Report(new(0, 0, Stage: ScanWorkStage.Enumerating));
                var (images, videos, enumerationPartial, rootFailed) = LivePhotoScanSupport.Collect(options, diagnostics, ct);
                partial = enumerationPartial;
                if (ct.IsCancellationRequested) return Cancelled();
                progress?.Report(new(images.Count + videos.Count, images.Count + videos.Count, Stage: ScanWorkStage.Enumerating));
                bool matching = (scanMode & (DiscoveryScanMode.CidMatch | DiscoveryScanMode.VivoMatch | DiscoveryScanMode.FilenamePair)) != 0;
                var paths = matching ? images.Concat(videos).ToArray() : images.ToArray();
                int completed = 0;
                var pending = new List<LivePhotoDiscoveryItem>();
                long lastBatch = Environment.TickCount64;
                foreach (var path in paths)
                {
                    if (ct.IsCancellationRequested) return Cancelled();
                    try
                    {
                        var fileRead = ScanFileSystem.Read(path);
                        if (fileRead.Failure is { } readFailure) { partial = true; diagnostics.Add(LivePhotoScanSupport.Failure(path, readFailure)); continue; }
                        var info = fileRead.Value!;
                        var item = new LivePhotoDiscoveryItem { FilePath = path, FileSizeBytes = info.Length, LastWriteTime = info.LastWriteTimeUtc.ToLocalTime() };
                        items.Add(item);
                        bool read = LivePhotoScanSupport.IsImage(path) && (Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg"
                            ? scanMode.HasFlag(DiscoveryScanMode.JpegMarkers) : scanMode.HasFlag(DiscoveryScanMode.HeicTrack));
                        if (read)
                        {
                            var marker = await session.ReadFastCandidateAsync(path, ct, LivePhotoBox.Interop.DiscoveryCapability.SingleFileMarker).ConfigureAwait(false);
                            if (marker?.State is LivePhotoBox.Interop.FactState.Incomplete or LivePhotoBox.Interop.FactState.ReadFailed)
                            {
                                partial = true; diagnostics.Add(LivePhotoScanSupport.Info(path, "DiscoveryIncomplete", marker.Diagnostic ?? "Marker discovery incomplete."));
                            }
                            if (marker?.SingleFileProtocol is not (null or SourceProtocol.Unknown))
                            {
                                item.Protocol = marker.SingleFileProtocol;
                                item.LivePhotoType = marker.ImageContainer == ImageContainer.Jpeg ? LivePhotoType.SingleFileJpeg : LivePhotoType.SingleFileHeic;
                                item.DetectionMethod = marker.ImageContainer == ImageContainer.Jpeg ? LivePhotoDetectionMethod.JpegByteMarkers : LivePhotoDetectionMethod.HeicVideoTrack;
                                itemProgress?.Report(item);
                                pending.Add(item);
                            }
                        }
                    }
                    catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
                    { partial = true; diagnostics.Add(LivePhotoScanSupport.Failure(path, ex)); }
                    if (candidateBatch != null && pending.Count > 0 && (pending.Count >= 128 || Environment.TickCount64 - lastBatch >= 100))
                    {
                        await candidateBatch(pending.ToArray(),ct).ConfigureAwait(false);pending.Clear();lastBatch = Environment.TickCount64;
                    }
                    progress?.Report(new(paths.Length, ++completed, Stage: ScanWorkStage.Identifying));
                }
                progress?.Report(new(pending.Count, 0, Stage: ScanWorkStage.PreparingResults));
                if (candidateBatch != null && pending.Count > 0) await candidateBatch(pending.ToArray(),ct).ConfigureAwait(false);
                progress?.Report(new(pending.Count, pending.Count, Stage: ScanWorkStage.PreparingResults));
                if (ct.IsCancellationRequested) return Cancelled();
                var byPath = items.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);
                foreach (var (flag, mode) in new[] { (DiscoveryScanMode.CidMatch, PairingMode.Cid), (DiscoveryScanMode.VivoMatch, PairingMode.Vivo) })
                {
                    if (!scanMode.HasFlag(flag)) continue;
                    var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { ExplicitFiles = paths, Mode = mode }, ct, progress, session).ConfigureAwait(false);
                    partial |= result.IsPartial;diagnostics.AddRange(result.Diagnostics);
                    foreach (var pair in result.Pairs)
                    {
                        if (!byPath.TryGetValue(pair.ImagePath,out var image) || !byPath.TryGetValue(pair.VideoPath,out var video) || image.IsLivePhoto) continue;
                        image.LivePhotoType = video.LivePhotoType = LivePhotoType.DualFile;
                        image.Protocol = video.Protocol = mode == PairingMode.Cid ? SourceProtocol.AppleLivePhoto : SourceProtocol.VivoLegacyDualFile;
                        image.ContentIdentifier = video.ContentIdentifier = pair.PairingIdentifier;
                        image.PairedVideoPath = video.FilePath;video.PairedImagePath = image.FilePath;
                        image.DetectionMethod = video.DetectionMethod = pair.DetectionMethod;
                        itemProgress?.Report(image);
                    }
                }
                if (scanMode == DiscoveryScanMode.FilenamePair)
                {
                    var named = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { ExplicitFiles = paths, Mode = PairingMode.Name },ct,progress,session).ConfigureAwait(false);
                    diagnostics.AddRange(named.Diagnostics);
                    foreach (var pair in named.Pairs)
                    {
                        var inspected = await session.InspectForDiscoveryAsync(pair.ImagePath,pair.VideoPath,ct).ConfigureAwait(false);
                        if (inspected.Failure is { } failure) { diagnostics.Add(LivePhotoScanSupport.Failure(pair.ImagePath,failure));continue; }
                        if (inspected.Facts is not { Protocol: SourceProtocol.AppleLivePhoto or SourceProtocol.VivoLegacyDualFile } facts) continue;
                        if (!byPath.TryGetValue(pair.ImagePath,out var image)||!byPath.TryGetValue(pair.VideoPath,out var video))continue;
                        image.LivePhotoType=video.LivePhotoType=LivePhotoType.DualFile;image.Protocol=video.Protocol=facts.Protocol;
                        image.ContentIdentifier=video.ContentIdentifier=facts.PairingIdentifier;
                        image.PairedVideoPath=video.FilePath;video.PairedImagePath=image.FilePath;
                    }
                }
                if (ct.IsCancellationRequested) return Cancelled();
                return new() { Items = items, Diagnostics = diagnostics, IsPartial = partial,
                    Status = rootFailed && paths.Length == 0 ? ScanOperationStatus.Failed : ScanOperationStatus.Completed };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return new() { Items = items, Diagnostics = diagnostics, Status = ScanOperationStatus.Cancelled, IsPartial = true }; }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                diagnostics.Add(LivePhotoScanSupport.Failure(options.InputDirectory ?? "",ex,"ScanOperationFailed"));
                return new() { Items = items, Diagnostics = diagnostics, Status = ScanOperationStatus.Failed, IsPartial = true };
            }
        }
        private static bool SameDiagnostic(ScanDiagnostic left, ScanDiagnostic right) =>
            left.DiagnosticCode == right.DiagnosticCode && left.Severity == right.Severity && left.Stage == right.Stage &&
            string.Equals(left.PrimaryPath, right.PrimaryPath, StringComparison.OrdinalIgnoreCase) &&
            left.RelatedPaths.SequenceEqual(right.RelatedPaths, StringComparer.OrdinalIgnoreCase) &&
            left.GroupKey == right.GroupKey && left.MessageKey == right.MessageKey &&
            (left.MessageArgs ?? Array.Empty<object>()).SequenceEqual(right.MessageArgs ?? Array.Empty<object>()) &&
            left.Summary == right.Summary && left.RawCategory == right.RawCategory &&
            left.RawStage == right.RawStage && left.RawCapability == right.RawCapability && left.LogMessage == right.LogMessage;
        private static void ApplySingleFacts(LivePhotoDiscoveryItem item, SourceMediaFacts facts)
        {
            item.Protocol = facts.Protocol;
            item.ContentIdentifier = facts.PairingIdentifier;
            if (facts.Protocol is SourceProtocol.NonLive or SourceProtocol.Unknown ||
                !facts.PrimaryImage.IsPresent || facts.MotionVideo is not { IsPresent: true, SourceIndex: 0 } video) return;
            bool jpeg = Path.GetExtension(item.FilePath).ToLowerInvariant() is ".jpg" or ".jpeg";
            item.LivePhotoType = jpeg ? LivePhotoType.SingleFileJpeg : LivePhotoType.SingleFileHeic;
            item.DetectionMethod = jpeg ? LivePhotoDetectionMethod.JpegByteMarkers : LivePhotoDetectionMethod.HeicVideoTrack;
            item.AppendedVideoLength = video.ByteLength;
            item.MotionVideoByteOffset = video.ByteOffset;
            item.MotionVideoByteLength = video.ByteLength;
            item.MotionVideoContainer = video.Container;
        }
        public static async Task<(LivePhotoType Type, SourceProtocol Protocol, long MotionVideoLength, ScanDiagnostic? Diagnostic)>
            DetectSingleFileWithDiagnosticAsync(string filePath, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return (LivePhotoType.None, SourceProtocol.Unknown, 0,
                LivePhotoScanSupport.Info(filePath, "Cancelled", "Discovery cancelled."));
            try
            {
                if (!LivePhotoScanSupport.IsImage(filePath))
                    return (LivePhotoType.None, SourceProtocol.Unknown, 0, LivePhotoScanSupport.Info(filePath, "UnsupportedExtension", "Unsupported image extension."));
                var marker = await LivePhotoBox.Interop.NativeMediaCandidate.ReadAsync(filePath, ct,
                    LivePhotoBox.Interop.DiscoveryCapability.SingleFileMarker).ConfigureAwait(false);
                var type = marker?.SingleFileProtocol is not (null or SourceProtocol.Unknown)
                    ? marker.ImageContainer == ImageContainer.Jpeg ? LivePhotoType.SingleFileJpeg : LivePhotoType.SingleFileHeic : LivePhotoType.None;
                return (type, marker?.SingleFileProtocol ?? SourceProtocol.Unknown, 0,
                    marker?.State is LivePhotoBox.Interop.FactState.Incomplete or LivePhotoBox.Interop.FactState.ReadFailed
                        ? LivePhotoScanSupport.Info(filePath,"DiscoveryIncomplete",marker.Diagnostic ?? "Marker discovery incomplete.") : null);
            }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                return (LivePhotoType.None, SourceProtocol.Unknown, 0, LivePhotoScanSupport.Failure(filePath, ex));
            }
        }
        // Preserve the strict compatibility API: callers without a diagnostic result must see inspection failures.
        public static async Task<LivePhotoType> DetectSingleFileTypeAsync(string filePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || !LivePhotoScanSupport.IsImage(filePath)) return LivePhotoType.None;
            var facts = await new SourceInspector().InspectAsync(filePath, null, ct).ConfigureAwait(false);
            var item = new LivePhotoDiscoveryItem { FilePath = filePath, FileSizeBytes = new FileInfo(filePath).Length };
            ApplySingleFacts(item, facts);
            return item.LivePhotoType;
        }
    }
}
