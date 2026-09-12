using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

/// <summary>
/// Eighth-round external-gate closeout: the LAST ownership seam is the
/// publish step itself.  Every active Cleaner sink must publish through the
/// SAME HANDLE that created and wrote the object
/// (SetFileInformationByHandle / FileRenameInfo, ReplaceIfExists = FALSE) and
/// must never hand a pathname to MoveFileExW as the "source" of ownership.
///
/// These tests drive a test-harness-only race seam
/// (lpb_test_set_cleaner_publish_fault): immediately BEFORE publish, the owned
/// object is renamed away from its temp pathname by handle and a foreign
/// object is created at that same temp pathname.  A pathname-based publish
/// would then move/delete the FOREIGN object; a handle-based publish must
/// move the ORIGINAL object and leave the foreign object untouched.
///
/// Failure-cleanup variant: the destination is pre-occupied by a foreign
/// object, so publish fails.  The failure cleanup (FileDispositionInfo through
/// the owned handle) must delete only the transaction-owned object and must
/// never bare-delete by pathname: both the temp-pathname foreign object and
/// the destination foreign object must survive byte-for-byte.
/// </summary>
public sealed class CleanerEighthRoundOwnershipTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static async Task<(ExtractedMediaBundle Bundle, MediaWorkspace Workspace)> ExtractAsync(string samplePath)
    {
        var workspace = new MediaWorkspace();
        var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, null, workspace);
        return (bundle, workspace);
    }

    // The seam writes a well-known 24-byte ASCII marker into the foreign
    // object it creates at the stolen temp pathname.
    private static ReadOnlySpan<byte> ForeignMarker => "LPB-FOREIGN-RACE-MARKER"u8;

    private static List<string> FindForeignMarkerFiles(string rootDir)
    {
        var hits = new List<string>();
        foreach (string file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < ForeignMarker.Length)
                {
                    continue;
                }
                Span<byte> head = stackalloc byte[ForeignMarker.Length];
                int read = fs.Read(head);
                if (read >= ForeignMarker.Length && head.SequenceEqual(ForeignMarker))
                {
                    hits.Add(file);
                }
            }
            catch (IOException)
            {
                // Transient share/race on a file that is being moved or deleted;
                // it is not a stable foreign object, so it is not evidence.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return hits;
    }

    private static List<string> FindRaceSideFiles(string rootDir)
    {
        try
        {
            return Directory.EnumerateFiles(rootDir, "*lpb-race-side*", SearchOption.AllDirectories).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return new List<string>();
        }
    }

    // ------------------------------------------------------------------
    // Publish-race: the temp pathname is taken over by a foreign object
    // immediately before publish.  The handle-based publish must move the
    // ORIGINAL object to the destination, register the ORIGINAL object's
    // identity, and leave the foreign object untouched at its temp pathname.
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_GenericJpegSink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(
            ResolveSample("oppo.jpg"), "race-oppo", ".jpg", "clean-race-oppo", ".jpg", null);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_Mp4Sink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(
            ResolveSample("vivo.jpg"), "race-vivo", ".jpg", "clean-race-vivo", ".jpg", "clean-race-vivo-video");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_SamsungSefJpegSink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(
            ResolveSample("三星.jpg"), "race-samsung-jpeg", ".jpg", "clean-race-samsung-jpeg", ".jpg", null);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_SamsungHeicSink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(
            ResolveSample("三星.heic"), "race-samsung-heic", ".heic", "clean-race-samsung-heic", ".heic", null);
    }

    private static async Task AssertPublishRaceAsync(
        string samplePath,
        string inputPrefix,
        string inputExt,
        string outputPrefix,
        string outputExt,
        string? videoOutputPrefix)
    {
        (ExtractedMediaBundle bundle, MediaWorkspace workspace) = await ExtractAsync(samplePath);
        using var workspaceLease = workspace;
        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueFromBundleAsync(context, bundle);
        string outPath = workspace.AllocateFilePath(outputPrefix, outputExt);
        string? outVidPath = videoOutputPrefix is null
            ? null
            : workspace.AllocateFilePath(videoOutputPrefix, ".mp4");

        // Arm the one-shot seam: the harness build swaps the owned temp object
        // for a foreign one at the same temp pathname right before publish.
        NativeResult armed = TestHarnessNativeMethods.SetCleanerPublishFault(context.Handle, 1);
        Assert.Equal(NativeResult.Ok, armed);

        using CleanupPlanAttempt attempt = plan.BeginCleanupAttempt();
        IReadOnlyList<RemovedProtocolFact> removed =
            await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, bundle.PrimaryImage!.Path, bundle.MotionVideo?.Path,
                bundle.CleanupSource?.Path, outPath, outVidPath);

        Assert.NotEmpty(removed);

        // The ORIGINAL object reached the destination: it exists, carries the
        // registered identity, and is not the foreign marker payload.
        Assert.True(File.Exists(outPath), "The handle-based publish must move the original object to the destination.");
        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: true);
        var record = records.FirstOrDefault(r => r.ArtifactRole == MediaArtifactKind.PrimaryImage);
        Assert.NotNull(record);
        WindowsFileIdentity disk = WindowsFileIdentity.Capture(outPath);
        Assert.Equal(disk.VolumeSerialNumber, record.VolumeSerial);
        Assert.Equal(disk.FileIndex, record.FileIndex);
        Assert.Equal(disk.LinkCount, record.LinkCount);

        byte[] publishedHead = new byte[ForeignMarker.Length];
        using (var fs = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.True(fs.Read(publishedHead, 0, ForeignMarker.Length) >= ForeignMarker.Length);
        }

        Assert.False(publishedHead.AsSpan().SequenceEqual(ForeignMarker),
            "The destination must contain the real cleaned payload, never the foreign marker.");

        // The foreign object survives byte-for-byte at the stolen temp
        // pathname: exactly one marker file remains in the staging root.
        List<string> foreign = FindForeignMarkerFiles(workspace.RootDirectory);
        Assert.Single(foreign);
        Assert.NotEqual(outPath, foreign[0]);

        // The owned object was renamed by handle from temp -> destination, so
        // the seam's side pathname must be empty afterwards.
        Assert.Empty(FindRaceSideFiles(workspace.RootDirectory));
    }

    // ------------------------------------------------------------------
    // Failure-cleanup race: destination pre-occupied + temp pathname taken
    // over.  Publish fails.  Failure cleanup must act through the owned
    // HANDLE only: the temp-pathname foreign object and the destination
    // foreign object both survive byte-for-byte.
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_GenericJpegSink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(
            ResolveSample("oppo.jpg"), "fail-oppo", ".jpg", "clean-fail-oppo", ".jpg", null);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_Mp4Sink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(
            ResolveSample("vivo.jpg"), "fail-vivo", ".jpg", "clean-fail-vivo", ".jpg", "clean-fail-vivo-video");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_SamsungSefJpegSink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(
            ResolveSample("三星.jpg"), "fail-samsung-jpeg", ".jpg", "clean-fail-samsung-jpeg", ".jpg", null);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_SamsungHeicSink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(
            ResolveSample("三星.heic"), "fail-samsung-heic", ".heic", "clean-fail-samsung-heic", ".heic", null);
    }

    private static async Task AssertFailureCleanupAsync(
        string samplePath,
        string inputPrefix,
        string inputExt,
        string outputPrefix,
        string outputExt,
        string? videoOutputPrefix)
    {
        (ExtractedMediaBundle bundle, MediaWorkspace workspace) = await ExtractAsync(samplePath);
        using var workspaceLease = workspace;
        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueFromBundleAsync(context, bundle);
        string outPath = workspace.AllocateFilePath(outputPrefix, outputExt);
        string? outVidPath = videoOutputPrefix is null
            ? null
            : workspace.AllocateFilePath(videoOutputPrefix, ".mp4");

        // Foreign object already occupies the destination: a no-overwrite
        // publish must fail closed instead of replacing it.
        byte[] destForeign = "LPB-DEST-FOREIGN-OBJECT"u8.ToArray();
        File.WriteAllBytes(outPath, destForeign);
        if (outVidPath is not null)
        {
            File.WriteAllBytes(outVidPath, destForeign);
        }

        NativeResult armed = TestHarnessNativeMethods.SetCleanerPublishFault(context.Handle, 1);
        Assert.Equal(NativeResult.Ok, armed);

        using CleanupPlanAttempt attempt = plan.BeginCleanupAttempt();
        // The no-overwrite publish must fail closed (InternalError) when the
        // destination is pre-occupied; the exact failure message is an
        // implementation detail -- the safety contract is that the failure is
        // surfaced AND every foreign object survives byte-for-byte.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, bundle.PrimaryImage!.Path, bundle.MotionVideo?.Path,
                bundle.CleanupSource?.Path, outPath, outVidPath));

        // The destination foreign object was never covered or deleted.
        Assert.Equal(destForeign, File.ReadAllBytes(outPath));
        if (outVidPath is not null)
        {
            Assert.Equal(destForeign, File.ReadAllBytes(outVidPath));
        }

        // The temp-pathname foreign object (seam marker) survives byte-for-byte.
        List<string> foreign = FindForeignMarkerFiles(workspace.RootDirectory);
        Assert.Single(foreign);

        // No race-side leftover: the owned object's failure cleanup was
        // exact-handle (FileDispositionInfo), and the publish never moved a
        // foreign object.
        Assert.Empty(FindRaceSideFiles(workspace.RootDirectory));
    }
}
