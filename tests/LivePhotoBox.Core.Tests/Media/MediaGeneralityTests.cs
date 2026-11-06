using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class MediaGeneralityTests
{
    private sealed class CaseFolder(string name) : IDisposable
    {
        private readonly TestFixtureWorkspace workspace = new();
        internal string Path => workspace.RootDirectory;
        public void Dispose() => workspace.Dispose();
    }

    private static byte[] Segment(byte[] image, byte marker, byte[] payload)
    {
        int n = payload.Length + 2;
        return image[..2].Concat(new byte[] {255, marker, (byte)(n >> 8), (byte)n}).Concat(payload).Concat(image[2..]).ToArray();
    }
    private static byte[] OrientedImage()
    {
        byte[] exif = {69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0};
        return Segment(P9TargetTestInputs.Image, 0xe1, exif);
    }
    private static byte[] Extended(byte[] image, bool geometry)
    {
        string property = geometry ? "xmlns:t='http://ns.adobe.com/tiff/1.0/' t:Orientation='6'" : "xmlns:q='urn:ordinary' q:Comment='keep'";
        byte[] xml = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description r:about='' " + property + "/></r:RDF></x:xmpmeta>");
        string guid = Convert.ToHexString(MD5.HashData(xml));
        string standard = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description r:about='' xmlns:n='http://ns.adobe.com/xmp/note/' n:HasExtendedXMP='" + guid + "'/></r:RDF></x:xmpmeta>";
        image = P9TargetTestInputs.AddXmp(image, standard);
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xmp/extension/\0" + guid);
        byte[] payload = new byte[header.Length + 8 + xml.Length]; header.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(header.Length,4), (uint)xml.Length);
        xml.CopyTo(payload,header.Length+8);
        return Segment(image,0xe1,payload);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtendedXmp_OrdinaryCanRotate_ActiveGeometryRetainsImageButRemovesIndependentPreview(bool geometry)
    {
        using var folder = new CaseFolder(nameof(ExtendedXmp_OrdinaryCanRotate_ActiveGeometryRetainsImageButRemovesIndependentPreview)+geometry);
        byte[] image = Extended(OrientedImage(),geometry);
        image = Segment(image,0xe2,P9TargetTestInputs.Image);
        string input=Path.Combine(folder.Path,"photo.jpg");File.WriteAllBytes(input,image);
        var result=await LivePhotoRepairService.RepairImageFileAsync(input,Path.Combine(folder.Path,"out"),true,false,new(),default);
        Assert.True(result.Status==(geometry?"warning":"success"), result.Status+": "+result.Message);
        Assert.NotNull(result.Image);
        Assert.Equal(geometry ? "warning" : "success", result.ImageStatus);
        Assert.Null(result.VideoStatus);
        var observed=await NativeMediaService.CapturePreservationObservationAsync(result.Image!,SourceProtocol.NonLive,ImageContainer.Jpeg,default);
        Assert.Equal(geometry?6:1,observed.Orientation);
        Assert.True(observed.HasExtendedXmp);
        Assert.Equal(0u,(await NativeMediaService.ProbeImageExifRepairAsync(result.Image!,1,default)).Changes);
        Assert.Equal(image,File.ReadAllBytes(input));
        if(geometry) Assert.Contains("_isolated",result.Image!);
    }
    [Fact]
    public async Task AllLimited_DoesNotPublishUnchangedCopy_HealthyHasExplicitResult()
    {
        using var folder=new CaseFolder(nameof(AllLimited_DoesNotPublishUnchangedCopy_HealthyHasExplicitResult));
        string input=Path.Combine(folder.Path,"photo.jpg"),output=Path.Combine(folder.Path,"out");
        File.WriteAllBytes(input,Extended(OrientedImage(),true));
        var limited = await LivePhotoRepairService.RepairImageFileAsync(input,output,true,false,new(),default);
        Assert.Equal("warning", limited.Status);
        Assert.Equal("warning_no_output", limited.OutcomeKey);
        Assert.Empty(limited.PublishedOutputs);
        Assert.Contains(limited.Actions, x => x.State == "not_completed");
        var compatibilityApi = await LivePhotoRepairService.RepairAsync(input, Path.Combine(output, "target.jpg"), new(),
            default, new RepairOptions { FixVideoRotation = false });
        Assert.False(compatibilityApi.Success);
        Assert.False(Directory.Exists(output));
        File.WriteAllBytes(input,P9TargetTestInputs.Image);
        var result=await LivePhotoRepairService.RepairImageFileAsync(input,output,true,false,new(),default);
        Assert.Equal("healthy",result.Status);
        Assert.Null(result.Image);
    }
    [Theory]
    [InlineData("r", "http://www.w3.org/1999/02/22-rdf-syntax-ns#", true)]
    [InlineData("ordinary", "http://www.w3.org/1999/02/22-rdf-syntax-ns#", true)]
    [InlineData("r", "urn:wrong-rdf", false)]
    public async Task OrdinaryXmpUsesNamespaceIdentityRatherThanPrefix(string prefix,string uri,bool valid)
    {
        using var folder=new CaseFolder(nameof(OrdinaryXmpUsesNamespaceIdentityRatherThanPrefix));
        string path=Path.Combine(folder.Path,"photo.jpg");
        string xml=$"<{prefix}:RDF xmlns:{prefix}='{uri}'><{prefix}:Description {prefix}:about='' xmlns:q='urn:ordinary' q:Keep='yes'/></{prefix}:RDF>";
        File.WriteAllBytes(path,P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image,xml));
        var discovered=await NativeMediaService.InspectMediaForDiscoveryAsync(path,null,default);
        Assert.Equal(valid,discovered.Facts!=null);
    }
    [Theory]
    [InlineData("success",ProcessStatus.Success)]
    [InlineData("healthy",ProcessStatus.Success)]
    [InlineData("warning",ProcessStatus.Warning)]
    [InlineData("failed",ProcessStatus.Failed)]
    [InlineData("skipped",ProcessStatus.Pending)]
    [InlineData("copied",ProcessStatus.Pending)]
    [InlineData("planned",ProcessStatus.Pending)]
    public void OutcomeMappingDoesNotPromoteNonExecutionToSuccess(string status,ProcessStatus expected)
        => Assert.Equal(expected,new LivePhotoRepairService.RepairResult(status,"details").ProcessStatus);

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task LegacyHintsAreReadOnlyCandidates_CompletePairStillRequired()
    {
        using var folder=new CaseFolder(nameof(LegacyHintsAreReadOnlyCandidates_CompletePairStillRequired));
        string photo=Path.Combine(folder.Path,"renamed.jpg"),video=Path.Combine(folder.Path,"other.mp4");
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"),photo);
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"),video);
        var a=await NativePairingHint.ReadAsync(photo,default);var b=await NativePairingHint.ReadAsync(video,default);
        Assert.NotNull(a);Assert.NotNull(b);Assert.Equal(SourceProtocol.VivoLegacyDualFile,a!.Protocol);Assert.Equal(a.Identifier,b!.Identifier);
        using(var batch=new RepairBatchContext())
        {
            var matches=await batch.CandidatesAsync(photo,default,SourceProtocol.VivoLegacyDualFile);
            Assert.Equal(2,matches.Count);
            Assert.Throws<IOException>(()=>File.Move(video,video+".moved"));
            Assert.Throws<IOException>(()=>File.WriteAllBytes(video,new byte[]{1,2,3}));
            string duplicate=Path.Combine(folder.Path,"duplicate.mp4");File.Copy(video,duplicate);
            Directory.SetLastWriteTimeUtc(folder.Path,DateTime.UtcNow.AddSeconds(2));
            await Assert.ThrowsAsync<IOException>(()=>batch.CandidatesAsync(photo,default,SourceProtocol.VivoLegacyDualFile));
        }
        using(var batch=new RepairBatchContext())
        {
            var result=await LivePhotoRepairService.RepairImageFileAsync(photo,Path.Combine(folder.Path,"out"),true,false,new(),default,batch:batch);
            Assert.Equal("failed",result.Status);Assert.Null(result.Image);
        }
        byte[] broken=File.ReadAllBytes(photo);broken[^1]^=1;File.WriteAllBytes(photo,broken);
        Assert.Null(await NativePairingHint.ReadAsync(photo,default));
    }
    [Fact]
    [Trait("Category","RealSamples")]
    public async Task AppleSingleMemberDoesNotBecomeStandaloneOrHealthyWhenCompanionIsUnconfirmed()
    {
        using var folder=new CaseFolder(nameof(AppleSingleMemberDoesNotBecomeStandaloneOrHealthyWhenCompanionIsUnconfirmed));
        string image=Path.Combine(folder.Path,"image.jpg");File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.JPG"),image);
        var alone=await LivePhotoRepairService.AnalyzeFileAsync(image);
        Assert.Equal(RepairIssueType.Limited,alone.IssueType);
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.MOV"),Path.Combine(folder.Path,"renamed.mov"));
        var paired=await LivePhotoRepairService.AnalyzeFileAsync(image);
        Assert.NotEqual(RepairIssueType.NonApple,paired.IssueType);
        File.Copy(image,Path.Combine(folder.Path,"duplicate.jpg"));
        var result=await LivePhotoRepairService.RepairImageFileAsync(image,Path.Combine(folder.Path,"out"),false,false,new(),default);
        Assert.Equal("failed",result.Status);Assert.Null(result.Image);
    }
    [Fact]
    [Trait("Category","RealSamples")]
    public async Task CancellationDuringDiscoveryReleasesAllCandidateLeases()
    {
        using var folder=new CaseFolder(nameof(CancellationDuringDiscoveryReleasesAllCandidateLeases));
        string image=Path.Combine(folder.Path,"image.jpg");File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"),image);
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"),Path.Combine(folder.Path,"video.mp4"));
        using(var batch=new RepairBatchContext())
        using(var cancellation=new CancellationTokenSource())
        {
            cancellation.CancelAfter(50);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>batch.CandidatesAsync(image,cancellation.Token,SourceProtocol.VivoLegacyDualFile));
        }
        File.Move(image,image+".moved");
        Assert.True(File.Exists(image+".moved"));
    }
    [Fact]
    public void PairingHintPodHasStableBoundedLayout()
    {
        Assert.Equal(208,System.Runtime.InteropServices.Marshal.SizeOf<NativePairingHintData>());
        Assert.Equal(80,System.Runtime.InteropServices.Marshal.OffsetOf<NativePairingHintData>(nameof(NativePairingHintData.Identifier)).ToInt32());
    }
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [Trait("Category","RealSamples")]
    public async Task BatchDiscoveryInspectionsGrowWithFiles_NotItemsTimesDirectory(int count)
    {
        using var folder=new CaseFolder(nameof(BatchDiscoveryInspectionsGrowWithFiles_NotItemsTimesDirectory)+count);
        for(int i=0;i<count;i++)
        {
            File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"),Path.Combine(folder.Path,$"{i}.jpg"));
            File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"),Path.Combine(folder.Path,$"renamed{i}.mp4"));
        }
        File.WriteAllBytes(Path.Combine(folder.Path,"broken.jpg"),new byte[]{1,2,3});
        var watch=Stopwatch.StartNew();using var batch=new RepairBatchContext();
        foreach(string path in Directory.GetFiles(folder.Path,"*.jpg").Where(p=>!p.EndsWith("broken.jpg")))
            Assert.Equal(2*count,(await batch.CandidatesAsync(path,default,SourceProtocol.VivoLegacyDualFile)).Count);
        watch.Stop();Assert.Equal(1,batch.DirectoryScans);Assert.Equal(2*count+1,batch.DiscoveryInspections);
        Assert.Equal(2*count+1,batch.PairingHintInspections);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();var cancellation=Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>batch.CandidatesAsync(Path.Combine(folder.Path,"0.jpg"),cancelled.Token));
        string evidence=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../split-merge/media-generality"));Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence,$"scale-{count}.json"),JsonSerializer.Serialize(new {files=2*count+1,bytes=Directory.GetFiles(folder.Path).Sum(p=>new FileInfo(p).Length),batch.DirectoryScans,batch.DiscoveryInspections,batch.PairingHintInspections,elapsedMs=watch.Elapsed.TotalMilliseconds,cancellationMs=cancellation.Elapsed.TotalMilliseconds,cache="warm sample cache; fresh batch context"}));
    }
}
