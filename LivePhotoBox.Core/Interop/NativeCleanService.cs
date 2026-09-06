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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativePostSnapshotCallback(nint userData)
    {
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
        return CleanSourceProtocolAsync(facts, actions, targets, inputImagePath, inputVideoPath, outputImagePath, outputVideoPath, cancellationToken);
    }

    internal static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolAsync(
        SourceMediaFacts facts,
        IReadOnlyList<PlannedCleanupAction> actions,
        IReadOnlyList<PlannedArtifactTarget>? targets,
        string inputImagePath,
        string? inputVideoPath,
        string? outputImagePath,
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
                    NativeMethods.TestSetCleanerSnapshotHook(ctx.Handle, (nint)fn, nint.Zero);
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

                Span<NativeRemovedProtocolFact> factsBuf = stackalloc NativeRemovedProtocolFact[64];
                fixed (NativeCleanupAction* pActions = actionsBuf)
                fixed (NativeCleanupArtifactBinding* pTargets = targetsBuf)
                fixed (NativeRemovedProtocolFact* pFacts = factsBuf)
                {
                    for (int i = 0; i < factsBuf.Length; i++)
                    {
                        pFacts[i].StructSize = (uint)sizeof(NativeRemovedProtocolFact);
                    }

                    NativeResult res = NativeMethods.CleanSourceProtocolWithPlan(
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
                        out nuint outCount);

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
