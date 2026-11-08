using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

internal sealed record MediaFileHashAttempt(string? Sha256, Exception? Failure)
{
    internal static Task<MediaFileHashAttempt> ComputeAsync(string path, CancellationToken token)
        => Task.Run(() => Compute(path,token),CancellationToken.None);

    internal static unsafe MediaFileHashAttempt ComputeHeld(SafeFileHandle source, long length, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        if (length < 0 || !GetFileSizeEx(source, out long actual) || actual != length)
            return new(null, new IOException("Retained media length does not match the requested hash extent."));
        if (!SetFilePointerEx(source, 0, out _, 0))
            return new(null, new IOException($"Cannot seek retained media (system error {Marshal.GetLastWin32Error()})."));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024]; long remaining = length;
        fixed (byte* data = buffer)
        {
            while (remaining > 0)
            {
                if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
                if (!ReadFile(source, data, (uint)Math.Min(buffer.Length, remaining), out uint read, 0) || read == 0)
                    return new(null, new IOException($"Cannot completely hash retained media (system error {Marshal.GetLastWin32Error()})."));
                hash.AppendData(buffer, 0, (int)read); remaining -= read;
            }
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), null);
    }

    internal static Task<MediaFileHashAttempt> ComputeRangeAsync(string path, long offset, long length, CancellationToken token)
        => Task.Run(() => ComputeRange(path, offset, length, token), CancellationToken.None);

    private static unsafe MediaFileHashAttempt ComputeRange(string path, long offset, long length, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        using var source = SourceReadAttempt.Open(path, 7);
        if (!source.Succeeded) return new(null, new IOException($"Cannot open media range (system error {source.Error})."));
        if (!source.TryGetLength(out long size, out int error)) return new(null, new IOException($"Cannot read media range length (system error {error})."));
        if (offset < 0 || length <= 0 || offset > size || length > size - offset)
            return new(null, new InvalidDataException("Media hash range exceeds its complete file."));
        if (!SetFilePointerEx(source.Handle, offset, out _, 0)) return new(null, new IOException($"Cannot seek media range (system error {Marshal.GetLastWin32Error()})."));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024]; long remaining = length;
        fixed (byte* data = buffer)
        {
            while (remaining > 0)
            {
                if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
                if (!ReadFile(source.Handle, data, (uint)Math.Min(buffer.Length, remaining), out uint read, 0) || read == 0)
                    return new(null, new IOException($"Cannot completely read media range (system error {Marshal.GetLastWin32Error()})."));
                hash.AppendData(buffer, 0, (int)read); remaining -= read;
            }
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), null);
    }

    internal static Task<Exception?> CopyToAsync(string path,Stream destination,CancellationToken token)
        => Task.Run(()=>CopyTo(path,destination,token),CancellationToken.None);

    internal static Task<Exception?> CopyToAsync(SafeFileHandle source, Stream destination, CancellationToken token)
        => Task.Run(() => CopyHeldFileTo(source, destination, token), CancellationToken.None);

    internal static unsafe Exception? CopyHeldFileTo(SafeFileHandle source, Stream destination, CancellationToken token, bool allowUnlinked = false, long? prefixLength = null)
    {
        if (token.IsCancellationRequested) return new OperationCanceledException(token);
        if (!LivePhotoBox.Media.Models.WindowsFileIdentity.TryCapture(source, out var identity, out int error) ||
            identity!.IsReparsePoint || (identity.LinkCount != 1 && !(allowUnlinked && identity.LinkCount == 0)) || (identity.FileAttributes & 16) != 0)
            return new IOException($"Cannot verify retained copy source (system error {error}).");
        if (!GetFileSizeEx(source, out long length) || !SetFilePointerEx(source, 0, out _, 0))
            return new IOException($"Cannot read retained copy source (system error {Marshal.GetLastWin32Error()}).");
        long copyLength = prefixLength ?? length;
        if (copyLength < 0 || copyLength > length) return new InvalidDataException("Retained copy range exceeds its complete source.");
        byte[] buffer = new byte[128 * 1024]; long offset = 0;
        fixed (byte* data = buffer)
        {
            while (offset < copyLength)
            {
                if (token.IsCancellationRequested) return new OperationCanceledException(token);
                uint count = (uint)Math.Min(buffer.Length, copyLength - offset);
                if (!ReadFile(source, data, count, out uint read, 0) || read == 0)
                    return new IOException($"Cannot completely read retained copy source (system error {Marshal.GetLastWin32Error()}).");
                if (TransactionStreamWriteAttempt.Write(destination, buffer.AsSpan(0, (int)read), token, offset) is { } failure) return failure;
                offset += read;
            }
        }
        if (!GetFileSizeEx(source, out long after) || after != length ||
            !LivePhotoBox.Media.Models.WindowsFileIdentity.TryCapture(source, out var current, out error) || !identity.Matches(current!))
            return new IOException("Retained copy source changed identity or length during reading.");
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);

    private static unsafe Exception? CopyTo(string path,Stream destination,CancellationToken token)
    {
        if(token.IsCancellationRequested)return new OperationCanceledException(token);
        using var source=SourceReadAttempt.Open(path);
        if(!source.Succeeded)return new IOException($"Cannot open media for copying (system error {source.Error}): {path}");
        if(!LivePhotoBox.Media.Models.WindowsFileIdentity.TryCapture(source.Handle,out var identity,out int error)||
            identity!.IsReparsePoint||(identity.FileAttributes&16)!=0)
            return new IOException($"Cannot verify the regular media object for copying (system error {error}).");
        byte[] buffer=new byte[128*1024];long offset=0;
        fixed(byte* data=buffer)
        {
            while(true)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                if(!ReadFile(source.Handle,data,(uint)buffer.Length,out uint count,0))
                    return new IOException($"Cannot read media for copying (system error {Marshal.GetLastWin32Error()}).");
                if(count==0)break;
                if(TransactionStreamWriteAttempt.Write(destination,buffer.AsSpan(0,(int)count),token,offset) is { } failure)return failure;
                offset+=count;
            }
        }
        return null;
    }

    internal static unsafe MediaFileHashAttempt Compute(string path,CancellationToken token)
    {
        if(token.IsCancellationRequested) return new(null,new OperationCanceledException(token));
        // Extraction retains the authoritative source/output leases. This
        // read-only hash handle shares those already-open writer handles;
        // it does not acquire write access or replace their protection.
        using var source=SourceReadAttempt.Open(path,7);
        if(!source.Succeeded) return new(null,new IOException($"Cannot open media for hashing (system error {source.Error}): {path}"));
        if(!LivePhotoBox.Media.Models.WindowsFileIdentity.TryCapture(source.Handle,out var identity,out int identityError) ||
            identity!.IsReparsePoint || (identity.FileAttributes&16)!=0)
            return new(null,new IOException($"Cannot verify the regular media object for hashing (system error {identityError}): {path}"));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=new byte[128*1024];
        fixed(byte* data=buffer)
        {
            while(true)
            {
                if(token.IsCancellationRequested) return new(null,new OperationCanceledException(token));
                if(!ReadFile(source.Handle,data,(uint)buffer.Length,out uint count,0))
                    return new(null,new IOException($"Cannot read media for hashing (system error {Marshal.GetLastWin32Error()}): {path}"));
                if(count==0) break;
                hash.AppendData(buffer,0,(int)count);
            }
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()),null);
    }

    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle file,byte* buffer,uint count,out uint read,nint overlapped);
}
