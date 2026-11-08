using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NeutralObservationResultTests
{
    [Theory]
    [InlineData("gainmap", "missing")]
    [InlineData("gainmap", "cancelled")]
    [InlineData("gainmap", "no-gainmap")]
    [InlineData("timing", "missing")]
    [InlineData("timing", "cancelled")]
    [InlineData("timing", "stale-hash")]
    [InlineData("timing", "valid")]
    public async Task ProductObservationReturnsExpectedOutcomesWithoutFirstChanceEvents(string route, string condition)
    {
        using var workspace = new TestFixtureWorkspace();
        string image = Path.Combine(workspace.RootDirectory, "source.jpg"), video = Path.Combine(workspace.RootDirectory, "source.mp4");
        File.WriteAllBytes(image, P9TargetTestInputs.Image); File.WriteAllBytes(video, P9TargetTestInputs.Video);
        var facts = await new SourceInspector().InspectAsync(image, video);
        if (condition == "stale-hash") facts = facts with { PrimarySha256 = new string('0', 64) };
        var artifact = new MediaArtifact { Path = video, Kind = MediaArtifactKind.MotionVideo,
            VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.H264,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(video))), ByteLength = new FileInfo(video).Length };
        using var cancel = new CancellationTokenSource(); if (condition == "cancelled") cancel.Cancel();
        if (condition == "missing") image = Path.Combine(workspace.RootDirectory, "missing.jpg");
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        Exception? failure;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            if (route == "gainmap") failure = (await NativeMediaService.TryInspectGainMapMetadataAsync(image, cancellationToken: cancel.Token)).Failure;
            else
            {
                var attempt = await NativeNeutralTiming.TryObserveAsync(image, video, artifact, facts, cancel.Token);
                failure = attempt.Failure;
                if (condition == "valid") Assert.NotNull(attempt.Timing);
                else Assert.Null(attempt.Timing);
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        if (condition == "valid") Assert.Null(failure); else Assert.NotNull(failure);
        if (condition == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }
}
