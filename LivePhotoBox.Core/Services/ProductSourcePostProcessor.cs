using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

internal static class ProductSourcePostProcessor
{
    internal static async Task<ProductSourcePostProcessing> ProcessAsync(ProductOperationOutcome outcome,
        IEnumerable<string> inputs, bool move, string? directory, CancellationToken token,
        Action<string, string>? onCompleted = null)
    {
        string action = move ? "move" : "recycle";
        var completed = new List<string>();
        ProductSourcePostProcessing Refused(Exception error)
        {
            string reason = error.Message;
            if (completed.Count > 0) reason += Environment.NewLine + "Already processed: " + string.Join(", ", completed);
            return new(action, error is OperationCanceledException ? "cancelled" : "failed", reason, directory);
        }
        if (token.IsCancellationRequested) return Refused(new OperationCanceledException(token));
        if (move && string.IsNullOrWhiteSpace(directory)) return Refused(new IOException("Archive directory is missing; originals retained."));
        var guard = outcome.SourceActionGuard;
        if (guard is null) return Refused(new IOException("Source action identity receipt is unavailable; originals retained."));
        string[] paths = inputs.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var outputs = new SourceInputProtection(outcome.CommittedOutputs.Select(o => o.Path));
        if (!outputs.IsComplete) return Refused(new IOException(outputs.Diagnostic ?? "Output protection failed; originals retained."));
        if (guard.ValidationFailure(outcome, paths, directory) is { } validationFailure) return Refused(validationFailure);
        if (directory is not null && OutputDirectoryAttempt.Ensure(directory) is { } directoryFailure) return Refused(directoryFailure);
        var creation = WindowsSplitOutputPublisher.SourceActionJournal.TryCreate(paths, directory);
        if (creation.Failure is { } journalFailure) return Refused(journalFailure);
        using var journal = creation.Journal!;
        foreach (string path in paths)
        {
            if (token.IsCancellationRequested) return Refused(new OperationCanceledException(token));
            Exception? failure = directory is not null
                ? guard.MoveFailure(outcome, path, Path.Combine(directory, Path.GetFileName(path)))
                : await Task.Run(() => SourceRecycleBinService.TryRecycle(outcome, path), CancellationToken.None).ConfigureAwait(false);
            if (failure is not null) return Refused(failure);
            string destination = directory is not null ? Path.Combine(directory, Path.GetFileName(path)) : "recycle bin";
            completed.Add(path + " -> " + destination);
            onCompleted?.Invoke(path, directory is not null ? "moved" : "recycled");
        }
        journal.Complete();
        journal.Dispose();
        if (journal.CleanupFailure is { } cleanupFailure) return Refused(cleanupFailure);
        return new(action, "completed", "", directory);
    }
}
