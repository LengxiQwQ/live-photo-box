using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Interop;

internal sealed record HuaweiRepairObservation(bool Supported, uint Changed, uint Pending,
    uint ImageOrientation, uint MainRotation, uint Reflection, uint MetadataChanges, string Details,
    NativeMediaService.NativeRegisteredOutputEvidence? Evidence);

internal static class NativeHuaweiRepair
{
    internal sealed record Attempt(HuaweiRepairObservation? Observation, Exception? Failure);
    internal static async Task<HuaweiRepairObservation> RunAsync(string source, string? output, uint actions, CancellationToken token)
    {
        var attempt = await TryRunAsync(source, output, actions, token).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Observation!;
    }
    internal static Task<Attempt> TryRunAsync(string source, string? output, uint actions, CancellationToken token)
        => Task.Run(() => Run(source, output, actions, token), CancellationToken.None);
    private static unsafe Attempt Run(string source, string? output, uint actions, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        using var protection = new NeutralArtifactReadLeaseSet();
        if (protection.TryPin(new MediaArtifact { Path = source, Kind = MediaArtifactKind.PrimaryImage }) is { } protectionFailure)
            return new(null, protectionFailure);
        using var input = SourceReadAttempt.Open(source);
        if (!input.Succeeded) return new(null, new IOException($"Cannot read repair source (system error {input.Error})."));
        if (!WindowsFileIdentity.TryCapture(input.Handle, out var identity, out int error) || !input.TryGetLength(out long length, out error))
            return new(null, new IOException($"Cannot verify repair source (system error {error})."));
        // Huawei and Samsung v1 requests intentionally share the same POD layout.
        var request = new NativeSamsungRepairRequest
        {
            StructSize = (uint)sizeof(NativeSamsungRepairRequest), ApiVersion = 1, Actions = actions,
            ExpectedIdentity = new NativeFileIdentity { VolumeSerial = identity!.VolumeSerialNumber,
                FileIndex = identity.FileIndex, FileSize = (ulong)length, LinkCount = identity.LinkCount }
        };
        var sourceHash = MediaFileHashAttempt.Compute(source, token);
        if (sourceHash.Failure is { } hashFailure) return new(null, hashFailure);
        byte[] hash = Convert.FromHexString(sourceHash.Sha256!);
        for (int i = 0; i < hash.Length; ++i) request.SourceSha256[i] = hash[i];
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        var result = new NativeHuaweiRepairResult { StructSize = (uint)sizeof(NativeHuaweiRepairResult), ApiVersion = 1 };
        using var context = NativeContext.Create(token);
        var status = NativeMethods.RepairHuawei(context.Handle, source, output, ref request, ref result);
        var receipts = output != null
            ? NativeMediaService.CaptureHuaweiRepairOutputs(context, Path.GetDirectoryName(Path.GetFullPath(output))!, output) : null;
        NativeMediaService.NativeRegisteredOutputEvidence? evidence = null;
        if (status == NativeResult.Ok && output != null && result.Changed != 0 &&
            !receipts!.TryGetValue(Path.GetFullPath(output), out evidence))
            return new(null, new InvalidDataException("Huawei repair did not register its complete output."));
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        // Expected unsupported files are ordinary item results, not exceptions.
        if (status is NativeResult.Unsupported or NativeResult.InvalidArgument or NativeResult.SourceChanged)
            return new(new(false, 0, actions, 0, 0, 0, 0, context.GetLastError() ?? "Huawei source could not be safely processed.", null), null);
        if (status != NativeResult.Ok) return new(null, context.CreateFailure(status));
        return new(new(true, result.Changed, result.Pending, result.ImageOrientation, result.MainRotation,
            result.Reflection, result.MetadataChanges, Marshal.PtrToStringUTF8((nint)result.Details) ?? "", evidence), null);
    }
}
