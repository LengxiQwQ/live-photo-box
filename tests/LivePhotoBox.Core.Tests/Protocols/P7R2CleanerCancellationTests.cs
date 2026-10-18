using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class P7R2CleanerCancellationTests
{
    [Theory]
    [InlineData("Oppo", CleanerFailureStage.Preflight, null)]
    [InlineData("Oppo", CleanerFailureStage.ArtifactVerification, null)]
    [InlineData("Oppo", CleanerFailureStage.Authorization, null)]
    [InlineData("Oppo", CleanerFailureStage.Planning, null)]
    [InlineData("Oppo", CleanerFailureStage.Staging, "BeforeNative")]
    [InlineData("ApplePair", CleanerFailureStage.Staging, "ImageStaged")]
    [InlineData("ApplePair", CleanerFailureStage.Staging, "VideoStaged")]
    [InlineData("ApplePair", CleanerFailureStage.PreservationDiff, null)]
    [InlineData("ApplePair", CleanerFailureStage.MediaValidation, null)]
    [InlineData("ApplePair", CleanerFailureStage.PostCleanInspection, "BeforeInspect")]
    [InlineData("ApplePair", CleanerFailureStage.Commit, "BeforePublish")]
    [InlineData("ApplePair", CleanerFailureStage.Commit, "AfterImageIdentityVerifiedBeforeRename")]
    [InlineData("ApplePair", CleanerFailureStage.Commit, "ImagePublished")]
    [InlineData("ApplePair", CleanerFailureStage.Commit, "AfterVideoIdentityVerifiedBeforeRename")]
    [InlineData("ApplePair", CleanerFailureStage.Commit, "VideoPublished")]
    [InlineData("ApplePair", CleanerFailureStage.Commit, "BeforeBundleCommit")]
    [InlineData("NonLive", CleanerFailureStage.Commit, "ImagePublished")]
    [InlineData("NonLive", CleanerFailureStage.Commit, "BeforeBundleCommit")]
    [Trait("Category", "RealSamples")]
    public async Task Clean_CancellationAtManagedSeam_PropagatesAndLeavesExtractedBaseline(
        string sampleKind,
        CleanerFailureStage targetStage,
        string? targetDetail)
    {
        (string primaryPath, string? secondaryPath) = ResolveSources(sampleKind);
        string[] sourcePaths = secondaryPath is null
            ? [primaryPath]
            : [primaryPath, secondaryPath];
        SortedDictionary<string, string> sourceHashesBefore = SnapshotHashes(sourcePaths);

        using var workspace = new MediaWorkspace();
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(
            primaryPath,
            secondaryPath);
        ExtractedMediaBundle extracted = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            primaryPath,
            secondaryPath,
            workspace);

        if (sampleKind == "NonLive")
        {
            using CleanupPlan originalPlan = extracted.CleanupPlan!;
            ProtocolCleanResult neutral = await new SourceProtocolCleaner().CleanAsync(
                new ProtocolCleanRequest { ExtractedBundle = extracted }, workspace);
            Assert.True(neutral.Success, neutral.ErrorMessage);
            using InspectedSource neutralInspection = await new SourceInspector().InspectWithPlanAsync(neutral.CleanedImage!.Path);
            Assert.Equal(SourceProtocol.NonLive, neutralInspection.Facts.Protocol);
            extracted = await new SourceExtractor().ExtractAsync(neutralInspection.ExtractionPlan, neutral.CleanedImage.Path, null, workspace);
        }

        Assert.NotNull(extracted.CleanupPlan);
        using CleanupPlan cleanupPlan = extracted.CleanupPlan!;

        // The baseline is taken only after production P2 extraction has published its
        // authorized artifacts into the workspace.
        KeyValuePair<string, string>[] extractedFilesBefore = SnapshotFiles(workspace.RootDirectory);
        string[] extractedDirectoriesBefore = SnapshotDirectories(workspace.RootDirectory);

        using var cancellationSource = new CancellationTokenSource();
        var cleaner = new SourceProtocolCleaner();
        bool targetSeamReached = false;

        // This hook only requests cancellation.  In particular, it never throws;
        // propagation must come from the cleaner/native operation observing the token.
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (!targetSeamReached &&
                stage == targetStage &&
                string.Equals(detail, targetDetail, StringComparison.Ordinal))
            {
                targetSeamReached = true;
                cancellationSource.Cancel();
            }

            return Task.CompletedTask;
        };

        ProtocolCleanResult? cleanResult = null;
        Exception? observedException = null;
        try
        {
            cleanResult = await cleaner.CleanAsync(
                new ProtocolCleanRequest
                {
                    ExtractedBundle = extracted,
                    CleanupPlan = cleanupPlan
                },
                workspace,
                cancellationSource.Token);
        }
        catch (Exception exception)
        {
            observedException = exception;
        }

        SortedDictionary<string, string> sourceHashesAfter = SnapshotHashes(sourcePaths);
        KeyValuePair<string, string>[] workspaceFilesAfter = SnapshotFiles(workspace.RootDirectory);
        string[] workspaceDirectoriesAfter = SnapshotDirectories(workspace.RootDirectory);
        string actualOutcome = GetActualOutcome(cleanResult, observedException);

        WriteCaseEvidenceIfConfigured(
            sampleKind,
            targetStage,
            targetDetail,
            new
            {
                sampleKind,
                sourceFiles = sourcePaths,
                sourceHashesBefore,
                sourceHashesAfter,
                targetSeam = new
                {
                    stage = targetStage.ToString(),
                    detail = targetDetail,
                    reached = targetSeamReached,
                    cancellationRequested = cancellationSource.IsCancellationRequested
                },
                actualOutcome,
                observedException = observedException?.ToString(),
                cleanerResult = cleanResult is null
                    ? null
                    : new
                    {
                        cleanResult.Success,
                        failureCategory = cleanResult.FailureCategory?.ToString(),
                        failureStage = cleanResult.FailureStage?.ToString(),
                        transactionState = cleanResult.TransactionState?.ToString(),
                        cleanResult.ErrorMessage,
                        cleanedImagePath = cleanResult.CleanedImage?.Path,
                        cleanedVideoPath = cleanResult.CleanedVideo?.Path
                    },
                workspace = new
                {
                    root = workspace.RootDirectory,
                    extractedFilesBefore,
                    workspaceFilesAfter,
                    extractedDirectoriesBefore,
                    workspaceDirectoriesAfter
                }
            });

        Assert.True(
            targetSeamReached,
            $"The requested managed seam {targetStage}/{targetDetail ?? "<stage>"} was not reached. " +
            $"Actual outcome: {actualOutcome}. Failure: {DescribeFailure(cleanResult, observedException)}");
        Assert.True(
            cancellationSource.IsCancellationRequested,
            "The target seam was reached without requesting cancellation.");
        Assert.IsAssignableFrom<OperationCanceledException>(observedException);

        Assert.Equal(
            sourceHashesBefore.ToArray(),
            sourceHashesAfter.ToArray());
        Assert.Equal(extractedFilesBefore, workspaceFilesAfter);
        Assert.Equal(extractedDirectoriesBefore, workspaceDirectoriesAfter);
        Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*", SearchOption.TopDirectoryOnly));
        // The NonLive fixture's setup first materializes and then re-extracts a
        // cleaned artifact, so legitimate clean-img/clean-vid files already
        // belong to the extracted baseline. Exact file/hash equality above is
        // the ownership check: it rejects newly created residue without
        // incorrectly requiring those baseline artifacts to be absent.
    }

    private static (string PrimaryPath, string? SecondaryPath) ResolveSources(string sampleKind)
    {
        return sampleKind switch
        {
            "Oppo" => (TestSampleResolver.ResolveSample("oppo.jpg"), null),
            "NonLive" => (TestSampleResolver.ResolveSample("oppo.jpg"), null),
            "ApplePair" =>
            (
                TestSampleResolver.ResolveSample("苹果双文件.HEIC"),
                TestSampleResolver.ResolveSample("苹果双文件.MOV")
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(sampleKind), sampleKind, "Unknown real-sample fixture.")
        };
    }

    private static SortedDictionary<string, string> SnapshotHashes(IEnumerable<string> paths)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            using var stream = File.OpenRead(path);
            hashes[path] = Convert.ToHexString(SHA256.HashData(stream));
        }

        return hashes;
    }

    private static KeyValuePair<string, string>[] SnapshotFiles(string rootDirectory)
    {
        return Directory
            .EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new KeyValuePair<string, string>(
                NormalizeRelativePath(rootDirectory, path),
                ComputeSha256(path)))
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SnapshotDirectories(string rootDirectory)
    {
        return Directory
            .EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(rootDirectory, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string NormalizeRelativePath(string rootDirectory, string path)
    {
        return Path.GetRelativePath(rootDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string GetActualOutcome(ProtocolCleanResult? result, Exception? exception)
    {
        if (exception is OperationCanceledException) return "OperationCanceledException";
        if (exception is not null) return "UnexpectedException";
        if (result?.Success == true) return "ReturnedSuccess";
        if (result is not null) return "ReturnedFailure";
        return "NoOutcome";
    }

    private static string DescribeFailure(ProtocolCleanResult? result, Exception? exception)
    {
        if (exception is not null) return exception.ToString();
        if (result is null) return "No result or exception.";
        return result.ErrorMessage ?? result.FailureCategory?.ToString() ?? "No failure details.";
    }

    private static void WriteCaseEvidenceIfConfigured(
        string sampleKind,
        CleanerFailureStage stage,
        string? detail,
        object evidence)
    {
        string? evidenceRoot = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidenceRoot)) return;

        Directory.CreateDirectory(evidenceRoot);
        string detailPart = string.IsNullOrWhiteSpace(detail) ? "stage" : detail;
        string fileName = $"{sampleKind}-{stage}-{detailPart}.json";
        string evidencePath = Path.Combine(evidenceRoot, fileName);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            evidence,
            new JsonSerializerOptions { WriteIndented = true });

        // CreateNew makes evidence immutable across reruns: an existing case file
        // is retained and the collision is surfaced to the caller.
        using var output = new FileStream(
            evidencePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        output.Write(json);
        output.Flush(flushToDisk: true);
    }
}
