using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class MergeSplitCollisionAvoidanceTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task MergeScan_SameBaseName_DifferentExtensions_RequiresExplicitIdentitySelection()
    {
        using var workspace = new TestFixtureWorkspace();

        // Pair 1: photo.heic + photo.mov (Apple Live Photo)
        string imgHeic = Path.Combine(workspace.RootDirectory, "photo.heic");
        string vidMov = Path.Combine(workspace.RootDirectory, "photo.mov");
        File.Copy(TestSampleResolver.ResolveSample("苹果双文件.HEIC"), imgHeic);
        File.Copy(TestSampleResolver.ResolveSample("苹果双文件.MOV"), vidMov);

        // Pair 2: photo.jpg + photo.mp4 (Vivo Live Photo)
        string imgJpg = Path.Combine(workspace.RootDirectory, "photo.jpg");
        string vidMp4 = Path.Combine(workspace.RootDirectory, "photo.mp4");
        // ID discovery reads the actual signed EOF record. The old minimal
        // synthetic string had no length framing and cannot authorize pairing.
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"), imgJpg);
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"), vidMp4);

        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
        {
            InputDirectory = workspace.RootDirectory,
            Mode = PairingMode.Name
        });

        // Pure filename composition cannot choose between two stills and two
        // movies. Explicit identity modes may resolve their independent pairs.
        Assert.Empty(result.Pairs);
        Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "ConflictMultipleMedia");
        Assert.Equal(ScanOperationStatus.Completed, result.Status);

        var apple = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
            { InputDirectory = workspace.RootDirectory, Mode = PairingMode.Cid });
        var vivo = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
            { InputDirectory = workspace.RootDirectory, Mode = PairingMode.Vivo });
        Assert.Single(apple.Pairs);
        Assert.Single(vivo.Pairs);
        var resolved = apple.Pairs.Concat(vivo.Pairs).ToArray();

        // Pair 1 should be heic + mov
        var pairHeic = resolved.FirstOrDefault(p => p.ImagePath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(pairHeic);
        Assert.Equal(vidMov, pairHeic.VideoPath);

        // Pair 2 should be jpg + mp4
        var pairJpg = resolved.FirstOrDefault(p => p.ImagePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(pairJpg);
        Assert.Equal(vidMp4, pairJpg.VideoPath);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task SplitAsync_ExistingTargetFiles_AutoRenamesWithConsistentSuffixNumber()
    {
        using var workspace = new TestFixtureWorkspace();
        string outputDir = Path.Combine(workspace.RootDirectory, "output");
        Directory.CreateDirectory(outputDir);

        string sample = TestSampleResolver.ResolveSample("oppo.jpg");

        // Split 1st time
        var split1 = await LivePhotoSplitService.SplitAsync(
            sample,
            outputDir,
            protocolIndex: ProtocolFormatMatrix.SplitProtocolNone,
            outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
            CancellationToken.None);

        Assert.True(File.Exists(split1.ImageOutputPath));
        Assert.True(File.Exists(split1.VideoOutputPath));
        string base1 = Path.GetFileNameWithoutExtension(split1.ImageOutputPath);
        Assert.Equal(base1, Path.GetFileNameWithoutExtension(split1.VideoOutputPath));

        // Split 2nd time into the same directory: must auto-rename with consistent suffix "(2)"
        var split2 = await LivePhotoSplitService.SplitAsync(
            sample,
            outputDir,
            protocolIndex: ProtocolFormatMatrix.SplitProtocolNone,
            outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
            CancellationToken.None);

        Assert.True(File.Exists(split2.ImageOutputPath));
        Assert.True(File.Exists(split2.VideoOutputPath));
        string base2 = Path.GetFileNameWithoutExtension(split2.ImageOutputPath);
        Assert.Equal(base2, Path.GetFileNameWithoutExtension(split2.VideoOutputPath));
        Assert.EndsWith("(2)", base2);

        // Split 3rd time: must auto-rename to "(3)"
        var split3 = await LivePhotoSplitService.SplitAsync(
            sample,
            outputDir,
            protocolIndex: ProtocolFormatMatrix.SplitProtocolNone,
            outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
            CancellationToken.None);

        Assert.True(File.Exists(split3.ImageOutputPath));
        Assert.True(File.Exists(split3.VideoOutputPath));
        string base3 = Path.GetFileNameWithoutExtension(split3.ImageOutputPath);
        Assert.Equal(base3, Path.GetFileNameWithoutExtension(split3.VideoOutputPath));
        Assert.EndsWith("(3)", base3);
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task MergeScan_Subdirectories_PreservesHierarchyWithoutCrossDirCollisions()
    {
        using var workspace = new TestFixtureWorkspace();
        string dirA = Path.Combine(workspace.RootDirectory, "SubA");
        string dirB = Path.Combine(workspace.RootDirectory, "SubB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        string tempDir = Path.Combine(Path.GetTempPath(), $"lpb-test-sub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string oppo = TestSampleResolver.ResolveSample("oppo.jpg");
            var split = await LivePhotoSplitService.SplitAsync(
                oppo,
                tempDir,
                protocolIndex: ProtocolFormatMatrix.SplitProtocolNone,
                outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
                CancellationToken.None);

            // SubA/same_name.jpg + SubA/same_name.mov
            string imgA = Path.Combine(dirA, "same_name.jpg");
            string vidA = Path.Combine(dirA, "same_name.mov");
            File.WriteAllBytes(imgA, SyntheticProtocolFixtures.CreateMinimalJpeg());
            File.Copy(split.VideoOutputPath, vidA, overwrite: true);

            // SubB/same_name.jpg + SubB/same_name.mov
            string imgB = Path.Combine(dirB, "same_name.jpg");
            string vidB = Path.Combine(dirB, "same_name.mov");
            File.WriteAllBytes(imgB, SyntheticProtocolFixtures.CreateMinimalJpeg());
            File.Copy(split.VideoOutputPath, vidB, overwrite: true);

            var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
            {
                InputDirectory = workspace.RootDirectory,
                Mode = PairingMode.Name,
                Recursive = true
            });

            // Each subdirectory pairs its own pair without conflict
            Assert.Equal(2, result.Pairs.Count);
            Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error);

            var pairA = result.Pairs.FirstOrDefault(p => p.ImagePath == imgA);
            Assert.NotNull(pairA);
            Assert.Equal(vidA, pairA.VideoPath);

            var pairB = result.Pairs.FirstOrDefault(p => p.ImagePath == imgB);
            Assert.NotNull(pairB);
            Assert.Equal(vidB, pairB.VideoPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "FaultTolerance")]
    public async Task MergeScan_CorruptFileInDirectory_DoesNotAbortScanningOfValidPairs()
    {
        using var workspace = new TestFixtureWorkspace();

        // 1. Valid Apple Live Photo pair
        string imgHeic = Path.Combine(workspace.RootDirectory, "valid.heic");
        string vidMov = Path.Combine(workspace.RootDirectory, "valid.mov");
        File.Copy(TestSampleResolver.ResolveSample("苹果双文件.HEIC"), imgHeic);
        File.Copy(TestSampleResolver.ResolveSample("苹果双文件.MOV"), vidMov);

        // 2. Corrupted file with random bytes masquerading as JPG
        string corruptJpg = Path.Combine(workspace.RootDirectory, "corrupt.jpg");
        File.WriteAllBytes(corruptJpg, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x99, 0x88 });

        // 3. Unmatched orphan video
        string orphanMp4 = Path.Combine(workspace.RootDirectory, "orphan.mp4");
        File.WriteAllBytes(orphanMp4, new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 });

        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
        {
            InputDirectory = workspace.RootDirectory,
            Mode = PairingMode.Name
        });

        // The scan must complete successfully, pairing the valid photo and keeping diagnostic for corrupt file
        Assert.Equal(ScanOperationStatus.Completed, result.Status);
        var pair = Assert.Single(result.Pairs);
        Assert.Equal(imgHeic, pair.ImagePath);
        Assert.Equal(vidMov, pair.VideoPath);

        // Corrupt file must have a diagnostic error, not aborting the scan
        Assert.Contains(result.Diagnostics, d => d.PrimaryPath == corruptJpg);
    }

    [Fact]
    [Trait("Category", "FaultTolerance")]
    public async Task DiscoveryScan_CorruptFileInDirectory_DiscoversValidLivePhotos()
    {
        using var workspace = new TestFixtureWorkspace();

        // 1. Valid Single-file Live Photo
        string validJpg = Path.Combine(workspace.RootDirectory, "valid.jpg");
        File.Copy(TestSampleResolver.ResolveSample("oppo.jpg"), validJpg);

        // 2. Corrupt file
        string corruptJpg = Path.Combine(workspace.RootDirectory, "corrupt.jpg");
        File.WriteAllBytes(corruptJpg, new byte[] { 0xFE, 0xED, 0xFA, 0xCE });

        var result = await LivePhotoDiscoveryService.ScanAsync(workspace.RootDirectory, DiscoveryScanMode.SplitOnly);

        // Valid photo must be discovered without scan failing
        Assert.Equal(ScanOperationStatus.Completed, result.Status);
        var validItem = Assert.Single(result.Items.Where(i => i.IsLivePhoto));
        Assert.Equal(validJpg, validItem.FilePath);
        Assert.Contains(result.Diagnostics, d => d.PrimaryPath == corruptJpg);
    }
}
