using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeMediaCandidateData
{
    public uint StructSize, ApiVersion, Flags, Protocol, Role, ImageContainer, VideoContainer, Reserved;
    public NativeFileIdentity Identity;
    public ulong FileSize, BytesRead, MotionOffset, MotionLength;
    public fixed byte Identifier[128];
}

// Discovery only: deliberately contains no content hash or processing authority.
internal sealed record DiscoveryPairingCandidate(SourceProtocol Protocol, uint Role, string Identifier);
internal enum DiscoveryCapability : uint { AppleId = 1, VivoId = 2, SingleFileMarker = 4 }
internal enum FactState { NotRequested, Found, NotFound, Incomplete, ReadFailed }
internal sealed record MediaCandidate(DiscoveryPairingCandidate? Pairing, ulong FileSize, ulong BytesRead,
    ImageContainer ImageContainer, VideoContainer VideoContainer, FactState State = FactState.NotFound,
    SourceProtocol SingleFileProtocol = SourceProtocol.Unknown, string? Diagnostic = null, ulong ReadCount = 0);

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_discover_media_candidate_v2", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult DiscoverMediaCandidate(nint context, string path, uint capabilities, ref NativeMediaCandidateData candidate);
}

internal static class NativeMediaCandidate
{
    internal static Task<MediaCandidate?> ReadAsync(string path, CancellationToken token, DiscoveryCapability capability = DiscoveryCapability.AppleId)
        => Task.Run(() => Read(path, token, capability), CancellationToken.None);

    private static unsafe MediaCandidate? Read(string path, CancellationToken token, DiscoveryCapability capability)
    {
        if (sizeof(NativeMediaCandidateData) != 224 || Marshal.OffsetOf<NativeMediaCandidateData>(nameof(NativeMediaCandidateData.Identifier)).ToInt32() != 96)
            throw new InvalidOperationException("Media candidate ABI layout mismatch.");
        if (token.IsCancellationRequested) return null;
        using var context = NativeContext.Create(token);
        var value = new NativeMediaCandidateData { StructSize = (uint)sizeof(NativeMediaCandidateData), ApiVersion = 2 };
        var result = NativeMethods.DiscoverMediaCandidate(context.Handle, path, (uint)capability, ref value);
        if (result is NativeResult.InvalidArgument or NativeResult.Unsupported)
            return new(null, value.FileSize, value.BytesRead, ImageContainer.Unknown, VideoContainer.Unknown,
                result == NativeResult.Unsupported ? FactState.Incomplete : FactState.ReadFailed,
                Diagnostic: "Native discovery could not complete the requested capability; no full-inspection fallback.");
        if (result == NativeResult.Cancelled || token.IsCancellationRequested) return null;
        if (result != NativeResult.Ok)
            return new(null, value.FileSize, value.BytesRead, ImageContainer.Unknown, VideoContainer.Unknown,
                FactState.ReadFailed, Diagnostic: context.CreateFailure(result).Message);
        if (value.StructSize != sizeof(NativeMediaCandidateData) || value.ApiVersion != 2 ||
            (value.Flags & 1) == 0 || (value.Flags & ~31u) != 0 || value.BytesRead > 4UL * 1024 * 1024)
            throw new InvalidOperationException("Native returned an invalid discovery candidate.");
        DiscoveryPairingCandidate? pair = null;
        if ((value.Flags & 2) != 0)
        {
            if (value.Role is not (1 or 2) || (value.Protocol != (uint)SourceProtocol.AppleLivePhoto && value.Protocol != (uint)SourceProtocol.VivoLegacyDualFile) ||
                value.Identifier[0] == 0 || value.Identifier[127] != 0)
                throw new InvalidOperationException("Native returned an invalid pairing candidate.");
            pair = new((SourceProtocol)value.Protocol, value.Role, Marshal.PtrToStringUTF8((nint)value.Identifier) ?? "");
        }
        return new(pair, value.FileSize, value.BytesRead, (ImageContainer)value.ImageContainer, (VideoContainer)value.VideoContainer,
            (value.Flags & 16) != 0 ? FactState.ReadFailed : (value.Flags & 8) != 0 ? FactState.Incomplete : (value.Flags & 6) != 0 ? FactState.Found : FactState.NotFound,
            (value.Flags & 4) != 0 ? (SourceProtocol)value.Protocol : SourceProtocol.Unknown,
            (value.Flags & 24) != 0 ? "Discovery budget, structure or read boundary was incomplete; no full-inspection fallback." : null, value.Reserved);
    }
}
