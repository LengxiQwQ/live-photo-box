using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceInspectorRoutingRegressionTests
{
    [Fact]
    public void EditStillRouting_UsesNativeFacts_AndHasNoManagedRangeScanner()
    {
        string source = ReadProductionSource("LivePhotoBox", "ViewModels", "EditViewModel.cs");
        string method = ExtractMethod(
            source,
            "private static async Task<string> ResolveStillPhotoSourceAsync",
            "private async Task ExportOneFrameAsync");

        Assert.Contains("SourceInspector", method, StringComparison.Ordinal);
        Assert.Contains("InspectAsync", method, StringComparison.Ordinal);
        Assert.Contains("PrimaryImage", method, StringComparison.Ordinal);
        Assert.Contains("MotionVideo", method, StringComparison.Ordinal);
        Assert.Contains("ByteOffset", method, StringComparison.Ordinal);
        Assert.Contains("ByteLength", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMpvdVideoStart", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHuaweiEmbeddedVideoRange", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAppendedVideoLength", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadMetadataTextSync", method, StringComparison.Ordinal);

        Assert.DoesNotContain("FastMetadataReader", source, StringComparison.Ordinal);
        Assert.Contains("ReadRebuiltResolutionsAsync", source, StringComparison.Ordinal);
        Assert.Contains("facts.PrimaryImage", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverExtraction_UsesNativeVideoOffsetAndLengthExactly()
    {
        using var temp = new DisposableTempDirectory();
        string source = Path.Combine(temp.Path, "source.jpg");
        string workDir = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(workDir);
        byte[] bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(source, bytes);

        const long offset = 7;
        const long length = 9;
        var inspector = new RecordingInspector((_, _) => SingleFileFacts(
            SourceProtocol.GoogleMotionPhotoV2,
            primaryLength: offset,
            videoOffset: offset,
            videoLength: length,
            container: VideoContainer.Mp4));

        string? extracted = await CoverChangeService.ExtractEmbeddedVideoForPreviewAsync(
            source,
            LivePhotoProtocolType.GoogleV2,
            workDir,
            CancellationToken.None,
            inspector);

        Assert.NotNull(extracted);
        Assert.Equal(bytes.Skip((int)offset).Take((int)length), await File.ReadAllBytesAsync(extracted!));
        Assert.Single(inspector.Calls);
    }

    [Fact]
    public async Task CoverExtraction_FailsClosed_WhenInspectorReportsAmbiguousStructure()
    {
        using var temp = new DisposableTempDirectory();
        string source = Path.Combine(temp.Path, "ambiguous.jpg");
        string workDir = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(workDir);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        var typedFailure = new SourceInspectionException(
            SourceInspectionFailureCategory.Ambiguous,
            SourceInspectionStage.Metadata,
            capability: 0x40,
            "Conflicting declarations.");
        var inspector = new RecordingInspector((_, _) => throw typedFailure);

        var observed = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            CoverChangeService.ExtractEmbeddedVideoForPreviewAsync(
                source,
                LivePhotoProtocolType.GoogleV2,
                workDir,
                CancellationToken.None,
                inspector));

        Assert.Same(typedFailure, observed);
        Assert.Empty(Directory.EnumerateFiles(workDir));
    }

    [Fact]
    public void CoverRouting_UsesNativeRangeAndContainerFacts_Only()
    {
        string source = ReadProductionSource("LivePhotoBox.Core", "Services", "CoverChangeService.cs");

        Assert.Contains("ISourceInspector", source, StringComparison.Ordinal);
        Assert.Contains("MotionVideo", source, StringComparison.Ordinal);
        Assert.Contains("ByteOffset", source, StringComparison.Ordinal);
        Assert.Contains("ByteLength", source, StringComparison.Ordinal);
        Assert.Contains("VideoContainer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMpvdVideoStart", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHuaweiEmbeddedVideoRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FindSamsungJpegVideoRange", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAppendedVideoLength", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadMetadataTextSync", source, StringComparison.Ordinal);

        string resolver = ReadProductionSource("LivePhotoBox.Core", "Services", "CoverInputResolver.cs");
        Assert.Contains("SourceInspector", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("LivePhotoProtocolDetector", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("catch\n", resolver, StringComparison.Ordinal);

        string split = ReadProductionSource("LivePhotoBox", "ViewModels", "SplitViewModel.cs");
        Assert.Contains("catch (SourceInspectionException", split, StringComparison.Ordinal);
        Assert.Contains("throw;", split, StringComparison.Ordinal);

        string merge = ReadProductionSource("LivePhotoBox", "ViewModels", "MergeViewModel.cs");
        Assert.Contains("MatchNativePairsAsync", merge, StringComparison.Ordinal);
        Assert.DoesNotContain("0 => true", merge, StringComparison.Ordinal);

        string mergeScan = ReadProductionSource("LivePhotoBox.Core", "Services", "LivePhotoMergeScanService.cs");
        Assert.Contains("SourceInspector", mergeScan, StringComparison.Ordinal);
        Assert.Contains("MatchAsync", mergeScan, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFileNameWithoutExtension(kvp.Key)", mergeScan, StringComparison.Ordinal);
    }

    private static string ReadProductionSource(params string[] relativePath)
    {
        string directory = AppContext.BaseDirectory;
        while (true)
        {
            string candidate = Path.Combine(new[] { directory }.Concat(relativePath).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            string? parent = Directory.GetParent(directory)?.FullName;
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                break;
            directory = parent;
        }

        throw new FileNotFoundException(
            $"Production source was not found: {Path.Combine(relativePath.ToArray())}");
    }

    private static string ExtractMethod(string source, string methodName, string followingMethodName)
    {
        int start = source.IndexOf(methodName, StringComparison.Ordinal);
        int end = source.IndexOf(followingMethodName, start + methodName.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method '{methodName}' was not found in production source.");
        Assert.True(end > start, $"Following method '{followingMethodName}' was not found after '{methodName}'.");
        return source[start..end];
    }

    private static SourceMediaFacts SingleFileFacts(
        SourceProtocol protocol,
        long primaryLength,
        long videoOffset,
        long videoLength,
        VideoContainer container,
        long primaryOffset = 0) => new()
    {
        Protocol = protocol,
        PrimaryImage = new ImageFacts
        {
            IsPresent = true,
            Container = ImageContainer.Jpeg,
            ByteOffset = primaryOffset,
            ByteLength = primaryLength
        },
        MotionVideo = new VideoFacts
        {
            IsPresent = true,
            Container = container,
            ByteOffset = videoOffset,
            ByteLength = videoLength,
            SourceIndex = 0
        }
    };

    private sealed class RecordingInspector(
        Func<string, string?, SourceMediaFacts> handler) : ISourceInspector
    {
        public List<(string Primary, string? Secondary)> Calls { get; } = [];

        public Task<SourceMediaFacts> InspectAsync(
            string primaryPath,
            string? secondaryPath = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((primaryPath, secondaryPath));
            return Task.FromResult(handler(primaryPath, secondaryPath));
        }
    }

    private sealed class DisposableTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lpb-routing-tests-" + Guid.NewGuid().ToString("N"));

        public DisposableTempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
