using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LivePhotoBox.Services;

namespace LivePhotoBox.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class PathHelperBenchmarks
{
    private const string InputDir = @"D:\Photos\2024";
    private string[] _testPaths = null!;

    [GlobalSetup]
    public void Setup()
    {
        _testPaths = Enumerable.Range(0, 1000)
            .Select(i => Path.Combine(InputDir, $"Vacation{i % 10}", $"IMG_{i:D4}.jpg"))
            .ToArray();
    }

    [Benchmark]
    public string GetPairingKey()
    {
        // Pick a fixed path so the workload is deterministic
        return PathHelper.GetPairingKey(InputDir, _testPaths[500]);
    }

    [Benchmark]
    public string? GetRelativeSubDirectory()
    {
        return PathHelper.GetRelativeSubDirectory(InputDir, _testPaths[500]);
    }
}

// Entry point
public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<PathHelperBenchmarks>();
    }
}
