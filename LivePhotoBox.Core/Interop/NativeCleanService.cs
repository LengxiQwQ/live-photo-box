using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Interop;

/// <summary>
/// Thin control plane service that invokes LivePhotoBox.Native execution plane protocol cleaning operations.
/// </summary>
internal static class NativeCleanService
{
    internal static Action? TestPostSnapshotHook { get; set; }
    // Optional binding of the post-snapshot hook to one specific Native
    // context.  A clean invoked on any other context must ignore the hook
    // instead of running foreign test code against an unrelated (and possibly
    // already destroyed) context.
    internal static nint TestPostSnapshotHookContext { get; set; }
    internal static Action<NativeContext, nint>? TestCleanerSnapshotConfigurator { get; set; }
    internal static Action<nint, nint>? TestCleanerSnapshotHandleConfigurator { get; set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativePostSnapshotCallback(nint contextHandle)
    {
        if (TestPostSnapshotHookContext != nint.Zero && contextHandle != TestPostSnapshotHookContext)
        {
            return;
        }
        TestPostSnapshotHook?.Invoke();
    }

    internal static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolAsync(
        SourceMediaFacts facts,
        IReadOnlyList<PlannedCleanupAction> actions,
        string inputImagePath,
        string? inputVideoPath,
        string? outputImagePath,
        string? outputVideoPath,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<PlannedArtifactTarget>();
        if (System.IO.File.Exists(inputImagePath))
        {
            var fi = new System.IO.FileInfo(inputImagePath);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = System.IO.File.OpenRead(inputImagePath);
            string hash = Convert.ToHexString(sha.ComputeHash(stream));
            targets.Add(new PlannedArtifactTarget
            {
                Role = MediaArtifactKind.PrimaryImage,
                ExpectedByteLength = fi.Length,
                ExpectedSha256 = hash
            });
        }
        if (!string.IsNullOrEmpty(inputVideoPath) && System.IO.File.Exists(inputVideoPath))
        {
            var fi = new System.IO.FileInfo(inputVideoPath);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = System.IO.File.OpenRead(inputVideoPath);
            string hash = Convert.ToHexString(sha.ComputeHash(stream));
            targets.Add(new PlannedArtifactTarget
            {
                Role = MediaArtifactKind.MotionVideo,
                ExpectedByteLength = fi.Length,
                ExpectedSha256 = hash
            });
        }
        return CleanSourceProtocolAsync(
            facts,
            actions,
            targets,
            inputImagePath,
            inputVideoPath,
            null,
            null,
            outputImagePath ?? throw new ArgumentNullException(nameof(outputImagePath)),
            outputVideoPath,
            cancellationToken);
    }

    internal static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolAsync(
        SourceMediaFacts facts,
        IReadOnlyList<PlannedCleanupAction> actions,
        IReadOnlyList<PlannedArtifactTarget>? targets,
        string inputImagePath,
        string? inputVideoPath,
        string? outputImagePath,
        string? outputVideoPath,
        CancellationToken cancellationToken = default) =>
        CleanSourceProtocolAsync(
            facts,
            actions,
            targets,
            inputImagePath,
            inputVideoPath,
            null,
            null,
            outputImagePath ?? throw new ArgumentNullException(nameof(outputImagePath)),
            outputVideoPath,
            cancellationToken);

    internal static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolAsync(
        SourceMediaFacts facts,
        IReadOnlyList<PlannedCleanupAction> actions,
        IReadOnlyList<PlannedArtifactTarget>? targets,
        string inputImagePath,
        string? inputVideoPath,
        string? cleanupSourcePath,
        PlannedArtifactTarget? cleanupSourceTarget,
        string outputImagePath,
        string? outputVideoPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            if (TestPostSnapshotHook != null)
            {
                unsafe
                {
                    delegate* unmanaged[Cdecl]<nint, void> fn = &NativePostSnapshotCallback;
                    TestCleanerSnapshotConfigurator?.Invoke(ctx, (nint)fn);
                }
            }
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(facts);

            int actionCount = actions?.Count ?? 0;
            Span<NativeCleanupAction> actionsBuf = actionCount > 0 ? stackalloc NativeCleanupAction[actionCount] : default;

            int targetCount = targets?.Count ?? 0;
            Span<NativeCleanupArtifactBinding> targetsBuf = targetCount > 0 ? stackalloc NativeCleanupArtifactBinding[targetCount] : default;

            unsafe
            {
                for (int i = 0; i < actionCount; i++)
                {
                    actionsBuf[i].StructSize = (uint)sizeof(NativeCleanupAction);
                    fixed (byte* pId = actionsBuf[i].ResidueId)
                        WriteFixedUtf8String(pId, 64, actions![i].ResidueId);
                    actionsBuf[i].OwnerProtocol = (int)actions[i].OwnerProtocol;
                    actionsBuf[i].ArtifactRole = (int)actions[i].ArtifactRole;
                    actionsBuf[i].StructureKind = (int)actions[i].StructureKind;
                    fixed (byte* pSel = actionsBuf[i].Selector)
                        WriteFixedUtf8String(pSel, 128, actions[i].Selector);
                    fixed (byte* pSem = actionsBuf[i].ExpectedSemantic)
                        WriteFixedUtf8String(pSem, 64, actions[i].ExpectedSemantic);
                    fixed (byte* pFp = actionsBuf[i].ExpectedFingerprint)
                        WriteFixedUtf8String(pFp, 64, actions[i].ExpectedFingerprint);
                    actionsBuf[i].CoordinateSpace = (int)actions[i].CoordinateSpace;
                    actionsBuf[i].RemovalMode = (int)actions[i].RemovalMode;
                    actionsBuf[i].IsMandatory = actions[i].IsMandatory ? 1 : 0;
                }

                for (int i = 0; i < targetCount; i++)
                {
                    targetsBuf[i].StructSize = (uint)sizeof(NativeCleanupArtifactBinding);
                    targetsBuf[i].ArtifactRole = (int)targets![i].Role;
                    targetsBuf[i].ExpectedLength = (ulong)targets[i].ExpectedByteLength;
                    if (string.IsNullOrWhiteSpace(targets[i].ExpectedSha256) || targets[i].ExpectedSha256.Length != 64)
                    {
                        throw new ArgumentException($"Target {targets[i].Role} must have a valid 64-character SHA-256.");
                    }
                    byte[] hashBytes = Convert.FromHexString(targets[i].ExpectedSha256);
                    if (hashBytes.Length != 32)
                    {
                        throw new ArgumentException($"Target {targets[i].Role} SHA-256 does not decode to 32 bytes.");
                    }
                    fixed (byte* pSha = targetsBuf[i].ExpectedSha256)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(hashBytes, 0, (nint)pSha, 32);
                    }
                    targetsBuf[i].HasExpectedSha256 = 1;
                }

                NativeCleanupArtifactBinding cleanupSourceBuf = default;
                NativeCleanupArtifactBinding* pCleanupSourceTarget = null;
                if (cleanupSourcePath != null || cleanupSourceTarget != null)
                {
                    if (cleanupSourcePath == null || cleanupSourceTarget == null ||
                        cleanupSourceTarget.Role != MediaArtifactKind.SourceContainer)
                    {
                        throw new ArgumentException(
                            "CleanupSource path and a SourceContainer cleanup target must be supplied together.");
                    }
                    if (cleanupSourceTarget.ExpectedByteLength <= 0 ||
                        string.IsNullOrWhiteSpace(cleanupSourceTarget.ExpectedSha256) ||
                        cleanupSourceTarget.ExpectedSha256.Length != 64)
                    {
                        throw new ArgumentException("CleanupSource must have a positive length and a valid SHA-256.");
                    }

                    cleanupSourceBuf.StructSize = (uint)sizeof(NativeCleanupArtifactBinding);
                    cleanupSourceBuf.ArtifactRole = (int)cleanupSourceTarget.Role;
                    cleanupSourceBuf.ExpectedLength = (ulong)cleanupSourceTarget.ExpectedByteLength;
                    byte[] cleanupSourceHashBytes = Convert.FromHexString(cleanupSourceTarget.ExpectedSha256);
                    if (cleanupSourceHashBytes.Length != 32)
                    {
                        throw new ArgumentException("CleanupSource SHA-256 does not decode to 32 bytes.");
                    }
                    for (int i = 0; i < cleanupSourceHashBytes.Length; i++)
                    {
                        cleanupSourceBuf.ExpectedSha256[i] = cleanupSourceHashBytes[i];
                    }
                    cleanupSourceBuf.HasExpectedSha256 = 1;
                    pCleanupSourceTarget = &cleanupSourceBuf;
                }

                Span<NativeRemovedProtocolFact> factsBuf = stackalloc NativeRemovedProtocolFact[256];
                fixed (NativeCleanupAction* pActions = actionsBuf)
                fixed (NativeCleanupArtifactBinding* pTargets = targetsBuf)
                fixed (NativeRemovedProtocolFact* pFacts = factsBuf)
                {
                    for (int i = 0; i < factsBuf.Length; i++)
                    {
                        pFacts[i].StructSize = (uint)sizeof(NativeRemovedProtocolFact);
                    }

                    NativeResult res;
                    nuint outCount;
                    if (cleanupSourcePath != null)
                    {
                        res = NativeMethods.CleanSourceProtocolWithPlanAndCleanupSource(
                            ctx.Handle,
                            in nativeFacts,
                            pActions,
                            (nuint)actionCount,
                            pTargets,
                            (nuint)targetCount,
                            inputImagePath,
                            inputVideoPath,
                            cleanupSourcePath,
                            pCleanupSourceTarget,
                            outputImagePath,
                            outputVideoPath,
                            pFacts,
                            (nuint)factsBuf.Length,
                            out outCount);
                    }
                    else
                    {
                        res = NativeMethods.CleanSourceProtocolWithPlan(
                            ctx.Handle,
                            in nativeFacts,
                            pActions,
                            (nuint)actionCount,
                            pTargets,
                            (nuint)targetCount,
                            inputImagePath,
                            inputVideoPath,
                            outputImagePath,
                            outputVideoPath,
                            pFacts,
                            (nuint)factsBuf.Length,
                            out outCount);
                    }

                    if (res != NativeResult.Ok)
                    {
                        string? msg = ctx.GetLastError();
                        if (msg != null && msg.Contains("TOCTOU", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new CleanerException(
                                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                                CleanerFailureStage.Staging,
                                facts.Protocol,
                                msg);
                        }
                        if (msg != null && (msg.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) || msg.Contains("artifact target", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new CleanerException(
                                CleanerFailureCategory.AuthorizedResidueAmbiguous,
                                CleanerFailureStage.Staging,
                                facts.Protocol,
                                msg);
                        }
                        ctx.ThrowIfFailed(res);
                    }

                    var factsList = new List<RemovedProtocolFact>();
                    int count = Math.Min((int)outCount, factsBuf.Length);
                    for (int i = 0; i < count; i++)
                    {
                        string proto = ReadFixedUtf8String(pFacts[i].ProtocolName, 64);
                        string comp = ReadFixedUtf8String(pFacts[i].Component, 64);
                        string desc = ReadFixedUtf8String(pFacts[i].Description, 128);
                        string residueId = ReadFixedUtf8String(pFacts[i].ResidueId, 64);
                        string op = ReadFixedUtf8String(pFacts[i].Operation, 64);
                        string beforeFp = ReadFixedUtf8String(pFacts[i].BeforeFingerprint, 64);
                        string afterSt = ReadFixedUtf8String(pFacts[i].AfterStatus, 64);

                        factsList.Add(new RemovedProtocolFact
                        {
                            ProtocolName = string.IsNullOrEmpty(proto) ? facts.Protocol.ToString() : proto,
                            Component = comp,
                            Description = desc,
                            ResidueId = string.IsNullOrEmpty(residueId) ? null : residueId,
                            ArtifactRole = (MediaArtifactKind)pFacts[i].ArtifactRole,
                            StructureKind = (ResidueStructureKind)pFacts[i].StructureKind,
                            Operation = string.IsNullOrEmpty(op) ? "Strip" : op,
                            BeforeFingerprint = string.IsNullOrEmpty(beforeFp) ? null : beforeFp,
                            AfterStatus = string.IsNullOrEmpty(afterSt) ? "Removed" : afterSt
                        });
                    }

                    return (IReadOnlyList<RemovedProtocolFact>)factsList;
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Plan-authorized destructive clean (P3).  The Native side derives every
    /// fact, action and target from the cleanup-plan record; this method only
    /// passes the plan token, generation, and input/output paths.  A plan on a
    /// harness context is dispatched to the test-harness Native build.
    /// </summary>
    internal static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolWithCleanupPlanAsync(
        CleanupPlanAttempt attempt,
        string inputImagePath,
        string? inputVideoPath,
        string? cleanupSourcePath,
        string outputImagePath,
        string? outputVideoPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(inputImagePath);
        ArgumentNullException.ThrowIfNull(outputImagePath);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            if (TestPostSnapshotHook != null)
            {
                unsafe
                {
                    delegate* unmanaged[Cdecl]<nint, void> fn = &NativePostSnapshotCallback;
                    TestCleanerSnapshotHandleConfigurator?.Invoke(attempt.ContextLease.Handle, (nint)fn);
                }
            }
            Span<NativeRemovedProtocolFact> factsBuf = stackalloc NativeRemovedProtocolFact[256];
            unsafe
            {
                fixed (NativeRemovedProtocolFact* pFacts = factsBuf)
                {
                    for (int i = 0; i < factsBuf.Length; i++)
                    {
                        pFacts[i].StructSize = (uint)sizeof(NativeRemovedProtocolFact);
                    }

                    nuint outCount = 0;
                    NativeResult res = attempt.UseHarnessLibrary
                        ? TestHarnessNativeMethods.CleanSourceProtocolWithCleanupPlan(
                            attempt.ContextLease.Handle,
                            attempt.NativeHandle,
                            attempt.Generation,
                            inputImagePath,
                            inputVideoPath,
                            cleanupSourcePath,
                            outputImagePath,
                            outputVideoPath,
                            pFacts,
                            (nuint)factsBuf.Length,
                            out outCount)
                        : NativeMethods.CleanSourceProtocolWithCleanupPlan(
                            attempt.ContextLease.Handle,
                            attempt.NativeHandle,
                            attempt.Generation,
                            inputImagePath,
                            inputVideoPath,
                            cleanupSourcePath,
                            outputImagePath,
                            outputVideoPath,
                            pFacts,
                            (nuint)factsBuf.Length,
                            out outCount);

                    if (res != NativeResult.Ok)
                    {
                        string? msg = ReadLastError(attempt.ContextLease.Handle, attempt.UseHarnessLibrary);
                        if (msg != null &&
                            (msg.Contains("ObjectIdentity", StringComparison.OrdinalIgnoreCase) ||
                             msg.Contains("TOCTOU", StringComparison.OrdinalIgnoreCase) ||
                             msg.Contains("SourceChanged", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new CleanerException(
                                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                                CleanerFailureStage.ArtifactVerification,
                                SourceProtocol.Unknown,
                                msg);
                        }
                        if (res == NativeResult.AuthorityViolation)
                        {
                            throw new CleanerException(
                                CleanerFailureCategory.NativeAuthorityViolation,
                                CleanerFailureStage.Authorization,
                                SourceProtocol.Unknown,
                                msg ?? "Native rejected the cleanup-plan authority.");
                        }
                        if (res == NativeResult.PlanReplayed)
                        {
                            throw new CleanerException(
                                CleanerFailureCategory.CleanupAuthorizationMissing,
                                CleanerFailureStage.Authorization,
                                SourceProtocol.Unknown,
                                msg ?? "The Native cleanup plan was replayed, consumed, or released.");
                        }
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(msg)
                                ? $"Native cleanup failed with result: {res}"
                                : $"Native cleanup failed ({res}): {msg}");
                    }

                    var factsList = new List<RemovedProtocolFact>();
                    int count = Math.Min((int)outCount, factsBuf.Length);
                    for (int i = 0; i < count; i++)
                    {
                        string proto = ReadFixedUtf8String(pFacts[i].ProtocolName, 64);
                        string comp = ReadFixedUtf8String(pFacts[i].Component, 64);
                        string desc = ReadFixedUtf8String(pFacts[i].Description, 128);
                        string residueId = ReadFixedUtf8String(pFacts[i].ResidueId, 64);
                        string op = ReadFixedUtf8String(pFacts[i].Operation, 64);
                        string beforeFp = ReadFixedUtf8String(pFacts[i].BeforeFingerprint, 64);
                        string afterSt = ReadFixedUtf8String(pFacts[i].AfterStatus, 64);

                        factsList.Add(new RemovedProtocolFact
                        {
                            ProtocolName = proto,
                            Component = comp,
                            Description = desc,
                            ResidueId = string.IsNullOrEmpty(residueId) ? null : residueId,
                            ArtifactRole = (MediaArtifactKind)pFacts[i].ArtifactRole,
                            StructureKind = (ResidueStructureKind)pFacts[i].StructureKind,
                            Operation = string.IsNullOrEmpty(op) ? "Strip" : op,
                            BeforeFingerprint = string.IsNullOrEmpty(beforeFp) ? null : beforeFp,
                            AfterStatus = string.IsNullOrEmpty(afterSt) ? "Removed" : afterSt
                        });
                    }

                    return (IReadOnlyList<RemovedProtocolFact>)factsList;
                }
            }
        }, cancellationToken);
    }

    /// <summary>Native-captured identity of one staged (pre-commit) cleaner output.</summary>
    internal readonly record struct CleanStagedOutputRecord(
        MediaArtifactKind ArtifactRole,
        uint AuxiliaryIndex,
        uint VolumeSerial,
        ulong FileIndex,
        ulong FileSize,
        uint LinkCount,
        string FinalPath);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileIdentity
    {
        public uint VolumeSerial;
        public ulong FileIndex;
        public ulong FileSize;
        public uint LinkCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeCleanStagedOutputRecord
    {
        public uint StructSize;
        public int ArtifactRole;
        public uint AuxiliaryIndex;
        public NativeFileIdentity Identity;
        public fixed byte FinalPath[1024];
    }

    /// <summary>
    /// Reads the staged-output ownership registry that the Native cleaner
    /// populated from the creating handles (never from pathname re-opens).
    /// The Managed layer may only ever roll back / publish objects that this
    /// registry reports: a file that merely appears inside the staging
    /// directory is foreign until the Native side proves it created it.
    /// </summary>
    internal static IReadOnlyList<CleanStagedOutputRecord> QueryStagedOutputs(
        nint contextHandle,
        bool useHarnessLibrary)
    {
        var result = new List<CleanStagedOutputRecord>();
        if (contextHandle == nint.Zero)
        {
            return result;
        }

        try
        {
            unsafe
            {
                nuint count = useHarnessLibrary
                    ? TestHarnessNativeMethods.CleanGetStagedOutputs(contextHandle, nint.Zero, 0)
                    : NativeMethods.CleanGetStagedOutputs(contextHandle, nint.Zero, 0);
                if (count == 0)
                {
                    return result;
                }

                var buf = new NativeCleanStagedOutputRecord[count];
                fixed (NativeCleanStagedOutputRecord* p = buf)
                {
                    nuint written = useHarnessLibrary
                        ? TestHarnessNativeMethods.CleanGetStagedOutputs(contextHandle, (nint)p, count)
                        : NativeMethods.CleanGetStagedOutputs(contextHandle, (nint)p, count);
                    for (nuint i = 0; i < written && i < count; i++)
                    {
                        NativeCleanStagedOutputRecord* pRec = &p[i];
                        string path = ReadFixedUtf8String(pRec->FinalPath, 1024);
                        result.Add(new CleanStagedOutputRecord(
                            (MediaArtifactKind)pRec->ArtifactRole,
                            pRec->AuxiliaryIndex,
                            pRec->Identity.VolumeSerial,
                            pRec->Identity.FileIndex,
                            pRec->Identity.FileSize,
                            pRec->Identity.LinkCount,
                            path));
                    }
                }
            }
        }
        catch
        {
            // Ownership registry is best-effort diagnostics; callers fail
            // closed when a required registry entry is absent.
        }

        return result;
    }

    private static string? ReadLastError(nint contextHandle, bool useHarnessLibrary)
    {
        try
        {
            Span<byte> buf = stackalloc byte[512];
            unsafe
            {
                fixed (byte* pBuf = buf)
                {
                    // The error buffer lives in the same Native build that
                    // executed the operation; reading it through the other
                    // build is cross-DLL ABI garbage.
                    nuint required = 0;
                    NativeResult res = useHarnessLibrary
                        ? TestHarnessNativeMethods.GetLastError(contextHandle, (nint)pBuf, (nuint)buf.Length, out required)
                        : NativeMethods.GetLastError(contextHandle, (nint)pBuf, (nuint)buf.Length, out required);
                    if (res == NativeResult.Ok && required > 0)
                    {
                        int len = 0;
                        while (len < (int)required && buf[len] != 0) len++;
                        return Encoding.UTF8.GetString(buf[..len]);
                    }
                }
            }
        }
        catch
        {
            // Best effort.
        }
        return null;
    }

    private static unsafe void WriteFixedUtf8String(byte* ptr, int maxLen, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            ptr[0] = 0;
            return;
        }
        int written = Encoding.UTF8.GetBytes(value, new Span<byte>(ptr, maxLen - 1));
        ptr[written] = 0;
    }

    private static unsafe string ReadFixedUtf8String(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0) len++;
        return len > 0 ? Encoding.UTF8.GetString(ptr, len) : string.Empty;
    }
}
