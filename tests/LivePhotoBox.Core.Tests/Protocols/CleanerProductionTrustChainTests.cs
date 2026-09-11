using System;
using System.IO;
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
/// Exercises the REAL P2 -> P3 production trust chain end to end:
/// SourceInspector (Native extraction plan) -> SourceExtractor (Native
/// published artifacts, which itself issues the production cleanup plan via
/// CleanupPlan.IssueFrom before committing the extraction record) ->
/// production CleanSourceProtocolWithCleanupPlan.
///
/// These tests must NOT use the test-harness issuer: a green harness path is
/// not evidence that the production chain works (the production issuer once
/// self-deadlocked on plan_mutex while every harness test stayed green).
/// </summary>
[Collection("NativeCleanerSnapshotHook")]
public sealed class CleanerProductionTrustChainTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task ProductionChain_InspectorExtractor_CleanSucceeds_NoDeadlock()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);

        // The production extractor issues the cleanup plan from the Native
        // extraction record (CleanupPlan.IssueFrom -> lpb_issue_cleanup_plan)
        // BEFORE committing.  The historic implementation self-deadlocked on
        // plan_mutex there, so a successful extract with a non-null plan is
        // itself the deadlock regression: if the issuer deadlocks, ExtractAsync
        // never returns and this test times out via the run guard below.
        Task<ExtractedMediaBundle> extractTask = new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        Task completed = await Task.WhenAny(extractTask, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(
            ReferenceEquals(completed, extractTask),
            "Production extract/IssueFrom chain self-deadlocked (lpb_issue_cleanup_plan).");

        ExtractedMediaBundle bundle = await extractTask;
        Assert.NotNull(bundle.CleanupPlan);

        var cleaner = new SourceProtocolCleaner();
        ProtocolCleanResult result = await cleaner.CleanAsync(
            new ProtocolCleanRequest { ExtractedBundle = bundle, CleanupPlan = bundle.CleanupPlan },
            workspace);

        Assert.True(result.Success, $"Production clean failed: {result.ErrorMessage}");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task ProductionChain_ExternalIssueFrom_AfterExtract_FailsClosed()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        Assert.NotNull(bundle.CleanupPlan);

        // The extraction record already handed its published artifacts to the
        // bundle's cleanup plan; a second issuance must be rejected.
        CleanerException replay = Assert.ThrowsAny<CleanerException>(() =>
            CleanupPlan.IssueFrom(inspected.ExtractionPlan));
        Assert.Equal(CleanerFailureCategory.CleanupAuthorizationMissing, replay.Category);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task ProductionChain_ConcurrentRawWriter_DuringProductionClean_FailsClosed()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        Assert.NotNull(bundle.CleanupPlan);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();

        string cleanedPath = workspace.AllocateFilePath("clean-img", ".jpg");
        NativeResult? rawResult = null;
        using var borrowed = new ManualResetEventSlim(initialState: false);
        using var hookEntered = new ManualResetEventSlim(initialState: false);

        NativeCleanService.TestCleanerSnapshotHandleConfigurator = TestNativeHarness.ConfigureCleanerSnapshotHook;
        NativeCleanService.TestPostSnapshotHookContext = attempt.ContextLease.Handle;
        NativeCleanService.TestPostSnapshotHook = () =>
        {
            hookEntered.Set();
            // Thread B tries to borrow the capability window while Thread A is
            // inside a plan-authorized clean on the same context.  The raw
            // writer must fail closed: authority is bound to the clean thread.
            Task.Run(() =>
            {
                rawResult = InjectJpegXmpRaw(attempt.ContextLease.Handle);
                borrowed.Set();
            });
            borrowed.Wait();
        };

        try
        {
            var removed = await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null, cleanedPath, null);

            Assert.True(hookEntered.IsSet, "The post-snapshot hook must run inside the production clean.");
            Assert.NotNull(rawResult);
            Assert.Equal(NativeResult.AuthorityViolation, rawResult!.Value);
            Assert.NotEmpty(removed);
            Assert.True(File.Exists(cleanedPath));
        }
        finally
        {
            NativeCleanService.TestPostSnapshotHook = null;
            NativeCleanService.TestPostSnapshotHookContext = nint.Zero;
            NativeCleanService.TestCleanerSnapshotHandleConfigurator = null;
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task ProductionChain_PublishedDestinationReplacedBySameContentForeign_RollbackRefusesToDelete()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        Assert.NotNull(bundle.CleanupPlan);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;

        var cleaner = new SourceProtocolCleaner();
        string? foreignPath = null;

        // The publish already moved the transaction's own staged object to the
        // destination.  A foreign process now replaces that destination with a
        // DIFFERENT object holding the SAME bytes: same path + same content,
        // but a new filesystem object.  Rollback must not delete it.
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
            {
                foreignPath = Directory.GetFiles(workspace.RootDirectory, "clean-img*").Single();
                byte[] sameBytes = File.ReadAllBytes(foreignPath);
                File.Delete(foreignPath);
                File.WriteAllBytes(foreignPath, sameBytes);
                throw new IOException("Simulated publish failure after same-content foreign replacement.");
            }
            return Task.CompletedTask;
        };

        ProtocolCleanResult result = await cleaner.CleanAsync(
            new ProtocolCleanRequest { ExtractedBundle = bundle, CleanupPlan = cleanupPlan },
            workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
        Assert.Contains("foreign-object protection", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // The foreign object at the original pathname survives untouched.
        Assert.NotNull(foreignPath);
        Assert.True(File.Exists(foreignPath));
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

    /// <summary>Calls a production raw in-place JPEG destructive primitive.</summary>
    private static unsafe NativeResult InjectJpegXmpRaw(nint contextHandle)
    {
        byte[] input = [0xFF, 0xD8, 0xFF, 0xD9]; // minimal JPEG (SOI + EOI)
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
}


/// <summary>
/// The Native post-snapshot hook is a process-wide test seam.  Classes that
/// set it must not run in parallel with any other test: a parallel test's
/// clean invocation would observe the foreign hook and corrupt its own input
/// (or crash).  Serializing the two hook users closes that cross-test window.
/// </summary>
[CollectionDefinition("NativeCleanerSnapshotHook", DisableParallelization = true)]
public sealed class NativeCleanerSnapshotHookCollection;
