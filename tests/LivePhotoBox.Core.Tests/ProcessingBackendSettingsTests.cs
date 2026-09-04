using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class ProcessingBackendSettingsTests
{
    private static readonly SemaphoreSlim SettingsTestLock = new(1, 1);

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"mode\":\"legacy\"}")]
    [InlineData("{\"schemaVersion\":2,\"mode\":\"legacy\",\"protocols\":{\"vivo-legacy\":\"native\"}}")]
    [InlineData("{\"schemaVersion\":3,\"revision\":42,\"mode\":\"legacy\"}")]
    public async Task ReadSettings_IgnoresOldLegacyModeSafely(string json)
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", path);
            await File.WriteAllTextAsync(path, json);

            ProcessingBackendSettings loaded = ProcessingBackendSettingsService.Load();
            Assert.NotNull(loaded);
            if (json.Contains("\"revision\":42"))
                Assert.Equal(42, loaded.Revision);
            else
                Assert.Equal(0, loaded.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":3,\"revision\":\"not-a-number\"}")]
    [InlineData("not valid json at all")]
    public async Task MalformedSettings_FallBackToDefaultsSafely(string json)
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.json");
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", path);
            await File.WriteAllTextAsync(path, json);

            ProcessingBackendSettings loaded = ProcessingBackendSettingsService.Load();
            Assert.NotNull(loaded);
            Assert.Equal(0, loaded.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task Save_IncrementsRevisionAndPersists()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            string path = Path.Combine(directory, "settings.json");
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", path);

            var settings = new ProcessingBackendSettings { Revision = 5 };
            ProcessingBackendSettingsService.Save(settings);

            ProcessingBackendSettings reloaded = ProcessingBackendSettingsService.Load();
            Assert.True(reloaded.Revision > 5);

            using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(3, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(persisted.RootElement.TryGetProperty("mode", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task SettingsService_FiresChangedEventOnSaveAndReset()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            int changedCount = 0;
            EventHandler handler = (_, _) => changedCount++;
            ProcessingBackendSettingsService.Changed += handler;
            try
            {
                ProcessingBackendSettingsService.Save(new ProcessingBackendSettings());
                Assert.Equal(1, changedCount);

                ProcessingBackendSettingsService.Reset();
                Assert.Equal(2, changedCount);
            }
            finally
            {
                ProcessingBackendSettingsService.Changed -= handler;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task Router_RunAsync_ExecutesInSessionAndCleansUpContext()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();

            int calls = 0;
            string result = await ProcessingPipelineRouter.RunAsync("convert", async () =>
            {
                Assert.NotNull(ProcessingPipelineRouter.Current);
                Assert.Equal("convert", ProcessingPipelineRouter.Current.Operation);
                calls++;
                await Task.Yield();
                return "native-ok";
            });

            Assert.Equal("native-ok", result);
            Assert.Equal(1, calls);
            Assert.Null(ProcessingPipelineRouter.Current);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task Router_NestedOperationCleansUpContextOnException()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));

            await ProcessingPipelineRouter.RunAsync("merge", async () =>
            {
                Assert.Equal("merge", ProcessingPipelineRouter.Current?.Operation);

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ProcessingPipelineRouter.RunAsync("split", () => throw new InvalidOperationException("test failure")));

                Assert.Equal("merge", ProcessingPipelineRouter.Current?.Operation);
            });

            Assert.Null(ProcessingPipelineRouter.Current);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("split")]
    [InlineData("cover")]
    [InlineData("repair")]
    public async Task RebuiltPublicServiceBoundaries_FailFastBeforeOutputHandling(string operation)
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();
            string outputDirectory = Path.Combine(directory, "output");
            string missingImage = Path.Combine(directory, "missing.jpg");
            string missingVideo = Path.Combine(directory, "missing.mp4");

            Exception? exception = await Record.ExceptionAsync(() => operation switch
            {
                "merge" => LivePhotoMergeService.WriteLivePhotoAsync(
                    missingImage, missingVideo, Path.Combine(outputDirectory, "merged.jpg"), 2, CancellationToken.None),
                "split" => LivePhotoSplitService.SplitAsync(
                    missingImage, outputDirectory, 0, 0, CancellationToken.None),
                "cover" => CoverChangeService.ChangeCoverAsync(new CoverChangeRequest
                {
                    ImagePath = missingImage,
                    LivePhotoType = LivePhotoType.SingleFileJpeg,
                    Protocol = LivePhotoProtocolType.GoogleV1,
                    TimestampUs = 0,
                    OutputImagePath = Path.Combine(outputDirectory, "cover.jpg")
                }, CancellationToken.None),
                "repair" => LivePhotoRepairService.RepairAsync(
                    missingImage, Path.Combine(outputDirectory, "repaired.jpg"), new RepairAnalysisResult(), CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            });

            Assert.NotNull(exception);
            if (operation is "split" or "merge")
            {
                Assert.IsType<FileNotFoundException>(exception);
            }
            else
            {
                var rebuilt = Assert.IsType<RebuiltPipelineNotReadyException>(exception);
                Assert.Equal(operation, rebuilt.Operation);
                Assert.Contains($"'{operation}' is not implemented yet in the Rebuilt Native pipeline", rebuilt.Message);
            }
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public void ArchitectureGuard_NoLegacyRuntimeOrRequireLegacySymbols()
    {
        Assembly coreAssembly = typeof(ProcessingBackendSettingsService).Assembly;

        // 1. Assert no ProcessingPipelineMode enum or type exists
        Type? modeType = coreAssembly.GetType("LivePhotoBox.Models.ProcessingPipelineMode");
        Assert.Null(modeType);

        // 2. Assert RebuiltPipelineBoundary has no RequireLegacy method
        Type? boundaryType = coreAssembly.GetType("LivePhotoBox.Services.RebuiltPipelineBoundary");
        if (boundaryType != null)
        {
            MethodInfo? requireLegacyMethod = boundaryType.GetMethod("RequireLegacy", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.Null(requireLegacyMethod);
        }

        // 3. Assert RebuiltPipelineNotReadyException contains no references to disabling or legacy fallback
        var ex = new RebuiltPipelineNotReadyException("test_op");
        Assert.DoesNotContain("switch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turn off", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
