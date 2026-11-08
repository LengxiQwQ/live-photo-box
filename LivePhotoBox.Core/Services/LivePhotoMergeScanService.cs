using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace LivePhotoBox.Services
{
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
        public SelectionIntent SelectionIntent { get; init; } = SelectionIntent.SourceRelationship;
        public MergeScanOptions? SelectionScope { get; init; }
        public bool IsScopeComplete { get; init; } = true;
        public string? PairingIdentifier { get; init; }
        public PairingKind PairingKind { get; init; } = PairingKind.NativeConfirmedSourcePair;
    }
    public sealed class LivePhotoScanResult
    {
        public required IReadOnlyList<LivePhotoFilePairInfo> Pairs { get; init; }
        public required int StandaloneImagesCount { get; init; }
        public required int StandaloneVideosCount { get; init; }
        public IReadOnlyList<string> StandaloneImagePaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> StandaloneVideoPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ScanDiagnostic> Diagnostics { get; init; } = Array.Empty<ScanDiagnostic>();
        public ScanOperationStatus Status { get; init; } = ScanOperationStatus.Completed;
        public bool IsPartial { get; init; }
        public string? ErrorMessage { get; init; }
    }
    public sealed class MergeScanOptions
    {
        public IReadOnlyList<string>? InputDirectories { get; init; }
        public string? InputDirectory { get; init; }
        public IReadOnlyList<string>? ExplicitFiles { get; init; }
        public PairingMode Mode { get; init; } = PairingMode.Name;
        /// <summary>Require protocol proof even when names select the candidate pair. Default preserves CLI composition.</summary>
        public bool RequireSourcePair { get; init; }
        public bool Recursive { get; init; }
    }
    public static class LivePhotoMergeScanService
    {
        private sealed record Candidate(string Path, long Size, SourceMediaFacts? Facts, SourceInspectionFailure? PendingBinding = null,
            DiscoveryPairingCandidate? Hint = null);
        public static async Task<LivePhotoScanResult> ScanAsync(MergeScanOptions options,
            CancellationToken cancellationToken = default, IProgress<WorkProgressSnapshot>? progress = null,
            Func<IReadOnlyList<LivePhotoFilePairInfo>, CancellationToken, Task>? candidateBatch = null)
        {
            using var session = new DiscoveryInspectionSession();
            return await ScanAsync(options, cancellationToken, progress, session, candidateBatch).ConfigureAwait(false);
        }

        internal static async Task<LivePhotoScanResult> ScanAsync(MergeScanOptions options,
            CancellationToken cancellationToken, IProgress<WorkProgressSnapshot>? progress, DiscoveryInspectionSession inspector,
            Func<IReadOnlyList<LivePhotoFilePairInfo>, CancellationToken, Task>? candidateBatch = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options.Mode));
            var diagnostics = new List<ScanDiagnostic>();
            var pairs = new List<LivePhotoFilePairInfo>();
            var images = new List<string>();
            var videos = new List<string>();
            bool partial = false, rootFailed = false;
            ScanOperationStatus status = ScanOperationStatus.Completed;
            LivePhotoScanResult Cancelled() => new() { Pairs = pairs, StandaloneImagesCount = 0,
                StandaloneVideosCount = 0, Diagnostics = diagnostics, Status = ScanOperationStatus.Cancelled, IsPartial = true };
            try
            {
                progress?.Report(new(0, 0, Stage: ScanWorkStage.Enumerating));
                (images, videos, partial, rootFailed) = LivePhotoScanSupport.Collect(options, diagnostics, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return Cancelled();
                progress?.Report(new(images.Count + videos.Count, images.Count + videos.Count, Stage: ScanWorkStage.Enumerating));
                var groups = new Dictionary<string, (List<string> Images, List<string> Videos)>(StringComparer.Ordinal);
                int completed = 0, total = images.Count + videos.Count;
                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var imageSet = images.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string path in images.Concat(videos))
                {
                    if (cancellationToken.IsCancellationRequested) return Cancelled();
                    try
                    {
                        var fileRead = ScanFileSystem.Read(path);
                        if (fileRead.Failure is { } readFailure) { partial = true; diagnostics.Add(LivePhotoScanSupport.Failure(path, readFailure)); continue; }
                        sizes[path] = fileRead.Value!.Length;
                        string? key;
                        if (options.Mode == PairingMode.Name)
                            key = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)).ToUpperInvariant();
                        else
                        {
                            var candidate = await inspector.ReadFastCandidateAsync(path, cancellationToken,
                                options.Mode == PairingMode.Cid ? DiscoveryCapability.AppleId : DiscoveryCapability.VivoId).ConfigureAwait(false);
                            if (candidate?.State is FactState.Incomplete or FactState.ReadFailed)
                            {
                                partial = true;
                                diagnostics.Add(LivePhotoScanSupport.Info(path, "DiscoveryIncomplete", candidate.Diagnostic ?? "Identity discovery is incomplete."));
                            }
                            var hint = candidate?.Pairing;
                            key = hint != null && hint.Role == (imageSet.Contains(path) ? 1u : 2u) ? hint.Identifier : null;
                            if (key != null && options.Mode == PairingMode.Cid)
                                key = Guid.TryParse(key, out var uuid) ? uuid.ToString("D") : null;
                        }
                        if (key != null)
                        {
                            if (!groups.TryGetValue(key, out var group)) groups[key] = group = ([], []);
                            (imageSet.Contains(path) ? group.Images : group.Videos).Add(path);
                        }
                    }
                    catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
                    {
                        partial = true;
                        diagnostics.Add(LivePhotoScanSupport.Failure(path, ex));
                    }
                    progress?.Report(new(total, ++completed, Stage: ScanWorkStage.Identifying));
                }
                progress?.Report(new(groups.Count, 0, Stage: ScanWorkStage.Pairing));
                completed = 0;
                int published = 0;
                long lastBatch = Environment.TickCount64;
                foreach (var (key, group) in groups)
                {
                    if (cancellationToken.IsCancellationRequested) return Cancelled();
                    if (group.Images.Count > 1 || group.Videos.Count > 1)
                    {
                        var paths = group.Images.Concat(group.Videos).ToArray();
                        diagnostics.Add(Conflict(paths[0], paths, options.Mode == PairingMode.Name ? "ConflictMultipleMedia" : "ConflictDuplicateIdentity", key));
                    }
                    else if (group.Images.Count == 1 && group.Videos.Count == 1 && !options.RequireSourcePair ||
                             group.Images.Count == 1 && group.Videos.Count == 1 && options.Mode != PairingMode.Name)
                    {
                        var image = group.Images[0]; var video = group.Videos[0];
                        pairs.Add(new()
                        {
                            BaseName = Path.GetFileNameWithoutExtension(image), ImagePath = image, VideoPath = video,
                            ImageSizeBytes = sizes[image], VideoSizeBytes = sizes[video],
                            Protocol = options.Mode switch { PairingMode.Cid => LivePhotoProtocolType.Apple, PairingMode.Vivo => LivePhotoProtocolType.Vivo, _ => LivePhotoProtocolType.Unknown },
                            DetectionMethod = options.Mode switch { PairingMode.Cid => LivePhotoDetectionMethod.ContentIdentifier, PairingMode.Vivo => LivePhotoDetectionMethod.VivoLivePhoto, _ => LivePhotoDetectionMethod.FilenamePairing },
                            PairingKind = options.Mode == PairingMode.Name ? PairingKind.FilenameCompositionRequest : PairingKind.IdentityCandidate,
                            PairingIdentifier = options.Mode == PairingMode.Name ? null : key,
                            VideoByteOffset = 0, VideoByteLength = 0, VideoContainer = VideoContainer.Unknown,
                            SelectionIntent = options.Mode switch { PairingMode.Cid => SelectionIntent.AppleIdentity, PairingMode.Vivo => SelectionIntent.VivoIdentity, _ => SelectionIntent.UserComposition },
                            SelectionScope = options.Mode == PairingMode.Name ? null : options,
                            IsScopeComplete = !partial
                        });
                    }
                    if (candidateBatch != null && pairs.Count > published && (pairs.Count - published >= 128 || Environment.TickCount64 - lastBatch >= 100))
                    {
                        await candidateBatch(pairs.Skip(published).ToArray(), cancellationToken).ConfigureAwait(false);
                        published = pairs.Count;lastBatch = Environment.TickCount64;
                    }
                    progress?.Report(new(groups.Count, ++completed, pairs.Count, Stage: ScanWorkStage.Pairing));
                }
                progress?.Report(new(pairs.Count, published, Stage: ScanWorkStage.PreparingResults));
                if (candidateBatch != null && pairs.Count > published)
                    await candidateBatch(pairs.Skip(published).ToArray(), cancellationToken).ConfigureAwait(false);
                progress?.Report(new(pairs.Count, pairs.Count, Stage: ScanWorkStage.PreparingResults));
                if (cancellationToken.IsCancellationRequested) return Cancelled();
                if (rootFailed && images.Count + videos.Count == 0) status = ScanOperationStatus.Failed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = ScanOperationStatus.Cancelled; partial = true; }
            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
            {
                diagnostics.Add(LivePhotoScanSupport.Failure(options.InputDirectory ?? "", ex, "ScanOperationFailed"));
                status = ScanOperationStatus.Failed; partial = true;
            }
            // Enumerated but unexamined files are not "unmatched" on cancel.
            if (status == ScanOperationStatus.Cancelled)
                return new() { Pairs = pairs, StandaloneImagesCount = 0, StandaloneVideosCount = 0,
                    Diagnostics = diagnostics, Status = status, IsPartial = true };
            var used = pairs.SelectMany(p => new[] { p.ImagePath, p.VideoPath }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var blocked = diagnostics.Where(d => d.Severity == ScanDiagnosticSeverity.Error)
                .SelectMany(d => d.RelatedPaths.Prepend(d.PrimaryPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var standaloneImages = images.Where(p => !used.Contains(p) && !blocked.Contains(p)).ToArray();
            var standaloneVideos = videos.Where(p => !used.Contains(p) && !blocked.Contains(p)).ToArray();
            foreach (var path in standaloneImages) diagnostics.Add(LivePhotoScanSupport.Info(path,"UnmatchedImage","No unique selected companion in this scope."));
            foreach (var path in standaloneVideos) diagnostics.Add(LivePhotoScanSupport.Info(path,"UnmatchedVideo","No unique selected companion in this scope."));
            return new() { Pairs = pairs, StandaloneImagePaths = standaloneImages, StandaloneVideoPaths = standaloneVideos,
                StandaloneImagesCount = standaloneImages.Length, StandaloneVideosCount = standaloneVideos.Length,
                Diagnostics = diagnostics, Status = status, IsPartial = partial };
        }
        private static ScanDiagnostic Conflict(string path, IEnumerable<string> related, string code, string? key = null) => new()
        {
            DiagnosticCode = code, Severity = ScanDiagnosticSeverity.Error, Stage = ScanDiagnosticStage.Pairing,
            PrimaryPath = path, RelatedPaths = related.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            GroupKey = key, MessageKey = "MergeDiagnostic_" + code,
            Summary = "Multiple members share this selection key; no pair was selected.",
            RawCategory = SourceInspectionFailureCategory.Ambiguous, RawStage = SourceInspectionStage.Pairing
        };
        private static bool Confirmed(SourceMediaFacts facts, Candidate video, SourceProtocol protocol) =>
            facts.Protocol == protocol && facts.PrimaryImage.IsPresent && facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };
        private static bool IsUnbound(SourceMediaFacts? facts) => facts is { Protocol: SourceProtocol.NonLive } && string.IsNullOrEmpty(facts.PairingIdentifier);
        private static LivePhotoFilePairInfo Pair(Candidate image, Candidate video, SourceMediaFacts facts) => new()
        {
            BaseName = Path.GetFileNameWithoutExtension(image.Path), ImagePath = image.Path, VideoPath = video.Path,
            ImageSizeBytes = image.Size, VideoSizeBytes = video.Size, Protocol = MapProtocol(facts.Protocol),
            DetectionMethod = facts.Protocol == SourceProtocol.AppleLivePhoto ? LivePhotoDetectionMethod.ContentIdentifier : LivePhotoDetectionMethod.VivoLivePhoto, VideoByteOffset = facts.MotionVideo!.ByteOffset,
            VideoByteLength = facts.MotionVideo.ByteLength, VideoContainer = facts.MotionVideo.Container,
            PairingIdentifier = facts.PairingIdentifier
        };

        // Strict compatibility entry points are not the product's result-oriented scan API.
        public static LivePhotoScanResult Scan(string inputDirectory, CancellationToken cancellationToken = default,
            IProgress<WorkProgressSnapshot>? progress = null)
        {
            bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
            var files = Directory.EnumerateFiles(inputDirectory, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList();
            var images = files.Where(LivePhotoScanSupport.IsImage).ToList();
            var videos = files.Where(LivePhotoScanSupport.IsVideo).ToList();
            var matches = MatchMetadataPairsAsync(images, videos, cancellationToken).GetAwaiter().GetResult();
            var pairs = new List<LivePhotoFilePairInfo>();
            var inspector = new SourceInspector();
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var facts = inspector.InspectAsync(match.ImagePath, match.VideoPath, cancellationToken).GetAwaiter().GetResult();
                var image = new Candidate(match.ImagePath, new FileInfo(match.ImagePath).Length, null);
                var video = new Candidate(match.VideoPath, new FileInfo(match.VideoPath).Length, null);
                if (!Confirmed(facts, video, SourceProtocol.AppleLivePhoto) && !Confirmed(facts, video, SourceProtocol.VivoLegacyDualFile))
                    throw new InvalidDataException("Native facts did not confirm a supported dual-file source.");
                pairs.Add(Pair(image, video, facts));
            }
            var used = pairs.SelectMany(p => new[] { p.ImagePath, p.VideoPath }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var standaloneImages = images.Where(p => !used.Contains(p)).ToList();
            var standaloneVideos = videos.Where(p => !used.Contains(p)).ToList();
            progress?.Report(new WorkProgressSnapshot(files.Count, files.Count, pairs.Count));
            return new LivePhotoScanResult
            {
                Pairs = pairs, StandaloneImagePaths = standaloneImages, StandaloneVideoPaths = standaloneVideos,
                StandaloneImagesCount = standaloneImages.Count, StandaloneVideosCount = standaloneVideos.Count
            };
        }
        internal static async Task<IReadOnlyList<MetadataPair>> MatchMetadataPairsAsync(
            IReadOnlyList<string> imagePaths, IReadOnlyList<string> videoPaths, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vivo = LivePhotoMetadataMatcher.MatchVivo(imagePaths, videoPaths);
            cancellationToken.ThrowIfCancellationRequested();
            var matchedImages = new HashSet<string>(vivo.Pairs.Select(pair => pair.ImagePath), StringComparer.OrdinalIgnoreCase);
            var matchedVideos = new HashSet<string>(vivo.Pairs.Select(pair => pair.VideoPath), StringComparer.OrdinalIgnoreCase);
            var apple = await LivePhotoMetadataMatcher.MatchAsync(
                imagePaths.Where(path => !matchedImages.Contains(path)).ToList(),
                videoPaths.Where(path => !matchedVideos.Contains(path)).ToList(), cancellationToken).ConfigureAwait(false);
            return vivo.Pairs.Concat(apple.Pairs).ToList();
        }
        internal static async Task<LivePhotoFilePairInfo?> PrepareExplicitMediaSelectionAsync(
            string imagePath, string videoPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            return new LivePhotoFilePairInfo
            {
                BaseName = Path.GetFileNameWithoutExtension(imagePath), ImagePath = imagePath, VideoPath = videoPath,
                ImageSizeBytes = new FileInfo(imagePath).Length, VideoSizeBytes = new FileInfo(videoPath).Length,
                Protocol = LivePhotoProtocolType.Unknown, DetectionMethod = LivePhotoDetectionMethod.ExplicitMediaSelection,
                VideoByteOffset = 0, VideoByteLength = new FileInfo(videoPath).Length, VideoContainer = VideoContainer.Unknown,
                SelectionIntent = SelectionIntent.UserComposition, PairingKind = PairingKind.ExplicitMediaSelection
            };
        }
        private static LivePhotoProtocolType MapProtocol(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
            SourceProtocol.VivoLegacyDualFile or SourceProtocol.VivoLivePhoto => LivePhotoProtocolType.Vivo,
            _ => LivePhotoProtocolType.Unknown
        };
    }
}
