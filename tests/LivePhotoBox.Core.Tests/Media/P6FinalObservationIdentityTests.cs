using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P6FinalObservationIdentityTests
{
    [Theory]
    [InlineData("video-write")]
    [InlineData("video-delete")]
    [InlineData("image-write")]
    [InlineData("gainmap-write")]
    [InlineData("namespace-rename")]
    [Trait("Category", "RealSamples")]
    public async Task P6_FinalObservations_RetainReadLeasesAcrossNativeProbe(string attack)
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new CapturingCleaner();
        var probe = new AttackingProbe(cleaner, workspace, attack);
        var bundle = await new NeutralMediaService(cleaner: cleaner, videoConverter: probe)
            .CreateNeutralBundleAsync(TestSampleResolver.ResolveSample("vivo.jpg"), null, workspace);

        Assert.True(probe.AttackAttempted);
        Assert.True(probe.AttackBlocked);
        Assert.NotNull(bundle.MotionVideo);
        var actual = await new VideoConverter().ProbeAsync(bundle.MotionVideo.Path);
        Assert.Equal(actual.Container, bundle.MotionVideo.VideoContainer);
        Assert.Equal(actual.RotationDegrees, bundle.Semantics.Orientation.MotionVideo!.ClockwiseRotationDegrees);
        foreach (var row in bundle.Manifest.Where(r => r.Role is "PrimaryImage" or "MotionVideo"))
            Assert.Equal(row.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(row.Path))), ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_FinalObservations_RejectPreexistingWriterAndReleaseAcquiredLeases()
    {
        using var workspace = new MediaWorkspace();
        var cleaner = new CapturingCleaner(keepWriter: true);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new NeutralMediaService(cleaner: cleaner)
                .CreateNeutralBundleAsync(TestSampleResolver.ResolveSample("vivo.jpg"), null, workspace));
            Assert.NotNull(cleaner.Result);
        }
        finally { cleaner.Writer?.Dispose(); }
        // Partial lease acquisition must unwind before the caller cleans up.
        using var exclusive = File.Open(cleaner.Result!.CleanedImage!.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_FinalObservations_RejectByteIdenticalReplacementObjectBeforeObservation()
    {
        using var fixture = new TestFixtureWorkspace();
        using var workspace = new MediaWorkspace();
        var cleaner = new ReplacingCleaner(fixture);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new NeutralMediaService(cleaner: cleaner)
                .CreateNeutralBundleAsync(TestSampleResolver.ResolveSample("红米老款-GV1.JPG"), null, workspace));
            Assert.True(cleaner.Replaced);
        }
        finally { cleaner.Restore(); }
    }

    private sealed class CapturingCleaner(bool keepWriter = false) : ISourceProtocolCleaner
    {
        internal ProtocolCleanResult? Result { get; private set; }
        internal FileStream? Writer { get; private set; }
        public async Task<ProtocolCleanResult> CleanAsync(ProtocolCleanRequest request, IMediaWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            Result = await new SourceProtocolCleaner().CleanAsync(request, workspace, cancellationToken);
            if (keepWriter)
                Writer = File.Open(Result.CleanedVideo!.Path, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            return Result;
        }
    }

    private sealed class AttackingProbe(CapturingCleaner cleaner, IMediaWorkspace workspace, string attack) : IVideoConverter
    {
        internal bool AttackAttempted { get; private set; }
        internal bool AttackBlocked { get; private set; }
        private readonly VideoConverter _inner = new();
        public Task<VideoConversionResult> ConvertAsync(VideoConversionRequest request, CancellationToken cancellationToken = default) =>
            _inner.ConvertAsync(request, cancellationToken);
        public async Task<VideoFacts> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            AttackAttempted = true;
            try
            {
                switch (attack)
                {
                    case "video-write": using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } break;
                    case "video-delete": File.Delete(path); break;
                    case "image-write": using (File.Open(cleaner.Result!.CleanedImage!.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } break;
                    case "gainmap-write": using (File.Open(cleaner.Result!.CleanedGainMap!.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } break;
                    case "namespace-rename": Directory.Move(workspace.RootDirectory, workspace.RootDirectory + "-foreign"); break;
                    default: throw new ArgumentOutOfRangeException(nameof(attack));
                }
            }
            catch (IOException) { AttackBlocked = true; }
            return await _inner.ProbeAsync(path, cancellationToken);
        }
    }

    private sealed class ReplacingCleaner(IMediaWorkspace fixture) : ISourceProtocolCleaner
    {
        private string? _path, _original, _foreign;
        internal bool Replaced { get; private set; }
        public async Task<ProtocolCleanResult> CleanAsync(ProtocolCleanRequest request, IMediaWorkspace workspace,
            CancellationToken cancellationToken = default)
        {
            var result = await new SourceProtocolCleaner().CleanAsync(request, workspace, cancellationToken);
            _path = result.CleanedImage!.Path;
            _original = fixture.AllocateFilePath("producer-original", ".jpg");
            _foreign = fixture.AllocateFilePath("foreign-replacement", ".jpg");
            File.Copy(_path, _foreign);
            File.Move(_path, _original);
            File.Move(_foreign, _path);
            Replaced = true;
            return result;
        }
        internal void Restore()
        {
            if (!Replaced) return;
            File.Move(_path!, _foreign!);
            File.Move(_original!, _path!);
        }
    }
}
