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

    internal static async Task PublishCopyAsync(MediaArtifact artifact,string finalPath,bool overwriteExisting,CancellationToken token,
        Func<SplitPublicationTraceEvent,CancellationToken,Task>? testHook=null,Action<WindowsOutputArtifactCopySource>? sourceCaptured=null)
    {
        var attempt=await TryPublishCopyAsync(artifact,finalPath,overwriteExisting,token,testHook,sourceCaptured).ConfigureAwait(false);
        if(attempt.Failure is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task<WindowsSplitOutputPublisher.PublicationAttempt> TryPublishCopyAsync(MediaArtifact artifact,
        string finalPath,bool overwriteExisting,CancellationToken token,Func<SplitPublicationTraceEvent,CancellationToken,Task>? testHook=null,
        Action<WindowsOutputArtifactCopySource>? sourceCaptured=null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        string sourcePath=Path.GetFullPath(artifact.Path),final=Path.GetFullPath(finalPath);
        if(string.Equals(sourcePath,final,StringComparison.OrdinalIgnoreCase))return new(null,new IOException("A source artifact cannot also be the final publication path."));
        using var source=SourceReadAttempt.Open(sourcePath);
        if(!source.Succeeded)return new(null,new IOException($"Cannot read artifact for publication (system error {source.Error})."));
        if(!WindowsFileIdentity.TryCapture(source.Handle,out WindowsFileIdentity? identity,out int error) ||
            identity!.IsReparsePoint||(identity.FileAttributes&(uint)FileAttributes.Directory)!=0||identity.LinkCount==0||
            (artifact.FileIdentity is not null&&!identity.Matches(artifact.FileIdentity)))
            return new(null,new IOException($"Source artifact is not the expected regular filesystem object (system error {error})."));
        long length=RandomAccess.GetLength(source.Handle);
        var hash=await MediaFileHashAttempt.ComputeAsync(sourcePath,token).ConfigureAwait(false);
        if(hash.Failure is { } hashFailure)return new(null,hashFailure);
        if((artifact.ByteLength>0&&artifact.ByteLength!=length)||(artifact.Sha256 is not null&&
            !string.Equals(artifact.Sha256,hash.Sha256,StringComparison.OrdinalIgnoreCase)))
            return new(null,new IOException("Source artifact does not match its recorded length or SHA-256."));
        sourceCaptured?.Invoke(new WindowsOutputArtifactCopySource(sourcePath,identity,length,hash.Sha256!));
        return await WindowsSplitOutputPublisher.TryPublishSingleAsync(final,overwriteExisting,async(stage,ct)=>
        {
            if(await MediaFileHashAttempt.CopyToAsync(sourcePath,stage,ct).ConfigureAwait(false) is { } copyFailure)return copyFailure;
            if(!WindowsFileIdentity.TryCapture(source.Handle,out var after,out int identityError)||!identity.Matches(after!))
                return new IOException($"Source artifact changed its identity (system error {identityError}).");
            var afterHash=await MediaFileHashAttempt.ComputeAsync(sourcePath,ct).ConfigureAwait(false);
            if(afterHash.Failure is { } afterFailure)return afterFailure;
            if(RandomAccess.GetLength(source.Handle)!=length||!string.Equals(hash.Sha256,afterHash.Sha256,StringComparison.OrdinalIgnoreCase))
                return new IOException("Source artifact changed content while its staged copy was written.");
            return null;
        },testHook,token).ConfigureAwait(false);
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

    private static SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile) =>
        NativeCreateFileW(LivePhotoBox.Interop.WindowsNativePath.Extended(fileName), desiredAccess, shareMode,
            securityAttributes, creationDisposition, flagsAndAttributes, templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFileW(
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
