using System.IO;
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
    public static class LivePhotoRepairService
    {
        internal sealed record RepairResult(string Status, string Message, string? Image = null, string? Video = null);

        // Native has recognized a legacy vivo image, but single-file inspection
        // cannot confirm its companion. This is enough to exclude it from the
        // default Apple/Samsung repair scope, not authority to repair or pair it.
        private static bool IsLegacyVivoPairingRequirement(SourceInspectionFailure? failure)
            => failure is { Category: SourceInspectionFailureCategory.Ambiguous,
                Stage: SourceInspectionStage.Pairing, Capability: NativeRuntime.VivoLegacyCapability };

        private static async Task<(SourceInspectionResult Inspection, string? Companion)> InspectRepairSourceAsync(
            string path, bool defaultDevicesOnly, CancellationToken token)
        {
            var inspection = await NativeMediaService.InspectMediaForDiscoveryAsync(path, null, token);
            if (defaultDevicesOnly || !IsLegacyVivoPairingRequirement(inspection.Failure))
                return (inspection, null);

            SourceInspectionResult? matched = null;
            string? companion = null;
            foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!))
            {
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

        internal static async Task<RepairResult> RepairImageFileAsync(
            string sourcePath, string outputDirectory, bool allDevices, bool dryRun, RepairOptions options, CancellationToken token, bool copyPerfect = false)
        {
            sourcePath = Path.GetFullPath(sourcePath);
            using var sourceDirectory = WindowsOwnedFilePublisher.PinDirectoryAncestors(Path.GetDirectoryName(sourcePath)!);
            using var sourceLease = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (inspection, confirmedCompanion) = await InspectRepairSourceAsync(sourcePath, !allDevices, token);
            if (inspection.Facts is not { } source)
            {
                if (!allDevices && IsLegacyVivoPairingRequirement(inspection.Failure))
                    return new("skipped", "Legacy vivo image is outside the default Apple/Samsung repair scope.");
                return new("failed", inspection.Failure?.Reason ?? "Cannot inspect input.");
            }
            if (source.Protocol is SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic)
                return await RepairSamsungAsync(sourcePath,outputDirectory,dryRun,options,token,copyPerfect);
            var container = source.PrimaryImage.Container;
            bool jpeg = container == ImageContainer.Jpeg;
            bool apple = source.Protocol == SourceProtocol.AppleLivePhoto;
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
            if (before.ExifParseError || before.CodestreamError)
                return new("failed", "Image EXIF or codestream cannot be verified.");

            uint actions = (options.StripImageThumbnail ? 1u : 0u) |
                (!jpeg && options.FixHeicOrientation ? 2u : 0u);
            var metadataProbe = actions == 0 ? new NativeMediaService.ImageExifRepairProbe(true, 0, null)
                : await NativeMediaService.ProbeImageExifRepairAsync(sourcePath, actions, token);
            uint metadata = metadataProbe.Changes;
            if (rotate && (before.Orientation > 8 || before.HasExtendedXmp))
                return new("failed", "JPEG has competing or unsupported orientation metadata.");

            string? videoPath = confirmedCompanion;
            if (apple)
            {
                foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(sourcePath)!))
                {
                    if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                    string candidateExtension = Path.GetExtension(candidate).ToLowerInvariant();
                    if (candidateExtension is not (".mov" or ".jpg" or ".jpeg" or ".heic" or ".heif")) continue;
                    var inspected = await NativeMediaService.InspectMediaForDiscoveryAsync(candidate, null, token);
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
            if (pair != null && (pair.Protocol != source.Protocol ||
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
                catch (InvalidOperationException ex) when (rotate || metadata != 0)
                {
                    videoWarning = ex.Message;
                }
            }
            bool rotateVideo = reflection != 0 || video?.RotationDegrees is 90 or 180 or 270;
            bool incompletePairedVideo = legacyPair && rotateVideo;
            if (incompletePairedVideo) rotateVideo = false;
            bool copy = !rotate && metadata == 0 && !rotateVideo;
            bool uncheckedEmbeddedVideo = videoPath == null && options.FixVideoRotation && source.MotionVideo?.IsPresent == true;
            bool incomplete = incompleteRotation || incompletePairedVideo || uncheckedEmbeddedVideo || !metadataProbe.Supported;
            if (copy && !incomplete && (!copyPerfect || (apple && videoPath == null)))
                return new("skipped", "No selected repair is needed.");

            var changes = new System.Collections.Generic.List<string>(4);
            if (rotate) changes.Add($"Apply EXIF orientation {before.Orientation} losslessly and reset it to 1.");
            if ((metadata & 1) != 0) changes.Add("Remove confirmed embedded thumbnails and previews.");
            if ((metadata & 2) != 0) changes.Add("Align primary EXIF orientation with the HEIF display transform.");
            if (rotateVideo) changes.Add(DescribeVideoTransform(video!, reflection));
            if (copy) changes.Add("Copy the image and any confirmed companion unchanged.");
            if (incompleteRotation) changes.Add("Image rotation was not performed: attached media requires structural reassembly. Original orientation and attached media are retained.");
            if (incompletePairedVideo) changes.Add("Paired video rotation was not performed; the original video and its pairing metadata are retained.");
            if (uncheckedEmbeddedVideo) changes.Add("Embedded video orientation was not repaired; its original bytes are retained.");
            if (!metadataProbe.Supported) changes.Add($"Thumbnail/orientation metadata repair could not be verified: {metadataProbe.Diagnostic}");
            bool isolate = incomplete || (apple && videoPath == null) || videoWarning != null ||
                (rotateVideo && pair?.Timing.CoverTimestampUs < 0);
            if (apple && videoPath == null) changes.Add("Apple MOV companion was not found. Output will be isolated.");
            if (videoWarning != null) changes.Add($"MOV rotation could not be verified: {videoWarning}");
            if (rotateVideo && pair?.Timing.CoverTimestampUs < 0)
                changes.Add("Source cover time is unknown. No cover timestamp will be invented.");
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
                catch (InvalidOperationException ex) when (evidence != null)
                {
                    rotate = false;
                    isolate = true;
                    changes.RemoveAt(0);
                    changes.Add($"JPEG rotation was skipped: {ex.Message}");
                    action = string.Join(" ", changes);
                }
            }
            using var stagedLease = File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (evidence != null) await VerifyStageAsync(stagedLease, evidence, token);

            string? stagedVideo = videoPath;
            FileStream? stagedVideoLease = null;
            try
            {
                if (rotateVideo)
                {
                    stagedVideo = workspace.AllocateFilePath("repair", Path.GetExtension(videoPath!));
                    try
                    {
                        stagedVideoLease = await RotateVideoAsync(videoPath!, stagedVideo, video!, PreservationPolicy.Strict, token);
                        action += " Video re-encoded; primary audio retained; Apple pairing and cover rebuilt. Unrelated auxiliary tracks were omitted.";
                    }
                    catch (InvalidOperationException ex) when (evidence != null)
                    {
                        stagedVideo = videoPath;
                        rotateVideo = false;
                        isolate = true;
                        action += $" Video rotation could not be verified: {ex.Message}";
                    }
                }
                if (isolate) outputDirectory = Path.Combine(outputDirectory, "_isolated");

                var after = await NativeMediaService.CapturePreservationObservationAsync(
                    staged, source.Protocol, container, token);
                var result = await NativeMediaService.InspectMediaAsync(staged, stagedVideo, token);
                if (composite && !await HasUnchangedAppendedMediaAsync(sourceLease, stagedLease, source.PrimaryImage.ByteLength, token))
                    return new("failed", "Appended media bytes or their positions changed during metadata repair.");
                bool swapsDimensions = rotate && before.Orientation >= 5;
                if ((rotate && after.Orientation != 1) || after.ExifParseError || after.CodestreamError ||
                    (jpeg && !rotate && before.ImageCodestreamSha256 != after.ImageCodestreamSha256) ||
                    (metadata != 0 && (await NativeMediaService.RepairImageExifAsync(staged, null, actions, token)).Changes != 0) ||
                    before.GpsSha256 != after.GpsSha256 || before.IccSha256 != after.IccSha256 ||
                    before.MakernoteNonliveSha256 != after.MakernoteNonliveSha256 ||
                    result.PrimaryImage.Width != (swapsDimensions ? source.PrimaryImage.Height : source.PrimaryImage.Width) ||
                    result.PrimaryImage.Height != (swapsDimensions ? source.PrimaryImage.Width : source.PrimaryImage.Height) ||
                    !string.Equals(result.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                    result.Protocol != source.Protocol ||
                    (pair != null && (result.Protocol != pair.Protocol ||
                        result.Timing.CoverTimestampUs != pair.Timing.CoverTimestampUs ||
                        (!rotateVideo && result.SecondarySha256 != pair.SecondarySha256))))
                    return new("failed", "The staged image or Apple pair did not pass repair validation.");

                Directory.CreateDirectory(outputDirectory);
                string stem = Path.GetFileNameWithoutExtension(sourcePath) + "_repaired";
                string imageFinal;
                string? videoFinal;
                int index = 1;
                do
                {
                    string name = index == 1 ? stem : $"{stem} ({index})";
                    imageFinal = Path.Combine(outputDirectory, name + Path.GetExtension(sourcePath));
                    videoFinal = videoPath == null ? null : Path.Combine(outputDirectory, name + Path.GetExtension(videoPath));
                    index++;
                }
                while (Path.Exists(imageFinal) || (videoFinal != null && Path.Exists(videoFinal)));

                if (videoFinal != null)
                    await WindowsSplitOutputPublisher.PublishAsync(staged, stagedVideo!, imageFinal, videoFinal, false, null, token);
                else
                    await WindowsSplitOutputPublisher.PublishSingleAsync(
                        imageFinal, false, (stream, ct) => stagedLease.CopyToAsync(stream, ct), null, token);

                return new(isolate ? "warning" : copy ? "copied" : "success", action, imageFinal, videoFinal);
            }
            finally
            {
                stagedVideoLease?.Dispose();
            }
        }

        private static uint SamsungActions(RepairOptions options) =>
            (options.FixImageRotation?1u:0u)|(options.StripImageThumbnail?2u:0u)|
            (options.FixHeicOrientation?4u:0u)|(options.FixVideoRotation?8u:0u);

        private static async Task<RepairResult> RepairSamsungAsync(string source,string directory,
            bool dryRun,RepairOptions options,CancellationToken token,bool copyPerfect)
        {
            using var workspace=new MediaWorkspace();
            string? staged=dryRun?null:workspace.AllocateFilePath("samsung",Path.GetExtension(source));
            var repair=await NativeSamsungRepair.RunAsync(source,workspace.RootDirectory,staged,SamsungActions(options),token);
            if(repair.Changed==0)
            {
                if(!copyPerfect)return new("skipped","No selected Samsung repair is needed.");
                if(dryRun)return new("planned","Copy the Samsung dynamic photo unchanged.");
                using var original=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read);
                string copy=await PublishSingleCopyAsync(source,directory,original,token);
                return new("copied","Copied the Samsung dynamic photo unchanged.",copy);
            }
            if(dryRun)return new("planned",repair.Details);
            if(repair.Evidence is null)throw new IOException("Samsung repair has no verified output receipt.");
            using var lease=File.Open(staged!,FileMode.Open,FileAccess.Read,FileShare.Read);
            await VerifyStageAsync(lease,repair.Evidence,token);
            if(repair.IsPartial)directory=Path.Combine(directory,"_isolated");
            string final=await PublishSingleCopyAsync(source,directory,lease,token);
            var encoded=new System.Collections.Generic.List<string>();
            if((repair.Reencoded&1)!=0)encoded.Add("photo/HDR images");
            if((repair.Reencoded&8)!=0)encoded.Add("main video");
            if((repair.Reencoded&16)!=0)encoded.Add("autoplay video");
            return new(repair.IsPartial?"warning":"success",repair.Details+" Re-encoded: "+(encoded.Count==0?"none":string.Join(", ",encoded))+".",final);
        }

        internal static async Task<RepairResult> RepairVideoFileAsync(string sourcePath, string outputDirectory,
            bool allDevices, bool repairLongVideos, bool dryRun, bool batch, RepairOptions options, CancellationToken token,
            bool copyPerfect = false)
        {
            if (!options.FixVideoRotation) return new("skipped", "Video rotation repair was disabled.");
            sourcePath = Path.GetFullPath(sourcePath);
            string directory = Path.GetDirectoryName(sourcePath)!;
            using var sourceDirectory = WindowsOwnedFilePublisher.PinDirectoryAncestors(directory);
            using var sourceLease = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = await NativeMediaService.InspectMediaAsync(sourcePath, null, token);
            bool isolate = !string.IsNullOrEmpty(source.PairingIdentifier);
            if (isolate)
            {
                string? imagePath = null;
                foreach (string candidate in Directory.EnumerateFiles(directory))
                {
                    string ext = Path.GetExtension(candidate).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".heic" or ".heif")) continue;
                    var inspected = await NativeMediaService.InspectMediaForDiscoveryAsync(candidate, null, token);
                    if (!string.Equals(inspected.Facts?.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (imagePath != null) return new("failed", "More than one image has this Apple pairing identifier.");
                    imagePath = candidate;
                }
                if (imagePath != null)
                    return batch ? new("skipped", "Apple MOV is processed with its paired image.")
                        : await RepairImageFileAsync(imagePath, outputDirectory, allDevices, dryRun, options, token);
            }
            else if (!allDevices)
                return new("skipped", "Default repair scope is Apple and Samsung Live Photos. Use --all-devices for supported standalone videos.");

            var (video, reflection) = await NativeMediaService.ProbeVideoRepairAsync(sourcePath, token);
            if (!isolate && !repairLongVideos && video.DurationSeconds > 3.5)
                return new("skipped", "Standalone video exceeds 3.5 seconds. Use --repair-long-videos to include it.");
            if (video.RotationDegrees == 0 && reflection == 0)
            {
                if (!copyPerfect || isolate)
                    return new("skipped", "No video orientation normalization needed.");
                if (dryRun) return new("planned", "Copy the video unchanged.");
                string copied = await PublishSingleCopyAsync(sourcePath, outputDirectory, sourceLease, token);
                return new("copied", "Copied the video unchanged.", Video: copied);
            }
            string action = DescribeVideoTransform(video, reflection);
            if (isolate)
            {
                action += " Apple image companion was not found. Output will be isolated.";
                outputDirectory = Path.Combine(outputDirectory, "_isolated");
            }
            if (dryRun) return new("planned", action);

            using var workspace = new MediaWorkspace();
            string staged = workspace.AllocateFilePath("repair", Path.GetExtension(sourcePath));
            FileStream repairedStage;
            try
            {
                repairedStage = await RotateVideoAsync(sourcePath, staged, video, PreservationPolicy.Strict, token);
            }
            catch (InvalidOperationException ex) when (!isolate &&
                ex.Message.Contains("movie/track/media header metadata differs", StringComparison.Ordinal))
            {
                // The checked Libav transform can still preserve display geometry.
                // Publish the best-effort artifact only in isolation.
                repairedStage = await RotateVideoAsync(sourcePath, staged, video, PreservationPolicy.BestEffort, token);
                isolate = true;
                outputDirectory = Path.Combine(outputDirectory, "_isolated");
                action += " Some source metadata could not be verified.";
            }
            using var stage = repairedStage;
            action += " Video re-encoded; only primary audio/video and confirmed Apple pairing/cover are carried.";
            var result = await NativeMediaService.InspectMediaAsync(staged, null, token);
            if (!string.Equals(result.PairingIdentifier, source.PairingIdentifier, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(source.PairingIdentifier) && result.Timing.CoverTimestampUs != source.Timing.CoverTimestampUs))
                return new("failed", "The repaired video did not preserve the Apple pairing identifier and cover.");

            string final = await PublishSingleCopyAsync(sourcePath, outputDirectory, stage, token);
            return new(isolate ? "warning" : "success", action, Video: final);
        }

        private static async Task<string> PublishSingleCopyAsync(string sourcePath, string outputDirectory,
            Stream stage, CancellationToken token)
        {
            Directory.CreateDirectory(outputDirectory);
            string stem = Path.GetFileNameWithoutExtension(sourcePath) + "_repaired";
            string extension = Path.GetExtension(sourcePath);
            string final = Path.Combine(outputDirectory, stem + extension);
            for (int index = 2; Path.Exists(final); index++)
                final = Path.Combine(outputDirectory, $"{stem} ({index}){extension}");
            await WindowsSplitOutputPublisher.PublishSingleAsync(final, false,
                (stream, ct) => stage.CopyToAsync(stream, ct), null, token);
            return final;
        }

        private static string DescribeVideoTransform(VideoFacts video, uint reflection) => reflection switch
        {
            1 => "Apply the horizontal display reflection to video frames.",
            2 => "Apply the vertical display reflection to video frames.",
            _ => $"Apply the display rotation ({video.RotationDegrees} degrees) to video frames."
        };

        private static async Task<FileStream> RotateVideoAsync(string source, string staged, VideoFacts before,
            PreservationPolicy policy, CancellationToken token)
        {
            if (before.Codec is not (VideoCodec.H264 or VideoCodec.Hevc))
                throw new InvalidOperationException("Video rotation requires an H.264 or HEVC input.");
            var attempt = await NativeMediaService.TranscodeVideoAttemptAsync(
                source, staged, before.Container, before.Codec, 18, token, policy, repairOrientation: true);
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
                return lease;
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
            try
            {
                return await AnalyzeFileCoreAsync(filePath, token, defaultDevicesOnly);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                return new() { IssueType = RepairIssueType.Error, IssueDescription = ex.Message };
            }
        }

        private static async Task<RepairAnalysisResult> AnalyzeFileCoreAsync(string filePath, CancellationToken token, bool defaultDevicesOnly)
        {
            token.ThrowIfCancellationRequested();
            var (discovery, _) = await InspectRepairSourceAsync(filePath, defaultDevicesOnly, token);
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
                analysis.NeedsImageOrientationRepair=(repair.Changed&5)!=0;
                analysis.RotationAngle=repair.ImageOrientation switch {3 or 4=>180,5 or 6=>90,7 or 8=>270,_=>0};
                analysis.AutoplayVideoRotationAngle=(int)repair.AutoplayRotation;
                analysis.NeedsEmbeddedVideoTransform=(repair.Changed&8)!=0;
                analysis.HasThumbnail=(repair.Changed&2)!=0;
                analysis.VideoRotationAngle=(int)repair.MainRotation;
                analysis.IssueType=repair.Changed!=0?RepairIssueType.NeedsRebuild:RepairIssueType.Perfect;
                analysis.IssueDescription=repair.Details;
                return analysis;
            }
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
            if (!metadataProbe.Supported)
            {
                analysis.IssueType = RepairIssueType.Limited;
                analysis.IssueDescription = $"照片可读取，但无法确认安全的元数据修复方法。未将其判为损坏或无需修复。{metadataProbe.Diagnostic}";
                return analysis;
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
            return (result.Status is "success" or "copied" or "warning",
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
