using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

internal static class TransactionStreamWriteAttempt
{
    internal static (FileStream? Stream, Exception? Failure) TryCreate(string path)
    {
        var handle = CreateFileW(path, 0xC0000000, 1, 0, 1, 0x00200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            return (null, new IOException($"Cannot create owned temporary output (system error {error}): {path}"));
        }
        return (new FileStream(handle, FileAccess.ReadWrite), null);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    // Only publisher-owned, synchronous file streams enter this transport.
    // WriteFile reports disk-full/access/device errors without first throwing.
    internal static unsafe Exception? Write(Stream stream,ReadOnlySpan<byte> bytes,CancellationToken token,long fileOffset=0)
    {
        if(stream is not FileStream file)return new InvalidOperationException("Transaction transport requires its owned file stream.");
        var handle=file.SafeFileHandle;
        // Exposing a FileStream handle can resynchronize its cached position.
        // Every segment therefore supplies its authoritative byte coordinate.
        if(fileOffset<0||!SetFilePointerEx(handle,fileOffset,out _,0))
            return new IOException($"Cannot position transaction output (system error {Marshal.GetLastWin32Error()}).");
        fixed(byte* data=bytes)
        {
            int offset=0;
            while(offset<bytes.Length)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                if(!WriteFile(handle,data+offset,(uint)(bytes.Length-offset),out uint written,0))
                {
                    int error=Marshal.GetLastWin32Error();
                    return new IOException($"Cannot write transaction output (system error {error}).",new Win32Exception(error));
                }
                if(written==0)return new IOException("Transaction output accepted no bytes.");
                offset+=(int)written;
            }
        }
        return null;
    }

    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool WriteFile(SafeFileHandle file,byte* data,uint count,out uint written,nint overlapped);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file,long distance,out long position,uint method);
}
