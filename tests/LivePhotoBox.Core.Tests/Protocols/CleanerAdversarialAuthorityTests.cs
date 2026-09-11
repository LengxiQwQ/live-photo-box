using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

/// <summary>
/// P2 -> P3 adversarial authority tests (closeout requirement 8).
///
/// Every scenario must FAIL CLOSED: a forged Managed DTO, raw SourceMediaFacts,
/// a replayed / cross-context / stale-generation plan, or a replaced /
/// hard-linked filesystem object can never authorize or survive a destructive
/// mutation.  Foreign filesystem objects must never be modified or deleted.
/// </summary>
public sealed class CleanerAdversarialAuthorityTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static async Task<SourceMediaFacts> InspectOppoAsync()
    {
        var inspector = new SourceInspector();
        return await inspector.InspectAsync(ResolveSample("oppo.jpg"));
    }

    private static async Task<string> CopyOppoToWorkspaceAsync(IMediaWorkspace workspace, string prefix)
    {
        string target = workspace.AllocateFilePath(prefix, ".jpg");
        File.Copy(ResolveSample("oppo.jpg"), target, overwrite: true);
        return target;
    }

    private static async Task<CleanerException> ExpectCleanerExceptionAsync(Func<Task> action)
        => await Assert.ThrowsAsync<CleanerException>(async () => await action());

    [Fact]
    public async Task Adversarial_ForgedFakeCleanupPlan_NeverAuthorizesAnything()
    {
        // A forged Managed CleanupPlan (zero Native token) carries no
        // authority: claiming it must fail closed before any mutation.
        CleanupPlan fake = CleanupPlan.CreateFake();

        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
        {
            using CleanupPlanAttempt attempt = fake.BeginCleanupAttempt();
            return Task.CompletedTask;
        });

        Assert.Equal(CleanerFailureCategory.NativeAuthorityViolation, ex.Category);
        Assert.Equal(CleanerFailureStage.Authorization, ex.Stage);
    }

    [Fact]
    public async Task Adversarial_RawSourceMediaFacts_CanNeverAuthorizeDestructiveClean()
    {
        // The raw Managed DTO cleanup entry point must remain permanently
        // fail-closed: facts + actions alone are never destructive authority.
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-rawfacts");
        string cleanedImage = workspace.AllocateFilePath("adv-rawfacts-out", ".jpg");

        var actions = facts.ConfirmedResidues!
            .Select(r => new PlannedCleanupAction
            {
                ResidueId = r.Id,
                OwnerProtocol = facts.Protocol,
                ArtifactRole = r.ArtifactRole,
                StructureKind = r.StructureKind,
                Selector = r.Selector,
                RemovalMode = r.RemovalMode,
                IsMandatory = true,
                ExpectedSemantic = r.ExpectedSemantic ?? "",
                ExpectedFingerprint = r.ExpectedFingerprint
            })
            .ToList();

        ExtractionException ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeCleanService.CleanSourceProtocolAsync(facts, actions, tempImage, null, cleanedImage, null));

        Assert.Equal(ExtractionFailureCategory.AuthorityViolation, ex.Category);
        Assert.False(File.Exists(cleanedImage), "Raw DTO cleanup must not publish outputs without Native authority.");
    }

    [Fact]
    public async Task Adversarial_CleanupPlanReplay_AfterConsume_FailsClosed()
    {
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-replay");
        string cleanedImage = workspace.AllocateFilePath("adv-replay-out", ".jpg");

        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueAsync(context, facts, tempImage, null, null);

        // First consume through the authorized Native path.
        using (CleanupPlanAttempt attempt = plan.BeginCleanupAttempt())
        {
            IReadOnlyList<RemovedProtocolFact> removed =
                await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                    attempt, tempImage, null, null, cleanedImage, null, CancellationToken.None);
            Assert.NotEmpty(removed);
        }

        // Replaying the consumed plan must fail closed.
        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
        {
            using CleanupPlanAttempt replay = plan.BeginCleanupAttempt();
            return Task.CompletedTask;
        });
        Assert.Equal(CleanerFailureCategory.CleanupAuthorizationMissing, ex.Category);
        Assert.Equal(CleanerFailureStage.Authorization, ex.Stage);
    }

    [Fact]
    public async Task Adversarial_CleanupPlanDoubleClaim_FailsClosed()
    {
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-doubleclaim");

        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueAsync(context, facts, tempImage, null, null);

        using (CleanupPlanAttempt first = plan.BeginCleanupAttempt())
        {
            // A second claim of the same single-use plan must be rejected.
            CleanerException ex = await ExpectCleanerExceptionAsync(() =>
            {
                using CleanupPlanAttempt second = plan.BeginCleanupAttempt();
                return Task.CompletedTask;
            });
            Assert.Equal(CleanerFailureCategory.CleanupAuthorizationMissing, ex.Category);
            Assert.Equal(CleanerFailureStage.Authorization, ex.Stage);
        }
    }

    [Fact]
    public async Task Adversarial_CleanupPlanCrossContext_Rejected()
    {
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-crossctx");
        string cleanedImage = workspace.AllocateFilePath("adv-crossctx-out", ".jpg");

        using var contextA = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueAsync(contextA, facts, tempImage, null, null);

        // A plan issued by context A can never be claimed through context B:
        // the opaque handle is bound to the issuing Native context.
        using var contextB = TestNativeContext.Create();
        using var foreignAttempt = CleanupPlan.CreateForTests(contextB.Handle, plan.NativeHandle, plan.Generation);

        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
        {
            using CleanupPlanAttempt attempt = foreignAttempt.BeginCleanupAttempt();
            return Task.CompletedTask;
        });

        Assert.Equal(CleanerFailureCategory.NativeAuthorityViolation, ex.Category);
        Assert.Equal(CleanerFailureStage.Authorization, ex.Stage);
        Assert.False(File.Exists(cleanedImage));
    }

    [Fact]
    public async Task Adversarial_CleanupPlanStaleGeneration_Rejected()
    {
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-stalegen");

        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueAsync(context, facts, tempImage, null, null);

        // A stale generation token must be rejected by the Native state machine.
        using var stalePlan = CleanupPlan.CreateForTests(context.Handle, plan.NativeHandle, plan.Generation + 1);

        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
        {
            using CleanupPlanAttempt attempt = stalePlan.BeginCleanupAttempt();
            return Task.CompletedTask;
        });

        Assert.Equal(CleanerFailureCategory.NativeAuthorityViolation, ex.Category);
        Assert.Equal(CleanerFailureStage.Authorization, ex.Stage);
    }

    [Fact]
    public async Task Adversarial_SamePathSameSha_DifferentFileId_RejectedBeforeMutation()
    {
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-fileid");
        string cleanedImage = workspace.AllocateFilePath("adv-fileid-out", ".jpg");

        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueAsync(context, facts, tempImage, null, null);

        // Replace the artifact with a byte-identical object at the same path:
        // same SHA, same length, but a different filesystem object.  The plan
        // authorized the original object; the replacement must fail closed.
        byte[] bytes = File.ReadAllBytes(tempImage);
        File.Delete(tempImage);
        File.WriteAllBytes(tempImage, bytes);

        using CleanupPlanAttempt attempt = plan.BeginCleanupAttempt();
        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
            NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, tempImage, null, null, cleanedImage, null, CancellationToken.None));

        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, ex.Category);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, ex.Stage);
        Assert.False(File.Exists(cleanedImage), "No output may be produced when the input object was replaced.");
    }

    [Fact]
    public async Task Adversarial_HardlinkedArtifact_RejectedBeforeMutation()
    {
        using var workspace = new MediaWorkspace();
        SourceMediaFacts facts = await InspectOppoAsync();
        string tempImage = await CopyOppoToWorkspaceAsync(workspace, "adv-hardlink");
        string cleanedImage = workspace.AllocateFilePath("adv-hardlink-out", ".jpg");

        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueAsync(context, facts, tempImage, null, null);

        // A second hardlink raises the link count; the plan authorizes the
        // single-link object it captured and must refuse the aliased object.
        string linkPath = Path.Combine(workspace.RootDirectory, "adv-hardlink-alias.jpg");
        Assert.True(
            CreateHardLink(linkPath, tempImage, out int win32Error),
            $"CreateHardLinkW failed with Win32 error {win32Error}.");

        using CleanupPlanAttempt attempt = plan.BeginCleanupAttempt();
        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
            NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, tempImage, null, null, cleanedImage, null, CancellationToken.None));

        Assert.Equal(CleanerFailureCategory.ArtifactChangedSinceExtraction, ex.Category);
        Assert.Equal(CleanerFailureStage.ArtifactVerification, ex.Stage);
        Assert.False(File.Exists(cleanedImage));
    }

    [Fact]
    public async Task Adversarial_PublishedOutputReplacedByForeignObject_RollbackRefusesToDelete()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var cleaner = new SourceProtocolCleaner();

        SourceMediaFacts facts = await inspector.InspectAsync(samplePath);
        var extracted = await TestFactsExtractor.ExtractAsync(facts, samplePath, null, workspace);

        using var nativeContext = TestNativeContext.Create();
        using var cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, extracted);

        byte[] foreignBytes = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01];
        string? foreignPath = null;

        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
            {
                // A foreign object takes over the published pathname before
                // the transaction can finish; rollback must not delete it.
                foreignPath = Directory.GetFiles(workspace.RootDirectory, "clean-img*").Single();
                File.Delete(foreignPath);
                File.WriteAllBytes(foreignPath, foreignBytes);
                throw new IOException("Simulated video publish failure after foreign replacement.");
            }
            return Task.CompletedTask;
        };

        ProtocolCleanResult result = await cleaner.CleanAsync(
            new ProtocolCleanRequest { ExtractedBundle = extracted, CleanupPlan = cleanupPlan },
            workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Rollback, result.FailureStage);
        Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
        Assert.Contains("foreign-object protection", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // The foreign object at the original pathname is untouched.
        Assert.NotNull(foreignPath);
        Assert.True(File.Exists(foreignPath));
        Assert.Equal(foreignBytes, File.ReadAllBytes(foreignPath));
    }

    [Fact]
    public async Task Adversarial_StagingForeignChildInjected_RollbackLeavesItAndRecordsFailure()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var cleaner = new SourceProtocolCleaner();

        SourceMediaFacts facts = await inspector.InspectAsync(samplePath);
        var extracted = await TestFactsExtractor.ExtractAsync(facts, samplePath, null, workspace);

        using var nativeContext = TestNativeContext.Create();
        using var cleanupPlan = await TestCleanerPlans.IssueFromBundleAsync(nativeContext, extracted);

        using var cts = new CancellationTokenSource();
        string? foreignChildPath = null;

        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageStaged")
            {
                // Inject a foreign child into this transaction's staging
                // directory; rollback must never recursively delete it.
                string stagingDir = Directory.GetDirectories(workspace.RootDirectory, "staging_*").Single();
                foreignChildPath = Path.Combine(stagingDir, "foreign-child.bin");
                File.WriteAllBytes(foreignChildPath, [0x11, 0x22, 0x33]);
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        CleanerException ex = await ExpectCleanerExceptionAsync(() =>
            cleaner.CleanAsync(
                new ProtocolCleanRequest { ExtractedBundle = extracted, CleanupPlan = cleanupPlan },
                workspace,
                cts.Token));

        Assert.Equal(CleanerFailureCategory.RollbackFailed, ex.Category);
        Assert.Equal(CleanerFailureStage.Rollback, ex.Stage);

        // The injected foreign child survives, and the staging directory is
        // left in place rather than recursively deleted.
        Assert.NotNull(foreignChildPath);
        Assert.True(File.Exists(foreignChildPath));
        Assert.Equal(workspace.RootDirectory, Path.GetDirectoryName(Directory.GetDirectories(workspace.RootDirectory, "staging_*").Single()) ?? workspace.RootDirectory);
        Assert.True(Directory.Exists(Path.GetDirectoryName(foreignChildPath)));
    }

    [Fact]
    public async Task Adversarial_ProductionNativeAppleStrip_WithoutCleanupAuthority_FailsClosed()
    {
        // The production Native build gates in-place Apple MakerNote mutation
        // behind an active cleanup-plan authority.  A direct P/Invoke against
        // the production DLL with no plan must fail closed (the harness build
        // keeps the raw behavior for contract tests only).
        byte[] heic = File.ReadAllBytes(ResolveSample("苹果双文件.HEIC"));
        using var context = NativeContext.Create();

        unsafe
        {
            fixed (byte* pData = heic)
            {
                NativeResult result = LpbAppleStripLivePhotoEntries(context.Handle, pData, (nuint)heic.Length);
                Assert.Equal(NativeResult.AuthorityViolation, result);
            }
        }

        string? error = context.GetLastError();
        Assert.NotNull(error);
        Assert.Contains("authority", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adversarial_ProductionNativeSefTrailer_WithoutCleanupAuthority_FailsClosed()
    {
        // The production DLL also gates SEF trailer construction (a live-photo
        // byte rewrite) behind an active cleanup-plan authority.
        byte[] video = [0x00, 0x00, 0x00, 0x10, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
                        (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x00, 0x00];
        byte[] output = new byte[video.Length + 256];
        using var context = NativeContext.Create();

        unsafe
        {
            fixed (byte* pVideo = video)
            fixed (byte* pOut = output)
            {
                nuint written = 0;
                NativeResult result = LpbSamsungSefBuildTrailer(
                    context.Handle, pVideo, (nuint)video.Length, 0, 0, pOut, (nuint)output.Length, out written);
                Assert.Equal(NativeResult.AuthorityViolation, result);
            }
        }

        string? error = context.GetLastError();
        Assert.NotNull(error);
        Assert.Contains("authority", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Adversarial_ProductionNativeJpegInjectXmp_WithoutCleanupAuthority_FailsClosed()
    {
        // lpb_jpeg_inject_xmp is used inside plan-authorized cleaning; the
        // production gate must reject any direct call without that authority.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0xFF, 0xD9];
        byte[] xmp = System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta/>");
        byte[] output = new byte[jpeg.Length + xmp.Length + 512];
        using var context = NativeContext.Create();

        unsafe
        {
            fixed (byte* pIn = jpeg)
            fixed (byte* pXmp = xmp)
            fixed (byte* pOut = output)
            {
                nuint written = 0;
                NativeResult result = LpbJpegInjectXmp(
                    context.Handle, pIn, (nuint)jpeg.Length, pXmp, (nuint)xmp.Length,
                    pOut, (nuint)output.Length, out written);
                Assert.Equal(NativeResult.AuthorityViolation, result);
            }
        }

        string? error = context.GetLastError();
        Assert.NotNull(error);
        Assert.Contains("authority", error, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);

    private static bool CreateHardLink(string link, string target, out int error)
    {
        bool result = CreateHardLinkW(link, target, nint.Zero);
        error = result ? 0 : Marshal.GetLastWin32Error();
        return result;
    }

    [DllImport("LivePhotoBox.Native", CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_strip_live_photo_entries")]
    private static unsafe extern NativeResult LpbAppleStripLivePhotoEntries(nint context, byte* data, nuint dataSize);

    [DllImport("LivePhotoBox.Native", CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_samsung_sef_build_trailer")]
    private static unsafe extern NativeResult LpbSamsungSefBuildTrailer(
        nint context, byte* videoData, nuint videoSize, int isHeic, ulong imageSize,
        byte* output, nuint outputSize, out nuint outWritten);

    [DllImport("LivePhotoBox.Native", CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_jpeg_inject_xmp")]
    private static unsafe extern NativeResult LpbJpegInjectXmp(
        nint context, byte* input, nuint inputSize, byte* xmpXml, nuint xmpXmlSize,
        byte* output, nuint outputSize, out nuint outWritten);
}
