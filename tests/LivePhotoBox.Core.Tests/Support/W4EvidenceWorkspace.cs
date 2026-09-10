using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Core.Tests.Support;

/// <summary>
/// Fixed, inspectable filesystem root for W4 adversarial tests.  It is
/// deliberately kept after Dispose so the test evidence can be audited.
/// </summary>
internal sealed class W4EvidenceWorkspace : IMediaWorkspace
{
    private readonly Dictionary<string, int> _allocatedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal W4EvidenceWorkspace(string caseName)
    {
        string safeCase = Sanitize(caseName);
        RootDirectory = Path.Combine(
            W4EvidencePaths.RepositoryRoot,
            ".ai-tmp",
            "workspace",
            "p2-w4",
            safeCase);
        if (Directory.Exists(RootDirectory))
        {
            RemoveExistingRootSafely(RootDirectory);
        }
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(Path.Combine(RootDirectory, "inputs"));
    }

    internal Action<string, string>? OnAllocated { get; set; }

    internal IReadOnlyDictionary<string, string> AllocatedPaths => _allocatedPaths;

    private readonly Dictionary<string, string> _allocatedPaths = new(StringComparer.OrdinalIgnoreCase);

    public string RootDirectory { get; }

    public string AllocateFilePath(string prefix, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string cleanPrefix = Sanitize(prefix);
        string cleanExtension = extension.StartsWith('.') ? extension : "." + extension;
        string key = cleanPrefix + cleanExtension;
        int ordinal = _allocatedNames.TryGetValue(key, out int current) ? current + 1 : 1;
        _allocatedNames[key] = ordinal;
        string name = ordinal == 1 ? key : $"{cleanPrefix}-{ordinal}{cleanExtension}";
        string path = Path.Combine(RootDirectory, name);
        _allocatedPaths[cleanPrefix] = path;
        OnAllocated?.Invoke(cleanPrefix, path);
        return path;
    }

    public async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found for SHA-256 computation.", filePath);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public async Task AssertSourceUnmodifiedAsync(
        string sourcePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string actual = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"W4 source changed: '{sourcePath}' expected {expectedSha256}, actual {actual}.");
        }
    }

    public void Dispose() => _disposed = true;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "case";
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 64)];
        int length = 0;
        foreach (char c in value)
        {
            if (length == buffer.Length) break;
            buffer[length++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return length == 0 ? "case" : new string(buffer[..length]);
    }

    private static void RemoveExistingRootSafely(string root)
    {
        // Directory.Delete(root, true) follows a junction/reparse entry on
        // some Windows builds.  Remove only the immediate reparse entry
        // itself, then delete the validated W4 case directory.
        foreach (string child in Directory.GetFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(child, recursive: false);
                }
                else
                {
                    File.Delete(child);
                }
            }
        }
        Directory.Delete(root, recursive: true);
    }
}

internal static class W4EvidencePaths
{
    private static string? _repositoryRoot;

    internal static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();

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

        throw new DirectoryNotFoundException("Unable to locate the Live Photo Box repository root for W4 evidence.");
    }
}
