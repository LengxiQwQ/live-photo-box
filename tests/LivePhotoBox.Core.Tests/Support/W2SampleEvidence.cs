using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Core.Tests.Support;

internal sealed record W2SampleSnapshot(
    string FileName,
    string OriginalPath,
    string CachePath,
    string OriginalSha256,
    string CacheSha256);

internal static class W2SampleEvidence
{
    internal static async Task<W2SampleSnapshot> CacheAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        string originalPath = TestSampleResolver.ResolveSample(fileName);
        Directory.CreateDirectory(W2EvidencePaths.CacheRoot);
        string cachePath = Path.Combine(W2EvidencePaths.CacheRoot, fileName);
        string originalSha = await ComputeSha256Async(originalPath, cancellationToken).ConfigureAwait(false);

        string? cacheSha = File.Exists(cachePath)
            ? await ComputeSha256Async(cachePath, cancellationToken).ConfigureAwait(false)
            : null;
        if (!string.Equals(originalSha, cacheSha, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(originalPath, cachePath, overwrite: true);
            cacheSha = await ComputeSha256Async(cachePath, cancellationToken).ConfigureAwait(false);
        }

        return new W2SampleSnapshot(fileName, originalPath, cachePath, originalSha, cacheSha!);
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
