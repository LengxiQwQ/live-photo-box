using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;
namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8SourceNeutralCoverageTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("红米老款-GV1.JPG", null)]
    [InlineData("小米.jpg", null)]
    [InlineData("oppo.jpg", null)]
    [InlineData("vivo.jpg", null)]
    [InlineData("三星.jpg", null)]
    [InlineData("三星.heic", null)]
    [InlineData("一加.jpg", null)]
    [InlineData("一加-改了封面照片.jpg", null)]
    [InlineData("华为-Mate80.jpg", null)]
    [InlineData("华为Mate80.heic", null)]
    [InlineData("荣耀.jpg", null)]
    [InlineData("vivo双文件.jpg", "vivo双文件.mp4")]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV")]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV")]
    public async Task EveryRealSourceStillCreatesNeutralWithoutChangingOriginal(string primaryName, string? companionName)
    {
        string primary = Cached(primaryName); string? companion = companionName is null ? null : Cached(companionName);
        string imageHash = Hash(primary);
        string? companionHash = companion is null ? null : Hash(companion);
        using var workspace = new MediaWorkspace();
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(primary, companion, workspace, null, PreservationPolicy.BestEffort);
        Assert.NotNull(bundle.PrimaryImage); Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.PrimaryImage.Path)); Assert.True(File.Exists(bundle.MotionVideo.Path));
        Assert.NotEmpty(bundle.Manifest);
        Assert.All(bundle.Manifest, artifact => Assert.True(File.Exists(artifact.Path), "Missing declared artifact: " + artifact.Path));
        Assert.Equal(imageHash, Hash(primary));
        if (companion is not null) Assert.Equal(companionHash, Hash(companion));
        PersistSnapshot(bundle, primaryName);
    }
    private static void PersistSnapshot(NeutralMediaBundle bundle, string name)
    {
        string? root = Environment.GetEnvironmentVariable("LPB_P9R8_NEUTRAL_SNAPSHOT_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        string destination = Path.Combine(root, name);
        if (Directory.Exists(destination)) throw new IOException("Snapshot is create-only: " + destination);
        Directory.CreateDirectory(destination);
        var assets = new List<object>();
        foreach (string path in bundle.Manifest.Select(a => a.Path).Concat(new[] { bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, bundle.GainMap?.Path })
            .Concat(bundle.AuxiliaryMedia.Select(a => a.MaterializedArtifact?.Path)).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            string copy = "asset-" + assets.Count + Path.GetExtension(path);
            string output = Path.Combine(destination, copy); File.Copy(path, output, false);
            Assert.Equal(Hash(path), Hash(output));
            assets.Add(new { sourcePath = path, copy, sha256 = Hash(output), length = new FileInfo(output).Length });
        }
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(m => m.ModuleName.Equals("LivePhotoBox.Native.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("LivePhotoBox.Native.TestHarness.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("LivePhotoBox.Video.Libav.dll", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { name = m.ModuleName, path = m.FileName, sha256 = Hash(m.FileName), length = new FileInfo(m.FileName).Length }).ToArray();
        Assert.Contains(modules, m => m.name.Equals("LivePhotoBox.Native.dll", StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(destination, "neutral.json"), JsonSerializer.Serialize(new { bundle.Manifest, bundle.Semantics, bundle.AuxiliaryMedia, bundle.PreservationCarriers, bundle.PreparationExecution, assets, loadedNativeModules = modules }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
    private static string Cached(string name)
    {
        string path = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples", name));
        Assert.True(File.Exists(path), "Required RealSample is missing: " + path); return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
