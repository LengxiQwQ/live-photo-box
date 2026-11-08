using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

internal sealed record SamsungRepairObservation(uint Changed,uint Reencoded,uint ImageOrientation,
    uint MainRotation,uint AutoplayRotation,bool HasAutoplay,bool IsPartial,string Details,
    NativeMediaService.NativeRegisteredOutputEvidence? Evidence);

internal static class NativeSamsungRepair
{
    internal sealed record Attempt(SamsungRepairObservation? Observation, Exception? Failure);
    internal static async Task<SamsungRepairObservation> RunAsync(string source, string workspace, string? output, uint actions, CancellationToken token)
    {
        var attempt = await TryRunAsync(source, workspace, output, actions, token).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Observation!;
    }
    internal static Task<Attempt> TryRunAsync(string source, string workspace, string? output, uint actions, CancellationToken token)
        => Task.Run(() => Run(source, workspace, output, actions, token), CancellationToken.None);
    private static unsafe Attempt Run(string source, string workspace, string? output, uint actions, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        using var protection = new NeutralArtifactReadLeaseSet();
        if (protection.TryPin(new MediaArtifact { Path = source, Kind = MediaArtifactKind.PrimaryImage }) is { } protectionFailure)
            return new(null, protectionFailure);
        using var input = SourceReadAttempt.Open(source);
        if (!input.Succeeded) return new(null, new IOException($"Cannot read repair source (system error {input.Error})."));
        if (!WindowsFileIdentity.TryCapture(input.Handle, out var identity, out int error) || !input.TryGetLength(out long length, out error))
            return new(null, new IOException($"Cannot verify repair source (system error {error})."));
        var request=new NativeSamsungRepairRequest {StructSize=(uint)sizeof(NativeSamsungRepairRequest),ApiVersion=1,Actions=actions,
            ExpectedIdentity=new NativeFileIdentity {VolumeSerial=identity!.VolumeSerialNumber,FileIndex=identity.FileIndex,
                FileSize=(ulong)length,LinkCount=identity.LinkCount}};
        var sourceHash = MediaFileHashAttempt.Compute(source, token);
        if (sourceHash.Failure is { } hashFailure) return new(null, hashFailure);
        byte[] hash = Convert.FromHexString(sourceHash.Sha256!);
        for (int i = 0; i < hash.Length; ++i) request.SourceSha256[i] = hash[i];
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        var result=new NativeSamsungRepairResult {StructSize=(uint)sizeof(NativeSamsungRepairResult),ApiVersion=1};
        using var context=NativeContext.Create(token);
        var status=NativeMethods.RepairSamsung(context.Handle,source,workspace,output,ref request,ref result);
        // Retain creating-handle receipts even for a failed/cancelled attempt,
        // so the existing workspace can safely clean every owned intermediate.
        var receipts=NativeMediaService.CaptureSamsungRepairOutputs(context,workspace,output);
        if (status != NativeResult.Ok) return new(null, context.CreateFailure(status));
        NativeMediaService.NativeRegisteredOutputEvidence? evidence=null;
        if(output!=null&&result.Changed!=0&&!receipts.TryGetValue(Path.GetFullPath(output),out evidence))
            return new(null, new InvalidDataException("Samsung repair did not register its complete output."));
        return new(new(result.Changed,result.Reencoded,result.ImageOrientation,result.MainRotation,result.AutoplayRotation,
            (result.Flags&1)!=0,(result.Flags&256)!=0,Marshal.PtrToStringUTF8((nint)result.Details)??"",evidence), null);
    }
}
