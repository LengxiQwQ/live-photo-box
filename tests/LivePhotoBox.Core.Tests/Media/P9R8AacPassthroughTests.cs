using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8AacPassthroughTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(TargetProtocol.GoogleMicroVideoV1, 1u)]
    [InlineData(TargetProtocol.GoogleMotionPhotoV2, 2u)]
    [InlineData(TargetProtocol.OppoLivePhoto, 3u)]
    public async Task ExplicitSbrAudioPassesThroughWithoutReencoding(TargetProtocol target, uint nativeTarget)
    {
        string source = Cached("vivo双文件.jpg"), companion = Cached("vivo双文件.mp4");
        string imageHash = Hash(source), companionHash = Hash(companion);
        using var folder = new P9TargetTestInputs.Folder(nameof(ExplicitSbrAudioPassesThroughWithoutReencoding) + target);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(target, OutputMediaFormat.JpegMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, companion, workspace,
            profile.MediaRequirement, PreservationPolicy.Strict);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(VideoCodec.H264, bundle.MotionVideo.VideoCodec);
        Assert.Equal(PreservationOutcome.Preserved, Assert.Single(bundle.Manifest, r => r.Role == "MotionVideo").PreservationOutcome);
        byte[] prepared = File.ReadAllBytes(bundle.MotionVideo.Path);
        string output = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false, CancellationToken.None,
            oppoInitialCoverPolicy: nativeTarget == 3 ? OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover : OppoInitialCoverPolicy.RequireKnownOriginal);
        var expected = new TargetValidationExpectations(nativeTarget, 0, (uint)bundle.MotionVideo.VideoCodec, bundle.Semantics.Timing.CoverTimestampUs)
        {
            UseV2 = true,
            PrimaryTimestampUs = nativeTarget == 3 ? bundle.Semantics.Timing.PrimaryTimestampUs ?? bundle.Semantics.Timing.CoverTimestampUs : null
        };
        using var reader = File.OpenRead(output);
        var observed = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(observed, expected);
        byte[] actual = File.ReadAllBytes(output);
        Assert.Equal(prepared, actual.AsSpan(checked((int)observed.Observed.VideoOffset), checked((int)observed.Observed.VideoLength)).ToArray());
        Assert.Equal(imageHash, Hash(source)); Assert.Equal(companionHash, Hash(companion));
    }
    private static string Cached(string name)
    {
        string path = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples", name));
        Assert.True(File.Exists(path), "Required RealSample is missing: " + path); return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
