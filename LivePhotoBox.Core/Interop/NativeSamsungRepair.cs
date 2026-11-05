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
    internal static Task<SamsungRepairObservation> RunAsync(string source,string workspace,string? output,uint actions,CancellationToken token)
        => Task.Run(()=>Run(source,workspace,output,actions,token),token);
    private static unsafe SamsungRepairObservation Run(string source,string workspace,string? output,uint actions,CancellationToken token)
    {
        using var input=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        var identity=WindowsFileIdentity.Capture(input.SafeFileHandle);
        var request=new NativeSamsungRepairRequest {StructSize=(uint)sizeof(NativeSamsungRepairRequest),ApiVersion=1,Actions=actions,
            ExpectedIdentity=new NativeFileIdentity {VolumeSerial=identity.VolumeSerialNumber,FileIndex=identity.FileIndex,
                FileSize=(ulong)input.Length,LinkCount=identity.LinkCount}};
        byte[] hash=SHA256.HashData(input);for(int i=0;i<32;++i)request.SourceSha256[i]=hash[i];
        var result=new NativeSamsungRepairResult {StructSize=(uint)sizeof(NativeSamsungRepairResult),ApiVersion=1};
        using var context=NativeContext.Create(token);
        var status=NativeMethods.RepairSamsung(context.Handle,source,workspace,output,ref request,ref result);
        // Retain creating-handle receipts even for a failed/cancelled attempt,
        // so the existing workspace can safely clean every owned intermediate.
        var receipts=NativeMediaService.CaptureSamsungRepairOutputs(context,workspace,output);
        context.ThrowIfFailed(status);
        NativeMediaService.NativeRegisteredOutputEvidence? evidence=null;
        if(output!=null&&result.Changed!=0&&!receipts.TryGetValue(Path.GetFullPath(output),out evidence))
            throw new InvalidDataException("Samsung repair did not register its complete output.");
        return new(result.Changed,result.Reencoded,result.ImageOrientation,result.MainRotation,result.AutoplayRotation,
            (result.Flags&1)!=0,(result.Flags&256)!=0,Marshal.PtrToStringUTF8((nint)result.Details)??"",evidence);
    }
}
