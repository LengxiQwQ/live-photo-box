using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8RemuxProofAbiTests
{
    [Fact]
    public void AdditiveProofLayoutKeepsOldMetadataLayoutUnchanged()
    {
        Assert.Equal(96,Marshal.SizeOf<NativeVideoPreservationResultV1>());
        Assert.Equal(192,Marshal.SizeOf<NativeVideoPreservationResultV2>());
        Assert.Equal(16,(int)Marshal.OffsetOf<NativeVideoPreservationResultV2>(nameof(NativeVideoPreservationResultV2.Metadata)));
        Assert.Equal(112,(int)Marshal.OffsetOf<NativeVideoPreservationResultV2>(nameof(NativeVideoPreservationResultV2.SourceLength)));
        Assert.Equal(128,(int)Marshal.OffsetOf<NativeVideoPreservationResultV2>(nameof(NativeVideoPreservationResultV2.SourceFileSha256)));
    }

    [Theory]
    [InlineData(0U,0U)]
    [InlineData(1U,0U)]
    [InlineData(3U,0U)]
    [InlineData(2U,1U)]
    public void BadVersionOrReservedRequestFailsWithoutOutputOrSuccessProof(uint version,uint reserved)
    {
        using var folder=new P9TargetTestInputs.Folder(nameof(BadVersionOrReservedRequestFailsWithoutOutputOrSuccessProof)+version+reserved);
        using var context=NativeContext.Create();
        var request=new NativeVideoPreservationRequestV1{StructSize=16,ApiVersion=1,Policy=0,Reserved=reserved};
        var diagnostics=new NativeVideoBackendDiagnostics{StructSize=208};
        var proof=new NativeVideoPreservationResultV2{StructSize=192,ApiVersion=version,ProofKind=1,ProofFlags=1};
        var result=NativeMethods.TranscodeVideoV4(context.Handle,"missing-source.mov",Path.Combine(folder.Path,"no.mp4"),1,1,23,
            in request,ref diagnostics,ref proof);
        Assert.NotEqual(NativeResult.Ok,result);
        Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);Assert.Equal(0U,proof.Metadata.Status);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }
}
