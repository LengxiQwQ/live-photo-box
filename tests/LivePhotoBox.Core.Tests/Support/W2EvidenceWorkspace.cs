using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Core.Tests.Support;

/// <summary>
/// Deterministic W2-only workspace. Its fixed path makes evidence and
/// repeated local runs inspectable; Dispose intentionally keeps the evidence
/// directory for the audit handoff.
/// </summary>
internal sealed class W2EvidenceWorkspace : IMediaWorkspace
{
    private readonly Dictionary<string, int> _allocatedNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    internal W2EvidenceWorkspace(string caseName)
    {
        string safeCase = Sanitize(caseName);
        RootDirectory = Path.Combine(W2EvidencePaths.RepositoryRoot, ".ai-tmp", "workspace", "p2-w2", safeCase);
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
        string cleanPrefix = Sanitize(prefix);
        string cleanExtension = extension.StartsWith('.') ? extension : "." + extension;
        int ordinal = _allocatedNames.TryGetValue(cleanPrefix + cleanExtension, out int current)
            ? current + 1
            : 1;
        _allocatedNames[cleanPrefix + cleanExtension] = ordinal;
        string name = ordinal == 1
            ? cleanPrefix + cleanExtension
            : $"{cleanPrefix}-{ordinal}{cleanExtension}";
        return Path.Combine(RootDirectory, name);
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
            FileShare.ReadWrite,
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
                $"W2 source changed: '{sourcePath}' expected {expectedSha256}, actual {actual}.");
        }
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
}

internal static class W2EvidencePaths
{
    private static string? _repositoryRoot;

    internal static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();

    internal static string CacheRoot => Path.Combine(RepositoryRoot, ".ai-tmp", "cache", "samples");

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

        throw new DirectoryNotFoundException("Unable to locate the Live Photo Box repository root for W2 evidence.");
    }
}
