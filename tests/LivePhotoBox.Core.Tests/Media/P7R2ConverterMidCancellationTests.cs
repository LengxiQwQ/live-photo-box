using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2ConverterMidCancellationTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeRemux_CancellationAtPostHeaderCheck_AbortsOwnedStage()
    {
        string source = TestSampleResolver.ResolveSample("苹果双文件.MOV");
        string sourceShaBefore = ComputeSha256(source);
        using var workspace = new TestFixtureWorkspace();
        string outputDirectory = Path.Combine(workspace.RootDirectory, "mid-remux-cancel");
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, "cancelled.mp4");
        using var cancellation = new CancellationTokenSource();
        var probe = new RemuxCancellationProbe(cancellation, outputDirectory);

        NativeContext? context = null;
        GCHandle probeHandle = default;
        NativeResult? hookInstallResult = null;
        NativeResult? nativeResult = null;
        Exception? managedFailure = null;
        string? actualOutcome = null;

        try
        {
            // NativeContext and NativeMethods bind to the production DLL, as the
            // NativeMediaService.RemuxVideoAsync wrapper does. The existing
            // harness-only callback setter replaces this context's cancellation
            // callback so the real remux observes cancellation at its first
            // in-loop check, after creating its owned stage and writing ftyp.
            NativeContext activeContext = NativeContext.Create(cancellation.Token);
            context = activeContext;
            probeHandle = GCHandle.Alloc(probe);
            nint contextHandle = activeContext.Handle;
            NativeResult configured = InstallHarnessCancelCallback(contextHandle, GCHandle.ToIntPtr(probeHandle));
            hookInstallResult = configured;
            Assert.Equal(NativeResult.Ok, configured);

            NativeResult completed = await Task.Run(() => InvokeProductionRemux(
                contextHandle,
                source,
                output));
            nativeResult = completed;
            managedFailure = Record.Exception(() => activeContext.ThrowIfFailed(completed));
            actualOutcome = managedFailure is OperationCanceledException
                ? "OperationCanceledException"
                : managedFailure is null ? "ReturnedSuccess" : "UnexpectedException";

            Assert.Equal(NativeResult.Cancelled, completed);
            OperationCanceledException cancelled = Assert.IsType<OperationCanceledException>(managedFailure);
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref probe.CancelCheckCount));
            Assert.Equal(1, Volatile.Read(ref probe.CancellationRequested));
            Assert.Null(probe.CallbackFailure);
            var stageAtCancellation = Assert.Single(probe.StagesAtCancellation);
            Assert.StartsWith("lpb-remux-", stageAtCancellation.Name, StringComparison.Ordinal);
            Assert.True(stageAtCancellation.Bytes >= 24, $"Expected the 24-byte MP4 ftyp to be written before cancellation; observed {stageAtCancellation.Bytes} bytes.");
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(outputDirectory, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly));
            Assert.Equal(sourceShaBefore, ComputeSha256(source));
        }
        finally
        {
            // Keep the unmanaged callback target alive until the production
            // context is destroyed; its callback pointer is stored in context.
            context?.Dispose();
            if (probeHandle.IsAllocated)
                probeHandle.Free();

            WriteEvidence(new
            {
                operation = "NativeMethods.RemuxVideo",
                implementation = "Production LivePhotoBox.Native DLL; same entry point and error mapping used by NativeMediaService.RemuxVideoAsync",
                input = source,
                sourceSha256Before = sourceShaBefore,
                sourceSha256After = TryComputeSha256(source),
                targetContainer = VideoContainer.Mp4.ToString(),
                output,
                hookInstallResult = hookInstallResult?.ToString(),
                cancellationBoundary = "first lpb_context_check_cancelled in remux_video_reader loop, following owned-stage creation and ftyp write",
                cancelCheckCount = Volatile.Read(ref probe.CancelCheckCount),
                cancellationRequestedInsideNativeCallback = Volatile.Read(ref probe.CancellationRequested) != 0,
                stagesAtCancellation = probe.StagesAtCancellation,
                callbackFailure = probe.CallbackFailure,
                nativeResult = nativeResult?.ToString(),
                actualOutcome,
                managedFailure = FailureEvidence(managedFailure),
                finalOutputExists = File.Exists(output),
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedRemuxResidueAfter = Directory.GetFiles(outputDirectory, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly)
            });
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeRemux_OccupiedDestinationAndMappedStage_ReportsPublishCleanupFailure()
    {
        using var workspace = new TestFixtureWorkspace();
        string? source = null;
        string? outputDirectory = null;
        string? relocatedOutputDirectoryForHandleProbe = null;
        string? output = null;
        string? sourceShaBefore = null;
        WindowsFileIdentity? sourceIdentityBefore = null;
        string? foreignDestinationShaBefore = null;
        WindowsFileIdentity? foreignDestinationIdentityBefore = null;
        byte[] foreignDestinationBytes = [0x52, 0x32, 0x2D, 0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E];
        NativeContext? context = null;
        GCHandle probeHandle = default;
        MappedRemuxPublicationProbe? probe = null;
        MappedStageSnapshot? stageAtNativeReturn = null;
        string? sourceShaAfter = null;
        WindowsFileIdentity? sourceIdentityAfter = null;
        string? foreignDestinationShaAfter = null;
        WindowsFileIdentity? foreignDestinationIdentityAfter = null;
        FileSnapshot[]? filesBefore = null;
        FileSnapshot[]? filesAfterNativeReturn = null;
        FileSnapshot[]? filesAfterHandleProbes = null;
        string? stageSha256AtNativeReturn = null;
        WindowsFileIdentity? exclusiveReopenIdentity = null;
        string? exclusiveReopenOutcome = null;
        string? directoryHandleProbeOutcome = null;
        bool nativeContextAliveDuringHandleProbe = false;
        string? mappingReleaseOutcome = null;
        string? harnessStageCleanupOutcome = null;
        NativeResult? hookInstallResult = null;
        NativeResult? nativeResult = null;
        Exception? managedFailure = null;
        Exception? testFailure = null;
        string? nativeLastError = null;
        string? actualOutcome = null;
        string? stageDirectoryAtEvidenceSnapshot = null;
        string[]? stageResidueAtEvidenceSnapshot = null;
        const string originalPublishFailure = "Failed to publish the owned remuxed video without replacing an existing artifact.";

        try
        {
            source = TestSampleResolver.ResolveSample("苹果双文件.MOV");
            sourceShaBefore = ComputeSha256(source);
            sourceIdentityBefore = WindowsFileIdentity.Capture(source);
            long expectedStageBytes = ExpectedRemuxedMp4ByteLength(source);

            string activeOutputDirectory = Path.Combine(workspace.RootDirectory, "native-remux-publish-mapped-cleanup-failure");
            outputDirectory = activeOutputDirectory;
            Directory.CreateDirectory(activeOutputDirectory);
            string activeOutput = Path.Combine(activeOutputDirectory, "occupied.mp4");
            output = activeOutput;
            File.WriteAllBytes(activeOutput, foreignDestinationBytes);
            foreignDestinationShaBefore = ComputeSha256(activeOutput);
            foreignDestinationIdentityBefore = WindowsFileIdentity.Capture(activeOutput);
            filesBefore = SnapshotWorkspaceFiles(workspace.RootDirectory);

            probe = new MappedRemuxPublicationProbe(activeOutputDirectory, expectedStageBytes);
            NativeContext activeContext = NativeContext.Create(CancellationToken.None);
            context = activeContext;
            probeHandle = GCHandle.Alloc(probe);
            NativeResult configured = InstallHarnessMappedRemuxPublicationCallback(
                activeContext.Handle,
                GCHandle.ToIntPtr(probeHandle));
            hookInstallResult = configured;
            Assert.Equal(NativeResult.Ok, configured);

            NativeResult completed = await Task.Run(() => InvokeProductionRemux(
                activeContext.Handle,
                source,
                activeOutput));
            nativeResult = completed;
            managedFailure = Record.Exception(() => activeContext.ThrowIfFailed(completed));
            actualOutcome = managedFailure is ExtractionException
                ? "ExtractionException"
                : managedFailure is null ? "ReturnedSuccess" : "UnexpectedException";
            nativeLastError = activeContext.GetLastError();
            stageAtNativeReturn = probe.StageAtPublicationAttempt;
            sourceShaAfter = TryComputeSha256(source);
            sourceIdentityAfter = TryCaptureIdentity(source);
            foreignDestinationShaAfter = TryComputeSha256(activeOutput);
            foreignDestinationIdentityAfter = TryCaptureIdentity(activeOutput);

            Assert.Equal(NativeResult.InternalError, completed);
            ExtractionException cleanupFailure = Assert.IsType<ExtractionException>(managedFailure);
            Assert.Equal(ExtractionFailureCategory.CleanupFailed, cleanupFailure.Category);
            Assert.NotNull(nativeLastError);
            Assert.Contains("[CleanupFailed]", nativeLastError, StringComparison.Ordinal);
            Assert.Contains(originalPublishFailure, nativeLastError, StringComparison.Ordinal);
            Assert.Contains(originalPublishFailure, cleanupFailure.Message, StringComparison.Ordinal);
            Assert.Null(probe.CallbackFailure);
            Assert.True(probe.MappedViewReadSucceeded);
            Assert.True(probe.MappedLength > 0);
            Assert.Equal(expectedStageBytes, probe.MappedLength);
            Assert.True(Volatile.Read(ref probe.CheckCount) > 0);

            MappedStageSnapshot stage = Assert.IsType<MappedStageSnapshot>(stageAtNativeReturn);
            Assert.StartsWith("lpb-remux-", stage.Name, StringComparison.Ordinal);
            Assert.EndsWith(".tmp", stage.Name, StringComparison.Ordinal);
            Assert.Equal(expectedStageBytes, stage.Bytes);
            Assert.Equal(1u, stage.Identity.LinkCount);
            Assert.Contains(stage.Path, nativeLastError, StringComparison.Ordinal);
            Assert.Contains("ownedHandleIdentity.fileIndex=" + stage.Identity.FileIndex, nativeLastError, StringComparison.Ordinal);
            Assert.Contains("ownedHandleIdentity.volumeSerial=" + stage.Identity.VolumeSerialNumber, nativeLastError, StringComparison.Ordinal);
            Assert.True(File.Exists(stage.Path), "The mapped remux stage should remain after Native could not confirm disposal.");
            Assert.Equal(stage.Identity, WindowsFileIdentity.Capture(stage.Path));
            Assert.Equal(stage.FirstByte, probe.MappedView!.ReadByte(0));
            stageSha256AtNativeReturn = ComputeSha256(stage.Path);
            Assert.Equal(stage.Bytes, new FileInfo(stage.Path).Length);
            Assert.Equal(foreignDestinationIdentityBefore, foreignDestinationIdentityAfter);
            Assert.Equal(foreignDestinationShaBefore, foreignDestinationShaAfter);
            Assert.Equal(foreignDestinationBytes, File.ReadAllBytes(activeOutput));
            Assert.Equal(sourceShaBefore, sourceShaAfter);
            Assert.Equal(sourceIdentityBefore, sourceIdentityAfter);
            Assert.True(File.Exists(activeOutput));
            Assert.Equal(stage.Path, Assert.Single(Directory.GetFiles(
                activeOutputDirectory,
                "lpb-remux-*.tmp",
                SearchOption.TopDirectoryOnly)));

            filesAfterNativeReturn = SnapshotWorkspaceFiles(workspace.RootDirectory);
            Assert.NotNull(filesBefore);
            string stageSha256 = Assert.IsType<string>(stageSha256AtNativeReturn);
            var expectedFilesAfterNativeReturn = Assert.IsType<FileSnapshot[]>(filesBefore)
                .Append(new FileSnapshot(Path.GetFullPath(stage.Path), stage.Bytes, stageSha256))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(expectedFilesAfterNativeReturn, filesAfterNativeReturn);

            // Release the real mapped view only after recording the Native
            // residue, then prove Native closed its file and directory handles.
            mappingReleaseOutcome = probe.ReleaseMappedResources();
            Assert.Equal("released", mappingReleaseOutcome);
            nativeContextAliveDuringHandleProbe = context is not null;
            Assert.True(nativeContextAliveDuringHandleProbe);
            using (var exclusiveStage = new FileStream(
                stage.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                exclusiveReopenIdentity = WindowsFileIdentity.Capture(exclusiveStage.SafeFileHandle);
                Assert.Equal(stage.Identity, exclusiveReopenIdentity);
                Assert.Equal(stage.Bytes, exclusiveStage.Length);
                exclusiveReopenOutcome = "opened same owned stage identity with FileShare.None";
            }
            Assert.Equal(stageSha256AtNativeReturn, ComputeSha256(stage.Path));

            relocatedOutputDirectoryForHandleProbe = Path.Combine(
                workspace.RootDirectory,
                "native-remux-publish-mapped-cleanup-failure-handle-probe");
            try
            {
                Directory.Move(activeOutputDirectory, relocatedOutputDirectoryForHandleProbe);
                Directory.Move(relocatedOutputDirectoryForHandleProbe, activeOutputDirectory);
                directoryHandleProbeOutcome = "directory rename and restore succeeded while NativeContext remained alive";
            }
            catch (Exception exception)
            {
                directoryHandleProbeOutcome = "failed: " + exception.GetType().Name + ": " + exception.Message;
                throw;
            }
            finally
            {
                if (!Directory.Exists(activeOutputDirectory) && Directory.Exists(relocatedOutputDirectoryForHandleProbe))
                {
                    try { Directory.Move(relocatedOutputDirectoryForHandleProbe, activeOutputDirectory); }
                    catch { /* Preserve the primary assertion; evidence records the failed move probe. */ }
                }
            }

            filesAfterHandleProbes = SnapshotWorkspaceFiles(workspace.RootDirectory);
            Assert.Equal(expectedFilesAfterNativeReturn, filesAfterHandleProbes);

            stageResidueAtEvidenceSnapshot = Directory.GetFiles(
                activeOutputDirectory,
                "lpb-remux-*.tmp",
                SearchOption.TopDirectoryOnly);
            Assert.Equal(stage.Path, Assert.Single(stageResidueAtEvidenceSnapshot));
            harnessStageCleanupOutcome = CleanupMappedRemuxStageAfterEvidenceSnapshot(stage, activeOutputDirectory);
            Assert.Equal("removed after pre-cleanup evidence snapshot", harnessStageCleanupOutcome);
            Assert.False(File.Exists(stage.Path));
            Assert.Empty(Directory.GetFiles(activeOutputDirectory, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly));
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            nativeLastError ??= context?.GetLastError();
            context?.Dispose();
            if (probeHandle.IsAllocated)
                probeHandle.Free();

            sourceShaAfter ??= TryComputeSha256(source);
            sourceIdentityAfter ??= TryCaptureIdentity(source);
            foreignDestinationShaAfter ??= TryComputeSha256(output);
            foreignDestinationIdentityAfter ??= TryCaptureIdentity(output);
            stageAtNativeReturn ??= probe?.StageAtPublicationAttempt;
            if (stageSha256AtNativeReturn is null && stageAtNativeReturn is { } capturedStage && File.Exists(capturedStage.Path))
                stageSha256AtNativeReturn = TryComputeSha256(capturedStage.Path);
            if (filesAfterNativeReturn is null && !string.IsNullOrWhiteSpace(workspace.RootDirectory))
            {
                try { filesAfterNativeReturn = SnapshotWorkspaceFiles(workspace.RootDirectory); }
                catch { /* Keep the test failure primary; evidence records unavailable snapshots. */ }
            }

            stageDirectoryAtEvidenceSnapshot =
                !string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory)
                    ? outputDirectory
                    : !string.IsNullOrWhiteSpace(relocatedOutputDirectoryForHandleProbe) && Directory.Exists(relocatedOutputDirectoryForHandleProbe)
                        ? relocatedOutputDirectoryForHandleProbe
                        : outputDirectory;
            stageResidueAtEvidenceSnapshot ??= string.IsNullOrWhiteSpace(stageDirectoryAtEvidenceSnapshot) || !Directory.Exists(stageDirectoryAtEvidenceSnapshot)
                ? []
                : Directory.GetFiles(stageDirectoryAtEvidenceSnapshot, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly);
            mappingReleaseOutcome ??= probe?.ReleaseMappedResources();
            if (probe is not null && harnessStageCleanupOutcome is null)
            {
                try
                {
                    harnessStageCleanupOutcome = CleanupMappedRemuxStageAfterEvidenceSnapshot(
                        stageAtNativeReturn,
                        stageDirectoryAtEvidenceSnapshot);
                }
                catch (Exception exception)
                {
                    harnessStageCleanupOutcome = "failed: " + exception.GetType().Name + ": " + exception.Message;
                }
            }

            WriteEvidence(new
                {
                    operation = "NativeMethods.RemuxVideo",
                    implementation = "Production LivePhotoBox.Native DLL; direct production entry point used by NativeMediaService.RemuxVideoAsync",
                    sampleName = "苹果双文件.MOV",
                    source,
                    sourceSha256Before = sourceShaBefore,
                    sourceSha256After = sourceShaAfter,
                    sourceIdentityBefore,
                    sourceIdentityAfter,
                    output,
                    outputDirectory,
                    foreignDestinationBytes = Convert.ToHexString(foreignDestinationBytes),
                    foreignDestinationSha256Before = foreignDestinationShaBefore,
                    foreignDestinationSha256After = foreignDestinationShaAfter,
                    foreignDestinationIdentityBefore,
                    foreignDestinationIdentityAfter,
                    expectedOriginalPublishFailure = originalPublishFailure,
                    mappedStageFileShare = "ReadWrite | Delete",
                    mappedViewAccess = "ReadOnly",
                    mappedViewReadSucceeded = probe?.MappedViewReadSucceeded,
                    mappedLength = probe?.MappedLength,
                    mappedFirstByte = probe?.MappedFirstByte,
                    stageAtPublicationAttempt = stageAtNativeReturn,
                    callbackCheckCount = probe is null ? 0 : Volatile.Read(ref probe.CheckCount),
                    callbackFailure = probe?.CallbackFailure,
                    hookInstallResult = hookInstallResult?.ToString(),
                    nativeResult = nativeResult?.ToString(),
                    nativeLastError,
                    actualOutcome,
                    managedFailure = FailureEvidence(managedFailure),
                    cleanupFailureCategory = (managedFailure as ExtractionException)?.Category.ToString(),
                    testFailure = FailureEvidence(testFailure),
                    outputStillMatchesForeignDestination = !string.IsNullOrWhiteSpace(output) &&
                        File.Exists(output) && TryComputeSha256(output) == foreignDestinationShaBefore,
                    filesBefore,
                    filesAfterNativeReturn,
                    filesAfterHandleProbes,
                    stageSha256AtNativeReturn,
                    stageResidueAtEvidenceSnapshot,
                    stageDirectoryAtEvidenceSnapshot,
                    exclusiveReopenIdentity,
                    exclusiveReopenOutcome,
                    directoryHandleProbeOutcome,
                    nativeContextAliveDuringHandleProbe,
                    mappingReleaseOutcome,
                    harnessStageCleanupOutcome,
                    stageExistsAfterHarnessCleanup = stageAtNativeReturn is { } finalStage && File.Exists(finalStage.Path)
                }, "native-remux-publish-collision-mapped-cleanup-failure.json");
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeImageCopy_CancellationAfterNonemptyOwnedStage_AbortsWithoutPublishing()
    {
        await RunImageCancellationAtStagedPublishAsync(
            sampleName: "oppo.jpg",
            targetContainer: ImageContainer.Jpeg,
            stagePrefix: "lpb-image-copy",
            evidenceFileName: "native-image-copy-mid-cancellation.json");
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(
        1,
        "native-image-heic-encode-stage-1-cancellation.json",
        "Cancel at preservation-observation entry while the nonempty HEIC stage awaits structural color validation")]
    [InlineData(
        6,
        "native-image-heic-encode-stage-6-cancellation.json",
        "Cancel after HEIC color validation at the final pre-publication check")]
    public async Task NativeImageHeicEncode_CancellationAtValidationAndPublishChecks_AbortsWithoutPublishing(
        int stageCheckNumber,
        string evidenceFileName,
        string cancellationBoundary)
    {
        await RunImageCancellationAtStagedPublishAsync(
            sampleName: "oppo.jpg",
            targetContainer: ImageContainer.Heic,
            stagePrefix: "lpb-heic-encode",
            evidenceFileName: evidenceFileName,
            minimumStageCheckNumber: stageCheckNumber,
            expectedCancelCheckNumber: stageCheckNumber,
            cancellationBoundary: cancellationBoundary);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeImageCopy_ReadOnlyOwnedStageBeforeCancellation_AbortsWithoutResidue()
    {
        await RunImageCancellationAtStagedPublishAsync(
            sampleName: "oppo.jpg",
            targetContainer: ImageContainer.Jpeg,
            stagePrefix: "lpb-image-copy",
            evidenceFileName: "native-image-copy-readonly-before-cancel.json",
            setStageReadOnlyBeforeCancel: true);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeImageJpegEncode_CancellationAfterNonemptyOwnedStage_AbortsWithoutPublishing()
    {
        await RunImageCancellationAtStagedPublishAsync(
            sampleName: "华为Mate80.heic",
            targetContainer: ImageContainer.Jpeg,
            stagePrefix: "lpb-jpeg-encode",
            evidenceFileName: "native-image-jpeg-encode-mid-cancellation.json",
            cancellationBoundary: "Cancel after the complete lpb-jpeg-encode stage has been written, at its pre-publication check");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeImageCopy_MappedReadViewMakesCancellationCleanupFail_ReportsOriginalCancellation()
    {
        using var workspace = new MediaWorkspace();
        using var outputFixture = new TestFixtureWorkspace();
        using var cancellation = new CancellationTokenSource();
        string? originalSource = null;
        string? extractedSource = null;
        string? outputDirectory = null;
        string? relocatedOutputDirectoryForHandleProbe = null;
        string? output = null;
        InspectedSource? inspected = null;
        ExtractedMediaBundle? extracted = null;
        NativeContext? context = null;
        GCHandle probeHandle = default;
        MappedStageCancellationProbe? probe = null;
        string? originalShaBefore = null;
        WindowsFileIdentity? originalIdentityBefore = null;
        string? originalShaAfter = null;
        WindowsFileIdentity? originalIdentityAfter = null;
        string? extractedShaBefore = null;
        WindowsFileIdentity? extractedIdentityBefore = null;
        string? extractedShaAfter = null;
        WindowsFileIdentity? extractedIdentityAfter = null;
        FileSnapshot[]? filesBefore = null;
        FileSnapshot[]? filesAfter = null;
        FileSnapshot[]? outputFilesBefore = null;
        FileSnapshot[]? outputFilesAfter = null;
        string? stageSha256AtNativeReturn = null;
        string? stageSha256AfterExclusiveReopen = null;
        WindowsFileIdentity? exclusiveReopenIdentity = null;
        string? exclusiveReopenOutcome = null;
        string? directoryHandleProbeOutcome = null;
        bool nativeContextAliveDuringHandleProbe = false;
        string? mappingReleaseOutcome = null;
        string? harnessStageCleanupOutcome = null;
        NativeResult? hookInstallResult = null;
        NativeResult? nativeResult = null;
        int? reencoded = null;
        Exception? managedFailure = null;
        Exception? testFailure = null;
        string? actualOutcome = null;
        string? nativeLastError = null;
        object? authorityEvidence = null;

        try
        {
            originalSource = TestSampleResolver.ResolveSample("oppo.jpg");
            originalShaBefore = ComputeSha256(originalSource);
            originalIdentityBefore = WindowsFileIdentity.Capture(originalSource);

            // Use the production Inspector-issued plan and Extractor-issued
            // cleanup plan to obtain the actual Native image-copy input.
            inspected = await new SourceInspector().InspectWithPlanAsync(originalSource);
            extracted = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                originalSource,
                null,
                workspace);
            Assert.NotNull(extracted.CleanupPlan);
            string activeExtractedSource = extracted.PrimaryImage.Path;
            extractedSource = activeExtractedSource;
            extractedShaBefore = ComputeSha256(activeExtractedSource);
            extractedIdentityBefore = WindowsFileIdentity.Capture(activeExtractedSource);
            authorityEvidence = new
            {
                chain = "SourceInspector.InspectWithPlanAsync -> SourceExtractor.ExtractAsync",
                sourceProtocol = extracted.SourceFacts.Protocol.ToString(),
                inspectorIssuedExtractionPlan = true,
                extractorIssuedCleanupPlan = extracted.CleanupPlan is not null,
                extractedPrimaryImage = extractedSource,
                extractedPrimaryContainer = extracted.PrimaryImage.ImageContainer.ToString()
            };

            string activeOutputDirectory = Path.Combine(outputFixture.RootDirectory, "native-image-mapped-cleanup-failure");
            outputDirectory = activeOutputDirectory;
            Directory.CreateDirectory(activeOutputDirectory);
            string activeOutput = Path.Combine(activeOutputDirectory, "cancelled-image.jpg");
            output = activeOutput;
            filesBefore = SnapshotWorkspaceFiles(workspace.RootDirectory);
            outputFilesBefore = SnapshotWorkspaceFiles(outputFixture.RootDirectory);

            probe = new MappedStageCancellationProbe(cancellation, activeOutputDirectory);
            NativeContext activeContext = NativeContext.Create(cancellation.Token);
            context = activeContext;
            probeHandle = GCHandle.Alloc(probe);
            NativeResult configured = InstallHarnessMappedImageCancelCallback(
                activeContext.Handle,
                GCHandle.ToIntPtr(probeHandle));
            hookInstallResult = configured;
            Assert.Equal(NativeResult.Ok, configured);

            (NativeResult Result, int Reencoded) operation = await Task.Run(() =>
            {
                NativeResult result = NativeMethods.ConvertImage(
                    activeContext.Handle,
                    activeExtractedSource,
                    activeOutput,
                    (int)ImageContainer.Jpeg,
                    90,
                    out int outputReencoded);
                return (result, outputReencoded);
            });
            nativeResult = operation.Result;
            reencoded = operation.Reencoded;
            managedFailure = Record.Exception(() => activeContext.ThrowIfFailed(operation.Result));
            actualOutcome = managedFailure is ExtractionException
                ? "ExtractionException"
                : managedFailure is OperationCanceledException
                    ? "OperationCanceledException"
                    : managedFailure is null ? "ReturnedSuccess" : "UnexpectedException";
            nativeLastError = activeContext.GetLastError();
            originalShaAfter = TryComputeSha256(originalSource);
            originalIdentityAfter = TryCaptureIdentity(originalSource);
            extractedShaAfter = TryComputeSha256(extractedSource);
            extractedIdentityAfter = TryCaptureIdentity(extractedSource);
            filesAfter = SnapshotWorkspaceFiles(workspace.RootDirectory);
            outputFilesAfter = SnapshotWorkspaceFiles(outputFixture.RootDirectory);

            Assert.Equal(NativeResult.InternalError, operation.Result);
            ExtractionException cleanupFailure = Assert.IsType<ExtractionException>(managedFailure);
            Assert.Equal(ExtractionFailureCategory.CleanupFailed, cleanupFailure.Category);
            Assert.Equal(ExtractionFailureCategory.Cancelled, cleanupFailure.OriginalCategory);
            Assert.IsType<OperationCanceledException>(cleanupFailure.InnerException);
            OperationCanceledException originalCancellation = Assert.IsType<OperationCanceledException>(cleanupFailure.InnerException);
            Assert.Equal(cancellation.Token, originalCancellation.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref probe.CancellationRequested));
            Assert.Null(probe.CallbackFailure);
            Assert.True(probe.MappedView is not null && probe.MappedViewReadSucceeded);
            Assert.True(probe.MappedLength > 0);

            MappedStageSnapshot stage = Assert.IsType<MappedStageSnapshot>(probe.StageAtCancellation);
            Assert.NotNull(nativeLastError);
            Assert.Contains(stage.Path, nativeLastError, StringComparison.Ordinal);
            Assert.Contains("ownedHandleIdentity.fileIndex=" + stage.Identity.FileIndex, nativeLastError, StringComparison.Ordinal);
            Assert.Contains("ownedHandleIdentity.volumeSerial=" + stage.Identity.VolumeSerialNumber, nativeLastError, StringComparison.Ordinal);
            Assert.StartsWith("lpb-image-copy-", stage.Name, StringComparison.Ordinal);
            Assert.EndsWith(".tmp", stage.Name, StringComparison.Ordinal);
            Assert.True(stage.Bytes > 0);
            Assert.Equal(1u, stage.Identity.LinkCount);
            Assert.False(File.Exists(output));
            Assert.True(File.Exists(stage.Path), "The mapped stage should remain after Native failed to dispose of it.");
            Assert.Equal(stage.Identity, WindowsFileIdentity.Capture(stage.Path));
            stageSha256AtNativeReturn = ComputeSha256(stage.Path);
            Assert.Equal(stage.Bytes, new FileInfo(stage.Path).Length);
            Assert.Equal(stage.FirstByte, probe.MappedView!.ReadByte(0));
            Assert.Equal(stage.Path, Assert.Single(Directory.GetFiles(
                activeOutputDirectory,
                "lpb-image-copy-*.tmp",
                SearchOption.TopDirectoryOnly)));

            Assert.Equal(originalShaBefore, originalShaAfter);
            Assert.Equal(originalIdentityBefore, originalIdentityAfter);
            Assert.Equal(extractedShaBefore, extractedShaAfter);
            Assert.Equal(extractedIdentityBefore, extractedIdentityAfter);

            // The view is the real blocker. Release it only after capturing
            // Native's residue, then prove both owned handles have closed.
            mappingReleaseOutcome = probe.ReleaseMappedResources();
            Assert.Equal("released", mappingReleaseOutcome);
            nativeContextAliveDuringHandleProbe = context is not null;
            using (var exclusiveStage = new FileStream(
                stage.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                WindowsFileIdentity openedIdentity = WindowsFileIdentity.Capture(exclusiveStage.SafeFileHandle);
                exclusiveReopenIdentity = openedIdentity;
                Assert.Equal(stage.Identity, openedIdentity);
                Assert.Equal(stage.Bytes, exclusiveStage.Length);
                exclusiveReopenOutcome = "opened same stage identity with FileShare.None";
            }
            stageSha256AfterExclusiveReopen = ComputeSha256(stage.Path);
            Assert.Equal(stageSha256AtNativeReturn, stageSha256AfterExclusiveReopen);
            Assert.Equal(stage.Identity, WindowsFileIdentity.Capture(stage.Path));

            relocatedOutputDirectoryForHandleProbe = Path.Combine(
                outputFixture.RootDirectory,
                "native-image-mapped-cleanup-failure-handle-probe");
            filesAfter = null;
            try
            {
                Directory.Move(outputDirectory, relocatedOutputDirectoryForHandleProbe);
                Directory.Move(relocatedOutputDirectoryForHandleProbe, outputDirectory);
                directoryHandleProbeOutcome = "directory rename and restore succeeded while NativeContext remained alive";
            }
            catch (Exception exception)
            {
                directoryHandleProbeOutcome = "failed: " + exception.GetType().Name + ": " + exception.Message;
                throw;
            }
            finally
            {
                if (!Directory.Exists(outputDirectory) && Directory.Exists(relocatedOutputDirectoryForHandleProbe))
                {
                    try { Directory.Move(relocatedOutputDirectoryForHandleProbe, outputDirectory); }
                    catch { /* Preserve the primary test result; evidence records the failed move probe. */ }
                }
            }

            filesAfter = SnapshotWorkspaceFiles(workspace.RootDirectory);
            outputFilesAfter = SnapshotWorkspaceFiles(outputFixture.RootDirectory);
            Assert.NotNull(filesBefore);
            Assert.NotNull(filesAfter);
            FileSnapshot[] sourceFilesBefore = Assert.IsType<FileSnapshot[]>(filesBefore);
            FileSnapshot[] sourceFilesAfter = Assert.IsType<FileSnapshot[]>(filesAfter);
            Assert.Equal(sourceFilesBefore, sourceFilesAfter);
            FileSnapshot[] outputFilesAtBaseline = Assert.IsType<FileSnapshot[]>(outputFilesBefore);
            FileSnapshot[] outputFilesAfterNative = Assert.IsType<FileSnapshot[]>(outputFilesAfter);
            string expectedStageSha256 = Assert.IsType<string>(stageSha256AtNativeReturn);
            var expectedFilesAfter = outputFilesAtBaseline
                .Append(new FileSnapshot(Path.GetFullPath(stage.Path), stage.Bytes, expectedStageSha256))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.Equal(expectedFilesAfter, outputFilesAfterNative);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            context?.Dispose();
            if (probeHandle.IsAllocated)
                probeHandle.Free();

            originalShaAfter ??= TryComputeSha256(originalSource);
            originalIdentityAfter ??= TryCaptureIdentity(originalSource);
            extractedShaAfter ??= TryComputeSha256(extractedSource);
            extractedIdentityAfter ??= TryCaptureIdentity(extractedSource);
            if (!string.IsNullOrWhiteSpace(outputDirectory) &&
                !Directory.Exists(outputDirectory) &&
                !string.IsNullOrWhiteSpace(relocatedOutputDirectoryForHandleProbe) &&
                Directory.Exists(relocatedOutputDirectoryForHandleProbe))
            {
                try { Directory.Move(relocatedOutputDirectoryForHandleProbe, outputDirectory); }
                catch { /* Evidence and cleanup below follow the current directory name. */ }
            }
            if (filesAfter is null && !string.IsNullOrWhiteSpace(workspace.RootDirectory))
            {
                try { filesAfter = SnapshotWorkspaceFiles(workspace.RootDirectory); }
                catch { /* Evidence records a missing snapshot if the tree could not be read. */ }
            }
            if (!string.IsNullOrWhiteSpace(outputFixture.RootDirectory))
            {
                try { outputFilesAfter = SnapshotWorkspaceFiles(outputFixture.RootDirectory); }
                catch { /* Evidence records a missing output snapshot if the tree could not be read. */ }
            }

            string? stageDirectoryAtEvidenceSnapshot =
                !string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory)
                    ? outputDirectory
                    : !string.IsNullOrWhiteSpace(relocatedOutputDirectoryForHandleProbe) && Directory.Exists(relocatedOutputDirectoryForHandleProbe)
                        ? relocatedOutputDirectoryForHandleProbe
                        : outputDirectory;
            string[] stageResidueAtEvidenceSnapshot =
                string.IsNullOrWhiteSpace(stageDirectoryAtEvidenceSnapshot) || !Directory.Exists(stageDirectoryAtEvidenceSnapshot)
                    ? Array.Empty<string>()
                    : Directory.GetFiles(stageDirectoryAtEvidenceSnapshot, "lpb-image-copy-*.tmp", SearchOption.TopDirectoryOnly);
            if (stageSha256AtNativeReturn is null && probe?.StageAtCancellation is { } stageAtCancellation &&
                File.Exists(stageAtCancellation.Path))
            {
                stageSha256AtNativeReturn = TryComputeSha256(stageAtCancellation.Path);
            }

            mappingReleaseOutcome ??= probe?.ReleaseMappedResources();
            if (probe is not null)
            {
                try
                {
                    harnessStageCleanupOutcome = CleanupMappedOwnedStageAfterEvidenceSnapshot(
                        probe.StageAtCancellation,
                        stageDirectoryAtEvidenceSnapshot);
                }
                catch (Exception exception)
                {
                    harnessStageCleanupOutcome = "failed: " + exception.GetType().Name + ": " + exception.Message;
                }
            }

            try
            {
                extracted?.CleanupPlan?.Dispose();
                inspected?.Dispose();
            }
            finally
            {
                WriteEvidence(new
                {
                    operation = "NativeMethods.ConvertImage",
                    implementation = "Production LivePhotoBox.Native DLL; direct production entry point used by NativeMediaService.ConvertImageAsync",
                    sampleName = "oppo.jpg",
                    originalSource,
                    originalSha256Before = originalShaBefore,
                    originalSha256After = originalShaAfter,
                    originalIdentityBefore,
                    originalIdentityAfter,
                    extractedSource,
                    extractedSha256Before = extractedShaBefore,
                    extractedSha256After = extractedShaAfter,
                    extractedIdentityBefore,
                    extractedIdentityAfter,
                    authorityEvidence,
                    targetContainer = ImageContainer.Jpeg.ToString(),
                    output,
                    outputDirectory,
                    stagePrefix = "lpb-image-copy",
                    cancellationBoundary = "Read-only memory-mapped view opened over a nonempty owned image-copy stage; callback then requested CTS cancellation without throwing",
                    hookInstallResult = hookInstallResult?.ToString(),
                    cancelCheckCount = probe is null ? 0 : Volatile.Read(ref probe.CancelCheckCount),
                    cancellationRequestedInsideNativeCallback = probe is not null && Volatile.Read(ref probe.CancellationRequested) != 0,
                    mappedViewReadSucceeded = probe?.MappedViewReadSucceeded,
                    mappedStageFileShare = "ReadWrite | Delete",
                    mappedViewAccess = "ReadOnly",
                    mappedLength = probe?.MappedLength,
                    mappedFirstByte = probe?.MappedFirstByte,
                    stageAtCancellation = probe?.StageAtCancellation,
                    callbackFailure = probe?.CallbackFailure,
                    nativeResult = nativeResult?.ToString(),
                    nativeLastError,
                    reencoded,
                    actualOutcome,
                    managedFailure = FailureEvidence(managedFailure),
                    cleanupFailureCategory = (managedFailure as ExtractionException)?.Category.ToString(),
                    originalFailureCategory = (managedFailure as ExtractionException)?.OriginalCategory?.ToString(),
                    originalFailureInner = FailureEvidence(managedFailure?.InnerException),
                    testFailure = FailureEvidence(testFailure),
                    finalOutputExists = !string.IsNullOrWhiteSpace(output) && File.Exists(output),
                    filesBefore,
                    filesAfter,
                    outputFilesBefore,
                    outputFilesAfter,
                    stageSha256AtNativeReturn,
                    stageSha256AfterExclusiveReopen,
                    stageResidueAtEvidenceSnapshot,
                    stageDirectoryAtEvidenceSnapshot,
                    stagePathAtEvidenceSnapshot = probe?.StageAtCancellation is { } capturedStage
                        ? (stageDirectoryAtEvidenceSnapshot is null
                            ? capturedStage.Path
                            : Path.Combine(stageDirectoryAtEvidenceSnapshot, capturedStage.Name))
                        : null,
                    exclusiveReopenIdentity,
                    exclusiveReopenOutcome,
                    directoryHandleProbeOutcome,
                    nativeContextAliveDuringHandleProbe,
                    mappingReleaseOutcome,
                    harnessStageCleanupOutcome
                }, "native-image-copy-mapped-disposition-failure.json");
            }
        }
    }

    private static async Task RunImageCancellationAtStagedPublishAsync(
        string sampleName,
        ImageContainer targetContainer,
        string stagePrefix,
        string evidenceFileName,
        int minimumStageCheckNumber = 1,
        bool setStageReadOnlyBeforeCancel = false,
        int? expectedCancelCheckNumber = null,
        string? cancellationBoundary = null)
    {
        using var workspace = new MediaWorkspace();
        using var outputFixture = new TestFixtureWorkspace();
        string? originalSource = null;
        string? extractedSource = null;
        string? outputDirectory = null;
        string? output = null;
        InspectedSource? inspected = null;
        ExtractedMediaBundle? extracted = null;
        CancellationTokenSource? cancellation = null;
        NativeContext? context = null;
        GCHandle probeHandle = default;
        ImageCancellationProbe? probe = null;
        string? originalShaBefore = null;
        WindowsFileIdentity? originalIdentityBefore = null;
        string? extractedShaBefore = null;
        WindowsFileIdentity? extractedIdentityBefore = null;
        FileSnapshot[]? filesBefore = null;
        FileSnapshot[]? filesAfter = null;
        FileSnapshot[]? outputFilesBefore = null;
        FileSnapshot[]? outputFilesAfter = null;
        NativeResult? hookInstallResult = null;
        NativeResult? nativeResult = null;
        int? reencoded = null;
        Exception? managedFailure = null;
        Exception? testFailure = null;
        string? actualOutcome = null;
        object? authorityEvidence = null;

        try
        {
            originalSource = TestSampleResolver.ResolveSample(sampleName);
            originalShaBefore = ComputeSha256(originalSource);
            originalIdentityBefore = WindowsFileIdentity.Capture(originalSource);

            // The media input comes from the production Inspector -> Extractor
            // authority chain. This avoids treating raw sample facts or a test
            // harness import as production-issued extraction authority.
            inspected = await new SourceInspector().InspectWithPlanAsync(originalSource);
            extracted = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                originalSource,
                null,
                workspace);
            Assert.NotNull(extracted.CleanupPlan);
            string activeExtractedSource = extracted.PrimaryImage.Path;
            extractedSource = activeExtractedSource;
            extractedShaBefore = ComputeSha256(activeExtractedSource);
            extractedIdentityBefore = WindowsFileIdentity.Capture(activeExtractedSource);
            authorityEvidence = new
            {
                chain = "SourceInspector.InspectWithPlanAsync -> SourceExtractor.ExtractAsync",
                sourceProtocol = extracted.SourceFacts.Protocol.ToString(),
                inspectorIssuedExtractionPlan = true,
                extractorIssuedCleanupPlan = extracted.CleanupPlan is not null,
                extractedPrimaryImage = extractedSource,
                extractedPrimaryContainer = extracted.PrimaryImage.ImageContainer.ToString()
            };

            string activeOutputDirectory = Path.Combine(outputFixture.RootDirectory, "native-image-mid-cancel");
            outputDirectory = activeOutputDirectory;
            Directory.CreateDirectory(activeOutputDirectory);
            string extension = targetContainer == ImageContainer.Jpeg ? ".jpg" : ".heic";
            string activeOutput = Path.Combine(activeOutputDirectory, "cancelled-image" + extension);
            output = activeOutput;
            filesBefore = SnapshotWorkspaceFiles(workspace.RootDirectory);
            outputFilesBefore = SnapshotWorkspaceFiles(outputFixture.RootDirectory);

            cancellation = new CancellationTokenSource();
            probe = new ImageCancellationProbe(
                cancellation,
                outputDirectory,
                stagePrefix,
                minimumStageCheckNumber,
                setStageReadOnlyBeforeCancel);
            NativeContext activeContext = NativeContext.Create(cancellation.Token);
            context = activeContext;
            probeHandle = GCHandle.Alloc(probe);
            NativeResult configured = InstallHarnessImageCancelCallback(
                activeContext.Handle,
                GCHandle.ToIntPtr(probeHandle));
            hookInstallResult = configured;
            Assert.Equal(NativeResult.Ok, configured);

            (NativeResult Result, int Reencoded) operation = await Task.Run(() =>
            {
                NativeResult result = NativeMethods.ConvertImage(
                    activeContext.Handle,
                    activeExtractedSource,
                    activeOutput,
                    (int)targetContainer,
                    90,
                    out int outputReencoded);
                return (result, outputReencoded);
            });
            NativeResult completed = operation.Result;
            nativeResult = completed;
            reencoded = operation.Reencoded;
            managedFailure = Record.Exception(() => activeContext.ThrowIfFailed(completed));
            actualOutcome = managedFailure is OperationCanceledException
                ? "OperationCanceledException"
                : managedFailure is null ? "ReturnedSuccess" : "UnexpectedException";

            Assert.Equal(NativeResult.Cancelled, completed);
            OperationCanceledException cancelled = Assert.IsType<OperationCanceledException>(managedFailure);
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            Assert.True(Volatile.Read(ref probe.CancelCheckCount) > 0);
            Assert.Equal(1, Volatile.Read(ref probe.CancellationRequested));
            Assert.Null(probe.CallbackFailure);
            ImageStageSnapshot stageAtCancellation = Assert.IsType<ImageStageSnapshot>(probe.StageAtCancellation);
            Assert.StartsWith(stagePrefix + "-", stageAtCancellation.Name, StringComparison.Ordinal);
            Assert.True(stageAtCancellation.Bytes > 0, "Cancellation must be requested only after the owned stage has nonzero bytes.");
            Assert.True(stageAtCancellation.CancelCheckNumber >= minimumStageCheckNumber);
            if (expectedCancelCheckNumber.HasValue)
                Assert.Equal(expectedCancelCheckNumber.Value, stageAtCancellation.CancelCheckNumber);
            Assert.Equal(1u, stageAtCancellation.Identity.LinkCount);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(outputDirectory, stagePrefix + "-*.tmp", SearchOption.TopDirectoryOnly));

            Assert.Equal(originalShaBefore, ComputeSha256(originalSource));
            Assert.Equal(originalIdentityBefore, WindowsFileIdentity.Capture(originalSource));
            Assert.Equal(extractedShaBefore, ComputeSha256(extractedSource));
            Assert.Equal(extractedIdentityBefore, WindowsFileIdentity.Capture(extractedSource));
            filesAfter = SnapshotWorkspaceFiles(workspace.RootDirectory);
            outputFilesAfter = SnapshotWorkspaceFiles(outputFixture.RootDirectory);
            Assert.NotNull(filesBefore);
            Assert.NotNull(filesAfter);
            Assert.Equal(filesBefore, filesAfter);
            FileSnapshot[] outputFilesAtBaseline = Assert.IsType<FileSnapshot[]>(outputFilesBefore);
            FileSnapshot[] outputFilesAfterNative = Assert.IsType<FileSnapshot[]>(outputFilesAfter);
            Assert.Equal(outputFilesAtBaseline, outputFilesAfterNative);

        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            // The callback target remains alive until NativeContext has dropped
            // the callback pointer registered in its test-harness setter.
            context?.Dispose();
            if (probeHandle.IsAllocated)
                probeHandle.Free();

            // Capture the production outcome before test-owned cleanup can
            // remove extracted files or the deliberate ReadOnly stage residue.
            if (filesAfter is null && !string.IsNullOrWhiteSpace(workspace.RootDirectory))
            {
                try { filesAfter = SnapshotWorkspaceFiles(workspace.RootDirectory); }
                catch { /* Keep the original test outcome; evidence records unavailable snapshots. */ }
            }
            if (outputFilesAfter is null && !string.IsNullOrWhiteSpace(outputFixture.RootDirectory))
            {
                try { outputFilesAfter = SnapshotWorkspaceFiles(outputFixture.RootDirectory); }
                catch { /* Evidence records unavailable output snapshots. */ }
            }

            string[] ownedStageResidueBeforeHarnessCleanup =
                string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory)
                    ? Array.Empty<string>()
                    : Directory.GetFiles(outputDirectory, stagePrefix + "-*.tmp", SearchOption.TopDirectoryOnly);
            string? readOnlyStageHarnessCleanup = null;
            if (setStageReadOnlyBeforeCancel)
            {
                try
                {
                    readOnlyStageHarnessCleanup = CleanupReadOnlyOwnedStageAfterEvidenceSnapshot(
                        probe?.StageAtCancellation,
                        outputDirectory,
                        stagePrefix);
                }
                catch (Exception exception)
                {
                    readOnlyStageHarnessCleanup = "failed: " + exception.GetType().Name + ": " + exception.Message;
                }
            }

            try
            {
                extracted?.CleanupPlan?.Dispose();
                inspected?.Dispose();
            }
            finally
            {
                WriteEvidence(new
                {
                    operation = "NativeMethods.ConvertImage",
                    implementation = "Production LivePhotoBox.Native DLL; direct production entry point used by NativeMediaService.ConvertImageAsync",
                    sampleName,
                    originalSource,
                    originalSha256Before = originalShaBefore,
                    originalSha256After = TryComputeSha256(originalSource),
                    originalIdentityBefore,
                    originalIdentityAfter = TryCaptureIdentity(originalSource),
                    extractedSource,
                    extractedSha256Before = extractedShaBefore,
                    extractedSha256After = TryComputeSha256(extractedSource),
                    extractedIdentityBefore,
                    extractedIdentityAfter = TryCaptureIdentity(extractedSource),
                    authorityEvidence,
                    targetContainer = targetContainer.ToString(),
                    output,
                    outputDirectory,
                    stagePrefix,
                    minimumStageCheckNumber,
                    setStageReadOnlyBeforeCancel,
                    expectedCancelCheckNumber,
                    stageAttributesBeforeReadOnly = probe?.StageAttributesBeforeReadOnly?.ToString(),
                    cancellationBoundary = cancellationBoundary ?? $"Native image publish check after nonempty {stagePrefix} owned staging bytes are ready and immediately before publish_no_replace",
                    hookInstallResult = hookInstallResult?.ToString(),
                    cancelCheckCount = probe is null ? 0 : Volatile.Read(ref probe.CancelCheckCount),
                    cancellationRequestedInsideNativeCallback = probe is not null && Volatile.Read(ref probe.CancellationRequested) != 0,
                    stageAtCancellation = probe?.StageAtCancellation,
                    callbackFailure = probe?.CallbackFailure,
                    nativeResult = nativeResult?.ToString(),
                    reencoded,
                    actualOutcome,
                    managedFailure = FailureEvidence(managedFailure),
                    testFailure = FailureEvidence(testFailure),
                    finalOutputExists = !string.IsNullOrWhiteSpace(output) && File.Exists(output),
                    filesBefore,
                    filesAfter,
                    outputFilesBefore,
                    outputFilesAfter,
                    ownedStageResidueAfter = ownedStageResidueBeforeHarnessCleanup,
                    readOnlyStageHarnessCleanup
                }, evidenceFileName);
            }
        }
    }

    private static string CleanupReadOnlyOwnedStageAfterEvidenceSnapshot(
        ImageStageSnapshot? stage,
        string? outputDirectory,
        string stagePrefix)
    {
        if (stage is null)
            return "not-needed: callback did not capture an owned stage";
        if (string.IsNullOrWhiteSpace(outputDirectory) ||
            !string.Equals(Path.GetDirectoryName(stage.Path), outputDirectory, StringComparison.OrdinalIgnoreCase) ||
            !stage.Name.StartsWith(stagePrefix + "-", StringComparison.Ordinal) ||
            !stage.Name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return "not-removed: captured path did not match the test output directory and stage prefix";
        }
        if (!File.Exists(stage.Path))
            return "already-absent";
        if (WindowsFileIdentity.Capture(stage.Path) != stage.Identity)
            return "not-removed: stage file identity changed after cancellation";

        FileAttributes currentAttributes = File.GetAttributes(stage.Path);
        if ((currentAttributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(stage.Path, currentAttributes & ~FileAttributes.ReadOnly);
        File.Delete(stage.Path);
        return File.Exists(stage.Path) ? "failed: stage still exists after harness cleanup" : "removed after pre-cleanup evidence snapshot";
    }

    private static string CleanupMappedOwnedStageAfterEvidenceSnapshot(
        MappedStageSnapshot? stage,
        string? outputDirectory)
    {
        if (stage is null)
            return "not-needed: callback did not capture an owned stage";
        if (string.IsNullOrWhiteSpace(outputDirectory) ||
            !stage.Name.StartsWith("lpb-image-copy-", StringComparison.Ordinal) ||
            !stage.Name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return "not-removed: captured path did not match the test output directory and image-copy stage prefix";
        }
        string stagePath = Path.Combine(outputDirectory, stage.Name);
        if (!File.Exists(stagePath) && File.Exists(stage.Path))
            stagePath = stage.Path;
        if (!File.Exists(stagePath))
            return "already-absent";
        if (!string.Equals(Path.GetDirectoryName(stagePath), outputDirectory, StringComparison.OrdinalIgnoreCase))
            return "not-removed: resolved stage path was outside the test output directory";
        if (WindowsFileIdentity.Capture(stagePath) != stage.Identity)
            return "not-removed: stage file identity changed after cancellation";

        File.Delete(stagePath);
        return File.Exists(stagePath) ? "failed: stage still exists after harness cleanup" : "removed after pre-cleanup evidence snapshot";
    }

    private static string CleanupMappedRemuxStageAfterEvidenceSnapshot(
        MappedStageSnapshot? stage,
        string? outputDirectory)
    {
        if (stage is null)
            return "not-needed: callback did not capture an owned remux stage";
        if (string.IsNullOrWhiteSpace(outputDirectory) ||
            !stage.Name.StartsWith("lpb-remux-", StringComparison.Ordinal) ||
            !stage.Name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return "not-removed: captured path did not match the test output directory and remux stage prefix";
        }

        string stagePath = Path.Combine(outputDirectory, stage.Name);
        if (!File.Exists(stagePath) && File.Exists(stage.Path))
            stagePath = stage.Path;
        if (!File.Exists(stagePath))
            return "already-absent";
        if (!string.Equals(Path.GetDirectoryName(stagePath), outputDirectory, StringComparison.OrdinalIgnoreCase))
            return "not-removed: resolved stage path was outside the test output directory";
        if (WindowsFileIdentity.Capture(stagePath) != stage.Identity)
            return "not-removed: stage file identity changed after Native returned";

        File.Delete(stagePath);
        return File.Exists(stagePath) ? "failed: stage still exists after harness cleanup" : "removed after pre-cleanup evidence snapshot";
    }

    private static long ExpectedRemuxedMp4ByteLength(string source)
    {
        using var input = File.OpenRead(source);
        Span<byte> header = stackalloc byte[16];
        input.ReadExactly(header[..8]);
        if (!header[4..8].SequenceEqual("ftyp"u8))
            throw new InvalidDataException("The remux test fixture must begin with an ISO-BMFF ftyp box.");

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        long oldFtypSize;
        if (size32 == 1)
        {
            input.ReadExactly(header[8..16]);
            ulong extendedSize = BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
            if (extendedSize > long.MaxValue)
                throw new InvalidDataException("The remux test fixture ftyp box exceeds the supported file length.");
            oldFtypSize = (long)extendedSize;
        }
        else if (size32 == 0)
        {
            oldFtypSize = input.Length;
        }
        else
        {
            oldFtypSize = size32;
        }

        if (oldFtypSize < 8 || oldFtypSize > input.Length)
            throw new InvalidDataException("The remux test fixture has an invalid ftyp box length.");

        return checked(input.Length + 24L - oldFtypSize);
    }

    private static unsafe NativeResult InstallHarnessCancelCallback(nint context, nint userData)
    {
        delegate* unmanaged[Cdecl]<nint, int> callback = &CancelAtRemuxCheck;
        return TestHarnessNativeMethods.TestSetCancelCallback(context, (nint)callback, userData);
    }

    private static unsafe NativeResult InstallHarnessImageCancelCallback(nint context, nint userData)
    {
        delegate* unmanaged[Cdecl]<nint, int> callback = &CancelWhenOwnedImageStageHasBytes;
        return TestHarnessNativeMethods.TestSetCancelCallback(context, (nint)callback, userData);
    }

    private static unsafe NativeResult InstallHarnessMappedImageCancelCallback(nint context, nint userData)
    {
        delegate* unmanaged[Cdecl]<nint, int> callback = &MapOwnedImageStageAndCancel;
        return TestHarnessNativeMethods.TestSetCancelCallback(context, (nint)callback, userData);
    }

    private static unsafe NativeResult InstallHarnessMappedRemuxPublicationCallback(nint context, nint userData)
    {
        delegate* unmanaged[Cdecl]<nint, int> callback = &MapCompletedRemuxStage;
        return TestHarnessNativeMethods.TestSetCancelCallback(context, (nint)callback, userData);
    }

    private static NativeResult InvokeProductionRemux(nint context, string source, string output) =>
        NativeMethods.RemuxVideo(context, source, output, (int)VideoContainer.Mp4);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CancelAtRemuxCheck(nint userData)
    {
        if (userData == nint.Zero)
            return 0;

        RemuxCancellationProbe? probe = null;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (!handle.IsAllocated || handle.Target is not RemuxCancellationProbe target)
                return 0;

            probe = target;
            int checkCount = Interlocked.Increment(ref probe.CancelCheckCount);
            if (checkCount != 1)
                return probe.Cancellation.Token.IsCancellationRequested ? 1 : 0;

            try
            {
                probe.StagesAtCancellation = Directory
                    .GetFiles(probe.OutputDirectory, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly)
                    .Select(path => new StageSnapshot(Path.GetFileName(path), new FileInfo(path).Length))
                    .ToArray();
            }
            catch (Exception exception)
            {
                // The callback must not throw through the C ABI. Still request
                // cancellation so the test reports the observation failure.
                probe.CallbackFailure = exception.ToString();
            }

            probe.Cancellation.Cancel();
            Volatile.Write(ref probe.CancellationRequested, 1);
            return 1;
        }
        catch (Exception exception)
        {
            if (probe is not null)
            {
                probe.CallbackFailure = exception.ToString();
                try
                {
                    probe.Cancellation.Cancel();
                    Volatile.Write(ref probe.CancellationRequested, 1);
                }
                catch
                {
                    // Never allow a test callback exception to cross the ABI.
                }
                return 1;
            }

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CancelWhenOwnedImageStageHasBytes(nint userData)
    {
        if (userData == nint.Zero)
            return 0;

        ImageCancellationProbe? probe = null;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (!handle.IsAllocated || handle.Target is not ImageCancellationProbe target)
                return 0;

            probe = target;
            int checkNumber = Interlocked.Increment(ref probe.CancelCheckCount);
            if (probe.Cancellation.IsCancellationRequested)
                return 1;

            foreach (string path in Directory.GetFiles(
                probe.OutputDirectory,
                probe.StagePrefix + "-*.tmp",
                SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                long bytes = info.Length;
                if (bytes <= 0 || checkNumber < probe.MinimumStageCheckNumber)
                    continue;

                WindowsFileIdentity stageIdentity = WindowsFileIdentity.Capture(path);
                if (probe.SetStageReadOnlyBeforeCancel)
                {
                    FileAttributes originalAttributes = File.GetAttributes(path);
                    probe.StageAttributesBeforeReadOnly = originalAttributes;
                    File.SetAttributes(path, originalAttributes | FileAttributes.ReadOnly);
                    stageIdentity = WindowsFileIdentity.Capture(path);
                }

                probe.StageAtCancellation = new ImageStageSnapshot(
                    Path.GetFileName(path),
                    path,
                    bytes,
                    checkNumber,
                    stageIdentity);
                probe.Cancellation.Cancel();
                Volatile.Write(ref probe.CancellationRequested, 1);
                return 1;
            }

            // Image conversion may poll before it creates its output stage.
            // Let those checks pass and cancel only at the post-write publish
            // checkpoint where this exact owned stage is visible and nonempty.
            return 0;
        }
        catch (Exception exception)
        {
            if (probe is not null)
                probe.CallbackFailure = exception.ToString();
            // Never throw a managed callback failure through the C ABI. A
            // missed stage observation makes the test fail at its assertions.
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MapOwnedImageStageAndCancel(nint userData)
    {
        if (userData == nint.Zero)
            return 0;

        MappedStageCancellationProbe? probe = null;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (!handle.IsAllocated || handle.Target is not MappedStageCancellationProbe target)
                return 0;

            probe = target;
            Interlocked.Increment(ref probe.CancelCheckCount);
            if (probe.Cancellation.IsCancellationRequested)
                return 1;

            foreach (string path in Directory.GetFiles(
                probe.OutputDirectory,
                "lpb-image-copy-*.tmp",
                SearchOption.TopDirectoryOnly))
            {
                long bytes = new FileInfo(path).Length;
                if (bytes <= 0)
                    continue;

                // Keep both the explicitly shared file stream and a read-only
                // mapped view alive while asking production Native to cancel.
                var stageStream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                probe.StageStream = stageStream;
                MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                    stageStream,
                    mapName: null,
                    capacity: 0,
                    access: MemoryMappedFileAccess.Read,
                    inheritability: HandleInheritability.None,
                    leaveOpen: true);
                probe.Mapping = mapping;
                MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
                    0,
                    bytes,
                    MemoryMappedFileAccess.Read);
                probe.MappedView = view;
                byte firstByte = view.ReadByte(0);
                WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
                probe.MappedLength = bytes;
                probe.MappedFirstByte = firstByte;
                probe.MappedViewReadSucceeded = true;
                probe.StageAtCancellation = new MappedStageSnapshot(
                    Path.GetFileName(path),
                    path,
                    bytes,
                    Volatile.Read(ref probe.CancelCheckCount),
                    identity,
                    firstByte);
                probe.Cancellation.Cancel();
                Volatile.Write(ref probe.CancellationRequested, 1);
                return 1;
            }

            // Wait for the real production-owned stage; never synthesize a
            // cleanup failure or cancel an earlier non-stage check.
            return 0;
        }
        catch (Exception exception)
        {
            if (probe is not null)
                probe.CallbackFailure = exception.ToString();
            // The callback must not throw across the C ABI. A map/open failure
            // remains visible as callback evidence and does not fake cancel.
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int MapCompletedRemuxStage(nint userData)
    {
        if (userData == nint.Zero)
            return 0;

        MappedRemuxPublicationProbe? probe = null;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (!handle.IsAllocated || handle.Target is not MappedRemuxPublicationProbe target)
                return 0;

            probe = target;
            Interlocked.Increment(ref probe.CheckCount);
            if (probe.StageAtPublicationAttempt is not null)
                return 0;

            foreach (string path in Directory.GetFiles(
                probe.OutputDirectory,
                "lpb-remux-*.tmp",
                SearchOption.TopDirectoryOnly))
            {
                long bytes = new FileInfo(path).Length;
                if (bytes < probe.ExpectedStageBytes)
                    continue;
                if (bytes != probe.ExpectedStageBytes)
                {
                    probe.CallbackFailure = $"Expected {probe.ExpectedStageBytes} completed remux bytes, observed {bytes}.";
                    return 0;
                }

                // Map only after every expected output byte has been written;
                // returning zero leaves the context uncancelled so Native
                // reaches the occupied no-replace publication attempt.
                var stageStream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                probe.StageStream = stageStream;
                MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                    stageStream,
                    mapName: null,
                    capacity: 0,
                    access: MemoryMappedFileAccess.Read,
                    inheritability: HandleInheritability.None,
                    leaveOpen: true);
                probe.Mapping = mapping;
                MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
                    0,
                    bytes,
                    MemoryMappedFileAccess.Read);
                probe.MappedView = view;
                byte firstByte = view.ReadByte(0);
                WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
                probe.MappedLength = bytes;
                probe.MappedFirstByte = firstByte;
                probe.MappedViewReadSucceeded = true;
                probe.StageAtPublicationAttempt = new MappedStageSnapshot(
                    Path.GetFileName(path),
                    path,
                    bytes,
                    Volatile.Read(ref probe.CheckCount),
                    identity,
                    firstByte);
                return 0;
            }

            // Ignore pre-stage and partial-stage callbacks. The real stage
            // size gate prevents this probe from fabricating a cleanup fault.
            return 0;
        }
        catch (Exception exception)
        {
            if (probe is not null)
                probe.CallbackFailure = exception.ToString();
            // Never throw a managed callback failure through the Native ABI.
            return 0;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string TryComputeSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "<unavailable: source path was not initialized>";

        try
        {
            return ComputeSha256(path);
        }
        catch (Exception exception)
        {
            return "<unavailable: " + exception.GetType().Name + ": " + exception.Message + ">";
        }
    }

    private static object[] SnapshotFiles(string directory)
    {
        return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => (object)new
            {
                name = Path.GetFileName(path),
                bytes = new FileInfo(path).Length,
                sha256 = ComputeSha256(path)
            })
            .ToArray();
    }

    private static object FailureEvidence(Exception? exception)
    {
        if (exception is null)
        {
            return new
            {
                type = (string?)null,
                message = (string?)null,
                hresult = (int?)null,
                innerType = (string?)null,
                win32NativeError = (int?)null
            };
        }

        return new
        {
            type = exception.GetType().FullName,
            message = exception.Message,
            hresult = exception.HResult,
            innerType = exception.InnerException?.GetType().FullName,
            win32NativeError = (exception.InnerException as Win32Exception)?.NativeErrorCode
        };
    }

    private static void WriteEvidence(object evidence, string fileName = "native-remux-mid-cancellation.json")
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE");
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("LPB_R2_EVIDENCE is required for P7-R2 converter cancellation evidence.");

        Directory.CreateDirectory(configuredRoot);
        string path = Path.Combine(configuredRoot, fileName);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions { WriteIndented = true });
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(json);
        output.Flush(flushToDisk: true);
    }

    private sealed record StageSnapshot(string Name, long Bytes);

    private sealed record ImageStageSnapshot(string Name, string Path, long Bytes, int CancelCheckNumber, WindowsFileIdentity Identity);

    private sealed record MappedStageSnapshot(
        string Name,
        string Path,
        long Bytes,
        int CancelCheckNumber,
        WindowsFileIdentity Identity,
        byte FirstByte);

    private sealed record FileSnapshot(string Path, long Bytes, string Sha256);

    private sealed class RemuxCancellationProbe(CancellationTokenSource cancellation, string outputDirectory)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public string OutputDirectory { get; } = outputDirectory;
        public int CancelCheckCount;
        public int CancellationRequested;
        public StageSnapshot[] StagesAtCancellation { get; set; } = [];
        public string? CallbackFailure { get; set; }
    }

    private sealed class ImageCancellationProbe(
        CancellationTokenSource cancellation,
        string outputDirectory,
        string stagePrefix,
        int minimumStageCheckNumber,
        bool setStageReadOnlyBeforeCancel)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public string OutputDirectory { get; } = outputDirectory;
        public string StagePrefix { get; } = stagePrefix;
        public int MinimumStageCheckNumber { get; } = minimumStageCheckNumber;
        public bool SetStageReadOnlyBeforeCancel { get; } = setStageReadOnlyBeforeCancel;
        public int CancelCheckCount;
        public int CancellationRequested;
        public ImageStageSnapshot? StageAtCancellation { get; set; }
        public FileAttributes? StageAttributesBeforeReadOnly { get; set; }
        public string? CallbackFailure { get; set; }
    }

    private sealed class MappedStageCancellationProbe(CancellationTokenSource cancellation, string outputDirectory)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public string OutputDirectory { get; } = outputDirectory;
        public int CancelCheckCount;
        public int CancellationRequested;
        public FileStream? StageStream { get; set; }
        public MemoryMappedFile? Mapping { get; set; }
        public MemoryMappedViewAccessor? MappedView { get; set; }
        public long MappedLength { get; set; }
        public byte? MappedFirstByte { get; set; }
        public bool MappedViewReadSucceeded { get; set; }
        public MappedStageSnapshot? StageAtCancellation { get; set; }
        public string? CallbackFailure { get; set; }

        public string ReleaseMappedResources()
        {
            string? failure = null;
            try { MappedView?.Dispose(); }
            catch (Exception exception) { failure = "view: " + exception.GetType().Name + ": " + exception.Message; }
            try { Mapping?.Dispose(); }
            catch (Exception exception) { failure ??= "mapping: " + exception.GetType().Name + ": " + exception.Message; }
            try { StageStream?.Dispose(); }
            catch (Exception exception) { failure ??= "stream: " + exception.GetType().Name + ": " + exception.Message; }
            MappedView = null;
            Mapping = null;
            StageStream = null;
            return failure ?? "released";
        }
    }

    private sealed class MappedRemuxPublicationProbe(string outputDirectory, long expectedStageBytes)
    {
        public string OutputDirectory { get; } = outputDirectory;
        public long ExpectedStageBytes { get; } = expectedStageBytes;
        public int CheckCount;
        public FileStream? StageStream { get; set; }
        public MemoryMappedFile? Mapping { get; set; }
        public MemoryMappedViewAccessor? MappedView { get; set; }
        public long MappedLength { get; set; }
        public byte? MappedFirstByte { get; set; }
        public bool MappedViewReadSucceeded { get; set; }
        public MappedStageSnapshot? StageAtPublicationAttempt { get; set; }
        public string? CallbackFailure { get; set; }

        public string ReleaseMappedResources()
        {
            string? failure = null;
            try { MappedView?.Dispose(); }
            catch (Exception exception) { failure = "view: " + exception.GetType().Name + ": " + exception.Message; }
            try { Mapping?.Dispose(); }
            catch (Exception exception) { failure ??= "mapping: " + exception.GetType().Name + ": " + exception.Message; }
            try { StageStream?.Dispose(); }
            catch (Exception exception) { failure ??= "stream: " + exception.GetType().Name + ": " + exception.Message; }
            MappedView = null;
            Mapping = null;
            StageStream = null;
            return failure ?? "released";
        }
    }

    private static FileSnapshot[] SnapshotWorkspaceFiles(string rootDirectory) =>
        Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileSnapshot(Path.GetFullPath(path), new FileInfo(path).Length, ComputeSha256(path)))
            .ToArray();

    private static WindowsFileIdentity? TryCaptureIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try { return WindowsFileIdentity.Capture(path); }
        catch { return null; }
    }
}
