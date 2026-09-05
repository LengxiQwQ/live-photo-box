using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Interop;

/// <summary>
/// Thin control plane service that invokes LivePhotoBox.Native execution plane protocol cleaning operations.
/// </summary>
public static class NativeCleanService
{
    public static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolAsync(
        SourceMediaFacts facts,
        IReadOnlyList<PlannedCleanupAction> actions,
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
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(facts);

            int actionCount = actions?.Count ?? 0;
            Span<NativeCleanupAction> actionsBuf = actionCount > 0 ? stackalloc NativeCleanupAction[actionCount] : default;
            unsafe
            {
                for (int i = 0; i < actionCount; i++)
                {
                    actionsBuf[i].StructSize = (uint)sizeof(NativeCleanupAction);
                    fixed (byte* pId = actionsBuf[i].ResidueId)
                        WriteFixedUtf8String(pId, 64, actions![i].ResidueId);
                    actionsBuf[i].ArtifactRole = (int)actions![i].ArtifactRole;
                    actionsBuf[i].StructureKind = (int)actions![i].StructureKind;
                    fixed (byte* pSel = actionsBuf[i].Selector)
                        WriteFixedUtf8String(pSel, 128, actions[i].Selector);
                    fixed (byte* pSem = actionsBuf[i].ExpectedSemantic)
                        WriteFixedUtf8String(pSem, 64, actions[i].ExpectedSemantic);
                    fixed (byte* pFp = actionsBuf[i].ExpectedFingerprint)
                        WriteFixedUtf8String(pFp, 64, actions[i].ExpectedFingerprint);
                    actionsBuf[i].RemovalMode = (int)actions[i].RemovalMode;
                    actionsBuf[i].IsMandatory = actions[i].IsMandatory ? 1 : 0;
                }

                Span<NativeRemovedProtocolFact> factsBuf = stackalloc NativeRemovedProtocolFact[64];
                fixed (NativeCleanupAction* pActions = actionsBuf)
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
                        inputImagePath,
                        inputVideoPath,
                        outputImagePath,
                        outputVideoPath,
                        pFacts,
                        (nuint)factsBuf.Length,
                        out nuint outCount);

                    ctx.ThrowIfFailed(res);

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

    public static Task<IReadOnlyList<RemovedProtocolFact>> CleanSourceProtocolAsync(
        SourceMediaFacts facts,
        string inputImagePath,
        string? inputVideoPath,
        string? outputImagePath,
        string? outputVideoPath,
        CancellationToken cancellationToken = default)
    {
        var actions = new List<PlannedCleanupAction>();
        if (facts.ConfirmedResidues != null)
        {
            foreach (var r in facts.ConfirmedResidues)
            {
                actions.Add(new PlannedCleanupAction
                {
                    ResidueId = r.Id,
                    ArtifactRole = r.ArtifactRole,
                    StructureKind = r.StructureKind,
                    Selector = r.Selector,
                    ExpectedSemantic = r.ExpectedSemantic,
                    RemovalMode = r.RemovalMode,
                    ExpectedFingerprint = r.ExpectedFingerprint,
                    IsMandatory = r.RequiredAfterExtraction
                });
            }
        }
        return CleanSourceProtocolAsync(facts, actions, inputImagePath, inputVideoPath, outputImagePath, outputVideoPath, cancellationToken);
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
