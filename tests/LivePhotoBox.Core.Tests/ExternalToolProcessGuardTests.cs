using LivePhotoBox.Services;
using System.Diagnostics;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class ExternalToolProcessGuardTests
{
    [Fact]
    public async Task RunAsync_SuccessfulProcess_RunsOnce()
    {
        int starts = 0;

        ExternalToolProcessGuard.RunResult result = await ExternalToolProcessGuard.RunAsync(
            () =>
            {
                starts++;
                return CreatePowerShell("[Console]::Out.Write('ok')");
            },
            TimeSpan.FromSeconds(5),
            "guard success test");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, starts);
        Assert.Equal(1, result.Attempts);
        Assert.Equal("ok", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_TimedOutProcess_StopsAfterTwoAttempts()
    {
        int starts = 0;

        ExternalToolProcessGuard.RunResult result = await ExternalToolProcessGuard.RunAsync(
            () =>
            {
                starts++;
                return CreatePowerShell("Start-Sleep -Seconds 5");
            },
            TimeSpan.FromMilliseconds(150),
            "guard timeout test");

        Assert.True(result.TimedOut);
        Assert.False(result.IsSuccess);
        Assert.Equal(ExternalToolProcessGuard.MaxAttempts, starts);
        Assert.Equal(ExternalToolProcessGuard.MaxAttempts, result.Attempts);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_StopsAfterTwoAttempts()
    {
        int starts = 0;

        ExternalToolProcessGuard.RunResult result = await ExternalToolProcessGuard.RunAsync(
            () =>
            {
                starts++;
                return CreatePowerShell("exit 7");
            },
            TimeSpan.FromSeconds(5),
            "guard failure test");

        Assert.False(result.IsSuccess);
        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(ExternalToolProcessGuard.MaxAttempts, starts);
        Assert.Equal(ExternalToolProcessGuard.MaxAttempts, result.Attempts);
    }

    [Fact]
    public async Task RunAsync_UserCancellation_DoesNotRetry()
    {
        int starts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalToolProcessGuard.RunAsync(
                () =>
                {
                    starts++;
                    return CreatePowerShell("Start-Sleep -Seconds 5");
                },
                TimeSpan.FromSeconds(5),
                "guard cancellation test",
                cts.Token));

        Assert.Equal(0, starts);
    }

    private static ProcessStartInfo CreatePowerShell(string command) => new()
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
}
