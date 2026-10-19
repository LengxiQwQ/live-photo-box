using System.Runtime.CompilerServices;
using System.Text;
using System.Security.Cryptography;
using ImageMagick;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class P0MaintenanceTests
{
    [Fact]
    public void ExifCopyRequest_IsExplicitlyRejected_AndCancellationIsNotSwallowed()
    {
        Assert.Throws<NotSupportedException>(() => EditExportMetadataPolicy.ValidateRequest(true));
        EditExportMetadataPolicy.ValidateRequest(false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => EditExportMetadataPolicy.ValidateRequest(true, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => EditExportMetadataPolicy.ValidateRequest(false, cancelled.Token));
    }

    [Theory]
    [InlineData(3, 0, 3)]
    [InlineData(3, 2, 1)]
    [InlineData(3, 2, 0)]
    public void IncompleteBatch_CannotReportSuccess(int expected, int succeeded, int failed)
    {
        Assert.Throws<InvalidOperationException>(() => EditExportMetadataPolicy.EnsureAllFramesSucceeded(expected, succeeded, failed));
    }

    [Fact]
    public void CompleteBatch_CanReportSuccess() => EditExportMetadataPolicy.EnsureAllFramesSucceeded(3, 3, 0);

    [Theory]
    [InlineData(0, LogLevel.Trace)]
    [InlineData(1, LogLevel.Debug)]
    [InlineData(2, LogLevel.Info)]
    [InlineData(3, LogLevel.Warning)]
    [InlineData(4, LogLevel.Error)]
    [InlineData(99, LogLevel.Error)]
    public unsafe void NativeLog_UsesExplicitUtf8LengthAndPreservesSeverity(int nativeLevel, LogLevel expected)
    {
        string marker = $"P0-{Guid.NewGuid():N}-原生日志";
        byte[] bytes = Encoding.UTF8.GetBytes(marker + "ignored");
        delegate* unmanaged[Cdecl]<nint, int, nint, nuint, void> callback = &NativeContext.LogCallback;
        fixed (byte* p = bytes) callback(0, nativeLevel, (nint)p, (nuint)Encoding.UTF8.GetByteCount(marker));
        var entry = Assert.Single(LogService.GetRecentEntries(1000), e => e.Message == marker);
        Assert.Equal(expected, entry.Level);
        Assert.Equal("LivePhotoBox.Native", entry.FilePath);
        Assert.Equal("lpb_log_callback", entry.OperationId);
    }

    [Fact]
    public unsafe void NativeLog_InvalidBufferDoesNotThrowAcrossAbi_AndIsObservable()
    {
        long before = NativeContext.LogCallbackFailures;
        delegate* unmanaged[Cdecl]<nint, int, nint, nuint, void> callback = &NativeContext.LogCallback;
        callback(0, 2, 0, 1);
        Assert.True(NativeContext.LogCallbackFailures > before);
    }

    [Fact]
    public unsafe void CancellationCallback_MissingOwnerFailsClosed()
    {
        delegate* unmanaged[Cdecl]<nint, int> callback = &NativeContext.CheckCancelledCallback;
        Assert.Equal(1, callback(0));
    }

    [Fact]
    public async Task NativeLog_RealContextCallbacksSurviveGcAndDeferredDispose()
    {
        Assert.True(NativeRuntime.Probe().IsAvailable);
        long failures = NativeContext.LogCallbackFailures;
        await Task.Run(() =>
        {
            var context = NativeContext.Create();
            using var lease = context.AcquireOperationLease();
            context.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.NotEqual(nint.Zero, context.Handle);
            lease.Dispose();
            Assert.Equal(nint.Zero, context.Handle);
            context.Dispose();
        });
        Assert.Contains(LogService.GetRecentEntries(1000), e => e.Message == "LivePhotoBox.Native context created.");
        Assert.Contains(LogService.GetRecentEntries(1000), e => e.Message == "LivePhotoBox.Native context destroyed.");
        Assert.Equal(failures, NativeContext.LogCallbackFailures);
    }

    [Theory]
    [InlineData("华为Mate80.heic")]
    [InlineData("三星.heic")]
    [Trait("Category", "RealSamples")]
    public async Task Thumbnail_InspectedExtractionDecodesRealHeicWithoutChangingSource(string sampleName)
    {
        string path = TestSampleResolver.ResolveSample(sampleName);
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        byte[] jpeg = await MagickHeicThumbnailProvider.LoadInspectedPrimaryThumbnailAsync(path, 160);
        using var image = new MagickImage(jpeg);
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        Assert.InRange(image.Width, 1U, 160U);
        Assert.InRange(image.Height, 1U, 160U);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task Thumbnail_MalformedSourceFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "lpb-p0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "bad.heic");
        try
        {
            await File.WriteAllTextAsync(path, "ftypheic LIVE_ Item:Semantic=\"MotionPhoto\" Item:Length=\"1024\"");
            await Assert.ThrowsAsync<LivePhotoBox.Media.Inspection.SourceInspectionException>(() =>
                MagickHeicThumbnailProvider.LoadInspectedPrimaryThumbnailAsync(path, 160));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task OriginalPhotoExport_UsesConfirmedAuxiliaryBytes_AndPreservesSource()
    {
        string path = TestSampleResolver.ResolveSample("一加-改了封面照片.jpg");
        byte[] source = await File.ReadAllBytesAsync(path);
        var facts = await new LivePhotoBox.Media.Inspection.SourceInspector().InspectAsync(path);
        var original = Assert.Single(facts.AuxiliaryItems, item => item.IsPresent && item.Semantic == "Original");
        byte[] data = Assert.IsType<byte[]>(EditTimingService.ReadOriginalPhotoBytes(path));
        Assert.Equal(source.AsSpan(checked((int)original.ByteOffset), checked((int)original.ByteLength)).ToArray(), data);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), original.Sha256, ignoreCase: true);
        using var decoded = new MagickImage(data);
        Assert.Equal(MagickFormat.Jpeg, decoded.Format);
        Assert.Equal(source, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void OriginalPhotoExport_MissingSourceIsAnError() =>
        Assert.Throws<LivePhotoBox.Media.Inspection.SourceInspectionException>(() =>
            EditTimingService.ReadOriginalPhotoBytes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.jpg")));
}
