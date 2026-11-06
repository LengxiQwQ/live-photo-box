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

            IProgress<LivePhotoDiscoveryItem>? itemProgress = null)

        {

            var diagnostics = new List<ScanDiagnostic>();

            var items = new List<LivePhotoDiscoveryItem>();

            try

            {

                var (images, videos, partial, rootFailed) = LivePhotoScanSupport.Collect(options, diagnostics, ct);

                foreach (string path in images.Concat(videos))

                {

                    ct.ThrowIfCancellationRequested();

                    try

                    {

                        var file = new FileInfo(path);

                        items.Add(new LivePhotoDiscoveryItem { FilePath = path, FileSizeBytes = file.Length, LastWriteTime = file.LastWriteTime });

                    }

                    catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))

                    {

                        diagnostics.Add(LivePhotoScanSupport.Failure(path, ex, "FileReadFailed"));

                    }

                }

                var byPath = items.ToDictionary(i => i.FilePath, StringComparer.OrdinalIgnoreCase);

                var pairs = new List<LivePhotoFilePairInfo>();

                foreach (var (flag, mode) in new[] { (DiscoveryScanMode.CidMatch, PairingMode.Cid), (DiscoveryScanMode.VivoMatch, PairingMode.Vivo), (DiscoveryScanMode.FilenamePair, PairingMode.Name) })

                {

                    if (!scanMode.HasFlag(flag)) continue;
                    // Filename matching is an explicit alternative, never a fallback in All/protocol discovery.
                    if (mode == PairingMode.Name && scanMode != DiscoveryScanMode.FilenamePair) continue;

                    var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions

                    {

                        ExplicitFiles = items.Select(i => i.FilePath).ToList(), Mode = mode, RequireSourcePair = true

                    }, ct).ConfigureAwait(false);

                    pairs.AddRange(result.Pairs);

                    if (result.Diagnostics.Count > 0)

                        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == ScanDiagnosticSeverity.Error || mode == PairingMode.Name))
                        {
                            // CID and vivo discovery inspect the same members. Report the same
                            // refusal once while preserving different pairing/container failures.
                            if (!diagnostics.Any(existing => SameDiagnostic(existing, diagnostic)))
                                diagnostics.Add(diagnostic);
                        }

                }

                var pairedPaths = pairs.SelectMany(p => new[] { p.ImagePath, p.VideoPath }).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // A successful full source pair resolves the solo refusal of a member (not any unrelated conflict).

                diagnostics.RemoveAll(d => pairedPaths.Contains(d.PrimaryPath) && d.DiagnosticCode is "SingleFileInspectionFailed" or "UnresolvedBinding");

                foreach (var pair in pairs)

                {

                    ct.ThrowIfCancellationRequested();

                    if (!byPath.TryGetValue(pair.ImagePath, out var image) || !byPath.TryGetValue(pair.VideoPath, out var video)) continue;

                    var protocol = pair.Protocol == LivePhotoProtocolType.Apple ? SourceProtocol.AppleLivePhoto : SourceProtocol.VivoLegacyDualFile;

                    foreach (var item in new[] { image, video })

                    {

                        item.LivePhotoType = LivePhotoType.DualFile;

                        item.DetectionMethod = pair.DetectionMethod;

                        item.Protocol = protocol;

                        item.MotionVideoByteOffset = pair.VideoByteOffset;

                        item.MotionVideoByteLength = pair.VideoByteLength;

                        item.MotionVideoContainer = pair.VideoContainer;

                        item.ContentIdentifier = pair.PairingIdentifier;

                    }

                    image.PairedVideoPath = video.FilePath;

                    video.PairedImagePath = image.FilePath;

                    itemProgress?.Report(image);

                }

                int completed = 0;

                progress?.Report(new WorkProgressSnapshot(items.Count, 0));

                foreach (var item in items)

                {

                    ct.ThrowIfCancellationRequested();

                    if (!pairedPaths.Contains(item.FilePath))

                    {

                        string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();

                        bool inspect = ext is ".jpg" or ".jpeg" ? scanMode.HasFlag(DiscoveryScanMode.JpegMarkers) :

                            ext is ".heic" or ".heif" && scanMode.HasFlag(DiscoveryScanMode.HeicTrack);

                        if (inspect)

                        {

                            try

                            {

                                var inspection = await new SourceInspector().InspectForDiscoveryAsync(item.FilePath, null, ct).ConfigureAwait(false);

                                if (inspection.Failure is { } failure)
                                {
                                    if (!HasError(diagnostics, item.FilePath))
                                    {
                                        diagnostics.Add(LivePhotoScanSupport.Failure(item.FilePath, failure, "SingleFileInspectionFailed"));
                                    }
                                }

                                else

                                {

                                    ApplySingleFacts(item, inspection.Facts!);

                                    if (item.IsLivePhoto)

                                    {

                                        // 单文件实况照片识别成功，移除先前双文件探测的误报冲突

                                        diagnostics.RemoveAll(d => string.Equals(d.PrimaryPath, item.FilePath, StringComparison.OrdinalIgnoreCase));

                                        itemProgress?.Report(item);

                                    }

                                }

                            }

                            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))

                            {

                                if (!HasError(diagnostics, item.FilePath))

                                    diagnostics.Add(LivePhotoScanSupport.Failure(item.FilePath, ex));

                            }

                        }

                        if (!item.IsLivePhoto && !HasError(diagnostics, item.FilePath))

                            diagnostics.Add(LivePhotoScanSupport.Info(item.FilePath, "NotSelected", "No source live photo selected by this discovery mode."));

                    }

                    progress?.Report(new WorkProgressSnapshot(items.Count, ++completed, items.Count(i => i.IsLivePhoto)));

                }

                return new LivePhotoDiscoveryResult

                {

                    Items = items.OrderBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase).ToList(), Diagnostics = diagnostics,

                    IsPartial = partial, Status = rootFailed && items.Count == 0 ? ScanOperationStatus.Failed : ScanOperationStatus.Completed

                };

            }

            catch (OperationCanceledException) when (ct.IsCancellationRequested)

            {

                return new LivePhotoDiscoveryResult { Items = Array.Empty<LivePhotoDiscoveryItem>(), Diagnostics = diagnostics,

                    Status = ScanOperationStatus.Cancelled, IsPartial = true };

            }

            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))

            {

                diagnostics.Add(LivePhotoScanSupport.Failure(options.InputDirectory ?? options.InputDirectories?.FirstOrDefault() ??

                    options.ExplicitFiles?.FirstOrDefault() ?? string.Empty, ex, "ScanOperationFailed", stage: ScanDiagnosticStage.Capability));

                return new LivePhotoDiscoveryResult { Items = Array.Empty<LivePhotoDiscoveryItem>(), Diagnostics = diagnostics,

                    Status = ScanOperationStatus.Failed, IsPartial = true, ErrorMessage = ex.Message };

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

        private static bool HasError(IEnumerable<ScanDiagnostic> diagnostics, string path) => diagnostics.Any(d =>

            d.Severity == ScanDiagnosticSeverity.Error &&

            (string.Equals(d.PrimaryPath, path, StringComparison.OrdinalIgnoreCase) || d.RelatedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)));



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

            ct.ThrowIfCancellationRequested();

            try

            {

                if (!LivePhotoScanSupport.IsImage(filePath))

                    return (LivePhotoType.None, SourceProtocol.Unknown, 0, LivePhotoScanSupport.Info(filePath, "UnsupportedExtension", "Unsupported image extension."));

                var inspection = await new SourceInspector().InspectForDiscoveryAsync(filePath, null, ct).ConfigureAwait(false);

                if (inspection.Failure is { } failure)

                    return (LivePhotoType.None, SourceProtocol.Unknown, 0, LivePhotoScanSupport.Failure(filePath, failure,

                        LivePhotoScanSupport.IsInputFailure(failure) ? "SingleFileInspectionFailed" : "ScanOperationFailed"));

                var facts = inspection.Facts!;

                var item = new LivePhotoDiscoveryItem { FilePath = filePath, FileSizeBytes = new FileInfo(filePath).Length };

                ApplySingleFacts(item, facts);

                return (item.LivePhotoType, item.Protocol, item.AppendedVideoLength, null);

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
