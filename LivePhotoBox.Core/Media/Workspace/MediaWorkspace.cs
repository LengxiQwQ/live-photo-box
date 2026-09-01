using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Media.Workspace;

/// <summary>
/// Manages a temporary workspace for media operations, ensuring source integrity and automatic cleanup.
/// </summary>
public sealed class MediaWorkspace : IMediaWorkspace
{
    private readonly bool _preserveOnDispose;
    private bool _disposed;

    public string RootDirectory { get; }

    public MediaWorkspace(string? baseDirectory = null, bool preserveOnDispose = false)
    {
        _preserveOnDispose = preserveOnDispose;
        string root = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(Path.GetTempPath(), "LivePhotoBox_Workspace_" + Guid.NewGuid().ToString("N"))
            : Path.Combine(baseDirectory, "LivePhotoBox_Workspace_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);
        RootDirectory = root;
    }

    public string CreateSubdirectory(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string subDir = Path.Combine(RootDirectory, name);
        Directory.CreateDirectory(subDir);
        return subDir;
    }

    public string AllocateTempFilePath(string prefix, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string ext = extension.TrimStart('.');
        string fileName = $"{prefix}_{Guid.NewGuid():N}.{ext}";
        return Path.Combine(RootDirectory, fileName);
    }

    public async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found for hash calculation: '{filePath}'", filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);

        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public async Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return;

        string currentHash = await ComputeFileSha256Async(sourcePath, ct).ConfigureAwait(false);
        if (!string.Equals(expectedSha256, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Source file immutability violation! File '{Path.GetFileName(sourcePath)}' hash changed from {expectedSha256} to {currentHash}.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_preserveOnDispose && Directory.Exists(RootDirectory))
        {
            try
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
