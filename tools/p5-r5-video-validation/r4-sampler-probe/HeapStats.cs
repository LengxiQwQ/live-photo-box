using System.Runtime.InteropServices;

internal static class HeapStats
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Entry
    {
        [FieldOffset(0)] public nint Data;
        [FieldOffset(8)] public uint Bytes;
        [FieldOffset(14)] public ushort Flags;
        [FieldOffset(16)] public uint Committed;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessHeaps(uint size, [Out] nint[] heaps);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HeapLock(nint heap);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HeapUnlock(nint heap);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool HeapWalk(nint heap, ref Entry entry);

    internal static object ReadAll()
    {
        var heaps = new nint[GetProcessHeaps(0, Array.Empty<nint>()) + 16];
        uint count = GetProcessHeaps((uint)heaps.Length, heaps);
        if (count > heaps.Length) throw new InvalidOperationException("Heap list changed; no partial diagnostic.");
        var result = new List<object>();
        for (int i = 0; i < count; i++)
        {
            nint heap = heaps[i];
            if (!HeapLock(heap)) throw new InvalidOperationException("HeapLock failed: " + Marshal.GetLastWin32Error());
            ulong busy = 0, free = 0, committed = 0, blocks = 0;
            int error;
            try
            {
                Entry entry = default;
                while (HeapWalk(heap, ref entry))
                {
                    if ((entry.Flags & 4) != 0) { busy += entry.Bytes; blocks++; }
                    else if ((entry.Flags & 1) != 0) committed += entry.Committed;
                    else if ((entry.Flags & 2) == 0) free += entry.Bytes;
                }
                error = Marshal.GetLastWin32Error();
            }
            finally { if (!HeapUnlock(heap)) throw new InvalidOperationException("HeapUnlock failed."); }
            result.Add(new { heap = heap.ToString("X"), busy, free, committed, blocks, error,
                             complete = error == 259 });
        }
        return result;
    }
}
