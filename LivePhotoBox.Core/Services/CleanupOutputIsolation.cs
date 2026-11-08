using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Services;

// Relocates only outputs whose exact creating-object receipts were captured by
// this operation. Never acquires ownership by hashing an arbitrary pathname.
internal static class CleanupOutputIsolation
{
    internal static AsyncLocal<Action<int, string>?> BeforeMoveForTests { get; } = new();
    internal sealed record IsolationAttempt(IReadOnlyList<ProductCommittedArtifact> Outputs, string? Directory, Exception? Failure);
    internal static Task<IsolationAttempt> TryIsolateAsync(IReadOnlyList<ProductCommittedArtifact> outputs,
        IReadOnlyList<string> inputs, string outputDirectory, CancellationToken token)
        => Task.Run(() => Isolate(outputs, inputs, outputDirectory, token), CancellationToken.None);

    private static IsolationAttempt Isolate(IReadOnlyList<ProductCommittedArtifact> outputs,
        IReadOnlyList<string> inputs, string outputDirectory, CancellationToken token)
    {
        if (outputs.Count == 0) return new(outputs, null, null);
        var receipts = outputs.Select(output => WorkspaceCleanupScope.FindPublished(output.Path)).ToArray();
        if (receipts.Any(receipt => receipt is null))
            return new(outputs, null, new IOException("隔离迁移未执行：缺少本次发布的精确文件对象凭据；已保留现有输出。"));
        foreach (string input in inputs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var source = SourceReadAttempt.Open(input, 7);
            if (!source.Succeeded || !WindowsFileIdentity.TryCapture(source.Handle, out var identity, out _))
                return new(outputs, null, new IOException("隔离迁移无法核验来源保护；已保留现有输出。"));
            if (receipts.Any(receipt => receipt!.FileIdentity.VolumeSerialNumber == identity!.VolumeSerialNumber &&
                receipt.FileIdentity.FileIndex == identity.FileIndex))
                return new(outputs, null, new IOException("隔离迁移不能移动用户原件或其别名。"));
        }
        if (token.IsCancellationRequested) return new(outputs, null, new OperationCanceledException(token));
        string directory = Path.Combine(ProductExportAssessment.IsolationDirectory(outputDirectory, Guid.NewGuid()),
            "cleanup-" + Guid.NewGuid().ToString("N"));
        if (OutputDirectoryAttempt.Ensure(directory) is { } directoryFailure) return new(outputs, null, directoryFailure);
        using var namespaces = new NeutralArtifactReadLeaseSet();
        if (namespaces.TryPinDirectory(directory) is { } namespaceFailure) return new(outputs, null, namespaceFailure);
        foreach (var output in outputs)
            if (namespaces.TryPinDirectory(Path.GetDirectoryName(Path.GetFullPath(output.Path))) is { } parentFailure)
                return new(outputs, null, parentFailure);
        var journalCreation = WindowsSplitOutputPublisher.SourceActionJournal.TryCreate(outputs.Select(x => x.Path).ToArray(), directory);
        if (journalCreation.Failure is { } journalFailure) return new(outputs, null, journalFailure);
        using var journal = journalCreation.Journal!;
        var handles = new List<SourceReadAttempt>();
        string[] locations = outputs.Select(x => x.Path).ToArray();
        bool settled = true;
        var result = Move();
        journal.Dispose();
        if (result.Failure is not null)
        {
            var confirmed = new List<ProductCommittedArtifact>();
            for (int i = 0; i < locations.Length; i++)
            {
                using var retained = SourceReadAttempt.Open(locations[i], 7);
                var expected = receipts[i]!;
                if (!retained.Succeeded || !WindowsFileIdentity.TryCapture(retained.Handle, out var actual, out _) ||
                    !expected.FileIdentity.Matches(actual!) || !retained.TryGetLength(out long length, out _) || length != expected.ByteLength) continue;
                var hash = MediaFileHashAttempt.ComputeHeld(retained.Handle, length, CancellationToken.None);
                if (hash.Failure is null && string.Equals(hash.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    confirmed.Add(outputs[i] with { Path = locations[i] });
            }
            result = result with { Outputs = confirmed };
            if (confirmed.Count != outputs.Count)
                result = result with { Failure = new IOException("部分原发布位置的文件身份已变化或无法核验，未将其计为已确认输出；外来文件未移动。", result.Failure) };
        }
        return journal.CleanupFailure is { } journalCleanupFailure
            ? result with { Failure = new IOException("隔离迁移记录未能清理；成品实际位置已保留。", journalCleanupFailure) }
            : result;

        IsolationAttempt Move()
        {
        try
        {
            for (int i = 0; i < outputs.Count; i++)
            {
                var opened = SourceReadAttempt.Open(outputs[i].Path, 5, 0x80010080);
                handles.Add(opened);
                var expected = receipts[i]!;
                if (!opened.Succeeded || !WindowsFileIdentity.TryCapture(opened.Handle, out var actual, out _) ||
                    !expected.FileIdentity.Matches(actual!) || actual!.IsReparsePoint || actual.LinkCount != 1 ||
                    !opened.TryGetLength(out long length, out _) || length != expected.ByteLength)
                    return new(outputs, null, new IOException("隔离迁移前输出对象或长度已变化；未移动现有文件。"));
                var hash = MediaFileHashAttempt.ComputeHeld(opened.Handle, length, token);
                if (hash.Failure is { } hashFailure) return new(outputs, null, hashFailure);
                if (!string.Equals(hash.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(outputs, null, new IOException("隔离迁移前输出内容已变化；未移动现有文件。"));
            }
            Exception? failure = null;
            int moved = 0;
            for (; moved < outputs.Count; moved++)
            {
                string destination = Path.Combine(directory, Path.GetFileName(outputs[moved].Path));
                BeforeMoveForTests.Value?.Invoke(moved, destination);
                failure = token.IsCancellationRequested ? new OperationCanceledException(token)
                    : WindowsSplitOutputPublisher.MoveAuthorizedSourceNoReplace(handles[moved].Handle, destination);
                if (failure is not null) break;
                locations[moved] = destination;
            }
            if (failure is null)
            {
                var relocated = outputs.Select((output, i) => output with { Path = locations[i] }).ToArray();
                foreach (var receipt in receipts.Select((receipt, i) => receipt! with { FinalPath = locations[i] }))
                    WorkspaceCleanupScope.RegisterPublished(receipt);
                return new(relocated, directory, null);
            }
            for (int i = moved - 1; i >= 0; i--)
            {
                var rollback = WindowsSplitOutputPublisher.MoveAuthorizedSourceNoReplace(handles[i].Handle, outputs[i].Path);
                if (rollback is null) locations[i] = outputs[i].Path;
                else settled = false;
            }
            var actualOutputs = outputs.Select((output, i) => output with { Path = locations[i] }).ToArray();
            return new(actualOutputs, null, settled ? failure : new IOException(
                "隔离迁移中断，部分成品未能回到原位置；实际位置已逐项记录，迁移日志保留。", failure));
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
            if (settled) journal.Complete();
        }
        }
    }
}
