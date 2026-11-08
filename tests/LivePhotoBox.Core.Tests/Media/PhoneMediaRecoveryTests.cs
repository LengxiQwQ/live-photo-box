using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class PhoneMediaRecoveryTests
{
    [Fact]
    public async Task UnreadableStillDoesNotHideReadableExplicitMovie()
    {
        var folder = new RecoveryFolder(nameof(UnreadableStillDoesNotHideReadableExplicitMovie));
        string image = Path.Combine(folder.Path, "bad.jpg"), video = Path.Combine(folder.Path, "good.mp4");
        File.WriteAllBytes(image, new byte[32]);
        File.WriteAllBytes(video, P9TargetTestInputs.Video);
        byte[] imageHash = SHA256.HashData(File.ReadAllBytes(image)), videoHash = SHA256.HashData(File.ReadAllBytes(video));
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        ProductOperationOutcome result;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(image, video, "recovery", 0,
                new LivePhotoMergeRunOptions { OutputDirectory = Path.Combine(folder.Path, "output"),
                    OutputProfile = OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4) }, default);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.Equal(ProductCompletionKind.RecoveredMedia, result.CompletionKind);
        Assert.Equal(ProductOperationStatus.Warning, result.Status);
        Assert.Null(result.SourceActionGuard);
        var recovered = Assert.Single(result.CommittedOutputs, x => x.Role == "RecoveredMotionVideo");
        Assert.Equal(videoHash, SHA256.HashData(File.ReadAllBytes(recovered.Path)));
        Assert.Single(result.CommittedOutputs, x => x.Role == "OriginalUnconverted");
        Assert.DoesNotContain(result.CommittedOutputs, x => x.Role == "TargetLivePhoto");
        Assert.Equal(imageHash, SHA256.HashData(File.ReadAllBytes(image)));
        Assert.Equal(videoHash, SHA256.HashData(File.ReadAllBytes(video)));
    }

    private sealed record QueueTask(int Index, string ImagePath, string VideoPath) : LivePhotoBox.Models.IMergeTaskInfo
    {
        public string BaseName => "item-" + Index;
        public LivePhotoBox.Models.ProcessStatus Status { get; set; }
        public string Details { get; set; } = string.Empty;
    }

    [Fact]
    public async Task LockedItemDoesNotBlockIndependentPairOrChangeOriginals()
    {
        var folder = new RecoveryFolder(nameof(LockedItemDoesNotBlockIndependentPairOrChangeOriginals));
        string blocked = Path.Combine(folder.Path,"blocked.jpg"), good = Path.Combine(folder.Path,"good.jpg"), video = Path.Combine(folder.Path,"video.mp4");
        File.WriteAllBytes(blocked,P9TargetTestInputs.Image);
        File.WriteAllBytes(good,P9TargetTestInputs.Image);
        File.WriteAllBytes(video,P9TargetTestInputs.Video);
        byte[] before=SHA256.HashData(File.ReadAllBytes(good));
        using var locked=new FileStream(blocked,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        using var pause=new ManualResetEventSlim(true);
        var outcomes=new Dictionary<int,ProductOperationOutcome>();
        var counts=new List<int>();
        await LivePhotoMergeRunnerService.RunWithOutcomesAsync(
            [new QueueTask(1,blocked,video),new QueueTask(2,good,video)],
            new LivePhotoMergeRunOptions { OutputDirectory=Path.Combine(folder.Path,"output"),
                OutputProfile=OutputProfile.Create(TargetProtocol.OppoLivePhoto,OutputMediaFormat.JpegMp4),
                MaxDegreeOfParallelism=1,TaskStartInterval=TimeSpan.Zero },pause,default,null,
            (task,outcome,count)=>{outcomes.Add(task.Index,outcome);counts.Add(count);});
        Assert.Equal(ProductCompletionKind.NoOutput,outcomes[1].CompletionKind);
        Assert.Empty(outcomes[1].CommittedOutputs);
        Assert.Contains(outcomes[2].CompletionKind!.Value,new[]{ProductCompletionKind.TargetCompleted,ProductCompletionKind.CompatibleCompleted});
        Assert.Contains(outcomes[2].CommittedOutputs,x=>x.Role=="TargetLivePhoto");
        Assert.Equal(new[]{1,2},counts);
        Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(good)));
    }

    private sealed class RecoveryFolder
    {
        internal string Path { get; }
        internal RecoveryFolder(string name)
        {
            Path = System.IO.Path.Combine(P9TargetTestInputs.Root, "phone-resilience", name);
            Directory.CreateDirectory(Path);
        }
    }
    [Fact]
    public async Task MalformedExplicitVideoSavesOriginalsWithoutExpectedException()
    {
        var folder = new RecoveryFolder(nameof(MalformedExplicitVideoSavesOriginalsWithoutExpectedException));
        string image = Path.Combine(folder.Path, "input.jpg"), video = Path.Combine(folder.Path, "input.mov");
        File.WriteAllBytes(image, P9TargetTestInputs.Image);
        File.WriteAllBytes(video, new byte[32]);
        var before = new[] { image, video }.Select(p => SHA256.HashData(File.ReadAllBytes(p))).ToArray();
        var firstChance = new ConcurrentQueue<string>();
        EventHandler<FirstChanceExceptionEventArgs> observe = (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("LivePhotoBox.", StringComparison.Ordinal) == true)
                firstChance.Enqueue(e.Exception.ToString());
        };
        AppDomain.CurrentDomain.FirstChanceException += observe;
        ProductOperationOutcome result;
        try
        {
            result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(image, video, "input", 0,
                new LivePhotoMergeRunOptions { OutputDirectory = Path.Combine(folder.Path, "out"),
                    OutputProfile = OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4) }, default);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
        Assert.Empty(firstChance);
        Assert.Equal(ProductOperationStatus.Warning, result.Status);
        Assert.Equal(ProductCompletionKind.OriginalSaved, result.CompletionKind);
        Assert.Equal(2, result.CommittedOutputs.Count);
        Assert.Null(result.SourceActionGuard);
        Assert.NotNull(result.IsolationDirectory);
        Assert.All(result.CommittedOutputs, a => Assert.True(File.Exists(a.Path)));
        for (int i = 0; i < 2; i++) Assert.Equal(before[i], SHA256.HashData(File.ReadAllBytes(new[] { image, video }[i])));
    }

    [Fact]
    public async Task MissingSourceReturnsNoOutputWithoutThrowing()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(MissingSourceReturnsNoOutputWithoutThrowing));
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(
            Path.Combine(folder.Path,"absent.jpg"),Path.Combine(folder.Path,"absent.mov"),"absent",0,
            new LivePhotoMergeRunOptions { OutputDirectory = Path.Combine(folder.Path,"out"),
                OutputProfile = OutputProfile.Create(TargetProtocol.OppoLivePhoto,OutputMediaFormat.JpegMp4) },default);
        Assert.Equal(ProductCompletionKind.NoOutput,result.CompletionKind);
        Assert.Empty(result.CommittedOutputs);
    }

    [Fact]
    public async Task UnalignedJpegCanNormalizeWithoutCropping()
    {
        var folder = new RecoveryFolder(nameof(UnalignedJpegCanNormalizeWithoutCropping));
        string source = Path.Combine(folder.Path,"unaligned.jpg"), output = Path.Combine(folder.Path,"normalized.jpg");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red,33,17))
        {
            var exif = new ImageMagick.ExifProfile();
            exif.SetValue(ImageMagick.ExifTag.Orientation,(ushort)6);
            image.SetProfile(exif);
            image.Orientation = ImageMagick.OrientationType.RightTop;
            image.Write(source,ImageMagick.MagickFormat.Jpeg);
        }
        if (File.Exists(output)) File.Delete(output);
        var result = await LivePhotoBox.Interop.NativeMediaService.TransformJpegLosslesslyWithOutputEvidenceAsync(
            source,output,7,default,allowReencode:true);
        Assert.Equal(LivePhotoBox.Interop.NativeResult.Ok,result.ResultCode);
        Assert.True(result.Reencoded);
        Assert.NotNull(result.OutputEvidence);
        using var actual = new ImageMagick.MagickImage(output);
        Assert.Equal(17u,actual.Width);
        Assert.Equal(33u,actual.Height);
        Assert.Equal((ushort)1,actual.GetExifProfile()!.GetValue(ImageMagick.ExifTag.Orientation)!.Value);
        var repaired = await LivePhotoRepairService.RepairImageFileAsync(source,Path.Combine(folder.Path,"repair-out"),
            allDevices:true,dryRun:false,new LivePhotoBox.Models.RepairOptions {
                FixImageRotation=true,StripImageThumbnail=false,FixHeicOrientation=false,FixVideoRotation=false },default);
        Assert.Equal("warning",repaired.Status);
        Assert.Equal("warning",repaired.ImageStatus);
        Assert.NotNull(repaired.Image);
        Assert.NotEqual(Path.Combine(folder.Path,"repair-out"),Path.GetDirectoryName(repaired.Image!));
        Assert.True(Path.GetFullPath(repaired.Image!).StartsWith(
            Path.GetFullPath(Path.Combine(folder.Path,"repair-out"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase));
    }
}
