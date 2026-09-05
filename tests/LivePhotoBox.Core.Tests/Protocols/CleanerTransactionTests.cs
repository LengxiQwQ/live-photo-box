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

    private sealed class StubInspector(SourceMediaFacts resultFacts) : ISourceInspector
    {
        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(resultFacts);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RollsBackAndThrowsWhenCancelledBeforeCommit()
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
        cts.Cancel(); // Pre-cancel

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = extracted
            }, workspace, cts.Token);
        });

        // Verify source immutability
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Verify no orphaned published clean outputs exist
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RollsBackWhenPostCleanInspectorRejectsArtifact()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var normalInspector = new SourceInspector();
        var extractor = new SourceExtractor();

        var facts = await normalInspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Stub inspector that claims the output is STILL an active OppoLivePhoto (inspection failure)
        var rejectingInspector = new StubInspector(new SourceMediaFacts
        {
            Protocol = SourceProtocol.OppoLivePhoto,
            PrimarySha256 = "dummy",
            PrimaryImage = new ImageFacts { ByteOffset = 0, ByteLength = 100, IsPresent = true },
            MotionVideo = new VideoFacts { ByteOffset = 100, ByteLength = 500, IsPresent = true }
        });

        var cleaner = new SourceProtocolCleaner(rejectingInspector);

        var result = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, workspace);

        Assert.False(result.Success);
        Assert.Equal(CleanerFailureCategory.ProtocolStillDetected, result.FailureCategory);
        Assert.Equal(CleanerFailureStage.PostCleanInspection, result.FailureStage);
        Assert.Null(result.CleanedImage);
        Assert.Null(result.CleanedVideo);

        // Source file untouched
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Staging cleaned up
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
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

        Assert.True(cleanResult1.Success);
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
}
