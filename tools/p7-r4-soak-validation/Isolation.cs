using System.Runtime.InteropServices;

internal static partial class Program
{
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, nint data, int length, out int needed);

    private static int SemaphoreCount()
    {
        // A temporary owned semaphore identifies the kernel type index without
        // guessing undocumented type numbers or inspecting another process.
        using var probe = new Semaphore(0, 1);
        int size = 1048576;
        while (true)
        {
            nint data = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(64, data, size, out int needed);
                if (status == unchecked((int)0xC0000004))
                {
                    size = Math.Max(size * 2, needed);
                    Require(size <= 67108864, "Diagnostic handle table budget.");
                    continue;
                }
                Require(status == 0, "Diagnostic handle query failed.");
                long count = Marshal.ReadInt64(data);
                var indices = new List<short>(); short? semaphoreType = null;
                for (long i = 0; i < count; i++)
                {
                    nint row = data + checked(16 + (int)i * 40);
                    if (Marshal.ReadInt64(row, 8) != Environment.ProcessId) continue;
                    short type = Marshal.ReadInt16(row, 30); indices.Add(type);
                    if (Marshal.ReadInt64(row, 16) == probe.SafeWaitHandle.DangerousGetHandle().ToInt64()) semaphoreType = type;
                }
                Require(semaphoreType is not null, "Owned semaphore type not found.");
                return indices.Count(t => t == semaphoreType) - 1;
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }

    private static async Task<int> Isolate(string[] args)
    {
        string samples = Path.GetFullPath(args[1]), destination = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(destination);
        using var log = new StreamWriter(new FileStream(Path.Combine(destination, "stages.jsonl"), FileMode.CreateNew));
        int round = -1;
        void Observe(string stage)
        {
            log.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { round, stage, semaphores = SemaphoreCount(), resources = Snapshot() }));
            log.Flush(); Console.WriteLine($"round={round} stage={stage}");
        }
        StageObserver = Observe;
        try
        {
            Observe("initial");
            for (round = 0; round < 8; round++)
            {
                for (int route = 0; route < Routes.Length; route++)
                {
                    await NeutralRoute(samples, route, null); Observe("neutral-" + route);
                }
                await MergeBatch(samples, Path.Combine(destination, "merge-" + round)); Observe("merge");
                await SplitBatch(samples, Path.Combine(destination, "split-" + round)); Observe("split");
                await CodecRound(samples, null); Observe("codecs-disposed");
            }
            Save(Path.Combine(destination, "isolation.json"), new { passed = true, rounds = 8 });
            return 0;
        }
        finally { StageObserver = null; }
    }
}
