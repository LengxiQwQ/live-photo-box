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
            internal bool? HistoryRecorded { get; init; }
            internal RepairActionResult[] Actions { get; init; } = [];
            internal RepairPublishedOutput[] PublishedOutputs => CreatePublishedOutputs(this);
            internal string Completion => Status == "warning" ? PublishedOutputs.Length == 0 ? "not_completed" : "partial_or_unverified"
                : Status == "success" ? "completed" : Status == "healthy" ? "not_needed" : Status;
            internal string OutcomeKey => Status == "warning" ? PublishedOutputs.Length == 0 ? "warning_no_output"
                : PublishedOutputs.All(x => x.Kind == "isolated_copy") ? "warning_copy" : "warning_isolated" : Status;
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
            var inspection = await NativeMediaService.InspectMediaForDiscoveryAsync(path, null, token);
            if (defaultDevicesOnly || !IsLegacyVivoPairingRequirement(inspection.Failure))
                return (inspection, null);

            SourceInspectionResult? matched = null;
            string? companion = null;
            var candidates = await batch.CandidatesAsync(path, token, SourceProtocol.VivoLegacyDualFile);
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
                token.ThrowIfCancellationRequested();
                if (Path.GetExtension(candidate).ToLowerInvariant() is not (".mp4" or ".mov")) continue;
                var pair = await NativeMediaService.InspectMediaForDiscoveryAsync(path, candidate, token);
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
            using var sourceDirectory = WindowsOwnedFilePublisher.PinDirectoryAncestors(Path.GetDirectoryName(sourcePath)!);
            using var sourceLease = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (inspection, confirmedCompanion) = await InspectRepairSourceAsync(sourcePath, !allDevices, token, batch);
            if (inspection.Facts is not { } source)
            {
                if (!allDevices && IsLegacyVivoPairingRequirement(inspection.Failure))
                    return new("skipped", "Legacy vivo image is outside the default Apple/Samsung repair scope.");
                return new("failed", inspection.Failure?.Reason ?? "Cannot inspect input.");
            }
            if (source.Protocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic)
            {
                var samsung = await RepairSamsungAsync(sourcePath,outputDirectory,dryRun,options,token,copyPerfect);
                batch.RecordPublished(samsung.Image, samsung.Video);
                return samsung;
            }
            if (source.Protocol == SourceProtocol.HuaweiMovingPhoto)
            {
                if (!allDevices) return new("skipped", "Skipped by default Apple/Samsung repair scope.");
                var huawei = await RepairHuaweiAsync(sourcePath, outputDirectory, dryRun, options, token, copyPerfect);
                batch.RecordPublished(huawei.Image, huawei.Video);
                return huawei;
            }
            var container = source.PrimaryImage.Container;
            bool jpeg = container == ImageContainer.Jpeg;
            bool apple = source.Protocol == SourceProtocol.AppleLivePhoto ||
                (!string.IsNullOrEmpty(source.PairingIdentifier) &&
                 (await NativePairingHint.ReadAsync(sourcePath, token))?.Protocol == SourceProtocol.AppleLivePhoto);
            bool legacyPair = source.Protocol == SourceProtocol.VivoLegacyDualFile;
            if (!apple && !allDevices)
                return new("skipped", "Default repair scope is Apple and Samsung Live Photos. Use --all-devices for supported standalone images.");
            if (container is not (ImageContainer.Jpeg or ImageContainer.Heic) ||
                (!jpeg && (source.MotionVideo?.IsPresent == true ||
                    source.Protocol is not (SourceProtocol.NonLive or SourceProtocol.AppleLivePhoto))))
                return new("failed", "The image contains a media structure this repair cannot safely change.");

            var before = await NativeMediaService.CapturePreservationObservationAsync(
                sourcePath, source.Protocol, container, token);
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
            var metadataProbe = actions == 0 ? new NativeMediaService.ImageExifRepairProbe(true, 0, null)
                : await NativeMediaService.ProbeImageExifRepairAsync(sourcePath, actions, token);
            uint metadata = limitedExif ? 0 : metadataProbe.Changes;
            if (rotate && before.Orientation > 8) { incompleteRotation = true; rotate = false; }

            string? videoPath = confirmedCompanion;
            if (apple)
            {
                foreach (var candidateEntry in await batch.CandidatesAsync(sourcePath, token, SourceProtocol.AppleLivePhoto, source.PairingIdentifier))
                {
                    string candidate = candidateEntry.Path;
                    if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                    string candidateExtension = Path.GetExtension(candidate).ToLowerInvariant();
                    if (candidateExtension is not (".mov" or ".jpg" or ".jpeg" or ".heic" or ".heif")) continue;
                    var inspected = candidateEntry.Inspection;
                    if (!string.Equals(inspected.Facts?.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (candidateExtension != ".mov")
                        return new("failed", "More than one image has this Apple pairing identifier.");
                    if (videoPath != null)
                        return new("failed", "More than one MOV has this Apple pairing identifier.");
                    videoPath = candidate;
                }
            }

            using var videoLease = videoPath == null ? null : File.Open(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var pair = videoPath == null ? null : await NativeMediaService.InspectMediaAsync(sourcePath, videoPath, token);
            if (pair != null && (pair.Protocol != (apple ? SourceProtocol.AppleLivePhoto : source.Protocol) ||
                !string.Equals(pair.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                (legacyPair && pair.SecondarySha256 != source.SecondarySha256)))
                return new("failed", "The image/video pair could not be confirmed.");

            VideoFacts? video = null;
            uint reflection = 0;
            string? videoWarning = null;
            if (videoPath != null && options.FixVideoRotation)
            {
                try
                {
                    (video, reflection) = await NativeMediaService.ProbeVideoRepairAsync(videoPath, token);
                }
                catch (InvalidOperationException ex)
                {
                    videoWarning = ex.Message;
                }
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
                    unchangedCopy = await PublishSingleCopyAsync(sourcePath, GetIsolationDirectory(outputDirectory), sourceLease, token);
                    batch.RecordPublished(unchangedCopy);
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

            using var workspace = new MediaWorkspace();
            string extension = Path.GetExtension(sourcePath);
            string staged = metadata != 0 || rotate ? workspace.AllocateFilePath("repair", extension) : sourcePath;
            NativeMediaService.NativeRegisteredOutputEvidence? evidence = null;
            if (metadata != 0)
            {
                var repaired = await NativeMediaService.RepairImageExifAsync(sourcePath, staged, actions, token);
                if (repaired.Changes != metadata || repaired.Evidence == null)
                    throw new IOException("Image repair result differed from its diagnostic plan.");
                evidence = repaired.Evidence;
            }
            if (rotate)
            {
                string rotated = metadata != 0 ? workspace.AllocateFilePath("repair", extension) : staged;
                const int normalizeExif = 7; // LPB_JPEG_TRANSFORM_NORMALIZE_EXIF
                try
                {
                    var attempt = await NativeMediaService.TransformJpegLosslesslyWithOutputEvidenceAsync(
                        metadata != 0 ? staged : sourcePath, rotated, normalizeExif, token);
                    evidence = attempt.OutputEvidence ??
                        throw new IOException("Could not verify Native JPEG repair output ownership.", attempt.OutputEvidenceFailure);
                    staged = rotated;
                }
                catch (InvalidOperationException ex)
                {
                    rotate = false;
                    incompleteRotation = true;
                    isolate = true;
                    changes.RemoveAt(0);
                    changes.Add(Text("Repair_Limited_JpegRotation", "JPEG rotation was skipped: {0}", ex.Message));
                    action = string.Join(" ", changes);
                    if (evidence == null) staged = sourcePath;
                    if (evidence == null && !rotateVideo) return new RepairResult("warning", action + " No repair completed; no unchanged copy was published.") { Actions = ActionResults(options, 0, 1, false) };
                }
            }
            using var stagedLease = File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (evidence != null) await VerifyStageAsync(stagedLease, evidence, token);

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
                        var repairedVideo = await RotateVideoAsync(videoPath!, stagedVideo, video!, token);
                        stagedVideoLease = repairedVideo.Lease;
                        videoMetadataVerified = repairedVideo.MetadataVerified;
                        isolate |= !repairedVideo.MetadataVerified;
                        action += Text("Repair_Action_PairedVideo", " Video re-encoded; primary audio retained; Apple pairing and cover rebuilt. Unrelated auxiliary tracks were omitted.");
                        if (!repairedVideo.MetadataVerified) action += Text("Repair_Limited_Preservation", " Source metadata preservation was not fully verified; output is isolated.");
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

                var after = await NativeMediaService.CapturePreservationObservationAsync(
                    staged, source.Protocol, container, token);
                var result = await NativeMediaService.InspectMediaAsync(staged, stagedVideo, token);
                if (composite && !await HasUnchangedAppendedMediaAsync(sourceLease, stagedLease, source.PrimaryImage.ByteLength, token))
                    return new("failed", "Appended media bytes or their positions changed during metadata repair.");
                bool swapsDimensions = rotate && before.Orientation >= 5;
                if ((rotate && after.Orientation != 1) || after.ExifParseError != before.ExifParseError || after.CodestreamError ||
                    (jpeg && !rotate && before.ImageCodestreamSha256 != after.ImageCodestreamSha256) ||
                    (metadata != 0 && (await NativeMediaService.RepairImageExifAsync(staged, null, actions, token)).Changes != 0) ||
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

                Directory.CreateDirectory(outputDirectory);
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

                if (videoFinal != null)
                    await WindowsSplitOutputPublisher.PublishAsync(staged, stagedVideo!, imageFinal, videoFinal, false, null, token);
                else
                    await WindowsSplitOutputPublisher.PublishSingleAsync(
                        imageFinal, false, (stream, ct) => stagedLease.CopyToAsync(stream, ct), null, token);

                batch.RecordPublished(imageFinal, videoFinal);
                return new(isolate ? "warning" : copy ? "copied" : "success", action, imageFinal, videoFinal)
                {
                    ImageStatus = evidence == null ? "copied" :
                        incompleteRotation || limitedExif || !metadataProbe.Supported ? "warning" : "success",
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
            using var workspace = new MediaWorkspace();
            string? staged = dryRun ? null : workspace.AllocateFilePath("huawei", Path.GetExtension(source));
            var repair = await NativeHuaweiRepair.RunAsync(source, staged, SamsungActions(options), token);
            if (!repair.Supported) return new RepairResult("failed", repair.Details);
            string details = Text("Repair_Action_Huawei", "已识别华为实况结构；按所选选项处理照片和内嵌视频，保留声音与 HDR 信息。") + " " + repair.Details;
            if (repair.Changed == 0)
            {
                if (repair.Pending != 0)
                {
                    details = Text("Repair_Huawei_NoAction", "文件可读取；部分所选检查或修复尚未完成，没有将未改动文件标为修复成功。") + " " + details;
                    if (!copyPerfect || dryRun) return new RepairResult(dryRun ? "planned" : "warning", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                    using var retained = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new RepairResult("warning", details, await PublishSingleCopyAsync(source, GetIsolationDirectory(directory), retained, token)) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                }
                if (!copyPerfect) return new RepairResult("healthy", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                if (dryRun) return new RepairResult("planned", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
                using var original = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                return new RepairResult("copied", details, await PublishSingleCopyAsync(source, directory, original, token)) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
            }
            if (dryRun) return new RepairResult("planned", details) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
            if (repair.Evidence == null) throw new IOException("Huawei repair has no verified output receipt.");
            using var lease = File.Open(staged!, FileMode.Open, FileAccess.Read, FileShare.Read);
            await VerifyStageAsync(lease, repair.Evidence, token);
            if (repair.Pending != 0)
            {
                directory = GetIsolationDirectory(directory);
                details += " " + Text("Repair_Huawei_Partial", "已完成可安全处理的照片或视频修复；仍有未完成检查或修复，输出至隔离目录。");
            }
            string final = await PublishSingleCopyAsync(source, directory, lease, token);
            return new RepairResult(repair.Pending != 0 ? "warning" : "success", details, final) { Actions = ActionResults(options, repair.Changed, repair.Pending, dryRun) };
        }


        private static async Task<RepairResult> RepairSamsungAsync(string source,string directory,
            bool dryRun,RepairOptions options,CancellationToken token,bool copyPerfect)
        {
            using var workspace=new MediaWorkspace();
            string? staged=dryRun?null:workspace.AllocateFilePath("samsung",Path.GetExtension(source));
            var repair=await NativeSamsungRepair.RunAsync(source,workspace.RootDirectory,staged,SamsungActions(options),token);
            if(repair.Changed==0)
            {
                if (repair.IsPartial)
                {
                    string? retainedCopy = null;
                    if (copyPerfect && !dryRun)
                    {
                        using var retained = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        retainedCopy = await PublishSingleCopyAsync(source, GetIsolationDirectory(directory), retained, token);
                    }
                    return new RepairResult(dryRun ? "planned" : "warning", repair.Details, retainedCopy)
                    { Actions = ActionResults(options, 0, SamsungActions(options) & 7u, dryRun),
                      Limitations = [new("image_preservation_unverified", "照片修复后的媒体保留检查未通过，原照片保留。") ] };
                }
                if(!copyPerfect)return new("healthy","Selected Samsung checks completed; no selected repair is needed.");
                if(dryRun)return new("planned","Copy the Samsung dynamic photo unchanged.");
                using var original=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read);
                string copy=await PublishSingleCopyAsync(source,directory,original,token);
                return new("copied","Copied the Samsung dynamic photo unchanged.",copy);
            }
            if(dryRun)return new("planned",repair.Details);
            if(repair.Evidence is null)throw new IOException("Samsung repair has no verified output receipt.");
            using var lease=File.Open(staged!,FileMode.Open,FileAccess.Read,FileShare.Read);
            await VerifyStageAsync(lease,repair.Evidence,token);
            if(repair.IsPartial)directory=GetIsolationDirectory(directory);
            string final=await PublishSingleCopyAsync(source,directory,lease,token);
            var encoded=new System.Collections.Generic.List<string>();
            if((repair.Reencoded&1)!=0)encoded.Add("photo/HDR images");
            if((repair.Reencoded&8)!=0)encoded.Add("main video");
            if((repair.Reencoded&16)!=0)encoded.Add("autoplay video");
            return new(repair.IsPartial?"warning":"success",repair.Details+" Re-encoded: "+(encoded.Count==0?"none":string.Join(", ",encoded))+".",final) { Actions = ActionResults(options, (repair.Changed & 15) | ((repair.Changed & 16) != 0 ? 8u : 0u), repair.IsPartial ? SamsungActions(options) & 7u : 0u, false),
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
            using var sourceDirectory = WindowsOwnedFilePublisher.PinDirectoryAncestors(directory);
            using var sourceLease = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = await NativeMediaService.InspectMediaAsync(sourcePath, null, token);
            var hint = string.IsNullOrEmpty(source.PairingIdentifier) ? null : await NativePairingHint.ReadAsync(sourcePath, token);
            if (hint?.Protocol == SourceProtocol.VivoLegacyDualFile)
            {
                if (!allDevices) return new("skipped", "Legacy vivo video is outside the default Apple/Samsung repair scope.");
                var candidates = await batchContext.CandidatesAsync(sourcePath, token, SourceProtocol.VivoLegacyDualFile, hint.Identifier);
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
                foreach (var candidateEntry in await batchContext.CandidatesAsync(sourcePath, token, SourceProtocol.AppleLivePhoto, source.PairingIdentifier))
                {
                    string candidate = candidateEntry.Path;
                    string ext = Path.GetExtension(candidate).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".heic" or ".heif")) continue;
                    var inspected = candidateEntry.Inspection;
                    if (!string.Equals(inspected.Facts?.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase))
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

            var (video, reflection) = await NativeMediaService.ProbeVideoRepairAsync(sourcePath, token);
            if (!isolate && !repairLongVideos && video.DurationSeconds > 3.5)
                return new("skipped", "Standalone video exceeds 3.5 seconds. Use --repair-long-videos to include it.");
            if (video.RotationDegrees == 0 && reflection == 0)
            {
                if (!copyPerfect || isolate)
                    return new("healthy", "Video orientation check completed; no normalization needed.");
                if (dryRun) return new("planned", "Copy the video unchanged.");
                string copied = await PublishSingleCopyAsync(sourcePath, outputDirectory, sourceLease, token);
                batchContext.RecordPublished(copied);
                return new("copied", "Copied the video unchanged.", Video: copied);
            }
            string action = DescribeVideoTransform(video, reflection);
            if (isolate)
            {
                action += " Apple image companion was not found. Output will be isolated.";
                outputDirectory = GetIsolationDirectory(outputDirectory);
            }
            if (dryRun) return new("planned", action);

            using var workspace = new MediaWorkspace();
            string staged = workspace.AllocateFilePath("repair", Path.GetExtension(sourcePath));
            var repairedVideo = await RotateVideoAsync(sourcePath, staged, video, token);
            using var stage = repairedVideo.Lease;
            if (!repairedVideo.MetadataVerified)
            {
                if (!isolate) outputDirectory = GetIsolationDirectory(outputDirectory);
                isolate = true;
                action += Text("Repair_Limited_Preservation", " Source metadata preservation was not fully verified; output is isolated.");
            }
            action += Text("Repair_Action_Video", " Video re-encoded; only primary audio/video and confirmed Apple pairing/cover are carried.");
            var result = await NativeMediaService.InspectMediaAsync(staged, null, token);
            if (!string.Equals(result.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(source.PairingIdentifier) && result.Timing.CoverTimestampUs != source.Timing.CoverTimestampUs))
                return new("failed", "The repaired video did not preserve the Apple pairing identifier and cover.");

            string final = await PublishSingleCopyAsync(sourcePath, outputDirectory, stage, token);
            batchContext.RecordPublished(final);
            return new(isolate ? "warning" : "success", action, Video: final) { Actions = ActionResults(options, 8, 0, false), Limitations = PairLimitations(false, false, !repairedVideo.MetadataVerified) };
        }

        private static async Task<string> PublishSingleCopyAsync(string sourcePath, string outputDirectory,
            Stream stage, CancellationToken token)
        {
            Directory.CreateDirectory(outputDirectory);
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            string final = Path.Combine(outputDirectory, stem + extension);
            for (int index = 1; Path.Exists(final); index++)
                final = Path.Combine(outputDirectory, $"{stem} ({index}){extension}");
            await WindowsSplitOutputPublisher.PublishSingleAsync(final, false,
                (stream, ct) => stage.CopyToAsync(stream, ct), null, token);
            return final;
        }

        private static string DescribeVideoTransform(VideoFacts video, uint reflection) => reflection switch
        {
            1 => Text("Repair_Action_Horizontal", "Apply the horizontal display reflection to video frames."),
            2 => Text("Repair_Action_Vertical", "Apply the vertical display reflection to video frames."),
            _ => Text("Repair_Action_Rotation", "Apply the display rotation ({0} degrees) to video frames.", video.RotationDegrees)
        };

        private sealed record VideoRepairStage(FileStream Lease, bool MetadataVerified);

        private static async Task<VideoRepairStage> RotateVideoAsync(string source, string staged, VideoFacts before,
            CancellationToken token)
        {
            if (before.Codec is not (VideoCodec.H264 or VideoCodec.Hevc))
                throw new InvalidOperationException("Video rotation requires an H.264 or HEVC input.");
            var attempt = await NativeMediaService.TranscodeVideoAttemptAsync(
                source, staged, before.Container, before.Codec, 18, token, PreservationPolicy.BestEffort, repairOrientation: true);
            token.ThrowIfCancellationRequested();
            if (!attempt.Succeeded)
                throw new InvalidOperationException(attempt.LastError ?? "Native video rotation failed.", attempt.FailureException);
            if (attempt.OutputEvidence is not { } evidence)
                throw new IOException("Could not verify Native video output ownership.", attempt.OutputEvidenceFailure);

            var lease = File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                await VerifyStageAsync(lease, evidence, token);
                var after = await NativeMediaService.ProbeVideoAsync(staged, token);
                bool swap = before.RotationDegrees is 90 or 270;
                if (after.RotationDegrees != 0 || before.HasAudio != after.HasAudio ||
                    after.Width != (swap ? before.Height : before.Width) ||
                    after.Height != (swap ? before.Width : before.Height))
                    throw new InvalidOperationException("Repaired video geometry or audio did not pass validation.");
                return new(lease, attempt.MetadataPreservation != null);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        private static async Task VerifyStageAsync(FileStream lease,
            NativeMediaService.NativeRegisteredOutputEvidence evidence, CancellationToken token)
        {
            if (!evidence.FileIdentity.Matches(WindowsFileIdentity.Capture(lease.SafeFileHandle)) ||
                lease.Length != evidence.ByteLength ||
                !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(lease, token)),
                    evidence.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged file no longer matches the Native output receipt.");
            lease.Position = 0;
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
                return await AnalyzeFileCoreAsync(filePath, token, defaultDevicesOnly, batch);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new() { IssueType = RepairIssueType.Error, IssueDescription = ex.Message };
            }
        }

        internal static async Task<RepairAnalysisResult> AnalyzeFileCoreAsync(string filePath, CancellationToken token, bool defaultDevicesOnly, RepairBatchContext batch)
        {
            token.ThrowIfCancellationRequested();
            var (discovery, _) = await InspectRepairSourceAsync(filePath, defaultDevicesOnly, token, batch);
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
            if (video)
            {
                var (observed, reflection) = await NativeMediaService.ProbeVideoRepairAsync(filePath, token);
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
                using var workspace=new MediaWorkspace();
                var repair=await NativeSamsungRepair.RunAsync(filePath,workspace.RootDirectory,null,15,token);
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
                var repair = await NativeHuaweiRepair.RunAsync(filePath, null, 15, token);
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
            var observedImage = await NativeMediaService.CapturePreservationObservationAsync(
                filePath, facts.Protocol, facts.PrimaryImage.Container, token);
            var metadataProbe = await NativeMediaService.ProbeImageExifRepairAsync(filePath, 3, token);
            uint metadata = metadataProbe.Changes;
            analysis.ImageOrientation = observedImage.Orientation;
            analysis.HasHeicOrientationConflict = (metadata & 2) != 0;
            analysis.HasThumbnail = (metadata & 1) != 0;
            analysis.DateTimeOriginal = observedImage.DateTimeOriginal;
            bool jpegRotation = facts.PrimaryImage.Container == ImageContainer.Jpeg && observedImage.Orientation > 1;
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
            if (facts.Protocol == SourceProtocol.AppleLivePhoto ||
                (!string.IsNullOrEmpty(facts.PairingIdentifier) &&
                 (await NativePairingHint.ReadAsync(filePath, token))?.Protocol == SourceProtocol.AppleLivePhoto))
            {
                var candidates = await batch.CandidatesAsync(filePath, token, SourceProtocol.AppleLivePhoto, facts.PairingIdentifier);
                int images = 0, videos = 0;
                foreach (var candidate in candidates)
                {
                    if (Path.GetExtension(candidate.Path).Equals(".mov", StringComparison.OrdinalIgnoreCase)) videos++;
                    else images++;
                }
                if (images != 1 || videos != 1)
                {
                    analysis.IssueType = RepairIssueType.Limited;
                    analysis.VideoCheckIncomplete = true;
                    analysis.IssueDescription = "照片可读取，但未能唯一确认配对视频；尚未完成视频检查。";
                    return analysis;
                }
            }
            analysis.IssueType = jpegRotation || (metadata & 2) != 0 ? RepairIssueType.NeedsRebuild
                : analysis.HasThumbnail ? RepairIssueType.NeedsStrip : RepairIssueType.Perfect;
            analysis.IssueDescription = analysis.NeedsRepair
                ? $"Orientation={observedImage.Orientation}; embedded thumbnail/preview={analysis.HasThumbnail}; HEIF orientation conflict={(metadata & 2) != 0}."
                : "No image repair needed.";
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
