using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

/// <summary>A prepared temporary file with creating-handle ownership evidence.</summary>
internal sealed class WindowsOwnedTemporaryArtifact : IDisposable
{
    private PublishedOwnedFile? _receipt;
    private bool _disposed;

    private WindowsOwnedTemporaryArtifact() { }

    internal string Path => _receipt!.FinalPath;
    internal WindowsFileIdentity Identity => _receipt!.FileIdentity;
    internal long ByteLength => _receipt!.ByteLength;
    internal string Sha256 => _receipt!.Sha256;

    internal static async Task<WindowsOwnedTemporaryArtifact> CreateAsync(
        string directory, string prefix, string extension,
        Func<Stream, CancellationToken, Task> writer, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(writer);
        if (prefix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            extension.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Temporary artifact name must be a filename.");

        // Allocate the owner before publishing. The receipt is created before
        // the transaction's commit barrier; registration itself cannot throw.
        var owner = new WindowsOwnedTemporaryArtifact();
        string finalPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory),
            prefix + Guid.NewGuid().ToString("N") + extension);
        owner._receipt = await WindowsSplitOutputPublisher.PublishSingleAsync(
            finalPath, false, writer, null, token).ConfigureAwait(false);
        return owner;
    }

    public void Dispose()
    {
        if (_disposed || _receipt == null)
            return;

        PublishedOwnedFile receipt = _receipt;
        try
        {
            using SafeFileHandle handle = WindowsOwnedFilePublisher.OpenOwnedStagedFile(
                receipt.FinalPath, receipt.FileIdentity);
            PublishedOwnedFile current = WindowsOwnedFilePublisher.CaptureOwnedHandleEvidence(
                handle, receipt.FinalPath, receipt.FileIdentity, CancellationToken.None);
            if (current.ByteLength != receipt.ByteLength ||
                !string.Equals(current.Sha256, receipt.Sha256, StringComparison.Ordinal))
                throw new IOException("Prepared temporary artifact content changed; refusing cleanup.");
            OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                handle, receipt.FileIdentity, receipt.FinalPath, out _);
            if (proof != OwnedObjectCleanupProof.Gone && proof != OwnedObjectCleanupProof.Unlinked)
                throw new IOException($"Exact-object cleanup proof is {proof}.");
            _disposed = true;
        }
        catch (Exception failure)
        {
            // Missing or replaced path is not proof that the owned object is
            // gone. Query that exact file ID, never delete a replacement.
            if (WindowsOwnedFilePublisher.IsObjectAliveByFileId(receipt.FileIdentity,
                    receipt.FinalPath) == ObjectAliveCheck.NotAlive)
            {
                _disposed = true;
                return;
            }
            throw new IOException(
                $"[CleanupFailed] Temporary artifact '{receipt.FinalPath}' " +
                $"(volume={receipt.FileIdentity.VolumeSerialNumber}, fileId={receipt.FileIdentity.FileIndex}) " +
                "could not be proven removed; foreign objects were preserved.", failure);
        }
    }
}
