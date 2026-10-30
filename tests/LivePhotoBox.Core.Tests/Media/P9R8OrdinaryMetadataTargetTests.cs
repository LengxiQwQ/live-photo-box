using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8OrdinaryMetadataTargetTests
{
    [Fact]
    public void AdditiveOrdinaryMetadataAbiHasExplicitLayouts()
    {
        Assert.Equal(1520,Marshal.SizeOf<NativeTargetOrdinaryMetadataV1>());
        Assert.Equal(408,Marshal.OffsetOf<NativeTargetOrdinaryMetadataV1>(nameof(NativeTargetOrdinaryMetadataV1.Materialized)).ToInt32());
        Assert.Equal(1304,Marshal.SizeOf<NativeTargetWriterRequestV4>());
        Assert.Equal(1288,Marshal.OffsetOf<NativeTargetWriterRequestV4>(nameof(NativeTargetWriterRequestV4.Metadata)).ToInt32());
        Assert.Equal(64,Marshal.SizeOf<NativeTargetOrdinaryMetadataExpectationV1>());
        Assert.Equal(208,Marshal.SizeOf<NativeTargetValidationRequestV4>());
        Assert.Equal(56,Marshal.SizeOf<NativeTargetOrdinaryMetadataObservedV1>());
        Assert.Equal(408,Marshal.SizeOf<NativeTargetValidationResultV3>());
    }

    [Theory]
    [InlineData("oppo.jpg",OutputMediaFormat.JpegMp4)]
    [InlineData("oppo.jpg",OutputMediaFormat.JpegMov)]
    [InlineData("一加.jpg",OutputMediaFormat.JpegMp4)]
    [InlineData("一加.jpg",OutputMediaFormat.JpegMov)]
    [InlineData("一加-改了封面照片.jpg",OutputMediaFormat.JpegMp4)]
    [InlineData("一加-改了封面照片.jpg",OutputMediaFormat.JpegMov)]
    [Trait("Category","RealSamples")]
    public async Task SharedProductPathPublishesActualOrdinaryRecordsWithFreshV4Validation(string name,OutputMediaFormat format)
    {
        string source=Source(name),sourceHash=Hash(source);
        using var folder=new P9TargetTestInputs.Folder(nameof(SharedProductPathPublishesActualOrdinaryRecordsWithFreshV4Validation)+name+format);
        var result=await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source,"","target",1,new LivePhotoMergeRunOptions
        {
            OutputDirectory=folder.Path,OutputProfile=OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2,format),
            StructuredOptions=new(){PreservationPolicy=PreservationPolicy.Strict}
        },CancellationToken.None);
        Assert.True(result.IsSuccess,result.Details);var output=Assert.Single(result.CommittedOutputs);
        Assert.Equal(Hash(output.Path),output.Sha256);Assert.Equal(new FileInfo(output.Path).Length,output.ByteLength);
        var row=Assert.Single(result.PreparationManifest,x=>x.Role=="OrdinaryMetadata");
        var semantics=Assert.IsType<NeutralMediaSemantics>(result.PreparationSemantics);
        Assert.Equal(row.Sha256,Assert.Single(semantics.OrdinaryMetadata).Sha256);
        var expected=new TargetValidationExpectations(2,(uint)format,(uint)semantics.MotionVideo!.VideoCodec,semantics.Timing.CoverTimestampUs)
        {
            UseV2=true,TargetVersion=1,
            OrdinaryMetadata=[new((ulong)row.ByteLength,row.Sha256,1)],
            Auxiliaries=result.PreparationManifest.Where(x=>x.Role is "GainMap" or "Auxiliary")
                .Select(x=>new TargetAuxiliaryExpectation(x.Semantic=="GainMap"?1U:x.Semantic=="Original"?2U:throw new InvalidDataException("Unexpected fixture auxiliary"),(uint)x.ImageContainer,(ulong)x.ByteLength,x.Sha256)).ToArray()
        };
        using var reader=File.OpenRead(output.Path);var report=NativeTargetValidator.Observe(reader,expected,CancellationToken.None);
        NativeTargetValidator.RequirePass(report,expected);var actual=Assert.IsType<NativeTargetValidationResultV3>(report.ObservedV3);
        Assert.Equal(1U,actual.MetadataPreservationStatus);Assert.Equal(1U,actual.MetadataCount);
        Assert.Equal(row.Sha256,actual.Metadata.GetSha256());Assert.Equal((ulong)row.ByteLength,actual.Metadata.ByteLength);
        Assert.Equal(0U,report.Observed.PreservationStatus);Assert.Equal(0U,report.Observed.DeviceStatus);
        Assert.Equal(sourceHash,Hash(source));Assert.Single(Directory.GetFiles(folder.Path));
        // Metadata proof cannot be dropped or forged while retaining a green
        // generic target/media result.
        Assert.Throws<TargetValidationException>(()=>NativeTargetValidator.RequirePass(report with{ObservedV3=null},expected));
        var broken=actual;broken.MetadataPreservationStatus=0;
        Assert.Throws<TargetValidationException>(()=>NativeTargetValidator.RequirePass(report with{ObservedV3=broken},expected));
        broken=actual;broken.Metadata.OwnerIndex=1;
        Assert.Throws<TargetValidationException>(()=>NativeTargetValidator.RequirePass(report with{ObservedV3=broken},expected));
        Assert.Throws<TargetValidationException>(()=>NativeTargetValidator.RequirePass(report,expected with{OrdinaryMetadata=[]}));
    }
    [Theory]
    [InlineData("size")]
    [InlineData("version")]
    [InlineData("flags")]
    [InlineData("owner")]
    [InlineData("kind")]
    [InlineData("relationship")]
    [InlineData("artifact-role")]
    [InlineData("artifact-container")]
    [InlineData("artifact-hash")]
    [InlineData("metadata-count")]
    [InlineData("metadata-pointer")]
    [InlineData("outer-reserved")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task PublicWriterV4RejectsMalformedOwnershipAndTransportBeforeStaging(string defect)
    {
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(Source("oppo.jpg"),null,workspace,null,PreservationPolicy.Strict);
        using var folder=new P9TargetTestInputs.Folder(nameof(PublicWriterV4RejectsMalformedOwnershipAndTransportBeforeStaging)+defect);
        using var context=NativeContext.Create();
        var result=PrepareMalformed(context,bundle,folder.Path,defect,out var plan);
        try{Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(nint.Zero,plan);Assert.Empty(Directory.GetFileSystemEntries(folder.Path));}
        finally{if(plan!=0)NativeMethods.TargetWriterRelease(context.Handle,plan);}
    }
    private static unsafe NativeResult PrepareMalformed(NativeContext context,LivePhotoBox.Protocols.Cleaning.NeutralMediaBundle bundle,string folder,string defect,out nint plan)
    {
        NativeTargetWriterArtifactV1* artifacts=stackalloc NativeTargetWriterArtifactV1[2];
        artifacts[0]=Artifact(bundle.PrimaryImage,0);artifacts[1]=Artifact(bundle.MotionVideo!,1);
        var request=new NativeTargetWriterRequestV1{StructSize=1080,Version=1,TargetProfile=2,TargetVersion=1,MediaFormat=0,ArtifactCount=2,Artifacts=(nint)artifacts,
            CoverTimestampPresent=1,CoverTimestampUs=bundle.Semantics.Timing.CoverTimestampUs!.Value};
        Text(folder,request.StagingDirectoryUtf8,1024);
        var v2=new NativeTargetWriterRequestV2{StructSize=1248,Version=2,Base=request};Text("primary:0",v2.PrimaryIdentity,128);
        var row=Assert.Single(bundle.Manifest,x=>x.Role=="OrdinaryMetadata");var carrier=Assert.Single(bundle.PreservationCarriers);
        var metadata=new NativeTargetOrdinaryMetadataV1{StructSize=1520,Version=1,Kind=1,Materialized=Artifact(carrier.MaterializedArtifact!,5)};
        Text(row.StableIdentity,metadata.StableIdentity,128);Text("primary:0",metadata.OwnerIdentity,128);
        Text("OpaqueCameraRecords",metadata.Semantic,32);Text("self-relative-record-directory-v1",metadata.Relationship,96);
        if(defect=="size")metadata.StructSize--;
        if(defect=="version")metadata.Version=2;
        if(defect=="flags")metadata.Flags=1;
        if(defect=="owner")metadata.OwnerIndex=1;
        if(defect=="kind")metadata.Kind=2;
        if(defect=="relationship")Text("unowned-records",metadata.Relationship,96);
        if(defect=="artifact-role")metadata.Materialized.Role=0;
        if(defect=="artifact-container")metadata.Materialized.Container=1;
        if(defect=="artifact-hash")metadata.Materialized.Sha256[0]^=1;
        var v4=new NativeTargetWriterRequestV4{StructSize=1304,Version=4,Base=new NativeTargetWriterRequestV3{StructSize=1272,Version=3,Base=v2},MetadataCount=1,Metadata=(nint)(&metadata)};
        if(defect=="metadata-count")v4.MetadataCount=0;
        if(defect=="metadata-pointer")v4.Metadata=0;
        if(defect=="outer-reserved")v4.Reserved=1;
        return NativeMethods.TargetWriterPrepareV4(context.Handle,&v4,out plan);
    }
    private static unsafe NativeTargetWriterArtifactV1 Artifact(MediaArtifact artifact,uint role)
    {
        var identity=artifact.FileIdentity!;
        var result=new NativeTargetWriterArtifactV1{StructSize=1112,Version=1,Role=role,Container=role==0?(uint)artifact.ImageContainer:role==1?(uint)artifact.VideoContainer:0,
            VideoCodec=role==1?(uint)artifact.VideoCodec:0,ExpectedIdentity=new NativeCleanService.NativeFileIdentity{
                VolumeSerial=identity.VolumeSerialNumber,FileIndex=identity.FileIndex,FileSize=(ulong)artifact.ByteLength,LinkCount=identity.LinkCount}};
        byte[] hash=Convert.FromHexString(artifact.Sha256!);for(int i=0;i<32;i++)result.Sha256[i]=hash[i];Text(artifact.Path,result.PathUtf8,1024);return result;
    }
    [Theory]
    [InlineData("missing-capsule")]
    [InlineData("wrong-hash")]
    [InlineData("early-rule")]
    [InlineData("late-media")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task ActualNativeV4RejectionNeverPublishesPartialOrdinaryPreservationProof(string defect)
    {
        string source=Source(defect=="missing-capsule"?"小米.jpg":"oppo.jpg");
        using var folder=new P9TargetTestInputs.Folder(nameof(ActualNativeV4RejectionNeverPublishesPartialOrdinaryPreservationProof)+defect);
        var result=await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source,"","target",1,new LivePhotoMergeRunOptions
        {OutputDirectory=folder.Path,OutputProfile=OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2,OutputMediaFormat.JpegMp4),StructuredOptions=new(){PreservationPolicy=PreservationPolicy.Strict}},CancellationToken.None);
        Assert.True(result.IsSuccess,result.Details);string path=Assert.Single(result.CommittedOutputs).Path;
        var semantics=Assert.IsType<NeutralMediaSemantics>(result.PreparationSemantics);
        var row=result.PreparationManifest.SingleOrDefault(x=>x.Role=="OrdinaryMetadata");
        string expectedHash=row?.Sha256??"BAA1EDB00F0158B4BDB43CE0F9A5BA6655AA799209D34A53E91BD240E1DF4BA2";
        if(defect=="wrong-hash")expectedHash=new string('1',64);
        var expected=new TargetValidationExpectations(2,defect=="early-rule"?255U:0U,(uint)semantics.MotionVideo!.VideoCodec,semantics.Timing.CoverTimestampUs)
        {
            UseV2=true,OrdinaryMetadata=[new((ulong)(row?.ByteLength??402),expectedHash,1)],
            Auxiliaries=result.PreparationManifest.Where(x=>x.Role is "GainMap" or "Auxiliary")
                .Select(x=>new TargetAuxiliaryExpectation(x.Semantic=="GainMap"?1U:2U,(uint)x.ImageContainer,(ulong)x.ByteLength,x.Sha256)).ToArray()
        };
        if(defect=="late-media")
        {
            TargetValidationReport valid;using(var input=File.OpenRead(path))valid=NativeTargetValidator.Observe(input,expected,CancellationToken.None);
            NativeTargetValidator.RequirePass(valid,expected);byte[] bytes=File.ReadAllBytes(path);
            int at=checked((int)valid.Observed.VideoOffset);Assert.Equal("ftyp",System.Text.Encoding.ASCII.GetString(bytes,at+4,4));
            System.Text.Encoding.ASCII.GetBytes("zzzz").CopyTo(bytes,at+4);File.WriteAllBytes(path,bytes);
        }
        using var reader=File.OpenRead(path);var failed=NativeTargetValidator.Observe(reader,expected,CancellationToken.None);
        Assert.False(failed.CanPublish);Assert.Throws<TargetValidationException>(()=>NativeTargetValidator.RequirePass(failed,expected));
        var observed=Assert.IsType<NativeTargetValidationResultV3>(failed.ObservedV3);
        Assert.Equal(0U,observed.MetadataPreservationStatus);Assert.Equal(0U,observed.MetadataCount);Assert.Equal(0U,observed.Metadata.Kind);
        Assert.Equal(0UL,observed.Metadata.ByteLength);Assert.Equal(0U,observed.Metadata.ChunkCount);Assert.Equal(new string('0',64),observed.Metadata.GetSha256());
    }
    private static unsafe void Text(string value,byte* destination,int capacity){new Span<byte>(destination,capacity).Clear();byte[] b=System.Text.Encoding.UTF8.GetBytes(value);Assert.InRange(b.Length,1,capacity-1);b.CopyTo(new Span<byte>(destination,capacity));}
    private static string Source(string name){string path=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));Assert.True(File.Exists(path),"Required cached original missing");return path;}
    private static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream));}
}
