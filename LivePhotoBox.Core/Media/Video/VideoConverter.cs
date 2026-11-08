using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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

    public async Task<VideoConversionResult> ConvertAsync(VideoConversionRequest request, CancellationToken cancellationToken = default)
    {
        var result = await TryConvertAsync(request,cancellationToken).ConfigureAwait(false);
        if (result.ExecutionRecord.Truth.FailureCategory == ConversionFailureCategory.Cancelled)
            throw new OperationCanceledException(cancellationToken);
        return result;
    }

    internal async Task<VideoConversionResult> TryConvertAsync(
        VideoConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
        var probe = await NativeMediaService.TryProbeVideoAsync(request.SourceArtifact.Path, cancellationToken).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            // Preserve the existing cancellation contract until the caller's
            // end-to-end result migration is complete; never label cancellation
            // as a malformed source video.
            if (probe.Result == NativeResult.Cancelled) return CancelledResult();
            sw.Stop();
            var category = ConversionFailureCategory.SourceInspection;
            return new VideoConversionResult
            {
                Success = false,
                ErrorMessage = $"Source video probe failed: {probe.Diagnostic}",
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
        sourceFacts = probe.Facts!;

        try
        {
            if (request.TrustedSourceFacts is { } trusted &&
                (trusted.Container != sourceFacts.Container || trusted.Codec != sourceFacts.Codec))
            {
                return FailureResult(new InvalidOperationException("Trusted conversion facts disagree with the probed source video; refusing conversion."), ConversionFailureCategory.TrustedFactsMismatch);
            }
            // Transcode or Remux via Native
            backendAttempt = await NativeMediaService.TranscodeVideoAttemptAsync(
                request.SourceArtifact.Path,
                outPath,
                request.TargetContainer,
                request.TargetCodec,
                request.Crf,
                cancellationToken, request.PreservationPolicy,
                keepVideoStabilizationEdges: request.KeepVideoStabilizationEdges).ConfigureAwait(false);
            if (!backendAttempt.Succeeded)
            {
                if (backendAttempt.ResultCode == NativeResult.Cancelled) return CancelledResult();
                return FailureResult(new NativeVideoTranscodeAttemptException(backendAttempt));
            }
            if (backendAttempt.OutputEvidence == null)
            {
                return FailureResult(new InvalidDataException(
                    "Native video conversion did not return exact creating-handle output evidence: " +
                    (backendAttempt.OutputEvidenceFailure?.Message ?? "unknown evidence failure")));
            }
            if (request.PreservationPolicy == PreservationPolicy.Strict && backendAttempt.MetadataPreservation is null)
                return FailureResult(new InvalidDataException("Native video conversion did not return verified scoped metadata evidence."));
            NativeMediaService.NativeVideoTranscodeResult backendDiagnostics = backendAttempt.Diagnostics;
            backendCompleted = true;

            if (!backendDiagnostics.IdentityValid)
            {
                return FailureResult(new InvalidOperationException(
                    "Native video conversion succeeded without a valid versioned backend/profile diagnostic token."));
            }

            bool isRemux = backendDiagnostics.Backend == VideoBackend.ProjectIsoBmffRemux;
            bool isMinimalLibav = IsMinimalLibav(backendDiagnostics);
            if (!isRemux && !isMinimalLibav && backendDiagnostics.Backend != VideoBackend.WindowsMediaFoundation)
            {
                return FailureResult(new InvalidOperationException(
                    "Native video conversion succeeded without identifying a supported backend owner."));
            }

            sw.Stop();

            if (!File.Exists(outPath))
            {
                return FailureResult(new FileNotFoundException("Converted output video was not found.", outPath));
            }

            // Re-probe actual output file to validate facts
            var outputProbe = await NativeMediaService.TryProbeVideoAsync(outPath, cancellationToken).ConfigureAwait(false);
            if (!outputProbe.Succeeded)
                return outputProbe.Result == NativeResult.Cancelled ? CancelledResult()
                    : FailureResult(new InvalidDataException(outputProbe.Diagnostic ?? "Output video probe failed."));
            VideoFacts probed = outputProbe.Facts!;

            if (request.TargetContainer != VideoContainer.Unknown && probed.Container != request.TargetContainer)
            {
                return FailureResult(new InvalidOperationException(
                    $"Output container mismatch: expected {request.TargetContainer}, actual {probed.Container}."));
            }

            if (request.TargetCodec != VideoCodec.Copy && request.TargetCodec != VideoCodec.Unknown && probed.Codec != request.TargetCodec)
            {
                return FailureResult(new InvalidOperationException(
                    $"Output codec mismatch: expected {request.TargetCodec}, actual {probed.Codec}."));
            }

            if (probed.DurationSeconds <= 0)
            {
                return FailureResult(new InvalidOperationException("Output video duration could not be determined or is zero."));
            }

            // Duration tolerance check: catch noticeable truncations
            if (sourceFacts.DurationSeconds > 0)
            {
                double durationTolerance = Math.Max(0.5, sourceFacts.DurationSeconds * 0.15);
                if (Math.Abs(probed.DurationSeconds - sourceFacts.DurationSeconds) > durationTolerance)
                {
                    return FailureResult(new InvalidOperationException(
                        $"Output video duration discrepancy/truncation detected: expected ~{sourceFacts.DurationSeconds:F2}s, actual {probed.DurationSeconds:F2}s."));
                }
            }

            bool audioPreserved = sourceFacts.HasAudio ? probed.HasAudio : true;
            bool retainCodedArea = request.KeepVideoStabilizationEdges || request.PreservationPolicy == PreservationPolicy.Strict;
            uint frameWidth = sourceFacts.Width - (retainCodedArea ? 0 : sourceFacts.CropLeft + sourceFacts.CropRight);
            uint frameHeight = sourceFacts.Height - (retainCodedArea ? 0 : sourceFacts.CropTop + sourceFacts.CropBottom);
            bool dimensionsCorrect = (sourceFacts.RotationDegrees is 90 or 270)
                ? (probed.Width == frameHeight && probed.Height == frameWidth)
                : (probed.Width == frameWidth && probed.Height == frameHeight);
            bool rotationPreserved = isRemux
                ? (sourceFacts.RotationDegrees == probed.RotationDegrees && sourceFacts.Reflection == probed.Reflection && probed.Width == sourceFacts.Width && probed.Height == sourceFacts.Height)
                : (probed.RotationDegrees == 0 && probed.Reflection == 0 && dimensionsCorrect);
            if (!audioPreserved)
            {
                return FailureResult(new InvalidOperationException("Output video lost a source audio stream."));
            }
            if (!rotationPreserved)
            {
                return FailureResult(new InvalidOperationException(
                    $"Output video rotation mismatch: expected {sourceFacts.RotationDegrees}, actual {probed.RotationDegrees}."));
            }

            bool byteIdenticalPassthrough = isRemux && await AreFilesByteIdenticalAsync(
                request.SourceArtifact.Path,
                outPath,
                cancellationToken).ConfigureAwait(false);
            var completeProof = backendAttempt.CompleteRemuxProof;
            bool provedCopy = false;
            if (completeProof is not null)
            {
                await using var input = File.OpenRead(request.SourceArtifact.Path);
                string actualInputHash = Convert.ToHexString(await SHA256.HashDataAsync(input,cancellationToken).ConfigureAwait(false));
                if (!isRemux || request.TargetCodec != VideoCodec.Copy ||
                    completeProof.SourceLength != (ulong)input.Length ||
                    !string.Equals(completeProof.SourceSha256,actualInputHash,StringComparison.OrdinalIgnoreCase) ||
                    (request.SourceArtifact.Sha256 is { } declared && !string.Equals(declared,actualInputHash,StringComparison.OrdinalIgnoreCase)))
                    return FailureResult(new InvalidDataException("Complete remux proof does not bind the actual original Copy input."));
                provedCopy = true;
            }
            ConversionOperationKind actualOperation = !isRemux
                ? (backendDiagnostics.StreamCopied ? ConversionOperationKind.ContainerRemux : ConversionOperationKind.LossyReencode)
                : byteIdenticalPassthrough
                    ? ConversionOperationKind.Passthrough
                    : ConversionOperationKind.ContainerRemux;
            PreservationOutcome preservationOutcome = backendDiagnostics.HdrDegradedToSdr
                ? PreservationOutcome.DegradedToSdr : !isRemux
                ? (backendDiagnostics.StreamCopied ? PreservationOutcome.PartiallyPreserved : PreservationOutcome.Reencoded)
                : byteIdenticalPassthrough || provedCopy
                    ? PreservationOutcome.Preserved
                    : PreservationOutcome.PartiallyPreserved;

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = probed.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                VideoContainer = probed.Container,
                VideoCodec = probed.Codec,
                ByteLength = backendAttempt.OutputEvidence.ByteLength,
                Sha256 = backendAttempt.OutputEvidence.Sha256,
                FileIdentity = backendAttempt.OutputEvidence.FileIdentity
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
                    RemuxUsed = isRemux || backendDiagnostics.StreamCopied,
                    DroppedAuxVideoTracks = backendDiagnostics.DroppedAuxVideo,
                    DroppedMebxTracks = backendDiagnostics.DroppedMebx,
                    DroppedOtherTracks = backendDiagnostics.DroppedOther,
                    AudioTranscodedToAac = backendDiagnostics.AudioTranscodedToAac,
                    StreamCopied = backendDiagnostics.StreamCopied,
                    Backend = backendDiagnostics.Backend,
                    HardwareMode = backendDiagnostics.HardwareMode,
                    SelectedEncoder = backendDiagnostics.SelectedEncoder,
                    HardwareFallbackOccurred = backendDiagnostics.HardwareFallbackOccurred,
                    HardwareFallbackReason = backendDiagnostics.HardwareFallbackReason,
                    MetadataPreservation = backendAttempt.MetadataPreservation,
                    CompleteRemuxProof = completeProof,
                    AudioPreserved = audioPreserved && !backendDiagnostics.AudioTranscodedToAac,
                    RotationPreserved = rotationPreserved,
                    DisplayArea = sourceFacts.HasDisplayAperture && request.PreservationPolicy != PreservationPolicy.Strict
                        ? new VideoDisplayAreaExecution {
                            SourceWidth=sourceFacts.Width,SourceHeight=sourceFacts.Height,
                            OutputWidth=probed.Width,OutputHeight=probed.Height,
                            CropTop=sourceFacts.CropTop,CropBottom=sourceFacts.CropBottom,
                            CropLeft=sourceFacts.CropLeft,CropRight=sourceFacts.CropRight,
                            KeptStabilizationEdges=request.KeepVideoStabilizationEdges } : null,
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
                        FallbackAllowed = backendDiagnostics.HardwareFallbackOccurred,
                        FallbackOccurred = backendDiagnostics.HardwareFallbackOccurred,
                        FallbackReason = backendDiagnostics.HardwareFallbackReason,
                        Pixel = ConversionComponentOutcome.NotApplicable,
                        Metadata = backendAttempt.MetadataPreservation != null
                            ? ConversionComponentOutcome.Preserved
                            : ConversionComponentOutcome.Degraded,
                        ColorIcc = provedCopy ? ConversionComponentOutcome.Preserved : ConversionComponentOutcome.NotEvaluated,
                        HdrGainMap = backendDiagnostics.HdrDegradedToSdr
                            ? ConversionComponentOutcome.Degraded : ConversionComponentOutcome.NotApplicable,
                        Auxiliary = ConversionComponentOutcome.NotApplicable,
                        // Existing probe checks remain compatibility diagnostics; R1 does not
                        // promote them into component-preservation evidence.
                        Audio = !sourceFacts.HasAudio ? ConversionComponentOutcome.NotApplicable
                            : backendDiagnostics.AudioTranscodedToAac ? ConversionComponentOutcome.Reencoded
                            : audioPreserved ? ConversionComponentOutcome.Preserved : ConversionComponentOutcome.Lost,
                        Timing = provedCopy ? ConversionComponentOutcome.Preserved : ConversionComponentOutcome.NotEvaluated,
                        Orientation = provedCopy ? ConversionComponentOutcome.Preserved : ConversionComponentOutcome.NotEvaluated,
                        // Whole-file identity or the versioned, input/output-bound
                        // original Copy proof is required; static-only evidence
                        // and a backend name never promote a partial remux.
                        PreservationOutcome = preservationOutcome
                    },
                    Duration = sw.Elapsed
                }
            };
        }
        catch (OperationCanceledException ex)
        {
            sw.Stop();
            if (!TryDeleteOutput(outPath, backendAttempt?.OutputEvidence))
            {
                return FailureResult(new IOException("Video conversion was cancelled, but its staged output could not be removed.",ex));
            }
            return CancelledResult();
        }
        catch (Exception ex)
        {
            return FailureResult(ex);
        }

        VideoConversionResult CancelledResult() => new()
        {
            Success=false, ErrorMessage="Video conversion cancelled.",
            ExecutionRecord=new VideoExecutionRecord {
                InputContainer=sourceFacts?.Container ?? VideoContainer.Unknown,
                InputCodec=sourceFacts?.Codec ?? VideoCodec.Unknown,
                RequestedContainer=request.TargetContainer,RequestedCodec=request.TargetCodec,
                OutputContainer=VideoContainer.Unknown,OutputCodec=VideoCodec.Unknown,
                SelectedEncoder=string.Empty,
                Truth=FailureTruth(requestedOperation,ConversionCapability.Unknown,request,
                    ConversionFactsOrigin.CompatibilityInspectionOrProbe,ConversionFailureStage.Cancelled,
                    ConversionFailureCategory.Cancelled),Duration=sw.Elapsed }
        };

        VideoConversionResult FailureResult(Exception ex, ConversionFailureCategory? knownCategory = null)
        {
            sw.Stop();
            bool cleanupSucceeded = TryDeleteOutput(outPath, backendAttempt?.OutputEvidence);
            NativeMediaService.NativeVideoTranscodeAttempt? failedAttempt =
                ex is NativeVideoTranscodeAttemptException attemptException
                    ? attemptException.Attempt
                    : backendAttempt;
            bool nativeAttemptFailed = failedAttempt is { Succeeded: false };
            ConversionFailureCategory category = !cleanupSucceeded
                ? ConversionFailureCategory.CleanupFailure
                : knownCategory.HasValue ? knownCategory.Value
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

    private static bool TryDeleteOutput(
        string path,
        NativeMediaService.NativeRegisteredOutputEvidence? expected) =>
        NativeMediaService.TryDeleteRegisteredOutput(path, expected);

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
        diagnostics.MinimalLibavSelected || diagnostics.Backend == VideoBackend.MinimalLibav;

    private static ConversionCapability CapabilityFor(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        diagnostics.Backend == VideoBackend.ProjectIsoBmffRemux
            ? ConversionCapability.VideoRemux
            : IsMinimalLibav(diagnostics)
                ? !diagnostics.IdentityValid || diagnostics.InputProfile == "Unknown"
                    ? ConversionCapability.Unknown
                    : diagnostics.InputRequiresHighPrecision && !diagnostics.HdrDegradedToSdr
                        ? ConversionCapability.VideoTranscodeHdr10Bit
                        : ConversionCapability.VideoTranscodeSdr
                : diagnostics.Backend == VideoBackend.WindowsMediaFoundation
                    ? ConversionCapability.VideoTranscodeSdr
                    : ConversionCapability.Unknown;

    private static string BackendNameFor(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        IsMinimalLibav(diagnostics) ? "minimal-libav" : diagnostics.Backend.ToString();

    private static string BackendVersionFor(NativeMediaService.NativeVideoTranscodeResult diagnostics) =>
        IsMinimalLibav(diagnostics) ? diagnostics.BackendVersion : string.Empty;

    private static ConversionFailureCategory ClassifyNativeFailure(NativeMediaService.NativeVideoTranscodeAttempt attempt)
    {
        if (attempt.FailureException is LivePhotoBox.Media.Extraction.ExtractionException
            { Category: LivePhotoBox.Media.Extraction.ExtractionFailureCategory.CleanupFailed })
            return ConversionFailureCategory.CleanupFailure;
        return attempt.ResultCode switch
        {
            NativeResult.Cancelled => ConversionFailureCategory.Cancelled,
            NativeResult.Unsupported => ConversionFailureCategory.Unsupported,
            NativeResult.InvalidArgument or NativeResult.SourceChanged => ConversionFailureCategory.SourceInspection,
            NativeResult.AbiMismatch => ConversionFailureCategory.BackendUnavailable,
            _ => ConversionFailureCategory.BackendFailure
        };
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
        FallbackAllowed = diagnostics?.HardwareFallbackOccurred ?? false,
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
