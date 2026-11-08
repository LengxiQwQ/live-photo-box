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
        var result = TryFromCleanup(attempt, path);
        if (result.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result.Primary;
    }

    internal sealed record HandoffAttempt(NativeCompoundPrimary? Primary, Exception? Failure);

    internal static HandoffAttempt TryFromCleanup(CleanupPlanAttempt attempt, string path)
    {
        if (attempt.UseHarnessLibrary)
            return new(null, new NotSupportedException("Compound lineage requires the production Native cleanup owner."));
        NativeResult result = NativeMethods.CompoundFromCleanup(attempt.ContextLease.Handle,
            attempt.NativeHandle, attempt.Generation, path, out ulong token);
        if (result == NativeResult.Unsupported) return new(null, null);
        if (result != NativeResult.Ok || token == 0)
            return new(null, new IOException("Native compound lineage handoff failed: " + result));
        return new(new NativeCompoundPrimary(token), null);
    }
    internal (SourceMediaFacts Facts, NativeCompoundPrimaryObservation Observation) Inspect(string path, CancellationToken token)
    {
        var attempt = TryInspect(path, token);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return (attempt.Facts!, attempt.Observation);
    }

    internal sealed record InspectionAttempt(SourceMediaFacts? Facts, NativeCompoundPrimaryObservation Observation, Exception? Failure);

    internal unsafe InspectionAttempt TryInspect(string path, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, default, new OperationCanceledException(token));
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
            if (token.IsCancellationRequested || result == NativeResult.Cancelled) return new(null, default, new OperationCanceledException(token));
            if (result != NativeResult.Ok) return new(null, default, new IOException("Compound primary inspection rejected current object: " + result + "; " + context.GetLastError()));
            return new(NativeMediaService.MapFromNativeFacts(facts), observation, null);
        }
        finally { if (retained) DangerousRelease(); }
    }
    internal static unsafe string OpaqueHash(NativeCompoundPrimaryObservation observation) =>
        Convert.ToHexString(new ReadOnlySpan<byte>(observation.OpaqueSha256, 32));
    internal MediaArtifact MaterializeOpaqueRecords(string source, string output, CancellationToken token)
    {
        var attempt = TryMaterializeOpaqueRecords(source, output, token);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Artifact!;
    }

    internal sealed record MaterializationAttempt(MediaArtifact? Artifact, Exception? Failure);

    internal MaterializationAttempt TryMaterializeOpaqueRecords(string source, string output, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        using var context = NativeContext.Create(token);
        using var operation = context.AcquireOperationLease();
        bool retained = false;
        NativeMediaService.NativeRegisteredOutputEvidence? actual = null;
        bool published = false;
        try
        {
            DangerousAddRef(ref retained);
            NativeResult result = NativeMethods.CompoundMaterializeRecords(operation.Handle,
                unchecked((ulong)DangerousGetHandle()), source, output);
            if (result != NativeResult.Ok) return new(null, context.CreateFailure(result));
            published = true;
            actual = NativeMediaService.CaptureRegisteredOutputEvidence(context, output, MediaArtifactKind.MetadataCarrier);
            if (token.IsCancellationRequested)
            {
                bool cleaned = NativeMediaService.TryDeleteRegisteredOutput(output, actual);
                return new(null, cleaned ? new OperationCanceledException(token)
                    : new IOException("Cannot clean the cancelled identity-bound opaque record artifact."));
            }
            return new(new MediaArtifact { Path = output, Kind = MediaArtifactKind.MetadataCarrier,
                ByteLength = actual.ByteLength, Sha256 = actual.Sha256, FileIdentity = actual.FileIdentity }, null);
        }
        catch
        {
            if (published && !NativeMediaService.TryDeleteRegisteredOutput(output, actual))
                throw new IOException("Cannot clean the failed identity-bound opaque record artifact.");
            throw;
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
        var attempt = TryInspectGainMap(path, materialized, token);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Metadata!.Value;
    }
    internal NativeMediaService.GainMapMetadataAttempt TryInspectGainMap(string path, string? materialized, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        using var context = NativeContext.Create(token);using var operation = context.AcquireOperationLease();
        bool retained = false;
        try
        {
            DangerousAddRef(ref retained);
            var metadata = new NativeGainMapMetadataV1 { StructSize = (uint)Marshal.SizeOf<NativeGainMapMetadataV1>(), ApiVersion = 1 };
            NativeResult result = NativeMethods.CompoundGainMap(operation.Handle, unchecked((ulong)DangerousGetHandle()), path, materialized, ref metadata);
            if (result != NativeResult.Ok) return new(null, context.CreateFailure(result));
            if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
            if (metadata.Kind is not (1 or 2)) return new(null, new InvalidDataException("Compound GainMap metadata kind is unknown."));
            return new(metadata, null);
        }
        finally { if (retained) DangerousRelease(); }
    }
}

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "lpb_compound_primary_materialize_records_v1", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeResult CompoundMaterializeRecords(nint context, ulong proof, string source, string output);
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
