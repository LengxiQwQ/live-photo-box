using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

internal sealed record WindowsOutputArtifactCopySource(
    string Path,
    WindowsFileIdentity Identity,
    long ByteLength,
    string Sha256);

/// <summary>
/// Publishes a copy of an already-produced artifact through the split-output
/// transaction's single-target path. The source stays open read-only from
/// identity/hash capture through the copy and is never renamed, overwritten,
/// or deleted by this helper.
/// </summary>
internal static class WindowsOutputArtifactPublisher
{
    private const int BufferSize = 128 * 1024;

    internal static async Task PublishCopyAsync(
        MediaArtifact artifact,
        string finalPath,
        bool overwriteExisting,
        CancellationToken token,
        Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook = null,
        Action<WindowsOutputArtifactCopySource>? sourceCaptured = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        string sourcePath = Path.GetFullPath(artifact.Path);
        string normalizedFinalPath = Path.GetFullPath(finalPath);
        if (string.Equals(sourcePath, normalizedFinalPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A source artifact cannot also be the final publication path.");

        using SafeFileHandle sourceHandle = OpenReadOnlyNoWriteShare(sourcePath);
        await using var source = new FileStream(sourceHandle, FileAccess.Read, BufferSize, isAsync: false);

        WindowsFileIdentity sourceIdentity = WindowsFileIdentity.Capture(source.SafeFileHandle);
        if (sourceIdentity.IsReparsePoint ||
            (sourceIdentity.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            sourceIdentity.LinkCount == 0 ||
            (artifact.FileIdentity != null && !sourceIdentity.Matches(artifact.FileIdentity)))
        {
            throw new IOException(
                $"Source artifact '{sourcePath}' is not the expected regular filesystem object; refusing to publish it.");
        }

        long sourceLength = source.Length;
        string sourceSha256 = await ComputeSha256Async(source, token).ConfigureAwait(false);
        if ((artifact.ByteLength > 0 && artifact.ByteLength != sourceLength) ||
            (artifact.Sha256 != null &&
             !string.Equals(artifact.Sha256, sourceSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException(
                $"Source artifact '{sourcePath}' does not match its recorded length or SHA-256.");
        }

        var sourceEvidence = new WindowsOutputArtifactCopySource(
            sourcePath,
            sourceIdentity,
            sourceLength,
            sourceSha256);
        sourceCaptured?.Invoke(sourceEvidence);

        await WindowsSplitOutputPublisher.PublishSingleAsync(
            normalizedFinalPath,
            overwriteExisting,
            async (stage, cancellationToken) =>
            {
                source.Position = 0;
                await source.CopyToAsync(stage, BufferSize, cancellationToken).ConfigureAwait(false);

                WindowsFileIdentity afterCopyIdentity = WindowsFileIdentity.Capture(source.SafeFileHandle);
                long afterCopyLength = source.Length;
                string afterCopySha256 = await ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
                if (!sourceIdentity.Matches(afterCopyIdentity) ||
                    sourceLength != afterCopyLength ||
                    !string.Equals(sourceSha256, afterCopySha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        $"Source artifact '{sourcePath}' changed identity or content while its staged copy was written.");
                }
            },
            testHook,
            token).ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(FileStream stream, CancellationToken token)
    {
        stream.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        stream.Position = 0;
        return Convert.ToHexString(hash);
    }

    private static SafeFileHandle OpenReadOnlyNoWriteShare(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint | SequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Unable to open converter source artifact '{path}' as a held read-only, non-reparse object.",
                new Win32Exception(error));
        }
        return handle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint SequentialScan = 0x08000000;
}
