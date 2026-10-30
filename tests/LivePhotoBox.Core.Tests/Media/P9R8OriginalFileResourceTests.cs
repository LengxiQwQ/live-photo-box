using System.Security.Cryptography;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8OriginalFileResourceTests
{
    [Fact]
    [Trait("Category","RealSamples")]
    public async Task WholeHistoricalOriginalSurvivesHeifWriterAndAuthorizedExtraction()
    {
        string source=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/一加-改了封面照片.jpg"));
        Assert.True(File.Exists(source),"Required edited Original real sample is missing.");
        string initial=Hash(source);
        var inspector=new SourceInspector();
        using var inspectedSource=await inspector.InspectWithPlanAsync(source);
        var original=Assert.Single(inspectedSource.Facts.AuxiliaryItems,x=>x.Semantic=="Original");
        byte[] sourceBytes=await File.ReadAllBytesAsync(source);
        byte[] expected=sourceBytes.AsSpan(checked((int)original.ByteOffset),checked((int)original.ByteLength)).ToArray();
        Assert.Equal(original.Sha256,Convert.ToHexString(SHA256.HashData(expected)));
        using var folder=new P9TargetTestInputs.Folder(nameof(WholeHistoricalOriginalSurvivesHeifWriterAndAuthorizedExtraction));
        var outcome=await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source,"","target",1,new LivePhotoMergeRunOptions
        {
            OutputDirectory=folder.Path,OutputProfile=OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2,OutputMediaFormat.HeicMov),
            StructuredOptions=new(){PreservationPolicy=PreservationPolicy.Strict}
        },CancellationToken.None);
        Assert.True(outcome.IsSuccess,outcome.Details);
        var output=Assert.Single(outcome.CommittedOutputs);Assert.Equal(output.Sha256,Hash(output.Path));
        using var inspected=await inspector.InspectWithPlanAsync(output.Path);
        Assert.Equal(2,inspected.Facts.AuxiliaryItems.Count);
        var stored=Assert.Single(inspected.Facts.AuxiliaryItems,x=>x.Semantic=="Original");
        Assert.Equal("uri ",stored.ItemType);Assert.True(stored.GraphComplete);
        Assert.Equal(ImageContainer.Jpeg,stored.Container);Assert.Equal(AuxiliaryCodec.Jpeg,stored.Codec);
        Assert.Equal("primary:0",stored.OwnerIdentity);Assert.Equal("Original",stored.Relationship);
        Assert.Equal(original.ByteLength,stored.ByteLength);Assert.Equal(original.Sha256,stored.Sha256);
        using var workspace=new MediaWorkspace();
        var extracted=await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan,output.Path,null,workspace);
        var descriptor=Assert.Single(extracted.AuxiliaryMedia,x=>x.Semantic=="Original");
        Assert.Equal(AuxiliaryRepresentation.Embedded,descriptor.Representation);
        Assert.Null(descriptor.MaterializedArtifact);
        Assert.Equal(stored.ByteOffset,descriptor.SourceOffset);Assert.Equal(stored.ByteLength,descriptor.SourceLength);
        Assert.Equal(stored.Sha256,descriptor.SourceSha256);
        byte[] extractedOwner=await File.ReadAllBytesAsync(extracted.PrimaryImage.Path);
        Assert.Equal(expected,extractedOwner.AsSpan(checked((int)descriptor.SourceOffset),checked((int)descriptor.SourceLength)).ToArray());
        Assert.Equal(initial,Hash(source));Assert.Equal(output.Sha256,Hash(output.Path));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
