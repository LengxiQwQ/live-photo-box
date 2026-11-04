using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

// In-memory receipt only. Log/history DTOs cannot authorize source actions.
internal sealed class ProductSourceActionGuard
{
    private sealed record Snapshot(string Path, WindowsFileIdentity Identity, string Hash);
    private readonly Snapshot[] sources;
    private readonly Snapshot[] outputs;
    private ProductSourceActionGuard(Snapshot[] sources, Snapshot[] outputs) =>
        (this.sources, this.outputs) = (sources, outputs);

    internal static ProductSourceActionGuard Capture(IEnumerable<string> inputs, IEnumerable<ProductCommittedArtifact> outputs) =>
        new(inputs.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Select(CaptureFile).ToArray(),
            outputs.Select(o => CaptureFile(o.Path)).ToArray());

    private static Snapshot CaptureFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Source post-processing cannot authorize a reparse-point path.");
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var identity = WindowsFileIdentity.Capture(file.SafeFileHandle);
        if (identity.IsReparsePoint || identity.LinkCount != 1)
            throw new IOException("Source post-processing cannot authorize a reparse point or multiply linked file.");
        return new(Path.GetFullPath(path), identity, Convert.ToHexString(SHA256.HashData(file)));
    }

    internal void Validate(ProductOperationOutcome outcome, IEnumerable<string> paths, string? moveDirectory = null)
    {
        if (!outcome.IsSuccess || !ReferenceEquals(outcome.SourceActionGuard, this))
            throw new IOException("Source post-processing requires this operation's green completion receipt.");
        var requested = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!requested.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(sources.Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Source post-processing input set differs from the completed operation.");
        foreach (var snapshot in sources.Concat(outputs))
        {
            var current = CaptureFile(snapshot.Path);
            if (!current.Identity.Matches(snapshot.Identity) || current.Hash != snapshot.Hash)
                throw new IOException("Source or output changed after completion; originals were retained: " + snapshot.Path);
        }
        if (moveDirectory is not null)
            foreach (string source in requested)
            {
                string destination = Path.Combine(Path.GetFullPath(moveDirectory), Path.GetFileName(source));
                if (File.Exists(destination) || Directory.Exists(destination))
                    throw new IOException("Archive destination already exists; originals were retained: " + destination);
            }
    }

    internal void ValidateSource(ProductOperationOutcome outcome, string path)
    {
        if (!outcome.IsSuccess || !ReferenceEquals(outcome.SourceActionGuard, this))
            throw new IOException("Source post-processing requires this operation's green completion receipt.");
        string fullPath = Path.GetFullPath(path);
        var expected = sources.SingleOrDefault(s => string.Equals(s.Path, fullPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Source is not authorized by the completed operation.");
        var current = CaptureFile(fullPath);
        if (!current.Identity.Matches(expected.Identity) || current.Hash != expected.Hash)
            throw new IOException("Source changed before post-processing; file was retained: " + fullPath);
    }
}
