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
    public void CurrentRuntime_ExposesOnlyCompletedPreviewProtocolCapability()
    {
        NativeRuntimeInfo runtime = NativeRuntime.Probe();
        var settings = new ProcessingBackendSettings { Mode = ProcessingBackendMode.Auto };

        ProcessingBackendProtocolDefinition vivoLegacy = ProcessingBackendProtocolCatalog.All
            .Single(definition => definition.Key == "vivo-legacy");
        ProcessingBackendProtocolDefinition huaweiHonor = ProcessingBackendProtocolCatalog.All
            .Single(definition => definition.Key == "huawei-honor");
        ProcessingBackendProtocolDefinition samsungJpeg = ProcessingBackendProtocolCatalog.All
            .Single(definition => definition.Key == "samsung-jpeg");
        ProcessingBackendProtocolDefinition samsungHeic = ProcessingBackendProtocolCatalog.All
            .Single(definition => definition.Key == "samsung-heic");

        Assert.Equal(NativeBackendMaturity.Preview,
            ProcessingBackendSettingsService.GetNativeMaturity(vivoLegacy, runtime));
        Assert.False(ProcessingBackendSettingsService.ShouldPreferNative(settings, vivoLegacy, runtime));

        Assert.Equal(NativeBackendMaturity.Preview,
            ProcessingBackendSettingsService.GetNativeMaturity(huaweiHonor, runtime));
        Assert.False(ProcessingBackendSettingsService.ShouldPreferNative(settings, huaweiHonor, runtime));

        Assert.Equal(NativeBackendMaturity.Preview,
            ProcessingBackendSettingsService.GetNativeMaturity(samsungJpeg, runtime));
        Assert.False(ProcessingBackendSettingsService.ShouldPreferNative(settings, samsungJpeg, runtime));

        Assert.Equal(NativeBackendMaturity.Preview,
            ProcessingBackendSettingsService.GetNativeMaturity(samsungHeic, runtime));
        Assert.False(ProcessingBackendSettingsService.ShouldPreferNative(settings, samsungHeic, runtime));

        Assert.All(ProcessingBackendProtocolCatalog.All.Where(definition => 
            definition != vivoLegacy && 
            definition != huaweiHonor && 
            definition != samsungJpeg && 
            definition != samsungHeic), definition =>
        {
            Assert.Equal(NativeBackendMaturity.Unavailable,
                ProcessingBackendSettingsService.GetNativeMaturity(definition, runtime));
            Assert.False(ProcessingBackendSettingsService.ShouldPreferNative(settings, definition, runtime));
        });
    }
}
