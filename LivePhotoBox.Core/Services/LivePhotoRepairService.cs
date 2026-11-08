using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 实况照片修复服务。
    /// 文件诊断与执行使用 Native，统一按新文件发布。
    /// </summary>
    public static partial class LivePhotoRepairService
    {
        internal sealed record RepairResult(string Status, string Message, string? Image = null, string? Video = null)
        {
            internal RepairLimitation[] Limitations { get; init; } = [];
            internal string? IsolationDirectory { get; init; }
            internal bool? HistoryRecorded { get; init; }
            internal RepairActionResult[] Actions { get; init; } = [];
            internal RepairPublishedOutput[] PublishedOutputs => CreatePublishedOutputs(this);
            internal string Completion => Status == "warning" ? PublishedOutputs.Length == 0 ? "not_completed" : "partial_or_unverified"
                : Status == "success" ? "completed" : Status == "healthy" ? "not_needed" : Status;
            internal string OutcomeKey => Status == "warning" ? PublishedOutputs.Length == 0 ? "warning_no_output"
                : PublishedOutputs.All(x => x.Kind == "isolated_copy") ? "warning_copy"
                : PublishedOutputs.All(x => x.Kind is "isolated_copy" or "isolated_repaired") ? "warning_isolated"
                : "warning_retained" : Status;
            // Published members can have different actual actions even though
            // publication and isolation are decided for the whole pair.
            internal string? ImageStatus { get; init; }
            internal string? VideoStatus { get; init; }
            internal ProcessStatus ProcessStatus => Status switch
            {
                "success" or "healthy" => Models.ProcessStatus.Success,
                "warning" => Models.ProcessStatus.Warning,
                "failed" => Models.ProcessStatus.Failed,
                _ => Models.ProcessStatus.Pending
            };
        }

        private static string Text(string key, string fallback, params object[] values)
        {
            string format = ResourceService.GetString(key);
            if (format == key) format = fallback;
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, values);
        }

        private static string GetIsolationDirectory(string baseDirectory)
        {
            string folderName = ResourceService.GetString("OutputDir_Quarantined");
            if (string.IsNullOrWhiteSpace(folderName)) folderName = "Quarantine";
            return Path.Combine(baseDirectory, folderName);
        }

        // Native has recognized a legacy vivo image, but single-file inspection
        // cannot confirm its companion. This is enough to exclude it from the
        // default Apple/Samsung repair scope, not authority to repair or pair it.
        private static bool IsLegacyVivoPairingRequirement(SourceInspectionFailure? failure)
            => failure is { Category: SourceInspectionFailureCategory.Ambiguous,
                Stage: SourceInspectionStage.Pairing, Capability: NativeRuntime.VivoLegacyCapability };

        private static async Task<(SourceInspectionResult Inspection, string? Companion)> InspectRepairSourceAsync(
            string path, bool defaultDevicesOnly, CancellationToken token, RepairBatchContext batch)
        {
            var inspection = await batch.InspectAsync(path, null, token);
            if (defaultDevicesOnly || !IsLegacyVivoPairingRequirement(inspection.Failure))
                return (inspection, null);

            SourceInspectionResult? matched = null;
            string? companion = null;
            var candidateAttempt = await batch.TryCandidatesAsync(path, token, SourceProtocol.VivoLegacyDualFile);
            if (candidateAttempt.Failure is { } candidateFailure)
                return (SourceInspectionResult.Rejected(new(SourceInspectionFailureCategory.Ambiguous,
                    SourceInspectionStage.Pairing, NativeRuntime.VivoLegacyCapability, candidateFailure.Message)), null);
            var candidates = candidateAttempt.Candidates;
            int images = 0;
            foreach (var candidate in candidates)
                if (Path.GetExtension(candidate.Path).ToLowerInvariant() is ".jpg" or ".jpeg") images++;
            if (images > 1)
                return (SourceInspectionResult.Rejected(new(SourceInspectionFailureCategory.Ambiguous,
                    SourceInspectionStage.Pairing, NativeRuntime.VivoLegacyCapability,
                    "More than one image has this confirmed legacy pairing identifier; no companion was selected.")), null);
            foreach (var candidateEntry in candidates)
            {
                string candidate = candidateEntry.Path;
                if (token.IsCancellationRequested) return (inspection, null);
                if (Path.GetExtension(candidate).ToLowerInvariant() is not (".mp4" or ".mov")) continue;
                var pair = await batch.InspectAsync(path, candidate, token);
                if (pair.Facts?.Protocol != SourceProtocol.VivoLegacyDualFile) continue;
                if (matched != null)
                    return (SourceInspectionResult.Rejected(new(SourceInspectionFailureCategory.Ambiguous,
                        SourceInspectionStage.Pairing, NativeRuntime.VivoLegacyCapability,
                        "More than one video has this image's confirmed pairing identifier; no companion was selected.")), null);
                matched = pair;
                companion = candidate;
            }
            return matched == null ? (inspection, null) : (matched, companion);
        }

        private static async Task<RepairResult> RepairImageFileCoreAsync(
            string sourcePath, string outputDirectory, bool allDevices, bool dryRun, RepairOptions options, CancellationToken token, bool copyPerfect = false, RepairBatchContext? batch = null)
        {
            using var localBatch = batch == null ? new RepairBatchContext() : null;
            batch ??= localBatch!;
            sourcePath = Path.GetFullPath(sourcePath);
            if (token.IsCancellationRequested) return new("cancelled", "用户取消，本项未完成发布。");
            using var sourceProtection = new NeutralArtifactReadLeaseSet();
            if (sourceProtection.TryPin(new MediaArtifact { Path = sourcePath, Kind = MediaArtifactKind.PrimaryImage }) is { } protectionFailure)
                return new("failed", protectionFailure.Message);
            using var sourceRead = SourceReadAttempt.Open(sourcePath);
            if (!sourceRead.Succeeded) return new("failed", $"Cannot read source (system error {sourceRead.Error}).");
            using var sourceLease = new FileStream(sourceRead.Handle, FileAccess.Read);
            var (inspection, confirmedCompanion) = await InspectRepairSourceAsync(sourcePath, !allDevices, token, batch);
            if (token.IsCancellationRequested) return new("cancelled", "用户取消，本项未完成发布。");
            if (inspection.Facts is not { } source)
            {
                if (!allDevices && IsLegacyVivoPairingRequirement(inspection.Failure))
                    return new("skipped", "Legacy vivo image is outside the default Apple/Samsung repair scope.");
                return new("failed", inspection.Failure?.Reason ?? "Cannot inspect input.");
            }
            if (source.Protocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic)
            {
                var samsung = await RepairSamsungAsync(sourcePath,outputDirectory,dryRun,options,token,copyPerfect);

                return samsung;
            }
            if (source.Protocol == SourceProtocol.HuaweiMovingPhoto)
            {
                if (!allDevices) return new("skipped", "Skipped by default Apple/Samsung repair scope.");
                var huawei = await RepairHuaweiAsync(sourcePath, outputDirectory, dryRun, options, token, copyPerfect);

                return huawei;
            }
            var container = source.PrimaryImage.Container;
            bool jpeg = container == ImageContainer.Jpeg;
            bool apple = source.Protocol == SourceProtocol.AppleLivePhoto ||
                (!string.IsNullOrEmpty(source.PairingIdentifier) &&
                 (await batch.ReadHintAsync(sourcePath, token))?.Protocol == SourceProtocol.AppleLivePhoto);
            bool legacyPair = source.Protocol == SourceProtocol.VivoLegacyDualFile;
            if (!apple && !allDevices)
                return new("skipped", "Default repair scope is Apple and Samsung Live Photos. Use --all-devices for supported standalone images.");
            if (container is not (ImageContainer.Jpeg or ImageContainer.Heic) ||
                (!jpeg && (source.MotionVideo?.IsPresent == true ||
                    source.Protocol is not (SourceProtocol.NonLive or SourceProtocol.AppleLivePhoto))))
                return new("failed", "The image contains a media structure this repair cannot safely change.");

            var beforeAttempt = await NativeMediaService.TryCapturePreservationObservationAsync(sourcePath, source.Protocol, container, token);
            if (beforeAttempt.Failure is { } observationFailure)
                return new(observationFailure is OperationCanceledException ? "cancelled" : "failed", observationFailure.Message);
            var before = beforeAttempt.Observation!;
            bool rotate = jpeg && options.FixImageRotation && before.Orientation > 1;
            bool composite = jpeg && source.PrimaryImage.ByteLength != sourceLease.Length;
            bool incompleteRotation = rotate && composite;
            if (incompleteRotation) rotate = false;
            if (before.CodestreamError)
                return new("failed", "Image codestream cannot be verified.");
            bool limitedExif = before.ExifParseError;
            if (limitedExif) { incompleteRotation |= rotate; rotate = false; }

            uint actions = (options.StripImageThumbnail ? 1u : 0u) |
                (!jpeg && options.FixHeicOrientation ? 2u : 0u);
            var metadataAttempt = actions == 0 ? new NativeMediaService.ImageExifRepairAttempt(0, null, null)
                : await NativeMediaService.TryRepairImageExifAsync(sourcePath, null, actions, token);
            if (metadataAttempt.Failure is OperationCanceledException) return new("cancelled", "用户取消，本项未完成发布。");
            var metadataProbe = new NativeMediaService.ImageExifRepairProbe(metadataAttempt.Failure is null,
                metadataAttempt.Changes, metadataAttempt.Failure?.Message);
            uint metadata = limitedExif ? 0 : metadataProbe.Changes;
            if (rotate && before.Orientation > 8) { incompleteRotation = true; rotate = false; }

            string? videoPath = confirmedCompanion;
            string? associationWarning = null;
            if (apple && (options.FixVideoRotation || copyPerfect))
            {
                try
                {
                    var candidateAttempt = await batch.TryCandidatesAsync(sourcePath, token, SourceProtocol.AppleLivePhoto, source.PairingIdentifier);
                    if (candidateAttempt.Failure is OperationCanceledException) return new("cancelled", "用户取消，本项未完成发布。");
                    if (candidateAttempt.Failure is { } candidateFailure)
                        associationWarning = "Video association incomplete; independent image actions only. " + candidateFailure.Message;
                    foreach (var candidateEntry in candidateAttempt.Candidates)
                    {
                        string candidate = candidateEntry.Path;
                        if (string.Equals(candidate,sourcePath,StringComparison.OrdinalIgnoreCase)) continue;
                        string candidateExtension = Path.GetExtension(candidate).ToLowerInvariant();
                        if (candidateExtension is not (".mov" or ".jpg" or ".jpeg" or ".heic" or ".heif")) continue;
                        if (!string.Equals(candidateEntry.Identifier,source.PairingIdentifier,StringComparison.OrdinalIgnoreCase)) continue;
                        if (candidateExtension != ".mov" || videoPath != null)
                        { videoPath=null;associationWarning="Pairing is ambiguous; no video selected. Independent image actions only.";break; }
                        videoPath=candidate;
                    }
                }
                catch (IOException ex) { videoPath=null;associationWarning="Video association incomplete; independent image actions only. "+ex.Message; }
            }

            using var videoRead = videoPath == null ? null : SourceReadAttempt.Open(videoPath);
            if (videoRead is { Succeeded: false })
            {
                associationWarning = $"配对视频暂不可读取（系统错误 {videoRead.Error}），仅继续独立图片操作。";
                videoPath = null;
            }
            using var videoLease = videoPath == null ? null : new FileStream(videoRead!.Handle, FileAccess.Read);
            var pairAttempt = videoPath == null ? null : await NativeMediaService.TryInspectMediaWithPlanAsync(sourcePath, videoPath, token);
            if (pairAttempt?.Failure is { } pairFailure) return PublicationRefused(pairFailure);
            using var pairSource = pairAttempt?.Source;
            var pair = pairSource?.Facts;
            if (pair != null && (pair.Protocol != (apple ? SourceProtocol.AppleLivePhoto : source.Protocol) ||
                !string.Equals(pair.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                (legacyPair && pair.SecondarySha256 != source.SecondarySha256)))
                return new("failed", "The image/video pair could not be confirmed.");

            VideoFacts? video = null;
            uint reflection = 0;
            string? videoWarning = associationWarning;
            if (videoPath != null && options.FixVideoRotation)
            {
                var videoProbe = await NativeMediaService.TryProbeVideoRepairAsync(videoPath, token);
                if (videoProbe.Failure is OperationCanceledException) return new("cancelled", "用户取消，本项未完成发布。");
                if (videoProbe.Failure is { } videoFailure) videoWarning = videoFailure.Message;
                else { video = videoProbe.Facts; reflection = videoProbe.Reflection; }
            }
            bool rotateVideo = reflection != 0 || video?.RotationDegrees is 90 or 180 or 270;
            bool incompletePairedVideo = legacyPair && rotateVideo;
            if (incompletePairedVideo) rotateVideo = false;
            bool copy = !rotate && metadata == 0 && !rotateVideo;
            bool uncheckedEmbeddedVideo = videoPath == null && options.FixVideoRotation && source.MotionVideo?.IsPresent == true;
            bool incomplete = incompleteRotation || incompletePairedVideo || uncheckedEmbeddedVideo || !metadataProbe.Supported || limitedExif || videoWarning != null ||
                (apple && videoPath == null && options.FixVideoRotation);
            if (copy && incomplete)
            {
                string? unchangedCopy = null;
                if (copyPerfect && !dryRun)
                {
                    var copyAttempt = await TryPublishSingleCopyAsync(sourcePath, GetIsolationDirectory(outputDirectory), sourceLease, token);
                    if (copyAttempt.Failure is { } copyFailure) return PublicationRefused(copyFailure);
                    unchangedCopy = copyAttempt.Path;

                }
                return new RepairResult(dryRun ? "planned" : "warning", "所选动作未完成，原内容保留。 " + metadataProbe.Diagnostic + " " + videoWarning, unchangedCopy)
                { Actions = ActionResults(options, 0, (incompleteRotation ? 1u : 0u) | (!metadataProbe.Supported || limitedExif ? 6u : 0u) |
                    (incompletePairedVideo || uncheckedEmbeddedVideo || videoWarning != null || (apple && videoPath == null) ? 8u : 0u), dryRun),
                    Limitations = PairLimitations(apple && videoPath == null, false, false) };
            }
            if (copy && !incomplete && (!copyPerfect || (apple && videoPath == null)))
                return new("healthy", "Selected checks completed; no selected repair is needed.");

            var changes = new System.Collections.Generic.List<string>(4);
            if (rotate) changes.Add(Text("Repair_Action_ImageRotation", "Apply EXIF orientation {0} losslessly and reset it to 1.", before.Orientation));
            if ((metadata & 1) != 0) changes.Add(Text("Repair_Action_Thumbnail", "Remove confirmed embedded thumbnails and previews."));
            if ((metadata & 2) != 0) changes.Add(Text("Repair_Action_Heif", "Align primary EXIF orientation with the HEIF display transform."));
            if (rotateVideo) changes.Add(DescribeVideoTransform(video!, reflection));
            if (copy) changes.Add(Text("Repair_Action_Copy", "Copy the image and any confirmed companion unchanged."));
            if (incompleteRotation) changes.Add(Text("Repair_Limited_Image", "Image rotation was not performed: its metadata or attached-media layout cannot safely be transformed. Original orientation is retained."));
            if (limitedExif) changes.Add(Text("Repair_Limited_Exif", "EXIF could not be fully diagnosed; original image bytes are retained."));
            if (incompletePairedVideo) changes.Add(Text("Repair_Limited_PairedVideo", "Paired video rotation was not performed; the original video and its pairing metadata are retained."));
            if (uncheckedEmbeddedVideo) changes.Add(Text("Repair_Limited_Embedded", "Embedded video orientation was not repaired; its original bytes are retained."));
            if (!metadataProbe.Supported) changes.Add(Text("Repair_Limited_Metadata", "Thumbnail/orientation metadata repair could not be verified: {0}", metadataProbe.Diagnostic ?? ""));
            bool isolate = incomplete || (apple && videoPath == null) || videoWarning != null ||
                (rotateVideo && pair?.Timing.CoverTimestampUs < 0);
            if (apple && videoPath == null) changes.Add(Text("Repair_Limited_Companion", "Apple MOV companion was not found. Output will be isolated."));
            if (videoWarning != null) changes.Add(Text("Repair_Limited_VideoProbe", "MOV rotation could not be verified: {0}", videoWarning));
            if (rotateVideo && pair?.Timing.CoverTimestampUs < 0)
                changes.Add(Text("Repair_Limited_Cover", "Source cover time is unknown. No cover timestamp will be invented."));
            string action = string.Join(" ", changes);
            if (dryRun) return new("planned", action);

            var workspaceAttempt = MediaWorkspace.TryCreate(token);
            if (workspaceAttempt.Failure is { } workspaceFailure) return PublicationRefused(workspaceFailure);
            using var workspace = workspaceAttempt.Workspace!;
            string extension = Path.GetExtension(sourcePath);
            string staged = metadata != 0 || rotate ? workspace.AllocateFilePath("repair", extension) : sourcePath;
            NativeMediaService.NativeRegisteredOutputEvidence? evidence = null;
            bool imageReencoded = false;
            if (metadata != 0)
            {
                var repaired = await NativeMediaService.TryRepairImageExifAsync(sourcePath, staged, actions, token);
                if (repaired.Failure is { } metadataFailure) return PublicationRefused(metadataFailure);
                if (repaired.Changes != metadata || repaired.Evidence == null)
                    return new("failed", "Image repair result differed from its diagnostic plan.");
                evidence = repaired.Evidence;
            }
            if (rotate)
            {
                string rotated = metadata != 0 ? workspace.AllocateFilePath("repair", extension) : staged;
                const int normalizeExif = 7; // LPB_JPEG_TRANSFORM_NORMALIZE_EXIF
                var attempt = await NativeMediaService.TransformJpegLosslesslyWithOutputEvidenceAsync(
                    metadata != 0 ? staged : sourcePath, rotated, normalizeExif, token, allowReencode: true);
                if (attempt.ResultCode == NativeResult.Ok)
                {
                    if (attempt.OutputEvidence is not { } outputEvidence)
                        return new("failed", "Could not verify Native JPEG repair output ownership. " + attempt.OutputEvidenceFailure?.Message);
                    evidence = outputEvidence;
                    staged = rotated;
                    if (attempt.Reencoded)
                    {
                        imageReencoded = true;
                        isolate = true;
                        action += Text("Repair_Action_JpegReencoded", " JPEG was re-encoded at quality 92 without cropping; the output is isolated.");
                    }
                }
                else
                {
                    rotate = false;
                    incompleteRotation = true;
                    isolate = true;
                    changes.RemoveAt(0);
                    changes.Add(Text("Repair_Limited_JpegRotation", "JPEG rotation was skipped: {0}", attempt.Diagnostic ?? "JPEG transform is unavailable."));
                    action = string.Join(" ", changes);
                    if (evidence == null) staged = sourcePath;
                    if (evidence == null && !rotateVideo) return new RepairResult("warning", action + " No repair completed; no unchanged copy was published.") { Actions = ActionResults(options, 0, 1, false) };
                }
            }
            using var stagedLease = File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (evidence != null && await TryVerifyStageAsync(stagedLease, staged, evidence, token) is { } stageFailure) return new("failed", stageFailure.Message);

            string? stagedVideo = videoPath;
            FileStream? stagedVideoLease = null;
            bool videoMetadataVerified = true;
            try
            {
                if (rotateVideo)
                {
                    stagedVideo = workspace.AllocateFilePath("repair", Path.GetExtension(videoPath!));
                    try
                    {
                        var repairAttempt = await TryRotateVideoAsync(videoPath!, stagedVideo, video!, token);
                        if (repairAttempt.Failure is { } repairFailure)
                        {
                            if (repairFailure is OperationCanceledException) return new("cancelled", "用户取消，本项未完成发布。");
                            if (evidence is null) return new("failed", repairFailure.Message);
                            stagedVideo = videoPath;
                            rotateVideo = false;
                            isolate = true;
                            action += Text("Repair_Limited_VideoRotation", " Video rotation could not be verified: {0}", repairFailure.Message);
                        }
                        else
                        {
                        var repairedVideo = repairAttempt.Stage!;
                        stagedVideoLease = repairedVideo.Lease;
                        videoMetadataVerified = repairedVideo.MetadataVerified;
                        isolate |= !repairedVideo.MetadataVerified;
                        action += Text("Repair_Action_PairedVideo", " Video re-encoded; primary audio retained; Apple pairing and cover rebuilt. Unrelated auxiliary tracks were omitted.");
                        if (!repairedVideo.MetadataVerified) action += Text("Repair_Limited_Preservation", " Source metadata preservation was not fully verified; output is isolated.");
                        }
                    }
                    catch (InvalidOperationException ex) when (evidence != null)
                    {
                        stagedVideo = videoPath;
                        rotateVideo = false;
                        isolate = true;
                        action += Text("Repair_Limited_VideoRotation", " Video rotation could not be verified: {0}", ex.Message);
                    }
                }
                if (!copy && evidence == null && !rotateVideo)
                    return new("failed", "No repair completed; no unchanged output was published. " + action);
                if (isolate) outputDirectory = GetIsolationDirectory(outputDirectory);

                var afterAttempt = await NativeMediaService.TryCapturePreservationObservationAsync(staged, source.Protocol, container, token);
                if (afterAttempt.Failure is { } afterFailure) return PublicationRefused(afterFailure);
                var after = afterAttempt.Observation!;
                var resultAttempt = await NativeMediaService.TryInspectMediaWithPlanAsync(staged, stagedVideo, token);
                if (resultAttempt.Failure is { } resultFailure) return PublicationRefused(resultFailure);
                using var resultSource = resultAttempt.Source!;
                var result = resultSource.Facts;
                var postMetadata = metadata == 0 ? new NativeMediaService.ImageExifRepairAttempt(0, null, null)
                    : await NativeMediaService.TryRepairImageExifAsync(staged, null, actions, token);
                if (postMetadata.Failure is { } metadataFailure) return PublicationRefused(metadataFailure);
                if (composite && !await HasUnchangedAppendedMediaAsync(sourceLease, stagedLease, source.PrimaryImage.ByteLength, token))
                    return new("failed", "Appended media bytes or their positions changed during metadata repair.");
                bool swapsDimensions = rotate && before.Orientation >= 5;
                if ((rotate && after.Orientation != 1) || after.ExifParseError != before.ExifParseError || after.CodestreamError ||
                    (jpeg && !rotate && before.ImageCodestreamSha256 != after.ImageCodestreamSha256) ||
                    (metadata != 0 && postMetadata.Changes != 0) ||
                    before.GpsSha256 != after.GpsSha256 || before.IccSha256 != after.IccSha256 ||
                    before.MakernoteNonliveSha256 != after.MakernoteNonliveSha256 ||
                    result.PrimaryImage.Width != (swapsDimensions ? source.PrimaryImage.Height : source.PrimaryImage.Width) ||
                    result.PrimaryImage.Height != (swapsDimensions ? source.PrimaryImage.Width : source.PrimaryImage.Height) ||
                    !string.Equals(result.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                    result.Protocol != (pair?.Protocol ?? source.Protocol) ||
                    (pair != null && (result.Protocol != pair.Protocol ||
                        result.Timing.CoverTimestampUs != pair.Timing.CoverTimestampUs ||
                        (!rotateVideo && result.SecondarySha256 != pair.SecondarySha256))))
                    return new("failed", "The staged image or Apple pair did not pass repair validation.");

                if (OutputDirectoryAttempt.Ensure(outputDirectory) is { } directoryFailure) return PublicationRefused(directoryFailure);
                string stem = Path.GetFileNameWithoutExtension(sourcePath);
                string imageFinal = Path.Combine(outputDirectory, stem + Path.GetExtension(sourcePath));
                string? videoFinal = videoPath == null ? null : Path.Combine(outputDirectory, stem + Path.GetExtension(videoPath));
                int index = 1;
                while (Path.Exists(imageFinal) || (videoFinal != null && Path.Exists(videoFinal)))
                {
                    string name = $"{stem} ({index})";
                    imageFinal = Path.Combine(outputDirectory, name + Path.GetExtension(sourcePath));
                    videoFinal = videoPath == null ? null : Path.Combine(outputDirectory, name + Path.GetExtension(videoPath));
                    index++;
                }

                var publication = videoFinal != null
                    ? await WindowsSplitOutputPublisher.TryPublishAsync(staged, stagedVideo!, imageFinal, videoFinal, false, null, token)
                    : await WindowsSplitOutputPublisher.TryPublishSingleAsync(imageFinal, false,
                        (stream, ct) => MediaFileHashAttempt.CopyToAsync(stagedLease.SafeFileHandle, stream, ct), null, token);
                if (publication.Failure is { } publicationFailure) return PublicationRefused(publicationFailure);

                return new(isolate ? "warning" : copy ? "copied" : "success", action, imageFinal, videoFinal)
                {
                    ImageStatus = evidence == null ? "copied" :
                        imageReencoded || incompleteRotation || limitedExif || !metadataProbe.Supported ? "warning" : "success",
                    VideoStatus = videoFinal == null ? null : !rotateVideo ? "copied" :
                        !videoMetadataVerified || pair?.Timing.CoverTimestampUs < 0 ? "warning" : "success",
                    Limitations = PairLimitations(apple && videoPath == null, rotateVideo && pair?.Timing.CoverTimestampUs < 0, !videoMetadataVerified),
                    Actions = ActionResults(options, (rotate ? 1u : 0u) | ((metadata & 1) != 0 ? 2u : 0u) |
                        ((metadata & 2) != 0 ? 4u : 0u) | (rotateVideo ? 8u : 0u),
                        (incompleteRotation ? 1u : 0u) | ((!metadataProbe.Supported || limitedExif) ? 6u : 0u) |
                        (incompletePairedVideo || uncheckedEmbeddedVideo || videoWarning != null || (apple && videoPath == null) ? 8u : 0u), false)
                };
            }
            finally
            {
                stagedVideoLease?.Dispose();
            }
        }

        private static uint SamsungActions(RepairOptions options) =>
            (options.FixImageRotation?1u:0u)|(options.StripImageThumbnail?2u:0u)|
            (options.FixHeicOrientation?4u:0u)|(options.FixVideoRotation?8u:0u);

        private static async Task<RepairResult> RepairHuaweiAsync(string source, string directory,
            bool dryRun, RepairOptions options, CancellationToken token, bool copyPerfect)
        {
            var workspaceAttempt = MediaWorkspace.TryCreate(token);
            if (workspaceAttempt.Failure is { } workspaceFailure) return PublicationRefused(workspaceFailure);
            using var workspace = workspaceAttempt.Workspace!;
            string? staged = dryRun ? null : workspace.AllocateFilePath("huawei", Path.GetExtension(source));
            var attempt = await NativeHuaweiRepair.TryRunAsync(source, staged, SamsungActions(options), token);
            if (attempt.Failure is { } repairFailure) return new(repairFailure is OperationCanceledException ? "cancelled" : "failed", repairFailure.Message);
            var repair = attempt.Observation!;
            if (!repair.Supported) return new RepairResult("failed", repair.Details);
            string details = Text("Repair_Action_Huawei", "已识别华为实况结构；按所选选项处理照片和内嵌视频，保留声音与 HDR 信息。") + " " + repair.Details;
            if (repair.Changed == 0)
            {
                if (repair.Pending != 0)
                {
                    details = Text("Repair_Huawei_NoAction", "文件可读取；部分所选检查或修复尚未完成，没有将未改动文件标为修复成功。") + " " + details;
                    if (!copyPerfect || dryRun) return new RepairResult(dryRun ? "planned" : "warning", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                    using var retained = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var isolatedCopy = await TryPublishSingleCopyAsync(source, GetIsolationDirectory(directory), retained, token);
                    if (isolatedCopy.Failure is { } isolatedFailure) return PublicationRefused(isolatedFailure);
                    return new RepairResult("warning", details, isolatedCopy.Path) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                }
                if (!copyPerfect) return new RepairResult("healthy", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                if (dryRun) return new RepairResult("planned", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                using var original = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                var copyAttempt = await TryPublishSingleCopyAsync(source, directory, original, token);
                    if (copyAttempt.Failure is { } copyFailure) return PublicationRefused(copyFailure);
                    return new RepairResult("copied", details, copyAttempt.Path) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
            }
            if (dryRun) return new RepairResult("planned", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
            if (repair.Evidence == null) return new("failed", "Huawei repair has no verified output receipt.");
            using var lease = File.Open(staged!, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (await TryVerifyStageAsync(lease, staged!, repair.Evidence, token) is { } stageFailure) return new("failed", stageFailure.Message);
            if (repair.Pending != 0)
            {
                directory = GetIsolationDirectory(directory);
                details += " " + Text("Repair_Huawei_Partial", "已完成可安全处理的照片或视频修复；仍有未完成检查或修复，输出至隔离目录。");
            }
            var publication = await TryPublishSingleCopyAsync(source, directory, lease, token);
            if (publication.Failure is { } publicationFailure) return PublicationRefused(publicationFailure);
            string final = publication.Path!;
            return new RepairResult(repair.Pending != 0 ? "warning" : "success", details, final) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
        }


        private static async Task<RepairResult> RepairSamsungAsync(string source,string directory,
            bool dryRun,RepairOptions options,CancellationToken token,bool copyPerfect)
        {
            var workspaceAttempt = MediaWorkspace.TryCreate(token);
            if (workspaceAttempt.Failure is { } workspaceFailure) return PublicationRefused(workspaceFailure);
            using var workspace = workspaceAttempt.Workspace!;
            string? staged=dryRun?null:workspace.AllocateFilePath("samsung",Path.GetExtension(source));
            var attempt=await NativeSamsungRepair.TryRunAsync(source,workspace.RootDirectory,staged,SamsungActions(options),token);
            if (attempt.Failure is { } repairFailure) return new(repairFailure is OperationCanceledException ? "cancelled" : "failed", repairFailure.Message);
            var repair=attempt.Observation!;
            if(repair.Changed==0)
            {
                if (repair.IsPartial)
                {
                    string? retainedCopy = null;
                    if (copyPerfect && !dryRun)
                    {
                        using var retained = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var copyAttempt = await TryPublishSingleCopyAsync(source, GetIsolationDirectory(directory), retained, token);
                        if (copyAttempt.Failure is { } copyFailure) return PublicationRefused(copyFailure);
                        retainedCopy = copyAttempt.Path;
                    }
                    return new RepairResult(dryRun ? "planned" : "warning", repair.Details, retainedCopy)
                    { Actions = ActionResults(options, 0, SamsungActions(options) & 7u, dryRun),
                      Limitations = [new("image_preservation_unverified", "照片修复后的媒体保留检查未通过，原照片保留。") ] };
                }
                if(!copyPerfect)return new("healthy","Selected Samsung checks completed; no selected repair is needed.");
                if(dryRun)return new("planned","Copy the Samsung dynamic photo unchanged.");
                using var original=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read);
                var copyPublication = await TryPublishSingleCopyAsync(source,directory,original,token);
            if (copyPublication.Failure is { } copyPublicationFailure) return PublicationRefused(copyPublicationFailure);
            string copy = copyPublication.Path!;
                return new("copied","Copied the Samsung dynamic photo unchanged.",copy);
            }
            if(dryRun)return new("planned",repair.Details);
            if(repair.Evidence is null)return new("failed", "Samsung repair has no verified output receipt.");
            using var lease=File.Open(staged!,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (await TryVerifyStageAsync(lease, staged!, repair.Evidence, token) is { } stageFailure) return new("failed", stageFailure.Message);
            if(repair.IsPartial)directory=GetIsolationDirectory(directory);
            var publication = await TryPublishSingleCopyAsync(source,directory,lease,token);
            if (publication.Failure is { } publicationFailure) return PublicationRefused(publicationFailure);
            string final = publication.Path!;
            string samsungDetails = repair.Details;
            var encoded=new System.Collections.Generic.List<string>();
            if((repair.Reencoded&1)!=0)encoded.Add("photo/HDR images");
            if((repair.Reencoded&8)!=0)encoded.Add("main video");
            if((repair.Reencoded&16)!=0)encoded.Add("autoplay video");
            return new(repair.IsPartial ?"warning":"success",samsungDetails+" Re-encoded: "+(encoded.Count==0?"none":string.Join(", ",encoded))+".",final) { Actions = ActionResults(options, (repair.Changed & 15) | ((repair.Changed & 16) != 0 ? 8u : 0u), repair.IsPartial ? SamsungActions(options) & 7u : 0u, false),
                Limitations = repair.IsPartial ? [new("image_preservation_unverified", "照片修复后的媒体保留检查未通过，原照片保留。") ] : [] };
        }

        private static async Task<RepairResult> RepairVideoFileCoreAsync(string sourcePath, string outputDirectory,
            bool allDevices, bool repairLongVideos, bool dryRun, bool batch, RepairOptions options, CancellationToken token,
            bool copyPerfect = false, RepairBatchContext? batchContext = null)
        {
            if (!options.FixVideoRotation) return new("skipped", "Video rotation repair was disabled.");
            using var localBatch = batchContext == null ? new RepairBatchContext() : null;
            batchContext ??= localBatch!;
            sourcePath = Path.GetFullPath(sourcePath);
            string directory = Path.GetDirectoryName(sourcePath)!;
            if (token.IsCancellationRequested) return new("cancelled", "用户取消，本项未完成发布。");
            using var sourceProtection = new NeutralArtifactReadLeaseSet();
            if (sourceProtection.TryPin(new MediaArtifact { Path = sourcePath, Kind = MediaArtifactKind.MotionVideo }) is { } protectionFailure)
                return new("failed", protectionFailure.Message);
            using var sourceRead = SourceReadAttempt.Open(sourcePath);
            if (!sourceRead.Succeeded) return new("failed", $"Cannot read source (system error {sourceRead.Error}).");
            using var sourceLease = new FileStream(sourceRead.Handle, FileAccess.Read);
            var inspection = await NativeMediaService.TryInspectMediaWithPlanAsync(sourcePath, cancellationToken: token);
            if (inspection.Failure is { } inspectionFailure) return new(inspectionFailure is OperationCanceledException ? "cancelled" : "failed", inspectionFailure.Message);
            using var inspectedVideo = inspection.Source!;
            var source = inspectedVideo.Facts;
            var hint = string.IsNullOrEmpty(source.PairingIdentifier) ? null : await batchContext.ReadHintAsync(sourcePath, token);
            if (hint?.Protocol == SourceProtocol.VivoLegacyDualFile)
            {
                if (!allDevices) return new("skipped", "Legacy vivo video is outside the default Apple/Samsung repair scope.");
                var candidateAttempt = await batchContext.TryCandidatesAsync(sourcePath, token, SourceProtocol.VivoLegacyDualFile, hint.Identifier);
                if (candidateAttempt.Failure is { } candidateFailure) return PublicationRefused(candidateFailure);
                var candidates = candidateAttempt.Candidates;
                string? image = null; int videos = 0;
                foreach (var candidate in candidates)
                {
                    if (Path.GetExtension(candidate.Path).ToLowerInvariant() is ".jpg" or ".jpeg")
                    {
                        if (image != null) return new("failed", "More than one image has this legacy pairing identifier.");
                        image = candidate.Path;
                    }
                    else videos++;
                }
                if (videos != 1) return new("failed", "Legacy video pairing is ambiguous.");
                if (image == null) return new("failed", "Legacy video companion is missing; no safe standalone binding rewrite is available.");
                return batch ? new("skipped", "Legacy video is processed with its paired image.")
                    : await RepairImageFileAsync(image, outputDirectory, allDevices, dryRun, options, token, copyPerfect, batchContext);
            }
            bool isolate = !string.IsNullOrEmpty(source.PairingIdentifier);
            if (isolate && hint?.Protocol != SourceProtocol.AppleLivePhoto)
                return new("failed", "Video pairing ownership could not be classified safely.");
            if (isolate)
            {
                string? imagePath = null;
                var candidateAttempt = await batchContext.TryCandidatesAsync(sourcePath, token, SourceProtocol.AppleLivePhoto, source.PairingIdentifier);
                if (candidateAttempt.Failure is { } candidateFailure) return PublicationRefused(candidateFailure);
                foreach (var candidateEntry in candidateAttempt.Candidates)
                {
                    string candidate = candidateEntry.Path;
                    string ext = Path.GetExtension(candidate).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".heic" or ".heif")) continue;
                    var inspected = candidateEntry;
                    if (!string.Equals(inspected.Identifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (imagePath != null) return new("failed", "More than one image has this Apple pairing identifier.");
                    imagePath = candidate;
                }
                if (imagePath != null)
                    return batch ? new("skipped", "Apple MOV is processed with its paired image.")
                        : await RepairImageFileAsync(imagePath, outputDirectory, allDevices, dryRun, options, token, copyPerfect, batchContext);
            }
            else if (!allDevices)
                return new("skipped", "Default repair scope is Apple and Samsung Live Photos. Use --all-devices for supported standalone videos.");

            var videoProbe = await NativeMediaService.TryProbeVideoRepairAsync(sourcePath, token);
            if (videoProbe.Failure is { } videoFailure) return new(videoFailure is OperationCanceledException ? "cancelled" : "failed", videoFailure.Message);
            var video = videoProbe.Facts!;
            uint reflection = videoProbe.Reflection;
            if (!isolate && !repairLongVideos && video.DurationSeconds > 3.5)
                return new("skipped", "Standalone video exceeds 3.5 seconds. Use --repair-long-videos to include it.");
            if (video.RotationDegrees == 0 && reflection == 0)
            {
                if (!copyPerfect || isolate)
                    return new("healthy", "Video orientation check completed; no normalization needed.");
                if (dryRun) return new("planned", "Copy the video unchanged.");
                var copyPublication = await TryPublishSingleCopyAsync(sourcePath, outputDirectory, sourceLease, token);
            if (copyPublication.Failure is { } copyPublicationFailure) return PublicationRefused(copyPublicationFailure);
            string copied = copyPublication.Path!;

                return new("copied", "Copied the video unchanged.", Video: copied);
            }
            string action = DescribeVideoTransform(video, reflection);
            if (isolate)
            {
                action += " Apple image companion was not found. Output will be isolated.";
                outputDirectory = GetIsolationDirectory(outputDirectory);
            }
            if (dryRun) return new("planned", action);

            var workspaceAttempt = MediaWorkspace.TryCreate(token);
            if (workspaceAttempt.Failure is { } workspaceFailure) return PublicationRefused(workspaceFailure);
            using var workspace = workspaceAttempt.Workspace!;
            string staged = workspace.AllocateFilePath("repair", Path.GetExtension(sourcePath));
            var repairAttempt = await TryRotateVideoAsync(sourcePath, staged, video, token);
            if (repairAttempt.Failure is { } repairFailure) return new(repairFailure is OperationCanceledException ? "cancelled" : "failed", repairFailure.Message);
            var repairedVideo = repairAttempt.Stage!;
            using var stage = repairedVideo.Lease;
            if (!repairedVideo.MetadataVerified)
            {
                if (!isolate) outputDirectory = GetIsolationDirectory(outputDirectory);
                isolate = true;
                action += Text("Repair_Limited_Preservation", " Source metadata preservation was not fully verified; output is isolated.");
            }
            action += Text("Repair_Action_Video", " Video re-encoded; only primary audio/video and confirmed Apple pairing/cover are carried.");
            var resultInspection = await NativeMediaService.TryInspectMediaWithPlanAsync(staged, cancellationToken: token);
            if (resultInspection.Failure is { } resultFailure) return new(resultFailure is OperationCanceledException ? "cancelled" : "failed", resultFailure.Message);
            using var resultSource = resultInspection.Source!;
            var result = resultSource.Facts;
            if (!string.Equals(result.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(source.PairingIdentifier) && result.Timing.CoverTimestampUs != source.Timing.CoverTimestampUs))
                return new("failed", "The repaired video did not preserve the Apple pairing identifier and cover.");

            var publication = await TryPublishSingleCopyAsync(sourcePath, outputDirectory, stage, token);
            if (publication.Failure is { } publicationFailure) return PublicationRefused(publicationFailure);
            string final = publication.Path!;

            return new(isolate ? "warning" : "success", action, Video: final) { Actions = ActionResults(options, 8, 0, false), Limitations = PairLimitations(false, false, !repairedVideo.MetadataVerified) };
        }

        private sealed record RepairCopyAttempt(string? Path, Exception? Failure);

        private static async Task<RepairCopyAttempt> TryPublishSingleCopyAsync(string sourcePath, string outputDirectory,
            Stream stage, CancellationToken token)
        {
            if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
            if (OutputDirectoryAttempt.Ensure(outputDirectory) is { } directoryFailure) return new(null, directoryFailure);
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            string final = Path.Combine(outputDirectory, stem + extension);
            for (int index = 1; Path.Exists(final); index++)
                final = Path.Combine(outputDirectory, $"{stem} ({index}){extension}");
            if (stage is not FileStream file) return new(null, new InvalidOperationException("Repair publication requires a retained file object."));
            var publication = await WindowsSplitOutputPublisher.TryPublishSingleAsync(final, false,
                (writer, ct) => MediaFileHashAttempt.CopyToAsync(file.SafeFileHandle, writer, ct), null, token).ConfigureAwait(false);
            return publication.Failure is { } failure ? new(null, failure) : new(publication.Receipt!.FinalPath, null);
        }

        private static RepairResult PublicationRefused(Exception failure)
            => new(failure is OperationCanceledException ? "cancelled" : "failed", failure.Message);

        private static string DescribeVideoTransform(VideoFacts video, uint reflection) => reflection switch
        {
            1 => Text("Repair_Action_Horizontal", "Apply the horizontal display reflection to video frames."),
            2 => Text("Repair_Action_Vertical", "Apply the vertical display reflection to video frames."),
            _ => Text("Repair_Action_Rotation", "Apply the display rotation ({0} degrees) to video frames.", video.RotationDegrees)
        };

        private sealed record VideoRepairStage(FileStream Lease, bool MetadataVerified);

        private sealed record VideoRepairAttempt(VideoRepairStage? Stage, Exception? Failure);

        private static async Task<VideoRepairAttempt> TryRotateVideoAsync(string source, string staged, VideoFacts before,
            CancellationToken token)
        {
            if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
            if (before.Codec is not (VideoCodec.H264 or VideoCodec.Hevc))
                return new(null, new NotSupportedException("Video rotation requires an H.264 or HEVC input."));
            var attempt = await NativeMediaService.TranscodeVideoAttemptAsync(
                source, staged, before.Container, before.Codec, 18, token, PreservationPolicy.BestEffort, repairOrientation: true);
            if (token.IsCancellationRequested || attempt.ResultCode == NativeResult.Cancelled)
                return new(null, new OperationCanceledException(token));
            if (!attempt.Succeeded)
                return new(null, attempt.FailureException ?? new InvalidOperationException(attempt.LastError ?? "Native video rotation failed."));
            if (attempt.OutputEvidence is not { } evidence)
                return new(null, new IOException("Could not verify Native video output ownership.", attempt.OutputEvidenceFailure));
            var opened = SourceReadAttempt.Open(staged);
            if (!opened.Succeeded) { opened.Dispose(); return new(null, new IOException($"Cannot read repaired video (system error {opened.Error}).")); }
            var lease = new FileStream(opened.Handle, FileAccess.Read);
            bool retained = false;
            try
            {
                if (await TryVerifyStageAsync(lease, staged, evidence, token) is { } stageFailure) return new(null, stageFailure);
                var probe = await NativeMediaService.TryProbeVideoAsync(staged, token);
                if (!probe.Succeeded) return new(null, new InvalidOperationException(probe.Diagnostic ?? "Repaired video cannot be verified."));
                var after = probe.Facts!;
                bool swap = before.RotationDegrees is 90 or 270;
                if (after.RotationDegrees != 0 || before.HasAudio != after.HasAudio ||
                    after.Width != (swap ? before.Height : before.Width) ||
                    after.Height != (swap ? before.Width : before.Height))
                    return new(null, new InvalidOperationException("Repaired video geometry or audio did not pass validation."));
                retained = true;
                return new(new(lease, attempt.MetadataPreservation != null), null);
            }
            finally { if (!retained) lease.Dispose(); }
        }

        private static async Task<Exception?> TryVerifyStageAsync(FileStream lease, string path,
            NativeMediaService.NativeRegisteredOutputEvidence evidence, CancellationToken token)
        {
            if (token.IsCancellationRequested) return new OperationCanceledException(token);
            if (!WindowsFileIdentity.TryCapture(lease.SafeFileHandle, out var identity, out int error) ||
                !evidence.FileIdentity.Matches(identity!) || lease.Length != evidence.ByteLength)
                return new IOException($"The staged file no longer matches the Native output receipt (system error {error}).");
            var hash = await MediaFileHashAttempt.ComputeAsync(path, token).ConfigureAwait(false);
            if (hash.Failure is { } hashFailure) return hashFailure;
            if (!string.Equals(hash.Sha256, evidence.Sha256, StringComparison.OrdinalIgnoreCase))
                return new IOException("The staged file bytes no longer match the Native output receipt.");
            lease.Position = 0;
            return null;
        }

        private static async Task<bool> HasUnchangedAppendedMediaAsync(FileStream source, FileStream output,
            long primaryLength, CancellationToken token)
        {
            if (primaryLength <= 0 || primaryLength > source.Length || source.Length != output.Length) return false;
            source.Position = primaryLength;
            byte[] expected = await SHA256.HashDataAsync(source, token);
            output.Position = primaryLength;
            byte[] actual = await SHA256.HashDataAsync(output, token);
            source.Position = output.Position = 0;
            return expected.AsSpan().SequenceEqual(actual);
        }

        /// <summary>
        /// 扫描与诊断文件。
        /// </summary>
        public static async Task<RepairAnalysisResult> AnalyzeFileAsync(
            string filePath, CancellationToken token = default, bool defaultDevicesOnly = false)
        {
            using var batch = new RepairBatchContext();
            return await AnalyzeFileInBatchAsync(filePath, token, defaultDevicesOnly, batch);
        }

        internal static async Task<RepairAnalysisResult> AnalyzeFileInBatchAsync(
            string filePath, CancellationToken token, bool defaultDevicesOnly, RepairBatchContext batch)
        {
            try
            {
                var analysis = await AnalyzeFileCoreAsync(filePath, token, defaultDevicesOnly, batch);
                if (token.IsCancellationRequested) return analysis;
                var version = ScanFileSystem.Read(filePath);
                if (version.Failure is { } versionFailure)
                    return new() { IssueType = RepairIssueType.Error, IssueDescription = versionFailure.Message };
                if (!WindowsFileIdentity.TryCapture(filePath, out var identity, out int identityError))
                    return new() { IssueType = RepairIssueType.Error, IssueDescription = $"Cannot retain diagnosed source identity (system error {identityError})." };
                analysis.DiagnosedFileSize = version.Value!.Length;
                analysis.DiagnosedWriteTimeUtc = version.Value.LastWriteTimeUtc;
                analysis.DiagnosedIdentity = identity;
                var choices = batch.DiagnosticOptions;
                analysis.DiagnosedActionMask = (choices.FixImageRotation ? 1u : 0u) | (choices.StripImageThumbnail ? 2u : 0u) |
                    (choices.FixHeicOrientation ? 4u : 0u) | (choices.FixVideoRotation ? 8u : 0u);
                return analysis;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new() { IssueType = RepairIssueType.Error, IssueDescription = ex.Message };
            }
        }

        internal static async Task<RepairAnalysisResult> SupplementAnalysisAsync(string path, RepairAnalysisResult previous,
            RepairOptions selected, bool defaultDevicesOnly, CancellationToken token)
        {
            var version = new FileInfo(path);
            if (previous.DiagnosedFileSize != version.Length || previous.DiagnosedWriteTimeUtc != version.LastWriteTimeUtc ||
                previous.DiagnosedIdentity != WindowsFileIdentity.Capture(path))
            {
                using var refreshed = new RepairBatchContext { DiagnosticOptions = selected };
                return await AnalyzeFileInBatchAsync(path,token,defaultDevicesOnly,refreshed).ConfigureAwait(false);
            }
            uint requested = (selected.FixImageRotation ? 1u : 0u) | (selected.StripImageThumbnail ? 2u : 0u) |
                (selected.FixHeicOrientation ? 4u : 0u) | (selected.FixVideoRotation ? 8u : 0u);
            uint missing = requested & ~previous.DiagnosedActionMask;
            if (missing == 0) return previous;
            using var batch = new RepairBatchContext { DiagnosticOptions = new() { FixImageRotation = (missing & 1) != 0,
                StripImageThumbnail = (missing & 2) != 0, FixHeicOrientation = (missing & 4) != 0, FixVideoRotation = (missing & 8) != 0 } };
            var added = await AnalyzeFileInBatchAsync(path,token,defaultDevicesOnly,batch).ConfigureAwait(false);
            if ((missing & 1) != 0) { previous.ImageOrientation = added.ImageOrientation; previous.NeedsImageOrientationRepair = added.NeedsImageOrientationRepair; previous.RotationAngle = added.RotationAngle; }
            if ((missing & 2) != 0) previous.HasThumbnail = added.HasThumbnail;
            if ((missing & 4) != 0) previous.HasHeicOrientationConflict = added.HasHeicOrientationConflict;
            if ((missing & 8) != 0) { previous.VideoRotationAngle = added.VideoRotationAngle; previous.NeedsEmbeddedVideoTransform = added.NeedsEmbeddedVideoTransform;
                previous.AutoplayVideoRotationAngle = added.AutoplayVideoRotationAngle;previous.VideoCheckIncomplete = added.VideoCheckIncomplete;
                previous.VideoDurationSeconds = added.VideoDurationSeconds;previous.VideoTrackTransform = added.VideoTrackTransform; }
            previous.DiagnosedActionMask |= added.DiagnosedActionMask;
            previous.IssueDescription += " " + added.IssueDescription;
            bool actionable = previous.NeedsImageOrientationRepair || previous.HasThumbnail || previous.HasHeicOrientationConflict ||
                previous.NeedsEmbeddedVideoTransform || previous.VideoRotationAngle != 0 || previous.VideoTrackTransform.Length != 0;
            previous.IssueType = actionable ? RepairIssueType.NeedsRebuild : added.IssueType;
            return previous;
        }

        internal static async Task<RepairAnalysisResult> AnalyzeFileCoreAsync(string filePath, CancellationToken token, bool defaultDevicesOnly, RepairBatchContext batch)
        {
            if (token.IsCancellationRequested) return new() { IssueType = RepairIssueType.Limited, IssueDescription = "诊断已取消，尚未完成检查。" };
            if (defaultDevicesOnly)
            {
                var apple = await FileIndexService.Shared.GetAsync(filePath, DiscoveryCapability.AppleId, token).ConfigureAwait(false);
                var marker = LivePhotoScanSupport.IsImage(filePath)
                    ? await FileIndexService.Shared.GetAsync(filePath, DiscoveryCapability.SingleFileMarker, token).ConfigureAwait(false) : null;
                if (token.IsCancellationRequested) return new() { IssueType = RepairIssueType.Limited, IssueDescription = "诊断已取消，尚未完成检查。" };
                if (apple?.Pairing == null && marker?.SingleFileProtocol is not (SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic))
                    return new() { IssueType = apple?.State is FactState.Incomplete or FactState.ReadFailed ? RepairIssueType.Limited : RepairIssueType.NonApple,
                        IsVideo = LivePhotoScanSupport.IsVideo(filePath), IssueDescription = "Outside Apple/Samsung scope or identity discovery incomplete; no deep diagnosis performed." };
            }
            var (discovery, _) = await InspectRepairSourceAsync(filePath, defaultDevicesOnly, token, batch);
            if (token.IsCancellationRequested) return new() { IssueType = RepairIssueType.Limited, IssueDescription = "诊断已取消，尚未完成检查。" };
            if (discovery.Facts is not { } facts)
            {
                if (defaultDevicesOnly && IsLegacyVivoPairingRequirement(discovery.Failure))
                    return new() { IssueType = RepairIssueType.NonApple,
                        IssueDescription = "Legacy vivo image is outside the default Apple/Samsung repair scope. " +
                            "Single-file inspection cannot confirm its video companion; this is not an image decoding failure." };
                if (IsLegacyVivoPairingRequirement(discovery.Failure))
                    return new() { IssueType = RepairIssueType.Limited,
                        IssueDescription = "照片已识别，但未能唯一确认配对视频；尚未修复。" + discovery.Failure!.Reason };
                return new() { IssueType = RepairIssueType.Error, IssueDescription = discovery.Failure?.Reason ?? "Cannot inspect input." };
            }
            bool video = Path.GetExtension(filePath).ToLowerInvariant() is ".mov" or ".mp4";
            if(defaultDevicesOnly && string.IsNullOrEmpty(facts.PairingIdentifier) &&
                facts.Protocol is not (SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic))
                return new() {IssueType=RepairIssueType.NonApple,IsVideo=video,
                    IssueDescription="Skipped by default Apple/Samsung repair scope."};
            var analysis = new RepairAnalysisResult
            {
                ContentIdentifier = facts.PairingIdentifier ?? "",
                IsVideo = video
            };
            var selectedActions = batch.DiagnosticOptions;
            uint actionMask = (selectedActions.FixImageRotation ? 1u : 0u) | (selectedActions.StripImageThumbnail ? 2u : 0u) |
                (selectedActions.FixHeicOrientation ? 4u : 0u) | (selectedActions.FixVideoRotation ? 8u : 0u);
            if (video)
            {
                if (!selectedActions.FixVideoRotation)
                    return new() { IsVideo = true, ContentIdentifier = analysis.ContentIdentifier, IssueType = RepairIssueType.Limited,
                        VideoCheckIncomplete = true, IssueDescription = "Video direction diagnosis was not requested." };
                var videoProbe = await NativeMediaService.TryProbeVideoRepairAsync(filePath, token);
                if (videoProbe.Failure is { } videoFailure)
                    return new() { IsVideo = true, VideoCheckIncomplete = true, IssueType = RepairIssueType.Limited, IssueDescription = videoFailure.Message };
                var observed = videoProbe.Facts!;
                uint reflection = videoProbe.Reflection;
                analysis.VideoRotationAngle = observed.RotationDegrees;
                analysis.VideoDurationSeconds = observed.DurationSeconds;
                analysis.VideoTrackTransform = reflection == 1 ? "flip_horizontal" : reflection == 2 ? "flip_vertical" : "";
                analysis.VideoCodec = observed.Codec.ToString();
                analysis.IssueType = observed.RotationDegrees != 0 || reflection != 0
                    ? RepairIssueType.NeedsRebuild : RepairIssueType.Perfect;
                analysis.IssueDescription = analysis.NeedsRepair ? DescribeVideoTransform(observed, reflection) : "No video orientation repair needed.";
            return analysis;
            }
            if(facts.Protocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic)
            {
                var workspaceAttempt = MediaWorkspace.TryCreate(token);
                if (workspaceAttempt.Failure is { } workspaceFailure)
                    return new() { IssueType = RepairIssueType.Limited, IssueDescription = workspaceFailure.Message };
                using var workspace = workspaceAttempt.Workspace!;
                var attempt=await NativeSamsungRepair.TryRunAsync(filePath,workspace.RootDirectory,null,actionMask,token);
                if (attempt.Failure is { } repairFailure) return new() { IssueType = RepairIssueType.Limited, IssueDescription = repairFailure.Message };
                var repair=attempt.Observation!;
                analysis.IsSamsungMotionPhoto=true;
                analysis.ImageOrientation=(int)repair.ImageOrientation;
                analysis.NeedsImageOrientationRepair=(repair.Changed&1)!=0;
                analysis.HasHeicOrientationConflict=(repair.Changed&4)!=0;
                analysis.RotationAngle=repair.ImageOrientation switch {3 or 4=>180,5 or 6=>90,7 or 8=>270,_=>0};
                analysis.AutoplayVideoRotationAngle=(int)repair.AutoplayRotation;
                analysis.NeedsEmbeddedVideoTransform=(repair.Changed&8)!=0;
                analysis.HasThumbnail=(repair.Changed&2)!=0;
                analysis.VideoRotationAngle=(int)repair.MainRotation;
                analysis.IssueType=repair.Changed!=0?RepairIssueType.NeedsRebuild:RepairIssueType.Perfect;
                analysis.IssueDescription=repair.Details;
                return analysis;
            }
            if (facts.Protocol == SourceProtocol.HuaweiMovingPhoto)
            {
                var attempt = await NativeHuaweiRepair.TryRunAsync(filePath, null, actionMask, token);
                if (attempt.Failure is { } repairFailure) return new() { IssueType = RepairIssueType.Limited, IssueDescription = repairFailure.Message };
                var repair = attempt.Observation!;
                analysis.ImageOrientation = (int)repair.ImageOrientation;
                analysis.HasThumbnail = (repair.Changed & 2) != 0;
                analysis.HasHeicOrientationConflict = (repair.Changed & 4) != 0;
                analysis.NeedsImageOrientationRepair = (repair.Changed & 1) != 0;
                analysis.VideoRotationAngle = (int)repair.MainRotation;
                analysis.NeedsEmbeddedVideoTransform = ((repair.Changed | repair.Pending) & 8) != 0;
                analysis.VideoTrackTransform = repair.Reflection == 1 ? "flip_horizontal" : repair.Reflection == 2 ? "flip_vertical" : "";
                analysis.IssueType = !repair.Supported ? RepairIssueType.Error
                    : repair.Changed != 0 ? ((repair.Changed & 13) != 0 ? RepairIssueType.NeedsRebuild : RepairIssueType.NeedsStrip)
                    : repair.Pending != 0 ? RepairIssueType.Limited : RepairIssueType.Perfect;
                analysis.IssueDescription = Text("Repair_Action_Huawei", "已识别华为实况结构；按所选选项处理照片和内嵌视频，保留声音与 HDR 信息。") + " " + repair.Details;
                return analysis;
            }
            analysis.VideoCheckIncomplete = facts.MotionVideo?.IsPresent == true;
            if ((actionMask & 7) == 0)
                return new() { ContentIdentifier = analysis.ContentIdentifier, VideoCheckIncomplete = true,
                    IssueType = RepairIssueType.Limited, IssueDescription = "Image diagnosis was not requested; video is diagnosed independently." };
            var imageObservation = await NativeMediaService.TryCapturePreservationObservationAsync(filePath, facts.Protocol, facts.PrimaryImage.Container, token);
            if (imageObservation.Failure is { } imageFailure)
                return new() { IssueType = RepairIssueType.Limited, IssueDescription = imageFailure.Message };
            var observedImage = imageObservation.Observation!;
            uint metadataActions = (selectedActions.StripImageThumbnail ? 1u : 0u) | (selectedActions.FixHeicOrientation ? 2u : 0u);
            var metadataAttempt = metadataActions == 0 ? new NativeMediaService.ImageExifRepairAttempt(0, null, null)
                : await NativeMediaService.TryRepairImageExifAsync(filePath, null, metadataActions, token);
            var metadataProbe = new NativeMediaService.ImageExifRepairProbe(metadataAttempt.Failure is null,
                metadataAttempt.Changes, metadataAttempt.Failure?.Message);
            uint metadata = metadataProbe.Changes;
            analysis.ImageOrientation = observedImage.Orientation;
            analysis.HasHeicOrientationConflict = (metadata & 2) != 0;
            analysis.HasThumbnail = (metadata & 1) != 0;
            analysis.DateTimeOriginal = observedImage.DateTimeOriginal;
            bool jpegRotation = selectedActions.FixImageRotation && facts.PrimaryImage.Container == ImageContainer.Jpeg && observedImage.Orientation > 1;
            analysis.NeedsImageOrientationRepair = jpegRotation;
            analysis.RotationAngle = observedImage.Orientation switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 };
            if (observedImage.CodestreamError)
            {
                analysis.IssueType = RepairIssueType.Error;
                analysis.IssueDescription = "Image codestream cannot be verified.";
                return analysis;
            }
            if (!metadataProbe.Supported || observedImage.ExifParseError)
            {
                analysis.IssueType = RepairIssueType.Limited;
                analysis.IssueDescription = $"照片可读取，但无法确认安全的元数据修复方法。未将其判为损坏或无需修复。{metadataProbe.Diagnostic}";
                return analysis;
            }
            string? associationWarning = null;
            if (selectedActions.FixVideoRotation && (facts.Protocol == SourceProtocol.AppleLivePhoto ||
                (!string.IsNullOrEmpty(facts.PairingIdentifier) &&
                 (await batch.ReadHintAsync(filePath, token))?.Protocol == SourceProtocol.AppleLivePhoto)))
            {
                try
                {
                    var candidateAttempt = await batch.TryCandidatesAsync(filePath, token, SourceProtocol.AppleLivePhoto, facts.PairingIdentifier);
                    if (candidateAttempt.Failure is { } candidateFailure)
                        associationWarning = "照片检查已完成；视频关联范围未完成：" + candidateFailure.Message;
                    var candidates = candidateAttempt.Candidates;
                    int images = candidates.Count(candidate => LivePhotoScanSupport.IsImage(candidate.Path));
                    int videos = candidates.Count(candidate => LivePhotoScanSupport.IsVideo(candidate.Path));
                    if (images != 1 || videos != 1) associationWarning = "照片可读取，但未能唯一确认配对视频；视频检查未完成。";
                }
                catch (IOException ex) { associationWarning = "照片检查已完成；视频关联范围未完成：" + ex.Message; }
                if (associationWarning != null) analysis.VideoCheckIncomplete = true;
            }
            analysis.IssueType = jpegRotation || (metadata & 2) != 0 ? RepairIssueType.NeedsRebuild
                : analysis.HasThumbnail ? RepairIssueType.NeedsStrip : associationWarning != null ? RepairIssueType.Limited : RepairIssueType.Perfect;
            analysis.IssueDescription = analysis.NeedsRepair
                ? $"Orientation={observedImage.Orientation}; embedded thumbnail/preview={analysis.HasThumbnail}; HEIF orientation conflict={(metadata & 2) != 0}."
                : "No image repair needed.";
            if (associationWarning != null) analysis.IssueDescription += " " + associationWarning;
            return analysis;
        }

        public static async Task<(bool Success, string Message)> RepairAsync(
            string sourcePath, string targetPath, RepairAnalysisResult analysis,
            CancellationToken token = default, RepairOptions? options = null)
        {
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                return (false, "Repair exports a new file. Source and target paths must differ.");
            string folder = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
            options ??= new RepairOptions();
            RepairResult result = analysis.IsVideo
                ? await RepairVideoFileAsync(sourcePath, folder, true, true, false, false, options, token)
                : await RepairImageFileAsync(sourcePath, folder, true, false, options, token);
            return (result.Status is "success" or "copied" || (result.Status == "warning" && result.PublishedOutputs.Length != 0),
                $"{result.Message} {result.Image} {result.Video}");
        }

        /// <summary>
        /// 写入实况照片标记。
        /// </summary>
        public static Task TryWriteLivePhotoBoxMarkerAsync(
            string targetPath,
            string operation,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 运行 exiftool（已废弃）。
        /// </summary>
        public static Task RunExifToolAsync(params string[] args)
            => throw new NotSupportedException("ExifTool has been removed from LivePhotoBox.");

        /// <summary>
        /// 运行 exiftool（已废弃）。
        /// </summary>
        public static Task RunExifToolAsync(CancellationToken token, params string[] args)
            => throw new NotSupportedException("ExifTool has been removed from LivePhotoBox.");
    }
}
