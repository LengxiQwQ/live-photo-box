using System;
using System.IO;
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
    [InlineData("legacy", ProcessingPipelineMode.Legacy)]
    [InlineData("REBUIlT", ProcessingPipelineMode.Rebuilt)]
    public void TryParseMode_AcceptsOnlyGlobalModes(string value, ProcessingPipelineMode expected)
    {
        Assert.True(ProcessingBackendSettingsService.TryParseMode(value, out ProcessingPipelineMode actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("auto")]
    [InlineData("google-v2")]
    public void TryParseMode_RejectsProtocolAndOldBackendTerms(string value) =>
        Assert.False(ProcessingBackendSettingsService.TryParseMode(value, out _));

    [Fact]
    public void Defaults_ToRebuilt()
    {
        ProcessingBackendSettings settings = new();
        Assert.Equal(ProcessingPipelineMode.Rebuilt, settings.Mode);
    }

    [Fact]
    public async Task LegacySchema_MigratesToGlobalSwitchWithoutProtocolMatrix()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", path);
            await File.WriteAllTextAsync(path, """{"schemaVersion":2,"protocols":{"vivo-legacy":"native"}}""");

            ProcessingBackendSettings migrated = ProcessingBackendSettingsService.Load();

            Assert.Equal(ProcessingPipelineMode.Rebuilt, migrated.Mode);
            using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(3, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("rebuilt", persisted.RootElement.GetProperty("mode").GetString());
            Assert.False(persisted.RootElement.TryGetProperty("protocols", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"mode\":\"legacy\"}")]
    [InlineData("{\"schemaVersion\":2,\"mode\":\"legacy\",\"protocols\":{\"vivo-legacy\":\"native\"}}")]
    public async Task LegacyModeInV1AndV2Settings_MigratesToLegacy(string json)
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

            ProcessingBackendSettings migrated = ProcessingBackendSettingsService.Load();

            Assert.Equal(ProcessingPipelineMode.Legacy, migrated.Mode);
            using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(3, persisted.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("legacy", persisted.RootElement.GetProperty("mode").GetString());
            Assert.False(persisted.RootElement.TryGetProperty("protocols", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task SetMode_PersistsOneGlobalValueAcrossConcurrentUpdates()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();
            await Task.WhenAll(
                Task.Run(() => ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Legacy)),
                Task.Run(() => ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Rebuilt)));

            ProcessingBackendSettings reloaded = ProcessingBackendSettingsService.Load();
            Assert.True(reloaded.Revision >= 2);
            Assert.Contains(reloaded.Mode, new[] { ProcessingPipelineMode.Legacy, ProcessingPipelineMode.Rebuilt });
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task RebuiltBoundary_FailsBeforeAnyLegacyProtocolPath()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();

            RebuiltPipelineNotReadyException exception = await Assert.ThrowsAsync<RebuiltPipelineNotReadyException>(
                () => ProcessingPipelineRouter.RunAsync("split", () => Task.CompletedTask));
            Assert.Contains("no Legacy protocol fallback", exception.Message, StringComparison.Ordinal);

            ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Legacy);
            Assert.True(ProcessingPipelineRouter.Begin("split").IsLegacy);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task RebuiltRouter_InvokesNativeOperationAndFreezesRebuiltMode()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();

            int calls = 0;
            string result = await ProcessingPipelineRouter.RunRebuiltAsync("convert", async () =>
            {
                Assert.Equal(ProcessingPipelineMode.Rebuilt, ProcessingPipelineRouter.Current?.Mode);
                Assert.Equal("convert", ProcessingPipelineRouter.Current?.Operation);
                calls++;
                ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Legacy);
                await Task.Yield();
                Assert.Equal(ProcessingPipelineMode.Rebuilt, ProcessingPipelineRouter.Current?.Mode);
                return "native";
            });

            Assert.Equal("native", result);
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
    public async Task RebuiltSplit_StopsBeforeSourceOrOutputHandling()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();
            string outputDirectory = Path.Combine(directory, "output");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                LivePhotoSplitService.SplitAsync(
                    Path.Combine(directory, "missing.jpg"), outputDirectory, 0, 0, CancellationToken.None));

            Assert.False(Directory.Exists(outputDirectory));
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

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":3,\"mode\":123}")]
    [InlineData("{\"schemaVersion\":3,\"mode\":\"auto\"}")]
    [InlineData("{\"schemaVersion\":3,\"mode\":\"native\"}")]
    [InlineData("{\"schemaVersion\":3,\"revision\":\"not-a-number\",\"mode\":\"rebuilt\"}")]
    public async Task MalformedOrUnsupportedCurrentSettings_DefaultToRebuilt(string json)
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

            Assert.Equal(ProcessingPipelineMode.Rebuilt, ProcessingBackendSettingsService.Load().Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", previous);
            TryDeleteDirectory(directory);
            SettingsTestLock.Release();
        }
    }

    [Fact]
    public async Task Router_InvokesOnlySelectedBranchAndFreezesModeForNestedCalls()
    {
        await SettingsTestLock.WaitAsync();
        string directory = Path.Combine(Path.GetTempPath(), "lpb-settings-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("LIVEPHOTOBOX_BACKEND_SETTINGS_PATH", Path.Combine(directory, "settings.json"));
            ProcessingBackendSettingsService.Reset();

            int rebuiltCalls = 0;
            await Assert.ThrowsAsync<RebuiltPipelineNotReadyException>(() =>
                ProcessingPipelineRouter.RunAsync("merge", () =>
                {
                    rebuiltCalls++;
                    return Task.CompletedTask;
                }));
            Assert.Equal(0, rebuiltCalls);

            ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Legacy);
            int legacyCalls = 0;
            await ProcessingPipelineRouter.RunAsync("merge", async () =>
            {
                Assert.Equal(ProcessingPipelineMode.Legacy, ProcessingPipelineRouter.Current?.Mode);
                Assert.Equal("merge", ProcessingPipelineRouter.Current?.Operation);
                legacyCalls++;
                ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Rebuilt);
                await Task.Yield();
                Assert.Equal(ProcessingPipelineMode.Legacy, ProcessingPipelineRouter.Current?.Mode);

                await ProcessingPipelineRouter.RunAsync("split", () =>
                {
                    Assert.Equal(ProcessingPipelineMode.Legacy, ProcessingPipelineRouter.Current?.Mode);
                    Assert.Equal("split", ProcessingPipelineRouter.Current?.Operation);
                    return Task.CompletedTask;
                });

                Assert.Equal("merge", ProcessingPipelineRouter.Current?.Operation);
            });

            Assert.Equal(1, legacyCalls);
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
            ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Legacy);

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

    [Fact]
    public async Task SettingsService_FiresChangedEvent()
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
                ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Legacy);
                Assert.Equal(1, changedCount);

                ProcessingBackendSettingsService.SetMode(ProcessingPipelineMode.Rebuilt);
                Assert.Equal(2, changedCount);

                ProcessingBackendSettingsService.Reset();
                Assert.Equal(3, changedCount);
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

    private static void TryDeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
