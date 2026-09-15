using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class PlatformFilesystemTests
{
    private static string Sample => TestSampleResolver.ResolveSample("oppo.jpg");

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImageCopy_ExistingDestination_PreservesForeignFileAndCleansOwnedTemp()
    {
        using var workspace = new MediaWorkspace();
        string destination = Path.Combine(workspace.RootDirectory, "目标-冲突.jpg");
        byte[] sentinel = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E];
        await File.WriteAllBytesAsync(destination, sentinel);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ConvertImageAsync(Sample, destination, ImageContainer.Jpeg, 90));

        Assert.Equal(sentinel, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "lpb-image-copy-*.tmp"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImageCopy_RealExclusiveShareLock_FailsBeforePublish()
    {
        using var workspace = new MediaWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "源-锁定.jpg");
        string destination = Path.Combine(workspace.RootDirectory, "输出-锁定.jpg");
        File.Copy(Sample, source);
        byte[] before = await File.ReadAllBytesAsync(source);

        using (var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                NativeMediaService.ConvertImageAsync(source, destination, ImageContainer.Jpeg, 90));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "lpb-image-copy-*.tmp"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImageCopy_UnicodePaths_PublishesExactCopyWithoutChangingSource()
    {
        using var workspace = new MediaWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "源-相片.jpg");
        string destination = Path.Combine(workspace.RootDirectory, "结果-相片.jpg");
        File.Copy(Sample, source);
        byte[] before = await File.ReadAllBytesAsync(source);

        bool reencoded = await NativeMediaService.ConvertImageAsync(
            source, destination, ImageContainer.Jpeg, 90);

        Assert.False(reencoded);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Equal(before, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "lpb-image-copy-*.tmp"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImageCopy_SourceDestinationAlias_IsRejectedWithoutMutation()
    {
        using var workspace = new MediaWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "源-同名.jpg");
        File.Copy(Sample, source);
        byte[] before = await File.ReadAllBytesAsync(source);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ConvertImageAsync(source, source, ImageContainer.Jpeg, 90));

        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Single(Directory.EnumerateFiles(workspace.RootDirectory));
    }
}
