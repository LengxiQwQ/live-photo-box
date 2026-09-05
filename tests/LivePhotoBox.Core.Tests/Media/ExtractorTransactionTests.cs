using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private static CancellationTokenSource? s_midStreamCts;
    private static long s_midStreamBytesObserved;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMidStreamStep(nint userData, int step, ulong bytesProcessed)
    {
        if (bytesProcessed > 0 && s_midStreamCts is { IsCancellationRequested: false })
        {
            Interlocked.Exchange(ref s_midStreamBytesObserved, (long)bytesProcessed);
            s_midStreamCts.Cancel();
        }
    }

    [Fact]
    public async Task Extract_MidStreamCancellation_CancelsCleanlyAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        // Create 2 MB file so multiple slice chunks occur
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(2 * 1024 * 1024));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 * 1024 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 1024 * 1024, ByteLength = 1024 * 1024, SourceIndex = 0 }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        using var cts = new CancellationTokenSource();
        s_midStreamCts = cts;
        s_midStreamBytesObserved = 0;

        nint callbackPtr;
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnMidStreamStep;
            callbackPtr = (nint)callback;
        }

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.None, targetArtifact: 0, triggerAfterBytes: 0, callbackPtr, nint.Zero);
            }, cts.Token));

        Assert.True(s_midStreamBytesObserved > 0, "Cancellation did not occur mid-stream after bytes were written!");

        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_SourceReplacedBeforeExtract_ThrowsSourceChangedAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] originalBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, originalBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            PrimarySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalBytes))
        };

        // Mutate source file after inspection/snapshot
        byte[] replacedBytes = CreateDummyFileBytes(8192);
        replacedBytes[100] ^= 0xFF;
        await File.WriteAllBytesAsync(dummySource, replacedBytes);

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.SourceChanged, ex.Category);

        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_DestinationFileAlreadyExists_DoesNotOverwriteAndRollsBack()
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

        string existingDest = Path.Combine(tempDir.Path, "existing_output.jpg");
        byte[] sentinel = [0xDE, 0xAD, 0xBE, 0xEF];
        await File.WriteAllBytesAsync(existingDest, sentinel);

        string videoDest = Path.Combine(tempDir.Path, "output_video.mp4");

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(
                dummySource,
                null,
                facts,
                outputImagePath: existingDest,
                outputVideoPath: videoDest,
                outputGainmapPath: null));

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, ex.Category);

        // Assert existing destination was NOT overwritten or modified
        byte[] actualDestBytes = await File.ReadAllBytesAsync(existingDest);
        Assert.Equal(sentinel, actualDestBytes);

        Assert.False(File.Exists(videoDest));
    }

    [Fact]
    public async Task Extract_DiskFullOnMotionVideo_RollsBackPrimaryImageAndLeavesNoFiles()
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
                ctx.SetExtractorFault(NativeExtractorFault.DiskFull, targetArtifact: 1, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.DiskFull, ex.Category);

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
