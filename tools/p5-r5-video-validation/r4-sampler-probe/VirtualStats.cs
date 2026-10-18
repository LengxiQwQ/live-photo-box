using System.Runtime.InteropServices;

internal static class VirtualStats
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Region
    {
        public nuint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nuint address, out Region region, nuint size);

    internal static object ReadAll()
    {
        ulong privateCommit = 0, imageCommit = 0, mappedCommit = 0, privateReserve = 0;
        ulong largestPrivateCommit = 0, privateCommitRegions = 0;
        nuint address = 0;
        while (VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<Region>()) != 0)
        {
            if (region.Type == 0x20000 && region.State == 0x1000)
            {
                privateCommit += region.RegionSize;
                largestPrivateCommit = Math.Max(largestPrivateCommit, region.RegionSize);
                privateCommitRegions++;
            }
            if (region.Type == 0x20000 && region.State == 0x2000) privateReserve += region.RegionSize;
            if (region.Type == 0x1000000 && region.State == 0x1000) imageCommit += region.RegionSize;
            if (region.Type == 0x40000 && region.State == 0x1000) mappedCommit += region.RegionSize;
            nuint next = checked(region.BaseAddress + region.RegionSize);
            if (next <= address) throw new InvalidOperationException("Invalid virtual region progression.");
            address = next;
        }
        int error = Marshal.GetLastWin32Error();
        if (error != 87) throw new InvalidOperationException("Incomplete VirtualQuery walk: " + error);
        return new { privateCommit, imageCommit, mappedCommit, privateReserve, largestPrivateCommit,
                     privateCommitRegions, complete = true };
    }
}
