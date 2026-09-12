using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

/// <summary>
/// P3 final-commit object-identity adversarial proofs.  These tests drive the
/// deterministic seam AFTER the Managed commit verified the staging object
/// from an OPEN handle (AfterImageIdentityVerifiedBeforeRename /
/// AfterVideoIdentityVerifiedBeforeRename) and BEFORE the same-handle rename
/// executes, then take over pathnames with foreign objects.  They prove the
/// transaction publishes only the exact object it verified: source pathname
/// replacement, same-content foreign replacement and destination takeover can
/// never smuggle a foreign object into the committed output, because the
/// publish goes through the verified handle (no-overwrite kernel rename) and
/// never re-opens a pathname.
/// </summary>
public sealed class CleanerManagedCommitOwnershipTests
{
    private const string ForeignMarkerText = "LPB-MANAGED-COMMIT-FOREIGN";
    private static readonly byte[] ForeignMarker = Encoding.UTF8.GetBytes(ForeignMarkerText);

    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static async Task<(ExtractedMediaBundle Bundle, TestNativeContext Context, CleanupPlan Plan)> PrepareAsync(
        string samplePath,
        string? secondaryPath,
        MediaWorkspace workspace)
    {
        var facts = await new SourceInspector().InspectAsync(samplePath, secondaryPath);
        ExtractedMediaBundle extracted = await TestFactsExtractor.ExtractAsync(facts, samplePath, secondaryPath, workspace);
        TestNativeContext nativeContext = TestNativeContext.Create();
        CleanupPlan cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, extracted);
        return (extracted, nativeContext, cleanupPlan);
    }

    private static (string StagingDir, string StagedPath) FindStagedFile(MediaWorkspace workspace, string pattern)
    {
        string stagingDir = Assert.Single(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
        string stagedPath = Assert.Single(Directory.GetFiles(stagingDir, pattern));
        return (stagingDir, stagedPath);
    }

    /// <summary>
    /// Workspace that pins the Cleaner's publish destination for a role to a
    /// caller-chosen pathname (so the test can occupy it with a foreign object
    /// deterministically at the seam).  Only used to PRE-ALLOCATE the
    /// destination name; nothing is created before the seam.
    /// </summary>
    private sealed class FixedPublishWorkspace : IMediaWorkspace
    {
        private readonly IMediaWorkspace _inner;
        private readonly string? _fixedImagePath;
        private readonly string? _fixedVideoPath;

        public FixedPublishWorkspace(IMediaWorkspace inner, string? fixedImagePath, string? fixedVideoPath)
        {
            _inner = inner;
            _fixedImagePath = fixedImagePath;
            _fixedVideoPath = fixedVideoPath;
        }

        public string RootDirectory => _inner.RootDirectory;

        public string AllocateFilePath(string prefix, string extension)
        {
            if (prefix == "clean-img" && _fixedImagePath != null) return _fixedImagePath;
            if (prefix == "clean-vid" && _fixedVideoPath != null) return _fixedVideoPath;
            return _inner.AllocateFilePath(prefix, extension);
        }

        public Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
            => _inner.ComputeFileSha256Async(filePath, cancellationToken);

        public Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default)
            => _inner.AssertSourceUnmodifiedAsync(sourcePath, expectedSha256, cancellationToken);

        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    // ---------------------------------------------------------------------
    // 1. PrimaryImage source pathname takeover between verify and rename
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommitRace_PrimaryImage_SourcePathnameTakeover_PublishesVerifiedObjectAndLeavesForeign()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;
            string? foreignPath = null;
            WindowsFileIdentity? ownedBeforeTakeover = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedImg = FindStagedFile(workspace, "stage-img*").StagedPath;
                    // Identity of the object the Cleaner verified from its open handle.
                    ownedBeforeTakeover = WindowsFileIdentity.Capture(stagedImg);

                    // Foreign actor: rename A to a side path (the commit handle
                    // stays valid and still refers to A), then put a foreign B
                    // at the original staged pathname.
                    string side = stagedImg + ".lpb-race-side";
                    File.Move(stagedImg, side);
                    foreignPath = stagedImg;
                    File.WriteAllBytes(foreignPath, ForeignMarker);
                }
                return Task.CompletedTask;
            };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(ownedBeforeTakeover);

            // The published output is the VERIFIED object A, not the foreign B.
            Assert.NotNull(result.CleanedImage);
            Assert.True(File.Exists(result.CleanedImage!.Path), "cleaned image must exist");
            WindowsFileIdentity finalIdentity = WindowsFileIdentity.Capture(result.CleanedImage.Path);
            Assert.Equal(ownedBeforeTakeover, finalIdentity);
            Assert.Equal(ownedBeforeTakeover, result.CleanedImage.FileIdentity);
            Assert.Equal(ComputeSha256(result.CleanedImage.Path), result.CleanedImage.Sha256);

            // Foreign B survives byte-for-byte at the original staged pathname.
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath), "foreign B must survive");
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignPath));
            Assert.NotEqual(ownedBeforeTakeover, WindowsFileIdentity.Capture(foreignPath));

            // The race side path is empty: the verified object was moved by
            // the handle from the side pathname to the destination.
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));

            // Source untouched.
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 2. Same-content foreign object: SHA is NOT ownership
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommitRace_PrimaryImage_SameContentForeignObject_ShaIsNotOwnership()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;
            string? foreignPath = null;
            byte[]? ownedBytes = null;
            WindowsFileIdentity? ownedBeforeTakeover = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedImg = FindStagedFile(workspace, "stage-img*").StagedPath;
                    ownedBeforeTakeover = WindowsFileIdentity.Capture(stagedImg);
                    // The commit handle is open with DELETE access (share
                    // READ|DELETE, no WRITE share); a pathname reader MUST
                    // declare FILE_SHARE_DELETE or the open fails with a
                    // sharing violation.  File.ReadAllBytes (share=Read only)
                    // is therefore unusable here.
                    using (var stagedStream = new FileStream(
                        stagedImg, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Write | FileShare.Delete))
                    {
                        ownedBytes = new byte[stagedStream.Length];
                        stagedStream.ReadExactly(ownedBytes, 0, ownedBytes.Length);
                    }

                    // Foreign B with byte-for-byte identical content: SHA(B) ==
                    // SHA(A) but FileId(B) != FileId(A).
                    string side = stagedImg + ".lpb-race-side";
                    File.Move(stagedImg, side);
                    foreignPath = stagedImg;
                    File.WriteAllBytes(foreignPath, ownedBytes);
                }
                return Task.CompletedTask;
            };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(ownedBeforeTakeover);
            Assert.NotNull(ownedBytes);

            // Ownership is decided by filesystem object identity, never by SHA.
            Assert.NotNull(result.CleanedImage);
            WindowsFileIdentity finalIdentity = WindowsFileIdentity.Capture(result.CleanedImage!.Path);
            Assert.Equal(ownedBeforeTakeover, finalIdentity);
            Assert.NotEqual(ownedBeforeTakeover, WindowsFileIdentity.Capture(foreignPath!));

            // Content evidence still holds: same SHA on both objects.
            Assert.Equal(ComputeSha256(foreignPath!), ComputeSha256(result.CleanedImage.Path));
            Assert.Equal(ComputeSha256(result.CleanedImage.Path), result.CleanedImage.Sha256);

            // Foreign B survives byte-for-byte at the staged pathname.
            Assert.True(File.Exists(foreignPath));
            Assert.Equal(ownedBytes, File.ReadAllBytes(foreignPath));

            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 3. Foreign destination takeover: no-overwrite must fail closed
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommitRace_PrimaryImage_ForeignDestinationTakeover_FailsClosedAndLeavesAllForeign()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        string foreignDestination = Path.Combine(workspace.RootDirectory, "foreign-img-dest.jpg");
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;
            string? foreignPath = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedImg = FindStagedFile(workspace, "stage-img*").StagedPath;
                    // Foreign actor: rename A to a side path, put foreign B at
                    // the staged pathname, and occupy the destination with C.
                    string side = stagedImg + ".lpb-race-side";
                    File.Move(stagedImg, side);
                    foreignPath = stagedImg;
                    File.WriteAllBytes(foreignPath, ForeignMarker);
                    File.WriteAllBytes(foreignDestination, ForeignMarker);
                }
                return Task.CompletedTask;
            };

            var fixedWorkspace = new FixedPublishWorkspace(workspace, foreignDestination, null);
            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, fixedWorkspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);

            // Foreign C at the destination survives byte-for-byte (no overwrite).
            Assert.True(File.Exists(foreignDestination));
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignDestination));

            // Foreign B at the staged pathname survives byte-for-byte.
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath));
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignPath));

            // The owned object was removed through the verified handle (the
            // race side path is empty) — never by bare pathname deletion.
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));

            // Source untouched.
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 4. MotionVideo source pathname takeover between verify and rename
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommitRace_MotionVideo_SourcePathnameTakeover_PublishesVerifiedVideoAndLeavesForeign()
    {
        string samplePath = ResolveSample("苹果双文件.HEIC");
        string secondaryPath = ResolveSample("苹果双文件.MOV");
        string shaBefore = ComputeSha256(samplePath);
        string shaVideoBefore = ComputeSha256(secondaryPath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, secondaryPath, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;
            string? foreignPath = null;
            WindowsFileIdentity? ownedBeforeTakeover = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterVideoIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedVid = FindStagedFile(workspace, "stage-vid*").StagedPath;
                    ownedBeforeTakeover = WindowsFileIdentity.Capture(stagedVid);

                    string side = stagedVid + ".lpb-race-side";
                    File.Move(stagedVid, side);
                    foreignPath = stagedVid;
                    File.WriteAllBytes(foreignPath, ForeignMarker);
                }
                return Task.CompletedTask;
            };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterVideoIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(ownedBeforeTakeover);

            // Image path also published normally.
            Assert.NotNull(result.CleanedImage);
            Assert.True(File.Exists(result.CleanedImage!.Path));

            // The published video is the VERIFIED object V, not the foreign B.
            Assert.NotNull(result.CleanedVideo);
            Assert.True(File.Exists(result.CleanedVideo!.Path), "cleaned video must exist");
            WindowsFileIdentity finalVideoIdentity = WindowsFileIdentity.Capture(result.CleanedVideo.Path);
            Assert.Equal(ownedBeforeTakeover, finalVideoIdentity);
            Assert.Equal(ownedBeforeTakeover, result.CleanedVideo.FileIdentity);
            Assert.Equal(ComputeSha256(result.CleanedVideo.Path), result.CleanedVideo.Sha256);

            // Foreign B survives byte-for-byte at the original staged pathname.
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath), "foreign B must survive");
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignPath));
            Assert.NotEqual(ownedBeforeTakeover, WindowsFileIdentity.Capture(foreignPath));

            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
            Assert.Equal(shaVideoBefore, ComputeSha256(secondaryPath));
        }
    }

    // ---------------------------------------------------------------------
    // 5. Partial commit: image publishes, video destination is taken over
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommitRace_PartialPublish_ImageSucceedsVideoForeignDestination_RollsBackOwnedArtifacts()
    {
        string samplePath = ResolveSample("苹果双文件.HEIC");
        string secondaryPath = ResolveSample("苹果双文件.MOV");
        string shaBefore = ComputeSha256(samplePath);
        string shaVideoBefore = ComputeSha256(secondaryPath);

        using var workspace = new MediaWorkspace();
        string foreignVideoDestination = Path.Combine(workspace.RootDirectory, "foreign-vid-dest.mov");
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, secondaryPath, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;
            string? foreignPath = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterVideoIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedVid = FindStagedFile(workspace, "stage-vid*").StagedPath;
                    // Foreign actor: rename V to a side path, put foreign B at
                    // the staged pathname, and occupy the video destination
                    // with C.  The image has already published successfully.
                    string side = stagedVid + ".lpb-race-side";
                    File.Move(stagedVid, side);
                    foreignPath = stagedVid;
                    File.WriteAllBytes(foreignPath, ForeignMarker);
                    File.WriteAllBytes(foreignVideoDestination, ForeignMarker);
                }
                return Task.CompletedTask;
            };

            var fixedWorkspace = new FixedPublishWorkspace(workspace, null, foreignVideoDestination);
            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, fixedWorkspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterVideoIdentityVerifiedBeforeRename", observedStage);

            // Foreign C at the video destination survives byte-for-byte.
            Assert.True(File.Exists(foreignVideoDestination));
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignVideoDestination));

            // Foreign B at the staged video pathname survives byte-for-byte.
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath));
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignPath));

            // The image publication was rolled back exactly: no owned clean-img
            // output remains, and no owned clean-vid output was ever created
            // (the only file at the video destination is foreign C).
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-vid*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));

            // Sources untouched.
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
            Assert.Equal(shaVideoBefore, ComputeSha256(secondaryPath));
        }
    }

    // ---------------------------------------------------------------------
    // 6. Cancellation between verify and rename fails closed
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_CancellationBetweenVerifyAndRename_FailsClosedAndCleansOwnedObject()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            using var cts = new CancellationTokenSource();
            int triggerCount = 0;
            string? observedStage = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }
                return Task.CompletedTask;
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace, cts.Token);
            });

            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);

            // Cancellation between verify and rename fails closed: no orphaned
            // staging directories, no published clean outputs.
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 7. Post-rename failure: the journal pre-registered the published record
    //    BEFORE the rename, so rollback exact-deletes the owned object at its
    //    real (published) location instead of leaking a transaction-owned
    //    clean-img output.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_PostRenameFailure_JournalFallbackExactDeletesPublishedObject()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;

            // The image rename has ALREADY succeeded (A is at clean-img) when
            // this seam fires.  Throw after publication: rollback must find the
            // owned object through the journal's published record — the
            // transaction-owned output must never leak.
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    observedStage = detail;
                    throw new IOException("Simulated post-rename failure.");
                }
                return Task.CompletedTask;
            };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.PublishFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("ImagePublished", observedStage);

            // The published object was exact-deleted through the journal's
            // published record (no foreign object was involved).
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 8. Cancellation AFTER the rename: the same journal fallback cleans the
    //    published object; cancellation never leaves a transaction-owned
    //    clean-img behind.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_PostRenameCancellation_JournalFallbackExactDeletesPublishedObject()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            using var cts = new CancellationTokenSource();
            int triggerCount = 0;
            string? observedStage = null;

            // Cancel AFTER the image rename succeeded (A is at clean-img).
            // The journal pre-registered the published record BEFORE the
            // rename, so rollback deletes A at its real location.
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    observedStage = detail;
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                }
                return Task.CompletedTask;
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace, cts.Token);
            });

            Assert.Equal(1, triggerCount);
            Assert.Equal("ImagePublished", observedStage);

            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 9. Rollback exact-delete open failure: a sharing violation must surface
    //    as RollbackFailed — never a silent "already gone".
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_RollbackOpenSharingViolation_SurfacesAsRollbackFailed()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;
            FileStream? lockStream = null;
            string? lockedPath = null;

            // At ImagePublished the image rename has already succeeded.  Hold
            // the published file open with a share mode that denies DELETE:
            // the rollback's exact-delete open then fails with
            // ERROR_SHARING_VIOLATION, which must surface as RollbackFailed —
            // NOT be silently treated as "already gone".
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    observedStage = detail;
                    lockedPath = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                    lockStream = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    throw new IOException("Simulated post-rename failure.");
                }
                return Task.CompletedTask;
            };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("ImagePublished", observedStage);

            // Fail closed: the rollback could not LOOK at the object, so the
            // transaction-owned file is left in place and the transaction
            // reports RollbackFailed instead of pretending it rolled back.
            Assert.NotNull(lockedPath);
            Assert.True(File.Exists(lockedPath));
            Assert.NotNull(lockStream);
            Assert.True(lockStream.Length > 0);
            lockStream.Dispose();
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 10. Rollback exact-delete open failure: ERROR_FILE_NOT_FOUND IS
    //     "already gone" and must NOT be reported as RollbackFailed.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_RollbackOpenFileNotFound_TreatedAsGone_NotRollbackFailed()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? observedStage = null;

            // The image rename has already succeeded; a foreign actor removes
            // the transaction-owned object before rollback runs.  Rollback's
            // exact-delete open then fails with ERROR_FILE_NOT_FOUND, which IS
            // "already gone" — the rollback must NOT be reported as failed.
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    observedStage = detail;
                    string cleanImg = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                    File.Delete(cleanImg);
                    throw new IOException("Simulated post-rename failure.");
                }
                return Task.CompletedTask;
            };

            var result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.PublishFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("ImagePublished", observedStage);

            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }
}
