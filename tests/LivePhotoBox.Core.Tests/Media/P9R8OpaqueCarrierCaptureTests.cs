using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Core.Tests.Support;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8OpaqueCarrierCaptureTests
{
    [Theory]
    [InlineData("一加.jpg",846375,8275393)]
    [InlineData("一加-改了封面照片.jpg",828956,7939533)]
    [InlineData("oppo.jpg",402,3905023)]
    [Trait("Category","RealSamples")]
    public async Task ExplicitCarrierOutputKeepsWholeRecordBlockAndCreatingHandleIdentity(string name,long size,long videoSize)
    {
        string source=Source(name);string sourceHash=Hash(source);
        using var inspected=await new SourceInspector().InspectWithPlanAsync(source);
        var carrier=Assert.Single(inspected.Facts.PreservationCarriers);
        Assert.Equal("OpaqueCameraRecords",carrier.Semantic);Assert.Equal("primary:0",carrier.OwnerIdentity);
        Assert.Equal("self-relative-record-directory-v1",carrier.Relationship);
        Assert.Equal(size,carrier.SourceLength);Assert.Equal(videoSize,inspected.Facts.MotionVideo!.ByteLength);
        Assert.Equal(24,Marshal.SizeOf<NativeCarrierExtractionOutputV1>());
        Assert.Equal(16,Marshal.OffsetOf<NativeCarrierExtractionOutputV1>(nameof(NativeCarrierExtractionOutputV1.OutputPath)).ToInt32());
        using var folder=new P9TargetTestInputs.Folder(nameof(ExplicitCarrierOutputKeepsWholeRecordBlockAndCreatingHandleIdentity)+size);
        string image=Path.Combine(folder.Path,"image.jpg"),video=Path.Combine(folder.Path,"motion.mp4"),metadata=Path.Combine(folder.Path,"ordinary.bin");
        string? gain=inspected.Facts.GainMap is {IsPresent:true}?Path.Combine(folder.Path,"gain.jpg"):null;
        var auxiliary=Auxiliaries(inspected.Facts,folder.Path);
        using(var attempt=inspected.ExtractionPlan.BeginExtractionAttempt(CancellationToken.None))
        {
            await NativeMediaService.ExtractMediaWithCarrierOutputsAsync(attempt,source,null,image,video,gain,null,auxiliary,[new(0,metadata)],null);
            var expected=new List<NativeMediaService.NativeExtractionOutputBinding>
            {
                new(MediaArtifactKind.PrimaryImage,uint.MaxValue,image),new(MediaArtifactKind.MotionVideo,uint.MaxValue,video),
                new(MediaArtifactKind.MetadataCarrier,0,metadata)
            };
            if(gain is not null)expected.Add(new(MediaArtifactKind.GainMap,uint.MaxValue,gain));
            expected.AddRange(auxiliary.Select(x=>new NativeMediaService.NativeExtractionOutputBinding(MediaArtifactKind.AuxiliaryItem,x.AuxiliaryIndex,x.Path)));
            var receipts=NativeMediaService.CaptureExtractionOutputEvidence(attempt,expected);
            var receipt=Assert.Single(receipts,x=>x.ArtifactRole==MediaArtifactKind.MetadataCarrier);
            Assert.Equal(carrier.SourceSha256,receipt.Sha256);Assert.Equal(carrier.SourceLength,receipt.ByteLength);Assert.Equal(1U,receipt.FileIdentity.LinkCount);
            byte[] original=File.ReadAllBytes(source);
            Assert.Equal(original.AsSpan(checked((int)carrier.SourceOffset),checked((int)carrier.SourceLength)).ToArray(),ReadRetained(metadata));
            var motion=inspected.Facts.MotionVideo;
            Assert.Equal(original.AsSpan(checked((int)motion.ByteOffset),checked((int)motion.ByteLength)).ToArray(),ReadRetained(video));
            Assert.Equal(NativeResult.Ok,NativeMethods.VerifyExtractionOutputs(attempt.ContextLease.Handle,attempt.NativeHandle,attempt.Generation));
            Assert.Equal(NativeResult.Ok,NativeMethods.RollbackExtractionOutputs(attempt.ContextLease.Handle,attempt.NativeHandle,attempt.Generation));
            Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
        }
        Assert.Equal(sourceHash,Hash(source));
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("invalid-index")]
    [InlineData("duplicate")]
    [InlineData("source-alias")]
    [InlineData("aux-alias")]
    [InlineData("existing")]
    [InlineData("missing-parent")]
    [InlineData("injected-write-after-carrier")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task CarrierCoverageAndTransactionFailuresKeepOriginalAndNoPartialOutputs(string defect)
    {
        string source=Source("一加.jpg"),sourceHash=Hash(source);
        using var inspected=await new SourceInspector().InspectWithPlanAsync(source);
        using var folder=new P9TargetTestInputs.Folder(nameof(CarrierCoverageAndTransactionFailuresKeepOriginalAndNoPartialOutputs)+defect);
        string metadata=Path.Combine(folder.Path,"ordinary.bin"),image=Path.Combine(folder.Path,"image.jpg"),video=Path.Combine(folder.Path,"motion.mp4"),gain=Path.Combine(folder.Path,"gain.jpg");
        var outputs=new List<NativeMediaService.NativeCarrierOutputBinding>{new(0,metadata)};
        if(defect=="omitted")outputs.Clear();
        if(defect=="invalid-index")outputs[0]=new(1,metadata);
        if(defect=="duplicate")outputs.Add(new(0,Path.Combine(folder.Path,"duplicate.bin")));
        if(defect=="source-alias")outputs[0]=new(0,source);
        if(defect=="aux-alias")outputs[0]=new(0,gain);
        if(defect=="existing")File.WriteAllText(metadata,"keep existing file");
        if(defect=="missing-parent")image=Path.Combine(folder.Path,"missing-parent","image.jpg");
        using(var attempt=inspected.ExtractionPlan.BeginExtractionAttempt(CancellationToken.None))
        {
            Action<NativeContext>? configure=defect=="injected-write-after-carrier"
                ?ctx=>TestNativeHarness.SetExtractorFault(ctx,NativeExtractorFault.WriteFail,targetArtifact:0,triggerAfterBytes:0):null;
            var error=await Record.ExceptionAsync(()=>NativeMediaService.ExtractMediaWithCarrierOutputsAsync(attempt,source,null,image,video,gain,null,[],outputs,configure));
            Assert.NotNull(error);
            if(defect=="omitted")Assert.Contains("no authorized preservation handoff",error.Message);
            if(defect is "invalid-index" or "duplicate")Assert.Contains("[AuthorityViolation]",error.Message);
            if(defect=="injected-write-after-carrier")Assert.Contains("[OutputWriteFailed]",error.Message);
        }
        if(defect=="existing")
        {
            Assert.Equal(metadata,Assert.Single(Directory.GetFileSystemEntries(folder.Path)));Assert.Equal("keep existing file",File.ReadAllText(metadata));
        }
        else Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
        Assert.Equal(sourceHash,Hash(source));
    }

    [Fact]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task SourceChangeAfterInspectionCannotAuthorizeCarrierCapture()
    {
        using var folder=new P9TargetTestInputs.Folder(nameof(SourceChangeAfterInspectionCannotAuthorizeCarrierCapture));
        string source=Path.Combine(folder.Path,"source.jpg");File.Copy(Source("一加.jpg"),source);
        using var inspected=await new SourceInspector().InspectWithPlanAsync(source);
        using(var stream=new FileStream(source,FileMode.Append,FileAccess.Write))stream.WriteByte(0x51);
        string changed=Hash(source);
        using(var attempt=inspected.ExtractionPlan.BeginExtractionAttempt(CancellationToken.None))
        {
            var error=await Record.ExceptionAsync(()=>NativeMediaService.ExtractMediaWithCarrierOutputsAsync(attempt,source,null,
                Path.Combine(folder.Path,"image.jpg"),Path.Combine(folder.Path,"motion.mp4"),Path.Combine(folder.Path,"gain.jpg"),null,[],
                [new(0,Path.Combine(folder.Path,"ordinary.bin"))],null));
            Assert.NotNull(error);Assert.Contains("[SourceChanged]",error.Message);
        }
        Assert.Equal(source,Assert.Single(Directory.GetFileSystemEntries(folder.Path)));Assert.Equal(changed,Hash(source));
    }
    [Theory]
    [InlineData("version")]
    [InlineData("size")]
    [InlineData("reserved")]
    [InlineData("null-pointer")]
    [InlineData("count-overflow")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task MalformedAdditiveTransportCannotCreateOutputs(string defect)
    {
        string source=Source("一加.jpg"),hash=Hash(source);
        using var inspected=await new SourceInspector().InspectWithPlanAsync(source);
        using var folder=new P9TargetTestInputs.Folder(nameof(MalformedAdditiveTransportCannotCreateOutputs)+defect);
        using(var attempt=inspected.ExtractionPlan.BeginExtractionAttempt())
        {
            var result=BadTransport(attempt,source,folder.Path,defect);
            Assert.Equal(NativeResult.AuthorityViolation,result);
            Assert.Contains("[AuthorityViolation]",attempt.Context.GetLastError());
        }
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));Assert.Equal(hash,Hash(source));
    }
    private static unsafe NativeResult BadTransport(ExtractionPlanAttempt attempt,string source,string folder,string defect)
    {
        nint path=Marshal.StringToCoTaskMemUTF8(Path.Combine(folder,"ordinary.bin"));
        try
        {
            var descriptor=new NativeCarrierExtractionOutputV1{StructSize=24,Version=1,OutputPath=path};
            if(defect=="size")descriptor.StructSize=23;
            if(defect=="version")descriptor.Version=2;
            if(defect=="reserved")descriptor.Reserved=1;
            return NativeMethods.ExtractMediaWithPlanOutputsV3(attempt.ContextLease.Handle,attempt.NativeHandle,source,null,
                Path.Combine(folder,"image.jpg"),Path.Combine(folder,"motion.mp4"),Path.Combine(folder,"gain.jpg"),null,null,0,
                defect=="null-pointer"?null:&descriptor,defect=="count-overflow"?9U:1U);
        }
        finally{Marshal.FreeCoTaskMem(path);}
    }
    private static NativeMediaService.NativeAuxiliaryOutputBinding[] Auxiliaries(SourceMediaFacts facts,string folder)=>
        facts.AuxiliaryItems.Select((item,index)=>(item,index)).Where(x=>x.item.IsPresent&&x.item.Representation==AuxiliaryRepresentation.Materialized&&
            !(facts.GainMap is {IsPresent:true} gain&&gain.AuxiliaryIndex==(uint)x.index))
            .Select(x=>new NativeMediaService.NativeAuxiliaryOutputBinding((uint)x.index,Path.Combine(folder,$"aux-{x.index}.jpg"))).ToArray();
    private static string Source(string name){string path=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));Assert.True(File.Exists(path),"Required cached original missing");return path;}
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
    private static byte[] ReadRetained(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);byte[] b=new byte[checked((int)stream.Length)];stream.ReadExactly(b);return b;}
}
