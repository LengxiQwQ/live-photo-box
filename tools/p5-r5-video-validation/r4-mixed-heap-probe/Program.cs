using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

// Intrusive offline diagnosis only. Invoke the existing exact mixed campaign;
// HeapWalk may change heap timing and cannot establish resource acceptance.
string runtime = Path.GetFullPath(args[0]);
string samples = Path.GetFullPath(args[1]);
string destination = Path.GetFullPath(args[2]);
int minutes = int.Parse(args[3]);
Directory.CreateDirectory(destination);
AssemblyLoadContext.Default.Resolving += (_, name) => {
    string path = Path.Combine(Path.GetDirectoryName(runtime)!, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var campaign = AssemblyLoadContext.Default.LoadFromAssemblyPath(runtime);
var program = campaign.GetType("Program", true)!;
var snapshot = program.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
using var output = new StreamWriter(new FileStream(Path.Combine(destination, "heap-samples.jsonl"), FileMode.CreateNew));
ulong Busy(object heaps) => JsonSerializer.SerializeToElement(heaps).EnumerateArray()
    .Aggregate(0UL, (sum, heap) => checked(sum + heap.GetProperty("busy").GetUInt64()));
object Heaps() {
    var value = HeapStats.ReadAll();
    if (JsonSerializer.SerializeToElement(value).EnumerateArray().Any(h => !h.GetProperty("complete").GetBoolean()))
        throw new InvalidOperationException("Incomplete diagnostic heap walk");
    return value;
}
// Qualify live-busy accounting against an explicitly owned process-heap block.
Heaps();
ulong before = Busy(Heaps());
nint allocation = HeapApi.HeapAlloc(HeapApi.GetProcessHeap(), 0, 8 * 1024 * 1024);
if (allocation == 0) throw new InvalidOperationException("Heap qualification allocation failed");
ulong during;
try { during = Busy(Heaps()); }
finally {
    if (!HeapApi.HeapFree(HeapApi.GetProcessHeap(), 0, allocation))
        throw new InvalidOperationException("Heap qualification release failed");
}
ulong after = Busy(Heaps());
if (during < before + 8 * 1024 * 1024 || during < after + 8 * 1024 * 1024)
    throw new InvalidOperationException($"Heap qualification failed: {before}/{during}/{after}");
File.WriteAllText(Path.Combine(destination, "qualification.json"), JsonSerializer.Serialize(new {
    passed = true, before, during, after, allocatedBytes = 8 * 1024 * 1024,
    meaning = "Live busy-byte delta only; busy plus free is not whole-process private memory." }));
int round = -1, checkpoint = 0;
void Observe(string stage) {
    if (stage == "heic") round++;
    var memory = GC.GetGCMemoryInfo();
    output.WriteLine(JsonSerializer.Serialize(new {
        round, checkpoint = checkpoint++, stage, resources = snapshot.Invoke(null, null),
        virtualMemory = VirtualStats.ReadAll(), heaps = Heaps(),
        gc = new { memory.TotalCommittedBytes, memory.HeapSizeBytes, memory.FragmentedBytes,
            collection0 = GC.CollectionCount(0), collection1 = GC.CollectionCount(1), collection2 = GC.CollectionCount(2) }
    }));
    output.Flush();
}
Observe("initial");
if (args.Length > 4 && args[4] == "qualify-only") return;
var observer = program.GetField("StageObserver", BindingFlags.Static | BindingFlags.NonPublic)!;
observer.SetValue(null, (Action<string>)Observe);
try {
    var mixed = program.GetMethod("Mixed", BindingFlags.Static | BindingFlags.NonPublic)!;
    int exit = await (Task<int>)mixed.Invoke(null, new object[] {
        new[] { "offline-mixed-heap", samples, Path.Combine(destination, "case") }, minutes })!;
    if (exit != 0) throw new InvalidOperationException("Mixed campaign failed: " + exit);
    Observe("after-existing-campaign-quiet");
} finally { observer.SetValue(null, null); }

internal static class HeapApi {
    [DllImport("kernel32.dll")] internal static extern nint GetProcessHeap();
    [DllImport("kernel32.dll")] internal static extern nint HeapAlloc(nint heap, uint flags, nuint bytes);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HeapFree(nint heap, uint flags, nint allocation);
}
