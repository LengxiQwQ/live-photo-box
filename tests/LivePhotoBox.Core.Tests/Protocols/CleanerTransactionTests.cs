using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class CleanerTransactionTests
{
    private static string ResolveSample(string filename)
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "designs", "各个机型测试", filename);
            if (File.Exists(candidate)) return candidate;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Sample file '{filename}' not found.");
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private sealed class DelegateInspector(Func<string, string?, Task<SourceMediaFacts>> handler) : ISourceInspector
    {
        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            return handler(primaryPath, secondaryPath);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_DeterministicCancellation_MidStaging_RollsBackCleanlyAndThrows()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        using var cts = new CancellationTokenSource();

        // Inject deterministic cancellation right after image staged
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Staging && detail == "ImageStaged")
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = extracted
            }, workspace, cts.Token);
        });

        // Verify source immutability
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Verify no orphaned staging or published clean outputs exist
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
        string[] publishedImgs = Directory.GetFiles(workspace.RootDirectory, "clean-img*");
        Assert.Empty(publishedImgs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_DeterministicCancellation_BeforeCommit_RollsBackCleanlyAndThrows()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        using var cts = new CancellationTokenSource();

        // Inject deterministic cancellation in commit stage right before publishing
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Commit && detail == "BeforePublish")
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = extracted
            }, workspace, cts.Token);
        });

        // Verify source immutability
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Verify no orphaned staging or published clean outputs exist
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
        string[] publishedImgs = Directory.GetFiles(workspace.RootDirectory, "clean-img*");
        Assert.Empty(publishedImgs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_PartialPublishFailure_ImageSucceedsVideoFails_RollsBackAndLeavesZeroPartialArtifacts()
    {
        string imgPath = ResolveSample("苹果双文件.HEIC");
        string movPath = ResolveSample("苹果双文件.MOV");
        string imgShaBefore = ComputeSha256(imgPath);
        string movShaBefore = ComputeSha256(movPath);

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Inject simulated failure right after image is published, before video is moved
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Commit && detail == "ImagePublished")
            {
                throw new IOException("Simulated disk full or hardware fault during video publish.");
            }
            return Task.CompletedTask;
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.PublishFailed, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Commit, result.FailureStage);
        Assert.Equal(CleanerTransactionState.RolledBack, result.TransactionState);
        Assert.Null(result.CleanedImage);
        Assert.Null(result.CleanedVideo);

        // Sources must remain untouched
        Assert.Equal(imgShaBefore, ComputeSha256(imgPath));
        Assert.Equal(movShaBefore, ComputeSha256(movPath));

        // Zero partial output guarantee: Clean image published must have been rolled back and deleted!
        string[] cleanImgs = Directory.GetFiles(workspace.RootDirectory, "clean-img*");
        Assert.Empty(cleanImgs);
        string[] cleanVids = Directory.GetFiles(workspace.RootDirectory, "clean-vid*");
        Assert.Empty(cleanVids);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RollbackFailure_ExplicitlySurfacedAsRollbackFailed()
    {
        string samplePath = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Inject commit failure, and then trigger simulated failure during rollback
        cleaner.FaultInjectionHook = (stage, detail) =>
        {
            if (stage == CleanerFailureStage.Commit && detail == "BeforePublish")
            {
                throw new IOException("Simulated commit failure");
            }
            if (stage == CleanerFailureStage.Rollback)
            {
                throw new IOException("Simulated locked file error during rollback deletion");
            }
            return Task.CompletedTask;
        };

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.RollbackFailed, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.Rollback, result.FailureStage);
        Assert.Equal(CleanerTransactionState.RollbackFailed, result.TransactionState);
        Assert.Contains("Critical rollback failure", result.ErrorMessage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_PostCleanInspection_FailsWhenVideoResidualProtocolDetected()
    {
        string imgPath = ResolveSample("苹果双文件.HEIC");
        string movPath = ResolveSample("苹果双文件.MOV");

        using var workspace = new MediaWorkspace();
        var realInspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await realInspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Mock inspector: when inspecting video individually, reports residual AppleLivePhoto
        var stubInspector = new DelegateInspector(async (p, s) =>
        {
            if (s == null && (p.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
            {
                // Video individually inspected reports residual protocol!
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.AppleLivePhoto,
                    PrimarySha256 = "dummy_vid",
                    PairingIdentifier = "residual_apple_pair_id",
                    PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true }
                };
            }
            return await realInspector.InspectAsync(p, s);
        });

        var cleaner = new SourceProtocolCleaner(stubInspector);

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ProtocolStillDetected, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.PostCleanInspection, result.FailureStage);
        Assert.Null(result.CleanedImage);
        Assert.Null(result.CleanedVideo);

        // Staging cleaned up
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_PostCleanInspection_FailsWhenDualPairStillMatches()
    {
        string imgPath = ResolveSample("苹果双文件.HEIC");
        string movPath = ResolveSample("苹果双文件.MOV");

        using var workspace = new MediaWorkspace();
        var realInspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await realInspector.InspectAsync(imgPath, movPath);
        var extracted = await extractor.ExtractAsync(facts, imgPath, movPath, workspace);

        // Mock inspector: image alone is NonLive, video alone is NonLive, but pair inspection still finds matching pair!
        var stubInspector = new DelegateInspector(async (p, s) =>
        {
            if (s != null)
            {
                // Pair recheck reports pair still bound!
                return new SourceMediaFacts
                {
                    Protocol = SourceProtocol.AppleLivePhoto,
                    PrimarySha256 = "dummy_img",
                    PairingIdentifier = "still_paired_uuid",
                    PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true }
                };
            }
            return await realInspector.InspectAsync(p, s);
        });

        var cleaner = new SourceProtocolCleaner(stubInspector);

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ProtocolStillDetected, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.PostCleanInspection, result.FailureStage);
        Assert.Null(result.CleanedImage);
        Assert.Null(result.CleanedVideo);

        // Staging cleaned up
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_ConcurrentExecutions_IsolatedWorkspaces_SucceedWithoutInterference()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace1 = new MediaWorkspace();
        using var workspace2 = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts1 = await inspector.InspectAsync(samplePath);
        var facts2 = await inspector.InspectAsync(samplePath);

        var extracted1 = await extractor.ExtractAsync(facts1, samplePath, null, workspace1);
        var extracted2 = await extractor.ExtractAsync(facts2, samplePath, null, workspace2);

        var task1 = cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = extracted1 }, workspace1);
        var task2 = cleaner.CleanAsync(new ProtocolCleanRequest { ExtractedBundle = extracted2 }, workspace2);

        await Task.WhenAll(task1, task2);

        var result1 = await task1;
        var result2 = await task2;

        Assert.True(result1.Success, result1.ErrorMessage);
        Assert.True(result2.Success, result2.ErrorMessage);
        Assert.Equal(result1.CleanedImage!.ByteLength, result2.CleanedImage!.ByteLength);
        Assert.Equal(result1.CleanedImage.Sha256, result2.CleanedImage.Sha256);

        // Source immutable
        Assert.Equal(shaBefore, ComputeSha256(samplePath));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_IdempotencyReturnsNonLiveWithoutAdditionalMutations()
    {
        string samplePath = ResolveSample("vivo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        // Pass 1
        var facts1 = await inspector.InspectAsync(samplePath);
        var extracted1 = await extractor.ExtractAsync(facts1, samplePath, null, workspace);
        var cleanResult1 = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted1
        }, workspace);

        Assert.True(cleanResult1.Success, cleanResult1.ErrorMessage);
        Assert.NotNull(cleanResult1.CleanedImage);
        Assert.NotEmpty(cleanResult1.RemovedFacts);

        // Pass 2: Inspect cleaned output
        var facts2 = await inspector.InspectAsync(cleanResult1.CleanedImage.Path);
        Assert.Equal(SourceProtocol.NonLive, facts2.Protocol);

        var extracted2 = await extractor.ExtractAsync(facts2, cleanResult1.CleanedImage.Path, null, workspace);
        var cleanResult2 = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted2
        }, workspace);

        Assert.True(cleanResult2.Success);
        Assert.NotNull(cleanResult2.CleanedImage);
        Assert.Empty(cleanResult2.RemovedFacts);
        Assert.NotNull(cleanResult2.CleanupPlan);
        Assert.Empty(cleanResult2.CleanupPlan.Actions);
        Assert.Equal(PreservationOutcome.Preserved, cleanResult2.PreservationOutcome);

        // Verify original source immutable throughout both passes
        Assert.Equal(shaBefore, ComputeSha256(samplePath));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_LargeVideo_StreamsWithBoundedMemory()
    {
        string imgPath = ResolveSample("苹果双文件.HEIC");
        string movPath = ResolveSample("苹果双文件.MOV");

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        // Construct a 40MB synthetic Apple MOV by copying ftyp/wide/moov from the real sample
        // but expanding mdat to 40MB.
        string largeMovPath = workspace.AllocateFilePath("large-apple-video", ".MOV");
        {
            byte[] movBytes = await File.ReadAllBytesAsync(movPath);
            const long targetMdatPayload = 40 * 1024 * 1024; // 40MB
            using var outFs = File.Create(largeMovPath);
            outFs.Write(movBytes, 0, 28); // ftyp + wide

            // write mdat header (8 bytes)
            long mdatBoxSize = targetMdatPayload + 8;
            byte[] mdatHdr = [
                (byte)(mdatBoxSize >> 24),
                (byte)(mdatBoxSize >> 16),
                (byte)(mdatBoxSize >> 8),
                (byte)mdatBoxSize,
                (byte)'m', (byte)'d', (byte)'a', (byte)'t'
            ];
            outFs.Write(mdatHdr);

            // Sparse seek or chunk write
            outFs.Seek(targetMdatPayload - 1, SeekOrigin.Current);
            outFs.WriteByte(0);

            // Write the original moov box from the real sample (from offset 2776582 to end)
            int moovStart = 2776582;
            outFs.Write(movBytes, moovStart, movBytes.Length - moovStart);
        }

        Assert.True(new FileInfo(largeMovPath).Length > 40 * 1024 * 1024);

        var facts = await inspector.InspectAsync(imgPath, largeMovPath);
        Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);

        var extracted = await extractor.ExtractAsync(facts, imgPath, largeMovPath, workspace);
        Assert.NotNull(extracted.MotionVideo);

        // Record initial memory
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long memBefore = GC.GetTotalMemory(true);

        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long memAfter = GC.GetTotalMemory(true);

        Assert.True(cleanResult.Success, cleanResult.ErrorMessage);
        Assert.NotNull(cleanResult.CleanedVideo);
        Assert.True(File.Exists(cleanResult.CleanedVideo.Path));
        Assert.True(new FileInfo(cleanResult.CleanedVideo.Path).Length > 40 * 1024 * 1024);

        // Bounded memory guarantee: managed memory growth must be far less than the 40MB video size (< 15MB)
        long managedDelta = Math.Max(0, memAfter - memBefore);
        Assert.True(managedDelta < 15 * 1024 * 1024,
            $"Managed memory delta ({managedDelta / (1024 * 1024)}MB) exceeded bounded streaming limit.");

        // Cleaned video must be NonLive
        var recheck = await inspector.InspectAsync(cleanResult.CleanedImage!.Path, cleanResult.CleanedVideo.Path);
        Assert.Equal(SourceProtocol.NonLive, recheck.Protocol);
    }
}

