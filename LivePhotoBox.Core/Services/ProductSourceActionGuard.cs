using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Services;

// In-memory receipt only. Log/history DTOs cannot authorize source actions.
internal sealed class ProductSourceActionGuard
{
    private sealed record Snapshot(string Path, WindowsFileIdentity Identity, string Hash);
    private readonly Snapshot[] sources;
    private readonly Snapshot[] outputs;
    private ProductSourceActionGuard(Snapshot[] sources, Snapshot[] outputs) =>
        (this.sources, this.outputs) = (sources, outputs);

    internal static ProductSourceActionGuard Capture(IEnumerable<string> inputs, IEnumerable<ProductCommittedArtifact> outputs)
    {
        var attempt = TryCapture(inputs, outputs);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Guard!;
    }

    internal sealed record CaptureAttempt(ProductSourceActionGuard? Guard, Exception? Failure);

    internal static CaptureAttempt TryCapture(IEnumerable<string> inputs, IEnumerable<ProductCommittedArtifact> outputs)
    {
        var sourceSnapshots = new List<Snapshot>();
        var outputSnapshots = new List<Snapshot>();
        foreach (string path in inputs.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = TryCaptureFile(path);
            if (snapshot.Failure is { } failure) return new(null, failure);
            sourceSnapshots.Add(snapshot.Snapshot!);
        }
        foreach (var output in outputs)
        {
            var snapshot = TryCaptureFile(output.Path);
            if (snapshot.Failure is { } failure) return new(null, failure);
            outputSnapshots.Add(snapshot.Snapshot!);
        }
        return new(new ProductSourceActionGuard(sourceSnapshots.ToArray(), outputSnapshots.ToArray()), null);
    }

    private static (Snapshot? Snapshot, Exception? Failure) TryCaptureFile(string path)
    {
        using var leases = new NeutralArtifactReadLeaseSet();
        if (leases.TryPin(new MediaArtifact { Path = path, Kind = MediaArtifactKind.PrimaryImage }) is { } leaseFailure) return (null, leaseFailure);
        using var file = SourceReadAttempt.Open(path);
        if (!file.Succeeded) return (null, new IOException($"Cannot read source action identity (system error {file.Error})."));
        if (!WindowsFileIdentity.TryCapture(file.Handle, out var identity, out int error))
            return (null, new IOException($"Cannot capture source action identity (system error {error})."));
        if (identity!.IsReparsePoint || identity.LinkCount != 1)
            return (null, new IOException("Source post-processing cannot authorize a reparse point or multiply linked file."));
        var hash = MediaFileHashAttempt.Compute(path, default);
        if (hash.Failure is { } failure) return (null, failure);
        return (new(Path.GetFullPath(path), identity, hash.Sha256!), null);
    }

    internal void Validate(ProductOperationOutcome outcome, IEnumerable<string> paths, string? moveDirectory = null)
    {
        if (ValidationFailure(outcome, paths, moveDirectory) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal Exception? ValidationFailure(ProductOperationOutcome outcome, IEnumerable<string> paths, string? moveDirectory = null)
    {
        if (!outcome.IsSuccess || !ReferenceEquals(outcome.SourceActionGuard, this))
            return new IOException("Source post-processing requires this operation's green completion receipt.");
        var requested = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!requested.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(sources.Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            return new IOException("Source post-processing input set differs from the completed operation.");
        foreach (var snapshot in sources.Concat(outputs))
        {
            var attempt = TryCaptureFile(snapshot.Path);
            if (attempt.Failure is { } failure) return failure;
            var current = attempt.Snapshot!;
            if (!current.Identity.Matches(snapshot.Identity) || current.Hash != snapshot.Hash)
                return new IOException("Source or output changed after completion; originals were retained: " + snapshot.Path);
        }
        if (moveDirectory is not null)
            foreach (string source in requested)
            {
                string destination = Path.Combine(Path.GetFullPath(moveDirectory), Path.GetFileName(source));
                if (File.Exists(destination) || Directory.Exists(destination))
                    return new IOException("Archive destination already exists; originals were retained: " + destination);
            }
        return null;
    }

    internal void ValidateSource(ProductOperationOutcome outcome, string path)
    {
        if (SourceValidationFailure(outcome, path) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal Exception? SourceValidationFailure(ProductOperationOutcome outcome, string path)
    {
        if (!outcome.IsSuccess || !ReferenceEquals(outcome.SourceActionGuard, this))
            return new IOException("Source post-processing requires this operation's green completion receipt.");
        string fullPath = Path.GetFullPath(path);
        var expected = sources.SingleOrDefault(s => string.Equals(s.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (expected is null) return new IOException("Source is not authorized by the completed operation.");
        var attempt = TryCaptureFile(fullPath);
        if (attempt.Failure is { } failure) return failure;
        var current = attempt.Snapshot!;
        if (!current.Identity.Matches(expected.Identity) || current.Hash != expected.Hash)
            return new IOException("Source changed before post-processing; file was retained: " + fullPath);
        return null;
    }

    internal Exception? MoveFailure(ProductOperationOutcome outcome, string path, string destination)
    {
        if (!outcome.IsSuccess || !ReferenceEquals(outcome.SourceActionGuard, this))
            return new IOException("Source post-processing requires this operation's green completion receipt.");
        string fullPath = Path.GetFullPath(path);
        var expected = sources.SingleOrDefault(s => string.Equals(s.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (expected is null) return new IOException("Source is not authorized by the completed operation.");
        using var namespaceLease = new NeutralArtifactReadLeaseSet();
        if (namespaceLease.TryPinDirectory(Path.GetDirectoryName(fullPath)) is { } sourceNamespaceFailure) return sourceNamespaceFailure;
        if (namespaceLease.TryPinDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))) is { } destinationNamespaceFailure) return destinationNamespaceFailure;
        // Retain DELETE access to this exact object while denying foreign writes
        // and replacements. The rename operates on this handle, never a re-opened path.
        using var source = SourceReadAttempt.Open(fullPath, share: 1, access: 0x80010000);
        if (!source.Succeeded) return new IOException($"Cannot retain original for archive move (system error {source.Error}); original retained.");
        if (!WindowsFileIdentity.TryCapture(source.Handle, out var identity, out int error) || !expected.Identity.Matches(identity!))
            return new IOException($"Source identity changed before archive move (system error {error}); original retained.");
        var hash = MediaFileHashAttempt.Compute(fullPath, default);
        if (hash.Failure is { } hashFailure) return hashFailure;
        if (hash.Sha256 != expected.Hash) return new IOException("Source bytes changed before archive move; original retained.");
        return WindowsSplitOutputPublisher.MoveAuthorizedSourceNoReplace(source.Handle, destination);
    }
}
