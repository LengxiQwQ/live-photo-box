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

public sealed class CleanerRealFilesystemTransactionTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private sealed class LockedDestinationWorkspace : IMediaWorkspace
    {
        private readonly IMediaWorkspace _inner;
        private readonly string _lockedFilePath;

        public LockedDestinationWorkspace(IMediaWorkspace inner, string lockedFilePath)
        {
            _inner = inner;
            _lockedFilePath = lockedFilePath;
        }

        public string RootDirectory => _inner.RootDirectory;

        public string AllocateFilePath(string prefix, string extension)
        {
            if (prefix == "clean-img")
            {
                return _lockedFilePath;
            }
            return _inner.AllocateFilePath(prefix, extension);
        }

        public Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
            => _inner.ComputeFileSha256Async(filePath, cancellationToken);

        public Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default)
            => _inner.AssertSourceUnmodifiedAsync(sourcePath, expectedSha256, cancellationToken);

        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RealFileSystem_DestinationLockedWithNoShare_FailsClosedAndRollsBack()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Pre-create and lock destination file with exclusive FileShare.None
        string lockedPath = Path.Combine(workspace.RootDirectory, "locked-destination.jpg");
        using var lockStream = new FileStream(lockedPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        lockStream.WriteByte(0x42);
        lockStream.Flush();

        var lockedWorkspace = new LockedDestinationWorkspace(workspace, lockedPath);

        var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
        {
            ExtractedBundle = extracted
        }, lockedWorkspace);

        Assert.False(cleanResult.Success);
        Assert.Equal(CleanerFailureCategory.PublishFailed, cleanResult.FailureCategory);
        Assert.Equal(CleanerFailureStage.Commit, cleanResult.FailureStage);
        Assert.Equal(CleanerTransactionState.RolledBack, cleanResult.TransactionState);

        // Verify original source immutability
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Verify rollback cleaned up staging directories
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RealFileSystem_PreExecutionCancellation_RollsBackCleanly()
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
        // Cancel token immediately before clean to trigger real OS cancellation in async path
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = extracted
            }, workspace, cts.Token);
        });

        // Verify original source immutability
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Verify no orphan staging directories
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RealFileSystem_InFlightCancellationDuringStaging_RollsBackCleanly()
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
        // Trigger real cancellation in-flight when staging starts
        cleaner.OnStagingStarted += () => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = extracted
            }, workspace, cts.Token);
        });

        // Verify original source immutability
        Assert.Equal(shaBefore, ComputeSha256(samplePath));

        // Verify no orphan staging directories
        string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
        Assert.Empty(stagingDirs);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Clean_RealFileSystem_ReadOnlyExistingDestinationFile_FailsClosedAndRollsBack()
    {
        string samplePath = ResolveSample("oppo.jpg");
        string shaBefore = ComputeSha256(samplePath);

        using var workspace = new MediaWorkspace();
        var inspector = new SourceInspector();
        var extractor = new SourceExtractor();
        var cleaner = new SourceProtocolCleaner();

        var facts = await inspector.InspectAsync(samplePath);
        var extracted = await extractor.ExtractAsync(facts, samplePath, null, workspace);

        // Create a subfolder with ReadOnly attribute and allocate destination inside it
        string readOnlySubdir = Path.Combine(workspace.RootDirectory, "readonly_dest");
        Directory.CreateDirectory(readOnlySubdir);
        
        string nonWritableDest = Path.Combine(readOnlySubdir, "clean-img.jpg");
        // Create destination file with ReadOnly attribute
        await File.WriteAllBytesAsync(nonWritableDest, [0x01, 0x02]);
        File.SetAttributes(nonWritableDest, FileAttributes.ReadOnly);

        try
        {
            var lockedWorkspace = new LockedDestinationWorkspace(workspace, nonWritableDest);

            var cleanResult = await cleaner.CleanAsync(new ProtocolCleanRequest
            {
                ExtractedBundle = extracted
            }, lockedWorkspace);

            Assert.False(cleanResult.Success);
            Assert.Equal(CleanerFailureCategory.PublishFailed, cleanResult.FailureCategory);
            Assert.Equal(CleanerFailureStage.Commit, cleanResult.FailureStage);
            Assert.Equal(CleanerTransactionState.RolledBack, cleanResult.TransactionState);

            // Verify original source immutability
            Assert.Equal(shaBefore, ComputeSha256(samplePath));

            // Verify rollback cleaned up staging directories
            string[] stagingDirs = Directory.GetDirectories(workspace.RootDirectory, "staging_*");
            Assert.Empty(stagingDirs);
        }
        finally
        {
            // Restore attributes for cleanup
            File.SetAttributes(nonWritableDest, FileAttributes.Normal);
        }
    }
}
