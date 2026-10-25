using System.Collections.Concurrent;
using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8ProductOutputTests
{
    [Theory]
    [InlineData(1, "plainmotionphoto.jpg")]
    [InlineData(2, "plain_motionphoto_001.jpg")]
    [Trait("Category", "DerivedRealSamples")]
    public async Task MixedBatch_PreservesRelativeDirectoryNamingAndIndividualResults(int namingRule, string fileName)
    {
        string inputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
            ?? throw new InvalidOperationException("Frozen derived ordinary media inputs are required.");
        using var workspace = new TestFixtureWorkspace();
        string inputRoot = Path.Combine(workspace.RootDirectory, "input");
        string nested = Path.Combine(inputRoot, "nested");
        Directory.CreateDirectory(nested);
        string image = Path.Combine(nested, "plain.jpg"), video = Path.Combine(nested, "plain.mp4");
        File.Copy(Path.Combine(inputs, "image.jpg"), image);
        File.Copy(Path.Combine(inputs, "video.mp4"), video);
        byte[] imageHash = SHA256.HashData(await File.ReadAllBytesAsync(image));
        byte[] videoHash = SHA256.HashData(await File.ReadAllBytesAsync(video));
        string output = Path.Combine(workspace.RootDirectory, "output");
        IMergeTaskInfo[] tasks = [new QueueTask(1, image, video, "plain"),
            new QueueTask(2, Path.Combine(nested, "missing.jpg"), Path.Combine(nested, "missing.mp4"), "missing")];
        var results = new ConcurrentDictionary<int, bool>();
        var counts = new ConcurrentBag<int>();
        using var pause = new ManualResetEventSlim(true);
        await LivePhotoMergeRunnerService.RunAsync(tasks, new LivePhotoMergeRunOptions
        {
            OutputDirectory = output,
            OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            NamingRuleIndex = namingRule,
            CustomNamingPattern = "{name}_{protocol}_{counter:D3}",
            PreserveSubfolders = true,
            InputDirectory = inputRoot,
            MaxDegreeOfParallelism = 2
        }, pause, CancellationToken.None, null, (task, success, _, completed) =>
        {
            Assert.True(results.TryAdd(task.Index, success));
            counts.Add(completed);
            task.Status = success ? ProcessStatus.Success : ProcessStatus.Failed;
        });

        Assert.True(results[1]);
        Assert.False(results[2]);
        Assert.Equal(ProcessStatus.Success, tasks[0].Status);
        Assert.Equal(ProcessStatus.Failed, tasks[1].Status);
        Assert.Equal(new[] { 1, 2 }, counts.Order());
        string published = Path.Combine(output, "nested", fileName);
        Assert.Equal(new[] { published }, Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.True(new FileInfo(published).Length > 0);
        Assert.Equal(imageHash, SHA256.HashData(await File.ReadAllBytesAsync(image)));
        Assert.Equal(videoHash, SHA256.HashData(await File.ReadAllBytesAsync(video)));
    }

    private sealed record QueueTask(int Index, string ImagePath, string VideoPath, string BaseName) : IMergeTaskInfo
    {
        public ProcessStatus Status { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
