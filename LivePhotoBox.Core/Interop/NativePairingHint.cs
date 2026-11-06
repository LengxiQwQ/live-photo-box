using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativePairingHintData
{
    public uint StructSize, ApiVersion, Protocol, Role;
    public NativeFileIdentity Identity;
    public fixed byte Sha256[32];
    public fixed byte Identifier[128];
}
internal sealed record PairingHint(SourceProtocol Protocol, uint Role, string Identifier, NativeFileIdentity Identity, string Sha256);
internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_discover_pairing_hint_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult DiscoverPairingHint(nint context, string path, ref NativePairingHintData hint);
}
internal static class NativePairingHint
{
    internal static Task<PairingHint?> ReadAsync(string path, CancellationToken token) => Task.Run(() => Read(path, token), token);
    private static unsafe PairingHint? Read(string path, CancellationToken token)
    {
        using var ctx = NativeContext.Create(token);
        var hint = new NativePairingHintData { StructSize = (uint)sizeof(NativePairingHintData), ApiVersion = 1 };
        var result = NativeMethods.DiscoverPairingHint(ctx.Handle, path, ref hint);
        if (result is NativeResult.InvalidArgument or NativeResult.Unsupported) return null;
        ctx.ThrowIfFailed(result);
        if (hint.Protocol == 0 || hint.Identifier[0] == 0) return null;
        return new((SourceProtocol)hint.Protocol, hint.Role, Marshal.PtrToStringUTF8((nint)hint.Identifier) ?? "", hint.Identity,
            Convert.ToHexString(new ReadOnlySpan<byte>(hint.Sha256, 32)));
    }
}
