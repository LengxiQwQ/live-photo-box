using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorCleanupSourceTransactionTests
{
    [Fact]
    [Trait("Category", "Extractor")]
    public async Task NativeCleanupSourceWriteFailureAfterWrittenChunk_RemovesOwnedPartialAndPreservesOriginalFailure()
    {
        using var workspace = new P2RepairEvidenceWorkspace("cleanup-source-partial-copy");
        string sourcePath = await CopySamsungSampleIntoWorkspaceAsync(workspace, "partial-copy-source.jpg");
        string beforeSha = await workspace.ComputeFileSha256Async(sourcePath);
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sourcePath);
        ExtractionException error = await Assert.ThrowsAsync<ExtractionException>(() =>
            new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                sourcePath,
                null,
                workspace,
                context => TestNativeHarness.SetExtractorFault(
                    context,
                    NativeExtractorFault.WriteFail,
                    targetArtifact: 100,
                    triggerAfterBytes: 128 * 1024)));

        Assert.Equal(ExtractionFailureCategory.OutputWriteFailed, error.Category);
        string cleanupSourcePath = workspace.GetAllocatedPath("cleanup-source");
        Assert.False(File.Exists(cleanupSourcePath), "The transaction-owned partial cleanup source was left behind.");
        Assert.Equal(beforeSha, await workspace.ComputeFileSha256Async(sourcePath));
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task NativeCleanupSourcePostPublishReplacement_PreservesForeignReplacementAndReportsPublishFailure()
    {
        using var workspace = new P2RepairEvidenceWorkspace("cleanup-source-replacement-race");
        string sourcePath = await CopySamsungSampleIntoWorkspaceAsync(workspace, "race-source.jpg");
        string beforeSha = await workspace.ComputeFileSha256Async(sourcePath);
        byte[] sentinel = Encoding.UTF8.GetBytes("third-party cleanup-source sentinel");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sourcePath);
        var state = new CleanupSourceRaceState(sentinel);
        GCHandle stateHandle = GCHandle.Alloc(state);
        try
        {
            nint callback = PostPublishReplacementCallback();
        ExtractionException error = await Assert.ThrowsAsync<ExtractionException>(() =>
                new SourceExtractor().ExtractAsync(
                    inspected.ExtractionPlan,
                    sourcePath,
                    null,
                    workspace,
                    context =>
                    {
                        state.Path = workspace.GetAllocatedPath("cleanup-source");
                        TestNativeHarness.SetExtractorFault(
                            context,
                            NativeExtractorFault.PostPublishBarrier,
                            targetArtifact: 100,
                            triggerAfterBytes: 0,
                            callback,
                            GCHandle.ToIntPtr(stateHandle));
                    }));

            Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, error.Category);
            Assert.True(Volatile.Read(ref state.CallbackInvoked) > 0, "The Native post-publish replacement barrier was not reached.");
            Assert.Null(state.CallbackError);
            string cleanupSourcePath = workspace.GetAllocatedPath("cleanup-source");
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(cleanupSourcePath));
            Assert.NotNull(state.MovedOwnedPath);
            Assert.False(File.Exists(state.MovedOwnedPath), "The transaction-owned artifact moved by the attacker was not rolled back.");
            Assert.Equal(beforeSha, await workspace.ComputeFileSha256Async(sourcePath));
        }
        finally
        {
            stateHandle.Free();
        }
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task NativeCleanupSourceCancellationAfterWrittenChunk_RemovesOwnedPartialAndPreservesCancellation()
    {
        using var workspace = new P2RepairEvidenceWorkspace("cleanup-source-cancellation-race");
        string sourcePath = await CopySamsungSampleIntoWorkspaceAsync(workspace, "cancelled-source.jpg");
        string beforeSha = await workspace.ComputeFileSha256Async(sourcePath);
        using var cts = new CancellationTokenSource();
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sourcePath);
        GCHandle stateHandle = GCHandle.Alloc(cts);
        try
        {
            nint callback = CancellationCallback();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new SourceExtractor().ExtractAsync(
                    inspected.ExtractionPlan,
                    sourcePath,
                    null,
                    workspace,
                    context => TestNativeHarness.SetExtractorFault(
                        context,
                        NativeExtractorFault.None,
                        targetArtifact: 100,
                        triggerAfterBytes: 0,
                        callback,
                        GCHandle.ToIntPtr(stateHandle)),
                    cts.Token));

            Assert.True(cts.IsCancellationRequested, "The Native cleanup-source callback did not request cancellation.");
        string cleanupSourcePath = workspace.GetAllocatedPath("cleanup-source");
            Assert.False(File.Exists(cleanupSourcePath), "The transaction-owned partial cleanup source was left behind after cancellation.");
        Assert.Equal(beforeSha, await workspace.ComputeFileSha256Async(sourcePath));
        }
        finally
        {
            stateHandle.Free();
        }
    }

    private static unsafe nint PostPublishReplacementCallback()
    {
        delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &ReplacePublishedCleanupSource;
        return (nint)callback;
    }

    private static unsafe nint CancellationCallback()
    {
        delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &CancelCleanupSourceExtraction;
        return (nint)callback;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReplacePublishedCleanupSource(nint userData, int _, ulong __)
    {
        var state = (CleanupSourceRaceState)GCHandle.FromIntPtr(userData).Target!;
        if (Interlocked.CompareExchange(ref state.CallbackInvoked, 1, 0) != 0) return;
        try
        {
            string moved = state.Path! + ".owned-before-replacement";
            File.Move(state.Path!, moved, overwrite: false);
            state.MovedOwnedPath = moved;
            File.WriteAllBytes(state.Path!, state.Sentinel);
        }
        catch (Exception exception)
        {
            state.CallbackError = exception.ToString();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CancelCleanupSourceExtraction(nint userData, int _, ulong bytesProcessed)
    {
        if (bytesProcessed > 0)
            ((CancellationTokenSource)GCHandle.FromIntPtr(userData).Target!).Cancel();
    }

    private sealed class CleanupSourceRaceState(byte[] sentinel)
    {
        internal string? Path { get; set; }
        internal string? MovedOwnedPath { get; set; }
        internal byte[] Sentinel { get; } = sentinel;
        internal string? CallbackError { get; set; }
        internal int CallbackInvoked;
    }

    private static async Task<string> CopySamsungSampleIntoWorkspaceAsync(
        P2RepairEvidenceWorkspace workspace,
        string destinationName)
    {
        W2SampleSnapshot snapshot = await W2SampleEvidence.CacheAsync("三星.jpg");
        string inputDirectory = Path.Combine(workspace.RootDirectory, "inputs");
        Directory.CreateDirectory(inputDirectory);
        string destinationPath = Path.Combine(inputDirectory, destinationName);
        File.Copy(snapshot.CachePath, destinationPath, overwrite: true);
        Assert.Equal(snapshot.CacheSha256, await workspace.ComputeFileSha256Async(destinationPath));
        return destinationPath;
    }
}

internal sealed class P2RepairEvidenceWorkspace : IMediaWorkspace
{
    private readonly Dictionary<string, int> _allocationCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _allocatedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal P2RepairEvidenceWorkspace(string caseName)
    {
        RootDirectory = Path.Combine(
            FindRepositoryRoot(),
            ".ai-tmp",
            "workspace",
            "p2-w1-w4-final-audit",
            Sanitize(caseName));
        if (Directory.Exists(RootDirectory))
            Directory.Delete(RootDirectory, recursive: true);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "inputs"));
    }

    public string RootDirectory { get; }

    public string AllocateFilePath(string prefix, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string cleanPrefix = Sanitize(prefix);
        string cleanExtension = extension.StartsWith('.') ? extension : "." + extension;
        string key = cleanPrefix + cleanExtension;
        int ordinal = _allocationCounts.TryGetValue(key, out int current) ? current + 1 : 1;
        _allocationCounts[key] = ordinal;
        string fileName = ordinal == 1 ? key : $"{cleanPrefix}-{ordinal}{cleanExtension}";
        string path = Path.Combine(RootDirectory, fileName);
        _allocatedPaths[cleanPrefix] = path;
        return path;
    }

    public async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    public async Task AssertSourceUnmodifiedAsync(
        string sourcePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string actual = await ComputeFileSha256Async(sourcePath, cancellationToken);
        Assert.Equal(expectedSha256, actual);
    }

    internal string GetAllocatedPath(string prefix) => _allocatedPaths[prefix];

    public void Dispose() => _disposed = true;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(directory, ".ai")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root for P2 repair evidence.");
    }

    private static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 64)];
        int length = 0;
        foreach (char c in value)
        {
            if (length == buffer.Length) break;
            buffer[length++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return length == 0 ? "case" : new string(buffer[..length]);
    }
}
