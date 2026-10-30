using System.Security.Cryptography;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8OrdinaryMetadataNeutralTests
{
    [Theory]
    [InlineData("一加.jpg",846375)]
    [InlineData("一加-改了封面照片.jpg",828956)]
    [InlineData("oppo.jpg",402)]
    [Trait("Category","RealSamples")]
    public async Task NeutralCarriesExactOrdinaryRecordsAsSeparateSemanticAndOwnedArtifact(string name,long expectedLength)
    {
        string source=Source(name);byte[] original=File.ReadAllBytes(source);string hash=Hash(original);
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,null,workspace,null,PreservationPolicy.Strict);
        var carrier=Assert.Single(bundle.PreservationCarriers,c=>c.Semantic==OrdinaryMetadataPreservation.Semantic);
        var artifact=await OrdinaryMetadataPreservation.VerifyAsync(carrier,CancellationToken.None);
        Assert.Equal(expectedLength,artifact.ByteLength);
        byte[] expected=original.AsSpan(checked((int)carrier.SourceOffset),checked((int)carrier.SourceLength)).ToArray();
        Assert.Equal(expected,File.ReadAllBytes(artifact.Path));Assert.Equal(Hash(expected),carrier.SourceSha256);
        var row=Assert.Single(bundle.Manifest,x=>x.Role=="OrdinaryMetadata");
        Assert.Equal(artifact.Path,row.Path);Assert.Equal(carrier.SourceSha256,row.Sha256);Assert.Equal(artifact.ByteLength,row.ByteLength);
        Assert.Equal(carrier.StableIdentity,row.StableIdentity);Assert.Equal("primary:0",row.OwnerIdentity);
        Assert.Equal(NeutralAuxiliaryRepresentation.Materialized,row.SemanticRepresentation);Assert.Null(row.OwnerByteOffset);
        var semantic=Assert.Single(bundle.Semantics.OrdinaryMetadata);
        Assert.Equal(row.Sha256,semantic.Sha256);Assert.Equal(row.ByteLength,semantic.ByteLength);Assert.Equal(row.Relationship,semantic.Relationship);
        Assert.Equal(NeutralEvidenceState.VerifiedPreserved,semantic.EvidenceState);
        var preservation=Assert.Single(bundle.Semantics.Preservation.OrdinaryMetadata);
        Assert.Equal(row.StableIdentity,preservation.SemanticIdentity);Assert.Equal(PreservationOutcome.Preserved,preservation.Outcome);
        Assert.DoesNotContain(bundle.Semantics.AuxiliaryMedia,x=>x.Semantic==carrier.Semantic);
        Assert.DoesNotContain(bundle.AuxiliaryMedia,x=>x.Semantic==carrier.Semantic);
        Assert.Equal(hash,Hash(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData("tamper")]
    [InlineData("replacement")]
    [InlineData("missing-artifact")]
    [InlineData("wrong-owner")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task OrdinaryCarrierRequiresActualUnchangedOwnedBytes(string defect)
    {
        string source=Source("oppo.jpg");string hash=Hash(File.ReadAllBytes(source));
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,null,workspace,null,PreservationPolicy.Strict);
        var carrier=Assert.Single(bundle.PreservationCarriers);var artifact=Assert.IsType<MediaArtifact>(carrier.MaterializedArtifact);
        byte[] original=File.ReadAllBytes(artifact.Path);string saved=artifact.Path+".retained";
        try
        {
            if(defect=="tamper"){byte[] changed=(byte[])original.Clone();changed[0]^=1;File.WriteAllBytes(artifact.Path,changed);}
            if(defect=="replacement"){File.Move(artifact.Path,saved);File.WriteAllBytes(artifact.Path,original);}
            if(defect=="missing-artifact")carrier=carrier with{MaterializedArtifact=null};
            if(defect=="wrong-owner")carrier=carrier with{OwnerIdentity="primary:9"};
            await Assert.ThrowsAsync<InvalidDataException>(()=>OrdinaryMetadataPreservation.VerifyAsync(carrier,CancellationToken.None));
        }
        finally
        {
            if(defect=="tamper")File.WriteAllBytes(artifact.Path,original);
            if(defect=="replacement"){File.Delete(artifact.Path);File.Move(saved,artifact.Path);}
        }
        Assert.Equal(hash,Hash(File.ReadAllBytes(source)));
    }
    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("namespace")]
    [Trait("Category","RealSamples")]
    public async Task FinalNativeObservationKeepsOrdinaryCarrierUnwritableAndUnreplaceable(string attack)
    {
        using var workspace=new MediaWorkspace();var cleaner=new CapturingCleaner();var probe=new AttackingProbe(cleaner,workspace,attack);
        var bundle=await new NeutralMediaService(cleaner:cleaner,videoConverter:probe)
            .CreateNeutralBundleAsync(Source("oppo.jpg"),null,workspace,null,PreservationPolicy.Strict);
        Assert.True(probe.Attempted);Assert.True(probe.Blocked);
        await OrdinaryMetadataPreservation.VerifyAsync(Assert.Single(bundle.PreservationCarriers),CancellationToken.None);
    }
    [Fact]
    [Trait("Category","RealSamples")]
    public async Task CarrierFinalObservationRejectsExistingWriterAndReleasesPartialLeases()
    {
        using var workspace=new MediaWorkspace();var cleaner=new CapturingCleaner(true);
        try
        {
            await Assert.ThrowsAsync<IOException>(()=>new NeutralMediaService(cleaner:cleaner)
                .CreateNeutralBundleAsync(Source("oppo.jpg"),null,workspace,null,PreservationPolicy.Strict));
            Assert.NotNull(cleaner.Carrier);
            await Assert.ThrowsAsync<IOException>(()=>OrdinaryMetadataPreservation.VerifyAsync(cleaner.Carrier!,CancellationToken.None));
        }
        finally{cleaner.Writer?.Dispose();}
        await OrdinaryMetadataPreservation.VerifyAsync(cleaner.Carrier!,CancellationToken.None);
        using var exclusive=File.Open(cleaner.Carrier!.MaterializedArtifact!.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }
    private sealed class CapturingCleaner(bool keepWriter=false):ISourceProtocolCleaner
    {
        internal PreservationCarrier? Carrier {get;private set;}
        internal FileStream? Writer {get;private set;}
        public async Task<ProtocolCleanResult> CleanAsync(ProtocolCleanRequest request,IMediaWorkspace workspace,CancellationToken token=default)
        {
            var result=await new SourceProtocolCleaner().CleanAsync(request,workspace,token);
            Carrier=Assert.Single(result.PreservationCarriers);
            if(keepWriter)Writer=File.Open(Carrier.MaterializedArtifact!.Path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);
            return result;
        }
    }
    private sealed class AttackingProbe(CapturingCleaner cleaner,IMediaWorkspace workspace,string attack):IVideoConverter
    {
        internal bool Attempted {get;private set;}internal bool Blocked {get;private set;}
        private readonly VideoConverter inner=new();
        public Task<VideoConversionResult> ConvertAsync(VideoConversionRequest request,CancellationToken token=default)=>inner.ConvertAsync(request,token);
        public async Task<VideoFacts> ProbeAsync(string path,CancellationToken token=default)
        {
            Attempted=true;string metadata=cleaner.Carrier!.MaterializedArtifact!.Path;
            try
            {
                switch(attack)
                {
                    case "write":using(File.Open(metadata,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){}break;
                    case "delete":File.Delete(metadata);break;
                    case "rename":File.Move(metadata,metadata+".foreign");break;
                    case "namespace":Directory.Move(workspace.RootDirectory,workspace.RootDirectory+"-foreign");break;
                    default:throw new ArgumentOutOfRangeException(nameof(attack));
                }
            }
            catch(IOException){Blocked=true;}
            return await inner.ProbeAsync(path,token);
        }
    }
    private static string Source(string name){string path=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));Assert.True(File.Exists(path),"Required cached original missing");return path;}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
}
