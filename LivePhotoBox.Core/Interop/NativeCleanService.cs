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

            Span<NativeRemovedProtocolFact> factsBuf = stackalloc NativeRemovedProtocolFact[64];
            unsafe
            {
                fixed (NativeRemovedProtocolFact* pFacts = factsBuf)
                {
                    for (int i = 0; i < factsBuf.Length; i++)
                    {
                        pFacts[i].StructSize = (uint)sizeof(NativeRemovedProtocolFact);
                    }

                    NativeResult res = NativeMethods.CleanSourceProtocol(
                        ctx.Handle,
                        in nativeFacts,
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

                        string? matchedResidueId = null;
                        MediaArtifactKind? matchedRole = null;
                        ResidueStructureKind? matchedKind = null;

                        if (facts.ConfirmedResidues != null)
                        {
                            foreach (var resItem in facts.ConfirmedResidues)
                            {
                                if (desc.Contains(resItem.Selector, StringComparison.OrdinalIgnoreCase) ||
                                    comp.Contains(resItem.Selector, StringComparison.OrdinalIgnoreCase) ||
                                    (resItem.ExpectedSemantic != null && desc.Contains(resItem.ExpectedSemantic, StringComparison.OrdinalIgnoreCase)))
                                {
                                    matchedResidueId = resItem.Id;
                                    matchedRole = resItem.ArtifactRole;
                                    matchedKind = resItem.StructureKind;
                                    break;
                                }
                            }
                        }

                        factsList.Add(new RemovedProtocolFact
                        {
                            ProtocolName = string.IsNullOrEmpty(proto) ? facts.Protocol.ToString() : proto,
                            Component = comp,
                            Description = desc,
                            ResidueId = matchedResidueId,
                            ArtifactRole = matchedRole,
                            StructureKind = matchedKind,
                            Operation = "Strip",
                            AfterStatus = "Removed"
                        });
                    }

                    return (IReadOnlyList<RemovedProtocolFact>)factsList;
                }
            }
        }, cancellationToken);
    }

    private static unsafe string ReadFixedUtf8String(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0) len++;
        return len > 0 ? Encoding.UTF8.GetString(ptr, len) : string.Empty;
    }
}
