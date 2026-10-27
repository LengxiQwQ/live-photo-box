using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R7VivoAuthorityTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("missing-binding")] [InlineData("missing-empty")]
    [InlineData("owner")] [InlineData("role")] [InlineData("selector")]
    [InlineData("semantic")] [InlineData("mode")] [InlineData("coordinates")]
    [InlineData("fingerprint")] [InlineData("stale-video")]
    public async Task ActualModernSourceMutationRequiresEveryIdentityAtNativeSite(string mutation)
    {
        string source=Path.Combine(W2EvidencePaths.CacheRoot,"vivo.jpg");Assert.True(File.Exists(source));
        string sourceHash=Hash(source);
        using var workspace=new MediaWorkspace();
        using var inspected=await new SourceInspector().InspectWithPlanAsync(source);
        var extracted=await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan,source,null,workspace);
        using var actualProductionPlan=extracted.CleanupPlan;
        Assert.NotNull(actualProductionPlan);
        string imageHash=Hash(extracted.PrimaryImage.Path),videoHash=Hash(extracted.MotionVideo!.Path);
        var actions=inspected.Facts.ConfirmedResidues.Select(r=>new PlannedCleanupAction {
            ResidueId=r.Id,OwnerProtocol=r.OwnerProtocol,ArtifactRole=r.ArtifactRole,StructureKind=r.StructureKind,
            Selector=r.Selector,ExpectedSemantic=r.ExpectedSemantic,ExpectedFingerprint=r.ExpectedFingerprint,
            CoordinateSpace=r.CoordinateSpace,RemovalMode=r.RemovalMode,IsMandatory=r.RequiredAfterExtraction
        }).ToList();
        if(mutation.StartsWith("missing",StringComparison.Ordinal))actions.RemoveAll(r=>r.ResidueId==(mutation=="missing-empty"?"vivo-modern-empty-audio":"vivo-modern-vid-binding"));
        else if(mutation!="stale-video") {
            int index=actions.FindIndex(r=>r.ResidueId=="vivo-modern-vid-binding");Assert.True(index>=0);
            var row=actions[index];actions[index]=mutation switch {
                "owner"=>row with{OwnerProtocol=SourceProtocol.VivoLegacyDualFile},
                "role"=>row with{ArtifactRole=MediaArtifactKind.PrimaryImage},
                "selector"=>row with{Selector="vivoMediaEStream"},
                "semantic"=>row with{ExpectedSemantic="whole-UUID"},
                "mode"=>row with{RemovalMode=ResidueRemovalMode.Delete},
                "coordinates"=>row with{CoordinateSpace=(CoordinateSpace)99},
                "fingerprint"=>row with{ExpectedFingerprint=new string('0',32)},
                _=>throw new InvalidOperationException()
            };
        }
        // The harness supplies hostile plan actions to the same Native
        // mutation code. The successful lifecycle uses the actual P2 plan;
        // this test does not pretend forged actions are a production API.
        using var context=TestNativeContext.Create();
        using var hostile=await TestCleanerPlans.IssueFromActionsAsync(context,inspected.Facts,actions,
            extracted.PrimaryImage.Path,extracted.MotionVideo.Path,extracted.CleanupSource?.Path);
        if(mutation=="stale-video")File.AppendAllText(extracted.MotionVideo.Path,"stale");
        string expectedVideo=Hash(extracted.MotionVideo.Path);
        var failure=await new SourceProtocolCleaner().CleanAsync(new ProtocolCleanRequest {
            ExtractedBundle=extracted with{CleanupPlan=hostile},PreservationPolicy=PreservationPolicy.Strict
        },workspace);
        Assert.False(failure.Success);Assert.Null(failure.CleanedImage);Assert.Null(failure.CleanedVideo);
        Assert.Empty(Directory.EnumerateDirectories(workspace.RootDirectory,"staging_*"));
        Assert.Equal(imageHash,Hash(extracted.PrimaryImage.Path));Assert.Equal(expectedVideo,Hash(extracted.MotionVideo.Path));
        Assert.Equal(sourceHash,Hash(source));
        if(mutation!="stale-video")Assert.Equal(videoHash,expectedVideo);
        string root=Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R7_OUTPUTS")??throw new InvalidOperationException("R7 output required");
        string path=Path.Combine(root,"authority");Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,mutation+".json"),JsonSerializer.Serialize(new {
            mutation,classification="Real original extraction + hostile harness plan actions; shared production mutation site and rollback",
            failure.Success,failure.FailureCategory,failure.FailureStage,failure.ErrorMessage,sourceUnchanged=Hash(source)==sourceHash,
            extractedImageUnchanged=Hash(extracted.PrimaryImage.Path)==imageHash,extractedVideoUnchanged=Hash(extracted.MotionVideo.Path)==expectedVideo
        },new JsonSerializerOptions{WriteIndented=true}));
    }

    private static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
}
