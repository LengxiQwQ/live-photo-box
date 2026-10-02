using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Video;

/// <summary>
/// Thin control plane wrapper that delegates video probing, stream remuxing, and transcoding to LivePhotoBox.Native.
/// </summary>
public sealed class VideoConverter : IVideoConverter
{
    public Task<VideoFacts> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        return NativeMediaService.ProbeVideoAsync(videoPath, cancellationToken);
    }

    public async Task<VideoConversionResult> ConvertAsync(
        VideoConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        ConversionOperationKind requestedOperation = request.TargetFps > 0
            ? ConversionOperationKind.Unsupported
            : request.TargetCodec == VideoCodec.Copy ? ConversionOperationKind.ContainerRemux : ConversionOperationKind.LossyReencode;

        // TargetFps handling
        if (request.TargetFps > 0)
        {
            sw.Stop();
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = "Custom TargetFps is not supported in the current media pipeline stage.",
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = request.SourceArtifact.VideoContainer,
                    InputCodec = request.SourceArtifact.VideoCodec,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = request.TargetContainer,
                    OutputCodec = request.TargetCodec,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Truth = FailureTruth(requestedOperation, ConversionCapability.VideoTranscodeSdr, request, ConversionFactsOrigin.ArtifactDeclaration, ConversionFailureStage.InvalidRequest, ConversionFailureCategory.Unsupported),
                    Duration = sw.Elapsed
                }
            };
        }

        string ext = request.TargetContainer == VideoContainer.Mov ? ".mov" : ".mp4";
        string outPath = Path.Combine(request.TargetDirectory, $"vid-conv-{Guid.NewGuid():N}{ext}");

        VideoFacts? sourceFacts = null;
        bool backendCompleted = false;
        try
        {
            sourceFacts = await ProbeAsync(request.SourceArtifact.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            ConversionFailureCategory category = IsBackendUnavailable(ex)
                ? ConversionFailureCategory.BackendUnavailable
                : ConversionFailureCategory.SourceInspection;
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = $"Source video probe failed: {ex.Message}",
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = VideoContainer.Unknown,
                    InputCodec = VideoCodec.Unknown,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = VideoContainer.Unknown,
                    OutputCodec = VideoCodec.Unknown,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Truth = FailureTruth(
                        requestedOperation,
                        ConversionCapability.VideoTranscodeSdr,
                        request,
                        ConversionFactsOrigin.CompatibilityInspectionOrProbe,
                        category == ConversionFailureCategory.BackendUnavailable
                            ? ConversionFailureStage.BackendUnavailable
                            : ConversionFailureStage.SourceInspection,
                        category),
                    Duration = sw.Elapsed
                }
            };
        }

        try
        {
            if (request.TrustedSourceFacts is { } trusted &&
                (trusted.Container != sourceFacts.Container || trusted.Codec != sourceFacts.Codec))
            {
                throw new InvalidOperationException("Trusted conversion facts disagree with the probed source video; refusing conversion.");
            }
            // Transcode or Remux via Native
            NativeMediaService.NativeVideoTranscodeResult backendDiagnostics = await NativeMediaService.TranscodeVideoWithDiagnosticsAsync(
                request.SourceArtifact.Path,
                outPath,
                request.TargetContainer,
                request.TargetCodec,
                request.Crf,
                cancellationToken).ConfigureAwait(false);
            backendCompleted = true;

            sw.Stop();

            if (!File.Exists(outPath))
            {
                throw new FileNotFoundException("Converted output video was not found.", outPath);
            }

            // Re-probe actual output file to validate facts
            VideoFacts probed = await ProbeAsync(outPath, cancellationToken).ConfigureAwait(false);

            if (request.TargetContainer != VideoContainer.Unknown && probed.Container != request.TargetContainer)
            {
                throw new InvalidOperationException(
                    $"Output container mismatch: expected {request.TargetContainer}, actual {probed.Container}.");
            }

            if (request.TargetCodec != VideoCodec.Copy && request.TargetCodec != VideoCodec.Unknown && probed.Codec != request.TargetCodec)
            {
                throw new InvalidOperationException(
                    $"Output codec mismatch: expected {request.TargetCodec}, actual {probed.Codec}.");
            }

            if (probed.DurationSeconds <= 0)
            {
                throw new InvalidOperationException("Output video duration could not be determined or is zero.");
            }

            // Duration tolerance check: catch noticeable truncations
            if (sourceFacts.DurationSeconds > 0)
            {
                double durationTolerance = Math.Max(0.5, sourceFacts.DurationSeconds * 0.15);
                if (Math.Abs(probed.DurationSeconds - sourceFacts.DurationSeconds) > durationTolerance)
                {
                    throw new InvalidOperationException(
                        $"Output video duration discrepancy/truncation detected: expected ~{sourceFacts.DurationSeconds:F2}s, actual {probed.DurationSeconds:F2}s.");
                }
            }

            bool audioPreserved = sourceFacts.HasAudio ? probed.HasAudio : true;
            bool rotationPreserved = sourceFacts.RotationDegrees == probed.RotationDegrees;
            if (!audioPreserved)
            {
                throw new InvalidOperationException("Output video lost a source audio stream.");
            }
            if (!rotationPreserved)
            {
                throw new InvalidOperationException(
                    $"Output video rotation mismatch: expected {sourceFacts.RotationDegrees}, actual {probed.RotationDegrees}.");
            }

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = probed.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                VideoContainer = probed.Container,
                VideoCodec = probed.Codec,
                ByteLength = new FileInfo(outPath).Length
            };

            return new VideoConversionResult
            {
                Success = true,
                OutputArtifact = outArtifact,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = sourceFacts.Container,
                    InputCodec = sourceFacts.Codec,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = probed.Container,
                    OutputCodec = probed.Codec,
                    RemuxUsed = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux,
                    Backend = backendDiagnostics.Backend,
                    HardwareMode = backendDiagnostics.HardwareMode,
                    SelectedEncoder = backendDiagnostics.SelectedEncoder,
                    HardwareFallbackOccurred = backendDiagnostics.HardwareFallbackOccurred,
                    HardwareFallbackReason = backendDiagnostics.HardwareFallbackReason,
                    AudioPreserved = audioPreserved,
                    RotationPreserved = rotationPreserved,
                    Truth = new ConversionExecutionTruth
                    {
                        RequestedOperationKind = requestedOperation,
                        ActualOperationKind = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux ? ConversionOperationKind.ContainerRemux : ConversionOperationKind.LossyReencode,
                        SelectedCapability = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux ? ConversionCapability.VideoRemux : ConversionCapability.VideoTranscodeSdr,
                        ActualCapability = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux ? ConversionCapability.VideoRemux : ConversionCapability.VideoTranscodeSdr,
                        FactsOrigin = request.TrustedSourceFacts == null ? ConversionFactsOrigin.CompatibilityInspectionOrProbe : ConversionFactsOrigin.CallerTrustedNeutralFacts,
                        PreservationPolicy = request.PreservationPolicy,
                        BackendName = backendDiagnostics.Backend.ToString(),
                        HardwareMode = backendDiagnostics.HardwareMode.ToString(),
                        FallbackOccurred = backendDiagnostics.HardwareFallbackOccurred,
                        FallbackReason = backendDiagnostics.HardwareFallbackReason,
                        Pixel = ConversionComponentOutcome.NotApplicable,
                        Metadata = ConversionComponentOutcome.NotEvaluated,
                        ColorIcc = ConversionComponentOutcome.NotEvaluated,
                        HdrGainMap = ConversionComponentOutcome.NotApplicable,
                        Auxiliary = ConversionComponentOutcome.NotApplicable,
                        // Existing probe checks remain compatibility diagnostics; R1 does not
                        // promote them into component-preservation evidence.
                        Audio = ConversionComponentOutcome.NotEvaluated,
                        Timing = ConversionComponentOutcome.NotEvaluated,
                        Orientation = ConversionComponentOutcome.NotEvaluated,
                        // R5 owns the complete independent video preservation proof.
                        // A successful remux is not enough for Strict to claim every
                        // deferred component was preserved.
                        PreservationOutcome = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux
                            ? PreservationOutcome.PartiallyPreserved
                            : PreservationOutcome.Reencoded
                    },
                    Duration = sw.Elapsed
                }
            };
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            if (!TryDeleteOutput(outPath))
            {
                throw new IOException(
                    "Video conversion was cancelled, but its staged output could not be removed.",
                    ex);
            }
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            bool cleanupSucceeded = TryDeleteOutput(outPath);
            ConversionFailureCategory category = !cleanupSucceeded
                ? ConversionFailureCategory.CleanupFailure
                : IsBackendUnavailable(ex)
                    ? ConversionFailureCategory.BackendUnavailable
                : backendCompleted && ex is FileNotFoundException
                    ? ConversionFailureCategory.OutputMissing
                    : backendCompleted && ex is InvalidOperationException
                        ? ConversionFailureCategory.OutputValidation
                        : ConversionFailureCategory.BackendFailure;
            ConversionFailureStage stage = category switch
            {
                ConversionFailureCategory.CleanupFailure => ConversionFailureStage.Cleanup,
                ConversionFailureCategory.OutputMissing or ConversionFailureCategory.OutputValidation => ConversionFailureStage.OutputValidation,
                ConversionFailureCategory.BackendUnavailable => ConversionFailureStage.BackendUnavailable,
                _ => ConversionFailureStage.BackendExecution
            };

            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = sourceFacts?.Container ?? VideoContainer.Unknown,
                    InputCodec = sourceFacts?.Codec ?? VideoCodec.Unknown,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = request.TargetContainer,
                    OutputCodec = request.TargetCodec,
                    RemuxUsed = false,
                    SelectedEncoder = string.Empty,
                    HardwareFallbackOccurred = false,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Truth = FailureTruth(requestedOperation, request.TargetCodec == VideoCodec.Copy ? ConversionCapability.VideoRemux : ConversionCapability.VideoTranscodeSdr, request, request.TrustedSourceFacts == null ? ConversionFactsOrigin.CompatibilityInspectionOrProbe : ConversionFactsOrigin.CallerTrustedNeutralFacts, ex.Message.StartsWith("Trusted conversion facts", StringComparison.Ordinal) ? ConversionFailureStage.TrustedFactsMismatch : stage, ex.Message.StartsWith("Trusted conversion facts", StringComparison.Ordinal) ? ConversionFailureCategory.TrustedFactsMismatch : category),
                    Duration = sw.Elapsed
                }
            };
        }
    }

    private static bool TryDeleteOutput(string path)
    {
        if (!File.Exists(path)) return true;
        try { File.Delete(path); return !File.Exists(path); }
        catch { return false; }
    }

    private static bool IsBackendUnavailable(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static ConversionExecutionTruth FailureTruth(ConversionOperationKind requested, ConversionCapability capability, VideoConversionRequest request, ConversionFactsOrigin origin, ConversionFailureStage stage, ConversionFailureCategory category) => new()
    {
        RequestedOperationKind = requested,
        ActualOperationKind = category == ConversionFailureCategory.Unsupported ? ConversionOperationKind.Unsupported : ConversionOperationKind.Unknown,
        SelectedCapability = capability,
        ActualCapability = ConversionCapability.Unknown,
        FactsOrigin = origin,
        PreservationPolicy = request.PreservationPolicy,
        BackendName = "LivePhotoBox.Native",
        HardwareMode = "Unknown",
        Pixel = ConversionComponentOutcome.NotApplicable,
        Metadata = ConversionComponentOutcome.NotEvaluated,
        ColorIcc = ConversionComponentOutcome.NotEvaluated,
        HdrGainMap = ConversionComponentOutcome.NotApplicable,
        Auxiliary = ConversionComponentOutcome.NotApplicable,
        Audio = ConversionComponentOutcome.NotEvaluated,
        Timing = ConversionComponentOutcome.NotEvaluated,
        Orientation = ConversionComponentOutcome.NotEvaluated,
        PreservationOutcome = PreservationOutcome.PartiallyPreserved,
        FailureStage = stage,
        FailureCategory = category
    };
}
