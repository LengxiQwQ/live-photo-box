using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8ImageMetadataProofTests
{
    [Theory]
    [InlineData(0U)]
    [InlineData(1U)]
    [InlineData(2U)]
    [InlineData(3U)]
    public void UnknownVersionOrStaleTokenCannotIssueMetadataProof(uint version)
    {
        Assert.Equal(80,Marshal.SizeOf<NativeHdrMetadataProofV2>());
        Assert.Equal(16,(int)Marshal.OffsetOf<NativeHdrMetadataProofV2>(nameof(NativeHdrMetadataProofV2.SourceFileSha256)));
        using var context=NativeContext.Create();
        var proof=new NativeHdrMetadataProofV2{StructSize=80,ApiVersion=version,ProofKind=2,ProofFlags=1};
        var result=NativeMethods.GetHdrMetadataProofV2(context.Handle,1234,ref proof);
        Assert.NotEqual(NativeResult.Ok,result);
        Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task OrdinaryXmpProofBindsOwnedStageAndCannotBeUsedByAnotherContext()
    {
        string source=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/苹果双文件.HEIC"));
        string video=Path.ChangeExtension(source,".MOV");
        string hash(string path)
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        Assert.True(File.Exists(source));Assert.True(File.Exists(video));
        string before=hash(source);
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,video,workspace);
        var binding=Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        using var folder=new P9TargetTestInputs.Folder(nameof(OrdinaryXmpProofBindsOwnedStageAndCannotBeUsedByAnotherContext));
        string output=Path.Combine(folder.Path,"not-published.jpg");
        using(var transaction=await NativeMediaService.StageHeicGainMapToJpegUltraHdrAsync(bundle.PrimaryImage.Path,output,
            binding,bundle.PrimaryImage.ByteLength,92,HdrOutputPolicy.PreserveHdr,CancellationToken.None))
        {
            transaction.Inspect();
            var proof=transaction.GetOrdinaryXmpProof();
            Assert.True(proof.HasValue);
            Assert.Equal(hash(bundle.PrimaryImage.Path),proof.Value.SourceSha256);
            Assert.Equal(hash(transaction.StagingPath),proof.Value.OutputSha256);
            using var other=NativeContext.Create();
            var foreign=new NativeHdrMetadataProofV2{StructSize=80,ApiVersion=2,ProofKind=2,ProofFlags=1};
            Assert.NotEqual(NativeResult.Ok,NativeMethods.GetHdrMetadataProofV2(other.Handle,transaction.Result.TransactionToken,ref foreign));
            Assert.Equal(0U,foreign.ProofKind);Assert.Equal(0U,foreign.ProofFlags);
            Assert.False(File.Exists(output));
        }
        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.Equal(before,hash(source));
    }
}
