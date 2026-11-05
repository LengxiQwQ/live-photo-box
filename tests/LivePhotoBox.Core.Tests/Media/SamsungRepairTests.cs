using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SamsungRepairTests
{
    [Fact]
    public void VersionedRequestAndResult_HaveStableLayouts()
    {
        Assert.Equal(80,Marshal.SizeOf<NativeSamsungRepairRequest>());
        Assert.Equal(2080,Marshal.SizeOf<NativeSamsungRepairResult>());
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public unsafe void ChangedSourceFingerprint_IsRejectedBeforeStaging()
    {
        using var folder=new P9TargetTestInputs.Folder(nameof(ChangedSourceFingerprint_IsRejectedBeforeStaging));
        using var workspace=new MediaWorkspace();
        string source=Path.Combine(folder.Path,"source.jpg"),output=Path.Combine(folder.Path,"output.jpg");
        File.Copy(TestSampleResolver.ResolveSample("三星.jpg"),source);
        var identity=WindowsFileIdentity.Capture(source);
        var request=new NativeSamsungRepairRequest {StructSize=80,ApiVersion=1,Actions=15,
            ExpectedIdentity=new NativeFileIdentity {VolumeSerial=identity.VolumeSerialNumber,FileIndex=identity.FileIndex,
                FileSize=(ulong)new FileInfo(source).Length,LinkCount=identity.LinkCount}};
        byte[] hash=SHA256.HashData(File.ReadAllBytes(source));for(int i=0;i<32;++i)request.SourceSha256[i]=hash[i];
        // Replay against a different same-length source, without touching originals.
        byte[] changed=File.ReadAllBytes(source);changed[100]^=1;File.WriteAllBytes(source,changed);
        var result=new NativeSamsungRepairResult {StructSize=2080,ApiVersion=1};
        using var context=NativeContext.Create(default);
        Assert.Equal(NativeResult.SourceChanged,NativeMethods.RepairSamsung(context.Handle,source,workspace.RootDirectory,output,ref request,ref result));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));Assert.False(File.Exists(output));Assert.Equal(changed,File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("三星.jpg")]
    [InlineData("三星.heic")]
    [Trait("Category","RealSamples")]
    public async Task DefaultDeviceFilter_DiagnosesSamsung(string name)
    {
        using var folder=new P9TargetTestInputs.Folder(nameof(DefaultDeviceFilter_DiagnosesSamsung)+Path.GetExtension(name));
        string source=TestSampleResolver.ResolveSample(name);
        string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var result=await LivePhotoRepairService.RepairImageFileAsync(source,folder.Path,false,true,new RepairOptions(),default);
        Assert.Equal("planned",result.Status);Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Equal(hash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
    }
    [Fact]
    [Trait("Category","RealSamples")]
    public async Task DefaultDeviceFilter_SkipsOtherMotionPhotoVendors()
    {
        using var folder=new P9TargetTestInputs.Folder(nameof(DefaultDeviceFilter_SkipsOtherMotionPhotoVendors));
        string source=TestSampleResolver.ResolveSample("vivo.jpg");
        var result=await LivePhotoRepairService.RepairImageFileAsync(source,folder.Path,false,false,new RepairOptions(),default);
        Assert.Equal("skipped",result.Status);Assert.Empty(Directory.GetFiles(folder.Path));
        var analysis=await LivePhotoRepairService.AnalyzeFileAsync(source,default,defaultDevicesOnly:true);
        Assert.Equal(RepairIssueType.NonApple,analysis.IssueType);
    }

}
