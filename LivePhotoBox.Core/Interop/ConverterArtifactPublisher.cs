using System;
using System.IO;
using System.Threading;
using LivePhotoBox.Media.Models;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

/// <summary>Internal adapter for publishing a canonical converter receipt to a caller name.</summary>
internal static class ConverterArtifactPublisher
{
    internal static void PublishNoReplace(MediaArtifact artifact, string callerOutputPath,
        CancellationToken cancellationToken = default, Action? directoryPinned = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerOutputPath);

        string stagedFullPath = Path.GetFullPath(artifact.Path);
        string outputFullPath = Path.GetFullPath(callerOutputPath);
        if (string.Equals(stagedFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Converter staging and final publication paths must differ.");
        }

        string expectedPrefix = artifact.Kind switch
        {
            MediaArtifactKind.PrimaryImage => "img-conv-",
            MediaArtifactKind.MotionVideo => "vid-conv-",
            _ => throw new IOException("Only canonical image/video converter artifacts may be published here.")
        };
        string stagedName = Path.GetFileName(stagedFullPath);
        if (!stagedName.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(stagedFullPath), Path.GetDirectoryName(outputFullPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Native converter output was not a converter-owned sibling artifact; refusing caller-path publication.");
        }

        WindowsFileIdentity expectedIdentity = artifact.FileIdentity
            ?? throw new IOException("Converter output artifact does not carry a verified WindowsFileIdentity.");

        using SafeFileHandle handle = WindowsOwnedFilePublisher.OpenOwnedStagedFile(stagedFullPath, expectedIdentity);
        try
        {
            using IDisposable directoryLease = WindowsOwnedFilePublisher.PinDirectoryAncestors(
                Path.GetDirectoryName(outputFullPath)!);
            // The directory may have moved before its pins were acquired.
            // Revalidate the staged name inside the now-stable namespace.
            using (WindowsOwnedFilePublisher.OpenOwnedStagedFile(stagedFullPath, expectedIdentity)) { }
            directoryPinned?.Invoke();
            PublishedOwnedFile current = WindowsOwnedFilePublisher.CaptureOwnedHandleEvidence(
                handle, stagedFullPath, expectedIdentity, cancellationToken);
            if (artifact.ByteLength != current.ByteLength ||
                string.IsNullOrWhiteSpace(artifact.Sha256) ||
                !string.Equals(artifact.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Converter output no longer matches its validated length and SHA-256; refusing publication.");
            }
            WindowsOwnedFilePublisher.PublishOwnedHandle(
                handle, outputFullPath, expectedIdentity, cancellationToken);
        }
        catch (Exception publishException)
        {
            Exception? cleanupException = null;
            try
            {
                OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                    handle, expectedIdentity, stagedFullPath, out _);
                if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                {
                    cleanupException = new IOException($"Converter-owned staging artifact could not be proven cleaned up ({proof}).");
                }
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }

            if (cleanupException is not null)
            {
                throw new IOException(
                    "No-overwrite output publication failed and the converter-owned staging artifact could not be removed.",
                    new AggregateException(publishException, cleanupException));
            }
            if (publishException is OperationCanceledException) throw;
            throw new IOException("No-overwrite output publication failed; the caller destination was left untouched.", publishException);
        }
    }
}
