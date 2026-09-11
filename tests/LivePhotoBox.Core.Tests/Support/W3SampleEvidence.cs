using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Core.Tests.Support;

internal sealed record W3SampleSnapshot(
    string FileName,
    string OriginalPath,
    string CachePath,
    string OriginalSha256,
    string CacheSha256);

internal static class W3SampleEvidence
{
    internal static async Task<W3SampleSnapshot> CacheAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        string originalPath = TestSampleResolver.ResolveSample(fileName);
        Directory.CreateDirectory(W3EvidencePaths.CacheRoot);
        string cachePath = Path.Combine(W3EvidencePaths.CacheRoot, fileName);
        string originalSha = await ComputeSha256Async(originalPath, cancellationToken).ConfigureAwait(false);
        string? cacheSha = File.Exists(cachePath)
            ? await ComputeSha256Async(cachePath, cancellationToken).ConfigureAwait(false)
            : null;
        if (!string.Equals(originalSha, cacheSha, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(originalPath, cachePath, overwrite: true);
            cacheSha = await ComputeSha256Async(cachePath, cancellationToken).ConfigureAwait(false);
        }

        return new W3SampleSnapshot(fileName, originalPath, cachePath, originalSha, cacheSha!);
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class W3EvidenceWorkspace : IMediaWorkspace
{
    private readonly Dictionary<string, int> _allocatedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal W3EvidenceWorkspace(string caseName)
    {
        RootDirectory = Path.Combine(W3EvidencePaths.WorkspaceRoot, caseName);
        if (Directory.Exists(RootDirectory))
        {
            Directory.Delete(RootDirectory, recursive: true);
        }
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public string AllocateFilePath(string prefix, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string cleanPrefix = string.IsNullOrWhiteSpace(prefix) ? "artifact" : prefix;
        string cleanExtension = extension.StartsWith('.') ? extension : "." + extension;
        string key = cleanPrefix + cleanExtension;
        int ordinal = _allocatedNames.TryGetValue(key, out int current) ? current + 1 : 1;
        _allocatedNames[key] = ordinal;
        string fileName = ordinal == 1 ? key : $"{cleanPrefix}-{ordinal}{cleanExtension}";
        return Path.Combine(RootDirectory, fileName);
    }

    public async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await W3SampleEvidence.ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task AssertSourceUnmodifiedAsync(
        string sourcePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string actual = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"W3 source changed: '{sourcePath}'.");
    }

    public void Dispose()
    {
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class W3EvidencePaths
{
    internal static string RepositoryRoot => W2EvidencePaths.RepositoryRoot;
    internal static string CacheRoot => Path.Combine(RepositoryRoot, ".ai-tmp", "cache", "samples");
    internal static string WorkspaceRoot => Path.Combine(RepositoryRoot, ".ai-tmp", "workspace", "p2-w3");
}
