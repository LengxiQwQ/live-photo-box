using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Workspace;

/// <summary>
/// Routes exact producer receipts to the live workspace which owns their
/// destination directory.  This registry never captures ownership from a
/// pathname: callers must provide an identity issued by the creating handle,
/// Native's transaction registry, or a managed publication transaction.
/// </summary>
internal static class WorkspaceOwnershipRegistry
{
    private const uint FileAttributeDirectory = 0x00000010;

    private static readonly ConcurrentDictionary<string, WeakReference<MediaWorkspace>> LiveWorkspaces =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void RegisterWorkspace(MediaWorkspace workspace, string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string root = NormalizePath(rootDirectory);
        var reference = new WeakReference<MediaWorkspace>(workspace);
        if (!LiveWorkspaces.TryAdd(root, reference))
            throw new IOException($"A media workspace is already registered at '{root}'.");
    }

    internal static void UnregisterWorkspace(MediaWorkspace workspace, string rootDirectory)
    {
        string root = NormalizePath(rootDirectory);
        if (LiveWorkspaces.TryGetValue(root, out WeakReference<MediaWorkspace>? reference) &&
            reference.TryGetTarget(out MediaWorkspace? current) &&
            ReferenceEquals(current, workspace))
        {
            LiveWorkspaces.TryRemove(root, out _);
        }
    }

    /// <summary>
    /// Registers a just-created regular file.  The caller must supply the
    /// identity captured from the producer's retained handle or a Native
    /// transaction receipt; this method does not reopen <paramref name="path"/>.
    /// </summary>
    internal static bool RegisterCreatedFile(
        string path,
        WindowsFileIdentity identity,
        long byteLength,
        string? sha256,
        string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        return TryGetWorkspace(path, out MediaWorkspace? workspace) &&
            workspace.RegisterCreatedFile(path, identity, byteLength, sha256, role);
    }

    /// <summary>
    /// Registers evidence returned by an exact managed publication.  A
    /// replacement publication may update a receipt only when the same exact
    /// object was already registered by this workspace.
    /// </summary>
    internal static bool RegisterPublishedFile(PublishedOwnedFile receipt, bool newlyCreatedObject)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.FinalPath);

        return TryGetWorkspace(receipt.FinalPath, out MediaWorkspace? workspace) &&
            workspace.RegisterPublishedFile(receipt, newlyCreatedObject);
    }

    /// <summary>
    /// Refuses mutation of a pre-existing target inside a live MediaWorkspace
    /// unless the workspace already owns that exact file identity.  The
    /// mutation identity must come from the transaction's retained handle;
    /// this method never captures identity from the pathname.
    /// </summary>
    internal static void ValidateExistingFileMutation(
        string path,
        WindowsFileIdentity mutationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(mutationIdentity);

        if (!TryGetWorkspace(path, out MediaWorkspace? workspace))
            return;

        MediaWorkspace owner = workspace
            ?? throw new IOException($"A live workspace routing entry for '{NormalizePath(path)}' has no owner.");

        if (!owner.HasRegisteredFile(path, mutationIdentity))
        {
            string normalizedPath = NormalizePath(path);
            throw new IOException(
                $"Refusing to claim existing workspace target '{normalizedPath}' without a previously registered exact object identity.");
        }
    }

    /// <summary>
    /// Retires a previously registered artifact only after the producer's
    /// exact-object transaction proves that the same object is no longer
    /// linked.  Alias or unknown cleanup outcomes remain registered and fail
    /// closed.
    /// </summary>
    internal static bool RetireFile(
        string path,
        WindowsFileIdentity expectedIdentity,
        OwnedObjectCleanupProof proof)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedIdentity);

        return TryGetWorkspace(path, out MediaWorkspace? workspace) &&
            workspace.RetireFile(path, expectedIdentity, proof);
    }

    /// <summary>
    /// Retires receipts for an exact file object after the lower-level
    /// filesystem transaction proves that object Gone or Unlinked.  The
    /// object may have been deleted while it was still under a staging path,
    /// even though its receipt already names the intended final path, so
    /// retirement joins only on volume serial and FileId.  Unproven outcomes
    /// never change workspace ownership state.
    /// </summary>
    internal static void NotifyExactObjectCleanup(
        WindowsFileIdentity identity,
        OwnedObjectCleanupProof proof)
    {
        if (identity is null ||
            proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked) ||
            (identity.FileAttributes & FileAttributeDirectory) != 0)
        {
            return;
        }

        // This is post-disposition bookkeeping: it must never turn a proven
        // delete into a reported transaction failure.  All matching is by the
        // exact same-volume object identity; no path is reopened or recaptured.
        try
        {
            foreach (KeyValuePair<string, WeakReference<MediaWorkspace>> entry in LiveWorkspaces)
            {
                if (!entry.Value.TryGetTarget(out MediaWorkspace? workspace))
                    continue;

                try
                {
                    workspace.RetireFileByIdentity(identity, proof);
                }
                catch
                {
                    // Keep deletion proof authoritative and fail closed at
                    // later workspace cleanup if bookkeeping could not finish.
                }
            }
        }
        catch
        {
            // Concurrent registry enumeration is best effort and non-throwing
            // after an exact-object cleanup proof has already been obtained.
        }
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                 path.Length >= 7 && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\')
            path = path[4..];

        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool TryGetWorkspace(string artifactPath, out MediaWorkspace? workspace)
    {
        workspace = null;
        string normalizedArtifactPath = NormalizePath(artifactPath);
        string? parent = Path.GetDirectoryName(normalizedArtifactPath);
        if (string.IsNullOrEmpty(parent)) return false;

        string normalizedParent = NormalizePath(parent);
        if (TryGetLiveWorkspace(normalizedParent, out workspace)) return true;

        // The production cleanup protocol intentionally registers only
        // direct-child regular files.  Still route nested paths to their
        // workspace so the workspace can reject them explicitly instead of
        // silently treating them as outside-owned artifacts.
        foreach (KeyValuePair<string, WeakReference<MediaWorkspace>> entry in LiveWorkspaces)
        {
            string root = entry.Key;
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!normalizedArtifactPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Value.TryGetTarget(out workspace)) return true;
            LiveWorkspaces.TryRemove(root, out _);
        }

        return false;
    }

    private static bool TryGetLiveWorkspace(string root, out MediaWorkspace? workspace)
    {
        workspace = null;
        if (!LiveWorkspaces.TryGetValue(root, out WeakReference<MediaWorkspace>? reference))
            return false;
        if (reference.TryGetTarget(out workspace)) return true;

        LiveWorkspaces.TryRemove(root, out _);
        return false;
    }
}
