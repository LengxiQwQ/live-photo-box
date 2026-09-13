using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Interop;
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
    // P3 immediate-ownership boundary.  A path rename before handle
    // acquisition is deliberately NOT proof that the object is gone: without
    // an exact retained handle, rollback must fail closed.  Once acquisition
    // succeeds, the same rename is cleaned through that exact handle.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImmediateImageHandle_BeforeAcquisitionRenameAway_FailsRollbackClosed()
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
            string? movedOwnedPath = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "BeforeImageHandleAcquisition")
                {
                    triggerCount++;
                    string stagedImage = FindStagedFile(workspace, "stage-img*").StagedPath;
                    movedOwnedPath = Path.Combine(workspace.RootDirectory, "owned-image-before-acquisition.jpg");
                    File.Move(stagedImage, movedOwnedPath);
                    throw new IOException("Simulated image rename before retained-handle acquisition.");
                }
                return Task.CompletedTask;
            };

            try
            {
                ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.NotNull(movedOwnedPath);
                Assert.True(File.Exists(movedOwnedPath), "A missing staging pathname without an exact handle must not be treated as object-gone.");
                Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            }
            finally
            {
                if (movedOwnedPath != null && File.Exists(movedOwnedPath)) File.Delete(movedOwnedPath);
            }

            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImmediateImageHandle_AfterAcquisitionRenameAway_ExactHandleRollsBack()
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
            string? movedOwnedPath = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "ImageHandleAcquired")
                {
                    triggerCount++;
                    string stagedImage = FindStagedFile(workspace, "stage-img*").StagedPath;
                    movedOwnedPath = Path.Combine(workspace.RootDirectory, "owned-image-after-acquisition.jpg");
                    File.Move(stagedImage, movedOwnedPath);
                    throw new IOException("Simulated image rename after retained-handle acquisition.");
                }
                return Task.CompletedTask;
            };

            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.NotNull(movedOwnedPath);
            Assert.False(File.Exists(movedOwnedPath), "The retained exact handle must delete the owned object after a pathname rename.");
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImmediateVideoHandle_BeforeAcquisitionRenameAway_FailsRollbackClosed()
    {
        string samplePath = ResolveSample("苹果双文件.HEIC");
        string secondaryPath = ResolveSample("苹果双文件.MOV");
        string imageShaBefore = ComputeSha256(samplePath);
        string videoShaBefore = ComputeSha256(secondaryPath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, secondaryPath, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? movedOwnedPath = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "BeforeVideoHandleAcquisition")
                {
                    triggerCount++;
                    string stagedVideo = FindStagedFile(workspace, "stage-vid*").StagedPath;
                    movedOwnedPath = Path.Combine(workspace.RootDirectory, "owned-video-before-acquisition.mov");
                    File.Move(stagedVideo, movedOwnedPath);
                    throw new IOException("Simulated video rename before retained-handle acquisition.");
                }
                return Task.CompletedTask;
            };

            try
            {
                ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.NotNull(movedOwnedPath);
                Assert.True(File.Exists(movedOwnedPath), "Path absence cannot prove that a pre-acquisition video object is gone.");
                Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-vid*", SearchOption.AllDirectories));
            }
            finally
            {
                if (movedOwnedPath != null && File.Exists(movedOwnedPath)) File.Delete(movedOwnedPath);
            }

            Assert.Equal(imageShaBefore, ComputeSha256(samplePath));
            Assert.Equal(videoShaBefore, ComputeSha256(secondaryPath));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImmediateVideoHandle_AfterAcquisitionRenameAway_ExactHandleRollsBack()
    {
        string samplePath = ResolveSample("苹果双文件.HEIC");
        string secondaryPath = ResolveSample("苹果双文件.MOV");
        string imageShaBefore = ComputeSha256(samplePath);
        string videoShaBefore = ComputeSha256(secondaryPath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, secondaryPath, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? movedOwnedPath = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "VideoHandleAcquired")
                {
                    triggerCount++;
                    string stagedVideo = FindStagedFile(workspace, "stage-vid*").StagedPath;
                    movedOwnedPath = Path.Combine(workspace.RootDirectory, "owned-video-after-acquisition.mov");
                    File.Move(stagedVideo, movedOwnedPath);
                    throw new IOException("Simulated video rename after retained-handle acquisition.");
                }
                return Task.CompletedTask;
            };

            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.NotNull(movedOwnedPath);
            Assert.False(File.Exists(movedOwnedPath), "The retained exact video handle must delete its renamed owned object.");
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-vid*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Equal(imageShaBefore, ComputeSha256(samplePath));
            Assert.Equal(videoShaBefore, ComputeSha256(secondaryPath));
        }
    }

    // ---------------------------------------------------------------------
    // 0b. P3 commit seam: the retained exact handle exists but imgPublished is
    //     still null when the failure fires (AfterImageIdentityVerified-
    //     BeforeRename, i.e. BEFORE RegisterPublishedRecord / PublishOwned-
    //     Handle).  A rename-away + throw at this seam must still clean A
    //     through the retained handle: the transaction must never skip handle
    //     cleanup because a DTO field is null, and must never report a false
    //     RolledBack while A lives under another pathname.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_RenameAwayAtIdentityVerifiedSeam_ExactHandleRollsBackEvenWhenNotYetPublished()
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
            string? movedAwayPath = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;
                    string stagedImage = FindStagedFile(workspace, "stage-img*").StagedPath;
                    movedAwayPath = stagedImage + ".lpb-owned-moved-away";
                    File.Move(stagedImage, movedAwayPath);
                    throw new IOException("Simulated failure after identity verification while the staged object is renamed away.");
                }
                return Task.CompletedTask;
            };

            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle,
                CleanupPlan = cleanupPlan
            }, workspace);

            Assert.False(result.Success);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(movedAwayPath);
            Assert.False(File.Exists(movedAwayPath), "The retained exact handle must delete A even though imgPublished was not yet set.");
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
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
            int beforeImageHandleAcquisitionCount = 0;
            int imageHandleAcquiredCount = 0;
            string? observedStage = null;
            string? foreignPath = null;
            string? side = null;
            WindowsFileIdentity? ownedBeforeTakeover = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "BeforeImageHandleAcquisition")
                {
                    beforeImageHandleAcquisitionCount++;
                }
                else if (stage == CleanerFailureStage.Staging && detail == "ImageHandleAcquired")
                {
                    imageHandleAcquiredCount++;
                }

                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedImg = FindStagedFile(workspace, "stage-img*").StagedPath;
                    // Identity of the object the Cleaner verified from its open handle.
                    ownedBeforeTakeover = WindowsFileIdentity.Capture(stagedImg);

                    // Foreign actor: rename A OUT of the staging directory (the
                    // commit handle stays valid and still refers to A), then put
                    // a foreign B at the original staged pathname.  Foreign
                    // objects are protected by file-level identity, never by
                    // denying writes into the directory.
                    side = Path.Combine(workspace.RootDirectory, "lpb-race-side-" + Guid.NewGuid().ToString("N") + ".jpg");
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

            // The commit DID publish the verified object A through its handle,
            // but the foreign B left inside the staging directory makes the
            // pre-commit directory cleanup unprovable (B5-A): the transaction
            // must NOT report Committed while its owned staging directory may
            // still exist.  Rollback then exact-deletes A through the retained
            // handle; foreign B survives byte-for-byte; the non-empty staging
            // directory is left in place and the transaction truthfully
            // reports RollbackFailed.
            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(ownedBeforeTakeover);

            // A was exact-deleted through the retained handle: the race side
            // pathname is empty and no clean output remains.
            Assert.NotNull(side);
            Assert.False(File.Exists(side), "the verified object must be exact-deleted through its retained handle");
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));

            // Foreign B survives byte-for-byte at the original staged pathname
            // and keeps the staging directory non-empty (never recursively
            // deleted).
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath), "foreign B must survive");
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignPath));
            Assert.NotEqual(ownedBeforeTakeover, WindowsFileIdentity.Capture(foreignPath));
            Assert.NotEmpty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));

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
            string? side = null;
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
                    // SHA(A) but FileId(B) != FileId(A).  Foreign objects are
                    // protected by file-level identity, never by denying writes
                    // into the directory.
                    side = Path.Combine(workspace.RootDirectory, "lpb-race-side-" + Guid.NewGuid().ToString("N") + ".jpg");
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

            // The same-content foreign B left in the staging directory makes
            // the pre-commit directory cleanup unprovable (B5-A): no Committed.
            // Rollback exact-deletes A through the retained handle; foreign B
            // survives byte-for-byte; the transaction truthfully reports
            // RollbackFailed.  Ownership is decided by filesystem object
            // identity, never by SHA.
            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(ownedBeforeTakeover);
            Assert.NotNull(ownedBytes);

            // A was exact-deleted through the retained handle.
            Assert.True(side != null, $"side was null. result.ErrorMessage={result.ErrorMessage}");
            Assert.False(File.Exists(side), "the verified object must be exact-deleted through its retained handle");
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));

            // Foreign B (byte-identical content, different FileId) survives at
            // the staged pathname and keeps the staging directory non-empty.
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath), "foreign B must survive");
            Assert.Equal(ownedBytes, File.ReadAllBytes(foreignPath));
            Assert.NotEqual(ownedBeforeTakeover, WindowsFileIdentity.Capture(foreignPath));
            Assert.NotEmpty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));

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
                    // Foreign actor: rename A OUT of the staging directory, put
                    // foreign B at the staged pathname, and occupy the
                    // destination with C.  Foreign objects are protected by
                    // file-level identity, never by denying writes.
                    string side = Path.Combine(workspace.RootDirectory, "lpb-race-side-" + Guid.NewGuid().ToString("N") + ".jpg");
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
            int beforeVideoHandleAcquisitionCount = 0;
            int videoHandleAcquiredCount = 0;
            string? observedStage = null;
            string? foreignPath = null;
            WindowsFileIdentity? ownedBeforeTakeover = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "BeforeVideoHandleAcquisition")
                {
                    beforeVideoHandleAcquisitionCount++;
                }
                else if (stage == CleanerFailureStage.Staging && detail == "VideoHandleAcquired")
                {
                    videoHandleAcquiredCount++;
                }

                if (stage == CleanerFailureStage.Commit && detail == "AfterVideoIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;

                    string stagedVid = FindStagedFile(workspace, "stage-vid*").StagedPath;
                    ownedBeforeTakeover = WindowsFileIdentity.Capture(stagedVid);

                    // Rename V OUT of the staging directory and put a foreign B
                    // at the staged pathname; foreign objects are protected by
                    // file-level identity, never by denying writes.
                    string side = Path.Combine(workspace.RootDirectory, "lpb-race-side-" + Guid.NewGuid().ToString("N") + ".mov");
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

            // Both artifacts were published through their handles, but the
            // foreign B left in the staging directory makes the pre-commit
            // directory cleanup unprovable (B5-A): no Committed.  Rollback
            // exact-deletes BOTH owned objects through their retained handles;
            // foreign B survives; the non-empty staging directory stays and
            // the transaction truthfully reports RollbackFailed.
            Assert.False(result.Success);
            Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
            Assert.Equal(1, triggerCount);
            Assert.Equal(1, beforeVideoHandleAcquisitionCount);
            Assert.Equal(1, videoHandleAcquiredCount);
            Assert.Equal("AfterVideoIdentityVerifiedBeforeRename", observedStage);
            Assert.NotNull(ownedBeforeTakeover);

            // Both owned objects were exact-deleted through their handles: no
            // clean image or video output survives, and the race side path is
            // empty.
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-vid*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-race-side*", SearchOption.AllDirectories));

            // Foreign B survives byte-for-byte at the original staged pathname
            // and keeps the staging directory non-empty (never recursively
            // deleted).
            Assert.NotNull(foreignPath);
            Assert.True(File.Exists(foreignPath), "foreign B must survive");
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignPath));
            Assert.NotEqual(ownedBeforeTakeover, WindowsFileIdentity.Capture(foreignPath));
            Assert.NotEmpty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));

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
                    // Foreign actor: rename V OUT of the staging directory, put
                    // foreign B at the staged pathname, and occupy the video
                    // destination with C.  The image has already published
                    // successfully.  Foreign objects are protected by
                    // file-level identity, never by denying writes.
                    string side = Path.Combine(workspace.RootDirectory, "lpb-race-side-" + Guid.NewGuid().ToString("N") + ".mov");
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
    // 9. Rollback exact-delete failure: ERROR_ACCESS_DENIED (READONLY object
    //    made undeletable by a foreign actor) must surface as RollbackFailed —
    //    never a silent "already gone" like a genuine NOT_FOUND.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_RollbackExactDeleteAccessDenied_SurfacesAsRollbackFailed()
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
            string? stagedPath = null;

            // A foreign actor makes the transaction-owned staged object
            // UNDELETABLE (READONLY) at the seam, then fails the transaction
            // BEFORE the rename.  Rollback's exact-delete then opens the
            // object (DELETE-open succeeds on READONLY files) but its
            // FileDispositionInfo fails with ERROR_ACCESS_DENIED — which is
            // NOT "already gone".  The transaction must surface RollbackFailed
            // and leave the owned object in place instead of falsely reporting
            // RolledBack.
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "AfterImageIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;
                    stagedPath = FindStagedFile(workspace, "stage-img*").StagedPath;
                    File.SetAttributes(stagedPath, FileAttributes.ReadOnly);
                    throw new IOException("Simulated pre-rename failure while the staged object is made undeletable.");
                }
                return Task.CompletedTask;
            };

            try
            {
                var result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.Equal("AfterImageIdentityVerifiedBeforeRename", observedStage);

                // Fail closed: the rollback could not DELETE the staged object
                // (ACCESS_DENIED, not NOT_FOUND), so the transaction reports
                // RollbackFailed and the transaction-owned object is left
                // where it was — never deleted through an unverified path.
                Assert.NotNull(stagedPath);
                Assert.True(File.Exists(stagedPath));
            }
            finally
            {
                // Clean up the read-only leftover so MediaWorkspace can
                // dispose its scratch directory.
                if (stagedPath != null && File.Exists(stagedPath))
                {
                    File.SetAttributes(stagedPath, FileAttributes.Normal);
                    File.Delete(stagedPath);
                }
            }

            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 10. A retained handle can prove that a foreign actor unlinked the exact
    //     owned object: LinkCount == 0 is valid cleanup evidence.  This is
    //     deliberately different from a pathname-only NOT_FOUND, which P3
    //     treats as CleanupUnproven and therefore RollbackFailed.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_RollbackRetainedHandleZeroLink_ProvesObjectGone()
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

            // The image rename has already succeeded and the journal still
            // owns its exact handle. A foreign actor unlinks A before rollback
            // runs; DeleteOwnedObject cannot disposition an unlinked object,
            // but LinkCount == 0 from that exact handle proves A is gone.
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

    // ---------------------------------------------------------------------
    // 11. Post-image-publish rename-away: while the bundle transaction is
    //     still active the published object's exact handle is retained, so a
    //     foreign actor renaming A away from clean-img (not deleting it)
    //     cannot make rollback report a false RolledBack — A is deleted
    //     through the handle no matter what pathname it now occupies.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_PostImagePublish_RenameAwayThenFailure_ExactHandleRollbackCleansOwnedObject()
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
            string? movedAwayPath = null;

            // At ImagePublished the image rename has already succeeded and the
            // commit handle to A is STILL open (the bundle transaction has not
            // committed).  A foreign actor renames A away from clean-img — not
            // a delete: A still exists under a different pathname.  The
            // subsequent failure must still clean A through the retained
            // handle; rollback must NOT report success while A survives.
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    observedStage = detail;
                    string cleanImg = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                    movedAwayPath = cleanImg + ".lpb-owned-moved-away";
                    File.Move(cleanImg, movedAwayPath);
                    throw new IOException("Simulated post-image-publish failure.");
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

            // The transaction-owned object was deleted through its retained
            // exact handle even though it had been renamed away: no clean-img
            // output and no moved-away owned artifact survive.
            Assert.NotNull(movedAwayPath);
            Assert.False(File.Exists(movedAwayPath));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-owned-moved-away*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // 12. Multi-artifact: image published and renamed away, then the video
    //     publish fails -> the image is rolled back through its retained
    //     exact handle; no owned artifact leaks and the foreign video
    //     destination survives byte-for-byte.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_ImagePublishedRenamedAway_VideoPublishFails_ImageExactHandleRollback()
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
            string? movedAwayPath = null;

            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    // Image A is already published; rename it away BEFORE the
                    // video publish fails.
                    string cleanImg = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                    movedAwayPath = cleanImg + ".lpb-owned-moved-away";
                    File.Move(cleanImg, movedAwayPath);
                }
                else if (stage == CleanerFailureStage.Commit && detail == "AfterVideoIdentityVerifiedBeforeRename")
                {
                    triggerCount++;
                    observedStage = detail;
                    // Occupy the video destination so the video publish fails.
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
            Assert.Equal(CleanerFailureCategory.PublishFailed, result.FailureCategory);
            Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
            Assert.Equal(2, triggerCount);
            Assert.Equal("AfterVideoIdentityVerifiedBeforeRename", observedStage);

            // The published image was deleted through its retained handle even
            // though it had been renamed away mid-transaction.
            Assert.NotNull(movedAwayPath);
            Assert.False(File.Exists(movedAwayPath));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "*lpb-owned-moved-away*", SearchOption.AllDirectories));

            // The foreign video destination survives byte-for-byte; no owned
            // clean-vid output was ever created.
            Assert.True(File.Exists(foreignVideoDestination));
            Assert.Equal(ForeignMarker, File.ReadAllBytes(foreignVideoDestination));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-vid*", SearchOption.AllDirectories));

            Assert.Empty(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
            Assert.Equal(shaBefore, ComputeSha256(samplePath));
            Assert.Equal(shaVideoBefore, ComputeSha256(secondaryPath));
        }
    }

    // ---------------------------------------------------------------------
    // 13. Post-image-publish rename-away where the retained exact-handle
    //     cleanup FAILS: the destination pathname is gone (NOT_FOUND) and the
    //     staging record was already dropped, so the pathname fallback sees
    //     NOTHING to delete — yet the pre-rollback failure recorded by
    //     AddRollbackFailure must survive Rollback()'s exception-list reset
    //     and force RollbackFailed.  A false RolledBack would leave the
    //     transaction-owned object alive under the side pathname.
    // ---------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ManagedCommit_PostImagePublish_RenameAway_RetainedHandleDeleteFails_ForcesRollbackFailed()
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
            string? movedAwayPath = null;

            // A is published (clean-img) and its exact handle is RETAINED while
            // the bundle transaction is still active.  The foreign actor renames
            // A away — so the destination pathname resolves to nothing and the
            // staging record was already dropped — AND makes A UNDELETABLE
            // (READONLY), so the retained-handle exact cleanup provably fails
            // (FileDispositionInfo -> ERROR_ACCESS_DENIED, file retained).
            // The pre-rollback failure evidence must survive Rollback()'s
            // internal exception reset: rollback must report RollbackFailed,
            // never a false RolledBack, because A is still alive.
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    observedStage = detail;
                    string cleanImg = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                    movedAwayPath = cleanImg + ".lpb-owned-moved-away";
                    File.Move(cleanImg, movedAwayPath);
                    File.SetAttributes(movedAwayPath, FileAttributes.ReadOnly);
                    throw new IOException("Simulated post-image-publish failure with the owned object renamed away and made undeletable.");
                }
                return Task.CompletedTask;
            };

            try
            {
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

                // The pre-rollback failure evidence survived Rollback()'s
                // exception-list reset: the reported rollback failure explicitly
                // names the retained-handle cleanup — the ONLY reason this
                // rollback is (truthfully) failed, because the pathname fallback
                // itself saw nothing to delete at clean-img.
                Assert.NotNull(result.ErrorMessage);
                Assert.Contains("through its retained handle", result.ErrorMessage);

                // The transaction-owned object is still alive under the side
                // pathname (the exact-handle cleanup could not delete it), which
                // is exactly why RollbackFailed — not RolledBack — is the
                // truthful verdict.
                Assert.NotNull(movedAwayPath);
                Assert.True(File.Exists(movedAwayPath));
                Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*.jpg", SearchOption.AllDirectories));
            }
            finally
            {
                // Restore and remove the owned leftover so MediaWorkspace can
                // dispose its scratch directory.
                if (movedAwayPath != null && File.Exists(movedAwayPath))
                {
                    File.SetAttributes(movedAwayPath, FileAttributes.Normal);
                    File.Delete(movedAwayPath);
                }
            }

            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    // ---------------------------------------------------------------------
    // P3 object-lifetime final-closeout adversarial proofs (probe-driven):
    //
    //  * Hard-link alias:  while the retained staged-file handle is open,
    //    CreateHardLinkW CAN succeed (probe H1).  A disposition success then
    //    only removes one name — the object survives under the alias (probe
    //    H4/E1/F4) — so "FileDispositionInfo == TRUE" is NOT object-gone
    //    proof.  Rollback must verify the exact FileId by
    //    FILE_OPEN_BY_FILE_ID after the final lease closes; if the object is
    //    still resolvable, cleanup is unproven and the transaction must
    //    report RollbackFailed (never a false RolledBack).
    //
    //  * Staging-directory rename-away:  the directory is a transaction-owned
    //    object.  While the staged FILE handles are open, even a raw
    //    MoveFileW of the whole directory fails with ACCESS_DENIED (probe
    //    D11/D12) — the lease is real.  The only observable takeover window
    //    is BEFORE the file handles are acquired (probe D2: empty/unreferenced
    //    directory renames fine).  In that window rollback holds no exact
    //    authority: PathMissing is NOT DirectoryGone and must fail closed,
    //    and a foreign directory occupying the old pathname is never deleted.
    // ---------------------------------------------------------------------

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ObjectLifetime_HardLinkAliasCreatedWhileHandleOpen_DispositionNotGoneProof_RollbackFailed()
    {
        // A hard link created while the P3 retained handle is open keeps the
        // exact filesystem object alive under a foreign name after the staged
        // name is dispositioned.  Only the kernel-backed by-FileId check can
        // detect this; rollback must fail closed and never delete the alias.
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? aliasPath = null;
            string? cleanImgPath = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                // The staging-directory lease (no FILE_SHARE_WRITE) denies hard
                // links to staged files (probe H4b: err=32), so the observable
                // alias window is AFTER the object is published to the final
                // destination.  The retained publish handle is still open and
                // the bundle transaction is still active, so an alias created
                // here is exactly the mid-transaction alias the by-FileId proof
                // must detect.
                if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
                {
                    triggerCount++;
                    cleanImgPath = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                    aliasPath = Path.Combine(workspace.RootDirectory, "owned-object-hardlink-alias.jpg");
                    if (!NativeIo.CreateHardLinkW(aliasPath, cleanImgPath, IntPtr.Zero))
                    {
                        throw new IOException($"CreateHardLinkW failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
                    }
                    throw new IOException("Simulated failure after a foreign hard-link alias was created on the owned object.");
                }
                return Task.CompletedTask;
            };

            try
            {
                ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                // Disposition was not attempted (the alias raised LinkCount), and
                // the by-FileId re-check proves the exact object is still alive
                // under the foreign alias: cleanup is unproven => RollbackFailed.
                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerFailureStage.Rollback, result.FailureStage);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.Contains("hard-link alias", result.ErrorMessage);
                Assert.NotNull(aliasPath);
                Assert.True(File.Exists(aliasPath), "foreign hard-link alias must never be deleted");
                // The owned object is STILL ALIVE under clean-img (and its
                // alias): the by-FileId proof detects the alias and rollback
                // truthfully reports RollbackFailed — never a false RolledBack.
                Assert.NotNull(cleanImgPath);
                Assert.True(File.Exists(cleanImgPath), "the owned object must still be alive under clean-img");
            }
            finally
            {
                if (aliasPath != null && File.Exists(aliasPath))
                {
                    File.SetAttributes(aliasPath, FileAttributes.Normal);
                    File.Delete(aliasPath);
                }
            }

            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ObjectLifetime_StagingDirectoryRenamedAwayBeforeFileHandles_FailsClosedNoFalseRolledBack()
    {
        // The ONLY window in which an external actor can move the staging
        // directory is before the staged-file handles are acquired (probe
        // D11/D12: open file handles deny the directory rename).  In that
        // window the transaction holds no exact authority over the files or
        // the directory: PathMissing is NOT ObjectGone and must fail closed.
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? movedDir = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "BeforeImageHandleAcquisition")
                {
                    triggerCount++;
                    string stagingDir = FindStagedFile(workspace, "stage-img*").StagingDir;
                    movedDir = stagingDir + ".owned-moved-away";
                    if (!NativeIo.MoveFileW(stagingDir, movedDir))
                    {
                        throw new IOException($"MoveFileW(staging dir) failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
                    }
                    throw new IOException("Simulated failure after the staging directory was renamed away.");
                }
                return Task.CompletedTask;
            };

            try
            {
                ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.NotNull(movedDir);
                Assert.True(Directory.Exists(movedDir), "the moved owned directory (with the staged object) must still exist");
                Assert.True(Directory.EnumerateFiles(movedDir).Any(), "the staged owned object must still exist under the moved directory");
                Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            }
            finally
            {
                if (movedDir != null && Directory.Exists(movedDir))
                {
                    Directory.Delete(movedDir, recursive: true);
                }
            }

            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ObjectLifetime_StagingDirectoryRenamedAway_ForeignEmptyDirectoryOccupiesOldPath_ForeignNeverDeleted()
    {
        // Same pre-acquisition takeover window, but a FOREIGN empty directory
        // now occupies the original staging pathname.  Rollback must never
        // delete the foreign directory (identity mismatch / no exact
        // authority) and must report RollbackFailed — never RolledBack while
        // the owned object lives under the moved directory.
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(samplePath, null, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? movedDir = null;
            string? foreignDir = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                if (stage == CleanerFailureStage.Staging && detail == "BeforeImageHandleAcquisition")
                {
                    triggerCount++;
                    string stagingDir = FindStagedFile(workspace, "stage-img*").StagingDir;
                    movedDir = stagingDir + ".owned-moved-away";
                    if (!NativeIo.MoveFileW(stagingDir, movedDir))
                    {
                        throw new IOException($"MoveFileW(staging dir) failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
                    }
                    Directory.CreateDirectory(stagingDir);
                    foreignDir = stagingDir;
                    throw new IOException("Simulated failure after a foreign empty directory took over the staging pathname.");
                }
                return Task.CompletedTask;
            };

            try
            {
                ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.NotNull(foreignDir);
                Assert.NotNull(movedDir);
                Assert.True(Directory.Exists(foreignDir), "foreign empty directory must survive rollback");
                Assert.Empty(Directory.EnumerateFileSystemEntries(foreignDir));
                Assert.True(Directory.Exists(movedDir), "the moved owned directory must still exist");
                Assert.True(Directory.EnumerateFiles(movedDir).Any(), "the staged owned object must still exist under the moved directory");
                Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
            }
            finally
            {
                if (movedDir != null && Directory.Exists(movedDir))
                {
                    Directory.Delete(movedDir, recursive: true);
                }
                if (foreignDir != null && Directory.Exists(foreignDir))
                {
                    Directory.Delete(foreignDir, recursive: true);
                }
            }

            Assert.Equal(shaBefore, ComputeSha256(samplePath));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ObjectLifetime_MotionVideoHardLinkAliasWhileHandleOpen_ImageAndVideoRolledBack_VideoAliasSurvives()
    {
        // Dual-file bundle (image + motion video).  After the image has been
        // published, an external hard-link alias is created on the STAGED
        // VIDEO object while its retained handle is open.  The video publish
        // then fails.  Rollback must exact-delete BOTH owned objects (the
        // published image through its handle, the staged video through its
        // handle) — but the video disposition only removes the staged name;
        // the object survives under the foreign alias, so the by-FileId
        // re-check proves it alive and the transaction must report
        // RollbackFailed (never a false RolledBack while the owned video
        // object still exists).
        string imgPath = ResolveSample("苹果双文件.HEIC");
        string movPath = ResolveSample("苹果双文件.MOV");
        string shaBefore = ComputeSha256(imgPath);

        using var workspace = new MediaWorkspace();
        var cleaner = new SourceProtocolCleaner();
        var (bundle, nativeContext, cleanupPlan) = await PrepareAsync(imgPath, movPath, workspace);
        using (nativeContext)
        using (cleanupPlan)
        {
            int triggerCount = 0;
            string? videoAliasPath = null;
            string? cleanVidPath = null;
            cleaner.FaultInjectionHook = (stage, detail) =>
            {
                // The staging-directory lease denies hard links to staged files
                // (probe H4b: err=32), so the observable alias window is AFTER
                // the video is published.  The retained publish handle is still
                // open and the bundle transaction is still active.
                if (stage == CleanerFailureStage.Commit && detail == "VideoPublished")
                {
                    triggerCount++;
                    cleanVidPath = Assert.Single(Directory.GetFiles(workspace.RootDirectory, "clean-vid*", SearchOption.AllDirectories));
                    videoAliasPath = Path.Combine(workspace.RootDirectory, "owned-video-hardlink-alias.mov");
                    if (!NativeIo.CreateHardLinkW(videoAliasPath, cleanVidPath, IntPtr.Zero))
                    {
                        throw new IOException($"CreateHardLinkW failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");
                    }
                    throw new IOException("Simulated failure after a foreign hard-link alias was created on the owned video.");
                }
                return Task.CompletedTask;
            };

            try
            {
                ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
                {
                    ExtractedBundle = bundle,
                    CleanupPlan = cleanupPlan
                }, workspace);

                Assert.False(result.Success);
                Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
                Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
                Assert.Equal(1, triggerCount);
                Assert.Contains("hard-link alias", result.ErrorMessage);
                Assert.NotNull(videoAliasPath);
                Assert.True(File.Exists(videoAliasPath), "foreign hard-link alias on the video object must never be deleted");
                // The image publish was rolled back exactly: no clean image output remains.
                Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "clean-img*", SearchOption.AllDirectories));
                // The owned VIDEO object is STILL ALIVE under clean-vid (and its
                // alias): the by-FileId proof detects the alias and rollback
                // truthfully reports RollbackFailed — never a false RolledBack.
                Assert.NotNull(cleanVidPath);
                Assert.True(File.Exists(cleanVidPath), "the owned video object must still be alive under clean-vid");
            }
            finally
            {
                if (videoAliasPath != null && File.Exists(videoAliasPath))
                {
                    File.SetAttributes(videoAliasPath, FileAttributes.Normal);
                    File.Delete(videoAliasPath);
                }
            }

            Assert.Equal(shaBefore, ComputeSha256(imgPath));
        }
    }

    // ---------------------------------------------------------------------
    // B5-C: staging-directory ownership is captured from the ATOMIC creating
    // handle (CreateOwnedDirectory = NtCreateFile FILE_CREATE +
    // FILE_DIRECTORY_FILE in one kernel transition).  A directory that
    // ALREADY exists at the staging pathname is never claimed — the create
    // fails closed with STATUS_OBJECT_NAME_COLLISION — so the old
    // create-then-reopen(pathname) TOCTOU can never turn a foreign directory
    // into "this transaction's" staging directory and later delete it.
    // ---------------------------------------------------------------------
    [Fact]
    public void ObjectLifetime_CreateOwnedDirectory_PreExistingForeignDirectory_FailsClosed()
    {
        using var workspace = new MediaWorkspace();
        string dir = Path.Combine(workspace.RootDirectory, "staging_preexisting_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CleanerException ex = Assert.Throws<CleanerException>(
                () => WindowsOwnedFilePublisher.CreateOwnedDirectory(dir));
            Assert.Equal(CleanerFailureCategory.OutputCreateFailed, ex.Category);
            Assert.Contains("already exists", ex.Message);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    private static class NativeIo
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileW(string existingFileName, string newFileName);
    }
}