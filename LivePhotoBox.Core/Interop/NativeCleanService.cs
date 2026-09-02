using System;
using System.Collections.Generic;
using System.IO;
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

            Span<byte> removedBuf = stackalloc byte[1024];
            NativeResult res;
            unsafe
            {
                fixed (byte* pBuf = removedBuf)
                {
                    res = NativeMethods.CleanSourceProtocol(
                        ctx.Handle,
                        in nativeFacts,
                        inputImagePath,
                        inputVideoPath,
                        outputImagePath,
                        outputVideoPath,
                        pBuf,
                        (nuint)removedBuf.Length);
                }
            }

            ctx.ThrowIfFailed(res);

            int nullIdx = removedBuf.IndexOf((byte)0);
            if (nullIdx < 0) nullIdx = removedBuf.Length;
            string removedRaw = Encoding.UTF8.GetString(removedBuf[..nullIdx]);

            var factsList = new List<RemovedProtocolFact>();
            if (!string.IsNullOrWhiteSpace(removedRaw))
            {
                var items = removedRaw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var item in items)
                {
                    string trimmed = item.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    string proto = facts.Protocol.ToString();
                    string comp = "Container";
                    if (trimmed.Contains("Xmp", StringComparison.OrdinalIgnoreCase)) comp = "XMP";
                    else if (trimmed.Contains("MakerNote", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Exif", StringComparison.OrdinalIgnoreCase)) comp = "EXIF/MakerNote";
                    else if (trimmed.Contains("Mp4", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Box", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Track", StringComparison.OrdinalIgnoreCase)) comp = "Video Track/Box";
                    else if (trimmed.Contains("Tail", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Trailer", StringComparison.OrdinalIgnoreCase)) comp = "Trailer";

                    factsList.Add(new RemovedProtocolFact
                    {
                        ProtocolName = proto,
                        Component = comp,
                        Description = trimmed
                    });
                }
            }

            return (IReadOnlyList<RemovedProtocolFact>)factsList;
        }, cancellationToken);
    }
}
