using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorFailureTests
{
    private static async Task<string> ComputeFileSha256Async(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    [Fact]
    public async Task Extract_NegativeOffset_ThrowsInvalidFacts()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = -1, ByteLength = 512 },
            PrimarySha256 = beforeSha
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_NegativeLength_ThrowsInvalidFacts()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = -500 },
            PrimarySha256 = beforeSha
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_ZeroLengthForPresentItem_ThrowsInvalidFacts()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 0 },
            PrimarySha256 = beforeSha
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_RangeExceedsFileSize_ThrowsInvalidFacts()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 500, ByteLength = 1000 },
            PrimarySha256 = beforeSha
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_RangeArithmeticOverflow_ThrowsInvalidFacts()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = long.MaxValue - 10, ByteLength = 100 },
            PrimarySha256 = beforeSha
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_MissingSecondarySource_ThrowsInvalidFacts()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Heic, ByteOffset = 0, ByteLength = 1024 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mov, ByteOffset = 0, ByteLength = 1024, SourceIndex = 1 },
            PrimarySha256 = beforeSha,
            SecondarySha256 = new string('b', 64)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, secondaryPath: null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_UnsupportedContainer_ThrowsUnsupportedLayout()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.bin");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Unknown, ByteOffset = 0, ByteLength = 512 },
            PrimarySha256 = new string('a', 64)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.UnsupportedLayout, ex.Category);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public async Task Extract_PathAliasingWithSource_ThrowsInvalidAlias()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, new byte[1024]);
        string beforeSha = await ComputeFileSha256Async(dummySource);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 },
            PrimarySha256 = beforeSha
        };

        // Directly call NativeMediaService with output path identical to primary source path
        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(
                dummySource,
                null,
                facts,
                outputImagePath: dummySource, // ALIAS!
                outputVideoPath: null,
                outputGainmapPath: null));

        Assert.Equal(ExtractionFailureCategory.InvalidAlias, ex.Category);
        Assert.Equal(beforeSha, await ComputeFileSha256Async(dummySource));
    }

    private sealed class DisposableTempDirectory : IDisposable
    {
        public string Path { get; }

        public DisposableTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lpb_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch { }
        }
    }
}
