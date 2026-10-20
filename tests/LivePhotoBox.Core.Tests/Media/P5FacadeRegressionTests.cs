using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P5FacadeRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImagePublication_ChangedContent_RejectsWithoutTouchingCallerFile(bool changeLength)
    {
        using var workspace = new TestFixtureWorkspace();
        string staged = Path.Combine(workspace.RootDirectory, "img-conv-change.jpg");
        string final = Path.Combine(workspace.RootDirectory, "final.jpg");
        File.WriteAllBytes(staged, [1, 2, 3, 4]);
        var artifact = new MediaArtifact { Path = staged, Kind = MediaArtifactKind.PrimaryImage,
            FileIdentity = WindowsFileIdentity.Capture(staged), ByteLength = 4, Sha256 = Hash(staged) };
        File.WriteAllBytes(final, [8, 9]);
        File.WriteAllBytes(staged, changeLength ? [5, 6, 7] : [5, 6, 7, 8]);
        Assert.Throws<IOException>(() => ConverterArtifactPublisher.PublishNoReplace(artifact, final));
        Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(final));
        Assert.False(File.Exists(staged));
        Assert.Single(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public void ImagePublication_CancelledAtCommit_CleansStageAndPreservesCancellation()
    {
        using var workspace = new TestFixtureWorkspace();
        string staged = Path.Combine(workspace.RootDirectory, "img-conv-cancel.jpg");
        string final = Path.Combine(workspace.RootDirectory, "final.jpg");
        File.WriteAllBytes(staged, [1, 2, 3, 4]);
        var artifact = new MediaArtifact { Path = staged, Kind = MediaArtifactKind.PrimaryImage,
            FileIdentity = WindowsFileIdentity.Capture(staged), ByteLength = 4, Sha256 = Hash(staged) };
        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ConverterArtifactPublisher.PublishNoReplace(artifact, final, cts.Token, cts.Cancel));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Theory]
    [InlineData("荣耀.jpg")]
    [InlineData("vivo.jpg")]
    [Trait("Category", "RealSamples")]
    public async Task NativeOrdinaryImageConversion_CannotDiscardEmbeddedGainMap(string filename)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(RepositoryRoot(), ".ai-tmp/cache/samples/P5-R3-v1", filename);
        string before = Hash(source);
        string output = Path.Combine(workspace.RootDirectory, "ordinary.heic");
        Assert.True((await NativeMediaService.InspectMediaAsync(source)).GainMap?.IsPresent);
        Exception? failure = await Record.ExceptionAsync(() =>
            NativeMediaService.ConvertImageAsync(source, output, ImageContainer.Heic, 90));
        Assert.NotNull(failure);
        Assert.Contains("semantic", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
        Assert.Equal(before, Hash(source));
    }

    [Fact]
    public async Task ImageExport_InvalidQuality_IsRejectedBeforeOpeningSource()
    {
        using var workspace = new TestFixtureWorkspace();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImageFormatService.ConvertImageAsync(
            Path.Combine(workspace.RootDirectory, "missing.jpg"),
            Path.Combine(workspace.RootDirectory, "output.heic"), quality: 0));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ImageExport_RequestedQuality_ChangesActualCrossContainerOutput()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(RepositoryRoot(), ".ai-tmp/cache/samples/P4-v1/oppo.jpg");
        string before = Hash(source);
        string low = Path.Combine(workspace.RootDirectory, "low.heic");
        string high = Path.Combine(workspace.RootDirectory, "high.heic");
        await ImageFormatService.ConvertImageAsync(source, low, quality: 10);
        await ImageFormatService.ConvertImageAsync(source, high, quality: 95);
        Assert.NotEqual(Hash(low), Hash(high));
        Assert.True(new FileInfo(low).Length < new FileInfo(high).Length,
            "The requested lower quality must reach the encoder rather than using one fixed quality.");
        Assert.Equal(before, Hash(source));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task HeicFacade_UnsupportedDepth_DoesNotDeleteExistingCallerDestination()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.heic");
        File.Copy(Path.Combine(RepositoryRoot(), ".ai-tmp/cache/samples/P5-R4-v1/3B_HEIC_SDR.heic"), source);
        string destination = Path.ChangeExtension(source, ".jpg");
        byte[] original = [1, 3, 5, 7, 9];
        File.WriteAllBytes(destination, original);
        string before = Hash(source);

        await Assert.ThrowsAnyAsync<Exception>(() => HeicConverterService.ConvertToJpegAsync(source));

        Assert.True(File.Exists(destination), "A rejected conversion deleted the caller's existing file.");
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(before, Hash(source));
        Assert.Equal(2, Directory.GetFiles(workspace.RootDirectory).Length);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task HeicFacade_SuccessfulEncode_CollisionPreservesDestinationAndCleansStage()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = await CreateOrdinaryHeicAsync(workspace.RootDirectory);
        string destination = Path.ChangeExtension(source, ".jpg");
        byte[] original = [2, 4, 6, 8];
        File.WriteAllBytes(destination, original);

        await Assert.ThrowsAnyAsync<Exception>(() => HeicConverterService.ConvertToJpegAsync(source));

        Assert.True(File.Exists(destination), "No-replace failure deleted the caller's existing file.");
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(2, Directory.GetFiles(workspace.RootDirectory).Length);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task HeicFacade_OrdinaryRoutes_UseValidatedArtifactsWithoutChangingSources()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = await CreateOrdinaryHeicAsync(workspace.RootDirectory);
        string sourceHash = Hash(source);
        string jpeg = await HeicConverterService.ConvertToJpegAsync(source);
        Assert.Equal(Path.ChangeExtension(source, ".jpg"), jpeg);
        Assert.Equal(ImageContainer.Jpeg, (await NativeMediaService.InspectMediaAsync(jpeg)).PrimaryImage.Container);
        string jpegHash = Hash(jpeg);
        string heic = await HeicConverterService.ConvertToHeicAsync(jpeg, workspace.RootDirectory);
        Assert.Equal(ImageContainer.Heic, (await NativeMediaService.InspectMediaAsync(heic)).PrimaryImage.Container);
        Assert.Equal(sourceHash, Hash(source));
        Assert.Equal(jpegHash, Hash(jpeg));
        Assert.Equal(3, Directory.GetFiles(workspace.RootDirectory).Length);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task HeicFacade_AlreadyCancelled_DoesNotTouchDestination()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = await CreateOrdinaryHeicAsync(workspace.RootDirectory);
        string destination = Path.ChangeExtension(source, ".jpg");
        File.WriteAllBytes(destination, [7, 8, 9]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HeicConverterService.ConvertToJpegAsync(source, cts.Token));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(destination));
        Assert.Equal(2, Directory.GetFiles(workspace.RootDirectory).Length);
    }

    private static async Task<string> CreateOrdinaryHeicAsync(string directory)
    {
        string input = Path.Combine(RepositoryRoot(), ".ai-tmp/cache/samples/P4-v1/苹果-双文件.JPG");
        string output = Path.Combine(directory, "ordinary.heic");
        Assert.True(await NativeMediaService.ConvertImageAsync(input, output, ImageContainer.Heic, 90));
        Assert.Null((await NativeMediaService.InspectMediaAsync(output)).GainMap);
        return output;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vcpkg.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
