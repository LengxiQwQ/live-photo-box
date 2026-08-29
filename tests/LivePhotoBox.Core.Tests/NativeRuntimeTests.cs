using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class NativeRuntimeTests
{
    [Fact]
    public void Probe_LoadsMatchingNativeRuntime()
    {
        NativeRuntimeInfo info = NativeRuntime.Probe();

        Assert.True(info.IsAvailable, info.Diagnostic);
        Assert.Equal(NativeRuntime.SupportedAbiVersion, info.AbiVersion);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.NotEqual(0UL, info.Capabilities & NativeRuntime.FoundationCapability);

        string managedVersion = typeof(NativeRuntime).Assembly.GetName().Version!.ToString(4);
        Assert.Equal(managedVersion, info.Version);
    }

    [Fact]
    public async Task Probe_IsStableAcrossConcurrentContexts()
    {
        Task<NativeRuntimeInfo>[] probes = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(NativeRuntime.Probe))
            .ToArray();

        NativeRuntimeInfo[] results = await Task.WhenAll(probes);

        Assert.All(results, result => Assert.True(result.IsAvailable, result.Diagnostic));
        Assert.Single(results.Select(result => result.Version).Distinct());
    }
}
