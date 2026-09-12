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
/// The publish-race seam (lpb_test_set_cleaner_publish_fault) is now
/// ARTIFACT-ROLE TARGETED: it fires exactly once, immediately before the first
/// publish whose artifact role equals the armed target (PrimaryImage for the
/// generic/SEF/HEIC sinks, MotionVideo for the MP4 sink).  Publishes for any
/// other role run normally and never consume the seam, so a test named after
/// one sink can no longer be silently satisfied by an earlier PrimaryImage
/// publish.  A harness-only getter records which role actually consumed the
/// seam, and every test asserts it.
///
/// MP4 tests use the REAL Vivo legacy dual-file pair (vivo双文件.jpg +
/// vivo双文件.mp4) and assert the detected protocol (VivoLegacyDualFile) and a
/// MotionVideo residue, which pins the production chain to
/// clean_vivo_legacy_video -> stream_clean_mp4_bytes ->
/// lpb_publish_cleaner_output_handle(artifact_role = MotionVideo) instead of
/// the generic write_file_binary path.
///
/// Failure-cleanup variant: the targeted sink's destination is pre-occupied by
/// a foreign object, so its no-overwrite publish fails.  For the MP4 case the
/// PrimaryImage destination stays FREE so PrimaryImage really publishes and is
/// registered FIRST, then the MotionVideo sink fails.  The failure cleanup
/// (FileDispositionInfo through the owned handle) must delete only the
/// transaction-owned object and must never bare-delete by pathname: both the
/// temp-pathname foreign object and the destination foreign object must
/// survive byte-for-byte.
/// </summary>
public sealed class CleanerEighthRoundOwnershipTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static async Task<(ExtractedMediaBundle Bundle, MediaWorkspace Workspace)> ExtractAsync(
        string samplePath,
        string? secondaryPath = null)
    {
        var workspace = new MediaWorkspace();
        var inspected = await new SourceInspector().InspectWithPlanAsync(samplePath, secondaryPath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, samplePath, secondaryPath, workspace);
        return (bundle, workspace);
    }

    // The seam writes a well-known 24-byte ASCII marker into the foreign
    // object it creates at the stolen temp pathname.
    private static ReadOnlySpan<byte> ForeignMarker => "LPB-FOREIGN-RACE-MARKER"u8;

    /// <summary>
    /// One sink under adversarial test: which real sample drives it, which
    /// artifact role the publish-race seam is armed for (and MUST be consumed
    /// by), and whether the sample must resolve to the Vivo legacy dual-file
    /// chain (the dedicated MP4 / stream_clean_mp4_bytes sink).
    /// </summary>
    private sealed record CleanerSinkCase(
        string SamplePath,
        string? SecondaryPath,
        string InputPrefix,
        string InputExt,
        string OutputPrefix,
        string OutputExt,
        string? VideoOutputPrefix,
        MediaArtifactKind SeamTarget,
        bool RequireVivoLegacyDualChain = false);

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

    private static void AssertFileDoesNotCarryForeignMarker(string path)
    {
        byte[] head = new byte[ForeignMarker.Length];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            Assert.True(fs.Read(head, 0, ForeignMarker.Length) >= ForeignMarker.Length,
                "Published object must be at least as large as the foreign marker.");
        }

        Assert.False(head.AsSpan().SequenceEqual(ForeignMarker),
            "The destination must contain the real cleaned payload, never the foreign marker.");
    }

    private static void AssertRegistryIdentity(
        string path,
        uint volumeSerial,
        ulong fileIndex,
        uint linkCount)
    {
        WindowsFileIdentity disk = WindowsFileIdentity.Capture(path);
        Assert.Equal(disk.VolumeSerialNumber, volumeSerial);
        Assert.Equal(disk.FileIndex, fileIndex);
        Assert.Equal(disk.LinkCount, linkCount);
    }

    /// <summary>
    /// Harness-only proof of where the seam actually fired: exactly once, and
    /// only by the targeted artifact role.  This is what prevents a test named
    /// after one sink from silently exercising another.
    /// </summary>
    private static void AssertSeamConsumedOn(nint contextHandle, MediaArtifactKind expectedRole)
    {
        NativeResult ok = TestHarnessNativeMethods.GetCleanerPublishFault(
            contextHandle, out int lastTriggeredArtifactRole, out int triggerCount);
        Assert.Equal(NativeResult.Ok, ok);
        Assert.Equal(1, triggerCount);
        Assert.Equal((int)expectedRole, lastTriggeredArtifactRole);
    }

    // ------------------------------------------------------------------
    // Publish-race: the temp pathname is taken over by a foreign object
    // immediately before the TARGETED sink's publish.  The handle-based
    // publish must move the ORIGINAL object to the destination, register the
    // ORIGINAL object's identity, and leave the foreign object untouched at
    // its temp pathname.  Publishes that ran BEFORE the targeted sink (e.g.
    // PrimaryImage before MotionVideo) must not consume the seam.
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_GenericJpegSink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(new CleanerSinkCase(
            ResolveSample("oppo.jpg"), null, "race-oppo", ".jpg", "clean-race-oppo", ".jpg", null,
            MediaArtifactKind.PrimaryImage));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_Mp4Sink_ForeignTempObjectSurvives()
    {
        // Real Vivo legacy dual-file pair -> LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL,
        // whose production chain runs write_file_binary(PrimaryImage) first and
        // THEN clean_vivo_legacy_video -> stream_clean_mp4_bytes (MotionVideo).
        // The seam is armed for MotionVideo, so the PrimaryImage publish that
        // runs first MUST NOT consume it; only the MP4 sink publish may.
        await AssertPublishRaceAsync(new CleanerSinkCase(
            ResolveSample("vivo双文件.jpg"),
            ResolveSample("vivo双文件.mp4"),
            "race-vivo", ".jpg", "clean-race-vivo", ".jpg", "clean-race-vivo-video",
            MediaArtifactKind.MotionVideo,
            RequireVivoLegacyDualChain: true));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_SamsungSefJpegSink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(new CleanerSinkCase(
            ResolveSample("三星.jpg"), null, "race-samsung-jpeg", ".jpg", "clean-race-samsung-jpeg", ".jpg", null,
            MediaArtifactKind.PrimaryImage));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishRace_SamsungHeicSink_ForeignTempObjectSurvives()
    {
        await AssertPublishRaceAsync(new CleanerSinkCase(
            ResolveSample("三星.heic"), null, "race-samsung-heic", ".heic", "clean-race-samsung-heic", ".heic", null,
            MediaArtifactKind.PrimaryImage));
    }

    private static async Task AssertPublishRaceAsync(CleanerSinkCase c)
    {
        (ExtractedMediaBundle bundle, MediaWorkspace workspace) = await ExtractAsync(c.SamplePath, c.SecondaryPath);
        using var workspaceLease = workspace;
        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueFromBundleAsync(context, bundle);

        if (c.RequireVivoLegacyDualChain)
        {
            // Pins the production chain to Vivo legacy dual-file:
            // write_file_binary(PrimaryImage) -> clean_vivo_legacy_video ->
            // stream_clean_mp4_bytes -> lpb_publish_cleaner_output_handle with
            // artifact_role == MotionVideo (NOT the generic write_file_binary
            // MotionVideo path, which only runs for Vivo X300 / Oppo).
            Assert.Equal(SourceProtocol.VivoLegacyDualFile, bundle.SourceFacts.Protocol);
            Assert.Contains(bundle.SourceFacts.ConfirmedResidues,
                r => r.ArtifactRole == MediaArtifactKind.MotionVideo);
        }

        string outPath = workspace.AllocateFilePath(c.OutputPrefix, c.OutputExt);
        string? outVidPath = c.VideoOutputPrefix is null
            ? null
            : workspace.AllocateFilePath(c.VideoOutputPrefix, ".mp4");

        // Arm the one-shot seam for exactly the targeted sink.
        NativeResult armed = TestHarnessNativeMethods.SetCleanerPublishFault(context.Handle, 1, (int)c.SeamTarget);
        Assert.Equal(NativeResult.Ok, armed);

        using CleanupPlanAttempt attempt = plan.BeginCleanupAttempt();
        IReadOnlyList<RemovedProtocolFact> removed =
            await NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, bundle.PrimaryImage!.Path, bundle.MotionVideo?.Path,
                bundle.CleanupSource?.Path, outPath, outVidPath);

        Assert.NotEmpty(removed);

        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: true);

        // PrimaryImage published first through its own sink, registered the
        // ORIGINAL object's identity at outPath, and must NOT have consumed a
        // MotionVideo-targeted seam.
        Assert.True(File.Exists(outPath), "The handle-based publish must move the original object to the destination.");
        Assert.Contains(records, r => r.ArtifactRole == MediaArtifactKind.PrimaryImage);
        var primary = records.Single(r => r.ArtifactRole == MediaArtifactKind.PrimaryImage);
        AssertRegistryIdentity(outPath, primary.VolumeSerial, primary.FileIndex, primary.LinkCount);
        AssertFileDoesNotCarryForeignMarker(outPath);

        if (outVidPath is not null)
        {
            // The MotionVideo publish reached the MP4 sink, consumed the seam
            // exactly there, and registered the ORIGINAL owned MP4 object's
            // identity at outVidPath -- not a foreign object and not the
            // PrimaryImage object.
            Assert.True(File.Exists(outVidPath), "The MotionVideo handle-based publish must move the original MP4 object to the destination.");
            Assert.Contains(records, r => r.ArtifactRole == MediaArtifactKind.MotionVideo);
            var motion = records.Single(r => r.ArtifactRole == MediaArtifactKind.MotionVideo);
            AssertRegistryIdentity(outVidPath, motion.VolumeSerial, motion.FileIndex, motion.LinkCount);
            AssertFileDoesNotCarryForeignMarker(outVidPath);
        }

        // The seam was consumed exactly once, by exactly the targeted sink.
        AssertSeamConsumedOn(context.Handle, c.SeamTarget);

        // The foreign object survives byte-for-byte at the stolen temp
        // pathname: exactly one marker file remains in the staging root, and
        // it is neither destination.
        List<string> foreign = FindForeignMarkerFiles(workspace.RootDirectory);
        Assert.Single(foreign);
        Assert.NotEqual(outPath, foreign[0]);
        if (outVidPath is not null)
        {
            Assert.NotEqual(outVidPath, foreign[0]);
        }

        // The owned object was renamed by handle from temp -> destination, so
        // the seam's side pathname must be empty afterwards.
        Assert.Empty(FindRaceSideFiles(workspace.RootDirectory));
    }

    // ------------------------------------------------------------------
    // Failure-cleanup race: the TARGETED sink's destination is pre-occupied
    // and its temp pathname is taken over.  Publish fails.  Failure cleanup
    // must act through the owned HANDLE only: the temp-pathname foreign
    // object and the destination foreign object both survive byte-for-byte.
    // For the MP4 case, PrimaryImage publishes (and registers) FIRST because
    // its destination stays free; only the MotionVideo sink fails.
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_GenericJpegSink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(new CleanerSinkCase(
            ResolveSample("oppo.jpg"), null, "fail-oppo", ".jpg", "clean-fail-oppo", ".jpg", null,
            MediaArtifactKind.PrimaryImage));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_Mp4Sink_ForeignTempAndDestinationSurvive()
    {
        // Real Vivo legacy dual-file pair; seam armed for MotionVideo only.
        // outPath stays FREE so PrimaryImage really publishes and registers
        // first; only outVidPath is pre-occupied so the MP4 sink's
        // no-overwrite publish fails.
        await AssertFailureCleanupAsync(new CleanerSinkCase(
            ResolveSample("vivo双文件.jpg"),
            ResolveSample("vivo双文件.mp4"),
            "fail-vivo", ".jpg", "clean-fail-vivo", ".jpg", "clean-fail-vivo-video",
            MediaArtifactKind.MotionVideo,
            RequireVivoLegacyDualChain: true));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_SamsungSefJpegSink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(new CleanerSinkCase(
            ResolveSample("三星.jpg"), null, "fail-samsung-jpeg", ".jpg", "clean-fail-samsung-jpeg", ".jpg", null,
            MediaArtifactKind.PrimaryImage));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FailureCleanup_SamsungHeicSink_ForeignTempAndDestinationSurvive()
    {
        await AssertFailureCleanupAsync(new CleanerSinkCase(
            ResolveSample("三星.heic"), null, "fail-samsung-heic", ".heic", "clean-fail-samsung-heic", ".heic", null,
            MediaArtifactKind.PrimaryImage));
    }

    private static async Task AssertFailureCleanupAsync(CleanerSinkCase c)
    {
        (ExtractedMediaBundle bundle, MediaWorkspace workspace) = await ExtractAsync(c.SamplePath, c.SecondaryPath);
        using var workspaceLease = workspace;
        using var context = TestNativeContext.Create();
        using var plan = await TestCleanerPlans.IssueFromBundleAsync(context, bundle);

        if (c.RequireVivoLegacyDualChain)
        {
            Assert.Equal(SourceProtocol.VivoLegacyDualFile, bundle.SourceFacts.Protocol);
            Assert.Contains(bundle.SourceFacts.ConfirmedResidues,
                r => r.ArtifactRole == MediaArtifactKind.MotionVideo);
        }

        string outPath = workspace.AllocateFilePath(c.OutputPrefix, c.OutputExt);
        string? outVidPath = c.VideoOutputPrefix is null
            ? null
            : workspace.AllocateFilePath(c.VideoOutputPrefix, ".mp4");

        // Foreign object occupies the TARGETED sink's destination so its
        // no-overwrite publish fails closed instead of replacing it.
        byte[] destForeign = "LPB-DEST-FOREIGN-OBJECT"u8.ToArray();
        if (outVidPath is not null)
        {
            // MP4 case: only the MotionVideo destination is pre-occupied.  The
            // PrimaryImage destination must stay FREE so PrimaryImage publishes
            // and registers BEFORE the MotionVideo sink fails.
            File.WriteAllBytes(outVidPath, destForeign);
        }
        else
        {
            // Single-sink case: the PrimaryImage destination is the target.
            File.WriteAllBytes(outPath, destForeign);
        }

        NativeResult armed = TestHarnessNativeMethods.SetCleanerPublishFault(context.Handle, 1, (int)c.SeamTarget);
        Assert.Equal(NativeResult.Ok, armed);

        using CleanupPlanAttempt attempt = plan.BeginCleanupAttempt();
        // The no-overwrite publish must fail closed (InternalError) when the
        // targeted destination is pre-occupied; the exact failure message is an
        // implementation detail -- the safety contract is that the failure is
        // surfaced AND every foreign object survives byte-for-byte.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync(
                attempt, bundle.PrimaryImage!.Path, bundle.MotionVideo?.Path,
                bundle.CleanupSource?.Path, outPath, outVidPath));

        var records = NativeCleanService.QueryStagedOutputs(attempt.ContextLease.Handle, useHarnessLibrary: true);

        if (outVidPath is not null)
        {
            // The MotionVideo publish failed BEFORE any MotionVideo record
            // could be registered; the destination foreign object survived
            // byte-for-byte.
            Assert.Equal(destForeign, File.ReadAllBytes(outVidPath));
            Assert.DoesNotContain(records, r => r.ArtifactRole == MediaArtifactKind.MotionVideo);

            // PrimaryImage published first (its destination was free), was
            // registered with the ORIGINAL object's identity, and did NOT
            // consume the MotionVideo-targeted seam.
            Assert.True(File.Exists(outPath), "PrimaryImage must publish before the MotionVideo sink fails.");
            Assert.Contains(records, r => r.ArtifactRole == MediaArtifactKind.PrimaryImage);
            var primary = records.Single(r => r.ArtifactRole == MediaArtifactKind.PrimaryImage);
            AssertRegistryIdentity(outPath, primary.VolumeSerial, primary.FileIndex, primary.LinkCount);
            AssertFileDoesNotCarryForeignMarker(outPath);
        }
        else
        {
            // The destination foreign object was never covered or deleted.
            Assert.Equal(destForeign, File.ReadAllBytes(outPath));
            Assert.DoesNotContain(records, r => r.ArtifactRole == MediaArtifactKind.PrimaryImage);
        }

        // The seam was consumed exactly once, by exactly the targeted sink.
        AssertSeamConsumedOn(context.Handle, c.SeamTarget);

        // The temp-pathname foreign object (seam marker) survives byte-for-byte.
        List<string> foreign = FindForeignMarkerFiles(workspace.RootDirectory);
        Assert.Single(foreign);

        // No race-side leftover: the owned object's failure cleanup was
        // exact-handle (FileDispositionInfo), and the publish never moved a
        // foreign object.
        Assert.Empty(FindRaceSideFiles(workspace.RootDirectory));
    }
}
