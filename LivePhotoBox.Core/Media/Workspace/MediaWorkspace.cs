using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Media.Workspace;

public sealed class MediaWorkspace : IMediaWorkspace
{
    private readonly string _rootDirectory;
    private bool _disposed;

    public string RootDirectory => _rootDirectory;

    public MediaWorkspace()
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "LivePhotoBox", "ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootDirectory);
    }

    public string AllocateFilePath(string prefix, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string ext = extension.StartsWith('.') ? extension : "." + extension;
        string fileName = $"{prefix}-{Guid.NewGuid():N}{ext}";
        return Path.Combine(_rootDirectory, fileName);
    }

    public async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for SHA256 computation.", filePath);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public async Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default)
    {
        string actualSha = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expectedSha256, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Source file immutability violation: '{sourcePath}' was modified in-place! Expected {expectedSha256}, got {actualSha}.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup of temp directory
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
