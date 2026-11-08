using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using System.IO;

namespace LivePhotoBox.Media.Workspace;

// The operation consumes cleanup diagnostics only after all of its owned read
// leases have left scope. Nested helpers also report to their enclosing task.
internal sealed class WorkspaceCleanupScope : IDisposable
{
    private static readonly AsyncLocal<WorkspaceCleanupScope?> Current = new();
    private readonly WorkspaceCleanupScope? parent;
    private readonly List<MediaWorkspace> workspaces = [];
    private readonly Dictionary<string, PublishedOwnedFile> publications = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    private WorkspaceCleanupScope() { parent = Current.Value; Current.Value = this; }
    internal static WorkspaceCleanupScope Begin() => new();
    internal static void Register(MediaWorkspace workspace)
    {
        for (var scope = Current.Value; scope is not null; scope = scope.parent)
            lock (scope.workspaces) scope.workspaces.Add(workspace);
    }

    internal static void RegisterPublished(PublishedOwnedFile receipt)
    {
        for (var scope = Current.Value; scope is not null; scope = scope.parent)
            lock (scope.publications)
            {
                foreach (string previous in scope.publications.Where(pair =>
                    pair.Value.FileIdentity.VolumeSerialNumber == receipt.FileIdentity.VolumeSerialNumber &&
                    pair.Value.FileIdentity.FileIndex == receipt.FileIdentity.FileIndex).Select(pair => pair.Key).ToArray())
                    scope.publications.Remove(previous);
                scope.publications[Path.GetFullPath(receipt.FinalPath)] = receipt;
            }
    }

    internal static PublishedOwnedFile? FindPublished(string path)
    {
        for (var scope = Current.Value; scope is not null; scope = scope.parent)
            lock (scope.publications)
                if (scope.publications.TryGetValue(Path.GetFullPath(path), out var receipt)) return receipt;
        return null;
    }

    internal IReadOnlyList<ProductCommittedArtifact> PublishedOutputs
    {
        get
        {
            MediaWorkspace[] roots; PublishedOwnedFile[] receipts;
            lock (workspaces) roots = workspaces.ToArray();
            lock (publications) receipts = publications.Values.ToArray();
            return receipts.Where(receipt => !roots.Any(workspace => receipt.FinalPath.StartsWith(
                    workspace.RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .Select(receipt => new ProductCommittedArtifact { Role = "ExportedMedia", Path = receipt.FinalPath,
                    Sha256 = receipt.Sha256, ByteLength = receipt.ByteLength }).ToArray();
        }
    }

    internal IReadOnlyList<string> Failures
    {
        get
        {
            MediaWorkspace[] snapshot;
            lock (workspaces) snapshot = workspaces.ToArray();
            return snapshot.Select(workspace => (workspace.RootDirectory, Attempt: workspace.LastCleanupAttempt))
                .Where(item => item.Attempt is { Succeeded: false })
                .Select(item => $"{item.Attempt!.LimitationMessage ?? item.Attempt.Failure?.Message} 保留的临时目录：{item.RootDirectory}")
                .Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Current.Value = parent;
    }
}
