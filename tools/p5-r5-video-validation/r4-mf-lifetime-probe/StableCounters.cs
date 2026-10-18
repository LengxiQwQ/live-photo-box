using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class StableCounters
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int kind, nint data, int length, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint VirtualAlloc(nint address, nuint bytes, uint kind, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool VirtualFree(nint address, nuint bytes, uint kind);

    internal static int Semaphores()
    {
        using var probe = new Semaphore(0, 1);
        for (int size = 1048576; ;)
        {
            nint data = VirtualAlloc(0, (nuint)size, 0x3000, 4);
            if (data == 0) throw new InvalidOperationException("Counter VirtualAlloc " + Marshal.GetLastWin32Error());
            try
            {
                int status = NtQuerySystemInformation(64, data, size, out int needed);
                if (status == unchecked((int)0xC0000004))
                {
                    size = Math.Max(size * 2, needed);
                    if (size > 67108864) throw new InvalidOperationException("Counter table budget");
                    continue;
                }
                if (status != 0) throw new InvalidOperationException("Counter NtQuery " + status);
                long count = Marshal.ReadInt64(data);
                short? type = null;
                // Two scans avoid allocating an additional per-handle list.
                for (long i = 0; i < count; i++)
                {
                    nint row = data + checked(16 + (int)i * 40);
                    if (Marshal.ReadInt64(row, 8) == Environment.ProcessId &&
                        Marshal.ReadInt64(row, 16) == probe.SafeWaitHandle.DangerousGetHandle().ToInt64())
                        type = Marshal.ReadInt16(row, 30);
                }
                if (type is null) throw new InvalidOperationException("Counter owned semaphore not found");
                int result = -1;
                for (long i = 0; i < count; i++)
                {
                    nint row = data + checked(16 + (int)i * 40);
                    if (Marshal.ReadInt64(row, 8) == Environment.ProcessId && Marshal.ReadInt16(row, 30) == type) result++;
                }
                return result;
            }
            finally { if (!VirtualFree(data, 0, 0x8000)) throw new InvalidOperationException("Counter VirtualFree " + Marshal.GetLastWin32Error()); }
        }
    }

    internal static object Snapshot()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { utc = DateTime.UtcNow, privateBytes = process.PrivateMemorySize64,
            workingSet = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            handles = process.HandleCount, threads = process.Threads.Count, semaphores = Semaphores() };
    }
}
