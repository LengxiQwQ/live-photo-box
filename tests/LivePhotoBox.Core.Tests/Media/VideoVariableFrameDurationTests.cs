using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using System.Security.Cryptography;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class VideoVariableFrameDurationTests
{
    [Theory]
    [Trait("Category", "Synthetic")]
    [InlineData("vfr-silent-last-short.mp4", 8)]
    [InlineData("bframes-silent-cfr.mp4", 16)]
    public async Task SidecarKeepsSourceEndTimeForVfrAndDecoderDrain(string fixture, int expectedFrames)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "synthetic", fixture);
        string expectedHash = fixture == "vfr-silent-last-short.mp4"
            ? "B267AC6156E653BB268FC06E205F4271AD5A16B2732A0D00BAA492921A9BDFF6"
            : "F00813467BE53AF70E1B9A95EBE5FA298D23FAF8993CD1C1FBCC81042B7BEBAA";
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        using var workspace = new TestFixtureWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "hevc.mp4");
        var attempt = await NativeMediaService.TranscodeVideoAttemptAsync(source, output,
            VideoContainer.Mp4, VideoCodec.Hevc, 23, preservationPolicy: PreservationPolicy.BestEffort);
        Assert.True(attempt.Succeeded, attempt.FailureException?.ToString());
        Assert.Equal(VideoBackend.MinimalLibav, attempt.Diagnostics.Backend);
        var sourceFacts = await new SourceInspector().InspectAsync(source);
        var artifact = new MediaArtifact { Kind = MediaArtifactKind.MotionVideo, Path = output,
            ByteLength = new FileInfo(output).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))) };
        var timing = await NativeNeutralTiming.ObserveAsync(source, null, artifact, sourceFacts);
        Assert.Equal(expectedFrames, timing.TotalFrames);
        // These fixed fixtures independently end at 1.6 s. VFR has eight
        // presentation stamps ending at 1.5 s, with a 0.1 s final interval;
        // guessing the 0.2 s average frame duration is incorrect.
        Assert.Equal(1_600_000, timing.DurationUs);
    }
}
