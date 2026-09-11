using System;
using System.IO;
using System.Runtime.CompilerServices;
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
/// Fourth-round external-gate blockers: staged-output ownership, exact
/// rollback deletion, exception/cancellation staging ownership, and
/// same-thread re-entrant raw-writer authority.
///
/// Core invariant under test: "I need to delete/publish the path that has this
/// File ID" is no longer enough — the transaction must prove "this File ID was
/// created/owned by MY transaction, and the instant I mutate it, it is still
/// that object."  A same-content replacement (same SHA, same length, different
/// filesystem object) must fail closed everywhere in the P2 -> P3 chain.
/// </summary>
public sealed class CleanerFourthRoundOwnershipTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);


    private static async Task<CleanerException> ExpectCleanerExceptionAsync(Func<Task> action)
        => await Assert.ThrowsAsync<CleanerException>(async () => await action());

    /// <summary>
    /// Replaces <paramref name="path"/> with a NEW filesystem object holding
    /// byte-identical content: same path, same SHA, same length, different
    /// File ID.  The transaction must never treat the replacement as owned.
    /// </summary>
    private static void ReplaceWithSameContentObject(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        File.Delete(path);
        File.WriteAllBytes(path, bytes);
    }

    // ------------------------------------------------------------------
    // B1: Rollback exact delete happens on the verified handle; a
    // same-content replacement that appears before rollback runs can never
    // be deleted (identity mismatch on the delete handle).
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Adversarial_Rollback_StagedSameContentReplacedAfterCancel_LeavesForeignObject()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using var cts = new CancellationTokenSource();

        var cleaner = new SourceProtocolCleaner();
        string? stagedReplacement = null;

        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageStaged")
            {
                // Same-content replacement of the staged output AFTER the
                // Native cleaner created and registered it.  The replacement
                // has a different File ID; rollback must refuse to delete it.
                stagedReplacement = Directory.GetFiles(
                    Directory.GetDirectories(workspace.RootDirectory, "staging_*").Single(), "stage-img*").Single();
                ReplaceWithSameContentObject(stagedReplacement);
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        // Cancellation with a failed rollback is a hard failure: the cleaner
        // must throw with an explicit RollbackFailed state (never pretend the
        // rollback succeeded) because the same-content foreign object at the
        // staged pathname must not be deleted.
        CleanerException cleanEx = await Assert.ThrowsAsync<CleanerException>(() =>
            cleaner.CleanAsync(
                new ProtocolCleanRequest { ExtractedBundle = bundle, CleanupPlan = cleanupPlan },
                workspace,
                cts.Token));
        Assert.Equal(CleanerFailureCategory.RollbackFailed, cleanEx.Category);

        Assert.NotNull(stagedReplacement);
        Assert.True(File.Exists(stagedReplacement), "The same-content foreign staged object must survive rollback.");
    }

    // ------------------------------------------------------------------
    // B2: On Native exception / cancellation, staged ownership comes ONLY
    // from the Native registry.  A foreign file injected into the staging
    // directory is never claimed, so rollback cannot delete it.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Adversarial_CancellationPath_ForeignStagingChild_NotClaimedByRegistry()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using var cts = new CancellationTokenSource();

        var cleaner = new SourceProtocolCleaner();
        string? foreignChildPath = null;
        string? stagingDir = null;

        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageStaged")
            {
                stagingDir = Directory.GetDirectories(workspace.RootDirectory, "staging_*").Single();
                // A foreign file appears in the staging directory while the
                // Native cleaner is (about to be) interrupted.  The Native
                // registry never reported it; CaptureNativeStagedOutputs must
                // NOT claim it as transaction-owned.
                foreignChildPath = Path.Combine(stagingDir, "foreign-injected.bin");
                File.WriteAllBytes(foreignChildPath, [0xAB, 0xCD, 0xEF]);
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        // Cancellation with a failed rollback is a hard failure: the foreign
        // child was never claimed as transaction-owned, so rollback refuses to
        // delete it, and the staging directory is left in place.
        CleanerException cleanEx = await Assert.ThrowsAsync<CleanerException>(() =>
            cleaner.CleanAsync(
                new ProtocolCleanRequest { ExtractedBundle = bundle, CleanupPlan = cleanupPlan },
                workspace,
                cts.Token));
        Assert.Equal(CleanerFailureCategory.RollbackFailed, cleanEx.Category);

        // The foreign child survives (never claimed, never deleted), and the
        // staging directory is left in place because it is not empty.
        Assert.NotNull(foreignChildPath);
        Assert.True(File.Exists(foreignChildPath), "A foreign file injected into the staging directory must never be deleted by rollback.");
        Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF }, File.ReadAllBytes(foreignChildPath));
        Assert.NotNull(stagingDir);
        Assert.True(Directory.Exists(stagingDir), "The staging directory must be left in place when it contains a foreign child.");
    }

    // ------------------------------------------------------------------
    // B3: Native -> Managed staging ownership is continuous.  The Managed
    // layer uses the Native registry identity for commit, and re-verifies
    // the exact object on the same path before the move: a same-content
    // replacement between Native return and commit fails closed.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Adversarial_StagedReplacedBeforeCommit_SameContent_FailsClosed()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;

        var cleaner = new SourceProtocolCleaner();
        string? stagedReplacement = null;

        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageStaged")
            {
                // The Native cleaner returned and registered ownership of its
                // staged object; a same-content object now takes over the path.
                stagedReplacement = Directory.GetFiles(
                    Directory.GetDirectories(workspace.RootDirectory, "staging_*").Single(), "stage-img*").Single();
                ReplaceWithSameContentObject(stagedReplacement);
            }
            return Task.CompletedTask;
        };

        ProtocolCleanResult result = await cleaner.CleanAsync(
            new ProtocolCleanRequest { ExtractedBundle = bundle, CleanupPlan = cleanupPlan },
            workspace);

        // Commit must fail closed (the object at the staged path is no longer
        // the exact object Native registered), and the same-content foreign
        // staged object must survive rollback.
        Assert.False(result.Success);
        Assert.True(
            result.ErrorMessage.Contains("no longer matches the identity", StringComparison.OrdinalIgnoreCase) ||
            result.ErrorMessage.Contains("identity this transaction registered", StringComparison.OrdinalIgnoreCase) ||
            result.ErrorMessage.Contains("foreign", StringComparison.OrdinalIgnoreCase),
            $"Expected a fail-closed identity error, got: {result.ErrorMessage}");

        Assert.NotNull(stagedReplacement);
        Assert.True(File.Exists(stagedReplacement), "The same-content foreign staged object must survive the failed commit.");
        Assert.False(
            Directory.GetFiles(workspace.RootDirectory, "clean-img*").Any(),
            "No destination may be published when the staged object was replaced.");
    }

    // ------------------------------------------------------------------
    // B4: thread-local authority cannot be borrowed by a SAME-thread
    // re-entrant external callback.  The cleaner suspends its capability
    // across the cancellation callback; a raw writer invoked from inside the
    // callback must fail with AuthorityViolation.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task ProductionChain_SameThreadReentrantCancelCallback_CannotBorrowCleanerAuthority()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();

        var probe = new ReentrantProbe { Context = attempt.ContextLease.Handle };
        GCHandle probeHandle = GCHandle.Alloc(probe);
        try
        {
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, int> fn = &ReentrantCancelCallback;
                NativeResult setResult = TestHarnessNativeMethods.TestSetCancelCallback(
                    attempt.ContextLease.Handle, (nint)fn, GCHandle.ToIntPtr(probeHandle));
                Assert.Equal(NativeResult.Ok, setResult);
            }

            string cleanedPath = workspace.AllocateFilePath("clean-img", ".jpg");
            IReadOnlyList<RemovedProtocolFact> removed = await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null, cleanedPath, null);

            Assert.NotEmpty(removed);
            Assert.True(File.Exists(cleanedPath), "The authorized clean must still succeed.");

            // The re-entrant raw writer ran on the SAME native thread while the
            // cleaner's cancellation callback was live.  It must have been
            // rejected: the capability window is suspended across the callback.
            Assert.NotNull(probe.Result);
            Assert.Equal(NativeResult.AuthorityViolation, probe.Result!.Value);
        }
        finally
        {
            probeHandle.Free();
        }
    }

    [DllImport("LivePhotoBox.Native.dll", EntryPoint = "lpb_jpeg_inject_xmp", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe NativeResult JpegInjectXmp(
        nint context,
        byte* input,
        nuint inputSize,
        byte* xmpXml,
        nuint xmpXmlSize,
        byte* output,
        nuint outputSize,
        out nuint outWritten);

    private static unsafe NativeResult InjectJpegXmpRaw(nint contextHandle)
    {
        byte[] input = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[] xmp = System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta/>");
        byte[] output = new byte[4096];
        fixed (byte* pInput = input)
        fixed (byte* pXmp = xmp)
        fixed (byte* pOutput = output)
        {
            return JpegInjectXmp(
                contextHandle,
                pInput,
                (nuint)input.Length,
                pXmp,
                (nuint)xmp.Length,
                pOutput,
                (nuint)output.Length,
                out _);
        }
    }

    private sealed class ReentrantProbe
    {
        public nint Context;
        public NativeResult? Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReentrantCancelCallback(nint userData)
    {
        if (userData == nint.Zero) return 0;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(userData);
            if (handle.IsAllocated && handle.Target is ReentrantProbe probe)
            {
                // Same native thread as the authorized cleaner, same context —
                // but the cleaner has suspended its capability across this
                // external callback boundary.  The raw destructive primitive
                // must fail the authority gate instead of borrowing it.
                probe.Result = InjectJpegXmpRaw(probe.Context);
            }
        }
        catch
        {
            // Best effort: the probe must never crash the clean.
        }
        return 0; // do not actually cancel
    }
}
