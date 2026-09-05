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

    private static string ComputeSha(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    [Fact]
    public async Task Extract_InjectedDiskFull_ThrowsDiskFullExceptionAndLeavesNoFiles()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(2 * 1024 * 1024);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 * 1024 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 1024 * 1024, ByteLength = 1024 * 1024, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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
    public async Task Extract_PreNativeSourceMutation_ThrowsSourceChangedAndCleansOutputs()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
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

    private static int s_singleStreamProbesPassed;
    private static string? s_singleStreamSourcePath;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSingleFileLifetimeStep(nint userData, int step, ulong bytesProcessed)
    {
        if (bytesProcessed > 0 && s_singleStreamSourcePath != null && s_singleStreamProbesPassed == 0)
        {
            try
            {
                // 1. Attempt open for write
                try
                {
                    using var fs = new FileStream(s_singleStreamSourcePath, FileMode.Open, FileAccess.Write, FileShare.None);
                    return;
                }
                catch (IOException) { }

                // 2. Attempt delete
                try
                {
                    File.Delete(s_singleStreamSourcePath);
                    return;
                }
                catch (IOException) { }

                // 3. Attempt rename/move
                try
                {
                    File.Move(s_singleStreamSourcePath, s_singleStreamSourcePath + ".renamed");
                    return;
                }
                catch (IOException) { }

                Interlocked.Increment(ref s_singleStreamProbesPassed);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Extract_SingleFile_SourceLockedDuringStream_MidStreamWriteDeleteMoveFailWithIOExceptionAndExtractionSucceeds()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] sourceBytes = CreateDummyFileBytes(2 * 1024 * 1024);
        await File.WriteAllBytesAsync(dummySource, sourceBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 * 1024 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 1024 * 1024, ByteLength = 1024 * 1024, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(sourceBytes)
        };

        s_singleStreamProbesPassed = 0;
        s_singleStreamSourcePath = dummySource;

        nint callbackPtr;
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnSingleFileLifetimeStep;
            callbackPtr = (nint)callback;
        }

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
        {
            ctx.SetExtractorFault(NativeExtractorFault.None, targetArtifact: 0, triggerAfterBytes: 0, callbackPtr, nint.Zero);
        });

        Assert.True(s_singleStreamProbesPassed > 0, "Mid-stream source lock probes did not execute or fail sharing contract!");
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.True(File.Exists(bundle.MotionVideo.Path));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(dummySource));
    }

    private static int s_dualStreamProbesPassed;
    private static string? s_dualPrimaryPath;
    private static string? s_dualSecondaryPath;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDualFileLifetimeStep(nint userData, int step, ulong bytesProcessed)
    {
        if (bytesProcessed > 0 && s_dualPrimaryPath != null && s_dualSecondaryPath != null && s_dualStreamProbesPassed == 0)
        {
            try
            {
                // Probe primary
                try
                {
                    using var fs = new FileStream(s_dualPrimaryPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    return;
                }
                catch (IOException) { }

                try
                {
                    File.Delete(s_dualPrimaryPath);
                    return;
                }
                catch (IOException) { }

                try
                {
                    File.Move(s_dualPrimaryPath, s_dualPrimaryPath + ".renamed");
                    return;
                }
                catch (IOException) { }

                // Probe secondary
                try
                {
                    using var fs = new FileStream(s_dualSecondaryPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    return;
                }
                catch (IOException) { }

                try
                {
                    File.Delete(s_dualSecondaryPath);
                    return;
                }
                catch (IOException) { }

                try
                {
                    File.Move(s_dualSecondaryPath, s_dualSecondaryPath + ".renamed");
                    return;
                }
                catch (IOException) { }

                Interlocked.Increment(ref s_dualStreamProbesPassed);
            }
            catch { }
        }
    }

    [Fact]
    public async Task Extract_DualFile_BothSourcesLockedDuringStream_MidStreamWriteDeleteMoveFailWithIOExceptionAndExtractionSucceeds()
    {
        using var tempDir = new DisposableTempDirectory();
        string primarySource = Path.Combine(tempDir.Path, "primary.jpg");
        string secondarySource = Path.Combine(tempDir.Path, "secondary.mp4");

        byte[] primaryBytes = CreateDummyFileBytes(1024 * 1024);
        byte[] secondaryBytes = CreateDummyFileBytes(1024 * 1024, isJpeg: false);
        await File.WriteAllBytesAsync(primarySource, primaryBytes);
        await File.WriteAllBytesAsync(secondarySource, secondaryBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 * 1024 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 0, ByteLength = 1024 * 1024, SourceIndex = 1 },
            PrimarySha256 = ComputeSha(primaryBytes),
            SecondarySha256 = ComputeSha(secondaryBytes)
        };

        s_dualStreamProbesPassed = 0;
        s_dualPrimaryPath = primarySource;
        s_dualSecondaryPath = secondarySource;

        nint callbackPtr;
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnDualFileLifetimeStep;
            callbackPtr = (nint)callback;
        }

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();
        var bundle = await extractor.ExtractAsync(facts, primarySource, secondarySource, workspace, ctx =>
        {
            ctx.SetExtractorFault(NativeExtractorFault.None, targetArtifact: 0, triggerAfterBytes: 0, callbackPtr, nint.Zero);
        });

        Assert.True(s_dualStreamProbesPassed > 0, "Dual-file mid-stream source lock probes did not execute or fail sharing contract!");
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.True(File.Exists(bundle.MotionVideo.Path));
        Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(primarySource));
        Assert.Equal(secondaryBytes, await File.ReadAllBytesAsync(secondarySource));
    }

    [Fact]
    public async Task Extract_FlushDiskFull_RollsBackAndReturnsDiskFull()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.FlushDiskFull, targetArtifact: 0, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.DiskFull, ex.Category);
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_FlushWriteFailure_RollsBackAndReturnsOutputWriteFailed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.FlushWriteFail, targetArtifact: 0, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.OutputWriteFailed, ex.Category);
        var workspaceFiles = Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories);
        Assert.Empty(workspaceFiles);
    }

    [Fact]
    public async Task Extract_CleanupFailure_AfterWriteFailure_SurfacesCleanupFailedAndPreservesOriginalFailure()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.WriteFail | NativeExtractorFault.CleanupFail, targetArtifact: 0, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.CleanupFailed, ex.Category);
        Assert.Equal(ExtractionFailureCategory.OutputWriteFailed, ex.OriginalCategory);
        Assert.NotNull(ex.InnerException);
    }

    private static CancellationTokenSource? s_cleanupCancelCts;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancelCleanupStep(nint userData, int step, ulong bytesProcessed)
    {
        if (bytesProcessed > 0 && s_cleanupCancelCts is { IsCancellationRequested: false })
        {
            s_cleanupCancelCts.Cancel();
        }
    }

    [Fact]
    public async Task Extract_CleanupFailure_AfterCancellation_SurfacesCleanupFailedAndPreservesOriginalFailure()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(2 * 1024 * 1024);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 1024 * 1024 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 1024 * 1024, ByteLength = 1024 * 1024, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        using var cts = new CancellationTokenSource();
        s_cleanupCancelCts = cts;

        nint callbackPtr;
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnCancelCleanupStep;
            callbackPtr = (nint)callback;
        }

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.CleanupFail, targetArtifact: 0, triggerAfterBytes: 0, callbackPtr, nint.Zero);
            }, cts.Token));

        Assert.Equal(ExtractionFailureCategory.CleanupFailed, ex.Category);
        Assert.Equal(ExtractionFailureCategory.Cancelled, ex.OriginalCategory);
        Assert.IsType<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task Extract_CleanupFailure_AfterPublishFailure_SurfacesCleanupFailedAndPreservesOriginalFailure()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 },
            PrimarySha256 = ComputeSha(dummyBytes)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace, ctx =>
            {
                ctx.SetExtractorFault(NativeExtractorFault.PublishFail | NativeExtractorFault.CleanupFail, targetArtifact: 1, triggerAfterBytes: 0);
            }));

        Assert.Equal(ExtractionFailureCategory.CleanupFailed, ex.Category);
        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, ex.OriginalCategory);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Extract_MissingPrimarySnapshot_FailsClosed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            PrimarySha256 = "" // Missing / empty!
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
    }

    [Fact]
    public async Task Extract_AllZeroPrimarySnapshot_FailsClosed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            PrimarySha256 = new string('0', 64) // All zeros!
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
    }

    [Fact]
    public async Task Extract_MalformedPrimarySnapshot_FailsClosed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        await File.WriteAllBytesAsync(dummySource, CreateDummyFileBytes(8192));

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            PrimarySha256 = "NOT_A_VALID_HEX_SHA256_STRING" // Malformed!
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, null, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
    }

    [Fact]
    public async Task Extract_MissingRequiredSecondarySnapshot_FailsClosed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        string dummySecondary = Path.Combine(tempDir.Path, "source.mp4");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);
        await File.WriteAllBytesAsync(dummySecondary, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 0, ByteLength = 4096, SourceIndex = 1 },
            PrimarySha256 = ComputeSha(dummyBytes),
            SecondarySha256 = "" // Missing required secondary snapshot!
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, dummySecondary, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
    }

    [Fact]
    public async Task Extract_MalformedSecondarySnapshot_FailsClosed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        string dummySecondary = Path.Combine(tempDir.Path, "source.mp4");
        byte[] dummyBytes = CreateDummyFileBytes(8192);
        await File.WriteAllBytesAsync(dummySource, dummyBytes);
        await File.WriteAllBytesAsync(dummySecondary, dummyBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 0, ByteLength = 4096, SourceIndex = 1 },
            PrimarySha256 = ComputeSha(dummyBytes),
            SecondarySha256 = "INVALID_SECONDARY_HEX" // Malformed!
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var ex = await Assert.ThrowsAsync<ExtractionException>(() =>
            extractor.ExtractAsync(facts, dummySource, dummySecondary, workspace));

        Assert.Equal(ExtractionFailureCategory.InvalidFacts, ex.Category);
    }

    [Fact]
    public unsafe void Extract_NativeDirectAbi_MissingPrimarySnapshot_FailsClosed()
    {
        using var tempDir = new DisposableTempDirectory();
        string dummySource = Path.Combine(tempDir.Path, "source.jpg");
        File.WriteAllBytes(dummySource, CreateDummyFileBytes(8192));
        string dummyDest = Path.Combine(tempDir.Path, "out.jpg");

        var nativeFacts = new NativeSourceMediaFacts();
        nativeFacts.StructSize = (uint)sizeof(NativeSourceMediaFacts);
        nativeFacts.PrimaryImage.StructSize = (uint)sizeof(NativeImageItemFacts);
        nativeFacts.PrimaryImage.IsPresent = 1;
        nativeFacts.PrimaryImage.Container = (int)ImageContainer.Jpeg;
        nativeFacts.PrimaryImage.FileRange.Offset = 0;
        nativeFacts.PrimaryImage.FileRange.Length = 4096;
        // PrimarySha256 left all zeroes

        using var ctx = NativeContext.Create();
        NativeResult res = NativeMethods.ExtractMedia(
            ctx.Handle,
            dummySource,
            null,
            in nativeFacts,
            dummyDest,
            null,
            null);

        Assert.Equal(NativeResult.InvalidArgument, res);
        string? lastErr = ctx.GetLastError();
        Assert.NotNull(lastErr);
        Assert.StartsWith("[InvalidFacts]", lastErr);
    }

    [Fact]
    public async Task Extract_SourceIndexZero_WithSecondaryPathPresent_StillReportsEmbeddedPrimaryVideoCorrectly()
    {
        using var tempDir = new DisposableTempDirectory();
        string primarySource = Path.Combine(tempDir.Path, "primary.jpg");
        string secondarySource = Path.Combine(tempDir.Path, "secondary.mp4");

        byte[] primaryBytes = CreateDummyFileBytes(8192);
        byte[] secondaryBytes = CreateDummyFileBytes(4096, isJpeg: false);
        await File.WriteAllBytesAsync(primarySource, primaryBytes);
        await File.WriteAllBytesAsync(secondarySource, secondaryBytes);

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts { IsPresent = true, Container = ImageContainer.Jpeg, ByteOffset = 0, ByteLength = 4096 },
            MotionVideo = new VideoFacts { IsPresent = true, Container = VideoContainer.Mp4, ByteOffset = 4096, ByteLength = 4096, SourceIndex = 0 }, // SourceIndex = 0: EMBEDDED in primary!
            PrimarySha256 = ComputeSha(primaryBytes)
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        // Even though caller passed secondarySource path, SourceIndex == 0 means embedded video
        var bundle = await extractor.ExtractAsync(facts, primarySource, secondarySource, workspace);

        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(4096, bundle.MotionVideo.ByteLength);
        Assert.Contains(bundle.ExtractedProtocolFacts, f => f.Component == "Embedded motion video");
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
