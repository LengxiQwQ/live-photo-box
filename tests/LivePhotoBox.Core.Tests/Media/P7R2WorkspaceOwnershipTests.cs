using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2WorkspaceOwnershipTests
{
    private static readonly byte[] RegisteredFixtureBytes = [0x52, 0x32, 0x2D, 0x4F, 0x57, 0x4E, 0x45, 0x52];
    private static readonly byte[] ForeignFixtureBytes = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E];

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealSamples")]
    public async Task ProductionInspectorExtractorCleaner_RegistersReceiptsAndDisposesWorkspaceExactly()
    {
        string imageSource = TestSampleResolver.ResolveSample("苹果双文件.HEIC");
        string videoSource = TestSampleResolver.ResolveSample("苹果双文件.MOV");
        FileEvidence[] sourceBefore = [CaptureFile(imageSource), CaptureFile(videoSource)];
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        InspectedSource? inspected = null;
        CleanupPlan? cleanupPlan = null;
        ExtractedMediaBundle? extracted = null;
        ProtocolCleanResult? cleanResult = null;
        FileEvidence[] ownedOutputsBeforeDispose = [];
        bool primaryReceiptPresent = false;
        bool videoReceiptPresent = false;
        bool cleanImageReceiptPresent = false;
        bool cleanVideoReceiptPresent = false;
        bool rootAbsentAfterDispose = false;
        FileEvidence[] sourceAfter = [];
        Exception? failure = null;

        try
        {
            inspected = await new SourceInspector().InspectWithPlanAsync(imageSource, videoSource);
            extracted = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                imageSource,
                videoSource,
                workspace);

            Assert.Equal(SourceProtocol.AppleLivePhoto, extracted.SourceFacts.Protocol);
            Assert.NotNull(extracted.MotionVideo);
            Assert.NotNull(extracted.CleanupPlan);
            cleanupPlan = extracted.CleanupPlan;

            ProtocolCleanResult result = await new SourceProtocolCleaner().CleanAsync(
                new ProtocolCleanRequest
                {
                    ExtractedBundle = extracted,
                    CleanupPlan = cleanupPlan,
                    PreservationPolicy = PreservationPolicy.BestEffort
                },
                workspace);
            cleanResult = result;
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.CleanedImage);
            Assert.NotNull(result.CleanedVideo);
            MediaArtifact cleanedImage = result.CleanedImage!;
            MediaArtifact cleanedVideo = result.CleanedVideo!;

            primaryReceiptPresent = workspace.HasRegisteredFile(
                extracted.PrimaryImage.Path,
                extracted.PrimaryImage.FileIdentity!);
            videoReceiptPresent = workspace.HasRegisteredFile(
                extracted.MotionVideo!.Path,
                extracted.MotionVideo.FileIdentity!);
            WindowsFileIdentity? cleanImageIdentity = cleanedImage.FileIdentity;
            WindowsFileIdentity? cleanVideoIdentity = cleanedVideo.FileIdentity;
            cleanImageReceiptPresent = cleanImageIdentity is not null &&
                workspace.HasRegisteredFile(cleanedImage.Path, cleanImageIdentity);
            cleanVideoReceiptPresent = cleanVideoIdentity is not null &&
                workspace.HasRegisteredFile(cleanedVideo.Path, cleanVideoIdentity);

            Assert.True(primaryReceiptPresent, "The extracted primary image must retain its producer-issued workspace receipt.");
            Assert.True(videoReceiptPresent, "The extracted motion video must retain its producer-issued workspace receipt.");
            Assert.True(cleanImageReceiptPresent, "The cleaned image must be registered from the exact publication receipt.");
            Assert.True(cleanVideoReceiptPresent, "The cleaned video must be registered from the exact publication receipt.");

            ownedOutputsBeforeDispose = SnapshotFiles(root);
            Assert.NotEmpty(ownedOutputsBeforeDispose);

            cleanupPlan.Dispose();
            cleanupPlan = null;
            inspected.Dispose();
            inspected = null;
            workspace.Dispose();
            rootAbsentAfterDispose = !Directory.Exists(root);
            Assert.True(rootAbsentAfterDispose, "Disposing a workspace with known production receipts must remove its root.");
            Assert.All(ownedOutputsBeforeDispose, output =>
                Assert.False(File.Exists(output.Path), $"Workspace disposal left registered output '{output.Path}' behind."));
            sourceAfter = [CaptureFile(imageSource), CaptureFile(videoSource)];
            Assert.Equal(sourceBefore, sourceAfter);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            cleanupPlan?.Dispose();
            inspected?.Dispose();
            if (Directory.Exists(root))
            {
                try { workspace.Dispose(); }
                catch (Exception exception) { failure ??= exception; }
            }

            sourceAfter = sourceAfter.Length == 0
                ? [CaptureFile(imageSource), CaptureFile(videoSource)]
                : sourceAfter;
            WriteEvidence("workspace-production-receipts-dispose", new
            {
                category = "RealSamples",
                authorityChain = "SourceInspector.InspectWithPlanAsync -> SourceExtractor.ExtractAsync -> SourceProtocolCleaner.CleanAsync",
                sources = new { before = sourceBefore, after = sourceAfter },
                protocol = extracted?.SourceFacts.Protocol.ToString(),
                extractedArtifacts = extracted is null ? null : new
                {
                    image = extracted.PrimaryImage.Path,
                    imageIdentity = extracted.PrimaryImage.FileIdentity,
                    video = extracted.MotionVideo?.Path,
                    videoIdentity = extracted.MotionVideo?.FileIdentity
                },
                cleanArtifacts = cleanResult is null ? null : new
                {
                    success = cleanResult.Success,
                    image = cleanResult.CleanedImage?.Path,
                    imageIdentity = cleanResult.CleanedImage?.FileIdentity,
                    video = cleanResult.CleanedVideo?.Path,
                    videoIdentity = cleanResult.CleanedVideo?.FileIdentity
                },
                receipts = new { primaryReceiptPresent, videoReceiptPresent, cleanImageReceiptPresent, cleanVideoReceiptPresent },
                ownedOutputsBeforeDispose,
                root,
                rootAbsentAfterDispose = rootAbsentAfterDispose || !Directory.Exists(root),
                failure = FailureEvidence(failure)
            });
        }
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealFilesystem")]
    public void WorkspaceRootPin_BlocksRenameWhileWorkspaceIsLive()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string moved = root + ".rename-probe";
        WindowsFileIdentity rootBefore = CaptureDirectory(root);
        Exception? renameFailure = null;
        Exception? disposeFailure = null;
        WindowsFileIdentity? rootAfterAttempt = null;

        try
        {
            renameFailure = Record.Exception(() => Directory.Move(root, moved));
            rootAfterAttempt = Directory.Exists(root) ? CaptureDirectory(root) : null;

            Assert.NotNull(renameFailure);
            Assert.True(renameFailure is IOException or UnauthorizedAccessException, renameFailure.ToString());
            Assert.True(rootAfterAttempt is not null && rootBefore == rootAfterAttempt,
                "A rejected rename must leave the same workspace root object at the registered path.");
            Assert.False(Directory.Exists(moved));

            int nativeError = NativeError(renameFailure);
            Assert.True(nativeError is 5 or 32,
                $"Expected access-denied/sharing-violation from the live root pin; native error was {nativeError}.");
        }
        finally
        {
            // If the pin did not block the operation, restore the exact root
            // name before disposing so this probe cannot orphan its workspace.
            if (Directory.Exists(moved) && !Directory.Exists(root))
                Directory.Move(moved, root);
            try { workspace.Dispose(); }
            catch (Exception exception) { disposeFailure = exception; }
            WriteEvidence("workspace-root-pin-rename", new
            {
                category = "RealFilesystem",
                root,
                moved,
                rootBefore,
                rootAfterAttempt,
                renameOutcome = renameFailure is null ? "Renamed" : "Rejected",
                renameFailure = FailureEvidence(renameFailure),
                nativeError = renameFailure is null ? (int?)null : NativeError(renameFailure),
                disposeFailure = FailureEvidence(disposeFailure),
                rootAbsentAfterDispose = !Directory.Exists(root)
            });
        }

        Assert.Null(disposeFailure);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "SyntheticFilesystem")]
    public void Dispose_RootRenamedAndForeignReplacementInstalled_FailsClosedAndCanRetry()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string relocatedOriginal = root + ".relocated-owned";
        string sentinelPath = Path.Combine(root, "foreign-root-sentinel.bin");
        WindowsFileIdentity originalRootIdentity = CaptureDirectory(root);
        FileEvidence? foreignSentinel = null;
        WindowsFileIdentity? replacementRootIdentity = null;
        bool hookReached = false;
        Exception? firstDisposeFailure = null;
        Exception? recoveryFailure = null;
        bool retrySucceeded = false;
        bool originalRootRetainedAfterFailure = false;
        bool foreignRootRetainedAfterFailure = false;
        bool foreignSentinelRetainedAfterFailure = false;
        AsyncLocal<Action<MediaWorkspaceCleanupStage, string>?> hook = MediaWorkspace.TestCleanupHook;
        Action<MediaWorkspaceCleanupStage, string>? previousHook = hook.Value;

        hook.Value = (stage, hookRoot) =>
        {
            if (stage != MediaWorkspaceCleanupStage.PinReleasedBeforeCleanupLease ||
                !string.Equals(Path.GetFullPath(hookRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
                hookReached)
                return;

            hookReached = true;
            Directory.Move(root, relocatedOriginal);
            Directory.CreateDirectory(root);
            replacementRootIdentity = CaptureDirectory(root);
            foreignSentinel = CreateUnregisteredFile(sentinelPath, ForeignFixtureBytes);
        };

        try
        {
            firstDisposeFailure = Record.Exception(workspace.Dispose);

            Assert.True(hookReached, "Dispose did not reach the pin-release/cleanup-lease seam.");
            Assert.IsType<IOException>(firstDisposeFailure);
            Assert.StartsWith("[CleanupFailed]", firstDisposeFailure!.Message, StringComparison.Ordinal);
            Assert.NotNull(foreignSentinel);
            Assert.NotNull(replacementRootIdentity);
            Assert.NotEqual(originalRootIdentity, replacementRootIdentity);
            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(relocatedOriginal));
            Assert.Equal(originalRootIdentity, CaptureDirectory(relocatedOriginal));
            Assert.Equal(replacementRootIdentity!, CaptureDirectory(root));
            Assert.Equal(foreignSentinel!, CaptureFile(sentinelPath));
            originalRootRetainedAfterFailure = true;
            foreignRootRetainedAfterFailure = true;
            foreignSentinelRetainedAfterFailure = true;

            hook.Value = previousHook;
            DeleteExactFile(foreignSentinel!, root);
            Assert.Equal(replacementRootIdentity!, CaptureDirectory(root));
            DeleteExactEmptyDirectory(root, replacementRootIdentity!, Path.GetDirectoryName(root)!);
            Directory.Move(relocatedOriginal, root);
            Assert.Equal(originalRootIdentity, CaptureDirectory(root));

            workspace.Dispose();
            retrySucceeded = !Directory.Exists(root);
            Assert.True(retrySucceeded, "Restoring the original root namespace must allow exact cleanup retry.");
        }
        catch (Exception exception)
        {
            recoveryFailure = exception;
            throw;
        }
        finally
        {
            hook.Value = previousHook;
            if (File.Exists(sentinelPath) && foreignSentinel is not null)
            {
                try
                {
                    if (CaptureFile(sentinelPath).Identity == foreignSentinel.Identity)
                        DeleteExactFile(foreignSentinel, root);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (Directory.Exists(root) && replacementRootIdentity is not null)
            {
                try
                {
                    if (CaptureDirectory(root) == replacementRootIdentity && !Directory.EnumerateFileSystemEntries(root).Any())
                        DeleteExactEmptyDirectory(root, replacementRootIdentity, Path.GetDirectoryName(root)!);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (Directory.Exists(relocatedOriginal) && !Directory.Exists(root))
            {
                try
                {
                    if (CaptureDirectory(relocatedOriginal) == originalRootIdentity)
                        Directory.Move(relocatedOriginal, root);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (Directory.Exists(root))
            {
                try { workspace.Dispose(); }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }

            WriteEvidence("workspace-root-replacement-cleanup", new
            {
                category = "SyntheticFilesystem",
                root,
                relocatedOriginal,
                hookReached,
                cleanupStage = MediaWorkspaceCleanupStage.PinReleasedBeforeCleanupLease.ToString(),
                originalRootIdentity,
                replacementRootIdentity,
                foreignSentinel,
                actualOutcome = firstDisposeFailure is null ? "ReturnedSuccess" : firstDisposeFailure.GetType().Name,
                firstDisposeFailure = FailureEvidence(firstDisposeFailure),
                originalRootRetainedAfterFailure,
                foreignRootRetainedAfterFailure,
                foreignSentinelRetainedAfterFailure,
                retrySucceeded,
                recoveryFailure = FailureEvidence(recoveryFailure),
                rootAbsentAtEvidence = !Directory.Exists(root)
            });
        }
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "SyntheticFilesystem")]
    public void Dispose_RegisteredChildReplacedAtSamePath_PreservesBothObjectsAndCanRetry()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string ownedPath = Path.Combine(root, "registered-owned.bin");
        string relocatedOwnedPath = root + ".registered-owned-relocated.bin";
        FileEvidence owned = CreateRegisteredFile(workspace, ownedPath, RegisteredFixtureBytes, "R2 registered child fixture");
        FileEvidence? foreign = null;
        Exception? firstDisposeFailure = null;
        Exception? recoveryFailure = null;
        bool retrySucceeded = false;
        bool originalOwnerRetainedAfterFailure = false;
        bool foreignRetainedAfterFailure = false;
        bool expectedIdentityRetainedAfterFailure = false;

        try
        {
            Assert.False(File.Exists(relocatedOwnedPath));
            File.Move(ownedPath, relocatedOwnedPath);
            foreign = CreateUnregisteredFile(ownedPath, ForeignFixtureBytes);
            firstDisposeFailure = Record.Exception(workspace.Dispose);

            Assert.IsType<IOException>(firstDisposeFailure);
            Assert.StartsWith("[CleanupFailed]", firstDisposeFailure!.Message, StringComparison.Ordinal);
            expectedIdentityRetainedAfterFailure = workspace.HasRegisteredFile(ownedPath, owned.Identity);
            Assert.True(expectedIdentityRetainedAfterFailure,
                "The ledger must keep the original expected identity after a same-path foreign replacement.");
            Assert.Equal(owned with { Path = relocatedOwnedPath }, CaptureFile(relocatedOwnedPath));
            Assert.Equal(foreign, CaptureFile(ownedPath));
            originalOwnerRetainedAfterFailure = true;
            foreignRetainedAfterFailure = true;

            DeleteExactFile(foreign, root);
            File.Move(relocatedOwnedPath, ownedPath);
            Assert.Equal(owned, CaptureFile(ownedPath));
            workspace.Dispose();
            retrySucceeded = !Directory.Exists(root);
            Assert.True(retrySucceeded, "Restoring the registered object's path must allow a bounded cleanup retry.");
        }
        catch (Exception exception)
        {
            recoveryFailure = exception;
            throw;
        }
        finally
        {
            if (foreign is not null && File.Exists(ownedPath))
            {
                try
                {
                    if (CaptureFile(ownedPath).Identity == foreign.Identity)
                        DeleteExactFile(foreign, root);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (File.Exists(relocatedOwnedPath) && !File.Exists(ownedPath))
            {
                try
                {
                    if (CaptureFile(relocatedOwnedPath).Identity == owned.Identity)
                        File.Move(relocatedOwnedPath, ownedPath);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (Directory.Exists(root))
            {
                try { workspace.Dispose(); }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }

            WriteEvidence("workspace-registered-child-replacement", new
            {
                category = "SyntheticFilesystem",
                root,
                owned,
                relocatedOwnedPath,
                foreign,
                actualOutcome = firstDisposeFailure is null ? "ReturnedSuccess" : firstDisposeFailure.GetType().Name,
                firstDisposeFailure = FailureEvidence(firstDisposeFailure),
                originalOwnerRetainedAfterFailure,
                foreignRetainedAfterFailure,
                expectedIdentityRetainedAfterFailure,
                retrySucceeded,
                recoveryFailure = FailureEvidence(recoveryFailure),
                rootAbsentAtEvidence = !Directory.Exists(root)
            });
        }
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "SyntheticFilesystem")]
    public void Dispose_UnknownForeignDirectory_FailsWithoutRecursiveDeleteAndCanRetry()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string foreignDirectory = Path.Combine(root, "unknown-foreign-child");
        string sentinelPath = Path.Combine(foreignDirectory, "sentinel.bin");
        Directory.CreateDirectory(foreignDirectory);
        WindowsFileIdentity directoryIdentity = CaptureDirectory(foreignDirectory);
        FileEvidence sentinel = CreateUnregisteredFile(sentinelPath, ForeignFixtureBytes);
        Exception? firstDisposeFailure = null;
        Exception? recoveryFailure = null;
        bool retrySucceeded = false;
        bool directoryRetainedAfterFailure = false;
        bool sentinelRetainedAfterFailure = false;

        try
        {
            firstDisposeFailure = Record.Exception(workspace.Dispose);
            Assert.IsType<IOException>(firstDisposeFailure);
            Assert.StartsWith("[CleanupFailed]", firstDisposeFailure!.Message, StringComparison.Ordinal);
            Assert.Equal(directoryIdentity, CaptureDirectory(foreignDirectory));
            Assert.Equal(sentinel, CaptureFile(sentinelPath));
            directoryRetainedAfterFailure = true;
            sentinelRetainedAfterFailure = true;

            DeleteExactFile(sentinel, root);
            Assert.Equal(directoryIdentity, CaptureDirectory(foreignDirectory));
            DeleteExactEmptyDirectory(foreignDirectory, directoryIdentity, root);
            workspace.Dispose();
            retrySucceeded = !Directory.Exists(root);
            Assert.True(retrySucceeded);
        }
        catch (Exception exception)
        {
            recoveryFailure = exception;
            throw;
        }
        finally
        {
            if (File.Exists(sentinelPath))
            {
                try
                {
                    if (CaptureFile(sentinelPath) == sentinel)
                        DeleteExactFile(sentinel, root);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (Directory.Exists(foreignDirectory))
            {
                try
                {
                    if (CaptureDirectory(foreignDirectory) == directoryIdentity && !Directory.EnumerateFileSystemEntries(foreignDirectory).Any())
                        DeleteExactEmptyDirectory(foreignDirectory, directoryIdentity, root);
                }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            if (Directory.Exists(root))
            {
                try { workspace.Dispose(); }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }

            WriteEvidence("workspace-unknown-directory", new
            {
                category = "SyntheticFilesystem",
                root,
                foreignDirectory,
                directoryIdentity,
                sentinel,
                actualOutcome = firstDisposeFailure is null ? "ReturnedSuccess" : firstDisposeFailure.GetType().Name,
                firstDisposeFailure = FailureEvidence(firstDisposeFailure),
                directoryRetainedAfterFailure,
                sentinelRetainedAfterFailure,
                retrySucceeded,
                recoveryFailure = FailureEvidence(recoveryFailure),
                rootAbsentAtEvidence = !Directory.Exists(root)
            });
        }
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealFilesystem")]
    public void Dispose_RegisteredChildWithRealShareLock_FailsAndRetryRemovesExactObject()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string path = Path.Combine(root, "registered-sharelock.bin");
        FileEvidence owned = CreateRegisteredFile(workspace, path, RegisteredFixtureBytes, "R2 share-lock fixture");
        FileStream? shareLock = null;
        Exception? firstDisposeFailure = null;
        Exception? recoveryFailure = null;
        bool retrySucceeded = false;
        bool sameIdentityAndBytesAfterFailure = false;
        bool expectedIdentityRetainedAfterFailure = false;

        try
        {
            shareLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            firstDisposeFailure = Record.Exception(workspace.Dispose);
            Assert.IsType<IOException>(firstDisposeFailure);
            Assert.StartsWith("[CleanupFailed]", firstDisposeFailure!.Message, StringComparison.Ordinal);
            sameIdentityAndBytesAfterFailure = CaptureFile(path) == owned;
            expectedIdentityRetainedAfterFailure = workspace.HasRegisteredFile(path, owned.Identity);
            Assert.True(sameIdentityAndBytesAfterFailure);
            Assert.True(expectedIdentityRetainedAfterFailure);

            shareLock.Dispose();
            shareLock = null;
            workspace.Dispose();
            retrySucceeded = !Directory.Exists(root);
            Assert.True(retrySucceeded, "Releasing the actual Windows share-lock must make the retained exact-owner retry succeed.");
        }
        catch (Exception exception)
        {
            recoveryFailure = exception;
            throw;
        }
        finally
        {
            shareLock?.Dispose();
            if (Directory.Exists(root))
            {
                try { workspace.Dispose(); }
                catch (Exception exception) { recoveryFailure ??= exception; }
            }
            WriteEvidence("workspace-registered-child-sharelock", new
            {
                category = "RealFilesystem / Windows share-mode exclusion",
                root,
                owned,
                shareLockDeniedDeleteAndRename = true,
                actualOutcome = firstDisposeFailure is null ? "ReturnedSuccess" : firstDisposeFailure.GetType().Name,
                firstDisposeFailure = FailureEvidence(firstDisposeFailure),
                sameIdentityAndBytesAfterFailure,
                expectedIdentityRetainedAfterFailure,
                retrySucceeded,
                recoveryFailure = FailureEvidence(recoveryFailure),
                rootAbsentAtEvidence = !Directory.Exists(root)
            });
        }
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "SyntheticFilesystem")]
    public async Task SinglePublisher_ExistingForeignTargetCannotBeClaimed_RejectsBeforeMutation()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string target = Path.Combine(root, "foreign-overwrite-target.bin");
        FileEvidence original = CreateUnregisteredFile(target, ForeignFixtureBytes);
        bool targetMutationReached = false;
        Exception? failure = null;
        Exception? cleanupFailure = null;
        FileEvidence? afterRollback = null;
        bool ledgerClaimsForeignObjectAfterRollback = false;
        string[] residualPathsAfterRollback = [];

        Task OnPublicationTraceAsync(SplitPublicationTraceEvent trace, CancellationToken _)
        {
            if (!targetMutationReached &&
                trace.Boundary == SplitPublicationBoundary.AfterTargetMutation &&
                string.Equals(trace.FinalPath, target, StringComparison.OrdinalIgnoreCase))
            {
                targetMutationReached = true;
            }

            return Task.CompletedTask;
        }

        try
        {
            failure = await Record.ExceptionAsync(() => WindowsSplitOutputPublisher.PublishSingleAsync(
                target,
                overwriteExisting: true,
                async (stream, token) =>
                {
                    await stream.WriteAsync(new byte[] { 0x4E, 0x45, 0x57, 0x2D, 0x42, 0x59, 0x54, 0x45 }, token);
                },
                OnPublicationTraceAsync,
                CancellationToken.None));

            Assert.False(targetMutationReached, "A foreign workspace object must be rejected before the real existing-target mutation boundary.");
            IOException registrationFailure = Assert.IsType<IOException>(failure);
            Assert.Contains("Refusing to claim existing workspace target", registrationFailure.Message, StringComparison.Ordinal);
            afterRollback = CaptureFile(target);
            Assert.Equal(original, afterRollback);
            ledgerClaimsForeignObjectAfterRollback = workspace.HasRegisteredFile(target, original.Identity);
            Assert.False(ledgerClaimsForeignObjectAfterRollback,
                "A failed existing-target publication must not turn a foreign object into a workspace-owned receipt.");
            residualPathsAfterRollback = SnapshotRelativeEntries(root);
            Assert.Equal(new[] { Path.GetFileName(target)! }, residualPathsAfterRollback);
        }
        finally
        {
            if (File.Exists(target))
            {
                try
                {
                    FileEvidence actual = CaptureFile(target);
                    if (actual.Identity == original.Identity && actual != original)
                    {
                        File.WriteAllBytes(target, ForeignFixtureBytes);
                        actual = CaptureFile(target);
                    }
                    if (actual == original)
                        DeleteExactFile(original, root);
                }
                catch (Exception exception) { cleanupFailure = exception; }
            }
            if (Directory.Exists(root))
            {
                try { workspace.Dispose(); }
                catch (Exception exception) { cleanupFailure ??= exception; }
            }
            WriteEvidence("workspace-single-publisher-foreign-rollback", new
            {
                category = "SyntheticFilesystem",
                operation = "WindowsSplitOutputPublisher.PublishSingleAsync",
                root,
                originalForeignTarget = original,
                targetMutationBoundary = SplitPublicationBoundary.AfterTargetMutation.ToString(),
                targetMutationReached,
                cancellationRequested = false,
                hookThrows = false,
                actualOutcome = failure is IOException ? "ReceiptRegistrationRejectedAndRolledBack" :
                    failure is null ? "ReturnedSuccess" : "UnexpectedException",
                failure = FailureEvidence(failure),
                targetAfterRollback = afterRollback ?? TryCaptureFile(target),
                ledgerClaimsForeignObjectAfterRollback,
                residualPathsAfterRollback,
                cleanupFailure = FailureEvidence(cleanupFailure),
                rootAbsentAtEvidence = !Directory.Exists(root)
            });
        }

        Assert.Null(cleanupFailure);
        Assert.False(Directory.Exists(root));
    }

    private static FileEvidence CreateRegisteredFile(MediaWorkspace workspace, string path, byte[] bytes, string role)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(stream.SafeFileHandle);
        long length = stream.Length;
        stream.Position = 0;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream));
        Assert.True(WorkspaceOwnershipRegistry.RegisterCreatedFile(path, identity, length, sha256, role),
            "A newly created direct child of this live MediaWorkspace must be registered against its creating handle identity.");
        return new FileEvidence(Path.GetFullPath(path), length, sha256, identity);
    }

    private static FileEvidence CreateUnregisteredFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(stream.SafeFileHandle);
        long length = stream.Length;
        stream.Position = 0;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return new FileEvidence(Path.GetFullPath(path), length, sha256, identity);
    }

    private static FileEvidence CaptureFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(stream.SafeFileHandle);
        long length = stream.Length;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return new FileEvidence(Path.GetFullPath(path), length, sha256, identity);
    }

    private static FileEvidence[] SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(CaptureFile)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static FileEvidence? TryCaptureFile(string path) => File.Exists(path) ? CaptureFile(path) : null;

    private static void DeleteExactFile(FileEvidence expected, string volumeProbePath)
    {
        using SafeFileHandle handle = WindowsOwnedFilePublisher.OpenOwnedStagedFile(expected.Path, expected.Identity);
        OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
            handle,
            expected.Identity,
            volumeProbePath,
            out bool dispositionArmed);
        Assert.True(dispositionArmed, "Cleanup of a test-created file must use a real delete disposition on the exact captured object.");
        Assert.True(proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked,
            $"Exact fixture removal was not proven: {proof}.");
        Assert.False(File.Exists(expected.Path));
    }

    private static void DeleteExactEmptyDirectory(string path, WindowsFileIdentity expectedIdentity, string volumeProbePath)
    {
        using SafeFileHandle handle = WindowsOwnedFilePublisher.OpenOwnedDirectory(path);
        WindowsFileIdentity actual = WindowsFileIdentity.Capture(handle);
        Assert.Equal(expectedIdentity, actual);
        OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
            handle,
            expectedIdentity,
            volumeProbePath,
            out bool dispositionArmed);
        Assert.True(dispositionArmed, "Cleanup of a test-created empty directory must use a real exact-object delete disposition.");
        Assert.True(proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked,
            $"Exact directory fixture removal was not proven: {proof}.");
        Assert.False(Directory.Exists(path));
    }

    private static WindowsFileIdentity CaptureDirectory(string path)
    {
        using SafeFileHandle handle = CreateFileW(
            path,
            0x00000080,
            0x00000007,
            IntPtr.Zero,
            3,
            0x00200000 | 0x02000000,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Could not open directory '{path}' for its identity.");
        return WindowsFileIdentity.Capture(handle);
    }

    private static int NativeError(Exception exception) => exception.HResult & 0xFFFF;

    private static string[] SnapshotRelativeEntries(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static object FailureEvidence(Exception? exception) => exception is null
        ? new { type = (string?)null, message = (string?)null, hresult = (int?)null, innerType = (string?)null, innerMessage = (string?)null }
        : new
        {
            type = exception.GetType().FullName,
            message = exception.Message,
            hresult = exception.HResult,
            innerType = exception.InnerException?.GetType().FullName,
            innerMessage = exception.InnerException?.Message
        };

    private static void WriteEvidence(string name, object evidence)
    {
        string evidenceRoot = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE")
            ?? throw new InvalidOperationException("LPB_R2_EVIDENCE is required for workspace ownership evidence.");
        if (string.IsNullOrWhiteSpace(evidenceRoot))
            throw new InvalidOperationException("LPB_R2_EVIDENCE is required for workspace ownership evidence.");
        Directory.CreateDirectory(evidenceRoot);
        string path = Path.Combine(evidenceRoot, name + ".json");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions { WriteIndented = true });
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(json);
        output.Flush(flushToDisk: true);
    }

    private sealed record FileEvidence(string Path, long Bytes, string Sha256, WindowsFileIdentity Identity);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
