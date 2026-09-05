using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorTransactionTests
{
    private static byte[] CreateDummyFileBytes(int size, bool isJpeg = true)
    {
        byte[] bytes = new byte[size];
        var rng = new Random(123);
        rng.NextBytes(bytes);
        if (isJpeg && size >= 2)
        {
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
        }
        return bytes;
    }

    [Fact]
    public async Task Extract_InjectedDiskFull_ThrowsDiskFullExceptionAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.DiskFull, targetArtifact: 0, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.DiskFull, ex.Category);

        // Verify that workspace directory is completely empty
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_InjectedWriteFailOnSecondaryArtifact_RollsBackPrimaryAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        // Inject write failure on targetArtifact 1 (MotionVideo)
        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.WriteFail, targetArtifact: 1, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.OutputWriteFailed, ex.Category);

        // Verify that primary image temp file was also rolled back and workspace is completely clean
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_InjectedPublishFail_RollsBackPublishedFilesAndCleansTempFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        // Inject publish failure on artifact 1 (MotionVideo) after artifact 0 (PrimaryImage) was already published
        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.PublishFail, targetArtifact: 1, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, ex.Category);

        // Verify that already published primary image was removed in rollback and no temp files remain
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_InjectedShortRead_ThrowsSourceRangeUnreadableAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.ShortRead, targetArtifact: 0, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.SourceRangeUnreadable, ex.Category);

        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_PreCancelledToken_ThrowsAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, cts.Token));

        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_MidStreamCancellation_CancelsCleanlyAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        // Create 1 MB file so copying takes multiple buffer iterations
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(1024 * 1024));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 * 1024 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        using var cts = new CancellationTokenSource();

        // Cancel the CTS from configureContext or background task
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                cts.Cancel();
            }, cts.Token));

        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_SourceModifiedMidStream_ThrowsSourceChangedAndCleansOutputs()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                // Mutate the source file after beforeSha was captured
                File.AppendAllText(dummySource, "MUTATION");
            }));

        Assert.Equal(ExtractionFailureCategory.SourceChanged, ex.Category);

        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
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
