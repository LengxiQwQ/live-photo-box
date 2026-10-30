using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8BoundImageProofTests
{
    [Fact]
    public unsafe void InvalidProofCapacitiesNeverWriteOutsideTheirDeclaredKnownRegion()
    {
        using var context=NativeContext.Create();var request=new NativeImageConversionRequestV2{StructSize=96,ApiVersion=2,SourceContainer=1,TargetContainer=2,Quality=95,OrientationPolicy=1};
        foreach(uint capacity in new uint[]{0,4,8,16,239,241,uint.MaxValue}) {
            byte* storage=stackalloc byte[256];new Span<byte>(storage,256).Fill(0xa5);
            var proof=(NativeImageConversionProofV1*)storage;proof->StructSize=capacity;proof->ApiVersion=1;
            byte[] before=new ReadOnlySpan<byte>(storage,256).ToArray();
            var result=NativeMethods.ConvertImageV2(context.Handle,"unused.jpg","unused.heic",in request,ref *proof);
            Assert.Equal(NativeResult.InvalidArgument,result);int limit=(int)Math.Min(capacity,240U);
            for(int i=0;i<256;i++) {
                byte expected=i<limit&&i>=8?(byte)0:before[i];
                Assert.Equal(expected,storage[i]);
            }
        }
    }

    [Fact]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task MalformedOrUnboundNativeProofCannotBecomeCorePreservationSuccess()
    {
        using var workspace=new MediaWorkspace();var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(P9R8BoundImageFailureTests.Cached("oppo.jpg"),null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(MalformedOrUnboundNativeProofCannotBecomeCorePreservationSuccess));
        string output=Path.Combine(folder.Path,"output.heic"),input=bundle.PrimaryImage.Path;
        using var context=NativeContext.Create();var request=P9R8BoundImageFailureTests.Request(input);var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        var result=NativeMethods.ConvertImageV2(context.Handle,input,output,in request,ref proof);context.ThrowIfFailed(result);
        var actual=NativeMediaService.CaptureRegisteredOutputEvidence(context,output,MediaArtifactKind.PrimaryImage);
        string sourceHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input)));ulong sourceLength=(ulong)new FileInfo(input).Length;
        var accepted=NativeBoundImageConverter.ValidateProof(proof,sourceHash,sourceLength,actual);Assert.Equal(1U,accepted.OutputOrientation);
        foreach(string defect in new[]{"size","version","kind","flags","operation","reserved","source-orientation","output-orientation","orientation-status",
            "source-width","output-width","count","metadata-status","icc-status","source-length","output-length","source-hash","output-hash","metadata-hash","empty-hash","volume","file-id","identity-size","links"}) {
            var changed=Mutate(proof,defect);
            Assert.Throws<InvalidDataException>(()=>NativeBoundImageConverter.ValidateProof(changed,sourceHash,sourceLength,actual));
        }
        Assert.Throws<InvalidDataException>(()=>NativeBoundImageConverter.ValidateProof(proof,new string('0',64),sourceLength,actual));
        Assert.Throws<InvalidDataException>(()=>NativeBoundImageConverter.ValidateProof(proof,sourceHash,sourceLength+1,actual));
        Assert.Equal(sourceHash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))));
    }

    [Fact]
    [Trait("Category","RealWindowsFilesystem")]
    public async Task BoundWrapperPinsActualOutputAndItsNamespaceUntilHandoffFinishes()
    {
        using var workspace=new MediaWorkspace();var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(P9R8BoundImageFailureTests.Cached("oppo.jpg"),null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(BoundWrapperPinsActualOutputAndItsNamespaceUntilHandoffFinishes));
        string output=Path.Combine(folder.Path,"output.heic");
        using var attempt=await NativeBoundImageConverter.ConvertAsync(bundle.PrimaryImage,output,95,CancellationToken.None);
        Assert.NotNull(attempt.Proof);Assert.NotNull(attempt.Attempt.OutputEvidence);Assert.Null(attempt.Attempt.OutputEvidenceFailure);
        Assert.Throws<IOException>(()=>{using var writer=new FileStream(output,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);});
        Assert.Throws<IOException>(()=>File.Delete(output));Assert.Throws<IOException>(()=>File.Move(output,output+".moved"));
        Assert.Throws<IOException>(()=>Directory.Move(folder.Path,folder.Path+"-moved"));
        attempt.Dispose();attempt.Dispose();
        using(var writer=new FileStream(output,FileMode.Open,FileAccess.Write,FileShare.Read))Assert.True(writer.CanWrite);
        Assert.Equal(attempt.Attempt.OutputEvidence!.Sha256,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))));
    }

    private static unsafe NativeImageConversionProofV1 Mutate(NativeImageConversionProofV1 proof,string defect)
    {
        switch(defect) {
        case "size":proof.StructSize--;break;case "version":proof.ApiVersion++;break;case "kind":proof.ProofKind=0;break;case "flags":proof.ProofFlags=3;break;
        case "operation":proof.Operation=1;break;case "reserved":proof.Reserved=1;break;case "source-orientation":proof.SourceOrientation=0;break;
        case "output-orientation":proof.OutputOrientation=6;break;case "orientation-status":proof.OrientationStatus=0;break;case "source-width":proof.SourceWidth=0;break;
        case "output-width":proof.OutputWidth++;break;case "count":proof.MetadataCount=5;break;case "metadata-status":proof.MetadataStatus=0;break;case "icc-status":proof.IccStatus=0;break;
        case "source-length":proof.SourceLength++;break;case "output-length":proof.OutputLength++;break;case "source-hash":proof.SourceSha256[0]^=1;break;
        case "output-hash":proof.OutputSha256[0]^=1;break;case "metadata-hash":proof.OutputMetadataSha256[0]^=1;break;
        case "empty-hash":for(int i=0;i<32;i++){proof.MappedSourceMetadataSha256[i]=0;proof.OutputMetadataSha256[i]=0;}break;
        case "volume":proof.OutputIdentity.VolumeSerial^=1;break;case "file-id":proof.OutputIdentity.FileIndex^=1;break;case "identity-size":proof.OutputIdentity.FileSize++;break;
        case "links":proof.OutputIdentity.LinkCount=2;break;default:throw new ArgumentException(defect);
        }
        return proof;
    }
}
