using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

/// <summary>
/// R2 deterministic continuity checks.  Each test records the identity of A
/// after exact handle acquisition, attempts a namespace takeover at the
/// strict validation boundary, and then proves the committed artifact is A.
/// No timing race or sleep is involved.
/// </summary>
public sealed class CleanerValidatePublishContinuityTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrimaryImage_ValidationNamespaceLeaseBlocksReplacement_AndPublishesA()
    {
        string sourcePath = TestSampleResolver.ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await new SourceInspector().InspectAsync(sourcePath);
        ExtractedMediaBundle bundle = await TestFactsExtractor.ExtractAsync(facts, sourcePath, null, workspace);
        using var nativeContext = TestNativeContext.Create();
        using CleanupPlan cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, bundle);

        var cleaner = new SourceProtocolCleaner();
        WindowsFileIdentity? identityA = null;
        bool replacementBlocked = false;
        string? movedA = null;
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageHandleAcquired")
            {
                string staged = FindStagedPath(workspace, "stage-img*");
                identityA = WindowsFileIdentity.Capture(staged);
            }
            else if (stage == CleanerFailureStage.Staging && detail == "ValidationNamespaceLeaseAcquired")
            {
                string staged = FindStagedPath(workspace, "stage-img*");
                movedA = staged + ".r2-attack-a";
                replacementBlocked = !TryManagedMove(staged, movedA);
                if (!replacementBlocked)
                {
                    // If the first takeover step ever succeeds, attempt the
                    // complete A -> B construction.  The assertion below must
                    // fail rather than silently accepting a weakened lease.
                    File.Copy(sourcePath, staged, overwrite: false);
                }
            }
            return Task.CompletedTask;
        };

        try
        {
            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle with { CleanupPlan = cleanupPlan }
            }, workspace);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(CleanerTransactionState.Committed, result.TransactionState);
            Assert.True(replacementBlocked, "strict validation lease allowed a primary-image namespace takeover");
            Assert.NotNull(identityA);
            Assert.Equal(identityA, result.CleanedImage!.FileIdentity);
        }
        finally
        {
            if (movedA != null && File.Exists(movedA)) File.Delete(movedA);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task MotionVideo_ValidationNamespaceLeaseBlocksReplacement_AndPublishesA()
    {
        string imagePath = TestSampleResolver.ResolveSample("苹果双文件.HEIC");
        string videoPath = TestSampleResolver.ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await new SourceInspector().InspectAsync(imagePath, videoPath);
        ExtractedMediaBundle bundle = await TestFactsExtractor.ExtractAsync(facts, imagePath, videoPath, workspace);
        using var nativeContext = TestNativeContext.Create();
        using CleanupPlan cleanupPlan = await TestCleanerPlans.IssueAsync(
            nativeContext, facts, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null);

        var cleaner = new SourceProtocolCleaner();
        WindowsFileIdentity? imageA = null;
        WindowsFileIdentity? videoA = null;
        bool imageBlocked = false;
        bool videoBlocked = false;
        string? movedImage = null;
        string? movedVideo = null;
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageHandleAcquired")
            {
                imageA = WindowsFileIdentity.Capture(FindStagedPath(workspace, "stage-img*"));
            }
            else if (stage == CleanerFailureStage.Staging && detail == "VideoHandleAcquired")
            {
                videoA = WindowsFileIdentity.Capture(FindStagedPath(workspace, "stage-vid*"));
            }
            else if (stage == CleanerFailureStage.Staging && detail == "ValidationNamespaceLeaseAcquired")
            {
                string stagedImage = FindStagedPath(workspace, "stage-img*");
                string stagedVideo = FindStagedPath(workspace, "stage-vid*");
                movedImage = stagedImage + ".r2-attack-a";
                movedVideo = stagedVideo + ".r2-attack-a";
                imageBlocked = !TryManagedMove(stagedImage, movedImage);
                videoBlocked = !TryManagedMove(stagedVideo, movedVideo);
            }
            return Task.CompletedTask;
        };

        try
        {
            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle with { CleanupPlan = cleanupPlan }
            }, workspace);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(CleanerTransactionState.Committed, result.TransactionState);
            Assert.True(imageBlocked, "strict validation lease allowed an image namespace takeover");
            Assert.True(videoBlocked, "strict validation lease allowed a video namespace takeover");
            Assert.Equal(imageA, result.CleanedImage!.FileIdentity);
            Assert.Equal(videoA, result.CleanedVideo!.FileIdentity);
        }
        finally
        {
            if (movedImage != null && File.Exists(movedImage)) File.Delete(movedImage);
            if (movedVideo != null && File.Exists(movedVideo)) File.Delete(movedVideo);
        }
    }

    [Fact]
    public async Task NonLive_ValidationEvidenceAndPublishUseSameRetainedA()
    {
        using var workspace = new MediaWorkspace();
        string sourcePath = workspace.AllocateFilePath("r2-nonlive-source", ".jpg");
        byte[] bytes =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
        ];
        await File.WriteAllBytesAsync(sourcePath, bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimarySha256 = sha,
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = bytes.Length, IsPresent = true }
        };
        var bundle = new ExtractedMediaBundle
        {
            SourceFacts = facts,
            PrimaryImage = new MediaArtifact
            {
                Path = sourcePath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = bytes.Length,
                Sha256 = sha
            }
        };
        using var nativeContext = TestNativeContext.Create();
        using CleanupPlan cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, bundle);

        var cleaner = new SourceProtocolCleaner();
        WindowsFileIdentity? identityA = null;
        bool replacementBlocked = false;
        string? movedA = null;
        string? stagedPath = null;
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ValidationNamespaceLeaseAcquired")
            {
                stagedPath = FindStagedPath(workspace, "stage-img*");
                identityA = WindowsFileIdentity.Capture(stagedPath);
                movedA = stagedPath + ".r2-attack-a";
                replacementBlocked = !TryManagedMove(stagedPath, movedA);
                if (!replacementBlocked)
                {
                    File.Copy(sourcePath, stagedPath, overwrite: false);
                }
            }
            return Task.CompletedTask;
        };

        try
        {
            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle with { CleanupPlan = cleanupPlan }
            }, workspace);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(CleanerTransactionState.Committed, result.TransactionState);
            Assert.True(replacementBlocked, "NonLive strict validation lease allowed a namespace takeover");
            Assert.NotNull(identityA);
            Assert.Equal(identityA, result.CleanedImage!.FileIdentity);
            Assert.Equal(sha, result.CleanedImage.Sha256);
        }
        finally
        {
            if (movedA != null && File.Exists(movedA)) File.Delete(movedA);
            if (stagedPath != null && File.Exists(stagedPath)) File.Delete(stagedPath);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CorruptedPrimaryA_IsWhatValidationSees_WhenBReplacementIsDenied()
    {
        string sourcePath = TestSampleResolver.ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await new SourceInspector().InspectAsync(sourcePath);
        ExtractedMediaBundle bundle = await TestFactsExtractor.ExtractAsync(facts, sourcePath, null, workspace);
        using var nativeContext = TestNativeContext.Create();
        using CleanupPlan cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, bundle);

        var cleaner = new SourceProtocolCleaner();
        WindowsFileIdentity? identityA = null;
        bool replacementBlocked = false;
        string? movedA = null;
        string? stagedPath = null;
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "BeforeImageHandleAcquisition")
            {
                stagedPath = FindStagedPath(workspace, "stage-img*");
                identityA = WindowsFileIdentity.Capture(stagedPath);

                // Same-length corruption while no retained file handle exists.
                // This is deliberately before exact handle acquisition so the
                // identity remains A even though the bytes are now bad.
                using var corruptor = new FileStream(
                    stagedPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                corruptor.WriteByte(0x00);
                corruptor.Flush(flushToDisk: true);
            }
            else if (stage == CleanerFailureStage.Staging && detail == "ValidationNamespaceLeaseAcquired")
            {
                string staged = FindStagedPath(workspace, "stage-img*");
                movedA = staged + ".r2-corrupt-a";
                replacementBlocked = !TryManagedMove(staged, movedA);
                if (!replacementBlocked)
                {
                    // A weakened implementation would now validate this good
                    // foreign B while publishing retained corrupted A.
                    File.Copy(sourcePath, staged, overwrite: false);
                }
            }
            return Task.CompletedTask;
        };

        try
        {
            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle with { CleanupPlan = cleanupPlan }
            }, workspace);

            Assert.False(result.Success, "corrupted retained A must not reach Committed through a B pathname");
            Assert.True(replacementBlocked, "strict validation lease allowed the A -> B replacement attack");
            Assert.NotEqual(CleanerTransactionState.Committed, result.TransactionState);
            Assert.NotNull(identityA);
        }
        finally
        {
            if (movedA != null && File.Exists(movedA)) File.Delete(movedA);
            if (stagedPath != null && File.Exists(stagedPath)) File.Delete(stagedPath);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PreLeaseReplacement_IsRejectedBeforeAnyPathValidatorCanRun()
    {
        string sourcePath = TestSampleResolver.ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await new SourceInspector().InspectAsync(sourcePath);
        ExtractedMediaBundle bundle = await TestFactsExtractor.ExtractAsync(facts, sourcePath, null, workspace);
        using var nativeContext = TestNativeContext.Create();
        using CleanupPlan cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, bundle);

        var cleaner = new SourceProtocolCleaner();
        WindowsFileIdentity? identityA = null;
        WindowsFileIdentity? identityB = null;
        string? movedA = null;
        string? stagedPath = null;
        bool attackConstructed = false;
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageHandleAcquired")
            {
                stagedPath = FindStagedPath(workspace, "stage-img*");
                identityA = WindowsFileIdentity.Capture(stagedPath);
                movedA = stagedPath + ".r2-prelease-a";
                if (!TryManagedMove(stagedPath, movedA))
                {
                    throw new IOException(
                        $"The deterministic pre-lease A -> side move failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                // No strict lease exists yet.  Construct the full A/B
                // discontinuity without throwing: VerifyRetainedStagedNamespace
                // must reject B before preservation or post-clean validation.
                File.Copy(sourcePath, stagedPath, overwrite: false);
                identityB = WindowsFileIdentity.Capture(stagedPath);
                attackConstructed = true;
            }
            return Task.CompletedTask;
        };

        try
        {
            ProtocolCleanResult result = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = bundle with { CleanupPlan = cleanupPlan }
            }, workspace);

            Assert.True(attackConstructed);
            Assert.NotNull(identityA);
            Assert.NotNull(identityB);
            Assert.NotEqual(identityA, identityB);
            Assert.False(result.Success, "pre-lease B must never be validated and committed as A");
            Assert.NotEqual(CleanerTransactionState.Committed, result.TransactionState);
        }
        finally
        {
            if (stagedPath != null && File.Exists(stagedPath)) File.Delete(stagedPath);
            if (movedA != null && File.Exists(movedA)) File.Delete(movedA);
        }
    }

    private static string FindStagedPath(MediaWorkspace workspace, string pattern)
    {
        string stagingDir = Assert.Single(Directory.GetDirectories(workspace.RootDirectory, "staging_*"));
        return Assert.Single(Directory.GetFiles(stagingDir, pattern));
    }

    private static bool TryManagedMove(string source, string destination)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"The deterministic namespace attack source disappeared before the move attempt: '{source}'.",
                source);
        }

        try
        {
            File.Move(source, destination);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
