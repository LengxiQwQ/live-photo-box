using System;
using System.IO;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NativeCompoundPrimaryObservation
{
    public uint StructSize, Version;
    public ulong KnownLength, OpaqueLength;
    public fixed byte OpaqueSha256[32];
}

// A Native-owned lineage lease. It is never reconstructed from a serialized
// carrier, manifest, pathname or observation. It owns no file handle after
// inspection, and every consumer rechecks the actual creating-object identity.
internal sealed class NativeCompoundPrimary : SafeHandleZeroOrMinusOneIsInvalid
{
    private const long NativeBytes = 19000;
    private NativeCompoundPrimary(ulong token) : base(true)
    { SetHandle(unchecked((nint)token)); GC.AddMemoryPressure(NativeBytes); }
    internal static NativeCompoundPrimary? FromCleanup(CleanupPlanAttempt attempt, string path)
    {
        if (attempt.UseHarnessLibrary)
            throw new NotSupportedException("Compound lineage requires the production Native cleanup owner.");
        NativeResult result = NativeMethods.CompoundFromCleanup(attempt.ContextLease.Handle,
            attempt.NativeHandle, attempt.Generation, path, out ulong token);
        if (result == NativeResult.Unsupported) return null;
        if (result != NativeResult.Ok || token == 0)
            throw new IOException("Native compound lineage handoff failed: " + result);
        return new NativeCompoundPrimary(token);
    }
    internal unsafe (SourceMediaFacts Facts, NativeCompoundPrimaryObservation Observation) Inspect(string path, CancellationToken token)
    {
        using var context = NativeContext.Create(token);
        using var operation = context.AcquireOperationLease();
        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            NativeSourceMediaFacts facts = default; facts.StructSize = (uint)sizeof(NativeSourceMediaFacts);
            var observation = new NativeCompoundPrimaryObservation { StructSize = (uint)sizeof(NativeCompoundPrimaryObservation), Version = 1 };
            NativeResult result = NativeMethods.CompoundInspect(operation.Handle, unchecked((ulong)DangerousGetHandle()),
                path, &facts, &observation);
            token.ThrowIfCancellationRequested();
            if (result != NativeResult.Ok) throw new IOException("Compound primary inspection rejected current object: " + result + "; " + context.GetLastError());
            return (NativeMediaService.MapFromNativeFacts(facts), observation);
        }
        finally { if (retained) DangerousRelease(); }
    }
    protected override bool ReleaseHandle()
    {
        try { return NativeMethods.CompoundRelease(unchecked((ulong)handle)) == NativeResult.Ok; }
        finally { GC.RemoveMemoryPressure(NativeBytes); }
    }
    internal NativeGainMapMetadataV1 InspectGainMap(string path, string? materialized, CancellationToken token)
    {
        using var context = NativeContext.Create(token);using var operation = context.AcquireOperationLease();
        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            var metadata = new NativeGainMapMetadataV1 { StructSize = (uint)Marshal.SizeOf<NativeGainMapMetadataV1>(), ApiVersion = 1 };
            NativeResult result = NativeMethods.CompoundGainMap(operation.Handle, unchecked((ulong)DangerousGetHandle()), path, materialized, ref metadata);
            token.ThrowIfCancellationRequested();context.ThrowIfFailed(result);
            if (metadata.Kind is not (1 or 2)) throw new InvalidDataException("Compound GainMap metadata kind is unknown.");
            return metadata;
        }
        finally { if (retained) DangerousRelease(); }
    }
}

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_compound_primary_gainmap_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CompoundGainMap(nint context, ulong proof, string path, string? materialized, ref NativeGainMapMetadataV1 metadata);
    [LibraryImport(LibraryName, EntryPoint = "lpb_compound_primary_from_cleanup_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CompoundFromCleanup(nint context, nint plan, ulong generation, string stage, out ulong proof);
    [LibraryImport(LibraryName, EntryPoint = "lpb_compound_primary_inspect_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial NativeResult CompoundInspect(nint context, ulong proof, string path, NativeSourceMediaFacts* facts, NativeCompoundPrimaryObservation* observed);
    [LibraryImport(LibraryName, EntryPoint = "lpb_compound_primary_release_v1")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CompoundRelease(ulong proof);
}
