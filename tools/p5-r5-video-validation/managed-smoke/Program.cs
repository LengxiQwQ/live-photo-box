using System.Text.Json;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;

if (args.Length != 3 || !string.Equals(args[0], "missing-sidecar", StringComparison.Ordinal))
{
    throw new ArgumentException("Usage: P5R5.ManagedSmoke missing-sidecar <canonical-huawei-motion.mp4> <output-directory>");
}

string inputPath = Path.GetFullPath(args[1]);
string outputDirectory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outputDirectory);
string outputPath = Path.Combine(outputDirectory, "huawei-missing-sidecar.mp4");
if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
{
    throw new IOException("The managed missing-sidecar output directory must be empty before the real runtime attempt.");
}

VideoFacts inputFacts = await new VideoConverter().ProbeAsync(inputPath).ConfigureAwait(false);
VideoConversionResult result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
{
    SourceArtifact = new MediaArtifact
    {
        Path = inputPath,
        Kind = MediaArtifactKind.MotionVideo,
        MimeType = "video/mp4",
        VideoContainer = inputFacts.Container,
        VideoCodec = inputFacts.Codec,
        ByteLength = new FileInfo(inputPath).Length
    },
    TargetContainer = VideoContainer.Mp4,
    TargetCodec = VideoCodec.Hevc,
    TargetDirectory = outputDirectory,
    PreservationPolicy = PreservationPolicy.Strict
}).ConfigureAwait(false);

ConversionExecutionTruth truth = result.ExecutionRecord.Truth;
bool residualOwnedOutput = Directory.EnumerateFileSystemEntries(outputDirectory)
    .Any(path => Path.GetFileName(path).StartsWith("vid-conv-", StringComparison.Ordinal) ||
                 Path.GetFileName(path).StartsWith("lpb-video-sidecar-", StringComparison.Ordinal) ||
                 Path.GetFileName(path).StartsWith("lpb-transcode-", StringComparison.Ordinal));
if (result.Success || result.OutputArtifact is not null || File.Exists(outputPath) || residualOwnedOutput ||
    truth.FailureCategory != ConversionFailureCategory.BackendUnavailable ||
    truth.FailureStage != ConversionFailureStage.BackendUnavailable ||
    truth.SelectedCapability != ConversionCapability.VideoTranscodeHdr10Bit ||
    truth.ActualOperationKind != ConversionOperationKind.Unknown ||
    truth.PreservationOutcome != PreservationOutcome.Unsupported ||
    !string.Equals(truth.BackendName, "minimal-libav", StringComparison.Ordinal) ||
    !string.Equals(truth.BackendVersion, "not-packaged", StringComparison.Ordinal) ||
    !truth.InputProfile.StartsWith("HEVC;", StringComparison.Ordinal) ||
    !string.Equals(truth.OutputProfile, "Unknown", StringComparison.Ordinal) ||
    truth.FallbackOccurred || result.ExecutionRecord.HardwareFallbackOccurred ||
    result.ExecutionRecord.Backend != VideoBackend.Unknown ||
    result.ExecutionRecord.HardwareMode != VideoHardwareMode.SoftwareForced ||
    !result.ExecutionRecord.SelectedEncoder.StartsWith("minimal-libav/P5-R5-v1", StringComparison.Ordinal) ||
    !result.ErrorMessage!.StartsWith("BackendUnavailable:", StringComparison.Ordinal))
{
    throw new InvalidDataException("The managed missing-sidecar attempt did not report the selected HDR owner truthfully or leave the output directory clean.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    success = result.Success,
    error = result.ErrorMessage,
    failureCategory = truth.FailureCategory.ToString(),
    failureStage = truth.FailureStage.ToString(),
    actualOperation = truth.ActualOperationKind.ToString(),
    selectedCapability = truth.SelectedCapability.ToString(),
    preservationOutcome = truth.PreservationOutcome.ToString(),
    backend = truth.BackendName,
    backendVersion = truth.BackendVersion,
    inputProfile = truth.InputProfile,
    outputProfile = truth.OutputProfile,
    nativeBackendEnum = result.ExecutionRecord.Backend.ToString(),
    hardwareMode = result.ExecutionRecord.HardwareMode.ToString(),
    selectedEncoder = result.ExecutionRecord.SelectedEncoder,
    fallbackOccurred = truth.FallbackOccurred,
    outputExists = File.Exists(outputPath),
    residualOwnedOutput
}));
