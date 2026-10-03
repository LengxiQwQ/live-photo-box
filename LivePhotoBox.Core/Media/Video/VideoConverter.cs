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
                    Truth = FailureTruth(requestedOperation, ConversionCapability.Unknown, request, ConversionFactsOrigin.ArtifactDeclaration, ConversionFailureStage.InvalidRequest, ConversionFailureCategory.Unsupported),
                    Duration = sw.Elapsed
                }
            };
        }

        string ext = request.TargetContainer == VideoContainer.Mov ? ".mov" : ".mp4";
        string outPath = Path.Combine(request.TargetDirectory, $"vid-conv-{Guid.NewGuid():N}{ext}");

        VideoFacts? sourceFacts = null;
        NativeMediaService.NativeVideoTranscodeAttempt? backendAttempt = null;
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
                        ConversionCapability.Unknown,
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
            backendAttempt = await NativeMediaService.TranscodeVideoAttemptAsync(
                request.SourceArtifact.Path,
                outPath,
                request.TargetContainer,
                request.TargetCodec,
                request.Crf,
                cancellationToken).ConfigureAwait(false);
            if (!backendAttempt.Succeeded)
            {
                if (backendAttempt.FailureException is OperationCanceledException cancelled)
                {
                    throw cancelled;
                }
                throw new NativeVideoTranscodeAttemptException(backendAttempt);
            }
            NativeMediaService.NativeVideoTranscodeResult backendDiagnostics = backendAttempt.Diagnostics;
            backendCompleted = true;

            if (!backendDiagnostics.IdentityValid)
            {
                throw new InvalidOperationException(
                    "Native video conversion succeeded without a valid versioned backend/profile diagnostic token.");
            }

            bool isRemux = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux;
            bool isMinimalLibav = IsMinimalLibav(backendDiagnostics);
            if (!isRemux && !isMinimalLibav && backendDiagnostics.Backend != VideoBackend.WindowsMediaFoundation)
            {
                throw new InvalidOperationException(
                    "Native video conversion succeeded without identifying a supported backend owner.");
            }

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

            bool byteIdenticalPassthrough = isRemux && await AreFilesByteIdenticalAsync(
                request.SourceArtifact.Path,
                outPath,
                cancellationToken).ConfigureAwait(false);
            ConversionOperationKind actualOperation = !isRemux
                ? ConversionOperationKind.LossyReencode
                : byteIdenticalPassthrough
                    ? ConversionOperationKind.Passthrough
                    : ConversionOperationKind.ContainerRemux;
            PreservationOutcome preservationOutcome = !isRemux
                ? PreservationOutcome.Reencoded
                : byteIdenticalPassthrough
                    ? PreservationOutcome.Preserved
                    : PreservationOutcome.PartiallyPreserved;

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
                    RemuxUsed = isRemux,
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
                        ActualOperationKind = actualOperation,
                        SelectedCapability = CapabilityFor(backendDiagnostics),
                        ActualCapability = CapabilityFor(backendDiagnostics),
                        FactsOrigin = request.TrustedSourceFacts == null ? ConversionFactsOrigin.CompatibilityInspectionOrProbe : ConversionFactsOrigin.CallerTrustedNeutralFacts,
                        PreservationPolicy = request.PreservationPolicy,
                        BackendName = BackendNameFor(backendDiagnostics),
                        BackendVersion = BackendVersionFor(backendDiagnostics),
                        HardwareMode = backendDiagnostics.HardwareMode.ToString(),
                        InputProfile = backendDiagnostics.InputProfile,
                        OutputProfile = backendDiagnostics.OutputProfile,
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
                        // Exact whole-file identity is direct before/after preservation evidence.
                        // A non-identical remux remains partial until independent R5 evidence.
                        PreservationOutcome = preservationOutcome
                    },
                    Duration = sw.Elapsed
                }
            };
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            if (!TryDeleteOutput(outPath, backendAttempt?.Succeeded == true))
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
            bool cleanupSucceeded = TryDeleteOutput(outPath, backendAttempt?.Succeeded == true);
            NativeMediaService.NativeVideoTranscodeAttempt? failedAttempt =
                ex is NativeVideoTranscodeAttemptException attemptException
                    ? attemptException.Attempt
                    : backendAttempt;
            bool nativeAttemptFailed = failedAttempt is { Succeeded: false };
            bool trustedFactsMismatch = ex.Message.StartsWith("Trusted conversion facts", StringComparison.Ordinal);
            ConversionFailureCategory category = !cleanupSucceeded
                ? ConversionFailureCategory.CleanupFailure
                : trustedFactsMismatch
                    ? ConversionFailureCategory.TrustedFactsMismatch
                : nativeAttemptFailed
                    ? ClassifyNativeFailure(failedAttempt!)
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
                ConversionFailureCategory.Unsupported =>
                    failedAttempt?.Diagnostics.OutputCandidateObserved == true
                        ? ConversionFailureStage.OutputValidation
                        : ConversionFailureStage.PolicyRejection,
                ConversionFailureCategory.SourceInspection => ConversionFailureStage.SourceInspection,
                ConversionFailureCategory.TrustedFactsMismatch => ConversionFailureStage.TrustedFactsMismatch,
                _ => ConversionFailureStage.BackendExecution
            };
            NativeMediaService.NativeVideoTranscodeResult? failureDiagnostics = failedAttempt?.Diagnostics;
            string errorMessage = nativeAttemptFailed && !string.IsNullOrWhiteSpace(failedAttempt!.LastError)
                ? failedAttempt.LastError!
                : ex.Message;

            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                ExecutionRecord = new VideoExecutionRecord
                {
                    InputContainer = sourceFacts?.Container ?? VideoContainer.Unknown,
                    InputCodec = sourceFacts?.Codec ?? VideoCodec.Unknown,
                    RequestedContainer = request.TargetContainer,
                    RequestedCodec = request.TargetCodec,
                    OutputContainer = VideoContainer.Unknown,
                    OutputCodec = VideoCodec.Unknown,
                    RemuxUsed = false,
                    Backend = failureDiagnostics?.Backend ?? VideoBackend.Unknown,
                    HardwareMode = failureDiagnostics?.HardwareMode ?? VideoHardwareMode.Unknown,
                    SelectedEncoder = failureDiagnostics?.SelectedEncoder ?? string.Empty,
                    HardwareFallbackOccurred = failureDiagnostics?.HardwareFallbackOccurred ?? false,
                    HardwareFallbackReason = failureDiagnostics?.HardwareFallbackReason ?? string.Empty,
                    AudioPreserved = false,
                    RotationPreserved = false,
                    Truth = FailureTruth(
                        requestedOperation,
                        ConversionCapability.Unknown,
                        request,
                        request.TrustedSourceFacts == null ? ConversionFactsOrigin.CompatibilityInspectionOrProbe : ConversionFactsOrigin.CallerTrustedNeutralFacts,
                        stage,
                        category,
                        failureDiagnostics),
                    Duration = sw.Elapsed
                }
            };
        }
    }

    private static bool TryDeleteOutput(string path, bool converterOwnsArtifact)
    {
        if (!File.Exists(path)) return true;
        if (!converterOwnsArtifact) return false;
        try { File.Delete(path); return !File.Exists(path); }
        catch { return false; }
    }

    private static async Task<bool> AreFilesByteIdenticalAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 64 * 1024;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        long expectedLength = source.Length;
        if (expectedLength != output.Length)
        {
            return false;
        }

        byte[] sourceBuffer = new byte[bufferSize];
        byte[] outputBuffer = new byte[bufferSize];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = (int)Math.Min(sourceBuffer.Length, remaining);
            if (!await ReadFullyAsync(source, sourceBuffer.AsMemory(0, chunkLength), cancellationToken).ConfigureAwait(false) ||
                !await ReadFullyAsync(output, outputBuffer.AsMemory(0, chunkLength), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            if (!sourceBuffer.AsSpan(0, chunkLength).SequenceEqual(outputBuffer.AsSpan(0, chunkLength)))
            {
                return false;
            }
            remaining -= chunkLength;
        }

        return source.Length == expectedLength && output.Length == expectedLength;
    }

    private static async Task<bool> ReadFullyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int bytesRead = await stream.ReadAsync(destination[totalRead..], cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return false;
            }
            totalRead += bytesRead;
        }

        return true;
    }

    private static bool IsBackendUnavailable(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static bool IsMinimalLibav(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        diagnostics.MinimalLibavSelected;

    private static ConversionCapability CapabilityFor(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        diagnostics.Backend == VideoBackend.ProjectIsoBmffRemux
            ? ConversionCapability.VideoRemux
            : IsMinimalLibav(diagnostics)
                ? ConversionCapability.VideoTranscodeHdr10Bit
                : diagnostics.Backend == VideoBackend.WindowsMediaFoundation
                    ? ConversionCapability.VideoTranscodeSdr
                    : ConversionCapability.Unknown;

    private static string BackendNameFor(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        IsMinimalLibav(diagnostics) ? "minimal-libav" : diagnostics.Backend.ToString();

    private static string BackendVersionFor(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        IsMinimalLibav(diagnostics) ? diagnostics.BackendVersion : string.Empty;

    private static ConversionFailureCategory ClassifyNativeFailure(NativeMediaService.NativeVideoTranscodeAttempt attempt)
    {
        string error = attempt.LastError ?? attempt.FailureException?.Message ?? string.Empty;
        if (error.StartsWith("Unsupported:", StringComparison.Ordinal))
        {
            return ConversionFailureCategory.Unsupported;
        }
        if (error.StartsWith("BackendUnavailable:", StringComparison.Ordinal))
        {
            return ConversionFailureCategory.BackendUnavailable;
        }
        if (error.StartsWith("OutputValidation:", StringComparison.Ordinal))
        {
            return ConversionFailureCategory.OutputValidation;
        }
        if (error.StartsWith("CleanupFailure:", StringComparison.Ordinal))
        {
            return ConversionFailureCategory.CleanupFailure;
        }
        if (attempt.ResultCode == NativeResult.InvalidArgument &&
            (error.Contains("failed closed", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("ambiguous", StringComparison.OrdinalIgnoreCase) ||
             error.Contains("unsupported", StringComparison.OrdinalIgnoreCase)))
        {
            return ConversionFailureCategory.SourceInspection;
        }
        return ConversionFailureCategory.BackendFailure;
    }

    private static ConversionExecutionTruth FailureTruth(
        ConversionOperationKind requested,
        ConversionCapability capability,
        VideoConversionRequest request,
        ConversionFactsOrigin origin,
        ConversionFailureStage stage,
        ConversionFailureCategory category,
        NativeMediaService.NativeVideoTranscodeResult? diagnostics = null) => new()
    {
        RequestedOperationKind = requested,
        ActualOperationKind = category == ConversionFailureCategory.Unsupported ? ConversionOperationKind.Unsupported : ConversionOperationKind.Unknown,
        SelectedCapability = diagnostics is null ? capability : CapabilityFor(diagnostics),
        ActualCapability = ConversionCapability.Unknown,
        FactsOrigin = origin,
        PreservationPolicy = request.PreservationPolicy,
        BackendName = diagnostics is null ? "Unknown" : BackendNameFor(diagnostics),
        BackendVersion = diagnostics is null ? string.Empty : BackendVersionFor(diagnostics),
        HardwareMode = diagnostics?.HardwareMode.ToString() ?? "Unknown",
        FallbackOccurred = diagnostics?.HardwareFallbackOccurred ?? false,
        FallbackReason = diagnostics?.HardwareFallbackReason,
        Pixel = ConversionComponentOutcome.NotApplicable,
        Metadata = ConversionComponentOutcome.NotEvaluated,
        ColorIcc = ConversionComponentOutcome.NotEvaluated,
        HdrGainMap = ConversionComponentOutcome.NotApplicable,
        Auxiliary = ConversionComponentOutcome.NotApplicable,
        Audio = ConversionComponentOutcome.NotEvaluated,
        Timing = ConversionComponentOutcome.NotEvaluated,
        Orientation = ConversionComponentOutcome.NotEvaluated,
        InputProfile = diagnostics?.InputProfile ?? "Unknown",
        OutputProfile = diagnostics?.OutputProfile ?? "Unknown",
        PreservationOutcome = PreservationOutcome.Unsupported,
        FailureStage = stage,
        FailureCategory = category
    };

    private sealed class NativeVideoTranscodeAttemptException : Exception
    {
        public NativeVideoTranscodeAttemptException(NativeMediaService.NativeVideoTranscodeAttempt attempt)
            : base(attempt.LastError ?? attempt.FailureException?.Message ?? $"Native video conversion failed with {attempt.ResultCode}.")
        {
            Attempt = attempt;
        }

        public NativeMediaService.NativeVideoTranscodeAttempt Attempt { get; }
    }
}
