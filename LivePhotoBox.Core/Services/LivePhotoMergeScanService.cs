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



        public bool Recursive { get; init; }



    }







    public static class LivePhotoMergeScanService



    {



        private sealed record Candidate(string Path, long Size, SourceMediaFacts? Facts, SourceInspectionFailure? PendingBinding = null);







        public static async Task<LivePhotoScanResult> ScanAsync(MergeScanOptions options,



            CancellationToken cancellationToken = default, IProgress<WorkProgressSnapshot>? progress = null)



        {



            ArgumentNullException.ThrowIfNull(options);



            if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options.Mode));



            var diagnostics = new List<ScanDiagnostic>();



            try



            {



                var (images, videos, partial, rootFailed) = LivePhotoScanSupport.Collect(options, diagnostics, cancellationToken);



                int total = images.Count + videos.Count, completed = 0;



                progress?.Report(new WorkProgressSnapshot(total, 0));



                var inspector = new SourceInspector();



                var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);



                foreach (string path in images.Concat(videos))



                {



                    cancellationToken.ThrowIfCancellationRequested();



                    try



                    {



                        long size = new FileInfo(path).Length;



                        var inspection = await inspector.InspectForDiscoveryAsync(path, null, cancellationToken).ConfigureAwait(false);



                        if (inspection.Failure is { } failure)
                        {
                            if (IsPendingVivoBinding(failure) && LivePhotoScanSupport.IsImage(path) && options.Mode != PairingMode.Cid)
                                candidates.Add(path, new Candidate(path, size, null, failure));
                            else
                            {
                                diagnostics.Add(LivePhotoScanSupport.Failure(path, failure));
                            }
                        }
                        else candidates.Add(path, new Candidate(path, size, inspection.Facts!));
                    }
                    catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))
                    {
                        diagnostics.Add(LivePhotoScanSupport.Failure(path, ex));
                    }



                    progress?.Report(new WorkProgressSnapshot(total, ++completed));



                }







                var pairs = new List<LivePhotoFilePairInfo>();



                switch (options.Mode)



                {



                    case PairingMode.Name:



                        await MatchNamesAsync(images, videos, candidates, pairs, diagnostics, inspector, cancellationToken).ConfigureAwait(false);



                        break;



                    case PairingMode.Cid:



                        await MatchIdsAsync(images, videos, candidates, pairs, diagnostics, inspector, cancellationToken).ConfigureAwait(false);



                        break;



                    case PairingMode.Vivo:



                        await MatchVivoCandidatesAsync(images, videos, candidates, pairs, diagnostics, inspector, cancellationToken).ConfigureAwait(false);



                        break;



                }



                cancellationToken.ThrowIfCancellationRequested();



                if (pairs.Count == 0 && diagnostics.Any(d => d.DiagnosticCode == "ScanOperationFailed"))
                    return Incomplete(ScanOperationStatus.Failed, diagnostics);



                var used = pairs.SelectMany(p => new[] { p.ImagePath, p.VideoPath }).ToHashSet(StringComparer.OrdinalIgnoreCase);



                foreach (var candidate in candidates.Values.Where(c => c.PendingBinding != null && !used.Contains(c.Path)))



                {



                    if (!HasDiagnostic(diagnostics, candidate.Path))



                        diagnostics.Add(LivePhotoScanSupport.Failure(candidate.Path, candidate.PendingBinding!, "UnresolvedBinding"));



                }



                foreach (string path in images.Concat(videos).Where(p => !used.Contains(p)))



                {



                    if (!HasDiagnostic(diagnostics, path))



                        diagnostics.Add(LivePhotoScanSupport.Info(path,



                            LivePhotoScanSupport.IsImage(path) ? "UnmatchedImage" : "UnmatchedVideo",



                            "No unique companion was confirmed in the selected pairing mode."));



                }



                // Rejected/conflicted inputs are diagnostics, not standalone conversion candidates.



                var rejected = diagnostics.Where(d => d.Severity == ScanDiagnosticSeverity.Error)



                    .SelectMany(d => d.RelatedPaths.Prepend(d.PrimaryPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);



                var standaloneImages = images.Where(p => !used.Contains(p) && !rejected.Contains(p)).ToList();



                var standaloneVideos = videos.Where(p => !used.Contains(p) && !rejected.Contains(p)).ToList();



                progress?.Report(new WorkProgressSnapshot(total, total, pairs.Count));



                return new LivePhotoScanResult



                {



                    Pairs = pairs.OrderBy(p => p.ImagePath, StringComparer.OrdinalIgnoreCase).ToList(),



                    StandaloneImagePaths = standaloneImages, StandaloneVideoPaths = standaloneVideos,



                    StandaloneImagesCount = standaloneImages.Count, StandaloneVideosCount = standaloneVideos.Count,



                    Diagnostics = diagnostics, IsPartial = partial,



                    Status = rootFailed && total == 0 ? ScanOperationStatus.Failed : ScanOperationStatus.Completed



                };



            }



            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)



            {



                return Incomplete(ScanOperationStatus.Cancelled, diagnostics);



            }



            catch (Exception ex) when (LivePhotoScanSupport.CanReportOperationFailure(ex))



            {



                diagnostics.Add(LivePhotoScanSupport.Failure(options.InputDirectory ?? options.InputDirectories?.FirstOrDefault() ??



                    options.ExplicitFiles?.FirstOrDefault() ?? string.Empty, ex, "ScanOperationFailed", stage: ScanDiagnosticStage.Capability));



                return Incomplete(ScanOperationStatus.Failed, diagnostics, ex.Message);



            }



        }







        private static LivePhotoScanResult Incomplete(ScanOperationStatus status, List<ScanDiagnostic> diagnostics, string? error = null) => new()



        {



            Pairs = Array.Empty<LivePhotoFilePairInfo>(), StandaloneImagesCount = 0, StandaloneVideosCount = 0,



            Diagnostics = diagnostics, Status = status, IsPartial = true, ErrorMessage = error



        };







        private static bool IsPendingVivoBinding(SourceInspectionFailure error) =>



            error.Category == SourceInspectionFailureCategory.Ambiguous && error.Stage == SourceInspectionStage.Pairing &&



            error.Capability == NativeRuntime.VivoLegacyCapability;







        private static bool HasDiagnostic(IEnumerable<ScanDiagnostic> diagnostics, string path) => diagnostics.Any(d =>



            string.Equals(d.PrimaryPath, path, StringComparison.OrdinalIgnoreCase) || d.RelatedPaths.Contains(path, StringComparer.OrdinalIgnoreCase));







        private static bool IsEmbeddedLive(Candidate c) => c.Facts is { Protocol: not SourceProtocol.NonLive and not SourceProtocol.Unknown,



            MotionVideo: { IsPresent: true, SourceIndex: 0 } };







        private static bool IsUnbound(SourceMediaFacts? facts) => facts is { Protocol: SourceProtocol.NonLive } &&



            string.IsNullOrEmpty(facts.PairingIdentifier) && facts.ProtocolTailLength == 0 && facts.ConfirmedResidues.Count == 0;







        private static ScanDiagnostic Conflict(string path, IEnumerable<string> related, string code, string? key = null) => new()



        {



            DiagnosticCode = code, Severity = ScanDiagnosticSeverity.Error, Stage = ScanDiagnosticStage.Pairing,



            PrimaryPath = path, RelatedPaths = related.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),



            GroupKey = key, MessageKey = "MergeDiagnostic_" + code,



            Summary = code == "ConflictMultipleMedia" ? "Multiple images or videos have the same directory and basename." :



                "The source relationship is not unique or cannot be confirmed safely.",



            RawCategory = SourceInspectionFailureCategory.Ambiguous, RawStage = SourceInspectionStage.Pairing



        };







        private static bool Confirmed(SourceMediaFacts facts, Candidate video, SourceProtocol protocol) =>



            facts.Protocol == protocol && facts.PrimaryImage.IsPresent &&



            facts.MotionVideo is { IsPresent: true, SourceIndex: 1, ByteOffset: >= 0, ByteLength: > 0 } motion &&



            motion.ByteOffset <= video.Size && motion.ByteLength <= video.Size - motion.ByteOffset;







        private static LivePhotoFilePairInfo Pair(Candidate image, Candidate video, SourceMediaFacts facts) => new()



        {



            BaseName = Path.GetFileNameWithoutExtension(image.Path), ImagePath = image.Path, VideoPath = video.Path,



            ImageSizeBytes = image.Size, VideoSizeBytes = video.Size, Protocol = MapProtocol(facts.Protocol),



            DetectionMethod = facts.Protocol == SourceProtocol.AppleLivePhoto ? LivePhotoDetectionMethod.ContentIdentifier : LivePhotoDetectionMethod.VivoLivePhoto,



            VideoByteOffset = facts.MotionVideo!.ByteOffset, VideoByteLength = facts.MotionVideo.ByteLength,



            VideoContainer = facts.MotionVideo.Container, PairingIdentifier = facts.PairingIdentifier,



            PairingKind = PairingKind.NativeConfirmedSourcePair



        };







private static async Task MatchNamesAsync(List<string> images, List<string> videos, Dictionary<string, Candidate> candidates,



            List<LivePhotoFilePairInfo> pairs, List<ScanDiagnostic> diagnostics, SourceInspector inspector, CancellationToken ct)



        {



            var imagePaths = images.ToHashSet(StringComparer.OrdinalIgnoreCase);



            foreach (var group in images.Concat(videos).GroupBy(p => Path.Combine(Path.GetDirectoryName(p)!, Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase))



            {



                ct.ThrowIfCancellationRequested();



                var imgs = group.Where(imagePaths.Contains).ToList();



                var vids = group.Where(p => !imagePaths.Contains(p)).ToList();



                if (imgs.Count == 0 || vids.Count == 0) continue;







                // 智能配对：即使同名但不同后缀名（如 photo.jpg+photo.mp4 与 photo.heic+photo.mov），各自匹配成对，不报错



                var matched = new List<(string ImagePath, string VideoPath)>();



                var remainingImgs = new List<string>(imgs);



                var remainingVids = new List<string>(vids);







                // 阶段 1：天然扩展名搭档最佳匹配（HEIC+MOV, JPG+MP4）



                for (int i = remainingImgs.Count - 1; i >= 0; i--)



                {



                    string img = remainingImgs[i];



                    string imgExt = Path.GetExtension(img).ToLowerInvariant();



                    string preferredVidExt = (imgExt is ".heic" or ".heif") ? ".mov" : ".mp4";







                    int vidIdx = remainingVids.FindIndex(v => Path.GetExtension(v).Equals(preferredVidExt, StringComparison.OrdinalIgnoreCase));



                    if (vidIdx >= 0)



                    {



                        matched.Add((img, remainingVids[vidIdx]));



                        remainingImgs.RemoveAt(i);



                        remainingVids.RemoveAt(vidIdx);



                    }



                }







                // 阶段 2：剩余文件两两配对（如 HEIC+MP4, JPG+MOV 等跨格式搭档）



                while (remainingImgs.Count > 0 && remainingVids.Count > 0)



                {



                    matched.Add((remainingImgs[0], remainingVids[0]));



                    remainingImgs.RemoveAt(0);



                    remainingVids.RemoveAt(0);



                }







                foreach (var (imgPath, vidPath) in matched)



                {



                    if (!candidates.TryGetValue(imgPath, out var image) || !candidates.TryGetValue(vidPath, out var video)) continue;



                    try



                    {



                        if (IsEmbeddedLive(image))



                        {



                            diagnostics.Add(Conflict(image.Path, [video.Path], "LivePhotoCannotBeCompanion", group.Key));



                            continue;



                        }



                        var inspection = await inspector.InspectForDiscoveryAsync(image.Path, video.Path, ct).ConfigureAwait(false);



                        if (inspection.Failure is { } failure)
                        {
                            diagnostics.Add(LivePhotoScanSupport.Failure(image.Path, failure, "PairInspectionFailed", [video.Path]));
                            continue;
                        }



                        var facts = inspection.Facts!;



                        if (Confirmed(facts, video, SourceProtocol.VivoLegacyDualFile) ||



                            (image.Facts != null && Confirmed(facts, video, SourceProtocol.AppleLivePhoto)))



                        {



                            pairs.Add(Pair(image, video, facts));



                        }



                        else if (IsUnbound(image.Facts) && image.Facts!.PrimaryImage.IsPresent && IsUnbound(video.Facts) &&



                            facts.Protocol == SourceProtocol.NonLive)



                        {



                            var probe = await NativeMediaService.ProbeVideoAsync(video.Path, ct).ConfigureAwait(false);



                            if (!probe.IsPresent || probe.Container == VideoContainer.Unknown || probe.Codec == VideoCodec.Unknown)



                            {



                                diagnostics.Add(LivePhotoScanSupport.Failure(video.Path,



                                    new SourceInspectionFailure(SourceInspectionFailureCategory.Malformed,



                                        SourceInspectionStage.Container, 0, "A valid companion video is required."), "PairInspectionFailed", [image.Path]));



                                continue;



                            }



                            pairs.Add(new LivePhotoFilePairInfo



                            {



                                BaseName = Path.GetFileNameWithoutExtension(image.Path), ImagePath = image.Path, VideoPath = video.Path,



                                ImageSizeBytes = image.Size, VideoSizeBytes = video.Size, Protocol = LivePhotoProtocolType.Unknown,



                                DetectionMethod = LivePhotoDetectionMethod.FilenamePairing, VideoByteOffset = 0,



                                VideoByteLength = video.Size, VideoContainer = probe.Container, PairingKind = PairingKind.FilenameCompositionRequest



                            });



                        }



                        else diagnostics.Add(Conflict(image.Path, [video.Path], "AmbiguousLiveBinding", group.Key));



                    }



                    catch (Exception ex) when (LivePhotoScanSupport.IsInputFailure(ex))



                    {



                        diagnostics.Add(LivePhotoScanSupport.Failure(image.Path, ex, "PairInspectionFailed", [video.Path]));



                    }



                }



            }



        }







        private static async Task MatchIdsAsync(List<string> images, List<string> videos, Dictionary<string, Candidate> candidates,



            List<LivePhotoFilePairInfo> pairs, List<ScanDiagnostic> diagnostics, SourceInspector inspector, CancellationToken ct)



        {



            // An embedded live image or a non-Apple owner is not an Apple identity candidate.



            bool Eligible(string path) => candidates.TryGetValue(path, out var c) && !IsEmbeddedLive(c) &&



                c.Facts is { Protocol: SourceProtocol.NonLive or SourceProtocol.AppleLivePhoto, PairingIdentifier: { Length: > 0 } };



            var imageGroups = images.Where(Eligible).GroupBy(p => candidates[p].Facts!.PairingIdentifier!, StringComparer.Ordinal)



                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);



            var videoGroups = videos.Where(Eligible).GroupBy(p => candidates[p].Facts!.PairingIdentifier!, StringComparer.Ordinal)



                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);



            foreach (string id in imageGroups.Keys.Union(videoGroups.Keys, StringComparer.Ordinal))



            {



                ct.ThrowIfCancellationRequested();



                var imgs = imageGroups.GetValueOrDefault(id) ?? new List<string>();



                var vids = videoGroups.GetValueOrDefault(id) ?? new List<string>();







                // 优先在同目录下配对（消除子文件夹备份文件的歧义）



                var matched = new List<(string Image, string Video)>();



                var remainingImgs = new List<string>(imgs);



                var remainingVids = new List<string>(vids);







                for (int i = remainingImgs.Count - 1; i >= 0; i--)



                {



                    string img = remainingImgs[i];



                    string? imgDir = Path.GetDirectoryName(img);



                    int vidIdx = remainingVids.FindIndex(v => string.Equals(Path.GetDirectoryName(v), imgDir, StringComparison.OrdinalIgnoreCase));



                    if (vidIdx >= 0)



                    {



                        matched.Add((img, remainingVids[vidIdx]));



                        remainingImgs.RemoveAt(i);



                        remainingVids.RemoveAt(vidIdx);



                    }



                }







                // 跨目录唯一成对时配对



                if (remainingImgs.Count == 1 && remainingVids.Count == 1)



                {



                    matched.Add((remainingImgs[0], remainingVids[0]));



                    remainingImgs.Clear();



                    remainingVids.Clear();



                }







                if (remainingImgs.Count > 1 || remainingVids.Count > 1)



                {



                    var conflicted = remainingImgs.Concat(remainingVids).ToList();



                    if (conflicted.Count > 0)



                        diagnostics.Add(Conflict(conflicted.First(), conflicted, "ConflictDuplicateIdentity", id));



                }







                foreach (var (imgPath, vidPath) in matched)



                {



                    var image = candidates[imgPath];



                    var video = candidates[vidPath];



                    try



                    {



                        var inspection = await inspector.InspectForDiscoveryAsync(image.Path, video.Path, ct).ConfigureAwait(false);



                        if (inspection.Failure is { } failure)
                        {
                            diagnostics.Add(LivePhotoScanSupport.Failure(image.Path, failure, "PairInspectionFailed", [video.Path]));
                            continue;
                        }



                        var facts = inspection.Facts!;



                        if (Confirmed(facts, video, SourceProtocol.AppleLivePhoto)) pairs.Add(Pair(image, video, facts));



                        else diagnostics.Add(Conflict(image.Path, [video.Path], "UnconfirmedProtocolPair", id));



                    }



                    catch (Exception ex) when (LivePhotoScanSupport.IsInputFailure(ex))



                    {



                        diagnostics.Add(LivePhotoScanSupport.Failure(image.Path, ex, "PairInspectionFailed", [video.Path]));



                    }



                }



            }



        }







        private static async Task MatchVivoCandidatesAsync(List<string> images, List<string> videos, Dictionary<string, Candidate> candidates,



            List<LivePhotoFilePairInfo> pairs, List<ScanDiagnostic> diagnostics, SourceInspector inspector, CancellationToken ct)



        {



            var edges = new List<LivePhotoFilePairInfo>();



            var rejected = new Dictionary<string, ScanDiagnostic>(StringComparer.OrdinalIgnoreCase);



            foreach (string imagePath in images.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".jpg" or ".jpeg"))



            {



                if (!candidates.TryGetValue(imagePath, out var image) || IsEmbeddedLive(image)) continue;



                foreach (string videoPath in videos)



                {



                    ct.ThrowIfCancellationRequested();



                    if (!candidates.TryGetValue(videoPath, out var video)) continue;



                    try



                    {



                        var inspection = await inspector.InspectForDiscoveryAsync(image.Path, video.Path, ct).ConfigureAwait(false);



                        if (inspection.Failure is { } failure)



                        {



                            // Non-pairing refusal is noted as an unconfirmed edge without aborting the whole matrix
                            if (failure.Stage != SourceInspectionStage.Pairing)
                                rejected.TryAdd(imagePath, LivePhotoScanSupport.Failure(imagePath, failure, "PairInspectionFailed", [videoPath]));
                            continue;



                        }



                        var facts = inspection.Facts!;



                        if (Confirmed(facts, video, SourceProtocol.VivoLegacyDualFile)) edges.Add(Pair(image, video, facts));



                    }



                    catch (Exception ex) when (LivePhotoScanSupport.IsInputFailure(ex))



                    {



                        LogService.Scan($"Vivo candidate rejected: '{imagePath}' + '{videoPath}': {ex}", LogLevel.Debug);



                        // Pairing refusal identifies an unconfirmed edge, not a broken endpoint.



                        // A pending image's own solo refusal is reported after the complete matrix.



                        if (ex is not SourceInspectionException { Stage: SourceInspectionStage.Pairing })



                            rejected.TryAdd(imagePath, LivePhotoScanSupport.Failure(imagePath, ex, "PairInspectionFailed", [videoPath]));



                    }



                }



            }







            var conflicted = edges.GroupBy(p => p.ImagePath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)



                .Concat(edges.GroupBy(p => p.VideoPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))



                .ToHashSet(StringComparer.OrdinalIgnoreCase);



            // Include every path in a conflicting component, not just its high-degree vertex.



            bool changed;



            do



            {



                changed = false;



                foreach (var edge in edges.Where(e => conflicted.Contains(e.ImagePath) || conflicted.Contains(e.VideoPath)))



                {



                    changed |= conflicted.Add(edge.ImagePath);



                    changed |= conflicted.Add(edge.VideoPath);



                }



            } while (changed);



            if (conflicted.Count > 0)



                diagnostics.Add(Conflict(conflicted.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).First(), conflicted, "ConflictDuplicateIdentity"));



            pairs.AddRange(edges.Where(e => !conflicted.Contains(e.ImagePath) && !conflicted.Contains(e.VideoPath)));



            var confirmed = edges.Select(e => e.ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);



            foreach (var (path, diagnostic) in rejected)



            {



                if (!confirmed.Contains(path) && !HasDiagnostic(diagnostics, path)) diagnostics.Add(diagnostic);



            }



        }







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



            var inspector = new SourceInspector();



            var image = await inspector.InspectAsync(imagePath, null, cancellationToken).ConfigureAwait(false);



            var videoSource = await inspector.InspectAsync(videoPath, null, cancellationToken).ConfigureAwait(false);



            if (!image.PrimaryImage.IsPresent || !IsUnbound(image) || !IsUnbound(videoSource)) return null;



            var facts = await inspector.InspectAsync(imagePath, videoPath, cancellationToken).ConfigureAwait(false);



            if (facts.Protocol != SourceProtocol.NonLive) return null;



            VideoFacts video = await NativeMediaService.ProbeVideoAsync(videoPath, cancellationToken).ConfigureAwait(false);



            if (!video.IsPresent || video.Container == VideoContainer.Unknown || video.Codec == VideoCodec.Unknown)



                throw new InvalidDataException("Explicit media selection requires a valid companion video.");



            return new LivePhotoFilePairInfo



            {



                BaseName = Path.GetFileNameWithoutExtension(imagePath), ImagePath = imagePath, VideoPath = videoPath,



                ImageSizeBytes = new FileInfo(imagePath).Length, VideoSizeBytes = new FileInfo(videoPath).Length,



                Protocol = LivePhotoProtocolType.Unknown, DetectionMethod = LivePhotoDetectionMethod.ExplicitMediaSelection,



                VideoByteOffset = 0, VideoByteLength = new FileInfo(videoPath).Length, VideoContainer = video.Container,



                PairingKind = PairingKind.ExplicitMediaSelection



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
