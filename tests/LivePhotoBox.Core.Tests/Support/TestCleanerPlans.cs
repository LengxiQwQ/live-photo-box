using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Core.Tests.Support;

/// <summary>
/// Issues a test-harness Native cleanup plan from a bundle's facts + artifact
/// paths, mirroring what the production extractor does from its extraction
/// record.  The plan is bound to the caller's <see cref="TestNativeContext"/>
/// and dispatches cleaning to the harness Native build.  Test-only: production
/// authority always originates from a trusted P2 extraction plan.
/// </summary>
internal static class TestCleanerPlans
{
    public static Task<CleanupPlan> IssueFromBundleAsync(
        TestNativeContext context,
        ExtractedMediaBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bundle);
        cancellationToken.ThrowIfCancellationRequested();

        return IssueAsync(
            context,
            bundle.SourceFacts,
            bundle.PrimaryImage?.Path,
            bundle.MotionVideo?.Path,
            bundle.CleanupSource?.Path,
            cancellationToken);
    }

    /// <summary>
    /// Issues a harness cleanup plan from caller-chosen artifact paths and a
    /// caller-authored action list.  Test-only adversarial helper: the residue
    /// metadata (owner protocol, coordinate space, duplicates, ...) is converted
    /// verbatim into the plan so Native validation can be exercised.
    /// </summary>
    public static Task<CleanupPlan> IssueFromActionsAsync(
        TestNativeContext context,
        SourceMediaFacts facts,
        IReadOnlyList<PlannedCleanupAction> actions,
        string? imagePath,
        string? videoPath,
        string? cleanupSourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(actions);
        cancellationToken.ThrowIfCancellationRequested();

        var residues = new List<ConfirmedProtocolResidue>(actions.Count);
        foreach (PlannedCleanupAction a in actions)
        {
            residues.Add(new ConfirmedProtocolResidue
            {
                Id = a.ResidueId,
                OwnerProtocol = a.OwnerProtocol,
                ArtifactRole = a.ArtifactRole,
                StructureKind = a.StructureKind,
                Selector = a.Selector,
                ExpectedSemantic = a.ExpectedSemantic,
                ExpectedFingerprint = a.ExpectedFingerprint,
                CoordinateSpace = a.CoordinateSpace,
                RemovalMode = a.RemovalMode,
                RequiredAfterExtraction = a.IsMandatory
            });
        }

        return IssueAsync(context, facts with { ConfirmedResidues = residues }, imagePath, videoPath, cleanupSourcePath, cancellationToken);
    }

    /// <summary>
    /// Issues a harness cleanup plan over caller-chosen artifact specs,
    /// preserving duplicates and arbitrary roles so Native ownership-record
    /// validation can be exercised adversarially.
    /// </summary>
    public static Task<CleanupPlan> IssueWithArtifactsAsync(
        TestNativeContext context,
        SourceMediaFacts facts,
        IReadOnlyList<(int Role, string Path)> artifacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(artifacts);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(facts);
            IReadOnlyList<ConfirmedProtocolResidue> residues = facts.ConfirmedResidues ?? [];

            unsafe
            {
                NativeConfirmedResidue[] residueArray = new NativeConfirmedResidue[residues.Count];
                for (int i = 0; i < residues.Count; i++)
                {
                    NativeConfirmedResidue r = residueArray[i];
                    r.StructSize = checked((uint)sizeof(NativeConfirmedResidue));
                    WriteFixed(r.ResidueId, 64, residues[i].Id);
                    r.OwnerProtocol = (int)residues[i].OwnerProtocol;
                    r.ArtifactRole = (int)residues[i].ArtifactRole;
                    r.StructureKind = (int)residues[i].StructureKind;
                    WriteFixed(r.Selector, 128, residues[i].Selector);
                    WriteFixed(r.ExpectedSemantic, 64, residues[i].ExpectedSemantic);
                    WriteFixed(r.ExpectedFingerprint, 64, residues[i].ExpectedFingerprint);
                    r.CoordinateSpace = (int)residues[i].CoordinateSpace;
                    r.RemovalMode = (int)residues[i].RemovalMode;
                    r.RequiredAfterExtraction = residues[i].RequiredAfterExtraction ? 1 : 0;
                    residueArray[i] = r;
                }

                TestCleanupArtifactSpec[] artifactArray = new TestCleanupArtifactSpec[artifacts.Count];
                var pathAllocations = new List<nint>(artifacts.Count);
                try
                {
                    for (int i = 0; i < artifacts.Count; i++)
                    {
                        artifactArray[i].ArtifactRole = artifacts[i].Role;
                        artifactArray[i].AuxiliaryIndex = 0;
                        artifactArray[i].Path = Marshal.StringToCoTaskMemUTF8(artifacts[i].Path);
                        pathAllocations.Add(artifactArray[i].Path);
                    }

                    fixed (NativeConfirmedResidue* pRes = residueArray)
                    fixed (TestCleanupArtifactSpec* pArtifacts = artifactArray)
                    {
                        NativeResult result = TestNativeMethods.IssueCleanupPlanFromFacts(
                            context.Handle,
                            in nativeFacts,
                            pRes,
                            (nuint)residueArray.Length,
                            pArtifacts,
                            (nuint)artifactArray.Length,
                            out nint plan,
                            out ulong generation);
                        if (result != NativeResult.Ok)
                        {
                            context.ThrowIfFailed(result);
                        }

                        return CleanupPlan.CreateForTests(context.Handle, plan, generation);
                    }
                }
                finally
                {
                    foreach (nint p in pathAllocations)
                    {
                        Marshal.FreeCoTaskMem(p);
                    }
                }
            }
        }, cancellationToken);
    }

    /// <summary>Issues a harness cleanup plan over caller-chosen artifact paths.</summary>
    public static Task<CleanupPlan> IssueAsync(
        TestNativeContext context,
        SourceMediaFacts facts,
        string? imagePath,
        string? videoPath,
        string? cleanupSourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(facts);
        cancellationToken.ThrowIfCancellationRequested();

        var specs = new List<TestCleanupArtifactSpec>();
        var paths = new List<string>();
        if (!string.IsNullOrEmpty(imagePath))
        {
            specs.Add(new TestCleanupArtifactSpec { ArtifactRole = (int)MediaArtifactKind.PrimaryImage });
            paths.Add(imagePath!);
        }
        if (!string.IsNullOrEmpty(videoPath))
        {
            specs.Add(new TestCleanupArtifactSpec { ArtifactRole = (int)MediaArtifactKind.MotionVideo });
            paths.Add(videoPath!);
        }
        if (!string.IsNullOrEmpty(cleanupSourcePath))
        {
            specs.Add(new TestCleanupArtifactSpec { ArtifactRole = (int)MediaArtifactKind.SourceContainer });
            paths.Add(cleanupSourcePath!);
        }

        return Task.Run(() =>
        {
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(facts);
            IReadOnlyList<ConfirmedProtocolResidue> residues = facts.ConfirmedResidues ?? [];

            unsafe
            {
                NativeConfirmedResidue[] residueArray = new NativeConfirmedResidue[residues.Count];
                for (int i = 0; i < residues.Count; i++)
                {
                    NativeConfirmedResidue r = residueArray[i];
                    r.StructSize = checked((uint)sizeof(NativeConfirmedResidue));
                    WriteFixed(r.ResidueId, 64, residues[i].Id);
                    r.OwnerProtocol = (int)residues[i].OwnerProtocol;
                    r.ArtifactRole = (int)residues[i].ArtifactRole;
                    r.StructureKind = (int)residues[i].StructureKind;
                    WriteFixed(r.Selector, 128, residues[i].Selector);
                    WriteFixed(r.ExpectedSemantic, 64, residues[i].ExpectedSemantic);
                    WriteFixed(r.ExpectedFingerprint, 64, residues[i].ExpectedFingerprint);
                    r.CoordinateSpace = (int)residues[i].CoordinateSpace;
                    r.RemovalMode = (int)residues[i].RemovalMode;
                    r.RequiredAfterExtraction = residues[i].RequiredAfterExtraction ? 1 : 0;
                    residueArray[i] = r;
                }

                TestCleanupArtifactSpec[] artifactArray = specs.ToArray();
                var pathAllocations = new List<nint>(artifactArray.Length);
                try
                {
                    for (int i = 0; i < artifactArray.Length; i++)
                    {
                        artifactArray[i].Path = Marshal.StringToCoTaskMemUTF8(paths[i]);
                        pathAllocations.Add(artifactArray[i].Path);
                    }

                    fixed (NativeConfirmedResidue* pRes = residueArray)
                    fixed (TestCleanupArtifactSpec* pArtifacts = artifactArray)
                    {
                        NativeResult result = TestNativeMethods.IssueCleanupPlanFromFacts(
                            context.Handle,
                            in nativeFacts,
                            pRes,
                            (nuint)residueArray.Length,
                            pArtifacts,
                            (nuint)artifactArray.Length,
                            out nint plan,
                            out ulong generation);
                        if (result != NativeResult.Ok)
                        {
                            context.ThrowIfFailed(result);
                        }

                        return CleanupPlan.CreateForTests(context.Handle, plan, generation);
                    }
                }
                finally
                {
                    foreach (nint p in pathAllocations)
                    {
                        Marshal.FreeCoTaskMem(p);
                    }
                }
            }
        }, cancellationToken);
    }

    private static unsafe void WriteFixed(byte* ptr, int maxLen, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            ptr[0] = 0;
            return;
        }
        int written = Encoding.UTF8.GetBytes(value, new Span<byte>(ptr, maxLen - 1));
        ptr[written] = 0;
    }
}

/// <summary>
/// Mirrors native lpb_test_cleanup_artifact_spec: the artifact role and the
/// UTF-8 path captured as the object identity at issue time.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TestCleanupArtifactSpec
{
    public int ArtifactRole;
    public uint AuxiliaryIndex;
    public nint Path;
}
