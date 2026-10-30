using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8ProductIntegrationTests
{
    private static string Source => Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples/小米.jpg"));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static LivePhotoMergeRunOptions Options(string folder, string? destination = null, bool overwrite = false) => new()
    {
        OutputDirectory = folder, OutputFilePath = destination, OverwriteExisting = overwrite,
        OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
        StructuredOptions = new() { PreservationPolicy = PreservationPolicy.Strict }
    };

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task OriginalSingleFilePublishesStructuredTargetAndBindsActualOutput()
    {
        Assert.True(File.Exists(Source), "Required original Xiaomi sample missing.");
        string before = Hash(Source);
        using var folder = new P9TargetTestInputs.Folder(nameof(OriginalSingleFilePublishesStructuredTargetAndBindsActualOutput));
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(Source, "", "target", 1,
            Options(folder.Path), CancellationToken.None);
        Assert.True(result.IsSuccess, result.Details);
        Assert.Equal(new[] { Source }, result.Inputs);
        var output = Assert.Single(result.CommittedOutputs);
        Assert.Equal(Hash(output.Path), output.Sha256);
        Assert.Equal(new FileInfo(output.Path).Length, output.ByteLength);
        Assert.NotEmpty(result.PreparationManifest);
        Assert.Equal(ProductTargetPreservation.Unknown, result.FinalTargetPreservation);
        Assert.Equal(before, Hash(Source));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "RealSamples")]
    public async Task SourcePathAndHardLinkCannotBeOverwritten(bool hardLink)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(SourcePathAndHardLinkCannotBeOverwritten) + hardLink);
        string original = Path.Combine(folder.Path, "original.jpg"), target = Path.Combine(folder.Path, "alias.jpg");
        File.Copy(Source, original);
        if (hardLink) Assert.True(CreateHardLinkW(target, original, IntPtr.Zero), Marshal.GetLastWin32Error().ToString());
        else target = original;
        string before = Hash(original);
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(original, "", "target", 1,
            Options(folder.Path, target, overwrite: true), CancellationToken.None);
        Assert.False(result.IsSuccess, result.Details);
        Assert.Empty(result.CommittedOutputs);
        Assert.Equal(before, Hash(original));
        Assert.Equal(before, Hash(target));
        Assert.Equal(hardLink ? 2 : 1, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task RealDestinationShareLockFailsWithoutPublishingOrLeavingStage()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(RealDestinationShareLockFailsWithoutPublishingOrLeavingStage));
        string target = Path.Combine(folder.Path, "locked.jpg");
        File.WriteAllText(target, "foreign destination");
        string before = Hash(Source);
        using (var locked = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(Source, "", "target", 1,
                Options(folder.Path, target, overwrite: true), CancellationToken.None);
            Assert.False(result.IsSuccess, result.Details);
            Assert.Empty(result.CommittedOutputs);
        }
        Assert.Equal("foreign destination", File.ReadAllText(target));
        Assert.Equal(before, Hash(Source));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task EarlierBatchOutputCannotOverwriteALaterTasksOriginal()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(EarlierBatchOutputCannotOverwriteALaterTasksOriginal));
        string a = Path.Combine(folder.Path, "a.jpg"), b = Path.Combine(folder.Path, "b.jpg");
        File.Copy(Source, a); File.Copy(Source, b);
        string before = Hash(a);
        var tasks = new[] { new InputTask(1, a), new InputTask(2, b) };
        var outcomes = new List<ProductOperationOutcome>();
        var options = new LivePhotoMergeRunOptions
        {
            OutputDirectory = folder.Path, OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            NamingRuleIndex = 2, CustomNamingPattern = "b", OverwriteExisting = true,
            TaskStartInterval = TimeSpan.Zero, MaxDegreeOfParallelism = 1
        };
        using var pause = new ManualResetEventSlim(true);
        await LivePhotoMergeRunnerService.RunWithOutcomesAsync(tasks, options, pause, CancellationToken.None,
            null, (_, outcome, _) => outcomes.Add(outcome));
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, result => { Assert.False(result.IsSuccess); Assert.Empty(result.CommittedOutputs); });
        Assert.Equal(before, Hash(a)); Assert.Equal(before, Hash(b));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    private sealed record InputTask(int Index, string ImagePath) : IMergeTaskInfo
    {
        public string VideoPath => "";
        public string BaseName => Path.GetFileNameWithoutExtension(ImagePath);
        public ProcessStatus Status { get; set; }
        public string Details { get; set; } = "";
    }

    [Fact]
    public async Task PrecancelledOperationDoesNotPublish()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(PrecancelledOperationDoesNotPublish));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(Source, "", "target", 1,
            Options(folder.Path), cancellation.Token);
        Assert.Equal(ProductOperationStatus.Cancelled, result.Status);
        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.CommittedOutputs);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
}
