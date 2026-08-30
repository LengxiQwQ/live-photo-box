using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class ProcessingBackendSettingsTests
{
    [Theory]
    [InlineData("auto", ProcessingBackendMode.Auto)]
    [InlineData("LEGACY", ProcessingBackendMode.Legacy)]
    [InlineData("custom", ProcessingBackendMode.Custom)]
    public void TryParseMode_AcceptsSupportedNames(string value, ProcessingBackendMode expected)
    {
        Assert.True(ProcessingBackendSettingsService.TryParseMode(value, out ProcessingBackendMode actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Catalog_ResolvesCanonicalKeysAndAliases()
    {
        Assert.True(ProcessingBackendProtocolCatalog.TryResolve("google-v1", out var canonical));
        Assert.True(ProcessingBackendProtocolCatalog.TryResolve("xiaomi", out var alias));
        Assert.Equal("google-v1", canonical!.Key);
        Assert.Equal("google-v2", alias!.Key);
        Assert.Equal(9, ProcessingBackendProtocolCatalog.All.Count);
    }

    [Fact]
    public void CurrentFoundationRuntime_DoesNotEnableReservedProtocolCapabilities()
    {
        NativeRuntimeInfo runtime = NativeRuntime.Probe();
        var settings = new ProcessingBackendSettings { Mode = ProcessingBackendMode.Auto };

        Assert.All(ProcessingBackendProtocolCatalog.All, definition =>
        {
            Assert.Equal(NativeBackendMaturity.Unavailable,
                ProcessingBackendSettingsService.GetNativeMaturity(definition, runtime));
            Assert.False(ProcessingBackendSettingsService.ShouldPreferNative(settings, definition, runtime));
        });
    }
}
