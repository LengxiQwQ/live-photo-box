using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2MergeTransactionTests
{
    [Fact]
    [Trait("Category", "R2")]
    public async Task WriteLivePhotoAsync_WritesCompleteOutputToHeldStageBeforePublication()
    {
        using var directory = new TemporaryDirectory();
        (string imagePath, string videoPath, byte[] videoBytes) = CreateInputs(directory.Path);
        directory.TrackFile("input-image", imagePath);
        directory.TrackFile("input-video", videoPath);
        string outputPath = Path.Combine(directory.Path, "merged.jpg");
        directory.TrackFile("output", outputPath);
        SplitPublicationTargetEvidence? beforePublish = null;
        using var trace = directory.CapturePublisherTrace((publication, _) =>
        {
            if (publication.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication)
            {
                Assert.False(File.Exists(outputPath));
                beforePublish = publication.Evidence;
            }
            return Task.CompletedTask;
        });

        await directory.ObserveOperationAsync("WriteLivePhotoAsync", () => LivePhotoMergeService.WriteLivePhotoAsync(
            imagePath,
            videoPath,
            outputPath,
            selectedModeIndex: 2,
            token: CancellationToken.None,
            outputFormatIndex: 0));

        Assert.NotNull(beforePublish);
        Assert.True(beforePublish.StageLength > videoBytes.Length);
        Assert.False(string.IsNullOrWhiteSpace(beforePublish.StageSha256));
        byte[] output = await File.ReadAllBytesAsync(outputPath);
        Assert.True(output.AsSpan(output.Length - videoBytes.Length).SequenceEqual(videoBytes));
        Assert.Equal(beforePublish.StageLength, output.LongLength);
        Assert.Equal(beforePublish.StageSha256, Convert.ToHexString(SHA256.HashData(output)));
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    public async Task WriteLivePhotoRebuiltAsync_CancellationBeforePublishRemovesStageAndLeavesNoFinal()
    {
        using var directory = new TemporaryDirectory();
        (string imagePath, string videoPath, _) = CreateInputs(directory.Path);
        directory.TrackFile("input-image", imagePath);
        directory.TrackFile("input-video", videoPath);
        string outputPath = Path.Combine(directory.Path, "cancelled.jpg");
        directory.TrackFile("output", outputPath);
        using var cancellation = new CancellationTokenSource();
        using var trace = directory.CapturePublisherTrace((publication, _) =>
        {
            if (publication.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication)
            {
                cancellation.Cancel();
                return Task.FromException(new OperationCanceledException(cancellation.Token));
            }
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.ObserveOperationAsync(
            "WriteLivePhotoRebuiltAsync", () => LivePhotoMergeService.WriteLivePhotoRebuiltAsync(
                imagePath,
                videoPath,
                outputPath,
                selectedModeIndex: 2,
                token: cancellation.Token,
                presentationTimestampUs: 0,
                outputFormatIndex: 0,
                overwriteExisting: false,
                testHook: null)));

        Assert.False(File.Exists(outputPath));
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    public async Task WriteLivePhotoRebuiltAsync_NoOverwriteCollisionLeavesExistingTargetUntouched()
    {
        using var directory = new TemporaryDirectory();
        (string imagePath, string videoPath, _) = CreateInputs(directory.Path);
        directory.TrackFile("input-image", imagePath);
        directory.TrackFile("input-video", videoPath);
        string outputPath = Path.Combine(directory.Path, "no-overwrite.jpg");
        byte[] original = [0x41, 0x42, 0x43, 0x44];
        await File.WriteAllBytesAsync(outputPath, original);
        FileSnapshot originalTarget = directory.TrackFile("preexisting-output", outputPath);
        using var trace = directory.CapturePublisherTrace();

        await Assert.ThrowsAsync<IOException>(() => directory.ObserveOperationAsync(
            "WriteLivePhotoRebuiltAsync(overwriteExisting:false)", () => LivePhotoMergeService.WriteLivePhotoRebuiltAsync(
                imagePath,
                videoPath,
                outputPath,
                selectedModeIndex: 2,
                token: CancellationToken.None,
                presentationTimestampUs: 0,
                outputFormatIndex: 0,
                overwriteExisting: false,
                testHook: null)));

        FileSnapshot after = CaptureFile(outputPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(originalTarget.Sha256, after.Sha256);
        Assert.Equal(originalTarget.Identity, after.Identity);
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    public async Task WriteLivePhotoAsync_LockedExistingOutputRemainsUntouched()
    {
        using var directory = new TemporaryDirectory();
        (string imagePath, string videoPath, _) = CreateInputs(directory.Path);
        directory.TrackFile("input-image", imagePath);
        directory.TrackFile("input-video", videoPath);
        string outputPath = Path.Combine(directory.Path, "locked.jpg");
        byte[] original = [0xD1, 0xD2, 0xD3, 0xD4];
        await File.WriteAllBytesAsync(outputPath, original);
        FileSnapshot originalTarget = directory.TrackFile("locked-output", outputPath);
        using var trace = directory.CapturePublisherTrace();

        Exception? failure;
        using (new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failure = await Record.ExceptionAsync(() => directory.ObserveOperationAsync(
                "WriteLivePhotoAsync(locked-output)", () => LivePhotoMergeService.WriteLivePhotoAsync(
                    imagePath,
                    videoPath,
                    outputPath,
                    selectedModeIndex: 2,
                    token: CancellationToken.None,
                    outputFormatIndex: 0)));
        }

        Assert.IsType<IOException>(failure);
        FileSnapshot after = CaptureFile(outputPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(originalTarget.Sha256, after.Sha256);
        Assert.Equal(originalTarget.Identity, after.Identity);
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    public async Task WriteLivePhotoRebuiltAsync_FailureAfterExistingTargetMutationRestoresExactOriginal()
    {
        using var directory = new TemporaryDirectory();
        (string imagePath, string videoPath, _) = CreateInputs(directory.Path);
        directory.TrackFile("input-image", imagePath);
        directory.TrackFile("input-video", videoPath);
        string outputPath = Path.Combine(directory.Path, "rollback.jpg");
        byte[] original = [0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
        await File.WriteAllBytesAsync(outputPath, original);
        FileSnapshot originalTarget = directory.TrackFile("rollback-output-before", outputPath);
        using var trace = directory.CapturePublisherTrace((publication, _) =>
            publication.Boundary == SplitPublicationBoundary.AfterTargetMutation
                ? Task.FromException(new IOException("Injected post-mutation interruption."))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => directory.ObserveOperationAsync(
            "WriteLivePhotoRebuiltAsync(post-mutation-failure)", () => LivePhotoMergeService.WriteLivePhotoRebuiltAsync(
                imagePath,
                videoPath,
                outputPath,
                selectedModeIndex: 2,
                token: CancellationToken.None,
                presentationTimestampUs: 0,
                outputFormatIndex: 0,
                overwriteExisting: true,
                testHook: null)));

        FileSnapshot after = CaptureFile(outputPath);
        Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(originalTarget.Sha256, after.Sha256);
        Assert.Equal(originalTarget.Identity, after.Identity);
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    public async Task WriteLivePhotoRebuiltAsync_LateForeignReplacementIsNotMutatedOrDeleted()
    {
        using var directory = new TemporaryDirectory();
        (string imagePath, string videoPath, _) = CreateInputs(directory.Path);
        directory.TrackFile("input-image", imagePath);
        directory.TrackFile("input-video", videoPath);
        string outputPath = Path.Combine(directory.Path, "replaced.jpg");
        string retainedOriginalPath = Path.Combine(directory.Path, "retained-original.jpg");
        byte[] original = [0x91, 0x92, 0x93];
        byte[] foreign = [0xE1, 0xE2, 0xE3, 0xE4];
        await File.WriteAllBytesAsync(outputPath, original);
        FileSnapshot originalTarget = directory.TrackFile("original-output-before-replacement", outputPath);
        directory.TrackFile("retained-original-path", retainedOriginalPath);
        FileSnapshot? foreignAtReplacement = null;
        bool replaced = false;
        using var trace = directory.CapturePublisherTrace((publication, _) =>
        {
            if (publication.Boundary == SplitPublicationBoundary.BeforeMutationRevalidation && !replaced)
            {
                File.Move(outputPath, retainedOriginalPath);
                File.WriteAllBytes(outputPath, foreign);
                foreignAtReplacement = CaptureFile(outputPath);
                replaced = true;
            }
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() => directory.ObserveOperationAsync(
            "WriteLivePhotoRebuiltAsync(late-foreign-replacement)", () => LivePhotoMergeService.WriteLivePhotoRebuiltAsync(
                imagePath,
                videoPath,
                outputPath,
                selectedModeIndex: 2,
                token: CancellationToken.None,
                presentationTimestampUs: 0,
                outputFormatIndex: 0,
                overwriteExisting: true,
                testHook: null)));

        Assert.True(replaced);
        FileSnapshot foreignAfter = CaptureFile(outputPath);
        FileSnapshot retainedAfter = CaptureFile(retainedOriginalPath);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(foreignAtReplacement?.Sha256, foreignAfter.Sha256);
        Assert.Equal(foreignAtReplacement?.Identity, foreignAfter.Identity);
        Assert.Equal(original, await File.ReadAllBytesAsync(retainedOriginalPath));
        Assert.Equal(originalTarget.Sha256, retainedAfter.Sha256);
        Assert.Equal(originalTarget.Identity, retainedAfter.Identity);
        directory.SetContext(new
        {
            originalTargetBeforeReplacement = originalTarget,
            foreignReplacementBaseline = foreignAtReplacement,
            foreignReplacementAfter = foreignAfter,
            retainedOriginalAfter = retainedAfter
        });
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    public async Task RunnerFailureBeforeWriterLeavesExplicitForeignOutputUntouched()
    {
        using var directory = new TemporaryDirectory();
        string outputPath = Path.Combine(directory.Path, "foreign-output.jpg");
        byte[] foreign = [0xF1, 0xF2, 0xF3, 0xF4];
        await File.WriteAllBytesAsync(outputPath, foreign);
        FileSnapshot originalTarget = directory.TrackFile("foreign-output-before", outputPath);
        using var trace = directory.CapturePublisherTrace();
        var options = new LivePhotoMergeRunOptions
        {
            OutputDirectory = directory.Path,
            SelectedModeIndex = 2,
            OutputFilePath = outputPath,
            MaxDegreeOfParallelism = 1,
            TaskStartInterval = TimeSpan.Zero,
            OverwriteExisting = false
        };

        (bool IsSuccess, string Details) result = default;
        bool resultCaptured = false;
        await directory.ObserveOperationAsync("ProcessSinglePairAsync(missing-sources)", async () =>
        {
            result = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
                Path.Combine(directory.Path, "missing.jpg"),
                Path.Combine(directory.Path, "missing.mp4"),
                "missing",
                taskIndex: 1,
                options: options,
                tempDir: directory.Path,
                token: CancellationToken.None);
            resultCaptured = true;
        });

        Assert.True(resultCaptured);
        Assert.False(result.IsSuccess);
        FileSnapshot after = CaptureFile(outputPath);
        Assert.Equal(foreign, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(originalTarget.Sha256, after.Sha256);
        Assert.Equal(originalTarget.Identity, after.Identity);
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-Runner-TempOwnership")]
    public async Task Runner_RunAsyncSuccessPreservesPreexistingTempTree()
    {
        using var directory = new TemporaryDirectory("runner-runasync-success-preserves-temp");
        string imagePath = TestSampleResolver.ResolveSample("苹果-双文件.JPG");
        string videoPath = TestSampleResolver.ResolveSample("苹果-双文件.MOV");
        FileSnapshot imageBefore = directory.TrackFile("real-apple-source-image-before", imagePath);
        FileSnapshot videoBefore = directory.TrackFile("real-apple-source-video-before", videoPath);
        string outputDirectory = Path.Combine(directory.Path, "runner-output");
        Directory.CreateDirectory(outputDirectory);
        PreexistingRunnerTempTree tempTree = CreatePreexistingRunnerTempTree(directory, outputDirectory);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.RunAsync",
            outcome = "real Apple pair reaches normal merge publication",
            sourceImage = imagePath,
            sourceVideo = videoPath,
            imageBefore,
            videoBefore,
            preexistingTempTree = tempTree
        });

        var options = CreateTopLevelRunnerOptions(outputDirectory);
        var task = CreateRunnerTask(imagePath, videoPath, "runner-temp-success");
        bool? completedSuccess = null;
        string? completedDetails = null;
        int completedCount = 0;
        string? publishedPath = null;
        FileSnapshot? outputBefore = null;
        using var pauseEvent = new ManualResetEventSlim(initialState: true);
        using var trace = directory.CapturePublisherTrace((publication, _) =>
        {
            if (publication.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication)
            {
                publishedPath = publication.FinalPath;
                outputBefore = directory.TrackFile("top-level-run-output-before-publication", publication.FinalPath);
            }
            return Task.CompletedTask;
        });

        await directory.ObserveOperationAsync("RunAsync(real-success)", () => LivePhotoMergeRunnerService.RunAsync(
            new IMergeTaskInfo[] { task },
            options,
            pauseEvent,
            CancellationToken.None,
            onTaskStarted: null,
            onTaskCompleted: (_, success, details, count) =>
            {
                completedSuccess = success;
                completedDetails = details;
                completedCount = count;
            }));

        FileSnapshot imageAfter = CaptureFile(imagePath);
        FileSnapshot videoAfter = CaptureFile(videoPath);
        FileSnapshot? outputAfter = publishedPath is null ? null : CaptureFile(publishedPath);
        object tempTreeAfter = AssertPreexistingRunnerTempTreePreserved(tempTree);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.RunAsync",
            outcome = "success",
            completedSuccess,
            completedDetails,
            completedCount,
            publishedPath,
            outputBefore,
            outputAfter,
            preexistingTempTree = tempTree,
            tempTreeAfter,
            imageBefore,
            imageAfter,
            videoBefore,
            videoAfter
        });

        Assert.True(completedSuccess == true, completedDetails);
        Assert.Equal(1, completedCount);
        Assert.NotNull(publishedPath);
        Assert.NotNull(outputBefore);
        Assert.False(outputBefore!.Exists);
        Assert.NotNull(outputAfter);
        Assert.True(outputAfter!.Exists);
        Assert.True(outputAfter.Length > 0);
        Assert.Equal(imageBefore.Sha256, imageAfter.Sha256);
        Assert.Equal(imageBefore.Identity, imageAfter.Identity);
        Assert.Equal(videoBefore.Sha256, videoAfter.Sha256);
        Assert.Equal(videoBefore.Identity, videoAfter.Identity);
        AssertNoTransactionArtifacts(outputDirectory);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-Runner-TempOwnership")]
    public async Task Runner_RunAsyncPairFailurePreservesPreexistingTempTreeAndSource()
    {
        using var directory = new TemporaryDirectory("runner-runasync-failure-preserves-temp");
        string imagePath = TestSampleResolver.ResolveSample("苹果-双文件.JPG");
        string missingVideoPath = Path.Combine(directory.Path, "missing-paired-video.mov");
        FileSnapshot imageBefore = directory.TrackFile("real-apple-source-image-before", imagePath);
        FileSnapshot missingVideoBefore = directory.TrackFile("missing-paired-video-before", missingVideoPath);
        string outputDirectory = Path.Combine(directory.Path, "runner-output");
        Directory.CreateDirectory(outputDirectory);
        PreexistingRunnerTempTree tempTree = CreatePreexistingRunnerTempTree(directory, outputDirectory);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.RunAsync",
            outcome = "expected pair preprocessing failure from absent secondary video",
            sourceImage = imagePath,
            missingVideoPath,
            imageBefore,
            missingVideoBefore,
            preexistingTempTree = tempTree
        });

        var options = CreateTopLevelRunnerOptions(outputDirectory);
        var task = CreateRunnerTask(imagePath, missingVideoPath, "runner-temp-failure");
        bool? completedSuccess = null;
        string? completedDetails = null;
        int completedCount = 0;
        using var pauseEvent = new ManualResetEventSlim(initialState: true);

        await directory.ObserveOperationAsync("RunAsync(pair-failure)", () => LivePhotoMergeRunnerService.RunAsync(
            new IMergeTaskInfo[] { task },
            options,
            pauseEvent,
            CancellationToken.None,
            onTaskStarted: null,
            onTaskCompleted: (_, success, details, count) =>
            {
                completedSuccess = success;
                completedDetails = details;
                completedCount = count;
            }));

        FileSnapshot imageAfter = CaptureFile(imagePath);
        FileSnapshot missingVideoAfter = CaptureFile(missingVideoPath);
        object tempTreeAfter = AssertPreexistingRunnerTempTreePreserved(tempTree);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.RunAsync",
            outcome = "pair failure returned through completion callback",
            completedSuccess,
            completedDetails,
            completedCount,
            preexistingTempTree = tempTree,
            tempTreeAfter,
            imageBefore,
            imageAfter,
            missingVideoBefore,
            missingVideoAfter
        });

        Assert.Equal(false, completedSuccess);
        Assert.False(string.IsNullOrWhiteSpace(completedDetails));
        Assert.Equal(1, completedCount);
        Assert.Equal(imageBefore.Sha256, imageAfter.Sha256);
        Assert.Equal(imageBefore.Identity, imageAfter.Identity);
        Assert.Equal(missingVideoBefore.Exists, missingVideoAfter.Exists);
        Assert.Equal(missingVideoBefore.Identity, missingVideoAfter.Identity);
        Assert.Empty(Directory.GetFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly));
        AssertNoTransactionArtifacts(outputDirectory);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-Runner-TempOwnership")]
    public async Task Runner_RunAsyncCancellationPreservesPreexistingTempTreeAndSources()
    {
        using var directory = new TemporaryDirectory("runner-runasync-cancel-preserves-temp");
        string imagePath = TestSampleResolver.ResolveSample("苹果-双文件.JPG");
        string videoPath = TestSampleResolver.ResolveSample("苹果-双文件.MOV");
        FileSnapshot imageBefore = directory.TrackFile("real-apple-source-image-before", imagePath);
        FileSnapshot videoBefore = directory.TrackFile("real-apple-source-video-before", videoPath);
        string outputDirectory = Path.Combine(directory.Path, "runner-output");
        Directory.CreateDirectory(outputDirectory);
        PreexistingRunnerTempTree tempTree = CreatePreexistingRunnerTempTree(directory, outputDirectory);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.RunAsync",
            outcome = "pre-cancelled before task processing",
            sourceImage = imagePath,
            sourceVideo = videoPath,
            imageBefore,
            videoBefore,
            preexistingTempTree = tempTree
        });

        var options = CreateTopLevelRunnerOptions(outputDirectory);
        var task = CreateRunnerTask(imagePath, videoPath, "runner-temp-cancel");
        using var pauseEvent = new ManualResetEventSlim(initialState: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => directory.ObserveOperationAsync(
            "RunAsync(pre-cancelled)",
            () => LivePhotoMergeRunnerService.RunAsync(
                new IMergeTaskInfo[] { task },
                options,
                pauseEvent,
                cancellation.Token,
                onTaskStarted: null,
                onTaskCompleted: null)));

        FileSnapshot imageAfter = CaptureFile(imagePath);
        FileSnapshot videoAfter = CaptureFile(videoPath);
        object tempTreeAfter = AssertPreexistingRunnerTempTreePreserved(tempTree);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.RunAsync",
            outcome = "cancelled before task processing",
            preexistingTempTree = tempTree,
            tempTreeAfter,
            imageBefore,
            imageAfter,
            videoBefore,
            videoAfter
        });

        Assert.Equal(imageBefore.Sha256, imageAfter.Sha256);
        Assert.Equal(imageBefore.Identity, imageAfter.Identity);
        Assert.Equal(videoBefore.Sha256, videoAfter.Sha256);
        Assert.Equal(videoBefore.Identity, videoAfter.Identity);
        Assert.Empty(Directory.GetFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly));
        AssertNoTransactionArtifacts(outputDirectory);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-Runner-NoOverwrite")]
    public async Task Runner_NoOverwritePublishesToCandidateWithoutCreatingReservationPlaceholder()
    {
        using var directory = new TemporaryDirectory("runner-no-overwrite-success");
        string imagePath = TestSampleResolver.ResolveSample("苹果-双文件.JPG");
        string videoPath = TestSampleResolver.ResolveSample("苹果-双文件.MOV");
        FileSnapshot imageBefore = directory.TrackFile("real-apple-source-image-before", imagePath);
        FileSnapshot videoBefore = directory.TrackFile("real-apple-source-video-before", videoPath);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.ProcessSinglePairAsync",
            sourceImageName = "苹果-双文件.JPG",
            sourceVideoName = "苹果-双文件.MOV",
            fixturePurpose = "Pass the original valid Apple dual-file pair through production Native inspection and cleaning."
        });
        string outputDirectory = Path.Combine(directory.Path, "runner-output");
        Directory.CreateDirectory(outputDirectory);
        string? candidatePath = null;
        FileSnapshot? candidateBefore = null;
        using var trace = directory.CapturePublisherTrace((publication, _) =>
        {
            if (publication.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication)
            {
                Assert.Null(candidatePath);
                string finalPath = publication.FinalPath;
                candidatePath = finalPath;
                FileSnapshot before = directory.TrackFile("candidate-output-before-publish", finalPath);
                candidateBefore = before;
                Assert.False(before.Exists);
            }
            return Task.CompletedTask;
        });
        var options = new LivePhotoMergeRunOptions
        {
            OutputDirectory = outputDirectory,
            SelectedModeIndex = 2,
            OutputFormatIndex = ProtocolFormatMatrix.FormatJpgMov,
            NamingRuleIndex = 0,
            MaxDegreeOfParallelism = 1,
            TaskStartInterval = TimeSpan.Zero,
            OverwriteExisting = false
        };

        (bool IsSuccess, string Details) result = default;
        await directory.ObserveOperationAsync("ProcessSinglePairAsync(no-overwrite-success)", async () =>
        {
            result = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
                imagePath,
                videoPath,
                "runner-no-overwrite",
                taskIndex: 1,
                options: options,
                tempDir: Path.Combine(directory.Path, "merge-temp"),
                token: CancellationToken.None);
        });

        FileSnapshot imageAfter = CaptureFile(imagePath);
        FileSnapshot videoAfter = CaptureFile(videoPath);
        Assert.True(result.IsSuccess, result.Details);
        Assert.NotNull(candidatePath);
        Assert.NotNull(candidateBefore);
        FileSnapshot outputAfter = CaptureFile(candidatePath!);
        Assert.True(outputAfter.Exists);
        Assert.True(outputAfter.Length > 0);
        Assert.NotNull(outputAfter.Identity);
        Assert.Equal(imageBefore.Sha256, imageAfter.Sha256);
        Assert.Equal(imageBefore.Identity, imageAfter.Identity);
        Assert.Equal(videoBefore.Sha256, videoAfter.Sha256);
        Assert.Equal(videoBefore.Identity, videoAfter.Identity);
        AssertNoTransactionArtifacts(outputDirectory);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.ProcessSinglePairAsync",
            sourceImageName = "苹果-双文件.JPG",
            sourceVideoName = "苹果-双文件.MOV",
            fixturePurpose = "Pass the original valid Apple dual-file pair through production Native inspection and cleaning.",
            overwriteExisting = false,
            result = new { result.IsSuccess, result.Details },
            candidatePath,
            candidateBefore,
            outputAfter,
            imageBefore,
            imageAfter,
            videoBefore,
            videoAfter
        });
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-Runner-NoOverwrite")]
    public async Task Runner_NoOverwriteForeignArrivalAtPublicationIsPreserved()
    {
        using var directory = new TemporaryDirectory("runner-no-overwrite-foreign-race");
        string imagePath = TestSampleResolver.ResolveSample("苹果-双文件.JPG");
        string videoPath = TestSampleResolver.ResolveSample("苹果-双文件.MOV");
        FileSnapshot imageBefore = directory.TrackFile("real-apple-source-image-before", imagePath);
        FileSnapshot videoBefore = directory.TrackFile("real-apple-source-video-before", videoPath);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.ProcessSinglePairAsync",
            sourceImageName = "苹果-双文件.JPG",
            sourceVideoName = "苹果-双文件.MOV",
            fixturePurpose = "Pass the original valid Apple dual-file pair through production Native inspection and cleaning."
        });
        string outputDirectory = Path.Combine(directory.Path, "runner-output");
        Directory.CreateDirectory(outputDirectory);
        byte[] foreignBytes = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x52, 0x41, 0x43, 0x45];
        string? candidatePath = null;
        FileSnapshot? candidateBefore = null;
        FileSnapshot? foreignBaseline = null;
        bool foreignCreated = false;
        using var trace = directory.CapturePublisherTrace((publication, _) =>
        {
            if (publication.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication && !foreignCreated)
            {
                string finalPath = publication.FinalPath;
                candidatePath = finalPath;
                FileSnapshot before = directory.TrackFile("candidate-output-before-foreign-arrival", finalPath);
                candidateBefore = before;
                Assert.False(before.Exists);
                using (var output = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(foreignBytes);
                    output.Flush(flushToDisk: true);
                }
                foreignBaseline = CaptureFile(finalPath);
                foreignCreated = true;
            }
            return Task.CompletedTask;
        });
        var options = new LivePhotoMergeRunOptions
        {
            OutputDirectory = outputDirectory,
            SelectedModeIndex = 2,
            OutputFormatIndex = ProtocolFormatMatrix.FormatJpgMov,
            NamingRuleIndex = 0,
            MaxDegreeOfParallelism = 1,
            TaskStartInterval = TimeSpan.Zero,
            OverwriteExisting = false
        };

        (bool IsSuccess, string Details) result = default;
        await directory.ObserveOperationAsync("ProcessSinglePairAsync(no-overwrite-foreign-race)", async () =>
        {
            result = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
                imagePath,
                videoPath,
                "runner-no-overwrite-race",
                taskIndex: 1,
                options: options,
                tempDir: Path.Combine(directory.Path, "merge-temp"),
                token: CancellationToken.None);
        });

        FileSnapshot imageAfter = CaptureFile(imagePath);
        FileSnapshot videoAfter = CaptureFile(videoPath);
        Assert.True(foreignCreated);
        Assert.False(result.IsSuccess, result.Details);
        Assert.NotNull(candidatePath);
        Assert.NotNull(candidateBefore);
        Assert.NotNull(foreignBaseline);
        FileSnapshot foreignAfter = CaptureFile(candidatePath!);
        Assert.Equal(foreignBytes, await File.ReadAllBytesAsync(candidatePath!));
        Assert.Equal(foreignBaseline.Sha256, foreignAfter.Sha256);
        Assert.Equal(foreignBaseline.Identity, foreignAfter.Identity);
        Assert.Equal(imageBefore.Sha256, imageAfter.Sha256);
        Assert.Equal(imageBefore.Identity, imageAfter.Identity);
        Assert.Equal(videoBefore.Sha256, videoAfter.Sha256);
        Assert.Equal(videoBefore.Identity, videoAfter.Identity);
        AssertNoTransactionArtifacts(outputDirectory);
        directory.SetContext(new
        {
            operation = "LivePhotoMergeRunnerService.ProcessSinglePairAsync",
            sourceImageName = "苹果-双文件.JPG",
            sourceVideoName = "苹果-双文件.MOV",
            fixturePurpose = "Pass the original valid Apple dual-file pair through production Native inspection and cleaning.",
            overwriteExisting = false,
            result = new { result.IsSuccess, result.Details },
            candidatePath,
            candidateBefore,
            foreignBaseline,
            foreignAfter,
            imageBefore,
            imageAfter,
            videoBefore,
            videoAfter
        });
        directory.MarkAssertionsPassed();
    }

    [Theory]
    [InlineData("荣耀.jpg", 2, "GCamera:MotionPhoto")]
    [InlineData("三星.jpg", 5, "SEFH")]
    [InlineData("华为-Mate80.jpg", 6, "LIVE_")]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-RealSample")]
    public async Task WriteLivePhotoAsync_RealSampleWriterRoutesPublishProtocolOutput(
        string sampleName,
        int protocolIndex,
        string expectedMarker)
    {
        using var directory = new TemporaryDirectory(
            $"real-success-{Path.GetFileNameWithoutExtension(sampleName)}-protocol-{protocolIndex}");
        string samplePath = TestSampleResolver.ResolveSample(sampleName);
        FileSnapshot sampleBefore = directory.TrackFile("real-sample-source-before", samplePath);
        directory.SetContext(new
        {
            operation = "LivePhotoSplitService.SplitAsync then LivePhotoMergeService.WriteLivePhotoAsync",
            sampleName,
            protocolIndex,
            expectedMarker
        });
        var split = await LivePhotoSplitService.SplitAsync(
            samplePath,
            directory.Path,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: "neutral-" + protocolIndex);
        directory.TrackFile("neutral-image-input", split.ImageOutputPath);
        directory.TrackFile("neutral-video-input", split.VideoOutputPath);
        string outputPath = Path.Combine(directory.Path, "merged-" + protocolIndex + ".jpg");
        directory.TrackFile("merged-output-before", outputPath);
        using var trace = directory.CapturePublisherTrace();

        await directory.ObserveOperationAsync($"WriteLivePhotoAsync(real-{protocolIndex})", () => LivePhotoMergeService.WriteLivePhotoAsync(
            split.ImageOutputPath,
            split.VideoOutputPath,
            outputPath,
            selectedModeIndex: protocolIndex,
            token: CancellationToken.None,
            outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4));

        byte[] output = await File.ReadAllBytesAsync(outputPath);
        Assert.True(output.Length > new FileInfo(split.ImageOutputPath).Length);
        Assert.True(output.AsSpan(0, 2).SequenceEqual(new byte[] { 0xFF, 0xD8 }));
        Assert.True(ContainsBytes(output, Encoding.ASCII.GetBytes(expectedMarker)));
        FileSnapshot sampleAfter = CaptureFile(samplePath);
        Assert.Equal(sampleBefore.Sha256, sampleAfter.Sha256);
        Assert.Equal(sampleBefore.Identity, sampleAfter.Identity);

        if (protocolIndex == 6)
        {
            byte[] tail = output.AsSpan(output.Length - 60).ToArray();
            long liveValue = new FileInfo(split.VideoOutputPath).Length + 20;
            Assert.True(ContainsBytes(tail, Encoding.ASCII.GetBytes($"LIVE_{liveValue}")));
        }
        else if (protocolIndex == 5)
        {
            Assert.Equal("SEFT", Encoding.ASCII.GetString(output, output.Length - 4, 4));
        }

        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("P7", "R2-Merge-RealSample")]
    public async Task WriteLivePhotoAsync_RealSampleFailureRestoresExactOldTarget()
    {
        using var directory = new TemporaryDirectory("real-rollback-honor");
        string samplePath = TestSampleResolver.ResolveSample("荣耀.jpg");
        FileSnapshot sampleBefore = directory.TrackFile("real-sample-source-before", samplePath);
        directory.SetContext(new
        {
            operation = "LivePhotoSplitService.SplitAsync then LivePhotoMergeService.WriteLivePhotoAsync",
            sampleName = "荣耀.jpg",
            protocolIndex = 2,
            rollbackBoundary = SplitPublicationBoundary.AfterTargetMutation.ToString()
        });
        var split = await LivePhotoSplitService.SplitAsync(
            samplePath,
            directory.Path,
            ProtocolFormatMatrix.SplitProtocolNone,
            ProtocolFormatMatrix.SplitFormatKeep,
            CancellationToken.None,
            outputBaseName: "neutral-rollback");
        directory.TrackFile("neutral-image-input", split.ImageOutputPath);
        directory.TrackFile("neutral-video-input", split.VideoOutputPath);
        string outputPath = Path.Combine(directory.Path, "existing.jpg");
        byte[] original = [0x31, 0x32, 0x33, 0xA1, 0xB2];
        await File.WriteAllBytesAsync(outputPath, original);
        FileSnapshot originalTarget = directory.TrackFile("preexisting-real-route-output", outputPath);
        using var trace = directory.CapturePublisherTrace((publication, _) =>
            publication.Boundary == SplitPublicationBoundary.AfterTargetMutation
                ? Task.FromException(new IOException("Injected post-mutation interruption."))
                : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => directory.ObserveOperationAsync(
            "WriteLivePhotoAsync(real-honor-rollback)", () => LivePhotoMergeService.WriteLivePhotoAsync(
                split.ImageOutputPath,
                split.VideoOutputPath,
                outputPath,
                selectedModeIndex: 2,
                token: CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4)));

        FileSnapshot targetAfter = CaptureFile(outputPath);
        FileSnapshot sampleAfter = CaptureFile(samplePath);
        Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(originalTarget.Sha256, targetAfter.Sha256);
        Assert.Equal(originalTarget.Identity, targetAfter.Identity);
        Assert.Equal(sampleBefore.Sha256, sampleAfter.Sha256);
        Assert.Equal(sampleBefore.Identity, sampleAfter.Identity);
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    public static IEnumerable<object[]> SourceAliasCases()
    {
        foreach (string route in new[] { "live", "rebuilt", "native", "huawei" })
        foreach (string sourceRole in new[] { "image", "video" })
        foreach (string aliasKind in new[] { "canonical-path", "hardlink" })
            yield return [route, sourceRole, aliasKind];
    }

    [Theory]
    [MemberData(nameof(SourceAliasCases))]
    [Trait("Category", "R2")]
    public async Task MergeWriter_SourceDestinationAliasFailsBeforeStagingAndPreservesIdentity(
        string route,
        string sourceRole,
        string aliasKind)
    {
        using var directory = new TemporaryDirectory($"alias-{route}-{sourceRole}-{aliasKind}");
        (string imagePath, string videoPath, _) = CreateInputs(directory.Path);
        string aliasedSource = sourceRole == "image" ? imagePath : videoPath;
        string targetPath;
        if (aliasKind == "canonical-path")
        {
            // The spelling differs, but Path.GetFullPath resolves it to the source path.
            targetPath = Path.Combine(directory.Path, ".", Path.GetFileName(aliasedSource));
        }
        else
        {
            targetPath = Path.Combine(directory.Path, "hardlink-target.jpg");
            if (!MergeTestNative.CreateHardLinkW(targetPath, aliasedSource, IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Unable to create the required hard-link alias from '{aliasedSource}' to '{targetPath}'.");
            }
        }
        // The hard-link fixture changes the source object's link count. Capture
        // the mutation baseline after fixture creation so the operation under
        // test is compared against the exact state it receives.
        FileSnapshot imageBefore = directory.TrackFile("image-input-before-writer", imagePath);
        FileSnapshot videoBefore = directory.TrackFile("video-input-before-writer", videoPath);
        FileSnapshot targetBefore = directory.TrackFile("aliased-output-before", targetPath);
        WindowsFileIdentity expectedSourceIdentity = sourceRole == "image"
            ? imageBefore.Identity!
            : videoBefore.Identity!;
        Assert.Equal(expectedSourceIdentity, targetBefore.Identity);
        directory.SetContext(new
        {
            route,
            sourceRole,
            aliasKind,
            imagePath,
            videoPath,
            targetPath,
            expectedAliasIdentity = targetBefore.Identity
        });
        using var trace = directory.CapturePublisherTrace();

        await Assert.ThrowsAsync<IOException>(() => directory.ObserveOperationAsync(
            $"MergeAlias({route},{sourceRole},{aliasKind})",
            () => InvokeWriterRouteAsync(route, imagePath, videoPath, targetPath)));

        FileSnapshot imageAfter = CaptureFile(imagePath);
        FileSnapshot videoAfter = CaptureFile(videoPath);
        FileSnapshot targetAfter = CaptureFile(targetPath);
        Assert.Equal(imageBefore.Sha256, imageAfter.Sha256);
        Assert.Equal(imageBefore.Identity, imageAfter.Identity);
        Assert.Equal(videoBefore.Sha256, videoAfter.Sha256);
        Assert.Equal(videoBefore.Identity, videoAfter.Identity);
        Assert.Equal(targetBefore.Sha256, targetAfter.Sha256);
        Assert.Equal(targetBefore.Identity, targetAfter.Identity);
        Assert.Empty(directory.PublisherTrace);
        AssertNoTransactionArtifacts(directory.Path);
        directory.MarkAssertionsPassed();
    }

    private static Task InvokeWriterRouteAsync(
        string route,
        string imagePath,
        string videoPath,
        string targetPath)
    {
        return route switch
        {
            "live" => LivePhotoMergeService.WriteLivePhotoAsync(
                imagePath,
                videoPath,
                targetPath,
                selectedModeIndex: 2,
                token: CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4),
            "rebuilt" => LivePhotoMergeService.WriteLivePhotoRebuiltAsync(
                imagePath,
                videoPath,
                targetPath,
                selectedModeIndex: 2,
                token: CancellationToken.None,
                presentationTimestampUs: 0,
                outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4,
                overwriteExisting: true,
                testHook: null),
            "native" => LivePhotoMergeService.WriteNativeAsync(
                imagePath,
                videoPath,
                targetPath,
                Array.Empty<byte>(),
                CancellationToken.None),
            "huawei" => LivePhotoMergeService.WriteHuaweiNativeAsync(
                imagePath,
                videoPath,
                targetPath,
                isHeicOutput: false,
                presentationTimestampUs: 0,
                token: CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown merge writer route.")
        };
    }

    private static LivePhotoMergeRunOptions CreateTopLevelRunnerOptions(string outputDirectory) => new()
    {
        OutputDirectory = outputDirectory,
        SelectedModeIndex = 2,
        OutputFormatIndex = ProtocolFormatMatrix.FormatJpgMov,
        NamingRuleIndex = 0,
        MaxDegreeOfParallelism = 1,
        TaskStartInterval = TimeSpan.Zero,
        OverwriteExisting = false
    };

    private static TestMergeTask CreateRunnerTask(string imagePath, string videoPath, string baseName) => new()
    {
        Index = 1,
        ImagePath = imagePath,
        VideoPath = videoPath,
        BaseName = baseName
    };

    private static PreexistingRunnerTempTree CreatePreexistingRunnerTempTree(
        TemporaryDirectory evidence,
        string outputDirectory)
    {
        string tempDirectory = Path.Combine(outputDirectory, "Temp");
        string foreignDirectory = Path.Combine(tempDirectory, "foreign-child");
        string sentinelPath = Path.Combine(tempDirectory, "sentinel.bin");
        string foreignPath = Path.Combine(foreignDirectory, "foreign.bin");
        byte[] sentinelBytes = [0x54, 0x45, 0x4D, 0x50, 0x2D, 0x53, 0x45, 0x4E, 0x54, 0x49, 0x4E, 0x45, 0x4C];
        byte[] foreignBytes = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x43, 0x48, 0x49, 0x4C, 0x44];
        Directory.CreateDirectory(foreignDirectory);
        File.WriteAllBytes(sentinelPath, sentinelBytes);
        File.WriteAllBytes(foreignPath, foreignBytes);
        FileSnapshot sentinelBefore = evidence.TrackFile("preexisting-runner-temp-sentinel-before", sentinelPath);
        FileSnapshot foreignBefore = evidence.TrackFile("preexisting-runner-temp-foreign-child-before", foreignPath);
        Assert.NotNull(sentinelBefore.Identity);
        Assert.NotNull(foreignBefore.Identity);
        return new PreexistingRunnerTempTree(
            tempDirectory,
            foreignDirectory,
            sentinelPath,
            sentinelBytes,
            sentinelBefore,
            foreignPath,
            foreignBytes,
            foreignBefore);
    }

    private static object AssertPreexistingRunnerTempTreePreserved(PreexistingRunnerTempTree before)
    {
        Assert.True(Directory.Exists(before.TempDirectory));
        Assert.True(Directory.Exists(before.ForeignDirectory));
        Assert.Equal(before.SentinelBytes, File.ReadAllBytes(before.SentinelPath));
        Assert.Equal(before.ForeignBytes, File.ReadAllBytes(before.ForeignPath));
        FileSnapshot sentinelAfter = CaptureFile(before.SentinelPath);
        FileSnapshot foreignAfter = CaptureFile(before.ForeignPath);
        Assert.Equal(before.SentinelBefore.Sha256, sentinelAfter.Sha256);
        Assert.Equal(before.SentinelBefore.Identity, sentinelAfter.Identity);
        Assert.Equal(before.ForeignBefore.Sha256, foreignAfter.Sha256);
        Assert.Equal(before.ForeignBefore.Identity, foreignAfter.Identity);
        return new { sentinelAfter, foreignAfter };
    }

    private sealed record PreexistingRunnerTempTree(
        string TempDirectory,
        string ForeignDirectory,
        string SentinelPath,
        byte[] SentinelBytes,
        FileSnapshot SentinelBefore,
        string ForeignPath,
        byte[] ForeignBytes,
        FileSnapshot ForeignBefore);

    private sealed class TestMergeTask : IMergeTaskInfo
    {
        public required int Index { get; init; }
        public required string ImagePath { get; init; }
        public required string VideoPath { get; init; }
        public required string BaseName { get; init; }
        public ProcessStatus Status { get; set; } = ProcessStatus.Pending;
        public string Details { get; set; } = string.Empty;
    }

    private static (string ImagePath, string VideoPath, byte[] VideoBytes) CreateInputs(string root)
    {
        string imagePath = Path.Combine(root, "input.jpg");
        string videoPath = Path.Combine(root, "input.mp4");
        byte[] videoBytes = SyntheticProtocolFixtures.CreateMinimalMp4();
        File.WriteAllBytes(imagePath, SyntheticProtocolFixtures.CreateMinimalJpeg());
        File.WriteAllBytes(videoPath, videoBytes);
        return (imagePath, videoPath, videoBytes);
    }

    private static void AssertNoTransactionArtifacts(string directory)
    {
        string[] leftovers = Directory.GetFiles(directory, ".lpb-split-*.tmp", SearchOption.TopDirectoryOnly);
        Assert.Empty(leftovers);
    }

    private static FileSnapshot CaptureFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return new FileSnapshot(fullPath, false, null, null, null, null);

        long? length = null;
        string? sha256 = null;
        string? contentFailure = null;
        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            length = stream.Length;
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception)
        {
            contentFailure = exception.GetType().FullName + ": " + exception.Message;
        }

        WindowsFileIdentity? identity = null;
        string? identityFailure = null;
        try
        {
            identity = WindowsFileIdentity.Capture(fullPath);
        }
        catch (Exception exception)
        {
            identityFailure = exception.GetType().FullName + ": " + exception.Message;
        }

        return new FileSnapshot(fullPath, true, length, sha256, identity, contentFailure ?? identityFailure);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
    }

    private sealed record FileSnapshot(
        string Path,
        bool Exists,
        long? Length,
        string? Sha256,
        WindowsFileIdentity? Identity,
        string? CaptureFailure);

    private sealed record TrackedFile(string Role, FileSnapshot Before);

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly List<TrackedFile> _trackedFiles = [];
        private readonly List<object> _operationResults = [];
        private readonly List<SplitPublicationTraceEvent> _publisherTrace = [];
        private bool _assertionsPassed;
        private object? _context;
        private bool _disposed;

        public TemporaryDirectory([CallerMemberName] string? caseName = null)
        {
            CaseName = Sanitize(caseName ?? "merge-case");
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lpb-p7r2-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string CaseName { get; }

        public string Path { get; }

        public IReadOnlyList<SplitPublicationTraceEvent> PublisherTrace => _publisherTrace;

        public FileSnapshot TrackFile(string role, string path)
        {
            FileSnapshot before = CaptureFile(path);
            _trackedFiles.Add(new TrackedFile(role, before));
            return before;
        }

        public void SetContext(object context) => _context = context;

        public void MarkAssertionsPassed() => _assertionsPassed = true;

        public IDisposable CapturePublisherTrace(
            Func<SplitPublicationTraceEvent, CancellationToken, Task>? callback = null)
        {
            return new PublisherTraceScope(this, callback);
        }

        public async Task ObserveOperationAsync(string operation, Func<Task> invoke)
        {
            try
            {
                await invoke().ConfigureAwait(false);
                _operationResults.Add(new { operation, outcome = "returned-success" });
            }
            catch (Exception exception)
            {
                _operationResults.Add(new
                {
                    operation,
                    outcome = "threw",
                    failure = FailureEvidence(exception)
                });
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                FileSnapshot[] workspaceAfter = Directory.Exists(Path)
                    ? Directory.GetFiles(Path, "*", SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .Select(CaptureFile)
                        .ToArray()
                    : [];
                string[] residueAfter = Directory.Exists(Path)
                    ? Directory.GetFiles(Path, ".lpb-split-*.tmp", SearchOption.AllDirectories)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : [];
                var fileEvidence = _trackedFiles.Select(tracked => new
                {
                    tracked.Role,
                    tracked.Before,
                    After = CaptureFile(tracked.Before.Path)
                }).ToArray();

                WriteEvidence(CaseName, new
                {
                    schema = "P7-R2-Merge-Transaction-Evidence-v1",
                    caseName = CaseName,
                    workspace = Path,
                    assertionsPassed = _assertionsPassed,
                    context = _context,
                    operations = _operationResults,
                    trackedFiles = fileEvidence,
                    publisherTrace = _publisherTrace,
                    workspaceFilesAfter = workspaceAfter,
                    transactionResidueAfter = residueAfter
                });
            }
            finally
            {
                try
                {
                    if (Directory.Exists(Path))
                        Directory.Delete(Path, recursive: true);
                }
                catch
                {
                    // Evidence already contains the post-operation filesystem snapshot.
                }
            }
        }

        private void AddTrace(SplitPublicationTraceEvent publication) => _publisherTrace.Add(publication);

        private static void WriteEvidence(string caseName, object evidence)
        {
            string? configuredRoot = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE");
            if (string.IsNullOrWhiteSpace(configuredRoot))
                throw new InvalidOperationException("LPB_R2_EVIDENCE is required for P7-R2 Merge transaction evidence.");

            Directory.CreateDirectory(configuredRoot);
            string evidencePath = System.IO.Path.Combine(
                configuredRoot,
                "p7r2-merge-" + Sanitize(caseName) + ".json");
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions { WriteIndented = true });
            using var output = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(json);
            output.Flush(flushToDisk: true);
        }

        private static string Sanitize(string value)
        {
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
                builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_');
            string result = new string(builder.ToString().Where(character => !invalid.Contains(character)).ToArray());
            return string.IsNullOrWhiteSpace(result) ? "merge-case" : result;
        }

        private sealed class PublisherTraceScope : IDisposable
        {
            private readonly TemporaryDirectory _owner;
            private readonly Func<SplitPublicationTraceEvent, CancellationToken, Task>? _callback;
            private readonly Func<SplitPublicationTraceEvent, CancellationToken, Task>? _previous;
            private bool _disposed;

            public PublisherTraceScope(
                TemporaryDirectory owner,
                Func<SplitPublicationTraceEvent, CancellationToken, Task>? callback)
            {
                _owner = owner;
                _callback = callback;
                _previous = WindowsSplitOutputPublisher.TestHook.Value;
                WindowsSplitOutputPublisher.TestHook.Value = OnTraceAsync;
            }

            private async Task OnTraceAsync(SplitPublicationTraceEvent publication, CancellationToken token)
            {
                _owner.AddTrace(publication);
                if (_callback is not null)
                    await _callback(publication, token).ConfigureAwait(false);
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                WindowsSplitOutputPublisher.TestHook.Value = _previous;
            }
        }
    }

    private static object FailureEvidence(Exception exception) => new
    {
        type = exception.GetType().FullName,
        message = exception.Message,
        hresult = exception.HResult,
        innerType = exception.InnerException?.GetType().FullName,
        innerMessage = exception.InnerException?.Message
    };

    private static class MergeTestNative
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(string linkPath, string existingPath, IntPtr securityAttributes);
    }
}
