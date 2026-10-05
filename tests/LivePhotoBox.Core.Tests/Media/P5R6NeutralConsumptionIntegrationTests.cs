using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Interop;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Core.Tests;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P5R6NeutralConsumptionIntegrationTests
{
    private static readonly object s_publicationEvidenceLock = new();

    private const string VivoImageSha256 = "751A39426D618404D144D6FC061CEE7FFDA389396516A11CAE1C926B056D47E6";
    private const string VivoVideoSha256 = "21C45D75AF6738A52DB0EAAFE383FCED216FA3367A3AB159191F45869A7A60D9";
    private const string RedmiImageSha256 = "48C7490F30C25D5F9E7CFC763D766BA3FDEDA8B12AA72A38426167233276C477";
    private const string OppoImageSha256 = "9C6E3FA8926660D066D1208EF99E9AB7D6A30E4B18905C5CA2C0221C9424BC0F";
    private const string P4SourceManifestSha256 = "7149B13D84A305450704722475A4ED670DACD8D258F305051E54479C603C50DE";

    public static TheoryData<int, int, ImageContainer, VideoContainer, VideoCodec> MergeRoutes => new()
    {
        { 2, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy },
        { 2, 1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Copy },
        { 6, 2, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Copy },
        { 2, 3, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Copy },
        { 6, 4, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Hevc }
    };

    public static TheoryData<string, int, ImageContainer, VideoContainer, VideoCodec> SplitNeutralRoutes => new()
    {
        { "红米老款-GV1.JPG", ProtocolFormatMatrix.SplitFormatKeep, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.H264 },
        { "oppo.jpg", ProtocolFormatMatrix.SplitFormatJpgMov, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Hevc },
        { "oppo.jpg", ProtocolFormatMatrix.SplitFormatHeicMov, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Hevc },
        { "红米老款-GV1.JPG", ProtocolFormatMatrix.SplitFormatJpgMp4, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.H264 }
    };

    public static TheoryData<int, int> SplitTargetWriterRows => new()
    {
        { ProtocolFormatMatrix.SplitProtocolApple, ProtocolFormatMatrix.SplitFormatJpgMov },
        { ProtocolFormatMatrix.SplitProtocolApple, ProtocolFormatMatrix.SplitFormatHeicMov },
        { ProtocolFormatMatrix.SplitProtocolVivo, ProtocolFormatMatrix.SplitFormatJpgMp4 }
    };

    [Theory]
    [MemberData(nameof(MergeRoutes))]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_MergeNeutral_FiveDistinctTuples_ReachCanonicalConsumerAndPublicCaller(
        int protocolIndex,
        int formatIndex,
        ImageContainer imageContainer,
        VideoContainer videoContainer,
        VideoCodec videoCodec)
    {
        (string imagePath, string videoPath) = ResolveFrozenVivoPair();
        string imageBefore = await ComputeSha256Async(imagePath);
        string videoBefore = await ComputeSha256Async(videoPath);
        Assert.Equal(VivoImageSha256, imageBefore);
        Assert.Equal(VivoVideoSha256, videoBefore);

        MediaFormatRequirement requirement = ProtocolMediaRequirements.GetMergeRequirement(protocolIndex, formatIndex);
        Assert.Equal(imageContainer, requirement.ImageContainer);
        Assert.Equal(videoContainer, requirement.VideoContainer);
        Assert.Equal(videoCodec, requirement.VideoCodec);
        Assert.Null(requirement.TargetFps);
        Assert.True(requirement.KeepSourceIfSame);

        using (var workspace = new MediaWorkspace())
        {
            var imageConverter = new CapturingImageConverter(new ImageConverter());
            var videoConverter = new CapturingVideoConverter(new VideoConverter());
            var neutralService = new NeutralMediaService(
                imageConverter: imageConverter,
                videoConverter: videoConverter);

            NeutralMediaBundle bundle = await neutralService.CreateNeutralBundleAsync(
                imagePath,
                videoPath,
                workspace,
                requirement,
                PreservationPolicy.BestEffort,
                CancellationToken.None);

            Assert.Equal(imageContainer, bundle.PrimaryImage.ImageContainer);
            Assert.NotNull(bundle.MotionVideo);
            Assert.Equal(videoContainer, bundle.MotionVideo!.VideoContainer);
            Assert.Equal(videoCodec == VideoCodec.Copy ? VideoCodec.H264 : VideoCodec.Hevc, bundle.MotionVideo.VideoCodec);
            Assert.Null(bundle.GainMap);
            Assert.Equal(GainMapRepresentation.None, bundle.GainMapRepresentation);
            Assert.False(bundle.SourceProvenance.GainMap?.IsPresent ?? false);

            await AssertNeutralManifestAsync(bundle, bundle.PrimaryImage, "PrimaryImage");
            await AssertNeutralManifestAsync(bundle, bundle.MotionVideo!, "MotionVideo");
            Assert.Equal(2, bundle.Manifest.Count);
            Assert.Empty(bundle.AuxiliaryMedia);

            // Reinspection is supplemental evidence that the final image no longer exposes
            // the source protocol binding. R1/R2 independent evidence remains authoritative.
            SourceMediaFacts neutralFacts = await new SourceInspector().InspectAsync(
                bundle.PrimaryImage.Path,
                cancellationToken: CancellationToken.None);
            Assert.Equal(SourceProtocol.NonLive, neutralFacts.Protocol);
            Assert.Null(neutralFacts.MotionVideo);
            Assert.Equal(0, neutralFacts.ProtocolTailLength);
            Assert.Null(neutralFacts.PairingIdentifier);

            AssertImageDispatch(imageConverter, imageContainer);
            AssertVideoDispatch(videoConverter, videoContainer, videoCodec);
        }

        Assert.Equal(imageBefore, await ComputeSha256Async(imagePath));
        Assert.Equal(videoBefore, await ComputeSha256Async(videoPath));

        using (var callerWorkspace = new MediaWorkspace())
        {
            string outputExtension = imageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
            string outputPath = Path.Combine(callerWorkspace.RootDirectory, "p5r6-merge-output" + outputExtension);
            string tempDirectory = Path.Combine(callerWorkspace.RootDirectory, "merge-temp");
            var options = new LivePhotoMergeRunOptions
            {
                OutputDirectory = callerWorkspace.RootDirectory,
                SelectedModeIndex = protocolIndex,
                OutputFormatIndex = formatIndex,
                OutputFilePath = outputPath,
                NamingRuleIndex = 0,
                MaxDegreeOfParallelism = 1,
                TaskStartInterval = TimeSpan.Zero,
                OverwriteExisting = false
            };

            (bool isSuccess, string details) = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
                imagePath,
                videoPath,
                "p5r6-neutral-merge",
                taskIndex: 0,
                options,
                tempDirectory,
                CancellationToken.None);

            if (protocolIndex == 2 && formatIndex == 3)
            {
                Assert.False(isSuccess, details);
                Assert.Contains("HEIC XMP injection is not supported in the Rebuilt Native engine.", details, StringComparison.Ordinal);
                Assert.False(File.Exists(outputPath));
            }
            else
            {
                Assert.True(isSuccess, details);
                Assert.True(File.Exists(outputPath));
                Assert.True(new FileInfo(outputPath).Length > 0);
            }
        }

        Assert.Equal(imageBefore, await ComputeSha256Async(imagePath));
        Assert.Equal(videoBefore, await ComputeSha256Async(videoPath));
    }

    [Theory]
    [MemberData(nameof(SplitNeutralRoutes))]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitNeutral_FourNoneTuples_ReachCanonicalConsumerAndPublicCaller(
        string sampleName,
        int formatIndex,
        ImageContainer expectedImageContainer,
        VideoContainer expectedVideoContainer,
        VideoCodec expectedVideoCodec)
    {
        string sourcePath = ResolveFrozenSample(sampleName);
        CorpusSample sourceRecord = RealSampleCorpusManifest.GetRequiredSample(sampleName);
        string sourceBefore = await ComputeSha256Async(sourcePath);
        Assert.Equal(NormalizeHash(sourceRecord.Sha256), sourceBefore);

        MediaFormatRequirement requirement = ProtocolMediaRequirements.GetSplitRequirement(
            ProtocolFormatMatrix.SplitProtocolNone,
            formatIndex);
        Assert.Equal(
            formatIndex == ProtocolFormatMatrix.SplitFormatKeep ? ImageContainer.Unknown : expectedImageContainer,
            requirement.ImageContainer);
        Assert.Equal(
            formatIndex == ProtocolFormatMatrix.SplitFormatKeep ? VideoContainer.Unknown : expectedVideoContainer,
            requirement.VideoContainer);
        Assert.Equal(
            formatIndex == ProtocolFormatMatrix.SplitFormatKeep ? VideoCodec.Copy : expectedVideoCodec,
            requirement.VideoCodec);
        Assert.Null(requirement.TargetFps);
        Assert.True(requirement.KeepSourceIfSame);

        using (var workspace = new MediaWorkspace())
        {
            var imageConverter = new CapturingImageConverter(new ImageConverter());
            var videoConverter = new CapturingVideoConverter(new VideoConverter());
            var neutralService = new NeutralMediaService(
                imageConverter: imageConverter,
                videoConverter: videoConverter);

            NeutralMediaBundle bundle = await neutralService.CreateNeutralBundleAsync(
                sourcePath,
                secondaryPath: null,
                workspace,
                requirement,
                PreservationPolicy.BestEffort,
                CancellationToken.None);

            Assert.Equal(expectedImageContainer, bundle.PrimaryImage.ImageContainer);
            Assert.NotNull(bundle.MotionVideo);
            Assert.Equal(expectedVideoContainer, bundle.MotionVideo!.VideoContainer);
            Assert.Equal(expectedVideoCodec, bundle.MotionVideo.VideoCodec);
            Assert.Null(bundle.GainMap);
            Assert.Equal(GainMapRepresentation.None, bundle.GainMapRepresentation);
            await AssertNeutralManifestAsync(bundle, bundle.PrimaryImage, "PrimaryImage");
            await AssertNeutralManifestAsync(bundle, bundle.MotionVideo!, "MotionVideo");
            Assert.Equal(2, bundle.Manifest.Count);

            SourceMediaFacts neutralFacts = await new SourceInspector().InspectAsync(
                bundle.PrimaryImage.Path,
                cancellationToken: CancellationToken.None);
            Assert.Equal(SourceProtocol.NonLive, neutralFacts.Protocol);
            Assert.Null(neutralFacts.MotionVideo);
            Assert.Equal(0, neutralFacts.ProtocolTailLength);
            Assert.Null(neutralFacts.PairingIdentifier);

            AssertSplitImageDispatch(imageConverter, expectedImageContainer);
            AssertSplitVideoDispatch(videoConverter, expectedVideoContainer, expectedVideoCodec);

            if (sampleName == "oppo.jpg" && formatIndex == ProtocolFormatMatrix.SplitFormatHeicMov)
            {
                await PersistNeutralHeicInputIfRequestedAsync(bundle.PrimaryImage.Path, sampleName, formatIndex);
            }
        }

        Assert.Equal(sourceBefore, await ComputeSha256Async(sourcePath));

        string routeId = GetSplitRouteId(sampleName, formatIndex);
        string outputDirectory = ResolveSplitOutputDirectory(routeId, out MediaWorkspace? ephemeralOutputWorkspace);
        using (ephemeralOutputWorkspace)
        {
            if (ephemeralOutputWorkspace == null)
            {
                Directory.CreateDirectory(outputDirectory);
                Assert.Empty(Directory.EnumerateFiles(outputDirectory));
            }

            LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
                sourcePath,
                outputDirectory,
                ProtocolFormatMatrix.SplitProtocolNone,
                formatIndex,
                CancellationToken.None,
                outputBaseName: $"p5r6-{routeId}",
                overwriteExisting: false);

            Assert.True(File.Exists(result.ImageOutputPath));
            Assert.True(File.Exists(result.VideoOutputPath));
            Assert.True(new FileInfo(result.ImageOutputPath).Length > 0);
            Assert.True(new FileInfo(result.VideoOutputPath).Length > 0);
            Assert.Equal(expectedImageContainer == ImageContainer.Heic ? ".HEIC" : ".JPG", Path.GetExtension(result.ImageOutputPath), ignoreCase: true);
            Assert.Equal(expectedVideoContainer == VideoContainer.Mov ? ".MOV" : ".MP4", Path.GetExtension(result.VideoOutputPath), ignoreCase: true);

            SourceMediaFacts outputImageFacts = await new SourceInspector().InspectAsync(
                result.ImageOutputPath,
                cancellationToken: CancellationToken.None);
            Assert.Equal(expectedImageContainer, outputImageFacts.PrimaryImage.Container);
            Assert.Equal(SourceProtocol.NonLive, outputImageFacts.Protocol);
            Assert.Null(outputImageFacts.MotionVideo);
            Assert.Equal(0, outputImageFacts.ProtocolTailLength);
            Assert.Null(outputImageFacts.PairingIdentifier);

            VideoFacts outputVideoFacts = await new VideoConverter().ProbeAsync(
                result.VideoOutputPath,
                CancellationToken.None);
            Assert.True(outputVideoFacts.IsPresent);
            Assert.Equal(expectedVideoContainer, outputVideoFacts.Container);
            Assert.Equal(expectedVideoCodec, outputVideoFacts.Codec);
        }

        Assert.Equal(sourceBefore, await ComputeSha256Async(sourcePath));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_TargetFps_PropagatesUnsupportedWithoutPublishingVideoOutput()
    {
        string sourcePath = ResolveFrozenSample("oppo.jpg");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        var requirement = new MediaFormatRequirement
        {
            ImageContainer = ImageContainer.Unknown,
            VideoContainer = VideoContainer.Unknown,
            VideoCodec = VideoCodec.Copy,
            TargetFps = 60,
            KeepSourceIfSame = true
        };

        using var workspace = new MediaWorkspace();
        var videoConverter = new CapturingVideoConverter(new VideoConverter());
        var neutralService = new NeutralMediaService(videoConverter: videoConverter);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            neutralService.CreateNeutralBundleAsync(
                sourcePath,
                secondaryPath: null,
                workspace,
                requirement,
                PreservationPolicy.BestEffort,
                CancellationToken.None));

        Assert.Contains("TargetFps", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceBefore, await ComputeSha256Async(sourcePath));
        var call = Assert.Single(videoConverter.Calls);
        Assert.Equal(VideoContainer.Mp4, call.Request.SourceArtifact.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, call.Request.SourceArtifact.VideoCodec);
        Assert.Equal(VideoContainer.Mp4, call.Request.TargetContainer);
        Assert.Equal(VideoCodec.Copy, call.Request.TargetCodec);
        Assert.Equal(60, call.Request.TargetFps);
        Assert.False(call.Result.Success);
        Assert.Null(call.Result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.Unsupported, call.Result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, call.Result.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(ConversionFailureStage.InvalidRequest, call.Result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(PreservationOutcome.Unsupported, call.Result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Theory]
    [MemberData(nameof(SplitTargetWriterRows))]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitTargetProtocolRows_RejectBeforeWorkspaceOrOutputCreation(
        int protocolIndex,
        int formatIndex)
    {
        Assert.True(ProtocolFormatMatrix.IsSplitAvailable(protocolIndex, formatIndex));
        using var workspace = new MediaWorkspace();
        string sourcePath = Path.Combine(workspace.RootDirectory, "missing-real-sample.jpg");
        string outputDirectory = Path.Combine(workspace.RootDirectory, "not-created-output");

        await Assert.ThrowsAsync<NotSupportedException>(() => LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            protocolIndex,
            formatIndex,
            CancellationToken.None));

        Assert.False(Directory.Exists(outputDirectory));
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_OverwriteSuccess_ReplacesBothAndPreservesSource()
    {
        const string baseName = "p5r6-publication-overwrite";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("overwrite-success");
        string imageTarget = Path.Combine(outputDirectory, baseName + ".JPG");
        string videoTarget = Path.Combine(outputDirectory, baseName + ".MP4");
        byte[] oldImage = [0x50, 0x35, 0x52, 0x36, 0x2D, 0x49, 0x4D, 0x47];
        byte[] oldVideo = [0x50, 0x35, 0x52, 0x36, 0x2D, 0x56, 0x49, 0x44];
        WriteNewFile(imageTarget, oldImage);
        WriteNewFile(videoTarget, oldVideo);
        string oldImageHash = await ComputeSha256Async(imageTarget);
        string oldVideoHash = await ComputeSha256Async(videoTarget);
        WindowsFileIdentity imageIdentityBefore = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityBefore = WindowsFileIdentity.Capture(videoTarget);
        var transactionEvidence = new List<SplitPublicationTargetEvidence>();
        using IDisposable hook = LivePhotoSplitService.SetSplitPublicationTimingTestHook(
            outputDirectory,
            (trace, _) =>
            {
                if (trace.Boundary == SplitPublicationBoundary.BeforeCommitBarrier)
                    transactionEvidence.Add(trace.Evidence);
                return Task.CompletedTask;
            });

        LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: true);

        Assert.Equal(imageTarget, Path.GetFullPath(result.ImageOutputPath));
        Assert.Equal(videoTarget, Path.GetFullPath(result.VideoOutputPath));
        await AssertSplitKeepOutputsAreValidAsync(result);
        string imageAfter = await ComputeSha256Async(imageTarget);
        string videoAfter = await ComputeSha256Async(videoTarget);
        WindowsFileIdentity imageIdentityAfter = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityAfter = WindowsFileIdentity.Capture(videoTarget);
        Assert.NotEqual(oldImageHash, imageAfter);
        Assert.NotEqual(oldVideoHash, videoAfter);
        Assert.Equal(imageIdentityBefore.VolumeSerialNumber, imageIdentityAfter.VolumeSerialNumber);
        Assert.Equal(imageIdentityBefore.FileIndex, imageIdentityAfter.FileIndex);
        Assert.Equal(imageIdentityBefore.FileAttributes, imageIdentityAfter.FileAttributes);
        Assert.Equal(1u, imageIdentityAfter.LinkCount);
        Assert.Equal(videoIdentityBefore.VolumeSerialNumber, videoIdentityAfter.VolumeSerialNumber);
        Assert.Equal(videoIdentityBefore.FileIndex, videoIdentityAfter.FileIndex);
        Assert.Equal(videoIdentityBefore.FileAttributes, videoIdentityAfter.FileAttributes);
        Assert.Equal(1u, videoIdentityAfter.LinkCount);
        string sourceAfter = await ComputeSha256Async(sourcePath);
        Assert.Equal(sourceBefore, sourceAfter);

        SplitPublicationTargetEvidence imageEvidence = Assert.Single(
            transactionEvidence,
            item => string.Equals(item.FinalPath, imageTarget, StringComparison.OrdinalIgnoreCase));
        SplitPublicationTargetEvidence videoEvidence = Assert.Single(
            transactionEvidence,
            item => string.Equals(item.FinalPath, videoTarget, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(imageIdentityBefore.FileIndex, imageEvidence.BaselineIdentity!.FileIndex);
        Assert.Equal(imageIdentityBefore.FileIndex, imageEvidence.FinalIdentity!.FileIndex);
        Assert.Equal(imageAfter, imageEvidence.FinalSha256);
        Assert.Equal(imageAfter, imageEvidence.StageSha256);
        AssertOwnedObjectCleanupProof(imageEvidence.StageIdentity, imageEvidence.StageCleanupProof, "image stage");
        Assert.Equal("Unlinked", imageEvidence.BackupCleanupProof);
        Assert.Equal(videoIdentityBefore.FileIndex, videoEvidence.BaselineIdentity!.FileIndex);
        Assert.Equal(videoIdentityBefore.FileIndex, videoEvidence.FinalIdentity!.FileIndex);
        Assert.Equal(videoAfter, videoEvidence.FinalSha256);
        Assert.Equal(videoAfter, videoEvidence.StageSha256);
        AssertOwnedObjectCleanupProof(videoEvidence.StageIdentity, videoEvidence.StageCleanupProof, "video stage");
        Assert.Equal("Unlinked", videoEvidence.BackupCleanupProof);
        AssertNoSplitTransactionArtifacts(outputDirectory);
        string[] remaining = GetFileNames(outputDirectory);
        Assert.Equal(new[] { Path.GetFileName(imageTarget), Path.GetFileName(videoTarget) }.OrderBy(x => x), remaining.OrderBy(x => x));

        AppendPublicationEvidence(
            "overwrite-success",
            sourcePath,
            new Dictionary<string, string>
            {
                ["sourceBefore"] = sourceBefore,
                ["sourceAfter"] = sourceAfter,
                ["oldImageBefore"] = oldImageHash,
                ["newImageAfter"] = imageAfter,
                ["oldVideoBefore"] = oldVideoHash,
                ["newVideoAfter"] = videoAfter
            },
            "Both staged real-sample outputs were published successfully; no forced failure.",
            remaining,
            transactionEvidence);
        CleanupPublicationScenarioDirectory(outputDirectory, imageTarget, videoTarget);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_SecondDestinationLocked_RollsBackFirstAndPreservesBothOriginals()
    {
        const string baseName = "p5r6-publication-locked-second";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("second-destination-locked");
        string imageTarget = Path.Combine(outputDirectory, baseName + ".JPG");
        string videoTarget = Path.Combine(outputDirectory, baseName + ".MP4");
        byte[] oldImage = [0x52, 0x36, 0x2D, 0x4F, 0x4C, 0x44, 0x2D, 0x49];
        byte[] oldVideo = [0x52, 0x36, 0x2D, 0x4F, 0x4C, 0x44, 0x2D, 0x56];
        WriteNewFile(imageTarget, oldImage);
        WriteNewFile(videoTarget, oldVideo);
        string imageBefore = await ComputeSha256Async(imageTarget);
        string videoBefore = await ComputeSha256Async(videoTarget);
        WindowsFileIdentity imageIdentityBefore = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityBefore = WindowsFileIdentity.Capture(videoTarget);
        string renameProbePath = imageTarget + ".foreign-rename-probe";
        var firstMutation = new TaskCompletionSource<SplitPublicationTraceEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirstMutation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRollbackEvidence = new List<SplitPublicationTargetEvidence>();
        using IDisposable hook = LivePhotoSplitService.SetSplitPublicationTimingTestHook(
            outputDirectory,
            async (trace, token) =>
            {
                if (trace.Boundary == SplitPublicationBoundary.AfterRollbackCleanup)
                {
                    postRollbackEvidence.Add(trace.Evidence);
                    return;
                }

                if (trace.Boundary == SplitPublicationBoundary.AfterTargetMutation &&
                    string.Equals(trace.FinalPath, imageTarget, StringComparison.OrdinalIgnoreCase))
                {
                    firstMutation.TrySetResult(trace);
                    await resumeFirstMutation.Task.WaitAsync(token).ConfigureAwait(false);
                }
            });

        Task<LivePhotoSplitResult> splitTask = LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: true);

        SplitPublicationTraceEvent firstMutationTrace;
        NativeOpenAttempt readAttempt;
        NativeOpenAttempt writeAttempt;
        NativePathAttempt renameAttempt;
        NativePathAttempt deleteAttempt;
        IOException failure;
        string videoAfter;
        try
        {
            firstMutationTrace = await firstMutation.Task.WaitAsync(TimeSpan.FromSeconds(30));
            readAttempt = TryOpenNativeFile(imageTarget, GenericReadAccess, FileShareRead);
            writeAttempt = TryOpenNativeFile(imageTarget, GenericReadAccess | GenericWriteAccess, 0);
            renameAttempt = TryMoveNativeFile(imageTarget, renameProbePath);
            deleteAttempt = renameAttempt.Succeeded
                ? new NativePathAttempt(false, 0)
                : TryDeleteNativeFile(imageTarget);

            using var videoLock = new FileStream(videoTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            resumeFirstMutation.TrySetResult(true);
            failure = await Assert.ThrowsAsync<IOException>(() => splitTask);
            AssertRealWin32Failure(failure, 32, 33);
            videoLock.Position = 0;
            videoAfter = Convert.ToHexString(SHA256.HashData(videoLock));
        }
        finally
        {
            resumeFirstMutation.TrySetResult(true);
        }

        Assert.False(readAttempt.Succeeded);
        Assert.Contains(readAttempt.Win32Error, new[] { 32, 33 });
        Assert.False(writeAttempt.Succeeded);
        Assert.Contains(writeAttempt.Win32Error, new[] { 32, 33 });
        Assert.False(renameAttempt.Succeeded);
        Assert.Contains(renameAttempt.Win32Error, new[] { 32, 33 });
        Assert.False(deleteAttempt.Succeeded);
        Assert.Contains(deleteAttempt.Win32Error, new[] { 32, 33 });

        string imageAfter = await ComputeSha256Async(imageTarget);
        string sourceAfter = await ComputeSha256Async(sourcePath);
        WindowsFileIdentity imageIdentityAfter = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityAfter = WindowsFileIdentity.Capture(videoTarget);
        Assert.Equal(imageBefore, imageAfter);
        Assert.Equal(videoBefore, videoAfter);
        Assert.Equal(imageIdentityBefore.FileIndex, imageIdentityAfter.FileIndex);
        Assert.Equal(imageIdentityBefore.VolumeSerialNumber, imageIdentityAfter.VolumeSerialNumber);
        Assert.Equal(imageIdentityBefore.FileAttributes, imageIdentityAfter.FileAttributes);
        Assert.Equal(1u, imageIdentityAfter.LinkCount);
        Assert.Equal(videoIdentityBefore.FileIndex, videoIdentityAfter.FileIndex);
        Assert.Equal(videoIdentityBefore.VolumeSerialNumber, videoIdentityAfter.VolumeSerialNumber);
        Assert.Equal(videoIdentityBefore.FileAttributes, videoIdentityAfter.FileAttributes);
        Assert.Equal(1u, videoIdentityAfter.LinkCount);
        Assert.Equal(sourceBefore, sourceAfter);
        AssertPostRollbackCleanupProofs(postRollbackEvidence, expectedTargetCount: 2);
        AssertNoSplitTransactionArtifacts(outputDirectory);
        string[] remaining = GetFileNames(outputDirectory);
        Assert.Equal(new[] { Path.GetFileName(imageTarget), Path.GetFileName(videoTarget) }.OrderBy(x => x), remaining.OrderBy(x => x));

        AppendPublicationEvidence(
            "second-destination-locked",
            sourcePath,
            new Dictionary<string, string>
            {
                ["sourceBefore"] = sourceBefore,
                ["sourceAfter"] = sourceAfter,
                ["originalImageBefore"] = imageBefore,
                ["originalImageAfterRollback"] = imageAfter,
                ["originalVideoBefore"] = videoBefore,
                ["originalVideoAfterFailure"] = videoAfter,
                ["readOpenWin32"] = readAttempt.Win32Error.ToString(),
                ["writeOpenWin32"] = writeAttempt.Win32Error.ToString(),
                ["renameWin32"] = renameAttempt.Win32Error.ToString(),
                ["deleteWin32"] = deleteAttempt.Win32Error.ToString()
            },
            "After a real in-place image mutation, external read/write/rename/delete opens failed with sharing errors; a real FileShare.None lock on video caused the second mutation open to fail, and the image rolled back to the same FileId and original hash.",
            remaining,
            new
            {
                firstMutationEvidence = firstMutationTrace.Evidence,
                postRollbackEvidence
            });
        CleanupPublicationScenarioDirectory(outputDirectory, imageTarget, videoTarget);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_NoOverwrite_PreservesExistingNamesAndPublishesSeparateOutputs()
    {
        const string baseName = "p5r6-publication-no-overwrite";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("no-overwrite-existing");
        string oldImagePath = Path.Combine(outputDirectory, baseName + ".JPG");
        string oldVideoPath = Path.Combine(outputDirectory, baseName + ".MP4");
        byte[] foreignImage = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x49];
        byte[] foreignVideo = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x56];
        WriteNewFile(oldImagePath, foreignImage);
        WriteNewFile(oldVideoPath, foreignVideo);
        string oldImageBefore = await ComputeSha256Async(oldImagePath);
        string oldVideoBefore = await ComputeSha256Async(oldVideoPath);

        LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: false);

        string expectedImage = Path.Combine(outputDirectory, baseName + " (2).JPG");
        string expectedVideo = Path.Combine(outputDirectory, baseName + " (2).MP4");
        Assert.Equal(expectedImage, Path.GetFullPath(result.ImageOutputPath));
        Assert.Equal(expectedVideo, Path.GetFullPath(result.VideoOutputPath));
        await AssertSplitKeepOutputsAreValidAsync(result);
        string oldImageAfter = await ComputeSha256Async(oldImagePath);
        string oldVideoAfter = await ComputeSha256Async(oldVideoPath);
        string sourceAfter = await ComputeSha256Async(sourcePath);
        Assert.Equal(oldImageBefore, oldImageAfter);
        Assert.Equal(oldVideoBefore, oldVideoAfter);
        Assert.Equal(sourceBefore, sourceAfter);
        AssertNoSplitTransactionArtifacts(outputDirectory);
        string[] remaining = GetFileNames(outputDirectory);
        string[] expectedNames =
        [
            Path.GetFileName(oldImagePath),
            Path.GetFileName(oldVideoPath),
            Path.GetFileName(expectedImage),
            Path.GetFileName(expectedVideo)
        ];
        Assert.Equal(expectedNames.OrderBy(x => x), remaining.OrderBy(x => x));

        AppendPublicationEvidence(
            "no-overwrite-existing",
            sourcePath,
            new Dictionary<string, string>
            {
                ["sourceBefore"] = sourceBefore,
                ["sourceAfter"] = sourceAfter,
                ["foreignImageBefore"] = oldImageBefore,
                ["foreignImageAfter"] = oldImageAfter,
                ["foreignVideoBefore"] = oldVideoBefore,
                ["foreignVideoAfter"] = oldVideoAfter,
                ["publishedImage"] = await ComputeSha256Async(expectedImage),
                ["publishedVideo"] = await ComputeSha256Async(expectedVideo)
            },
            "Pre-existing foreign base-name targets were preserved and both real-sample outputs published to separate no-replace candidates.",
            remaining);
        CleanupPublicationScenarioDirectory(outputDirectory, oldImagePath, oldVideoPath, expectedImage, expectedVideo);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_NoOverwrite_LateOccupantFailsWithoutTouchingForeignObject()
    {
        const string baseName = "p5r6-publication-late-occupant";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("no-overwrite-late-occupant");
        string oldImagePath = Path.Combine(outputDirectory, baseName + ".JPG");
        string oldVideoPath = Path.Combine(outputDirectory, baseName + ".MP4");
        string expectedImagePath = Path.Combine(outputDirectory, baseName + " (2).JPG");
        string expectedVideoPath = Path.Combine(outputDirectory, baseName + " (2).MP4");
        byte[] oldImage = [0x4C, 0x41, 0x54, 0x45, 0x2D, 0x49, 0x4D, 0x47];
        byte[] oldVideo = [0x4C, 0x41, 0x54, 0x45, 0x2D, 0x56, 0x49, 0x44];
        byte[] lateForeign = [0x52, 0x45, 0x41, 0x4C, 0x2D, 0x4F, 0x43, 0x43];
        WriteNewFile(oldImagePath, oldImage);
        WriteNewFile(oldVideoPath, oldVideo);
        string oldImageBefore = await ComputeSha256Async(oldImagePath);
        string oldVideoBefore = await ComputeSha256Async(oldVideoPath);
        var selectedVideo = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRollbackEvidence = new List<SplitPublicationTargetEvidence>();
        using IDisposable hook = LivePhotoSplitService.SetSplitPublicationTimingTestHook(
            outputDirectory,
            (trace, token) =>
            {
                if (trace.Boundary == SplitPublicationBoundary.AfterRollbackCleanup)
                {
                    postRollbackEvidence.Add(trace.Evidence);
                    return Task.CompletedTask;
                }

                if (trace.Boundary != SplitPublicationBoundary.BeforeMissingTargetPublication ||
                    !string.Equals(Path.GetFullPath(trace.FinalPath), expectedVideoPath, StringComparison.OrdinalIgnoreCase))
                    return Task.CompletedTask;

                selectedVideo.TrySetResult(trace.FinalPath);
                return resumePublication.Task.WaitAsync(token);
            });

        Task<LivePhotoSplitResult> splitTask = LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: false);

        try
        {
            string selectedPath = await selectedVideo.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(expectedVideoPath, Path.GetFullPath(selectedPath));
            WriteNewFile(selectedPath, lateForeign);
            string foreignBefore = await ComputeSha256Async(selectedPath);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(lateForeign)), foreignBefore);
            resumePublication.TrySetResult();

            IOException failure = await Assert.ThrowsAsync<IOException>(() => splitTask);
            Win32Exception collision = AssertRealWin32Failure(failure, 80, 183);
            string foreignAfter = await ComputeSha256Async(selectedPath);
            string sourceAfter = await ComputeSha256Async(sourcePath);
            Assert.Equal(foreignBefore, foreignAfter);
            Assert.Equal(sourceBefore, sourceAfter);
            Assert.Equal(oldImageBefore, await ComputeSha256Async(oldImagePath));
            Assert.Equal(oldVideoBefore, await ComputeSha256Async(oldVideoPath));
            AssertPostRollbackCleanupProofs(postRollbackEvidence, expectedTargetCount: 2);
            AssertNoSplitTransactionArtifacts(outputDirectory);
            Assert.False(File.Exists(expectedImagePath));
            string[] remaining = GetFileNames(outputDirectory);
            Assert.Equal(
                new[] { Path.GetFileName(oldImagePath), Path.GetFileName(oldVideoPath), Path.GetFileName(expectedVideoPath) }.OrderBy(x => x),
                remaining.OrderBy(x => x));

            AppendPublicationEvidence(
                "no-overwrite-late-occupant",
                sourcePath,
                new Dictionary<string, string>
                {
                    ["sourceBefore"] = sourceBefore,
                    ["sourceAfter"] = sourceAfter,
                    ["foreignLateOccupantBefore"] = foreignBefore,
                    ["foreignLateOccupantAfter"] = foreignAfter,
                    ["foreignBaseImageBefore"] = oldImageBefore,
                    ["foreignBaseImageAfter"] = await ComputeSha256Async(oldImagePath),
                    ["foreignBaseVideoBefore"] = oldVideoBefore,
                    ["foreignBaseVideoAfter"] = await ComputeSha256Async(oldVideoPath)
                },
                "The per-call seam paused after selecting the second final name; the test created a real file there and resumed. The actual handle-bound no-replace rename failed with a Win32 name collision, the first staged publication was removed, and the foreign object remained byte-identical.",
                remaining,
                new
                {
                    nativeErrorCode = collision.NativeErrorCode,
                    postRollbackEvidence
                });
        }
        finally
        {
            resumePublication.TrySetResult();
        }

        CleanupPublicationScenarioDirectory(
            outputDirectory,
            oldImagePath,
            oldVideoPath,
            expectedVideoPath);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_PreExistingHardLinkAlias_IsRejectedBeforeMutation()
    {
        const string baseName = "p5r6-publication-preexisting-alias";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("preexisting-alias");
        string imageTarget = Path.Combine(outputDirectory, baseName + ".JPG");
        string videoTarget = Path.Combine(outputDirectory, baseName + ".MP4");
        string aliasPath = Path.Combine(outputDirectory, baseName + ".JPG.preexisting-alias");
        byte[] oldImage = [0x50, 0x52, 0x45, 0x2D, 0x41, 0x4C, 0x49, 0x41];
        byte[] oldVideo = [0x50, 0x52, 0x45, 0x2D, 0x56, 0x49, 0x44, 0x45];
        WriteNewFile(imageTarget, oldImage);
        WriteNewFile(videoTarget, oldVideo);
        (bool aliasCreated, int aliasError) = CreateHardLinkForSplitTest(aliasPath, imageTarget);
        Assert.True(aliasCreated, $"CreateHardLinkW failed with Win32 error {aliasError}.");

        string imageBefore = await ComputeSha256Async(imageTarget);
        string videoBefore = await ComputeSha256Async(videoTarget);
        WindowsFileIdentity imageIdentityBefore = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityBefore = WindowsFileIdentity.Capture(videoTarget);
        Assert.Equal(2u, imageIdentityBefore.LinkCount);
        var postRollbackEvidence = new List<SplitPublicationTargetEvidence>();
        using IDisposable hook = LivePhotoSplitService.SetSplitPublicationTimingTestHook(
            outputDirectory,
            (trace, _) =>
            {
                if (trace.Boundary == SplitPublicationBoundary.AfterRollbackCleanup)
                    postRollbackEvidence.Add(trace.Evidence);
                return Task.CompletedTask;
            });

        IOException failure = await Assert.ThrowsAsync<IOException>(() => LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: true));

        Assert.Contains("link count", failure.Message, StringComparison.OrdinalIgnoreCase);
        AssertPostRollbackCleanupProofs(postRollbackEvidence, expectedTargetCount: 2);
        Assert.Equal(imageBefore, await ComputeSha256Async(imageTarget));
        Assert.Equal(imageBefore, await ComputeSha256Async(aliasPath));
        Assert.Equal(videoBefore, await ComputeSha256Async(videoTarget));
        Assert.Equal(sourceBefore, await ComputeSha256Async(sourcePath));
        WindowsFileIdentity imageIdentityAfter = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity aliasIdentityAfter = WindowsFileIdentity.Capture(aliasPath);
        WindowsFileIdentity videoIdentityAfter = WindowsFileIdentity.Capture(videoTarget);
        Assert.Equal(imageIdentityBefore.FileIndex, imageIdentityAfter.FileIndex);
        Assert.Equal(imageIdentityBefore.FileIndex, aliasIdentityAfter.FileIndex);
        Assert.Equal(2u, imageIdentityAfter.LinkCount);
        Assert.Equal(videoIdentityBefore.FileIndex, videoIdentityAfter.FileIndex);
        AssertNoSplitTransactionArtifacts(outputDirectory);
        string[] remaining = GetFileNames(outputDirectory);
        Assert.Equal(
            new[] { Path.GetFileName(imageTarget), Path.GetFileName(videoTarget), Path.GetFileName(aliasPath) }.OrderBy(x => x),
            remaining.OrderBy(x => x));

        AppendPublicationEvidence(
            "preexisting-alias",
            sourcePath,
            new Dictionary<string, string>
            {
                ["sourceBefore"] = sourceBefore,
                ["sourceAfter"] = await ComputeSha256Async(sourcePath),
                ["imageBefore"] = imageBefore,
                ["imageAfter"] = await ComputeSha256Async(imageTarget),
                ["aliasAfter"] = await ComputeSha256Async(aliasPath),
                ["videoBefore"] = videoBefore,
                ["videoAfter"] = await ComputeSha256Async(videoTarget),
                ["imageFileId"] = imageIdentityAfter.FileIndex.ToString("X16"),
                ["imageLinkCount"] = imageIdentityAfter.LinkCount.ToString()
            },
            "A real pre-existing hard link raised the baseline link count to two; Split rejected it before mutation and preserved both names and the other destination.",
            remaining,
            new
            {
                aliasCreationWin32 = aliasError,
                imageIdentityBefore,
                imageIdentityAfter,
                aliasIdentityAfter,
                videoIdentityBefore,
                videoIdentityAfter,
                postRollbackEvidence
            });

        CleanupPublicationScenarioDirectory(outputDirectory, imageTarget, videoTarget, aliasPath);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_ConcurrentAliasAtFinalBarrier_RollsBackAndPreservesAlias()
    {
        const string baseName = "p5r6-publication-concurrent-alias";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("concurrent-alias-final-barrier");
        string imageTarget = Path.Combine(outputDirectory, baseName + ".JPG");
        string videoTarget = Path.Combine(outputDirectory, baseName + ".MP4");
        string aliasPath = Path.Combine(outputDirectory, baseName + ".JPG.concurrent-alias");
        byte[] oldImage = [0x43, 0x4F, 0x4E, 0x43, 0x2D, 0x49, 0x4D, 0x47];
        byte[] oldVideo = [0x43, 0x4F, 0x4E, 0x43, 0x2D, 0x56, 0x49, 0x44];
        WriteNewFile(imageTarget, oldImage);
        WriteNewFile(videoTarget, oldVideo);
        string imageBefore = await ComputeSha256Async(imageTarget);
        string videoBefore = await ComputeSha256Async(videoTarget);
        WindowsFileIdentity imageIdentityBefore = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityBefore = WindowsFileIdentity.Capture(videoTarget);

        var barrierReached = new TaskCompletionSource<SplitPublicationTraceEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transactionEvidence = new List<SplitPublicationTargetEvidence>();
        var postRollbackEvidence = new List<SplitPublicationTargetEvidence>();
        using IDisposable hook = LivePhotoSplitService.SetSplitPublicationTimingTestHook(
            outputDirectory,
            async (trace, token) =>
            {
                if (trace.Boundary == SplitPublicationBoundary.AfterRollbackCleanup)
                {
                    postRollbackEvidence.Add(trace.Evidence);
                    return;
                }

                if (trace.Boundary != SplitPublicationBoundary.BeforeCommitBarrier)
                    return;

                transactionEvidence.Add(trace.Evidence);
                if (string.Equals(trace.FinalPath, imageTarget, StringComparison.OrdinalIgnoreCase))
                {
                    barrierReached.TrySetResult(trace);
                    await resumeBarrier.Task.WaitAsync(token).ConfigureAwait(false);
                }
            });

        Task<LivePhotoSplitResult> splitTask = LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: true);

        SplitPublicationTraceEvent barrierTrace;
        bool aliasCreated;
        int aliasError;
        try
        {
            barrierTrace = await barrierReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            (aliasCreated, aliasError) = CreateHardLinkForSplitTest(aliasPath, imageTarget);
        }
        finally
        {
            resumeBarrier.TrySetResult(true);
        }

        Assert.True(aliasCreated, $"CreateHardLinkW failed with Win32 error {aliasError}.");
        IOException failure = await Assert.ThrowsAsync<IOException>(() => splitTask);
        Assert.Contains("alias", failure.Message, StringComparison.OrdinalIgnoreCase);

        string imageAfter = await ComputeSha256Async(imageTarget);
        string aliasAfter = await ComputeSha256Async(aliasPath);
        string videoAfter = await ComputeSha256Async(videoTarget);
        string sourceAfter = await ComputeSha256Async(sourcePath);
        WindowsFileIdentity imageIdentityAfter = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity aliasIdentityAfter = WindowsFileIdentity.Capture(aliasPath);
        WindowsFileIdentity videoIdentityAfter = WindowsFileIdentity.Capture(videoTarget);
        Assert.Equal(imageBefore, imageAfter);
        Assert.Equal(imageBefore, aliasAfter);
        Assert.Equal(videoBefore, videoAfter);
        Assert.Equal(sourceBefore, sourceAfter);
        AssertPostRollbackCleanupProofs(postRollbackEvidence, expectedTargetCount: 2);
        Assert.Equal(imageIdentityBefore.FileIndex, imageIdentityAfter.FileIndex);
        Assert.Equal(imageIdentityBefore.FileIndex, aliasIdentityAfter.FileIndex);
        Assert.Equal(imageIdentityBefore.VolumeSerialNumber, imageIdentityAfter.VolumeSerialNumber);
        Assert.Equal(2u, imageIdentityAfter.LinkCount);
        Assert.Equal(videoIdentityBefore.FileIndex, videoIdentityAfter.FileIndex);
        Assert.Equal(1u, videoIdentityAfter.LinkCount);
        AssertNoSplitTransactionArtifacts(outputDirectory);
        string[] remaining = GetFileNames(outputDirectory);
        Assert.Equal(
            new[] { Path.GetFileName(imageTarget), Path.GetFileName(videoTarget), Path.GetFileName(aliasPath) }.OrderBy(x => x),
            remaining.OrderBy(x => x));

        AppendPublicationEvidence(
            "concurrent-alias-final-barrier",
            sourcePath,
            new Dictionary<string, string>
            {
                ["sourceBefore"] = sourceBefore,
                ["sourceAfter"] = sourceAfter,
                ["imageBefore"] = imageBefore,
                ["imageAfterRollback"] = imageAfter,
                ["aliasAfterRollback"] = aliasAfter,
                ["videoBefore"] = videoBefore,
                ["videoAfterRollback"] = videoAfter,
                ["imageLinkCountAfterRollback"] = imageIdentityAfter.LinkCount.ToString()
            },
            "A real hard-link alias was created after per-target verification while the transaction was paused at the final barrier. The barrier detected it; both existing targets rolled back and the test-owned alias remained untouched until test teardown.",
            remaining,
            new
            {
                aliasCreationWin32 = aliasError,
                barrierEvidence = barrierTrace.Evidence,
                allBarrierEvidence = transactionEvidence,
                postRollbackEvidence,
                imageIdentityBefore,
                imageIdentityAfter,
                aliasIdentityAfter,
                videoIdentityBefore,
                videoIdentityAfter
            });

        CleanupPublicationScenarioDirectory(outputDirectory, imageTarget, videoTarget, aliasPath);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R6_SplitPublication_ForeignReplacementAfterBaseline_IsRejectedWithoutOverwritingForeignObject()
    {
        const string baseName = "p5r6-publication-baseline-replacement";
        string sourcePath = ResolveFrozenSample("红米老款-GV1.JPG");
        string sourceBefore = await ComputeSha256Async(sourcePath);
        string outputDirectory = CreatePublicationScenarioDirectory("baseline-replacement");
        string imageTarget = Path.Combine(outputDirectory, baseName + ".JPG");
        string videoTarget = Path.Combine(outputDirectory, baseName + ".MP4");
        string movedBaselinePath = imageTarget + ".captured-original";
        byte[] oldImage = [0x42, 0x41, 0x53, 0x45, 0x2D, 0x49, 0x4D, 0x47];
        byte[] oldVideo = [0x42, 0x41, 0x53, 0x45, 0x2D, 0x56, 0x49, 0x44];
        byte[] foreignImage = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x49];
        WriteNewFile(imageTarget, oldImage);
        WriteNewFile(videoTarget, oldVideo);
        string imageBefore = await ComputeSha256Async(imageTarget);
        string videoBefore = await ComputeSha256Async(videoTarget);
        WindowsFileIdentity imageIdentityBefore = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity videoIdentityBefore = WindowsFileIdentity.Capture(videoTarget);

        var baselineReached = new TaskCompletionSource<SplitPublicationTraceEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeMutation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postRollbackEvidence = new List<SplitPublicationTargetEvidence>();
        using IDisposable hook = LivePhotoSplitService.SetSplitPublicationTimingTestHook(
            outputDirectory,
            async (trace, token) =>
            {
                if (trace.Boundary == SplitPublicationBoundary.AfterRollbackCleanup)
                {
                    postRollbackEvidence.Add(trace.Evidence);
                    return;
                }

                if (trace.Boundary == SplitPublicationBoundary.BeforeMutationRevalidation &&
                    string.Equals(trace.FinalPath, imageTarget, StringComparison.OrdinalIgnoreCase))
                {
                    baselineReached.TrySetResult(trace);
                    await resumeMutation.Task.WaitAsync(token).ConfigureAwait(false);
                }
            });

        Task<LivePhotoSplitResult> splitTask = LivePhotoSplitService.SplitAsync(
            sourcePath,
            outputDirectory,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: baseName,
            overwriteExisting: true);

        SplitPublicationTraceEvent baselineTrace;
        string foreignBefore;
        WindowsFileIdentity foreignIdentityBefore;
        try
        {
            baselineTrace = await baselineReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            File.Move(imageTarget, movedBaselinePath);
            WriteNewFile(imageTarget, foreignImage);
            foreignBefore = await ComputeSha256Async(imageTarget);
            foreignIdentityBefore = WindowsFileIdentity.Capture(imageTarget);
        }
        finally
        {
            resumeMutation.TrySetResult(true);
        }

        IOException failure = await Assert.ThrowsAsync<IOException>(() => splitTask);
        Assert.Contains("identity", failure.Message, StringComparison.OrdinalIgnoreCase);
        string foreignAfter = await ComputeSha256Async(imageTarget);
        string movedOriginalAfter = await ComputeSha256Async(movedBaselinePath);
        string videoAfter = await ComputeSha256Async(videoTarget);
        string sourceAfter = await ComputeSha256Async(sourcePath);
        WindowsFileIdentity foreignIdentityAfter = WindowsFileIdentity.Capture(imageTarget);
        WindowsFileIdentity movedOriginalIdentity = WindowsFileIdentity.Capture(movedBaselinePath);
        WindowsFileIdentity videoIdentityAfter = WindowsFileIdentity.Capture(videoTarget);
        Assert.Equal(foreignBefore, foreignAfter);
        Assert.Equal(imageBefore, movedOriginalAfter);
        Assert.Equal(videoBefore, videoAfter);
        Assert.Equal(sourceBefore, sourceAfter);
        AssertPostRollbackCleanupProofs(postRollbackEvidence, expectedTargetCount: 2);
        Assert.Equal(foreignIdentityBefore.FileIndex, foreignIdentityAfter.FileIndex);
        Assert.Equal(foreignIdentityBefore.VolumeSerialNumber, foreignIdentityAfter.VolumeSerialNumber);
        Assert.Equal(imageIdentityBefore.FileIndex, movedOriginalIdentity.FileIndex);
        Assert.Equal(videoIdentityBefore.FileIndex, videoIdentityAfter.FileIndex);
        AssertNoSplitTransactionArtifacts(outputDirectory);
        string[] remaining = GetFileNames(outputDirectory);
        Assert.Equal(
            new[] { Path.GetFileName(imageTarget), Path.GetFileName(videoTarget), Path.GetFileName(movedBaselinePath) }.OrderBy(x => x),
            remaining.OrderBy(x => x));

        AppendPublicationEvidence(
            "baseline-replacement",
            sourcePath,
            new Dictionary<string, string>
            {
                ["sourceBefore"] = sourceBefore,
                ["sourceAfter"] = sourceAfter,
                ["foreignBefore"] = foreignBefore,
                ["foreignAfter"] = foreignAfter,
                ["movedOriginalBefore"] = imageBefore,
                ["movedOriginalAfter"] = movedOriginalAfter,
                ["videoBefore"] = videoBefore,
                ["videoAfter"] = videoAfter,
                ["foreignFileId"] = foreignIdentityAfter.FileIndex.ToString("X16"),
                ["originalFileId"] = movedOriginalIdentity.FileIndex.ToString("X16")
            },
            "The test moved the baseline object and placed a known foreign object at the final path after baseline capture. Immediate retained-handle revalidation rejected the changed FileId before writing and preserved both objects.",
            remaining,
            new
            {
                baselineEvidence = baselineTrace.Evidence,
                foreignIdentityBefore,
                foreignIdentityAfter,
                movedOriginalIdentity,
                imageIdentityBefore,
                videoIdentityBefore,
                videoIdentityAfter,
                postRollbackEvidence
            });

        CleanupPublicationScenarioDirectory(outputDirectory, imageTarget, videoTarget, movedBaselinePath);
    }

    private static string CreatePublicationScenarioDirectory(string scenarioName)
    {
        string scenarioRoot = FindRepositoryRootAndJoin(
            ".ai-tmp/workspace/P5-R6/neutral-split-exact-object-content-transaction/scenarios");
        Directory.CreateDirectory(scenarioRoot);
        string scenarioDirectory = Path.Combine(scenarioRoot, scenarioName);
        if (Directory.Exists(scenarioDirectory))
            RemoveOnlyKnownInterruptedSeeds(scenarioDirectory, scenarioName);

        Directory.CreateDirectory(scenarioDirectory);
        return scenarioDirectory;
    }

    private static void RemoveOnlyKnownInterruptedSeeds(string directory, string scenarioName)
    {
        Dictionary<string, byte[]> expected = scenarioName switch
        {
            "overwrite-success" => new()
            {
                ["p5r6-publication-overwrite.JPG"] = [0x50, 0x35, 0x52, 0x36, 0x2D, 0x49, 0x4D, 0x47],
                ["p5r6-publication-overwrite.MP4"] = [0x50, 0x35, 0x52, 0x36, 0x2D, 0x56, 0x49, 0x44]
            },
            "second-destination-locked" => new()
            {
                ["p5r6-publication-locked-second.JPG"] = [0x52, 0x36, 0x2D, 0x4F, 0x4C, 0x44, 0x2D, 0x49],
                ["p5r6-publication-locked-second.MP4"] = [0x52, 0x36, 0x2D, 0x4F, 0x4C, 0x44, 0x2D, 0x56]
            },
            "no-overwrite-existing" => new()
            {
                ["p5r6-publication-no-overwrite.JPG"] = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x49],
                ["p5r6-publication-no-overwrite.MP4"] = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x56]
            },
            "no-overwrite-late-occupant" => new()
            {
                ["p5r6-publication-late-occupant.JPG"] = [0x4C, 0x41, 0x54, 0x45, 0x2D, 0x49, 0x4D, 0x47],
                ["p5r6-publication-late-occupant.MP4"] = [0x4C, 0x41, 0x54, 0x45, 0x2D, 0x56, 0x49, 0x44]
            },
            "preexisting-alias" => new()
            {
                ["p5r6-publication-preexisting-alias.JPG"] = [0x50, 0x52, 0x45, 0x2D, 0x41, 0x4C, 0x49, 0x41],
                ["p5r6-publication-preexisting-alias.MP4"] = [0x50, 0x52, 0x45, 0x2D, 0x56, 0x49, 0x44, 0x45],
                ["p5r6-publication-preexisting-alias.JPG.preexisting-alias"] = [0x50, 0x52, 0x45, 0x2D, 0x41, 0x4C, 0x49, 0x41]
            },
            "concurrent-alias-final-barrier" => new()
            {
                ["p5r6-publication-concurrent-alias.JPG"] = [0x43, 0x4F, 0x4E, 0x43, 0x2D, 0x49, 0x4D, 0x47],
                ["p5r6-publication-concurrent-alias.MP4"] = [0x43, 0x4F, 0x4E, 0x43, 0x2D, 0x56, 0x49, 0x44],
                ["p5r6-publication-concurrent-alias.JPG.concurrent-alias"] = [0x43, 0x4F, 0x4E, 0x43, 0x2D, 0x49, 0x4D, 0x47]
            },
            "baseline-replacement" => new()
            {
                ["p5r6-publication-baseline-replacement.JPG"] = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x49],
                ["p5r6-publication-baseline-replacement.MP4"] = [0x42, 0x41, 0x53, 0x45, 0x2D, 0x56, 0x49, 0x44],
                ["p5r6-publication-baseline-replacement.JPG.captured-original"] = [0x42, 0x41, 0x53, 0x45, 0x2D, 0x49, 0x4D, 0x47]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenarioName), scenarioName, "Unknown publication test scenario.")
        };

        Assert.Empty(Directory.EnumerateDirectories(directory));
        string[] actualPaths = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] expectedPaths = expected.Keys
            .Select(name => Path.GetFullPath(Path.Combine(directory, name)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(expectedPaths, actualPaths);

        foreach ((string fileName, byte[] expectedBytes) in expected)
        {
            string path = Path.Combine(directory, fileName);
            Assert.Equal(expectedBytes, File.ReadAllBytes(path));
        }

        foreach (string path in expectedPaths)
            File.Delete(path);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        Directory.Delete(directory);
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static (bool Succeeded, int Win32Error) CreateHardLinkForSplitTest(string linkPath, string targetPath)
    {
        bool succeeded = SplitPublicationNative.CreateHardLinkW(
            Path.GetFullPath(linkPath),
            Path.GetFullPath(targetPath),
            IntPtr.Zero);
        return (succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    private static NativeOpenAttempt TryOpenNativeFile(string path, uint desiredAccess, uint shareMode)
    {
        SafeFileHandle handle = SplitPublicationNative.CreateFileW(
            Path.GetFullPath(path),
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            SplitPublicationNative.OpenExisting,
            SplitPublicationNative.OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return new NativeOpenAttempt(false, error);
        }

        handle.Dispose();
        return new NativeOpenAttempt(true, 0);
    }

    private static NativePathAttempt TryMoveNativeFile(string sourcePath, string destinationPath)
    {
        bool succeeded = SplitPublicationNative.MoveFileExW(
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(destinationPath),
            flags: 0);
        return new NativePathAttempt(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    private static NativePathAttempt TryDeleteNativeFile(string path)
    {
        bool succeeded = SplitPublicationNative.DeleteFileW(Path.GetFullPath(path));
        return new NativePathAttempt(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error());
    }

    private sealed record NativeOpenAttempt(bool Succeeded, int Win32Error);

    private sealed record NativePathAttempt(bool Succeeded, int Win32Error);

    private static class SplitPublicationNative
    {
        public const uint OpenExisting = 3;
        public const uint OpenReparsePoint = 0x00200000;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(string linkPath, string existingPath, IntPtr securityAttributes);

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileExW(string existingPath, string newPath, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteFileW(string fileName);
    }

    private const uint GenericReadAccess = 0x80000000;
    private const uint GenericWriteAccess = 0x40000000;
    private const uint FileShareRead = 0x00000001;

    private static async Task AssertSplitKeepOutputsAreValidAsync(LivePhotoSplitResult result)
    {
        Assert.True(File.Exists(result.ImageOutputPath));
        Assert.True(File.Exists(result.VideoOutputPath));
        Assert.True(new FileInfo(result.ImageOutputPath).Length > 0);
        Assert.True(new FileInfo(result.VideoOutputPath).Length > 0);

        SourceMediaFacts imageFacts = await new SourceInspector().InspectAsync(
            result.ImageOutputPath,
            cancellationToken: CancellationToken.None);
        Assert.Equal(ImageContainer.Jpeg, imageFacts.PrimaryImage.Container);
        Assert.Equal(SourceProtocol.NonLive, imageFacts.Protocol);
        Assert.Null(imageFacts.MotionVideo);
        Assert.Equal(0, imageFacts.ProtocolTailLength);
        Assert.Null(imageFacts.PairingIdentifier);

        VideoFacts videoFacts = await new VideoConverter().ProbeAsync(
            result.VideoOutputPath,
            CancellationToken.None);
        Assert.True(videoFacts.IsPresent);
        Assert.Equal(VideoContainer.Mp4, videoFacts.Container);
        Assert.Equal(VideoCodec.H264, videoFacts.Codec);
    }

    private static string[] GetFileNames(string directory) => Directory
        .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
        .Select(Path.GetFileName)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray()!;

    private static void AssertNoSplitTransactionArtifacts(string directory)
    {
        string[] transactionFiles = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith(".lpb-split-", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(transactionFiles);
    }

    private static void CleanupPublicationScenarioDirectory(string directory, params string[] expectedFiles)
    {
        string[] actual = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] expected = expectedFiles
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(expected, actual);

        foreach (string path in expected)
            File.Delete(path);

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        Directory.Delete(directory);
    }

    private static void AppendPublicationEvidence(
        string scenario,
        string sourcePath,
        IReadOnlyDictionary<string, string> hashes,
        string failureMechanism,
        IEnumerable<string> remainingFiles,
        object? transactionEvidence = null)
    {
        string evidencePath = FindRepositoryRootAndJoin(
            ".ai-tmp/workspace/P5-R6/neutral-split-exact-object-content-transaction/publication-test-evidence.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            scenario,
            sourcePath = Path.GetFullPath(sourcePath),
            hashes,
            failureMechanism,
            remainingFiles = remainingFiles.OrderBy(name => name, StringComparer.Ordinal),
            transactionEvidence,
            capturedAtUtc = DateTimeOffset.UtcNow
        });

        lock (s_publicationEvidenceLock)
            File.AppendAllText(evidencePath, json + Environment.NewLine);
    }

    private static void AssertPostRollbackCleanupProofs(
        IReadOnlyCollection<SplitPublicationTargetEvidence> evidence,
        int expectedTargetCount)
    {
        Assert.Equal(expectedTargetCount, evidence.Count);
        foreach (SplitPublicationTargetEvidence target in evidence)
        {
            AssertOwnedObjectCleanupProof(
                target.StageIdentity,
                target.StageCleanupProof,
                $"stage for '{target.FinalPath}'");

            if (target.BackupIdentity is not null)
            {
                AssertOwnedObjectCleanupProof(
                    target.BackupIdentity,
                    target.BackupCleanupProof,
                    $"backup for '{target.FinalPath}'");
            }
            else
            {
                Assert.Null(target.BackupCleanupProof);
            }
        }
    }

    private static void AssertOwnedObjectCleanupProof(
        WindowsFileIdentity? identity,
        string? cleanupProof,
        string role)
    {
        Assert.NotNull(identity);
        Assert.True(
            cleanupProof is "Gone" or "Unlinked",
            $"The exact transaction-owned {role} object {identity!.VolumeSerialNumber:X8}:{identity.FileIndex:X16} has no accepted cleanup proof (actual: {cleanupProof ?? "missing"}).");
    }

    private static Win32Exception AssertRealWin32Failure(Exception exception, params int[] expectedNativeErrors)
    {
        Win32Exception? win32Exception = FindWin32Exception(exception);
        Assert.NotNull(win32Exception);
        Assert.Contains(win32Exception!.NativeErrorCode, expectedNativeErrors);
        return win32Exception;
    }

    private static Win32Exception? FindWin32Exception(Exception exception)
    {
        if (exception is Win32Exception win32)
            return win32;
        if (exception is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                Win32Exception? found = FindWin32Exception(inner);
                if (found != null)
                    return found;
            }
        }

        return exception.InnerException == null ? null : FindWin32Exception(exception.InnerException);
    }

    private static void AssertImageDispatch(CapturingImageConverter capture, ImageContainer target)
    {
        if (target == ImageContainer.Jpeg)
        {
            Assert.Empty(capture.Calls);
            return;
        }

        var call = Assert.Single(capture.Calls);
        Assert.Equal(ImageContainer.Jpeg, call.Request.SourceArtifact.ImageContainer);
        Assert.Equal(ImageContainer.Heic, call.Request.TargetContainer);
        Assert.Null(call.Request.TrustedHdrGainMapBinding);
        Assert.False(call.Request.TrustedSourceFacts?.HasGainMap ?? true);
        Assert.True(call.Result.Success, call.Result.ErrorMessage);
        Assert.NotNull(call.Result.OutputArtifact);
        Assert.Equal(ImageContainer.Heic, call.Result.ExecutionRecord.OutputContainer);
        Assert.Equal(ConversionOperationKind.LossyReencode, call.Result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.Reencoded, call.Result.ExecutionRecord.Truth.Pixel);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, call.Result.ExecutionRecord.Truth.Metadata);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, call.Result.ExecutionRecord.Truth.Orientation);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, call.Result.ExecutionRecord.Truth.ColorIcc);
        Assert.False(call.Result.ExecutionRecord.Truth.FallbackOccurred);
    }

    private static void AssertVideoDispatch(
        CapturingVideoConverter capture,
        VideoContainer targetContainer,
        VideoCodec targetCodec)
    {
        if (targetContainer == VideoContainer.Mp4 && targetCodec == VideoCodec.Copy)
        {
            Assert.Empty(capture.Calls);
            return;
        }

        var call = Assert.Single(capture.Calls);
        Assert.Equal(VideoContainer.Mp4, call.Request.SourceArtifact.VideoContainer);
        Assert.Equal(VideoCodec.H264, call.Request.SourceArtifact.VideoCodec);
        VideoConversionSourceFacts trustedFacts = Assert.IsType<VideoConversionSourceFacts>(call.Request.TrustedSourceFacts);
        Assert.Equal(VideoContainer.Mp4, trustedFacts.Container);
        Assert.Equal(VideoCodec.H264, trustedFacts.Codec);
        Assert.Equal(targetContainer, call.Request.TargetContainer);
        Assert.Equal(targetCodec, call.Request.TargetCodec);
        Assert.Equal(0, call.Request.TargetFps);
        Assert.True(call.Result.Success, call.Result.ErrorMessage);
        Assert.NotNull(call.Result.OutputArtifact);
        Assert.Equal(targetContainer, call.Result.ExecutionRecord.OutputContainer);
        Assert.Equal(targetCodec == VideoCodec.Copy ? VideoCodec.H264 : targetCodec, call.Result.ExecutionRecord.OutputCodec);
        Assert.False(call.Result.ExecutionRecord.HardwareFallbackOccurred);
        Assert.False(call.Result.ExecutionRecord.Truth.FallbackOccurred);

        if (targetContainer == VideoContainer.Mov && targetCodec == VideoCodec.Copy)
        {
            Assert.True(call.Result.ExecutionRecord.RemuxUsed);
            Assert.Equal(ConversionOperationKind.ContainerRemux, call.Result.ExecutionRecord.Truth.ActualOperationKind);
            Assert.Equal(ConversionCapability.VideoRemux, call.Result.ExecutionRecord.Truth.SelectedCapability);
            Assert.Equal(ConversionCapability.VideoRemux, call.Result.ExecutionRecord.Truth.ActualCapability);
            Assert.Equal(VideoBackend.ProjectIsoBmffRemux, call.Result.ExecutionRecord.Backend);
            Assert.Equal("ProjectIsoBmffRemux", call.Result.ExecutionRecord.Truth.BackendName);
            Assert.Equal("ProjectIsoBmffStreamRemux", call.Result.ExecutionRecord.SelectedEncoder);
        }
        else
        {
            Assert.Equal(VideoContainer.Mp4, targetContainer);
            Assert.Equal(VideoCodec.Hevc, targetCodec);
            Assert.False(call.Result.ExecutionRecord.RemuxUsed);
            Assert.Equal(ConversionOperationKind.LossyReencode, call.Result.ExecutionRecord.Truth.ActualOperationKind);
            Assert.Equal(ConversionCapability.VideoTranscodeSdr, call.Result.ExecutionRecord.Truth.SelectedCapability);
            Assert.Equal(ConversionCapability.VideoTranscodeSdr, call.Result.ExecutionRecord.Truth.ActualCapability);
            Assert.Equal(VideoBackend.WindowsMediaFoundation, call.Result.ExecutionRecord.Backend);
            Assert.Equal(VideoHardwareMode.SoftwareForced, call.Result.ExecutionRecord.HardwareMode);
            Assert.Equal("WindowsMediaFoundation", call.Result.ExecutionRecord.Truth.BackendName);
            Assert.Equal("SoftwareForced", call.Result.ExecutionRecord.Truth.HardwareMode);
            Assert.Contains("H.264;", call.Result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
            Assert.Contains("HEVC;", call.Result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
            Assert.Contains("CICP=5/6/6;range=full", call.Result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
            Assert.Contains("CICP=5/6/6;range=full", call.Result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
        }
    }

    private static void AssertSplitImageDispatch(CapturingImageConverter capture, ImageContainer target)
    {
        if (target == ImageContainer.Jpeg)
        {
            Assert.Empty(capture.Calls);
            return;
        }

        var call = Assert.Single(capture.Calls);
        Assert.Equal(ImageContainer.Jpeg, call.Request.SourceArtifact.ImageContainer);
        Assert.Equal(ImageContainer.Heic, call.Request.TargetContainer);
        Assert.Null(call.Request.TrustedHdrGainMapBinding);
        Assert.False(call.Request.TrustedSourceFacts?.HasGainMap ?? true);
        Assert.True(call.Result.Success, call.Result.ErrorMessage);
        Assert.NotNull(call.Result.OutputArtifact);
        Assert.Equal(ImageContainer.Heic, call.Result.ExecutionRecord.OutputContainer);
        Assert.Equal(ConversionOperationKind.LossyReencode, call.Result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.Reencoded, call.Result.ExecutionRecord.Truth.Pixel);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, call.Result.ExecutionRecord.Truth.Metadata);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, call.Result.ExecutionRecord.Truth.Orientation);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, call.Result.ExecutionRecord.Truth.ColorIcc);
        Assert.False(call.Result.ExecutionRecord.Truth.FallbackOccurred);
    }

    private static void AssertSplitVideoDispatch(
        CapturingVideoConverter capture,
        VideoContainer targetContainer,
        VideoCodec targetCodec)
    {
        if (targetContainer == VideoContainer.Mp4 && targetCodec == VideoCodec.H264)
        {
            Assert.Empty(capture.Calls);
            return;
        }

        var call = Assert.Single(capture.Calls);
        Assert.Equal(VideoContainer.Mp4, call.Request.SourceArtifact.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, call.Request.SourceArtifact.VideoCodec);
        VideoConversionSourceFacts trustedFacts = Assert.IsType<VideoConversionSourceFacts>(call.Request.TrustedSourceFacts);
        Assert.Equal(VideoContainer.Mp4, trustedFacts.Container);
        Assert.Equal(VideoCodec.Hevc, trustedFacts.Codec);
        Assert.Equal(VideoContainer.Mov, call.Request.TargetContainer);
        Assert.Equal(VideoCodec.Copy, call.Request.TargetCodec);
        Assert.Equal(0, call.Request.TargetFps);
        Assert.True(call.Result.Success, call.Result.ErrorMessage);
        Assert.NotNull(call.Result.OutputArtifact);
        Assert.Equal(VideoContainer.Mov, call.Result.ExecutionRecord.OutputContainer);
        Assert.Equal(VideoCodec.Hevc, call.Result.ExecutionRecord.OutputCodec);
        Assert.Equal(VideoCodec.Copy, call.Result.ExecutionRecord.RequestedCodec);
        Assert.True(call.Result.ExecutionRecord.RemuxUsed);
        Assert.Equal(ConversionOperationKind.ContainerRemux, call.Result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.VideoRemux, call.Result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.VideoRemux, call.Result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal(VideoBackend.ProjectIsoBmffRemux, call.Result.ExecutionRecord.Backend);
        Assert.Equal("ProjectIsoBmffRemux", call.Result.ExecutionRecord.Truth.BackendName);
        Assert.Equal("ProjectIsoBmffStreamRemux", call.Result.ExecutionRecord.SelectedEncoder);
        Assert.False(call.Result.ExecutionRecord.Truth.FallbackOccurred);
    }

    private static async Task AssertNeutralManifestAsync(
        NeutralMediaBundle bundle,
        MediaArtifact artifact,
        string role)
    {
        NeutralArtifactManifest manifest = Assert.Single(bundle.Manifest, entry => entry.Role == role);
        var info = new FileInfo(artifact.Path);
        Assert.True(info.Exists, $"Neutral {role} artifact is missing: {artifact.Path}");
        Assert.Equal(Path.GetFullPath(artifact.Path), Path.GetFullPath(manifest.Path));
        Assert.Equal(info.Length, manifest.ByteLength);
        Assert.Equal(await ComputeSha256Async(artifact.Path), NormalizeHash(manifest.Sha256));
        if (role == "MotionVideo")
        {
            Assert.Equal(artifact.VideoContainer, manifest.VideoContainer);
            Assert.Equal(artifact.VideoCodec, manifest.VideoCodec);
            Assert.NotEqual(VideoContainer.Unknown, manifest.VideoContainer);
            Assert.NotEqual(VideoCodec.Unknown, manifest.VideoCodec);
        }
    }

    private static (string ImagePath, string VideoPath) ResolveFrozenVivoPair()
    {
        Assert.Equal(P4SourceManifestSha256, ComputeSha256(FindRepositoryRootAndJoin("tests/fixtures/realsamples-manifest.json")));

        CorpusSample imageRecord = RealSampleCorpusManifest.GetRequiredSample("vivo双文件.jpg");
        CorpusSample videoRecord = RealSampleCorpusManifest.GetRequiredSample("vivo双文件.mp4");
        Assert.Equal(VivoImageSha256, NormalizeHash(imageRecord.Sha256));
        Assert.Equal(VivoVideoSha256, NormalizeHash(videoRecord.Sha256));

        string imagePath = FindRepositoryRootAndJoin(".ai-tmp/cache/samples/vivo双文件.jpg");
        string videoPath = FindRepositoryRootAndJoin(".ai-tmp/cache/samples/P5-R5-v1/vivo双文件.mp4");
        Assert.True(File.Exists(imagePath), $"Required P4-v1 Vivo image cache is missing: {imagePath}");
        Assert.True(File.Exists(videoPath), $"Required P5-R5-v1 Vivo video cache is missing: {videoPath}");
        Assert.Equal(imageRecord.ByteSize, new FileInfo(imagePath).Length);
        Assert.Equal(videoRecord.ByteSize, new FileInfo(videoPath).Length);
        Assert.Equal(VivoImageSha256, ComputeSha256(imagePath));
        Assert.Equal(VivoVideoSha256, ComputeSha256(videoPath));
        return (imagePath, videoPath);
    }

    private static string ResolveFrozenSample(string filename)
    {
        Assert.Equal(P4SourceManifestSha256, ComputeSha256(FindRepositoryRootAndJoin("tests/fixtures/realsamples-manifest.json")));
        CorpusSample record = RealSampleCorpusManifest.GetRequiredSample(filename);
        string expectedHash = NormalizeHash(filename switch
        {
            "红米老款-GV1.JPG" => RedmiImageSha256,
            "oppo.jpg" => OppoImageSha256,
            _ => throw new ArgumentOutOfRangeException(nameof(filename), filename, "Unexpected P5 R6 Split sample.")
        });
        string samplePath = TestSampleResolver.ResolveSample(filename);
        Assert.True(File.Exists(samplePath), $"Required P4-v1 sample is missing: {samplePath}");
        Assert.Equal(expectedHash, NormalizeHash(record.Sha256));
        Assert.Equal(record.ByteSize, new FileInfo(samplePath).Length);
        Assert.Equal(expectedHash, ComputeSha256(samplePath));
        return samplePath;
    }

    private static string ResolveSplitOutputDirectory(string routeId, out MediaWorkspace? ephemeralWorkspace)
    {
        string? evidenceRoot = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P5R6_PUBLIC_OUTPUT_ROOT");
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            ephemeralWorkspace = null;
            return Path.Combine(evidenceRoot, routeId);
        }

        ephemeralWorkspace = new MediaWorkspace();
        return ephemeralWorkspace.RootDirectory;
    }

    private static string GetSplitRouteId(string sampleName, int formatIndex)
    {
        string sampleId = sampleName == "oppo.jpg" ? "oppo" : "redmi-gv1";
        return $"{sampleId}-split-format-{formatIndex}";
    }

    private static async Task PersistNeutralHeicInputIfRequestedAsync(
        string neutralPrimaryPath,
        string sampleName,
        int formatIndex)
    {
        string? evidenceRoot = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P5R6_NEUTRAL_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(evidenceRoot))
            return;

        string routeDirectory = Path.Combine(
            Path.GetFullPath(evidenceRoot),
            GetSplitRouteId(sampleName, formatIndex));
        Directory.CreateDirectory(routeDirectory);
        string evidencePath = Path.Combine(routeDirectory, "neutral-primary.heic");
        Assert.False(File.Exists(evidencePath), $"Refusing to overwrite existing Neutral-input evidence: {evidencePath}");
        File.Copy(neutralPrimaryPath, evidencePath, overwrite: false);
        Assert.Equal(await ComputeSha256Async(neutralPrimaryPath), await ComputeSha256Async(evidencePath));
    }

    private static string FindRepositoryRootAndJoin(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "tests", "fixtures", "realsamples-manifest.json")))
                return Path.Combine(current.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Live Photo Box repository root from the test output directory.");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static string NormalizeHash(string hash) => hash.ToUpperInvariant();

    private sealed class CapturingImageConverter(IImageConverter inner) : IImageConverter
    {
        public List<(ImageConversionRequest Request, ImageConversionResult Result)> Calls { get; } = [];

        public async Task<ImageConversionResult> ConvertAsync(
            ImageConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            ImageConversionResult result = await inner.ConvertAsync(request, cancellationToken);
            Calls.Add((request, result));
            return result;
        }
    }

    private sealed class CapturingVideoConverter(IVideoConverter inner) : IVideoConverter
    {
        public List<(VideoConversionRequest Request, VideoConversionResult Result)> Calls { get; } = [];

        public Task<VideoFacts> ProbeAsync(string videoPath, CancellationToken cancellationToken = default) =>
            inner.ProbeAsync(videoPath, cancellationToken);

        public async Task<VideoConversionResult> ConvertAsync(
            VideoConversionRequest request,
            CancellationToken cancellationToken = default)
        {
            VideoConversionResult result = await inner.ConvertAsync(request, cancellationToken);
            Calls.Add((request, result));
            return result;
        }
    }
}
