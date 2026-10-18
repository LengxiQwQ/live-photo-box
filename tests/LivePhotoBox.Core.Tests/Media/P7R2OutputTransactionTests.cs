using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2OutputTransactionTests
{
    private const string AppleImageName = "苹果双文件.HEIC";
    private const string AppleVideoName = "苹果双文件.MOV";

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task SplitPublisher_CancelAfterFirstTargetMutation_RestoresBothOriginalObjects()
    {
        using PreparedApplePair pair = await PrepareApplePairAsync();
        using var outputFixture = new TestFixtureWorkspace();
        string outputDirectory = Path.Combine(outputFixture.RootDirectory, "split-cancel-output");
        Directory.CreateDirectory(outputDirectory);
        string imageTarget = Path.Combine(outputDirectory, "foreign-image.heic");
        string videoTarget = Path.Combine(outputDirectory, "foreign-video.mov");
        await File.WriteAllBytesAsync(imageTarget, [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x49]);
        await File.WriteAllBytesAsync(videoTarget, [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x56]);

        var imageBefore = CaptureFile(imageTarget);
        var videoBefore = CaptureFile(videoTarget);
        using var cancellation = new CancellationTokenSource();
        bool targetSeamReached = false;
        Exception? failure = null;
        string? actualOutcome = null;

        Task OnPublicationTraceAsync(SplitPublicationTraceEvent trace, CancellationToken _)
        {
            if (!targetSeamReached &&
                trace.Boundary == SplitPublicationBoundary.AfterTargetMutation &&
                string.Equals(trace.FinalPath, imageTarget, StringComparison.OrdinalIgnoreCase))
            {
                targetSeamReached = true;
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }

        try
        {
            failure = await Record.ExceptionAsync(() => WindowsSplitOutputPublisher.PublishAsync(
                pair.Bundle.PrimaryImage.Path,
                pair.Bundle.MotionVideo!.Path,
                imageTarget,
                videoTarget,
                overwriteExisting: true,
                OnPublicationTraceAsync,
                cancellation.Token));

            actualOutcome = failure is OperationCanceledException
                ? "OperationCanceledException"
                : failure is null ? "ReturnedSuccess" : "UnexpectedException";

            Assert.True(targetSeamReached, "AfterTargetMutation was not reached for the first target.");
            Assert.True(cancellation.IsCancellationRequested);
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(imageBefore, CaptureFile(imageTarget));
            Assert.Equal(videoBefore, CaptureFile(videoTarget));
            Assert.Equal(pair.SourceHashesBefore.ToArray(), SnapshotHashes(pair.SourcePaths).ToArray());
            Assert.Empty(Directory.GetFiles(outputDirectory, ".lpb-split-*.tmp", SearchOption.TopDirectoryOnly));
            Assert.Equal(
                new[] { Path.GetFileName(imageTarget), Path.GetFileName(videoTarget) }.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                Directory.GetFiles(outputDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            WriteEvidence("split-publisher-cancel-after-mutation", new
            {
                operation = "WindowsSplitOutputPublisher.PublishAsync",
                authority = pair.AuthorityEvidence,
                sourcePaths = pair.SourcePaths,
                sourceHashesBefore = pair.SourceHashesBefore,
                sourceHashesAfter = SnapshotHashes(pair.SourcePaths),
                targetSeam = new
                {
                    boundary = SplitPublicationBoundary.AfterTargetMutation.ToString(),
                    target = imageTarget,
                    reached = targetSeamReached,
                    cancellationRequested = cancellation.IsCancellationRequested,
                    hookThrows = false
                },
                actualOutcome,
                observedFailure = FailureEvidence(failure),
                targetsBefore = new { image = imageBefore, video = videoBefore },
                targetsAfter = new { image = TryCaptureFile(imageTarget), video = TryCaptureFile(videoTarget) },
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedSplitResidueAfter = Directory.GetFiles(outputDirectory, ".lpb-split-*.tmp", SearchOption.TopDirectoryOnly)
            });
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task SplitPublisher_RealAclDenyCreateFiles_FailsAtStagingAndPreservesTargets()
    {
        using PreparedApplePair pair = await PrepareApplePairAsync();
        using var outputFixture = new TestFixtureWorkspace();
        string outputDirectory = Path.Combine(outputFixture.RootDirectory, "split-acl-output");
        Directory.CreateDirectory(outputDirectory);
        string imageTarget = Path.Combine(outputDirectory, "foreign-image.heic");
        string videoTarget = Path.Combine(outputDirectory, "foreign-video.mov");
        await File.WriteAllBytesAsync(imageTarget, [0x41, 0x43, 0x4C, 0x2D, 0x49]);
        await File.WriteAllBytesAsync(videoTarget, [0x41, 0x43, 0x4C, 0x2D, 0x56]);

        var imageBefore = CaptureFile(imageTarget);
        var videoBefore = CaptureFile(videoTarget);
        DirectorySecurity? originalAcl = null;
        Exception? aclProbeFailure = null;
        Exception? failure = null;
        string? actualOutcome = null;
        string probePath = Path.Combine(outputDirectory, "acl-create-probe.tmp");
        bool aclRestored = false;

        try
        {
            originalAcl = DenyCreateFiles(outputDirectory);
            aclProbeFailure = await Record.ExceptionAsync(() => File.WriteAllBytesAsync(probePath, [0x50]));
            Assert.IsType<UnauthorizedAccessException>(aclProbeFailure);
            Assert.False(File.Exists(probePath));

            failure = await Record.ExceptionAsync(() => WindowsSplitOutputPublisher.PublishAsync(
                pair.Bundle.PrimaryImage.Path,
                pair.Bundle.MotionVideo!.Path,
                imageTarget,
                videoTarget,
                overwriteExisting: true,
                testHook: null,
                CancellationToken.None));
            actualOutcome = failure is IOException ? "IOException" : failure is null ? "ReturnedSuccess" : "UnexpectedException";

            IOException stagingFailure = Assert.IsType<IOException>(failure);
            Assert.Contains("transaction-owned Split stage", stagingFailure.Message, StringComparison.OrdinalIgnoreCase);
            Win32Exception denied = Assert.IsType<Win32Exception>(stagingFailure.InnerException);
            Assert.Equal(5, denied.NativeErrorCode);
            Assert.Equal(imageBefore, CaptureFile(imageTarget));
            Assert.Equal(videoBefore, CaptureFile(videoTarget));
            Assert.Equal(pair.SourceHashesBefore.ToArray(), SnapshotHashes(pair.SourcePaths).ToArray());
            Assert.Empty(Directory.GetFiles(outputDirectory, ".lpb-split-*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (originalAcl is not null)
            {
                new DirectoryInfo(outputDirectory).SetAccessControl(originalAcl);
                aclRestored = string.Equals(
                    new DirectoryInfo(outputDirectory).GetAccessControl(AccessControlSections.Access)
                        .GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    originalAcl.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    StringComparison.Ordinal);
            }

            WriteEvidence("split-publisher-acl-deny-staging", new
            {
                operation = "WindowsSplitOutputPublisher.PublishAsync",
                authority = pair.AuthorityEvidence,
                sourcePaths = pair.SourcePaths,
                sourceHashesBefore = pair.SourceHashesBefore,
                sourceHashesAfter = SnapshotHashes(pair.SourcePaths),
                fault = new
                {
                    kind = "RealWindowsAclDenyCreateFiles",
                    probePath,
                    probeFailure = FailureEvidence(aclProbeFailure),
                    verifiedDenied = aclProbeFailure is UnauthorizedAccessException,
                    expectedWin32AccessDenied = 5
                },
                actualOutcome,
                observedFailure = FailureEvidence(failure),
                targetsBefore = new { image = imageBefore, video = videoBefore },
                targetsAfter = new { image = TryCaptureFile(imageTarget), video = TryCaptureFile(videoTarget) },
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedSplitResidueAfter = Directory.GetFiles(outputDirectory, ".lpb-split-*.tmp", SearchOption.TopDirectoryOnly),
                aclRestored
            });
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeImageCopy_PreCancelledAndOccupiedDestination_PreservesOutputAndCleansStage()
    {
        using PreparedApplePair pair = await PrepareApplePairAsync();
        using var outputFixture = new TestFixtureWorkspace();
        string outputDirectory = Path.Combine(outputFixture.RootDirectory, "image-copy-output");
        Directory.CreateDirectory(outputDirectory);
        string cancelledOutput = Path.Combine(outputDirectory, "cancelled.heic");
        string occupiedOutput = Path.Combine(outputDirectory, "foreign.heic");
        byte[] sentinel = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x49, 0x4D, 0x47];
        await File.WriteAllBytesAsync(occupiedOutput, sentinel);
        var occupiedBefore = CaptureFile(occupiedOutput);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? cancelFailure = null;
        Exception? collisionFailure = null;
        string? actualCancelOutcome = null;
        string? actualCollisionOutcome = null;

        try
        {
            cancelFailure = await Record.ExceptionAsync(() => NativeMediaService.ConvertImageAsync(
                pair.Bundle.PrimaryImage.Path,
                cancelledOutput,
                pair.Bundle.SourceFacts.PrimaryImage.Container,
                quality: 90,
                cancellation.Token));
            actualCancelOutcome = cancelFailure is OperationCanceledException
                ? "OperationCanceledException"
                : cancelFailure is null ? "ReturnedSuccess" : "UnexpectedException";
            Assert.IsAssignableFrom<OperationCanceledException>(cancelFailure);
            Assert.Equal(cancellation.Token, ((OperationCanceledException)cancelFailure!).CancellationToken);
            Assert.False(File.Exists(cancelledOutput));

            collisionFailure = await Record.ExceptionAsync(() => NativeMediaService.ConvertImageAsync(
                pair.Bundle.PrimaryImage.Path,
                occupiedOutput,
                pair.Bundle.SourceFacts.PrimaryImage.Container,
                quality: 90,
                CancellationToken.None));
            actualCollisionOutcome = collisionFailure is InvalidOperationException
                ? "InvalidOperationException"
                : collisionFailure is null ? "ReturnedSuccess" : "UnexpectedException";

            InvalidOperationException destinationCollision = Assert.IsType<InvalidOperationException>(collisionFailure);
            Assert.Contains("Failed to copy image through an owned output", destinationCollision.Message, StringComparison.Ordinal);
            Assert.Equal(occupiedBefore, CaptureFile(occupiedOutput));
            Assert.Equal(pair.SourceHashesBefore.ToArray(), SnapshotHashes(pair.SourcePaths).ToArray());
            Assert.Empty(Directory.GetFiles(outputDirectory, "lpb-image-copy-*.tmp", SearchOption.TopDirectoryOnly));
            Assert.Equal(new[] { Path.GetFileName(occupiedOutput) }, Directory.GetFiles(outputDirectory).Select(Path.GetFileName));
        }
        finally
        {
            WriteEvidence("native-image-copy-cancel-and-collision", new
            {
                operation = "NativeMediaService.ConvertImageAsync",
                authority = pair.AuthorityEvidence,
                input = pair.Bundle.PrimaryImage.Path,
                targetContainer = pair.Bundle.SourceFacts.PrimaryImage.Container.ToString(),
                sourcePaths = pair.SourcePaths,
                sourceHashesBefore = pair.SourceHashesBefore,
                sourceHashesAfter = SnapshotHashes(pair.SourcePaths),
                preCancelledCall = new
                {
                    tokenAlreadyCancelled = true,
                    actualOutcome = actualCancelOutcome,
                    failure = FailureEvidence(cancelFailure),
                    outputCreated = File.Exists(cancelledOutput)
                },
                destinationConflict = new
                {
                    target = occupiedOutput,
                    sentinelHex = Convert.ToHexString(sentinel),
                    before = occupiedBefore,
                    after = TryCaptureFile(occupiedOutput),
                    actualOutcome = actualCollisionOutcome,
                    failure = FailureEvidence(collisionFailure)
                },
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedImageCopyResidueAfter = Directory.GetFiles(outputDirectory, "lpb-image-copy-*.tmp", SearchOption.TopDirectoryOnly)
            });
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeVideoRemux_PreCancelledAndOccupiedDestination_PreservesOutputAndCleansStage()
    {
        using PreparedApplePair pair = await PrepareApplePairAsync();
        using var outputFixture = new TestFixtureWorkspace();
        string outputDirectory = Path.Combine(outputFixture.RootDirectory, "video-remux-output");
        Directory.CreateDirectory(outputDirectory);
        string cancelledOutput = Path.Combine(outputDirectory, "cancelled.mp4");
        string occupiedOutput = Path.Combine(outputDirectory, "foreign.mp4");
        byte[] sentinel = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x56, 0x49, 0x44];
        await File.WriteAllBytesAsync(occupiedOutput, sentinel);
        var occupiedBefore = CaptureFile(occupiedOutput);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? cancelFailure = null;
        Exception? collisionFailure = null;
        string? actualCancelOutcome = null;
        string? actualCollisionOutcome = null;

        try
        {
            cancelFailure = await Record.ExceptionAsync(() => NativeMediaService.RemuxVideoAsync(
                pair.Bundle.MotionVideo!.Path,
                cancelledOutput,
                VideoContainer.Mp4,
                cancellation.Token));
            actualCancelOutcome = cancelFailure is OperationCanceledException
                ? "OperationCanceledException"
                : cancelFailure is null ? "ReturnedSuccess" : "UnexpectedException";
            Assert.IsAssignableFrom<OperationCanceledException>(cancelFailure);
            Assert.Equal(cancellation.Token, ((OperationCanceledException)cancelFailure!).CancellationToken);
            Assert.False(File.Exists(cancelledOutput));

            collisionFailure = await Record.ExceptionAsync(() => NativeMediaService.RemuxVideoAsync(
                pair.Bundle.MotionVideo.Path,
                occupiedOutput,
                VideoContainer.Mp4,
                CancellationToken.None));
            actualCollisionOutcome = collisionFailure is InvalidOperationException
                ? "InvalidOperationException"
                : collisionFailure is null ? "ReturnedSuccess" : "UnexpectedException";

            InvalidOperationException destinationCollision = Assert.IsType<InvalidOperationException>(collisionFailure);
            Assert.Contains("Failed to publish the owned remuxed video without replacing an existing artifact", destinationCollision.Message, StringComparison.Ordinal);
            Assert.Equal(occupiedBefore, CaptureFile(occupiedOutput));
            Assert.Equal(pair.SourceHashesBefore.ToArray(), SnapshotHashes(pair.SourcePaths).ToArray());
            Assert.Empty(Directory.GetFiles(outputDirectory, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly));
            Assert.Equal(new[] { Path.GetFileName(occupiedOutput) }, Directory.GetFiles(outputDirectory).Select(Path.GetFileName));
        }
        finally
        {
            WriteEvidence("native-video-remux-cancel-and-collision", new
            {
                operation = "NativeMediaService.RemuxVideoAsync",
                authority = pair.AuthorityEvidence,
                input = pair.Bundle.MotionVideo!.Path,
                targetContainer = VideoContainer.Mp4.ToString(),
                sourcePaths = pair.SourcePaths,
                sourceHashesBefore = pair.SourceHashesBefore,
                sourceHashesAfter = SnapshotHashes(pair.SourcePaths),
                preCancelledCall = new
                {
                    tokenAlreadyCancelled = true,
                    actualOutcome = actualCancelOutcome,
                    failure = FailureEvidence(cancelFailure),
                    outputCreated = File.Exists(cancelledOutput)
                },
                destinationConflict = new
                {
                    target = occupiedOutput,
                    sentinelHex = Convert.ToHexString(sentinel),
                    before = occupiedBefore,
                    after = TryCaptureFile(occupiedOutput),
                    actualOutcome = actualCollisionOutcome,
                    failure = FailureEvidence(collisionFailure)
                },
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedRemuxResidueAfter = Directory.GetFiles(outputDirectory, "lpb-remux-*.tmp", SearchOption.TopDirectoryOnly)
            });
        }
    }

    private static async Task<PreparedApplePair> PrepareApplePairAsync()
    {
        string originalImage = TestSampleResolver.ResolveSample(AppleImageName);
        string originalVideo = TestSampleResolver.ResolveSample(AppleVideoName);
        var workspace = new MediaWorkspace();
        InspectedSource? inspected = null;
        ExtractedMediaBundle? bundle = null;
        try
        {
            inspected = await new SourceInspector().InspectWithPlanAsync(originalImage, originalVideo);
            bundle = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                originalImage,
                originalVideo,
                workspace);
            Assert.Equal(SourceProtocol.AppleLivePhoto, bundle.SourceFacts.Protocol);
            Assert.NotNull(bundle.MotionVideo);
            Assert.NotNull(bundle.CleanupPlan);

            string[] sourcePaths = [originalImage, originalVideo, bundle.PrimaryImage.Path, bundle.MotionVideo!.Path];
            var sourceHashes = SnapshotHashes(sourcePaths);
            object authorityEvidence = new
            {
                chain = "SourceInspector.InspectWithPlanAsync -> SourceExtractor.ExtractAsync",
                protocol = bundle.SourceFacts.Protocol.ToString(),
                originalImage,
                originalVideo,
                extractionPlanIssued = inspected.ExtractionPlan is not null,
                cleanupPlanIssued = bundle.CleanupPlan is not null,
                extractedPrimaryImage = bundle.PrimaryImage.Path,
                extractedMotionVideo = bundle.MotionVideo!.Path
            };

            return new PreparedApplePair(workspace, inspected, bundle, sourcePaths, sourceHashes, authorityEvidence);
        }
        catch
        {
            bundle?.CleanupPlan?.Dispose();
            inspected?.Dispose();
            workspace.Dispose();
            throw;
        }
    }

    private static DirectorySecurity DenyCreateFiles(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        DirectorySecurity current = directory.GetAccessControl(AccessControlSections.Access);
        var original = new DirectorySecurity();
        original.SetSecurityDescriptorBinaryForm(
            current.GetSecurityDescriptorBinaryForm(),
            AccessControlSections.Access);

        DirectorySecurity changed = directory.GetAccessControl(AccessControlSections.Access);
        changed.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        directory.SetAccessControl(changed);
        return original;
    }

    private static SortedDictionary<string, string> SnapshotHashes(IEnumerable<string> paths)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
            hashes[path] = File.Exists(path) ? ComputeSha256(path) : "<missing>";
        return hashes;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static FileEvidence CaptureFile(string path) => new(
        Path.GetFullPath(path),
        new FileInfo(path).Length,
        ComputeSha256(path),
        WindowsFileIdentity.Capture(path));

    private static FileEvidence? TryCaptureFile(string path)
    {
        try
        {
            return File.Exists(path) ? CaptureFile(path) : null;
        }
        catch (Exception exception)
        {
            return new FileEvidence(Path.GetFullPath(path), null, null, null, exception.ToString());
        }
    }

    private static object[] SnapshotFiles(string directory)
    {
        return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => (object)new
            {
                path = Path.GetFileName(path),
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

    private static void WriteEvidence(string name, object value)
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE");
        if (string.IsNullOrWhiteSpace(configuredRoot))
            throw new InvalidOperationException("LPB_R2_EVIDENCE is required for P7-R2 output transaction evidence.");
        string evidenceRoot = configuredRoot;
        Directory.CreateDirectory(evidenceRoot);
        string path = Path.Combine(evidenceRoot, name + ".json");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(json);
        output.Flush(flushToDisk: true);
    }

    private sealed record FileEvidence(
        string Path,
        long? Bytes,
        string? Sha256,
        WindowsFileIdentity? Identity,
        string? CaptureFailure = null);

    private sealed class PreparedApplePair : IDisposable
    {
        public PreparedApplePair(
            MediaWorkspace workspace,
            InspectedSource inspected,
            ExtractedMediaBundle bundle,
            string[] sourcePaths,
            SortedDictionary<string, string> sourceHashesBefore,
            object authorityEvidence)
        {
            Workspace = workspace;
            Inspected = inspected;
            Bundle = bundle;
            SourcePaths = sourcePaths;
            SourceHashesBefore = sourceHashesBefore;
            AuthorityEvidence = authorityEvidence;
        }

        public MediaWorkspace Workspace { get; }
        public InspectedSource Inspected { get; }
        public ExtractedMediaBundle Bundle { get; }
        public string[] SourcePaths { get; }
        public SortedDictionary<string, string> SourceHashesBefore { get; }
        public object AuthorityEvidence { get; }

        public void Dispose()
        {
            Bundle.CleanupPlan?.Dispose();
            Inspected.Dispose();
            Workspace.Dispose();
        }
    }
}
