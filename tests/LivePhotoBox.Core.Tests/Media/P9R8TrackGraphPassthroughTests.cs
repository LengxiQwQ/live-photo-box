using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8TrackGraphPassthroughTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV", TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMov, 1u)]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV", TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMov, 2u)]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV", TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.HeicMov, 2u)]
    public async Task OrdinaryMetadataAuxiliaryTracksAndPcmRemainInTargetMovie(string primaryName, string companionName,
        TargetProtocol target, OutputMediaFormat format, uint nativeTarget)
    {
        string primary = Cached(primaryName), companion = Cached(companionName);
        string primaryHash = Hash(primary), companionHash = Hash(companion);
        using var folder = new P9TargetTestInputs.Folder(nameof(OrdinaryMetadataAuxiliaryTracksAndPcmRemainInTargetMovie) + primaryName + target);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(target, format);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(primary, companion, workspace,
            profile.MediaRequirement, PreservationPolicy.BestEffort);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(VideoContainer.Mov, bundle.MotionVideo.VideoContainer);
        byte[] prepared = File.ReadAllBytes(bundle.MotionVideo.Path);
        string output = Path.Combine(folder.Path, format == OutputMediaFormat.HeicMov ? "target.heic" : "target.jpg");
        var auxiliaries = bundle.Manifest.Where(m => m.Role is "GainMap" or "Auxiliary").Select(m =>
            new TargetAuxiliaryExpectation(m.Semantic switch { "GainMap" => 1u, "Original" => 2u, _ => throw new InvalidDataException("Unexpected test asset") },
                (uint)m.ImageContainer, checked((ulong)m.ByteLength), m.Sha256)).ToArray();
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false, CancellationToken.None);
        var expected = new TargetValidationExpectations(nativeTarget, (uint)format, (uint)bundle.MotionVideo.VideoCodec, bundle.Semantics.Timing.CoverTimestampUs)
        { UseV2 = true, Auxiliaries = auxiliaries };
        using var reader = File.OpenRead(output);
        var observed = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(observed, expected);
        byte[] actual = File.ReadAllBytes(output);
        Assert.Equal(prepared, actual.AsSpan(checked((int)observed.Observed.VideoOffset), checked((int)observed.Observed.VideoLength)).ToArray());
        Assert.Equal(primaryHash, Hash(primary)); Assert.Equal(companionHash, Hash(companion));
        string? root = Environment.GetEnvironmentVariable("LPB_P9R8_TRACK_OUTPUT_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            string destination = Path.Combine(root, primaryName + "-" + target + "-" + format);
            if (Directory.Exists(destination)) throw new IOException("Snapshot is create-only: " + destination);
            Directory.CreateDirectory(destination);
            File.Copy(output, Path.Combine(destination, Path.GetFileName(output)), false);
            File.Copy(bundle.PrimaryImage.Path, Path.Combine(destination, "prepared" + Path.GetExtension(bundle.PrimaryImage.Path)), false);
            File.WriteAllBytes(Path.Combine(destination, "prepared.mov"), prepared);
            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(m => m.ModuleName.Equals("LivePhotoBox.Native.dll", StringComparison.OrdinalIgnoreCase))
                .Select(m => new { name = m.ModuleName, path = m.FileName, sha256 = Hash(m.FileName) }).ToArray();
            Assert.Single(modules);
            File.WriteAllText(Path.Combine(destination, "identity.json"), JsonSerializer.Serialize(new
            {
                primary, primaryHash, companion, companionHash, target, format, targetName = target.ToString(), formatName = format.ToString(),
                preparedImageSha256 = Hash(bundle.PrimaryImage.Path), bundle.Semantics, bundle.Manifest,
                outputSha256 = Hash(output), preparedSha256 = Convert.ToHexString(SHA256.HashData(prepared)),
                observed.Observed.VideoOffset, observed.Observed.VideoLength, modules
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static string Cached(string name)
    {
        string path = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples", name));
        Assert.True(File.Exists(path), "Required RealSample is missing: " + path); return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
