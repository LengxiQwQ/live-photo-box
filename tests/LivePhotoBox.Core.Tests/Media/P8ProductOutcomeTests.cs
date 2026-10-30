using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8ProductOutcomeTests
{
    [Theory]
    [InlineData(SourceInspectionFailureCategory.Unsupported, "Unsupported")]
    [InlineData(SourceInspectionFailureCategory.Malformed, "Failed")]
    [InlineData(SourceInspectionFailureCategory.Io, "Failed")]
    public void TypedInspectorDiagnostic_PreservesRawCategoryAndStage(
        SourceInspectionFailureCategory category, string status)
    {
        var failure = new SourceInspectionException(category, SourceInspectionStage.Pairing, 1, "localized text has no routing authority");
        var diagnostic = ProductOutcomeCompletion.Failure(failure, ProductOperationStage.Unknown);
        Assert.Equal(category, diagnostic.InspectionCategory);
        Assert.Equal(SourceInspectionStage.Pairing, diagnostic.InspectionStage);
        Assert.Equal(ProductOperationStage.Inspection, diagnostic.Stage);
        Assert.Equal(status, ProductOutcomeCompletion.Status(failure, diagnostic).ToString());
        Assert.Equal(ProductTargetPreservation.Unknown, new ProductOperationOutcome().FinalTargetPreservation);
    }

    [Fact]
    [Trait("Category", "SyntheticWithRealInputs")]
    public async Task NeutralConverterFailure_RetainsActualReturnedRecordAndLegacyExceptionType()
    {
        using var workspace = new MediaWorkspace();
        var converter = new FailedVideoConverter();
        var service = new NeutralMediaService(videoConverter: converter);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateNeutralBundleAsync(
            Image(), Video(), workspace, new MediaFormatRequirement { ImageContainer = ImageContainer.Jpeg, VideoContainer = VideoContainer.Unknown }));
        Assert.True(NeutralPreparationDiagnostics.TryRead(failure, out var diagnostic));
        Assert.Equal(ProductOperationStage.VideoConversion, diagnostic.Stage);
        Assert.Same(converter.Record, Assert.Single(diagnostic.Execution.Videos));
        var projected = ProductOutcomeCompletion.Failure(failure, ProductOperationStage.Unknown);
        Assert.Equal(ConversionFailureCategory.Unsupported, projected.ConversionCategory);
        Assert.Equal(ProductOperationStatus.Unsupported, ProductOutcomeCompletion.Status(failure, projected));
        Assert.Contains("Synthetic", JsonSerializer.Serialize(diagnostic.Execution), StringComparison.Ordinal);
        Assert.Null(diagnostic.Execution.Image);
    }

    [Fact]
    [Trait("Category", "SyntheticWithRealInputs")]
    public async Task NeutralCancellation_AfterCompanionPreparationRetainsCompletedRecordAndSameOce()
    {
        using var workspace = new MediaWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException("Synthetic image-stage cancellation", cancellation.Token);
        var service = new NeutralMediaService(imageConverter: new CancelledImageConverter(original));
        // The token passed to preparation is live. Only the injected image
        // converter cancels, after the real companion Copy operation completed.
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => service.CreateNeutralBundleAsync(
            Image(), Video(), workspace, new MediaFormatRequirement { ImageContainer = ImageContainer.Heic, VideoContainer = VideoContainer.Unknown }));
        Assert.Same(original, failure);
        Assert.True(NeutralPreparationDiagnostics.TryRead(failure, out var diagnostic));
        Assert.Equal(ProductOperationStage.ImageConversion, diagnostic.Stage);
        Assert.Single(diagnostic.Execution.Videos);
        Assert.Equal(VideoBackend.ProjectIsoBmffRemux, diagnostic.Execution.Videos[0].Backend);
        Assert.Null(diagnostic.Execution.Image);
        Assert.Equal(ProductFailureCategory.Cancelled,
            ProductOutcomeCompletion.Failure(failure, ProductOperationStage.Unknown).Category);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task MergeCollision_ReportsOnlyActualCommittedSuffixAndUnknownTargetPreservation()
    {
        using var workspace = new TestFixtureWorkspace();
        // Default namingRuleIndex=0 retains the base name (suffix is rule1).
        string foreign = Path.Combine(workspace.RootDirectory, "plain.jpg");
        await File.WriteAllTextAsync(foreign, "foreign output");
        string inputHash = Hash(Image());
        string videoHash = Hash(Video());
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(
            Image(), Video(), "plain", 1, Options(workspace.RootDirectory), CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        var output = Assert.Single(outcome.CommittedOutputs);
        Assert.Equal("TargetLivePhoto", output.Role);
        Assert.NotEqual(foreign, output.Path);
        Assert.Equal(Path.Combine(workspace.RootDirectory, "plain (2).jpg"), output.Path);
        Assert.True(File.Exists(output.Path));
        Assert.Equal("foreign output", await File.ReadAllTextAsync(foreign));
        Assert.Equal(inputHash, Hash(Image()));
        Assert.Equal(videoHash, Hash(Video()));
        Assert.Equal(ProductTargetPreservation.Unknown, outcome.FinalTargetPreservation);
        Assert.Equal("PreparedNeutral", outcome.PreparationEvidenceScope);
        Assert.NotNull(outcome.PreparationSemantics);
        Assert.NotEmpty(outcome.PreparationExecution!.Videos);
        Assert.NotEqual(Guid.Empty, outcome.OperationId);
        Assert.True(outcome.CompletedUtc >= outcome.StartedUtc);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task SplitSuccess_ReportsActualTwoOutputsAndNoInventedConverter()
    {
        using var workspace = new TestFixtureWorkspace();
        // Original-source Split coverage is separate from the structured target round trip below.
        string source = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_SPLIT_LIVE_SAMPLE")
            ?? throw new InvalidOperationException("The frozen cached Honor Split input must be provided; no sample skip.");
        Assert.True(File.Exists(source));
        Assert.Equal("970658F835ADD139247694A67803E21A0CA0421290A3A2D269D52D970CABF759", Hash(source));
        string sourceHash = Hash(source);
        var split = await LivePhotoSplitService.SplitWithOutcomeAsync(source,
            Path.Combine(workspace.RootDirectory, "split"), SplitMediaFormat.KeepSource, CancellationToken.None,
            outputBaseName: "chosen");
        Assert.True(split.IsSuccess, split.Details);
        Assert.Equal(2, split.CommittedOutputs.Count);
        Assert.Equal(new[] { "PrimaryImage", "MotionVideo" }, split.CommittedOutputs.Select(item => item.Role));
        Assert.All(split.CommittedOutputs, output => Assert.True(File.Exists(output.Path)));
        Assert.All(split.CommittedOutputs, output => Assert.StartsWith("chosen", Path.GetFileName(output.Path), StringComparison.Ordinal));
        Assert.Equal(sourceHash, Hash(source));
        Assert.NotNull(split.PreparationExecution);
        Assert.Null(split.PreparationExecution.Image);
        Assert.Empty(split.PreparationExecution.Videos);
        Assert.Equal(ProductTargetPreservation.Unknown, split.FinalTargetPreservation);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task StructuredWriterKeepsUniqueXmpAndSplitStillRejectsAnExplicitDuplicate()
    {
        using var workspace = new TestFixtureWorkspace();
        string imageHash = Hash(Image()), videoHash = Hash(Video());
        Assert.Equal(1, CountStandardXmpPackets(Image()));
        var merge = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(
            Image(), Video(), "duplicate-xmp", 1, Options(workspace.RootDirectory), CancellationToken.None);
        Assert.True(merge.IsSuccess, merge.Details);
        string committedMerge = Assert.Single(merge.CommittedOutputs).Path;
        Assert.True(File.Exists(committedMerge));
        Assert.Equal(1, CountStandardXmpPackets(committedMerge));
        Assert.Equal(ProductTargetPreservation.Unknown, merge.FinalTargetPreservation);
        string committedHash = Hash(committedMerge);
        var roundTrip = await LivePhotoSplitService.SplitWithOutcomeAsync(committedMerge,
            Path.Combine(workspace.RootDirectory, "round-trip"), SplitMediaFormat.KeepSource, CancellationToken.None);
        Assert.True(roundTrip.IsSuccess, roundTrip.Details);
        Assert.Equal(2, roundTrip.CommittedOutputs.Count);
        Assert.Equal(committedHash, Hash(committedMerge));

        string malformed = Path.Combine(workspace.RootDirectory, "explicit-duplicate.jpg");
        File.WriteAllBytes(malformed, P9TargetTestInputs.AddXmp(File.ReadAllBytes(committedMerge),
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>"));
        Assert.Equal(2, CountStandardXmpPackets(malformed));
        string splitOutput = Path.Combine(workspace.RootDirectory, "must-not-publish-split");

        var split = await LivePhotoSplitService.SplitWithOutcomeAsync(malformed, splitOutput,
            SplitMediaFormat.KeepSource, CancellationToken.None, outputBaseName: "rejected");
        Assert.Equal(ProductOperationStatus.Failed, split.Status);
        Assert.Equal(ProductOperationStage.Inspection, split.Diagnostic.Stage);
        Assert.Equal(SourceInspectionStage.Metadata, split.Diagnostic.InspectionStage);
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, split.Diagnostic.InspectionCategory);
        Assert.Empty(split.CommittedOutputs);
        Assert.False(Directory.Exists(splitOutput));
        Assert.True(File.Exists(committedMerge)); // Merge already committed; Split does not undo it.
        Assert.Equal(committedHash, Hash(committedMerge));
        Assert.Equal(imageHash, Hash(Image()));
        Assert.Equal(videoHash, Hash(Video()));
        Assert.Equal(ProductTargetPreservation.Unknown, split.FinalTargetPreservation);
    }

    [Fact]
    public async Task FailedMerge_ForeignDestinationIsNotAnOutputOfThisOperation()
    {
        using var workspace = new TestFixtureWorkspace();
        string foreign = Path.Combine(workspace.RootDirectory, "foreign.jpg");
        await File.WriteAllTextAsync(foreign, "foreign retained");
        var options = new LivePhotoMergeRunOptions
        {
            OutputDirectory = workspace.RootDirectory,
            OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            OutputFilePath = foreign
        };
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(
            Path.Combine(workspace.RootDirectory, "missing.jpg"), Path.Combine(workspace.RootDirectory, "missing.mp4"),
            "invalid", 1, options, CancellationToken.None);
        Assert.False(outcome.IsSuccess);
        Assert.Empty(outcome.CommittedOutputs);
        Assert.Equal("foreign retained", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task TypedSingleCancellation_ReturnsOutcomeWhileLegacyStillThrows()
    {
        using var workspace = new TestFixtureWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = Options(workspace.RootDirectory);
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync("missing.jpg", "missing.mp4",
            "cancel", 1, options, cancellation.Token);
        Assert.Equal(ProductOperationStatus.Cancelled, outcome.Status);
        Assert.Equal(130, outcome.ExitCode);
        Assert.Empty(outcome.CommittedOutputs);
        await Assert.ThrowsAsync<OperationCanceledException>(() => LivePhotoMergeRunnerService.ProcessSinglePairAsync(
            "missing.jpg", "missing.mp4", "cancel", 1, options, workspace.RootDirectory, cancellation.Token));
    }

    [Fact]
    public async Task TypedBatchCancellation_DeliversStartedOutcomeWithoutLegacyCountIncrement()
    {
        using var workspace = new TestFixtureWorkspace();
        using var cancellation = new CancellationTokenSource();
        using var pause = new ManualResetEventSlim(true);
        var outcomes = new List<ProductOperationOutcome>();
        var counts = new List<int>();
        IMergeTaskInfo[] tasks = [new QueueTask(1), new QueueTask(2)];
        await Assert.ThrowsAsync<OperationCanceledException>(() => LivePhotoMergeRunnerService.RunWithOutcomesAsync(
            tasks, Options(workspace.RootDirectory), pause, cancellation.Token,
            _ => cancellation.Cancel(), (_, outcome, count) => { outcomes.Add(outcome); counts.Add(count); }));
        Assert.Equal(ProductOperationStatus.Cancelled, Assert.Single(outcomes).Status);
        Assert.Equal(0, Assert.Single(counts));
        Assert.Empty(outcomes[0].CommittedOutputs);
    }

    private static string Inputs() => Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
        ?? throw new InvalidOperationException("Frozen R2 derived ordinary pair inputs are required; missing samples are not skipped.");
    private static string Image() => Path.Combine(Inputs(), "image.jpg");
    private static string Video() => Path.Combine(Inputs(), "video.mp4");
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static int CountStandardXmpPackets(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Assert.True(data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8);
        ReadOnlySpan<byte> header = "http://ns.adobe.com/xap/1.0/\0"u8;
        int position = 2, count = 0;
        while (position < data.Length)
        {
            Assert.Equal(0xFF, data[position]);
            while (position < data.Length && data[position] == 0xFF) position++;
            Assert.True(position < data.Length);
            byte marker = data[position++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
            Assert.True(position + 2 <= data.Length);
            int length = (data[position] << 8) | data[position + 1];
            Assert.True(length >= 2 && length <= data.Length - position);
            if (marker == 0xE1 && data.AsSpan(position + 2, length - 2).StartsWith(header)) count++;
            position += length;
        }
        return count;
    }
    private static LivePhotoMergeRunOptions Options(string output) => new()
    {
        OutputDirectory = output, OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
        TaskStartInterval = TimeSpan.Zero, MaxDegreeOfParallelism = 1
    };

    private sealed record QueueTask(int Index) : IMergeTaskInfo
    {
        public string ImagePath => "missing.jpg";
        public string VideoPath => "missing.mp4";
        public string BaseName => "cancel";
        public ProcessStatus Status { get; set; }
        public string Details { get; set; } = string.Empty;
    }

    private sealed class CancelledImageConverter(OperationCanceledException exception) : IImageConverter
    {
        public Task<ImageConversionResult> ConvertAsync(ImageConversionRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<ImageConversionResult>(exception);
    }

    private sealed class FailedVideoConverter : IVideoConverter
    {
        public VideoExecutionRecord Record { get; } = new()
        {
            InputContainer = VideoContainer.Mp4, InputCodec = VideoCodec.H264,
            RequestedContainer = VideoContainer.Mp4, RequestedCodec = VideoCodec.Copy,
            OutputContainer = VideoContainer.Unknown, OutputCodec = VideoCodec.Unknown,
            Backend = VideoBackend.MinimalLibav, SelectedEncoder = "minimal-libav/libx264",
            HardwareFallbackOccurred = true, HardwareFallbackReason = "Synthetic returned failure record",
            Truth = new() { FallbackAllowed = true, FallbackOccurred = true,
                BackendName = "minimal-libav", BackendVersion = "9.0.1",
                FallbackReason = "Synthetic returned failure record", FailureStage = ConversionFailureStage.PolicyRejection,
                FailureCategory = ConversionFailureCategory.Unsupported }
        };
        public Task<VideoFacts> ProbeAsync(string path, CancellationToken cancellationToken = default)
            => new VideoConverter().ProbeAsync(path, cancellationToken);
        public Task<VideoConversionResult> ConvertAsync(VideoConversionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new VideoConversionResult { Success = false, ExecutionRecord = Record, ErrorMessage = "Synthetic converter failure" });
    }
}
