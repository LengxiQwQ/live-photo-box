using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;
using Xunit.Abstractions;
using System.Buffers.Binary;
using System.Text;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class LightweightScanTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FilenameSelection_DoesNotOpenMedia_EvenWhenUnreadable()
    {
        using var ws = new TestFixtureWorkspace();
        string image = Path.Combine(ws.RootDirectory,"same.jpg"), video = Path.Combine(ws.RootDirectory,"same.mp4");
        File.WriteAllText(image,"not media"); File.WriteAllText(video,"not media");
        using var imageLock = File.Open(image,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var videoLock = File.Open(video,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var session = new DiscoveryInspectionSession();
        var result = await LivePhotoMergeScanService.ScanAsync(new() { InputDirectory = ws.RootDirectory },default,null,session);
        var pair = Assert.Single(result.Pairs);
        Assert.Equal(SelectionIntent.UserComposition,pair.SelectionIntent);
        Assert.Equal(PairingKind.FilenameCompositionRequest,pair.PairingKind);
        Assert.Equal(0,session.SingleInspections + session.PairInspections + session.CandidateInspections + session.HintInspections);
        Assert.Equal(0UL,session.CandidateBytesRead);
        File.WriteAllText(Path.Combine(ws.RootDirectory,"same.heic"),"third member");
        result = await LivePhotoMergeScanService.ScanAsync(new() { InputDirectory = ws.RootDirectory });
        Assert.Empty(result.Pairs);Assert.Contains(result.Diagnostics,d=>d.DiagnosticCode=="ConflictMultipleMedia");
    }

    [Theory]
    [InlineData("苹果-双文件.JPG", DiscoveryCapability.AppleId)]
    [InlineData("苹果-双文件.MOV", DiscoveryCapability.AppleId)]
    [InlineData("苹果双文件.HEIC", DiscoveryCapability.AppleId)]
    [InlineData("vivo双文件.jpg", DiscoveryCapability.VivoId)]
    [InlineData("vivo双文件.mp4", DiscoveryCapability.VivoId)]
    [Trait("Category","RealSamples")]
    internal async Task SelectedIdentity_IsBoundedAndReusable(string sample, DiscoveryCapability capability)
    {
        using var ws = new TestFixtureWorkspace();
        string path = Path.Combine(ws.RootDirectory,"renamed"+Path.GetExtension(sample));
        File.Copy(TestSampleResolver.ResolveSample(sample),path);
        var index = new FileIndexService();
        var facts = await Task.WhenAll(Enumerable.Range(0,8).Select(_=>index.GetAsync(path,capability,default)));
        Assert.NotNull(facts[0]?.Pairing);
        Assert.All(facts,f=>Assert.Same(facts[0],f));
        Assert.Equal(1,index.Reads);
        Assert.Equal(FactState.Found,facts[0]!.State);
        Assert.InRange(facts[0]!.BytesRead,1UL,4UL*1024*1024);
        output.WriteLine($"{sample}: bytes={facts[0]!.BytesRead}; reads={facts[0]!.ReadCount}");
        // No persistent lease: editing invalidates and causes one new capability read.
        File.SetLastWriteTimeUtc(path,File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await index.GetAsync(path,capability,default);
        Assert.Equal(2,index.Reads);
        File.Delete(path);
        await Assert.ThrowsAnyAsync<Exception>(()=>index.GetAsync(path,capability,default));
    }

    [Theory]
    [InlineData("红米老款-GV1.JPG")]
    [InlineData("oppo.jpg")]
    [InlineData("vivo.jpg")]
    [InlineData("三星.jpg")]
    [InlineData("三星.heic")]
    [InlineData("华为-Mate80.jpg")]
    [InlineData("华为Mate80.heic")]
    [InlineData("荣耀.jpg")]
    [Trait("Category","RealSamples")]
    public async Task SingleFileMarkers_SelectWithoutFullInspection(string sample)
    {
        using var session = new DiscoveryInspectionSession();
        var path = TestSampleResolver.ResolveSample(sample);
        var result = await LivePhotoDiscoveryService.ScanFilesAsync(new() { ExplicitFiles = new[]{path} },DiscoveryScanMode.SplitOnly,session:session);
        Assert.True(Assert.Single(result.Items).IsLivePhoto);
        Assert.Equal(0,session.SingleInspections+session.PairInspections+session.HintInspections);
        output.WriteLine($"{sample}: {result.Items[0].Protocol}; bytes={session.CandidateBytesRead}; partial={result.IsPartial}");
    }

    [Theory]
    [InlineData("苹果-双文件.JPG")]
    [InlineData("苹果双文件.HEIC")]
    [InlineData("vivo双文件.jpg")]
    [Trait("Category","RealSamples")]
    public async Task DualFileIdentity_DoesNotMeanSingleFileLive(string sample)
    {
        var result = await LivePhotoDiscoveryService.ScanFilesAsync(new() { ExplicitFiles = new[]{TestSampleResolver.ResolveSample(sample)} },DiscoveryScanMode.SplitOnly);
        Assert.False(Assert.Single(result.Items).IsLivePhoto);
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task IdentityIndex_CrossDirectoryRenamedAndIncrementalDuplicate()
    {
        using var ws = new TestFixtureWorkspace();
        Directory.CreateDirectory(Path.Combine(ws.RootDirectory,"a"));Directory.CreateDirectory(Path.Combine(ws.RootDirectory,"b"));
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.JPG"),Path.Combine(ws.RootDirectory,"a","renamed.jpg"));
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.MOV"),Path.Combine(ws.RootDirectory,"b","other.mov"));
        var options = new MergeScanOptions { InputDirectory = ws.RootDirectory, Recursive = true, Mode = PairingMode.Cid };
        using var session = new DiscoveryInspectionSession();
        Assert.Single((await LivePhotoMergeScanService.ScanAsync(options,default,null,session)).Pairs);
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.MOV"),Path.Combine(ws.RootDirectory,"a","duplicate.mov"));
        var conflict = await LivePhotoMergeScanService.ScanAsync(options,default,null,session);
        Assert.Empty(conflict.Pairs);Assert.Contains(conflict.Diagnostics,d=>d.DiagnosticCode=="ConflictDuplicateIdentity");
        Assert.Equal(0,session.SingleInspections+session.PairInspections+session.HintInspections);
    }

    [Fact]
    [Trait("Category","DerivedRealSamples")]
    public async Task AppleUuidCase_IsEquivalentAtDiscoveryAndExecution()
    {
        using var ws=new TestFixtureWorkspace();
        string image=Path.Combine(ws.RootDirectory,"image.jpg"),video=Path.Combine(ws.RootDirectory,"other.mov");
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.JPG"),image);
        var hint=await NativePairingHint.ReadAsync(image,default);Assert.NotNull(hint);
        byte[] bytes=File.ReadAllBytes(TestSampleResolver.ResolveSample("苹果-双文件.MOV"));
        byte[] original=Encoding.UTF8.GetBytes(hint.Identifier),lower=Encoding.UTF8.GetBytes(hint.Identifier.ToLowerInvariant());
        int replacements=0;
        for(int i=0;i<=bytes.Length-original.Length;i++)
            if(bytes.AsSpan(i,original.Length).SequenceEqual(original)){lower.CopyTo(bytes,i);i+=original.Length-1;replacements++;}
        Assert.True(replacements>0);File.WriteAllBytes(video,bytes);
        var result=await LivePhotoMergeScanService.ScanAsync(new(){InputDirectory=ws.RootDirectory,Mode=PairingMode.Cid});
        Assert.Single(result.Pairs);
        Assert.Null(await ProductInputAssessment.RejectionAsync(image,video,default,SelectionIntent.AppleIdentity));
    }

    [Theory]
    [InlineData(0,0)] [InlineData(1,1)] [InlineData(2,2)] [InlineData(3,0)]
    public void MergeSettingsMigration_PreservesValidChoices(int old,int expected) => Assert.Equal(expected,ScanSettingsMigration.MapMerge(old));
    [Theory]
    [InlineData(0,0)] [InlineData(1,0)] [InlineData(5,5)] [InlineData(8,0)] [InlineData(9,0)]
    public void SplitSettingsMigration_RemovesDualFileChoices(int old,int expected) => Assert.Equal(expected,ScanSettingsMigration.MapSplit(old));

    private static byte[] JpegMarker(string xml)
    {
        var payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0"+xml);
        var result = new byte[payload.Length+8];
        result[0]=255;result[1]=216;result[2]=255;result[3]=225;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4),checked((ushort)(payload.Length+2)));
        payload.CopyTo(result,6);result[^2]=255;result[^1]=218;
        return result;
    }
    [Theory]
    [InlineData("<r xmlns:g='http://ns.google.com/photos/1.0/camera/' g:MotionPhoto='1'/>",true)]
    [InlineData("<r xmlns:g='http://ns.google.com/photos/1.0/camera/' g:MotionPhoto='0'/>",false)]
    [InlineData("<r xmlns:g='unowned' g:MotionPhoto='1'/>",false)]
    [InlineData("<r xmlns:i='http://ns.google.com/photos/1.0/container/item/'><i:Semantic>GainMap</i:Semantic><i:Mime>image/jpeg</i:Mime></r>",false)]
    public async Task Markers_DoNotRequireVideo_AndOrdinaryHdrIsNotLive(string xml,bool expected)
    {
        using var ws=new TestFixtureWorkspace();string path=Path.Combine(ws.RootDirectory,"marker.jpg");
        File.WriteAllBytes(path,JpegMarker(xml));
        var marker=await NativeMediaCandidate.ReadAsync(path,default,DiscoveryCapability.SingleFileMarker);
        Assert.Equal(expected,marker?.SingleFileProtocol is not (null or SourceProtocol.Unknown));
        Assert.Null(marker?.Pairing);
    }

    [Fact]
    public async Task DisabledFlagAndIndependentVideoDeclaration_RetainCandidateWithConflict()
    {
        using var ws=new TestFixtureWorkspace();string path=Path.Combine(ws.RootDirectory,"conflict.jpg");
        File.WriteAllBytes(path,JpegMarker("<r xmlns:g='http://ns.google.com/photos/1.0/camera/' xmlns:c='http://ns.google.com/photos/1.0/container/' xmlns:i='http://ns.google.com/photos/1.0/container/item/' g:MotionPhoto='0'><c:Directory><c:Item i:Semantic='MotionPhoto' i:Mime='video/mp4'/></c:Directory></r>"));
        var index=new FileIndexService();
        var marker=await index.GetAsync(path,DiscoveryCapability.SingleFileMarker,default);
        Assert.NotEqual(SourceProtocol.Unknown,marker!.SingleFileProtocol);Assert.Equal(FactState.Incomplete,marker.State);
        await index.GetAsync(path,DiscoveryCapability.SingleFileMarker,default);Assert.Equal(1,index.Reads);
        await index.GetAsync(path,DiscoveryCapability.SingleFileMarker,default,retry:true);Assert.Equal(2,index.Reads);
    }

    [Fact]
    public async Task LargeMdatAndOverBudgetMetadata_AreNeverFullRead()
    {
        using var ws=new TestFixtureWorkspace();string path=Path.Combine(ws.RootDirectory,"large.mp4");
        static byte[] Header(uint size,string type) {var bytes=new byte[8];BinaryPrimitives.WriteUInt32BigEndian(bytes,size);Encoding.ASCII.GetBytes(type).CopyTo(bytes,4);return bytes;}
        using(var file=File.Create(path))
        {
            file.Write(Header(16,"ftyp"));file.Write(Encoding.ASCII.GetBytes("isom\0\0\0\0"));
            file.Write(Header(32*1024*1024,"mdat"));file.Seek(32*1024*1024-8,SeekOrigin.Current);
            file.Write(Header(8+5*1024*1024,"moov"));file.Write(Header(5*1024*1024,"meta"));
            file.SetLength(file.Position+5*1024*1024-8);
        }
        var fact=await NativeMediaCandidate.ReadAsync(path,default,DiscoveryCapability.AppleId);
        Assert.NotNull(fact);Assert.Equal(FactState.Incomplete,fact.State);Assert.Null(fact.Pairing);
        Assert.InRange(fact.BytesRead,1UL,256UL);
        using(var file=File.Open(path,FileMode.Open,FileAccess.Write,FileShare.None)) file.SetLength(19);
        fact=await NativeMediaCandidate.ReadAsync(path,default,DiscoveryCapability.AppleId);
        Assert.Equal(FactState.Incomplete,fact!.State);Assert.Null(fact.Pairing);
    }

    [Fact]
    public async Task PartialEnumeration_MustNotClaimCompleteIdentityScope()
    {
        var result=await LivePhotoMergeScanService.ScanAsync(new(){ExplicitFiles=new[]{Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".jpg")},Mode=PairingMode.Cid});
        Assert.True(result.IsPartial);Assert.Empty(result.Pairs);
    }

    [Fact]
    public void RetainedScope_PrunesKnownDeletion_WithoutOpeningSurvivingContent()
    {
        using var ws=new TestFixtureWorkspace();
        string live=Path.Combine(ws.RootDirectory,"live.jpg"),deleted=Path.Combine(ws.RootDirectory,"deleted.jpg");
        File.WriteAllText(live,"not media");File.WriteAllText(deleted,"not media");File.Delete(deleted);
        using var locked=File.Open(live,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        Assert.Equal(new[]{live},LivePhotoScanSupport.RetainSelections(new[]{live,deleted}).ToArray());
    }

    [Fact]
    public async Task CandidateBatches_AreBoundedAndCancellationRetainsPartialPairs()
    {
        using var ws=new TestFixtureWorkspace();
        for(int i=0;i<256;i++) {File.WriteAllText(Path.Combine(ws.RootDirectory,$"{i}.jpg"),"candidate");File.WriteAllText(Path.Combine(ws.RootDirectory,$"{i}.mp4"),"candidate");}
        using var cancellation=new CancellationTokenSource();int submitted=0;
        var result=await LivePhotoMergeScanService.ScanAsync(new(){InputDirectory=ws.RootDirectory},cancellation.Token,
            candidateBatch:(batch,token)=> {Assert.InRange(batch.Count,1,128);submitted+=batch.Count;cancellation.Cancel();return Task.CompletedTask;});
        Assert.Equal(ScanOperationStatus.Cancelled,result.Status);Assert.True(result.IsPartial);
        Assert.InRange(submitted,1,128);Assert.Equal(submitted,result.Pairs.Count);
        // Remaining enumerated files have not been classified when cancellation stops matching.
        Assert.Equal(0, result.StandaloneImagesCount);
        Assert.Equal(0, result.StandaloneVideosCount);
        Assert.Empty(result.StandaloneImagePaths);
        Assert.Empty(result.StandaloneVideoPaths);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.DiagnosticCode is "UnmatchedImage" or "UnmatchedVideo");
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task RepairDefaultScope_ExcludesOtherProtocolsBeforeDeepInspection()
    {
        using var session=new DiscoveryInspectionSession();
        using var batch=new RepairBatchContext { DiscoverySession=session };
        var analysis=await LivePhotoRepairService.AnalyzeFileInBatchAsync(TestSampleResolver.ResolveSample("oppo.jpg"),default,true,batch);
        Assert.Equal(RepairIssueType.NonApple,analysis.IssueType);
        Assert.Equal(0,session.SingleInspections+session.PairInspections);
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task RepairActionChange_OnlySupplementsMissingChoices()
    {
        using var batch=new RepairBatchContext { DiagnosticOptions=new() {FixImageRotation=true,StripImageThumbnail=false,FixHeicOrientation=false,FixVideoRotation=false} };
        string path=TestSampleResolver.ResolveSample("苹果-双文件.JPG");
        var first=await LivePhotoRepairService.AnalyzeFileInBatchAsync(path,default,true,batch);
        Assert.Equal(1u,first.DiagnosedActionMask);
        var selected=new RepairOptions {FixImageRotation=true,StripImageThumbnail=true,FixHeicOrientation=false,FixVideoRotation=false};
        var supplemented=await LivePhotoRepairService.SupplementAnalysisAsync(path,first,selected,true,default);
        Assert.Equal(3u,supplemented.DiagnosedActionMask);
        Assert.Same(supplemented,await LivePhotoRepairService.SupplementAnalysisAsync(path,supplemented,selected,true,default));
    }

    [Fact]
    public void CoverFallback_UsesFirstValidFrameAndRecordsLoss()
    {
        var changes=new System.Collections.Generic.List<NotCarriedItem>();
        var timestamp=LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(new(){EvidenceState=NeutralEvidenceState.Unknown,
            DurationUs=1_000_000,FirstValidFrameTimestampUs=40_000},null,PreservationPolicy.BestEffort,changes);
        Assert.Equal(40_000,timestamp);Assert.Single(changes);
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task Composition_ExistingCompoundPrimaryRetainsImageGraph()
    {
        using var workspace = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2,OutputMediaFormat.JpegMp4);
        string sample = TestSampleResolver.ResolveSample("vivo.jpg");
        string plain = TestSampleResolver.ResolveSample("vivo双文件.mp4");
        var bundle = await new LivePhotoBox.Media.NeutralMediaService().CreateCompositionBundleAsync(sample,plain,workspace,
            profile.MediaRequirement,PreservationPolicy.BestEffort,default);
        string? capture = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_LIGHTWEIGHT_CAPTURE");
        if (capture != null)
        {
            Directory.CreateDirectory(capture);
            File.Copy(bundle.PrimaryImage.Path,Path.Combine(capture,"neutral.jpg"),true);
            if(bundle.MotionVideo!=null)File.Copy(bundle.MotionVideo.Path,Path.Combine(capture,"neutral.mp4"),true);
        }
        var facts = await new LivePhotoBox.Media.Inspection.SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        Assert.True(facts.PrimaryImage.IsPresent);
        Assert.Equal(SourceProtocol.NonLive,facts.Protocol);
        Assert.NotNull(bundle.MotionVideo);
        Assert.NotNull(bundle.GainMap);
        Assert.True(facts.GainMap?.IsPresent == true);
        output.WriteLine($"image={bundle.PrimaryImage.ByteLength}; gainmap={bundle.GainMapRepresentation}; auxiliary={bundle.AuxiliaryMedia.Count}");
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task Composition_PreservesSelectedVideoCover_WithoutItsOriginalImage()
    {
        using var workspace=new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string selectedVideo=TestSampleResolver.ResolveSample("苹果-双文件.MOV");
        var original=await new LivePhotoBox.Media.Inspection.SourceInspector().InspectAsync(
            TestSampleResolver.ResolveSample("苹果-双文件.JPG"),selectedVideo);
        var profile=OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2,OutputMediaFormat.JpegMp4);
        var bundle=await new LivePhotoBox.Media.NeutralMediaService().CreateCompositionBundleAsync(
            TestSampleResolver.ResolveSample("oppo.jpg"),selectedVideo,workspace,profile.MediaRequirement,PreservationPolicy.BestEffort,default);
        Assert.True(original.Timing.CoverTimestampUs>=0);
        Assert.Equal(original.Timing.CoverTimestampUs,bundle.Semantics.Timing.CoverTimestampUs);
        Assert.Contains(bundle.Semantics.Timing.EvidenceState,new[]{NeutralEvidenceState.VerifiedPreserved,NeutralEvidenceState.VerifiedConverted,NeutralEvidenceState.Verified});
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task CompositionRerun_AvoidsOccupiedIsolationOutput_WithoutOverwriting()
    {
        using var ws=new TestFixtureWorkspace();
        var options=new LivePhotoMergeRunOptions { OutputDirectory=Path.Combine(ws.RootDirectory,"out"), KeyPhotoTimestampUs=10_000_000,
            OutputProfile=OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2,OutputMediaFormat.JpegMp4) };
        string image=TestSampleResolver.ResolveSample("oppo.jpg"),video=TestSampleResolver.ResolveSample("vivo双文件.mp4");
        var first=await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(image,video,"chosen",1,options,default);
        Assert.Equal(ProductOperationStatus.Warning,first.Status);
        var before=Assert.Single(first.CommittedOutputs);
        var digest=System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(before.Path));
        var second=await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(image,video,"chosen",1,options,default);
        Assert.Equal(ProductOperationStatus.Warning,second.Status);
        Assert.NotEqual(before.Path,Assert.Single(second.CommittedOutputs).Path);
        Assert.Equal(digest,System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(before.Path)));
    }
}

