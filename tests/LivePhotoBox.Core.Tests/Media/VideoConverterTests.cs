using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class VideoConverterTests
{
    private static string ResolveSample(string fileName) => TestSampleResolver.ResolveSample(fileName);

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Probe_AppleMov_ExtractsVideoFacts()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        var converter = new VideoConverter();
        var facts = await converter.ProbeAsync(sample);

        Assert.NotNull(facts);
        Assert.True(facts.IsPresent);
        Assert.Equal(VideoContainer.Mov, facts.Container);
        Assert.Equal(VideoCodec.Hevc, facts.Codec);
        Assert.True(facts.Width > 0);
        Assert.True(facts.Height > 0);
        Assert.True(facts.DurationSeconds > 0);
        Assert.True(facts.Fps > 0);
        Assert.True(facts.HasAudio);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Probe_VivoMp4_ExtractsVideoFacts()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        var converter = new VideoConverter();
        var facts = await converter.ProbeAsync(sample);

        Assert.NotNull(facts);
        Assert.True(facts.IsPresent);
        Assert.Equal(VideoContainer.Mp4, facts.Container);
        Assert.Equal(VideoCodec.H264, facts.Codec);
        Assert.True(facts.Width > 0);
        Assert.True(facts.Height > 0);
        Assert.True(facts.DurationSeconds > 0);
        Assert.True(facts.Fps > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_MovToMp4_CopyRequest_ProjectsRemuxTruth()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ConversionOperationKind.ContainerRemux, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoBackend.ProjectIsoBmffRemux, result.ExecutionRecord.Backend);
        Assert.Contains("HEVC;", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Equal(result.ExecutionRecord.Truth.InputProfile, result.ExecutionRecord.Truth.OutputProfile);
        Assert.Equal(ConversionCapability.VideoRemux, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.VideoRemux, result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal("ProjectIsoBmffRemux", result.ExecutionRecord.Truth.BackendName);
        Assert.Equal(ConversionOperationKind.ContainerRemux, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(VideoHardwareMode.NotApplicable, result.ExecutionRecord.HardwareMode);
        Assert.False(result.ExecutionRecord.HardwareFallbackOccurred);
        Assert.Equal(VideoContainer.Mov, result.ExecutionRecord.InputContainer);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.OutputContainer);
        Assert.Equal(VideoCodec.Hevc, result.ExecutionRecord.OutputCodec);
        Assert.True(result.ExecutionRecord.AudioPreserved);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_AppleMovToMov_CopyRequest_ReportsByteIdenticalPassthrough()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/quicktime",
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Hevc,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = VideoContainer.Mov,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.Equal(await File.ReadAllBytesAsync(sample), await File.ReadAllBytesAsync(result.OutputArtifact.Path));
        Assert.Equal(ConversionOperationKind.ContainerRemux, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.Passthrough, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(PreservationOutcome.Preserved, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.True(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoBackend.ProjectIsoBmffRemux, result.ExecutionRecord.Backend);
        Assert.Equal("ProjectIsoBmffStreamRemux", result.ExecutionRecord.SelectedEncoder);
        Assert.Equal(ConversionCapability.VideoRemux, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.VideoRemux, result.ExecutionRecord.Truth.ActualCapability);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_VivoMp4ToMp4_CopyRequest_ReportsNonIdenticalContainerRemux()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.H264,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        byte[] sourceBytes = await File.ReadAllBytesAsync(sample);
        byte[] outputBytes = await File.ReadAllBytesAsync(result.OutputArtifact.Path);
        Assert.False(sourceBytes.AsSpan().SequenceEqual(outputBytes));
        Assert.Equal(ConversionOperationKind.ContainerRemux, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.ContainerRemux, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.True(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoBackend.ProjectIsoBmffRemux, result.ExecutionRecord.Backend);
        Assert.Equal("ProjectIsoBmffStreamRemux", result.ExecutionRecord.SelectedEncoder);
        Assert.Equal(ConversionCapability.VideoRemux, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.VideoRemux, result.ExecutionRecord.Truth.ActualCapability);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_Mp4ToMov_PrioritizesStreamRemux()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/mp4",
            VideoContainer = VideoContainer.Mp4,
            VideoCodec = VideoCodec.H264,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mov,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ConversionOperationKind.ContainerRemux, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.InputContainer);
        Assert.Equal(VideoContainer.Mov, result.ExecutionRecord.OutputContainer);
        Assert.Equal(VideoCodec.H264, result.ExecutionRecord.OutputCodec);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_AppleHevcToH264_ProjectsSoftwareForcedSdrTruth()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoBackend.WindowsMediaFoundation, result.ExecutionRecord.Backend);
        Assert.Equal(VideoHardwareMode.SoftwareForced, result.ExecutionRecord.HardwareMode);
        Assert.False(result.ExecutionRecord.HardwareFallbackOccurred);
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.VideoTranscodeSdr, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.VideoTranscodeSdr, result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal("WindowsMediaFoundation", result.ExecutionRecord.Truth.BackendName);
        Assert.Equal("SoftwareForced", result.ExecutionRecord.Truth.HardwareMode);
        Assert.Contains("HEVC;", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Contains("H.264;", result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
        Assert.Contains("CICP=12/1/6;range=full", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Contains("CICP=12/1/6;range=full", result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
        Assert.Contains("Hardware transforms are disabled", result.ExecutionRecord.HardwareFallbackReason,
            StringComparison.Ordinal);
        Assert.Equal(VideoCodec.H264, result.ExecutionRecord.OutputCodec);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.OutputContainer);

        // Verify output file independently
        var reProbed = await converter.ProbeAsync(result.OutputArtifact.Path);
        Assert.Equal(VideoContainer.Mp4, reProbed.Container);
        Assert.Equal(VideoCodec.H264, reProbed.Codec);
        Assert.True(reProbed.DurationSeconds > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_HevcToH264Mov_PublishesRealTranscode()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };
        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mov,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.RemuxUsed);
        var facts = await converter.ProbeAsync(result.OutputArtifact.Path);
        Assert.Equal(VideoContainer.Mov, facts.Container);
        Assert.Equal(VideoCodec.H264, facts.Codec);
        Assert.True(facts.DurationSeconds > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_VivoH264ToHevc_ProjectsSoftwareForcedSdrTruth()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/mp4",
            VideoContainer = VideoContainer.Mp4,
            VideoCodec = VideoCodec.H264,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Hevc,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.VideoTranscodeSdr, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal("WindowsMediaFoundation", result.ExecutionRecord.Truth.BackendName);
        Assert.Equal("SoftwareForced", result.ExecutionRecord.Truth.HardwareMode);
        Assert.Contains("H.264;", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Contains("HEVC;", result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
        Assert.Contains("CICP=5/6/6;range=full", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Contains("CICP=5/6/6;range=full", result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
        Assert.Equal(VideoCodec.Hevc, result.ExecutionRecord.OutputCodec);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.OutputContainer);

        // Verify output file independently
        var reProbed = await converter.ProbeAsync(result.OutputArtifact.Path);
        Assert.Equal(VideoContainer.Mp4, reProbed.Container);
        Assert.Equal(VideoCodec.Hevc, reProbed.Codec);
        Assert.True(reProbed.DurationSeconds > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_TargetFps_ReturnsExplicitUnsupportedWithoutOutput()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory,
            TargetFps = 60
        });

        Assert.False(result.Success);
        Assert.Contains("TargetFps", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConversionOperationKind.Unsupported, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(ConversionFailureStage.InvalidRequest, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionCapability.Unknown, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(PreservationOutcome.Unsupported, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_TrustedFactsMismatchDoesNotSelectAnSdrOwner()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();
        VideoConversionResult result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/quicktime",
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Hevc,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory,
            TrustedSourceFacts = new VideoConversionSourceFacts
            {
                Container = VideoContainer.Mov,
                Codec = VideoCodec.H264
            }
        });

        Assert.False(result.Success);
        Assert.Equal(ConversionFailureCategory.TrustedFactsMismatch, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(ConversionFailureStage.TrustedFactsMismatch, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionCapability.Unknown, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal("Unknown", result.ExecutionRecord.Truth.InputProfile);
        Assert.Equal("Unknown", result.ExecutionRecord.Truth.OutputProfile);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Fact]
    public async Task P5R5_Convert_SourceProbeFailureDoesNotSelectAnSdrOwner()
    {
        using var workspace = new MediaWorkspace();
        string missingSource = Path.Combine(workspace.RootDirectory, "missing-source.mp4");
        VideoConversionResult result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = missingSource,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.Hevc,
                ByteLength = 1
            },
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.False(result.Success);
        Assert.Equal(ConversionFailureCategory.SourceInspection, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(ConversionCapability.Unknown, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_HuaweiMain10ToHevc_ReportsMinimalLibavAndHdrCapability()
    {
        string sample = ResolveHuaweiDerivedVideo();
        using var workspace = new MediaWorkspace();
        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.Hevc,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Hevc,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.Equal(VideoBackend.Unknown, result.ExecutionRecord.Backend);
        Assert.Equal(VideoHardwareMode.SoftwareForced, result.ExecutionRecord.HardwareMode);
        Assert.StartsWith("minimal-libav/P5-R5-v1/", result.ExecutionRecord.SelectedEncoder, StringComparison.Ordinal);
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.VideoTranscodeHdr10Bit, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.VideoTranscodeHdr10Bit, result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal("minimal-libav", result.ExecutionRecord.Truth.BackendName);
        Assert.Equal("9.0.1", result.ExecutionRecord.Truth.BackendVersion);
        Assert.Contains("HEVC;", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Contains("HEVC;", result.ExecutionRecord.Truth.OutputProfile, StringComparison.Ordinal);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_Convert_HuaweiMain10ToH264_ReturnsUnsupportedWithoutRetryOrArtifact()
    {
        string sample = ResolveHuaweiDerivedVideo();
        using var workspace = new MediaWorkspace();
        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mp4,
                VideoCodec = VideoCodec.Hevc,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.StartsWith("Unsupported:", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(VideoBackend.Unknown, result.ExecutionRecord.Backend);
        Assert.Equal(VideoHardwareMode.SoftwareForced, result.ExecutionRecord.HardwareMode);
        Assert.StartsWith("minimal-libav/P5-R5-v1", result.ExecutionRecord.SelectedEncoder, StringComparison.Ordinal);
        Assert.Equal(ConversionOperationKind.Unsupported, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(ConversionFailureStage.OutputValidation, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionCapability.VideoTranscodeHdr10Bit, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal("minimal-libav", result.ExecutionRecord.Truth.BackendName);
        Assert.Equal("9.0.1", result.ExecutionRecord.Truth.BackendVersion);
        Assert.Equal(PreservationOutcome.Unsupported, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.Contains("HEVC;", result.ExecutionRecord.Truth.InputProfile, StringComparison.Ordinal);
        Assert.Equal("Unknown", result.ExecutionRecord.Truth.OutputProfile);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_NativeAttempt_RetainsUnsupportedResultCodeDiagnosticsAndLastError()
    {
        string sample = ResolveHuaweiDerivedVideo();
        using var workspace = new MediaWorkspace();
        string outputPath = Path.Combine(workspace.RootDirectory, "huawei-h264.mp4");

        NativeMediaService.NativeVideoTranscodeAttempt attempt = await NativeMediaService.TranscodeVideoAttemptAsync(
            sample,
            outputPath,
            VideoContainer.Mp4,
            VideoCodec.H264,
            23);

        Assert.True(attempt.ResultCode == NativeResult.InvalidArgument, attempt.LastError ?? "Native failure result code was not the frozen Unsupported code.");
        Assert.False(attempt.Succeeded);
        Assert.NotNull(attempt.FailureException);
        Assert.StartsWith("Unsupported:", attempt.LastError, StringComparison.Ordinal);
        Assert.Equal(VideoBackend.Unknown, attempt.Diagnostics.Backend);
        Assert.Equal(VideoHardwareMode.SoftwareForced, attempt.Diagnostics.HardwareMode);
        Assert.StartsWith("minimal-libav/P5-R5-v1", attempt.Diagnostics.SelectedEncoder, StringComparison.Ordinal);
        Assert.Equal("9.0.1", attempt.Diagnostics.BackendVersion);
        Assert.True(attempt.Diagnostics.OutputCandidateObserved);
        Assert.Contains("HEVC;", attempt.Diagnostics.InputProfile, StringComparison.Ordinal);
        Assert.Equal("Unknown", attempt.Diagnostics.OutputProfile);
        Assert.False(attempt.Diagnostics.HardwareFallbackOccurred);
        Assert.False(File.Exists(outputPath));
    }

    private static string ResolveHuaweiDerivedVideo()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".ai", "state.json")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not locate the Live Photo Box workspace for the frozen P5-R5 Huawei source.");
        }

        string path = Path.Combine(directory.FullName, ".ai-tmp", "workspace", "P5-R5", "huawei-extraction-pass-c", "motion.mp4");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The canonically extracted P5-R5 Huawei motion-video sample is required; this RealSamples test does not skip.", path);
        }
        if (new FileInfo(path).Length != 6_519_404)
        {
            throw new InvalidDataException("The P5-R5 Huawei motion-video sample has an unexpected byte length.");
        }
        using var stream = File.OpenRead(path);
        string sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        if (!string.Equals(sha256, "664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The P5-R5 Huawei motion-video sample identity does not match the frozen canonical extraction.");
        }
        return path;
    }

    [Fact]
    public void ProtocolMediaRequirements_MapsMergeMatricesCorrectly()
    {
        // Google V1 (1): JPG+MP4 (0), JPG+MOV (1)
        var g1Mp4 = ProtocolMediaRequirements.GetMergeRequirement(1, 0);
        Assert.Equal(ImageContainer.Jpeg, g1Mp4.ImageContainer);
        Assert.Equal(VideoContainer.Mp4, g1Mp4.VideoContainer);

        var g1Mov = ProtocolMediaRequirements.GetMergeRequirement(1, 1);
        Assert.Equal(ImageContainer.Jpeg, g1Mov.ImageContainer);
        Assert.Equal(VideoContainer.Mov, g1Mov.VideoContainer);

        // Google V2 (2): HEIC+MOV (3)
        var g2Heic = ProtocolMediaRequirements.GetMergeRequirement(2, 3);
        Assert.Equal(ImageContainer.Heic, g2Heic.ImageContainer);
        Assert.Equal(VideoContainer.Mov, g2Heic.VideoContainer);

        // Huawei (6): HEIC+MP4(H265) (4)
        var hwH265 = ProtocolMediaRequirements.GetMergeRequirement(6, 4);
        Assert.Equal(ImageContainer.Heic, hwH265.ImageContainer);
        Assert.Equal(VideoContainer.Mp4, hwH265.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, hwH265.VideoCodec);
    }

    [Fact]
    public void ProtocolMediaRequirements_MapsSplitMatricesCorrectly()
    {
        var keep = ProtocolMediaRequirements.GetSplitRequirement(0, 0);
        Assert.Equal(ImageContainer.Unknown, keep.ImageContainer);

        var appleJpgMov = ProtocolMediaRequirements.GetSplitRequirement(1, 1);
        Assert.Equal(ImageContainer.Jpeg, appleJpgMov.ImageContainer);
        Assert.Equal(VideoContainer.Mov, appleJpgMov.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, appleJpgMov.VideoCodec);

        var appleHeicMov = ProtocolMediaRequirements.GetSplitRequirement(1, 2);
        Assert.Equal(ImageContainer.Heic, appleHeicMov.ImageContainer);
        Assert.Equal(VideoContainer.Mov, appleHeicMov.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, appleHeicMov.VideoCodec);

        var vivoJpgMp4 = ProtocolMediaRequirements.GetSplitRequirement(2, 3);
        Assert.Equal(ImageContainer.Jpeg, vivoJpgMp4.ImageContainer);
        Assert.Equal(VideoContainer.Mp4, vivoJpgMp4.VideoContainer);
        Assert.Equal(VideoCodec.H264, vivoJpgMp4.VideoCodec);
    }

    [Theory]
    [InlineData(1, 0)] // Apple + Keep -> Invalid
    [InlineData(1, 3)] // Apple + JPG+MP4 -> Invalid
    [InlineData(2, 0)] // vivo + Keep -> Invalid
    [InlineData(2, 1)] // vivo + JPG+MOV -> Invalid
    [InlineData(2, 2)] // vivo + HEIC+MOV -> Invalid
    [InlineData(99, 1)] // Unknown protocol -> Invalid
    [InlineData(1, 99)] // Unknown format -> Invalid
    public void ProtocolMediaRequirements_InvalidSplitCombinations_ThrowsArgumentException(int protocol, int format)
    {
        Assert.Throws<ArgumentException>(() =>
            ProtocolMediaRequirements.GetSplitRequirement(protocol, format));
    }

    [Fact]
    public async Task Probe_NonExistentFile_ThrowsFileNotFoundException()
    {
        var converter = new VideoConverter();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            converter.ProbeAsync(@"C:\non_existent_video_path_xyz123.mp4"));
    }

    [Fact]
    public async Task Probe_IncompleteIsoBmff_ThrowsInsteadOfReturningGuessedFacts()
    {
        using var workspace = new MediaWorkspace();
        string inputPath = workspace.AllocateFilePath("incomplete_video", ".mp4");
        await File.WriteAllBytesAsync(inputPath, [
            0x00, 0x00, 0x00, 0x10, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0x00, 0x00, 0x00, 0x00
        ]);

        var converter = new VideoConverter();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => converter.ProbeAsync(inputPath));

        Assert.Contains("moov/mdat", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Convert_SourceProbeFailure_DoesNotGuessFromArtifact()
    {
        using var workspace = new MediaWorkspace();
        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = @"C:\non_existent_video_path_xyz123.mp4",
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/mp4",
                VideoContainer = VideoContainer.Mov,
                VideoCodec = VideoCodec.Hevc
            },
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.False(result.Success);
        Assert.Contains("Source video probe failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(VideoContainer.Unknown, result.ExecutionRecord.InputContainer);
        Assert.Equal(VideoCodec.Unknown, result.ExecutionRecord.InputCodec);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_WhenCancelled_ThrowsOperationCanceledException()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var converter = new VideoConverter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            converter.ConvertAsync(new VideoConversionRequest
            {
                SourceArtifact = artifact,
                TargetContainer = VideoContainer.Mp4,
                TargetCodec = VideoCodec.H264,
                TargetDirectory = workspace.RootDirectory
            }, cts.Token));

        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }
}
