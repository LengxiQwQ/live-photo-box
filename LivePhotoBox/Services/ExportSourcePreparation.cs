using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Services;

/// <summary>Creates owner-tracked temporary inputs used by Edit export.</summary>
internal static class ExportSourcePreparation
{
    private const int CopyBufferSize = 64 * 1024;

    // The test callback runs only after a real primary-range chunk has been
    // written. Tests use it to request cancellation without throwing.
    internal static AsyncLocal<Action<long>?> RangeChunkWrittenTestHook { get; } = new();

    internal static void DisposePreparedSource(
        ref WindowsOwnedTemporaryArtifact? artifact, Exception? originalFailure = null)
    {
        try
        {
            artifact?.Dispose();
            artifact = null;
        }
        catch (Exception cleanupFailure)
        {
            // Retain ownership on failure so a bounded finally retry can use
            // the same receipt. Preserve both failures when cleanup also fails.
            if (originalFailure != null)
                throw new AggregateException(
                    "Export failed and its prepared source cleanup also failed [CleanupFailed].",
                    originalFailure, cleanupFailure);
            throw;
        }
    }

    internal static Task<WindowsOwnedTemporaryArtifact> PrepareStillRangeAsync(
        string sourcePath,
        long offset,
        long length,
        CancellationToken token,
        string? expectedSourceSha256 = null,
        string? temporaryDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string extension = Path.GetExtension(sourcePath);
        return WindowsOwnedTemporaryArtifact.CreateAsync(
            temporaryDirectory ?? Path.GetTempPath(),
            "lpb_still_",
            extension,
            (destination, writerToken) => CopyRangeAsync(
                sourcePath,
                offset,
                length,
                expectedSourceSha256,
                destination,
                writerToken),
            token);
    }

    internal static Task<WindowsOwnedTemporaryArtifact> PrepareOriginalBytesAsync(
        ReadOnlyMemory<byte> originalBytes,
        CancellationToken token,
        string? temporaryDirectory = null)
    {
        if (originalBytes.IsEmpty)
            throw new ArgumentException("Original photo bytes must not be empty.", nameof(originalBytes));

        return WindowsOwnedTemporaryArtifact.CreateAsync(
            temporaryDirectory ?? Path.GetTempPath(),
            "lpb_orig_export_",
            ".jpg",
            (destination, writerToken) => destination.WriteAsync(originalBytes, writerToken).AsTask(),
            token);
    }

    private static async Task CopyRangeAsync(
        string sourcePath,
        long offset,
        long length,
        string? expectedSourceSha256,
        Stream destination,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || length <= 0)
            throw new InvalidDataException("Requested primary image range is invalid.");

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            useAsync: true);
        if (offset > source.Length || length > source.Length - offset)
            throw new InvalidDataException("Requested primary image range is outside the source file.");

        if (!string.IsNullOrWhiteSpace(expectedSourceSha256))
        {
            string actualSourceSha256 = await ComputeSha256Async(source, token).ConfigureAwait(false);
            if (!string.Equals(
                    actualSourceSha256,
                    expectedSourceSha256.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "[SourceChanged] Source SHA-256 no longer matches the Native inspection snapshot.");
            }
        }

        source.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = length;
        long written = 0;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Source changed while extracting the inspected primary range.");

            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
            written += read;
            RangeChunkWrittenTestHook.Value?.Invoke(written);
        }

        token.ThrowIfCancellationRequested();
    }

    private static async Task<string> ComputeSha256Async(Stream source, CancellationToken token)
    {
        source.Seek(0, SeekOrigin.Begin);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
