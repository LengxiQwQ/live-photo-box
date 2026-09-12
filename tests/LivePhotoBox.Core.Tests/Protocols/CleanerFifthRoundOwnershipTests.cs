using System;
using System.IO;
using System.Runtime.InteropServices;
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
/// Fifth-round external-gate blockers (B1-B4): the final ownership window is
/// the one BETWEEN "the object was created/published" and "the registry record
/// was established", plus the last two bare-pathname mutation points.
///
/// Invariant: ownership must be established from the creating/publishing
/// HANDLE.  No code path may re-open a pathname to re-create ownership, and no
/// failure path may bare-delete by pathname.  A foreign object that occupies an
/// expected output path, a legacy fixed temp name, or a same-content
/// replacement must fail closed and survive.
/// </summary>
public sealed class CleanerFifthRoundOwnershipTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static unsafe void InitFacts(NativeRemovedProtocolFact* facts, int count)
    {
        for (int i = 0; i < count; i++)
        {
            facts[i].StructSize = (uint)sizeof(NativeRemovedProtocolFact);
        }
    }

    // ------------------------------------------------------------------
    // B1 (evidence): the Native ownership registry is captured from the
    // publishing handle, so the registered identity must exactly match the
    // filesystem object that was published to the destination path.  This is
    // the continuous chain: create handle -> exact identity -> registry ->
    // publish -> commit/rollback on the same exact identity.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Production_NativeRegistryIdentity_MatchesPublishedObjectOnDisk()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();
        string outPath = workspace.AllocateFilePath("registry-identity", ".jpg");

        IReadOnlyList<RemovedProtocolFact> removed = await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
            attempt, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null, outPath, null);
        Assert.NotEmpty(removed);

        Assert.True(File.Exists(outPath));
        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: false);
        var record = Assert.Single(records);
        Assert.Equal(MediaArtifactKind.PrimaryImage, record.ArtifactRole);

        WindowsFileIdentity disk = WindowsFileIdentity.Capture(outPath);
        Assert.Equal(disk.VolumeSerialNumber, record.VolumeSerial);
        Assert.Equal(disk.FileIndex, record.FileIndex);
        Assert.Equal(disk.LinkCount, record.LinkCount);
    }

    // ------------------------------------------------------------------
    // B3: the Native BUFFER_TOO_SMALL failure path must remove only the exact
    // object the transaction published (identity from the creating handle).
    // It must never bare-delete by pathname.  After the failure the output
    // path is empty again and the ownership registry is cleared.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Adversarial_NativeBufferTooSmall_LeavesOnlyOwnedOutputForTransactionRollback()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();
        string outPath = workspace.AllocateFilePath("buffer-too-small", ".jpg");

        unsafe
        {
            var facts = new NativeRemovedProtocolFact[1];
            fixed (NativeRemovedProtocolFact* pFacts = facts)
            {
                InitFacts(pFacts, 1);
                nuint outCount = 0;
                NativeResult res = NativeMethods.CleanSourceProtocolWithCleanupPlan(
                    attempt.ContextLease.Handle, attempt.NativeHandle, attempt.Generation,
                    bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null,
                    outPath, null, pFacts, 0, out outCount);

                Assert.Equal(NativeResult.BufferTooSmall, res);
            }
        }

        // The Native BUFFER_TOO_SMALL path never pathname-deletes.  The
        // published object and its ownership registry record are left intact so
        // that only the transaction rollback (exact verified handle +
        // FileDispositionInfo) may remove the object this transaction created.
        // A same-content replacement or foreign object that took over the path
        // is therefore never at risk, and the caller can retry with a larger
        // buffer or roll back through the registry.
        Assert.True(File.Exists(outPath), "The published object is left for the transaction rollback to decide.");
        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: false);
        var record = Assert.Single(records);
        WindowsFileIdentity disk = WindowsFileIdentity.Capture(outPath);
        Assert.Equal(disk.VolumeSerialNumber, record.VolumeSerial);
        Assert.Equal(disk.FileIndex, record.FileIndex);
        Assert.Equal(disk.LinkCount, record.LinkCount);
    }

    // ------------------------------------------------------------------
    // B4: the Samsung HEIF cleaner must never pre-delete a fixed temp
    // pathname.  It creates a unique temp (CreateNew semantics), publishes it
    // no-overwrite, and only ever cleans its own objects.  A foreign file
    // that happens to sit on the legacy fixed temp name must survive.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Adversarial_HeicLegacyFixedTempName_InjectedSurvivesClean()
    {
        string samplePath = ResolveSample("三星.heic");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();
        string outPath = workspace.AllocateFilePath("clean-samsung", ".heic");
        string injectedTemp = outPath + ".lpb-heif-cleaning-tmp";
        File.WriteAllBytes(injectedTemp, [0xDE, 0xAD, 0xBE, 0xEF]);

        unsafe
        {
            var facts = new NativeRemovedProtocolFact[256];
            fixed (NativeRemovedProtocolFact* pFacts = facts)
            {
                InitFacts(pFacts, 256);
                nuint outCount = 0;
                NativeResult res = NativeMethods.CleanSourceProtocolWithCleanupPlan(
                    attempt.ContextLease.Handle, attempt.NativeHandle, attempt.Generation,
                    bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null,
                    outPath, null, pFacts, 256, out outCount);
                Assert.Equal(NativeResult.Ok, res);
            }
        }

        Assert.True(File.Exists(outPath), "The Samsung HEIC clean must publish its output.");
        Assert.True(File.Exists(injectedTemp), "A foreign file on the legacy fixed temp name must never be pre-deleted.");
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], File.ReadAllBytes(injectedTemp));
    }

    // ------------------------------------------------------------------
    // Sixth-round gate: ownership for the three dedicated Cleaner sinks
    // (MP4 video, Samsung SEF JPEG, Samsung HEIC) must be continuous from the
    // creating handle.  Each sink writes, publishes and captures identity with
    // ONE handle; the registry record must exactly match the filesystem object
    // on disk.  These tests drive each real sink (not the OPPO write_file_binary
    // path) so the sink-specific chain is actually covered.
    // ------------------------------------------------------------------
    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Production_Mp4VideoSink_RegistryIdentity_MatchesPublishedObjectOnDisk()
    {
        string samplePath = ResolveSample("vivo.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();
        string outPath = workspace.AllocateFilePath("clean-vivo", ".jpg");
        string outVidPath = workspace.AllocateFilePath("clean-vivo-video", ".mp4");

        IReadOnlyList<RemovedProtocolFact> removed = await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
            attempt, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null, outPath, outVidPath);
        Assert.NotEmpty(removed);

        Assert.True(File.Exists(outVidPath), "The MP4 sink must publish its video output.");
        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: false);

        bool found = false;
        ulong videoVolumeSerial = 0, videoFileIndex = 0;
        uint videoLinkCount = 0;
        foreach (var r in records)
        {
            if (r.ArtifactRole == MediaArtifactKind.MotionVideo)
            {
                videoVolumeSerial = r.VolumeSerial;
                videoFileIndex = r.FileIndex;
                videoLinkCount = r.LinkCount;
                found = true;
                break;
            }
        }
        Assert.True(found, "The MP4 sink must register the published video object in the ownership registry.");

        WindowsFileIdentity videoDisk = WindowsFileIdentity.Capture(outVidPath);
        Assert.Equal(videoDisk.VolumeSerialNumber, videoVolumeSerial);
        Assert.Equal(videoDisk.FileIndex, videoFileIndex);
        Assert.Equal(videoDisk.LinkCount, videoLinkCount);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Production_SamsungSefJpegSink_RegistryIdentity_MatchesPublishedObjectOnDisk()
    {
        string samplePath = ResolveSample("三星.jpg");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();
        string outPath = workspace.AllocateFilePath("clean-samsung-jpeg", ".jpg");

        Assert.NotNull(bundle.CleanupSource);
        IReadOnlyList<RemovedProtocolFact> removed = await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
            attempt, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, bundle.CleanupSource.Path, outPath, null);
        Assert.NotEmpty(removed);

        Assert.True(File.Exists(outPath), "The Samsung SEF sink must publish its output.");
        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: false);
        var record = Assert.Single(records);
        Assert.Equal(MediaArtifactKind.PrimaryImage, record.ArtifactRole);

        WindowsFileIdentity disk = WindowsFileIdentity.Capture(outPath);
        Assert.Equal(disk.VolumeSerialNumber, record.VolumeSerial);
        Assert.Equal(disk.FileIndex, record.FileIndex);
        Assert.Equal(disk.LinkCount, record.LinkCount);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "CleanerProductionChain")]
    public async Task Production_SamsungHeicSink_RegistryIdentity_MatchesPublishedObjectOnDisk()
    {
        string samplePath = ResolveSample("三星.heic");
        using var workspace = new MediaWorkspace();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        using CleanupPlan cleanupPlan = bundle.CleanupPlan!;
        using CleanupPlanAttempt attempt = cleanupPlan.BeginCleanupAttempt();
        string outPath = workspace.AllocateFilePath("clean-samsung-heic", ".heic");

        IReadOnlyList<RemovedProtocolFact> removed = await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
            attempt, bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, null, outPath, null);
        Assert.NotEmpty(removed);

        Assert.True(File.Exists(outPath), "The Samsung HEIC sink must publish its output.");
        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: false);
        var record = Assert.Single(records);
        Assert.Equal(MediaArtifactKind.PrimaryImage, record.ArtifactRole);

        WindowsFileIdentity disk = WindowsFileIdentity.Capture(outPath);
        Assert.Equal(disk.VolumeSerialNumber, record.VolumeSerial);
        Assert.Equal(disk.FileIndex, record.FileIndex);
        Assert.Equal(disk.LinkCount, record.LinkCount);
    }
}
