using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class VideoProbeResultTests
{
    [Fact]
    public async Task MissingVideoReturnsDiagnosticWithoutThrowing()
    {
        var result = await NativeMediaService.TryProbeVideoAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.mov"));
        Assert.False(result.Succeeded);
        Assert.Null(result.Facts);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
    }

    [Fact]
    public async Task CancelledProbeReturnsCancelledWithoutStartingNativeOperation()
    {
        var result = await NativeMediaService.TryProbeVideoAsync("absent.mov", new CancellationToken(true));
        Assert.Equal(NativeResult.Cancelled, result.Result);
        Assert.Null(result.Facts);
    }

    [Fact]
    public async Task MalformedVideoDoesNotRaiseFirstChanceNativeRefusal()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mov");
        File.WriteAllBytes(path, new byte[32]);
        int refusals = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observer = (_, args) =>
        {
            if (args.Exception is InvalidOperationException &&
                args.Exception.Message.StartsWith("Native media operation failed", StringComparison.Ordinal))
                Interlocked.Increment(ref refusals);
        };
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            var result = await NativeMediaService.TryProbeVideoAsync(path);
            Assert.False(result.Succeeded);
            Assert.Null(result.Facts);
            Assert.Equal(0, refusals);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observer;
            File.Delete(path);
        }
    }
}
