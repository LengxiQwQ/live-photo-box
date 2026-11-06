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
    internal static Task<HuaweiRepairObservation> RunAsync(string source, string? output, uint actions, CancellationToken token)
        => Task.Run(() => Run(source, output, actions, token), token);

    private static unsafe HuaweiRepairObservation Run(string source, string? output, uint actions, CancellationToken token)
    {
        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var identity = WindowsFileIdentity.Capture(input.SafeFileHandle);
        // Huawei and Samsung v1 requests intentionally share the same POD layout.
        var request = new NativeSamsungRepairRequest
        {
            StructSize = (uint)sizeof(NativeSamsungRepairRequest), ApiVersion = 1, Actions = actions,
            ExpectedIdentity = new NativeFileIdentity { VolumeSerial = identity.VolumeSerialNumber,
                FileIndex = identity.FileIndex, FileSize = (ulong)input.Length, LinkCount = identity.LinkCount }
        };
        byte[] hash = SHA256.HashData(input);
        for (int i = 0; i < hash.Length; ++i) request.SourceSha256[i] = hash[i];
        token.ThrowIfCancellationRequested();
        var result = new NativeHuaweiRepairResult { StructSize = (uint)sizeof(NativeHuaweiRepairResult), ApiVersion = 1 };
        using var context = NativeContext.Create(token);
        var status = NativeMethods.RepairHuawei(context.Handle, source, output, ref request, ref result);
        var receipts = output != null
            ? NativeMediaService.CaptureHuaweiRepairOutputs(context, Path.GetDirectoryName(Path.GetFullPath(output))!, output) : null;
        NativeMediaService.NativeRegisteredOutputEvidence? evidence = null;
        if (status == NativeResult.Ok && output != null && result.Changed != 0 &&
            !receipts!.TryGetValue(Path.GetFullPath(output), out evidence))
            throw new InvalidDataException("Huawei repair did not register its complete output.");
        token.ThrowIfCancellationRequested();
        // Expected unsupported files are ordinary item results, not exceptions.
        if (status is NativeResult.Unsupported or NativeResult.InvalidArgument or NativeResult.SourceChanged)
            return new(false, 0, actions, 0, 0, 0, 0, context.GetLastError() ?? "Huawei source could not be safely processed.", null);
        context.ThrowIfFailed(status);
        return new(true, result.Changed, result.Pending, result.ImageOrientation, result.MainRotation,
            result.Reflection, result.MetadataChanges, Marshal.PtrToStringUTF8((nint)result.Details) ?? "", evidence);
    }
}
