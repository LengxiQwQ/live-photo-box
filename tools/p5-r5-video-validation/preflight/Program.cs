using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 4)
            {
                throw new ArgumentException("Usage: P5R5Preflight <source-path> <workspace-path> <expected-sha256> <expected-size>");
            }

            string sourcePath = Path.GetFullPath(args[0]);
            string outputRoot = Path.GetFullPath(args[1]);
            string expectedHash = args[2].ToUpperInvariant();
            if (!long.TryParse(args[3], out long expectedSize) || expectedSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(args), "Expected size must be a positive integer.");
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Preflight source file is missing.", sourcePath);
            }

            long parentSize = new FileInfo(sourcePath).Length;
            string parentHash = await HashFileAsync(sourcePath, CancellationToken.None).ConfigureAwait(false);
            if (parentSize != expectedSize || !string.Equals(parentHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Parent identity mismatch: bytes={parentSize}, sha256={parentHash}.");
            }

            using InspectedSource inspected = await new SourceInspector()
                .InspectWithPlanAsync(sourcePath, null, CancellationToken.None)
                .ConfigureAwait(false);

            SourceMediaFacts facts = inspected.Facts;
            if (!string.Equals(facts.PrimarySha256, parentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Native Inspector's bound parent identity does not match the cached source hash.");
            }

            VideoFacts sourceVideo = facts.MotionVideo is { IsPresent: true } video
                ? video
                : throw new InvalidDataException("Native Inspector did not identify a motion-video item.");

            ValidateRange(sourceVideo.ByteOffset, sourceVideo.ByteLength, parentSize);
            string sourceRangeHash = await HashRangeAsync(sourcePath, sourceVideo.ByteOffset, sourceVideo.ByteLength, CancellationToken.None)
                .ConfigureAwait(false);

            await using var workspace = new PersistentMediaWorkspace(outputRoot);
            ExtractedMediaBundle bundle = await new SourceExtractor()
                .ExtractAsync(inspected.ExtractionPlan, sourcePath, null, workspace, CancellationToken.None)
                .ConfigureAwait(false);

            MediaArtifact motionVideo = bundle.MotionVideo
                ?? throw new InvalidDataException("Native SourceExtractor did not materialize the inspected motion-video item.");
            string extractedHash = await workspace.ComputeFileSha256Async(motionVideo.Path, CancellationToken.None).ConfigureAwait(false);
            long extractedSize = new FileInfo(motionVideo.Path).Length;

            if (motionVideo.SourceOffset != sourceVideo.ByteOffset ||
                motionVideo.ByteLength != sourceVideo.ByteLength ||
                extractedSize != sourceVideo.ByteLength ||
                !string.Equals(sourceRangeHash, extractedHash, StringComparison.OrdinalIgnoreCase) ||
                (motionVideo.Sha256 is not null && !string.Equals(motionVideo.Sha256, extractedHash, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Canonical source range and extracted motion-video bytes do not have the same identity.");
            }

            var result = new
            {
                schemaVersion = 1,
                sourcePath,
                parentBytes = parentSize,
                parentSha256 = parentHash,
                protocol = facts.Protocol.ToString(),
                nativeFacts = new
                {
                    byteOffset = sourceVideo.ByteOffset,
                    byteLength = sourceVideo.ByteLength,
                    sourceIndex = sourceVideo.SourceIndex,
                    container = sourceVideo.Container.ToString(),
                    codec = sourceVideo.Codec.ToString(),
                    width = sourceVideo.Width,
                    height = sourceVideo.Height,
                    rotationDegrees = sourceVideo.RotationDegrees,
                    durationSeconds = sourceVideo.DurationSeconds,
                    fps = sourceVideo.Fps,
                    hasAudio = sourceVideo.HasAudio
                },
                sourceRangeSha256 = sourceRangeHash,
                extractedPath = motionVideo.Path,
                extractedBytes = extractedSize,
                extractedSha256 = extractedHash,
                rangeAndExtractedBytesIdentical = true
            };

            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"P5R5_PREFLIGHT_ERROR: {ex}");
            return 1;
        }
    }

    private static void ValidateRange(long offset, long length, long parentSize)
    {
        if (offset < 0 || length <= 0 || offset > parentSize || length > parentSize - offset)
        {
            throw new InvalidDataException("Native Inspector returned a motion-video range outside the hash-locked parent file.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> HashRangeAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Parent file ended inside the inspected motion-video range.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class PersistentMediaWorkspace : IMediaWorkspace
    {
        private readonly Dictionary<string, int> _allocations = new(StringComparer.OrdinalIgnoreCase);

        public PersistentMediaWorkspace(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            if (Directory.Exists(RootDirectory) && Directory.GetFileSystemEntries(RootDirectory).Length > 0)
            {
                throw new IOException($"Refusing to reuse a non-empty deterministic preflight workspace: {RootDirectory}");
            }

            Directory.CreateDirectory(RootDirectory);
        }

        public string RootDirectory { get; }

        public string AllocateFilePath(string prefix, string extension)
        {
            string safePrefix = SanitizePrefix(prefix);
            string ext = extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
            int allocation = _allocations.TryGetValue(safePrefix, out int current) ? current + 1 : 1;
            _allocations[safePrefix] = allocation;
            string suffix = allocation == 1 ? string.Empty : $"-{allocation:D2}";
            string path = Path.Combine(RootDirectory, safePrefix + suffix + ext);
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new IOException($"Refusing to overwrite a preflight artifact: {path}");
            }

            return path;
        }

        public async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("File missing from preflight workspace.", filePath);
            }

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }

        public async Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default)
        {
            string actual = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Source immutability check failed in preflight workspace.");
            }
        }

        public void Dispose()
        {
            // The fixed .ai-tmp workspace is intentionally retained for reproducible evidence.
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string SanitizePrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new ArgumentException("Workspace artifact prefix cannot be empty.", nameof(prefix));
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = prefix.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] is '/' or '\\')
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }
    }
}
