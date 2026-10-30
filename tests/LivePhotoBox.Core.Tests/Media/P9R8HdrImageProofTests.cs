using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

// Construction regressions for the additive HDR image proof. These do not
// replace all-source CLI, preservation, independent or device acceptance.
public sealed class P9R8HdrImageProofTests
{
    [Fact]
    public void OldRuntimeCannotClaimCompleteHeifOriginalFileInventory()
    {
        ulong old=NativeRuntime.FoundationCapability|NativeRuntime.HeicBackendCapability|NativeRuntime.HeicSecondaryImageEncoderCapability;
        Assert.Throws<LivePhotoBox.Media.Inspection.SourceInspectionException>(()=>NativeMediaService.ValidateHeifFileInventoryCapability(old));
        NativeMediaService.ValidateHeifFileInventoryCapability(old|NativeRuntime.HeifOriginalFileResourceCapability);
    }

    [Fact]
    public unsafe void CompleteHdrProofRejectsEveryInvalidCapacityWithoutWritingBeyondDeclaredRegion()
    {
        using var context=NativeContext.Create();
        byte* storage=stackalloc byte[512];
        foreach(uint capacity in new uint[]{0,1,4,8,16,80,239,240,241,512})
        {
            new Span<byte>(storage,512).Fill(0xa5);*(uint*)storage=capacity;*(uint*)(storage+4)=1;
            byte[] before=new ReadOnlySpan<byte>(storage,512).ToArray();
            ref var proof=ref *(NativeImageConversionProofV1*)storage;
            Assert.NotEqual(NativeResult.Ok,NativeMethods.GetHdrImageConversionProofV1(context.Handle,0,ref proof));
            int firstUntouched=(int)Math.Min(capacity,240U);
            Assert.Equal(before.AsSpan(firstUntouched).ToArray(),new ReadOnlySpan<byte>(storage+firstUntouched,512-firstUntouched).ToArray());
            if(capacity>=16)Assert.Equal(0U,proof.ProofKind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category","RealSamples")]
    public async Task CompleteHdrProofTokenIsInvalidAfterItsActualAbortOrCommit(bool commit)
    {
        string source=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/三星.jpg"));
        using var workspace=new MediaWorkspace();var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(CompleteHdrProofTokenIsInvalidAfterItsActualAbortOrCommit)+commit);
        string output=Path.Combine(folder.Path,"final.heic");
        using var transaction=await NativeMediaService.StageJpegGainMapToHeicAsync(bundle.PrimaryImage.Path,output,
            Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding),bundle.PrimaryImage.ByteLength,92,HdrOutputPolicy.PreserveHdr);
        var proof=transaction.GetCompleteHdrImageProof();ulong token=transaction.Result.TransactionToken;
        var context=Assert.IsType<NativeContext>(typeof(NativeHdrGainMapConversionTransaction)
            .GetField("_context",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(transaction));
        if(commit)
        {
            transaction.Commit();var actual=transaction.CaptureCommittedOutputEvidence(output);
            Assert.Equal(OutputHash(proof),actual.Sha256);Assert.Equal(proof.OutputIdentity.FileIndex,actual.FileIdentity.FileIndex);
            Assert.Equal(proof.OutputIdentity.VolumeSerial,actual.FileIdentity.VolumeSerialNumber);
        }
        else transaction.Abort();
        var stale=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1,ProofKind=2,ProofFlags=1};
        Assert.NotEqual(NativeResult.Ok,NativeMethods.GetHdrImageConversionProofV1(context.Handle,token,ref stale));
        Assert.Equal(0U,stale.ProofKind);Assert.Equal(0U,stale.ProofFlags);
        Assert.Throws<InvalidOperationException>(()=>transaction.GetCompleteHdrImageProof());
        Assert.Equal(commit,File.Exists(output));Assert.Equal(commit?1:0,Directory.GetFiles(folder.Path).Length);
    }

    [Theory]
    [InlineData("vivo.jpg",1U)]
    [InlineData("三星.jpg",6U)]
    [InlineData("一加.jpg",1U)]
    [InlineData("一加-改了封面照片.jpg",1U)]
    [Trait("Category","RealSamples")]
    public async Task CompleteHdrImageProofBindsBothOwnersAndOwnedStage(string name,uint direction)
    {
        string source=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));
        Assert.True(File.Exists(source));string original=Hash(source);
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,null,workspace);
        var binding=Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        using var folder=new P9TargetTestInputs.Folder(nameof(CompleteHdrImageProofBindsBothOwnersAndOwnedStage)+name);
        string output=Path.Combine(folder.Path,"not-published.heic");
        using(var transaction=await NativeMediaService.StageJpegGainMapToHeicAsync(bundle.PrimaryImage.Path,output,
            binding,bundle.PrimaryImage.ByteLength,95,HdrOutputPolicy.PreserveHdr))
        {
            var proof=transaction.GetCompleteHdrImageProof();
            Assert.Equal(240,Marshal.SizeOf<NativeImageConversionProofV1>());
            Assert.Equal(240U,proof.StructSize);Assert.Equal(1U,proof.ApiVersion);Assert.Equal(2U,proof.ProofKind);
            Assert.Equal(1U,proof.ProofFlags);Assert.Equal(3U,proof.Operation);
            Assert.Equal(direction,proof.SourceOrientation);Assert.Equal(1U,proof.OutputOrientation);
            Assert.Equal(1U,proof.OrientationStatus);Assert.Equal(1U,proof.MetadataStatus);Assert.InRange(proof.MetadataCount,1U,8U);
            Assert.Equal(Hash(bundle.PrimaryImage.Path),SourceHash(proof));Assert.Equal(Hash(transaction.StagingPath),OutputHash(proof));
            Assert.Equal((ulong)bundle.PrimaryImage.ByteLength,proof.SourceLength);
            Assert.Equal(transaction.Result.PrimaryWidth,proof.OutputWidth);Assert.Equal(transaction.Result.PrimaryHeight,proof.OutputHeight);
            Assert.Equal(direction>=5?proof.SourceHeight:proof.SourceWidth,proof.OutputWidth);
            Assert.Equal(direction>=5?proof.SourceWidth:proof.SourceHeight,proof.OutputHeight);
            Assert.True(MetadataHashesEqual(proof));Assert.Equal(1U,proof.OutputIdentity.LinkCount);
            Assert.Equal(proof.OutputLength,proof.OutputIdentity.FileSize);
            using(var staged=new FileStream(transaction.StagingPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                var actual=new NativeMediaService.NativeRegisteredOutputEvidence(WindowsFileIdentity.Capture(staged.SafeFileHandle),
                    staged.Length,Hash(transaction.StagingPath));
                var verified=NativeHdrImageProofValidator.Validate(proof,Hash(bundle.PrimaryImage.Path),proof.SourceLength,direction,actual);
                var execution=new ImageExecutionRecord{InputContainer=ImageContainer.Jpeg,OutputContainer=ImageContainer.Heic,HdrConversionEvidence=verified};
                Assert.NotNull(NeutralMediaService.GetVerifiedPhysicalMapping(execution));
                foreach(var forged in new[]{verified with{SourceSha256=new string('0',64)},verified with{OutputSha256=new string('0',64)},
                    verified with{SourceOrientation=9},verified with{OutputOrientation=6}})
                    Assert.Throws<InvalidDataException>(()=>NeutralMediaService.GetVerifiedPhysicalMapping(execution with{HdrConversionEvidence=forged}));
                var roundtrip=JsonSerializer.Deserialize<ImageHdrConversionEvidence>(JsonSerializer.Serialize(verified));
                Assert.NotNull(roundtrip);Assert.Null(NeutralMediaService.GetVerifiedPhysicalMapping(execution with{HdrConversionEvidence=roundtrip}));
                foreach(var invalid in Tampered(proof))
                    Assert.Throws<InvalidDataException>(()=>NativeHdrImageProofValidator.Validate(invalid,Hash(bundle.PrimaryImage.Path),proof.SourceLength,direction,actual));
            }
            var locked=Assert.Throws<IOException>(()=>
            {
                using var writer=new FileStream(bundle.PrimaryImage.Path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
            });
            Assert.Equal(32,locked.HResult&0xffff);
            using var foreign=NativeContext.Create();
            var rejected=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1,ProofKind=2,ProofFlags=1};
            Assert.NotEqual(NativeResult.Ok,NativeMethods.GetHdrImageConversionProofV1(foreign.Handle,transaction.Result.TransactionToken,ref rejected));
            Assert.Equal(0U,rejected.ProofKind);Assert.Equal(0U,rejected.ProofFlags);
            Assert.False(File.Exists(output));
        }
        Assert.Empty(Directory.GetFiles(folder.Path));Assert.Equal(original,Hash(source));
    }

    [Theory]
    [InlineData("vivo.jpg")]
    [InlineData("三星.jpg")]
    [InlineData("一加.jpg")]
    [InlineData("一加-改了封面照片.jpg")]
    [Trait("Category","RealSamples")]
    public async Task StrictHdrImageConversionUsesCompleteProofAndCommitsExactCreator(string name)
    {
        string source=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));
        Assert.True(File.Exists(source));string before=Hash(source);
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(StrictHdrImageConversionUsesCompleteProofAndCommitsExactCreator)+name);
        var result=await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact=bundle.PrimaryImage,TargetContainer=ImageContainer.Heic,TargetDirectory=folder.Path,
            PreservationPolicy=PreservationPolicy.Strict,HdrOutputPolicy=HdrOutputPolicy.PreserveHdr,
            TrustedHdrGainMapBinding=bundle.HdrGainMapBinding,TargetHdrGainMapSemantic=ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary
        });
        Assert.True(result.Success,result.ErrorMessage);var output=Assert.IsType<MediaArtifact>(result.OutputArtifact);
        var evidence=Assert.IsType<ImageHdrConversionEvidence>(result.ExecutionRecord.HdrConversionEvidence);
        Assert.Null(result.ExecutionRecord.OrdinaryConversionEvidence);Assert.Null(result.ExecutionRecord.MetadataMapping);
        Assert.Equal(Hash(bundle.PrimaryImage.Path),evidence.SourceSha256);Assert.Equal(Hash(output.Path),evidence.OutputSha256);
        Assert.Equal(output.Sha256,evidence.OutputSha256);Assert.NotNull(output.FileIdentity);
        Assert.Equal(1U,evidence.OutputOrientation);Assert.True(result.ExecutionRecord.PixelReencoded);
        Assert.Equal(ConversionComponentOutcome.Preserved,result.ExecutionRecord.Truth.Metadata);
        Assert.Equal(ConversionComponentOutcome.Preserved,result.ExecutionRecord.Truth.Orientation);
        Assert.Equal(ConversionComponentOutcome.Reencoded,result.ExecutionRecord.Truth.HdrGainMap);
        Assert.Equal(ConversionComponentOutcome.Reencoded,result.ExecutionRecord.Truth.Pixel);
        Assert.Equal(before,Hash(source));Assert.Single(Directory.GetFiles(folder.Path));
    }

    private static unsafe NativeImageConversionProofV1[] Tampered(NativeImageConversionProofV1 valid)
    {
        var result=new NativeImageConversionProofV1[29];
        for(int i=0;i<result.Length;++i)
        {
            var p=valid;
            switch(i)
            {
                case 0:p.StructSize=239;break;case 1:p.ApiVersion=2;break;case 2:p.ProofKind=1;break;
                case 3:p.ProofFlags=0;break;case 4:p.Operation=0;break;case 5:p.Reserved=1;break;
                case 6:p.SourceOrientation=0;break;case 7:p.SourceOrientation=9;break;case 8:p.OutputOrientation=6;break;
                case 9:p.OrientationStatus=0;break;case 10:p.SourceWidth=0;break;case 11:p.SourceHeight=65536;break;
                case 12:p.OutputWidth++;break;case 13:p.OutputHeight++;break;case 14:p.MetadataCount=9;break;
                case 15:p.MetadataCount=0;break;case 16:p.MetadataStatus=0;break;case 17:p.IccStatus=3;break;
                case 18:p.SourceLength++;break;case 19:p.OutputLength++;break;case 20:p.SourceSha256[0]^=1;break;
                case 21:p.OutputSha256[0]^=1;break;case 22:p.MappedSourceMetadataSha256[0]^=1;break;
                case 23:p.OutputMetadataSha256[0]^=1;break;case 24:p.OutputIdentity.LinkCount=2;break;
                case 25:p.OutputIdentity.VolumeSerial^=1;break;case 26:p.OutputIdentity.FileIndex^=1;break;
                case 27:p.OutputIdentity.FileSize++;break;
                case 28:for(int j=0;j<32;++j)p.MappedSourceMetadataSha256[j]=p.OutputMetadataSha256[j]=0;break;
            }
            result[i]=p;
        }
        return result;
    }

    private static string Hash(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static unsafe string SourceHash(NativeImageConversionProofV1 p)=>Convert.ToHexString(new ReadOnlySpan<byte>(p.SourceSha256,32));
    private static unsafe string OutputHash(NativeImageConversionProofV1 p)=>Convert.ToHexString(new ReadOnlySpan<byte>(p.OutputSha256,32));
    private static unsafe bool MetadataHashesEqual(NativeImageConversionProofV1 p)=>
        new ReadOnlySpan<byte>(p.MappedSourceMetadataSha256,32).SequenceEqual(new ReadOnlySpan<byte>(p.OutputMetadataSha256,32))&&
        !new ReadOnlySpan<byte>(p.MappedSourceMetadataSha256,32).ToArray().All(x=>x==0);
}
