using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R7VivoBoundaryTests
{
    [Theory]
    [InlineData("vivo.jpg")]
    [InlineData("小米.jpg")]
    [Trait("Category", "RealSamples")]
    public async Task OriginalModernVivoCompletesNeutralStructuredTargetAndFreshValidation(string filename)
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, filename);
        Assert.True(File.Exists(source), "Required original vivo X300 sample is missing.");
        string hash = Hash(source);
        string evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R7_OUTPUTS")
            ?? throw new InvalidOperationException("R7 evidence output is required.");
        evidence=Path.Combine(evidence,filename.Replace('.', '_'));
        Directory.CreateDirectory(evidence);
        var profile = OutputProfile.Create(TargetProtocol.VivoLivePhoto, OutputMediaFormat.JpegMp4);
        string? secondary=null;
        string? secondaryHash=secondary is null?null:Hash(secondary);
        using var workspace = new MediaWorkspace();
        try
        {
            var sourceFacts = await new SourceInspector().InspectAsync(source,secondary);
            Assert.Equal(filename=="vivo.jpg"?SourceProtocol.VivoLivePhoto:SourceProtocol.GoogleMotionPhotoV2, sourceFacts.Protocol);
            var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, secondary, workspace,
                profile.MediaRequirement, PreservationPolicy.Strict);
            File.Copy(bundle.PrimaryImage.Path, Path.Combine(evidence, "neutral.jpg"), true);
            File.Copy(bundle.MotionVideo!.Path, Path.Combine(evidence, "neutral.mp4"), true);
            File.WriteAllText(Path.Combine(evidence, "neutral.json"), JsonSerializer.Serialize(new
            { bundle.Manifest, bundle.Semantics, bundle.PreservationCarriers }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path)).Protocol);
            string target = Path.Combine(evidence, "target.jpg");
            if(filename=="vivo.jpg") {
                Assert.Contains(sourceFacts.ConfirmedResidues, row => row.Id == "vivo-modern-vid-binding");
                Assert.Contains(sourceFacts.ConfirmedResidues, row => row.Id == "vivo-modern-empty-audio");
            }
            await StructuredTargetWriterService.WriteAsync(bundle, profile, target, workspace.RootDirectory, true, CancellationToken.None);
            var expected = new TargetValidationExpectations(4,0,(uint)bundle.MotionVideo.VideoCodec,bundle.Semantics.Timing.CoverTimestampUs)
            {
                UseV2=true,
                Auxiliaries=bundle.Manifest.Where(row=>row.Role=="GainMap").Select(row=>new TargetAuxiliaryExpectation(1,1,(ulong)row.ByteLength,row.Sha256)).ToArray()
            };
            using(var reader=File.OpenRead(target))NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader,expected,CancellationToken.None),expected);
            Assert.True(File.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(evidence, "*.stage", SearchOption.AllDirectories));
        }
        finally { Assert.Equal(hash, Hash(source)); if(secondary is not null)Assert.Equal(secondaryHash,Hash(secondary)); }
    }

    [Theory]
    [Trait("Category","RealSamples")]
    [InlineData("红米老款-GV1.JPG",null,"target-padding")]
    [InlineData("vivo双文件.jpg","vivo双文件.mp4","target-audio-profile")]
    [InlineData("苹果-双文件.JPG","苹果-双文件.MOV","neutral-conversion")]
    [InlineData("oppo.jpg",null,"target-comment-conflict")]
    public async Task UnrepresentableRealSourceKeepsMetadataAndCannotPublish(string filename,string? companion,string boundary) {
        string source=Path.Combine(W2EvidencePaths.CacheRoot,filename);Assert.True(File.Exists(source));string before=Hash(source);
        string? secondary=companion is null?null:Path.Combine(W2EvidencePaths.CacheRoot,companion);string? secondaryBefore=secondary is null?null:Hash(secondary);
        string root=Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R7_OUTPUTS")??throw new InvalidOperationException("R7 output required");
        string evidence=Path.Combine(root,"rejected-real",boundary);Directory.CreateDirectory(evidence);string final=Path.Combine(evidence,"must-not-publish.jpg");
        using var workspace=new MediaWorkspace();var profile=OutputProfile.Create(TargetProtocol.VivoLivePhoto,OutputMediaFormat.JpegMp4);
        Exception failure;
        try {
            if(boundary=="neutral-conversion") {
                failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>new NeutralMediaService().CreateNeutralBundleAsync(source,secondary,workspace,profile.MediaRequirement,PreservationPolicy.Strict));
                Assert.Contains("no safe preservation mapping",failure.Message);
            }else {
                var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(source,secondary,workspace,profile.MediaRequirement,PreservationPolicy.Strict);
                string imageHash=Hash(bundle.PrimaryImage.Path),videoHash=Hash(bundle.MotionVideo!.Path);
                File.Copy(bundle.PrimaryImage.Path,Path.Combine(evidence,"neutral.jpg"),true);
                failure=await Assert.ThrowsAsync<NotSupportedException>(()=>StructuredTargetWriterService.WriteAsync(bundle,profile,final,workspace.RootDirectory,false,CancellationToken.None));
                if(boundary=="target-padding")Assert.Contains("preserved primary-padding",failure.Message);
                if(boundary=="target-comment-conflict")Assert.Contains("Existing UserComment conflicts",failure.Message);
                if(boundary=="target-audio-profile")Assert.Contains("Independent generic media structure rejected",failure.Message);
                Assert.Equal(imageHash,Hash(bundle.PrimaryImage.Path));Assert.Equal(videoHash,Hash(bundle.MotionVideo.Path));
            }
            Assert.False(File.Exists(final));Assert.Empty(Directory.EnumerateFiles(evidence,"*.stage",SearchOption.AllDirectories));
            File.WriteAllText(Path.Combine(evidence,"rejection.json"),JsonSerializer.Serialize(new{filename,companion,boundary,stage=boundary.StartsWith("neutral",StringComparison.Ordinal)?"Neutral conversion":"Native structured target prepare/stage",exception=failure.GetType().FullName,failure.Message,sourceHash=before,secondaryHash=secondaryBefore},new JsonSerializerOptions{WriteIndented=true}));
        }finally{Assert.Equal(before,Hash(source));if(secondary is not null)Assert.Equal(secondaryBefore,Hash(secondary));}
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}
