using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Media.Image;

/// <summary>
/// Thin control plane wrapper that delegates image conversions to LivePhotoBox.Native.
/// </summary>
public sealed class ImageConverter : IImageConverter
{
    public async Task<ImageConversionResult> ConvertAsync(
        ImageConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await TryConvertAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.ExecutionRecord?.Truth.FailureCategory == ConversionFailureCategory.Cancelled)
            throw new OperationCanceledException(result.ErrorMessage, cancellationToken);
        return result;
    }

    internal async Task<ImageConversionResult> TryConvertAsync(
        ImageConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        ConversionOperationKind requestedOperation = request.TrustedHdrGainMapBinding != null
            ? ConversionOperationKind.HdrGainMapConversion
            : request.Transform != ImageTransformKind.None
            ? ConversionOperationKind.LosslessTransform
            : request.SourceArtifact.ImageContainer == request.TargetContainer
            ? ConversionOperationKind.Passthrough
            : ConversionOperationKind.LossyReencode;
        ConversionFactsOrigin factsOrigin = request.TrustedSourceFacts == null
            ? ConversionFactsOrigin.ArtifactDeclaration
            : ConversionFactsOrigin.CallerTrustedNeutralFacts;
        ConversionCapability selectedCapability = request.TrustedHdrGainMapBinding != null
            ? ConversionCapability.HdrGainMapConversion
            : request.Transform != ImageTransformKind.None
            ? ConversionCapability.ImageLosslessTransform
            : ConversionCapability.ImageCodec;

        ImageConversionResult Rejected(string message, ConversionFailureStage stage, ConversionFailureCategory category)
            => new() { Success = false, ErrorMessage = message,
                ExecutionRecord = FailureRecord(request, requestedOperation, factsOrigin, stage, category, sw.Elapsed) };
        if (cancellationToken.IsCancellationRequested)
            return Rejected("Image conversion cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled);

        if (request.Transform != ImageTransformKind.None &&
            (request.SourceArtifact.ImageContainer != ImageContainer.Jpeg ||
             request.TargetContainer != ImageContainer.Jpeg))
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "R2 physical transforms currently support JPEG-to-JPEG only.",
                ExecutionRecord = FailureRecord(
                    request,
                    ConversionOperationKind.LosslessTransform,
                    factsOrigin,
                    ConversionFailureStage.InvalidRequest,
                    ConversionFailureCategory.Unsupported,
                    sw.Elapsed)
            };
        }

        if (request.SourceArtifact.ImageContainer is not ImageContainer.Jpeg and not ImageContainer.Heic)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "Only JPEG and HEIC image sources are supported.",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(requestedOperation, ConversionCapability.ImageCodec, request.PreservationPolicy, ConversionFactsOrigin.ArtifactDeclaration, ConversionFailureStage.InvalidRequest, ConversionFailureCategory.Unsupported),
                    Duration = sw.Elapsed
                }
            };
        }

        // Every R2 route is ordinary-image-only, including same-container
        // passthrough. Inspect before allocating an output path so semantic
        // auxiliaries cannot pass through an unguarded direction.
        ImageConversionSourceFacts conversionFacts;
        if (request.TrustedSourceFacts != null)
        {
            conversionFacts = request.TrustedSourceFacts;
        }
        else
        {
            try
            {
                var inspection = await NativeMediaService.TryInspectMediaWithPlanAsync(request.SourceArtifact.Path,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (inspection.Failure is { } failure)
                    return Rejected(failure.Message, failure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.SourceInspection,
                        failure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.SourceInspection);
                using var inspected = inspection.Source!;
                SourceMediaFacts sourceFacts = inspected.Facts;
                conversionFacts = ImageConversionEligibility.ToConversionFacts(sourceFacts);
                factsOrigin = ConversionFactsOrigin.CompatibilityInspectionOrProbe;
            }
            catch (OperationCanceledException) { return Rejected("Image inspection cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled); }
            catch (Exception ex)
            {
                sw.Stop();
                ConversionFailureCategory category = IsBackendUnavailable(ex)
                    ? ConversionFailureCategory.BackendUnavailable
                    : ConversionFailureCategory.SourceInspection;
                return new ImageConversionResult
                {
                    Success = false,
                    ErrorMessage = $"Source image inspection failed: {ex.Message}",
                    ExecutionRecord = FailureRecord(
                        request,
                        requestedOperation,
                        factsOrigin,
                        category == ConversionFailureCategory.BackendUnavailable
                            ? ConversionFailureStage.BackendUnavailable
                            : ConversionFailureStage.SourceInspection,
                        category,
                        sw.Elapsed)
                };
            }
        }

        // A supplied R3 binding never replaces inspection. Re-read the exact
        // source graph and content hash before any output allocation, then
        // dispatch semantic inputs exclusively through the explicit R3 seam.
        if (request.TrustedHdrGainMapBinding is { } hdrBinding)
        {
            try
            {
                var bindingAttempt = await TryVerifyHdrGainMapBindingAsync(request, hdrBinding, cancellationToken).ConfigureAwait(false);
                if (bindingAttempt.Failure is { } bindingFailure)
                    return Rejected(bindingFailure.Message, bindingFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.TrustedFactsMismatch,
                        bindingFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.TrustedFactsMismatch);
                conversionFacts = bindingAttempt.Facts!;
                factsOrigin = ConversionFactsOrigin.CallerTrustedNeutralFacts;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                return new ImageConversionResult
                {
                    Success = false,
                    ErrorMessage = $"Trusted HDR/GainMap binding mismatch: {ex.Message}",
                    ExecutionRecord = new ImageExecutionRecord
                    {
                        InputContainer = request.SourceArtifact.ImageContainer,
                        OutputContainer = request.TargetContainer,
                        PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                        Truth = FailureTruth(ConversionOperationKind.HdrGainMapConversion,
                            ConversionCapability.HdrGainMapConversion, request.PreservationPolicy,
                            ConversionFactsOrigin.CallerTrustedNeutralFacts, ConversionFailureStage.TrustedFactsMismatch,
                            ConversionFailureCategory.TrustedFactsMismatch),
                        Duration = sw.Elapsed
                    }
                };
            }
        }

        // Neutral declarations can only strengthen policy, never hide a
        // semantic auxiliary. Reconcile an ordinary-looking trusted facts
        // object with fresh Native inspection before deciding R2 eligibility.
        if (request.TrustedHdrGainMapBinding == null &&
            request.TrustedSourceFacts is { HasGainMap: false, HasAuxiliaryMedia: false } trustedOrdinaryFacts)
        {
            try
            {
                var inspection = await NativeMediaService.TryInspectMediaWithPlanAsync(request.SourceArtifact.Path,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (inspection.Failure is { } failure)
                    return Rejected(failure.Message, failure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.SourceInspection,
                        failure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.SourceInspection);
                using var inspected = inspection.Source!;
                SourceMediaFacts observed = inspected.Facts;
                if (observed.PrimaryImage.Container != trustedOrdinaryFacts.Container ||
                    observed.PrimaryImage.Container != request.SourceArtifact.ImageContainer)
                {
                    sw.Stop();
                    return new ImageConversionResult
                    {
                        Success = false,
                        ErrorMessage = "Fresh Native inspection disagrees with the trusted ordinary-image source facts.",
                        ExecutionRecord = new ImageExecutionRecord
                        {
                            InputContainer = request.SourceArtifact.ImageContainer,
                            OutputContainer = request.TargetContainer,
                            PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                            Truth = FailureTruth(requestedOperation, selectedCapability, request.PreservationPolicy,
                                ConversionFactsOrigin.CallerTrustedNeutralFacts, ConversionFailureStage.TrustedFactsMismatch,
                                ConversionFailureCategory.TrustedFactsMismatch),
                            Duration = sw.Elapsed
                        }
                    };
                }
                conversionFacts = ImageConversionEligibility.ToConversionFacts(observed);
                factsOrigin = ConversionFactsOrigin.CompatibilityInspectionOrProbe;
            }
            catch (OperationCanceledException) { return Rejected("Image inspection cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled); }
            catch (Exception ex)
            {
                sw.Stop();
                return new ImageConversionResult
                {
                    Success = false,
                    ErrorMessage = $"Fresh Native inspection of the claimed ordinary image failed: {ex.Message}",
                    ExecutionRecord = FailureRecord(request, requestedOperation, factsOrigin,
                        IsBackendUnavailable(ex) ? ConversionFailureStage.BackendUnavailable : ConversionFailureStage.SourceInspection,
                        IsBackendUnavailable(ex) ? ConversionFailureCategory.BackendUnavailable : ConversionFailureCategory.SourceInspection,
                        sw.Elapsed)
                };
            }
        }

        if (request.TrustedHdrGainMapBinding is { } semanticBinding)
        {
            return await ConvertBoundHdrGainMapAsync(
                request, semanticBinding, factsOrigin, sw, cancellationToken).ConfigureAwait(false);
        }

        if (request.TargetHdrGainMapSemantic != ImageHdrGainMapTargetSemantic.Unknown)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "A target GainMap semantic requires a fresh Native-verified source binding.",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(ConversionOperationKind.HdrGainMapConversion,
                        ConversionCapability.HdrGainMapConversion, request.PreservationPolicy, factsOrigin,
                        ConversionFailureStage.TrustedFactsMismatch, ConversionFailureCategory.TrustedFactsMismatch),
                    Duration = sw.Elapsed
                }
            };
        }

        if (conversionFacts.HasGainMap || (conversionFacts.HasAuxiliaryMedia && request.PreservationPolicy == PreservationPolicy.Strict))
        {
            sw.Stop();
            bool hasBinding = request.TrustedHdrGainMapBinding != null;
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = hasBinding
                    ? "The verified GainMap binding is valid, but no Native HDR/GainMap semantic conversion operation is available for this request."
                    : ImageConversionEligibility.SemanticAuxiliaryBlockedMessage,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(ConversionOperationKind.HdrGainMapConversion,
                        ConversionCapability.HdrGainMapConversion, request.PreservationPolicy,
                        factsOrigin,
                        hasBinding ? ConversionFailureStage.BackendUnavailable : ConversionFailureStage.PolicyRejection,
                        hasBinding ? ConversionFailureCategory.BackendUnavailable : ConversionFailureCategory.Unsupported),
                    Duration = sw.Elapsed
                }
            };
        }

        if (conversionFacts.Container != request.SourceArtifact.ImageContainer)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "Project inspection disagrees with the declared source image container; refusing codec conversion.",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(requestedOperation, selectedCapability, request.PreservationPolicy, factsOrigin, ConversionFailureStage.TrustedFactsMismatch, ConversionFailureCategory.TrustedFactsMismatch),
                    Duration = sw.Elapsed
                }
            };
        }

        if (!ImageConversionEligibility.IsAllowed(
                conversionFacts,
                request.TargetContainer,
                request.PreservationPolicy,
                out string? semanticError))
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = semanticError,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(ConversionOperationKind.HdrGainMapConversion, ConversionCapability.HdrGainMapConversion, request.PreservationPolicy, factsOrigin, ConversionFailureStage.PolicyRejection, ConversionFailureCategory.Unsupported),
                    Duration = sw.Elapsed
                }
            };
        }

        // Inspect semantic scope before applying generic conversion policy so
        // GainMap inputs retain the R1 HdrGainMapConversion/Unsupported truth,
        // even when Strict independently prohibits this cross-container route.
        bool boundOrdinaryConversion=request.TrustedHdrGainMapBinding==null&&!conversionFacts.HasAuxiliaryMedia&&request.SourceArtifact.Kind==MediaArtifactKind.PrimaryImage&&request.SourceArtifact.ImageContainer==ImageContainer.Jpeg&&request.TargetContainer==ImageContainer.Heic&&
            request.Transform==ImageTransformKind.None;
        if (!boundOrdinaryConversion&&request.PreservationPolicy == PreservationPolicy.Strict &&
            request.SourceArtifact.ImageContainer != request.TargetContainer &&
            request.SourceArtifact.ImageContainer != ImageContainer.Unknown)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "Strict preservation policy cannot be satisfied for cross-container image conversion without metadata loss.",
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(requestedOperation, selectedCapability, request.PreservationPolicy, factsOrigin, ConversionFailureStage.PolicyRejection, ConversionFailureCategory.PolicyRejection),
                    Duration = sw.Elapsed
                }
            };
        }

        // JPEG has no R4-frozen mapping for HEIF NCLX. Refuse high-bit-depth,
        // HDR-relevant, ambiguous, or otherwise unrepresentable HEIC color
        // facts before a destination path is allocated. Same-container HEIC
        // passthrough intentionally bypasses this lossy-reencode preflight.
        if (request.SourceArtifact.ImageContainer == ImageContainer.Heic &&
            request.TargetContainer == ImageContainer.Jpeg)
        {
            try
            {
                var primaryAttempt = await NativeMediaService.TryDecodeHeicPrimaryAsync(request.SourceArtifact.Path,
                    cancellationToken).ConfigureAwait(false);
                if (primaryAttempt.Failure is { } primaryFailure)
                    return Rejected(primaryFailure.Message, primaryFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.SourceInspection,
                        primaryFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.SourceInspection);
                NativeHeicPrimaryDecodeInfo primary = primaryAttempt.Info;
                var colorAttempt = await NativeMediaService.TryCapturePreservationObservationAsync(
                    request.SourceArtifact.Path,
                    SourceProtocol.Unknown,
                    ImageContainer.Heic,
                    cancellationToken).ConfigureAwait(false);
                if (colorAttempt.Failure is { } colorFailure)
                    return Rejected(colorFailure.Message, colorFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.SourceInspection,
                        colorFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.SourceInspection);
                var color = colorAttempt.Observation!;

                bool directlyRepresentableSrgb = primary.HasNclx != 0 && primary.HasIcc == 0 &&
                    primary.NclxPrimaries == 1 && primary.NclxTransfer == 13 &&
                    primary.NclxMatrix is 0 or 1 or 5 or 6;
                bool unsupported = primary.SourceBitDepth > 8 ||
                    primary.DecodedSignalBitDepth > 8 ||
                    primary.DecodedStorageBitDepth > 8 ||
                    primary.IsHdrRelevant != 0 ||
                    (primary.HasNclx != 0 && !directlyRepresentableSrgb) ||
                    color.IccParseError ||
                    (primary.HasIcc != 0) != color.HasIcc;
                if (unsupported)
                {
                    sw.Stop();
                    return new ImageConversionResult
                    {
                        Success = false,
                        ErrorMessage = "HEIC primary depth or color properties are ambiguous or unsupported for JPEG under the current R4 policy.",
                        ExecutionRecord = FailureRecord(
                            request,
                            requestedOperation,
                            factsOrigin,
                            ConversionFailureStage.PolicyRejection,
                            ConversionFailureCategory.Unsupported,
                            sw.Elapsed)
                    };
                }
            }
            catch (OperationCanceledException) { return Rejected("Image inspection cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled); }
            catch (Exception ex)
            {
                sw.Stop();
                ConversionFailureCategory category = IsBackendUnavailable(ex)
                    ? ConversionFailureCategory.BackendUnavailable
                    : ConversionFailureCategory.SourceInspection;
                return new ImageConversionResult
                {
                    Success = false,
                    ErrorMessage = $"HEIC primary color/depth inspection failed: {ex.Message}",
                    ExecutionRecord = FailureRecord(
                        request,
                        requestedOperation,
                        factsOrigin,
                        category == ConversionFailureCategory.BackendUnavailable
                            ? ConversionFailureStage.BackendUnavailable
                            : ConversionFailureStage.SourceInspection,
                        category,
                        sw.Elapsed)
                };
            }
        }

        string? ext = request.TargetContainer switch
        {
            ImageContainer.Heic => ".heic",
            ImageContainer.Jpeg => ".jpg",
            _ => null
        };
        if (ext is null)
        {
            sw.Stop();
            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = "Only JPEG and HEIC image containers are supported.",
                ExecutionRecord = FailureRecord(request, requestedOperation, factsOrigin, ConversionFailureStage.InvalidRequest, ConversionFailureCategory.Unsupported, sw.Elapsed)
            };
        }
        string outPath = Path.Combine(request.TargetDirectory, $"img-conv-{Guid.NewGuid():N}{ext}");
        bool backendCompleted = false;
        NativeMediaService.NativeRegisteredOutputEvidence? registeredOutputEvidence = null;
        NativeBoundImageAttempt? boundAttempt=null;

        ImageConversionResult OutputRejected(string message, ConversionFailureCategory category)
        {
            boundAttempt?.Dispose();
            if (!TryDeleteOutput(outPath, registeredOutputEvidence))
                return Rejected(message + " Staged image cleanup could not be completed.", ConversionFailureStage.Cleanup, ConversionFailureCategory.CleanupFailure);
            return Rejected(message, category == ConversionFailureCategory.Cancelled ? ConversionFailureStage.Cancelled : ConversionFailureStage.OutputValidation, category);
        }

        try
        {
            bool losslessTransform = request.Transform != ImageTransformKind.None;
            bool reencoded;
            if (losslessTransform)
            {
                NativeMediaService.NativeImageConversionAttempt nativeAttempt =
                    await NativeMediaService.TransformJpegLosslesslyWithOutputEvidenceAsync(
                    request.SourceArtifact.Path,
                    outPath,
                    ToNativeJpegTransform(request.Transform),
                    cancellationToken).ConfigureAwait(false);
                if (nativeAttempt.ResultCode != NativeResult.Ok)
                {
                    var category = nativeAttempt.ResultCode == NativeResult.Cancelled ? ConversionFailureCategory.Cancelled : nativeAttempt.ResultCode == NativeResult.Unsupported
                        ? ConversionFailureCategory.Unsupported : ConversionFailureCategory.BackendFailure;
                    return new ImageConversionResult { Success = false,
                        ErrorMessage = nativeAttempt.Diagnostic ?? $"Native image operation returned {nativeAttempt.ResultCode}.",
                        ExecutionRecord = FailureRecord(request, requestedOperation, factsOrigin,
                            category == ConversionFailureCategory.Cancelled ? ConversionFailureStage.Cancelled : ConversionFailureStage.BackendExecution, category, sw.Elapsed) };
                }
                registeredOutputEvidence = nativeAttempt.OutputEvidence;
                backendCompleted = true;
                if (registeredOutputEvidence == null)
                    return OutputRejected(
                        "Native JPEG transform did not return exact creating-handle output evidence: " +
                        (nativeAttempt.OutputEvidenceFailure?.Message ?? "unknown evidence failure"), ConversionFailureCategory.OutputValidation);
                reencoded = false;
            }
            else
            {
                NativeMediaService.NativeImageConversionAttempt nativeAttempt;
                if(boundOrdinaryConversion) {
                    boundAttempt=await NativeBoundImageConverter.TryConvertAsync(request.SourceArtifact,outPath,request.Quality,cancellationToken).ConfigureAwait(false);
                    nativeAttempt=boundAttempt.Attempt;
                    if(nativeAttempt.ResultCode==NativeResult.Unsupported && request.PreservationPolicy!=PreservationPolicy.Strict)
                    {
                        boundOrdinaryConversion=false;boundAttempt.Dispose();boundAttempt=null;
                        nativeAttempt=await NativeMediaService.ConvertImageWithOutputEvidenceAsync(request.SourceArtifact.Path,
                            outPath,request.TargetContainer,request.Quality,cancellationToken).ConfigureAwait(false);
                    }
                } else nativeAttempt=await NativeMediaService.ConvertImageWithOutputEvidenceAsync(request.SourceArtifact.Path,
                    outPath,request.TargetContainer,request.Quality,cancellationToken).ConfigureAwait(false);
                if (nativeAttempt.ResultCode != NativeResult.Ok)
                {
                    var category = nativeAttempt.ResultCode == NativeResult.Cancelled ? ConversionFailureCategory.Cancelled : nativeAttempt.ResultCode == NativeResult.Unsupported
                        ? ConversionFailureCategory.Unsupported : ConversionFailureCategory.BackendFailure;
                    return new ImageConversionResult { Success = false,
                        ErrorMessage = nativeAttempt.Diagnostic ?? $"Native image operation returned {nativeAttempt.ResultCode}.",
                        ExecutionRecord = FailureRecord(request, requestedOperation, factsOrigin,
                            category == ConversionFailureCategory.Cancelled ? ConversionFailureStage.Cancelled : ConversionFailureStage.BackendExecution, category, sw.Elapsed) };
                }
                registeredOutputEvidence = nativeAttempt.OutputEvidence;
                backendCompleted = true;
                if (registeredOutputEvidence == null)
                    return OutputRejected(
                        "Native image conversion did not return exact creating-handle output evidence: " +
                        (nativeAttempt.OutputEvidenceFailure?.Message ?? "unknown evidence failure"), ConversionFailureCategory.OutputValidation);
                if(boundOrdinaryConversion&&boundAttempt?.Proof is null)
                    return OutputRejected("Bound ordinary image conversion has no complete validated proof: "+nativeAttempt.OutputEvidenceFailure?.Message, ConversionFailureCategory.OutputValidation);
                reencoded = nativeAttempt.Reencoded;
            }

            sw.Stop();

            if (!File.Exists(outPath))
            {
                return OutputRejected("Converted output image was not found.", ConversionFailureCategory.OutputMissing);
            }

            // Inspect output header to verify actual container
            ImageContainer actualContainer = DetectOutputContainer(outPath);
            if (actualContainer != request.TargetContainer && request.TargetContainer != ImageContainer.Unknown)
            {
                return OutputRejected(
                    $"Output image container mismatch: expected {request.TargetContainer}, actual {actualContainer}.", ConversionFailureCategory.OutputValidation);
            }

            if (losslessTransform)
            {
                var inputInspection = await NativeMediaService.TryInspectMediaWithPlanAsync(request.SourceArtifact.Path,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (inputInspection.Failure is { } inputFailure)
                    return OutputRejected(inputFailure.Message, inputFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.OutputValidation);
                using var inputInspected = inputInspection.Source!;
                var outputInspection = await NativeMediaService.TryInspectMediaWithPlanAsync(outPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (outputInspection.Failure is { } outputFailure)
                    return OutputRejected(outputFailure.Message, outputFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.OutputValidation);
                using var outputInspected = outputInspection.Source!;
                SourceMediaFacts inputFacts = inputInspected.Facts;
                SourceMediaFacts outputFacts = outputInspected.Facts;
                bool swapsDimensions = request.Transform is ImageTransformKind.Rotate90 or ImageTransformKind.Rotate270 or
                    ImageTransformKind.Transpose or ImageTransformKind.Transverse;
                uint expectedWidth = swapsDimensions ? inputFacts.PrimaryImage.Height : inputFacts.PrimaryImage.Width;
                uint expectedHeight = swapsDimensions ? inputFacts.PrimaryImage.Width : inputFacts.PrimaryImage.Height;
                if (outputFacts.PrimaryImage.Width != expectedWidth || outputFacts.PrimaryImage.Height != expectedHeight)
                {
                    return OutputRejected(
                        $"Lossless JPEG transform dimensions are invalid: expected {expectedWidth}x{expectedHeight}, " +
                        $"actual {outputFacts.PrimaryImage.Width}x{outputFacts.PrimaryImage.Height}.", ConversionFailureCategory.OutputValidation);
                }
            }

            ImagePostValidation? postValidation = boundAttempt?.Proof is { } ordinaryProof
                ?new ImagePostValidation(ordinaryProof.MetadataCount==0?ConversionComponentOutcome.NotApplicable:ConversionComponentOutcome.Preserved,
                    ConversionComponentOutcome.Preserved,ordinaryProof.HasIcc?ConversionComponentOutcome.Preserved:ConversionComponentOutcome.NotApplicable,
                    PreservationOutcome.Preserved)
                : null;
            if (postValidation is null)
            {
                var validation = await ImagePostValidator.TryVerifyOrdinaryAsync(request.SourceArtifact.Path,
                    outPath, request.SourceArtifact.ImageContainer, actualContainer, reencoded, losslessTransform,
                    cancellationToken).ConfigureAwait(false);
                if (validation.Failure is { } validationFailure)
                    return OutputRejected(validationFailure.Message, validationFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.OutputValidation);
                postValidation = validation.Validation!;
            }
            if ((!reencoded||boundOrdinaryConversion) && request.PreservationPolicy == PreservationPolicy.Strict &&
                postValidation!.PreservationOutcome != PreservationOutcome.Preserved)
            {
                return OutputRejected(
                    "Strict preservation policy could not prove every applicable JPEG preservation component.", ConversionFailureCategory.OutputValidation);
            }

            bool metadataCopied = postValidation!.Metadata == ConversionComponentOutcome.Preserved;
            PreservationOutcome preservationOutcome = !reencoded
                ? postValidation!.PreservationOutcome
                : postValidation!.PreservationOutcome;
            if (conversionFacts.HasAuxiliaryMedia)
            {
                preservationOutcome = PreservationOutcome.PartiallyPreserved;
            }

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = actualContainer == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = actualContainer,
                ImageCodec = actualContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = registeredOutputEvidence!.ByteLength,
                Sha256 = registeredOutputEvidence.Sha256,
                FileIdentity = registeredOutputEvidence.FileIdentity
            };

            return new ImageConversionResult
            {
                Success = true,
                OutputArtifact = outArtifact,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = actualContainer,
                    PixelReencoded = reencoded,
                    MetadataCopied = metadataCopied,
                    PreservationOutcome = preservationOutcome,
                    OrdinaryConversionEvidence=boundAttempt?.Proof,
                    Truth = new ConversionExecutionTruth
                    {
                        RequestedOperationKind = requestedOperation,
                        ActualOperationKind = reencoded ? ConversionOperationKind.LossyReencode :
                            losslessTransform ? ConversionOperationKind.LosslessTransform : ConversionOperationKind.Passthrough,
                        SelectedCapability = selectedCapability,
                        ActualCapability = selectedCapability,
                        FactsOrigin = factsOrigin,
                        PreservationPolicy = request.PreservationPolicy,
                        BackendName = "LivePhotoBox.Native",
                        HardwareMode = "NotApplicable",
                        Pixel = reencoded ? ConversionComponentOutcome.Reencoded : ConversionComponentOutcome.Preserved,
                        Metadata = postValidation!.Metadata,
                        ColorIcc = postValidation!.ColorIcc,
                        HdrGainMap = ConversionComponentOutcome.NotEvaluated,
                        Auxiliary = ConversionComponentOutcome.NotEvaluated,
                        Audio = ConversionComponentOutcome.NotApplicable,
                        Timing = ConversionComponentOutcome.NotApplicable,
                        Orientation = postValidation!.Orientation,
                        PreservationOutcome = preservationOutcome
                    },
                    Duration = sw.Elapsed
                }
            };
        }
        catch (OperationCanceledException ex)
        {
            boundAttempt?.Dispose();
            sw.Stop();
            if (!TryDeleteOutput(outPath, registeredOutputEvidence))
            {
                return Rejected("Image conversion was cancelled, but its staged output could not be removed. " + ex.Message,
                    ConversionFailureStage.Cleanup, ConversionFailureCategory.CleanupFailure);
            }
            return Rejected("Image conversion cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled);
        }
        catch (Exception ex)
        {
            boundAttempt?.Dispose();
            sw.Stop();
            bool cleanupSucceeded = TryDeleteOutput(outPath, registeredOutputEvidence);
            ConversionFailureCategory category = !cleanupSucceeded
                ? ConversionFailureCategory.CleanupFailure
                : IsBackendUnavailable(ex)
                    ? ConversionFailureCategory.BackendUnavailable
                : boundOrdinaryConversion&&ex is NotSupportedException
                    ? ConversionFailureCategory.Unsupported
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
                ConversionFailureCategory.Unsupported => ConversionFailureStage.PolicyRejection,
                _ => ConversionFailureStage.BackendExecution
            };

            return new ImageConversionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = request.SourceArtifact.ImageContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = false,
                    MetadataCopied = false,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Truth = FailureTruth(requestedOperation, selectedCapability, request.PreservationPolicy, factsOrigin, stage, category),
                    Duration = sw.Elapsed
                }
            };
        }
        finally { boundAttempt?.Dispose(); }
    }

    private static bool TryDeleteOutput(
        string path,
        NativeMediaService.NativeRegisteredOutputEvidence? expected) =>
        NativeMediaService.TryDeleteRegisteredOutput(path, expected);

    private sealed record HdrBindingAttempt(ImageConversionSourceFacts? Facts, Exception? Failure);

    private static async Task<HdrBindingAttempt> TryVerifyHdrGainMapBindingAsync(
        ImageConversionRequest request,
        ImageHdrGainMapSourceBinding binding,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(null, new OperationCanceledException(cancellationToken));
        string sourcePath = Path.GetFullPath(request.SourceArtifact.Path);
        if (!string.Equals(sourcePath, Path.GetFullPath(binding.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            request.SourceArtifact.ImageContainer != binding.SourceContainer)
            return new(null, new InvalidDataException("Binding source path or container does not match the requested artifact."));

        using var sourceLease = new NeutralArtifactReadLeaseSet();
        if (sourceLease.TryPin(request.SourceArtifact) is { } leaseFailure) return new(null, leaseFailure);
        using var source = SourceReadAttempt.Open(sourcePath);
        if (!source.Succeeded) return new(null, new IOException($"Cannot retain HDR source (system error {source.Error})."));
        if (!WindowsFileIdentity.TryCapture(source.Handle, out var capturedIdentity, out int identityError))
            return new(null, new IOException($"Cannot verify HDR source identity (system error {identityError})."));
        WindowsFileIdentity currentIdentity = capturedIdentity!;
        if (!binding.SourceFileIdentity.Matches(currentIdentity) ||
            (request.SourceArtifact.FileIdentity != null && !request.SourceArtifact.FileIdentity.Matches(currentIdentity)))
            return new(null, new InvalidDataException("Source filesystem object identity changed after Neutral binding."));

        var sourceHash = await MediaFileHashAttempt.ComputeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (sourceHash.Failure is { } hashFailure) return new(null, hashFailure);
        if (!string.Equals(sourceHash.Sha256, binding.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return new(null, new InvalidDataException("Source artifact content hash changed after Neutral binding."));
        var inspection = await NativeMediaService.TryInspectMediaWithPlanAsync(sourcePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (inspection.Failure is { } inspectionFailure) return new(null, inspectionFailure);
        using var inspected = inspection.Source!;
        SourceMediaFacts observed = inspected.Facts;
        if (observed.GainMap is not { } gainMap) return new(null, new InvalidDataException("Native inspection no longer reports the bound GainMap."));
        if (!gainMap.IsPresent || gainMap.AuxiliaryIndex != binding.AuxiliaryIndex ||
            gainMap.ItemId != binding.ItemId || gainMap.Container != binding.SourceContainer ||
            gainMap.ByteOffset != binding.ByteOffset || gainMap.ByteLength != binding.ByteLength ||
            gainMap.Representation != binding.Representation || gainMap.Ownership != binding.Ownership ||
            gainMap.OwnerArtifactRole != binding.OwnerArtifactRole ||
            !string.Equals(gainMap.Relationship, binding.Relationship, StringComparison.Ordinal) ||
            gainMap.AuxiliaryIndex >= observed.AuxiliaryItems.Count)
            return new(null, new InvalidDataException("Native source facts disagree with the bound GainMap item, range, owner, or relationship."));

        AuxiliaryMediaFacts auxiliary = observed.AuxiliaryItems[checked((int)gainMap.AuxiliaryIndex)];
        if (!auxiliary.IsPresent || auxiliary.ItemId != binding.ItemId ||
            auxiliary.Representation != binding.Representation || auxiliary.Ownership != binding.Ownership ||
            auxiliary.ByteOffset != binding.ByteOffset || auxiliary.ByteLength != binding.ByteLength ||
            !string.Equals(auxiliary.StableIdentity, binding.InspectedStableIdentity, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Semantic, binding.Semantic, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.OwnerIdentity, binding.InspectedOwnerIdentity, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Relationship, binding.Relationship, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Sha256, binding.GainMapSha256, StringComparison.OrdinalIgnoreCase))
            return new(null, new InvalidDataException("Native auxiliary graph no longer matches the bound GainMap bytes and identity."));

        if (!string.IsNullOrWhiteSpace(observed.PrimarySha256) &&
            !string.Equals(observed.PrimarySha256, binding.PrimaryImageSha256, StringComparison.OrdinalIgnoreCase))
            return new(null, new InvalidDataException("Native primary-image SHA-256 differs from the bound inspected image item."));

        return new(ImageConversionEligibility.ToConversionFacts(observed), null);
    }

    private static async Task<ImageConversionResult> ConvertBoundHdrGainMapAsync(
        ImageConversionRequest request,
        ImageHdrGainMapSourceBinding binding,
        ConversionFactsOrigin factsOrigin,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        bool allowedFallback = request.PreservationPolicy == PreservationPolicy.BestEffort &&
            request.HdrOutputPolicy == HdrOutputPolicy.AllowSdrDegradation;
        bool heicToJpeg = binding.SourceContainer == ImageContainer.Heic &&
            request.TargetContainer == ImageContainer.Jpeg &&
            request.TargetHdrGainMapSemantic == ImageHdrGainMapTargetSemantic.JpegIsoGainMap;
        bool jpegToHeic = binding.SourceContainer == ImageContainer.Jpeg &&
            request.TargetContainer == ImageContainer.Heic &&
            request.TargetHdrGainMapSemantic == ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary;
        if ((!heicToJpeg && !jpegToHeic) ||
            request.SourceArtifact.ImageContainer != binding.SourceContainer ||
            request.Transform != ImageTransformKind.None ||
            request.HdrOutputPolicy is not HdrOutputPolicy.PreserveHdr and not HdrOutputPolicy.AllowSdrDegradation)
        {
            sw.Stop();
            return HdrGainMapFailure(request, factsOrigin,
                "The bound R3 target semantic is unsupported for this source, container, transform, or policy.",
                ConversionFailureStage.InvalidRequest, ConversionFailureCategory.Unsupported, sw.Elapsed, allowedFallback);
        }

        string extension = request.TargetContainer == ImageContainer.Jpeg ? ".jpg" : ".heic";
        string outputPath = Path.Combine(request.TargetDirectory, $"img-hdr-conv-{Guid.NewGuid():N}{extension}");
        NativeHdrGainMapConversionTransaction? transaction = null;
        bool stageCreated = false;
        NeutralArtifactReadLeaseSet? hdrOutputLease=null;
        NativeMediaService.NativeRegisteredOutputEvidence? committedHdrOutput=null;
        ImageConversionResult Refused(string message, ConversionFailureStage stage, ConversionFailureCategory category)
        {
            hdrOutputLease?.Dispose(); hdrOutputLease = null;
            bool cleaned = transaction is null || AbortTransaction(transaction);
            transaction = null;
            if (committedHdrOutput is not null) cleaned &= TryDeleteOutput(outputPath, committedHdrOutput);
            return HdrGainMapFailure(request, factsOrigin, message,
                cleaned ? stage : ConversionFailureStage.Cleanup, cleaned ? category : ConversionFailureCategory.CleanupFailure,
                sw.Elapsed, allowedFallback);
        }
        try
        {
            var staging = await NativeMediaService.TryStageBoundHdrGainMapAsync(
                    request.SourceArtifact.Path, outputPath, binding, request.SourceArtifact.ByteLength,
                    request.Quality, request.HdrOutputPolicy, request.TargetHdrGainMapSemantic, cancellationToken).ConfigureAwait(false);
            if (staging.Failure is { } stagingFailure)
                return HdrGainMapFailure(request, factsOrigin, stagingFailure.Message,
                    stagingFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.BackendExecution,
                    stagingFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : stagingFailure is NotSupportedException ? ConversionFailureCategory.Unsupported : ConversionFailureCategory.BackendFailure,
                    sw.Elapsed, allowedFallback);
            transaction = staging.Transaction!;
            stageCreated = true;

            var postAttempt = await ImagePostValidator.TryVerifyHdrGainMapAsync(request.SourceArtifact.Path,
                transaction, transaction.Result, binding.SourceContainer, request.TargetContainer,
                request.TargetHdrGainMapSemantic, heicToJpeg ? null : "urn:com:photo:aux:hdrgainmap", cancellationToken,
                request.PreservationPolicy == PreservationPolicy.BestEffort).ConfigureAwait(false);
            if (postAttempt.Failure is { } postFailure)
                return Refused(postFailure.Message, postFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.OutputValidation,
                    postFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.OutputValidation);
            ImageHdrGainMapPostValidation post = postAttempt.Validation!;
            // Rebind the source after post-validation and before commit so a
            // changed source cannot be reported as the input of this result.
            var bindingAttempt = await TryVerifyHdrGainMapBindingAsync(request, binding, cancellationToken).ConfigureAwait(false);
            if (bindingAttempt.Failure is { } bindingFailure)
            {
                bool cleaned = AbortTransaction(transaction); transaction = null;
                return HdrGainMapFailure(request, factsOrigin, bindingFailure.Message,
                    !cleaned ? ConversionFailureStage.Cleanup : bindingFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.TrustedFactsMismatch,
                    !cleaned ? ConversionFailureCategory.CleanupFailure : bindingFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.TrustedFactsMismatch,
                    sw.Elapsed, allowedFallback);
            }

            if (request.PreservationPolicy == PreservationPolicy.Strict &&
                !IsFullyPreserved(post.Preservation.Metadata) ||
                request.PreservationPolicy == PreservationPolicy.Strict &&
                !IsFullyPreserved(post.Preservation.Orientation) ||
                request.PreservationPolicy == PreservationPolicy.Strict &&
                !IsFullyPreserved(post.Preservation.ColorIcc))
            {
                return Refused(
                    "Strict HDR/GainMap conversion could not prove every applicable metadata, orientation, and ICC component before commit. " +
                    $"Metadata={post.Preservation.Metadata}; Orientation={post.Preservation.Orientation}; ICC={post.Preservation.ColorIcc}; " +
                    $"ChangedFields={post.Preservation.DifferenceDetails}.", ConversionFailureStage.OutputValidation, ConversionFailureCategory.OutputValidation);
            }

            FileInfo stagedFile = new(transaction.StagingPath);
            if (!stagedFile.Exists || stagedFile.Length <= 0)
                return Refused("Native HDR/GainMap stage disappeared before commit.", ConversionFailureStage.OutputValidation, ConversionFailureCategory.OutputMissing);
            if (cancellationToken.IsCancellationRequested)
                return Refused("HDR/GainMap conversion cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled);
            if (transaction.TryCommit() is { } commitFailure)
                return Refused(commitFailure.Message, commitFailure is OperationCanceledException ? ConversionFailureStage.Cancelled : ConversionFailureStage.OutputValidation,
                    commitFailure is OperationCanceledException ? ConversionFailureCategory.Cancelled : ConversionFailureCategory.OutputValidation);
            if(post.CreatingOutputIdentity is { } created)
                committedHdrOutput=new(created,stagedFile.Length,post.OutputSha256);
            NativeMediaService.NativeRegisteredOutputEvidence outputEvidence =
                transaction.CaptureCommittedOutputEvidence(outputPath);
            if(post.HdrConversionEvidence is { } complete)
            {
                if(post.CreatingOutputIdentity is not { } identity||!identity.Matches(outputEvidence.FileIdentity)||
                    outputEvidence.Sha256!=complete.OutputSha256)
                    return Refused("Committed HDR image is not the proof producer's creating object and bytes.", ConversionFailureStage.OutputValidation, ConversionFailureCategory.OutputValidation);
                hdrOutputLease=new NeutralArtifactReadLeaseSet();
                if (hdrOutputLease.TryPin(new MediaArtifact{Path=outputPath,Kind=MediaArtifactKind.PrimaryImage,
                    ImageContainer=request.TargetContainer,ByteLength=outputEvidence.ByteLength,
                    Sha256=outputEvidence.Sha256,FileIdentity=outputEvidence.FileIdentity}) is { } leaseFailure)
                    return Refused(leaseFailure.Message, ConversionFailureStage.OutputValidation, ConversionFailureCategory.OutputValidation);
            }
            transaction.Dispose();
            transaction = null;

            sw.Stop();
            PreservationOutcome preservation = request.PreservationPolicy == PreservationPolicy.Strict
                ? PreservationOutcome.Preserved
                : PreservationOutcome.PartiallyPreserved;
            return new ImageConversionResult
            {
                Success = true,
                OutputArtifact = new MediaArtifact
                {
                    Path = outputPath,
                    Kind = request.SourceArtifact.Kind,
                    MimeType = request.TargetContainer == ImageContainer.Jpeg ? "image/jpeg" : "image/heic",
                    ImageContainer = request.TargetContainer,
                    ImageCodec = request.TargetContainer == ImageContainer.Jpeg ? ImageCodec.Jpeg : ImageCodec.Hevc,
                    ByteLength = outputEvidence.ByteLength,
                    Sha256 = outputEvidence.Sha256,
                    FileIdentity = outputEvidence.FileIdentity
                },
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = binding.SourceContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = true,
                    MetadataCopied = post.Preservation.Metadata == ConversionComponentOutcome.Preserved,
                    MetadataMapping = post.MetadataMapping,
                    HdrConversionEvidence = post.HdrConversionEvidence,
                    PreservationOutcome = preservation,
                    Truth = new ConversionExecutionTruth
                    {
                        RequestedOperationKind = ConversionOperationKind.HdrGainMapConversion,
                        ActualOperationKind = ConversionOperationKind.HdrGainMapConversion,
                        SelectedCapability = ConversionCapability.HdrGainMapConversion,
                        ActualCapability = ConversionCapability.HdrGainMapConversion,
                        FactsOrigin = factsOrigin,
                        PreservationPolicy = request.PreservationPolicy,
                        FallbackAllowed = allowedFallback,
                        FallbackOccurred = false,
                        BackendName = "LivePhotoBox.Native",
                        HardwareMode = "NotApplicable",
                        Pixel = ConversionComponentOutcome.Reencoded,
                        Metadata = post.Preservation.Metadata,
                        ColorIcc = post.Preservation.ColorIcc,
                        HdrGainMap = ConversionComponentOutcome.Reencoded,
                        Auxiliary = ConversionComponentOutcome.Reencoded,
                        Audio = ConversionComponentOutcome.NotApplicable,
                        Timing = ConversionComponentOutcome.NotApplicable,
                        Orientation = post.Preservation.Orientation,
                        PreservationOutcome = preservation
                    },
                    Duration = sw.Elapsed
                }
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return Refused("HDR/GainMap conversion cancelled.", ConversionFailureStage.Cancelled, ConversionFailureCategory.Cancelled);
        }
        catch (Exception ex)
        {
            sw.Stop();
            hdrOutputLease?.Dispose();hdrOutputLease=null;
            bool cleanupSucceeded = transaction is null || AbortTransaction(transaction);
            if(committedHdrOutput is not null)cleanupSucceeded&=TryDeleteOutput(outputPath,committedHdrOutput);
            transaction = null;
            ConversionFailureStage stage = !cleanupSucceeded
                ? ConversionFailureStage.Cleanup
                : stageCreated ? ConversionFailureStage.OutputValidation :
                    IsBackendUnavailable(ex) ? ConversionFailureStage.BackendUnavailable : ConversionFailureStage.BackendExecution;
            ConversionFailureCategory category = !cleanupSucceeded
                ? ConversionFailureCategory.CleanupFailure
                : stageCreated ? ConversionFailureCategory.OutputValidation :
                    IsBackendUnavailable(ex) ? ConversionFailureCategory.BackendUnavailable : ConversionFailureCategory.BackendFailure;
            return HdrGainMapFailure(request, factsOrigin, ex.Message, stage, category, sw.Elapsed, allowedFallback);
        }
        finally
        {
            hdrOutputLease?.Dispose();
            transaction?.Dispose();
        }
    }

    private static bool AbortTransaction(NativeHdrGainMapConversionTransaction transaction)
    {
        bool succeeded = true;
        try { succeeded = transaction.TryAbort() is null; }
        catch { succeeded = false; }
        try { transaction.Dispose(); }
        catch { succeeded = false; }
        return succeeded;
    }

    private static bool IsFullyPreserved(ConversionComponentOutcome outcome) =>
        outcome is ConversionComponentOutcome.Preserved or ConversionComponentOutcome.NotApplicable;

    private static ImageConversionResult HdrGainMapFailure(
        ImageConversionRequest request,
        ConversionFactsOrigin factsOrigin,
        string error,
        ConversionFailureStage stage,
        ConversionFailureCategory category,
        TimeSpan duration,
        bool fallbackAllowed) => new()
    {
        Success = false,
        ErrorMessage = error,
        ExecutionRecord = new ImageExecutionRecord
        {
            InputContainer = request.SourceArtifact.ImageContainer,
            OutputContainer = request.TargetContainer,
            PreservationOutcome = PreservationOutcome.PartiallyPreserved,
            Duration = duration,
            Truth = FailureTruth(ConversionOperationKind.HdrGainMapConversion,
                ConversionCapability.HdrGainMapConversion, request.PreservationPolicy, factsOrigin, stage, category) with
            {
                FallbackAllowed = fallbackAllowed
            }
        }
    };

    private static bool IsBackendUnavailable(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static int ToNativeJpegTransform(ImageTransformKind transform) => transform switch
    {
        ImageTransformKind.Rotate90 => 0,
        ImageTransformKind.Rotate180 => 1,
        ImageTransformKind.Rotate270 => 2,
        ImageTransformKind.FlipHorizontal => 3,
        ImageTransformKind.FlipVertical => 4,
        ImageTransformKind.Transpose => 5,
        ImageTransformKind.Transverse => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(transform), transform, "A physical JPEG transform is required.")
    };

    private static ImageExecutionRecord FailureRecord(ImageConversionRequest request, ConversionOperationKind requestedOperation, ConversionFactsOrigin origin, ConversionFailureStage stage, ConversionFailureCategory category, TimeSpan duration) => new()
    {
        InputContainer = request.SourceArtifact.ImageContainer,
        OutputContainer = request.TargetContainer,
        PixelReencoded = false,
        MetadataCopied = false,
        PreservationOutcome = PreservationOutcome.PartiallyPreserved,
        Truth = FailureTruth(requestedOperation, request.Transform != ImageTransformKind.None ? ConversionCapability.ImageLosslessTransform : ConversionCapability.ImageCodec, request.PreservationPolicy, origin, stage, category),
        Duration = duration
    };

    private static ConversionExecutionTruth FailureTruth(ConversionOperationKind requested, ConversionCapability capability, PreservationPolicy policy, ConversionFactsOrigin origin, ConversionFailureStage stage, ConversionFailureCategory category) => new()
    {
        RequestedOperationKind = requested,
        ActualOperationKind = stage == ConversionFailureStage.PolicyRejection && category == ConversionFailureCategory.Unsupported ? ConversionOperationKind.Unsupported : ConversionOperationKind.Unknown,
        SelectedCapability = capability,
        ActualCapability = ConversionCapability.Unknown,
        FactsOrigin = origin,
        PreservationPolicy = policy,
        BackendName = "LivePhotoBox.Native",
        HardwareMode = "NotApplicable",
        Pixel = ConversionComponentOutcome.NotEvaluated,
        Metadata = ConversionComponentOutcome.NotEvaluated,
        ColorIcc = ConversionComponentOutcome.NotEvaluated,
        HdrGainMap = ConversionComponentOutcome.NotEvaluated,
        Auxiliary = ConversionComponentOutcome.NotEvaluated,
        Audio = ConversionComponentOutcome.NotApplicable,
        Timing = ConversionComponentOutcome.NotApplicable,
        Orientation = ConversionComponentOutcome.NotEvaluated,
        PreservationOutcome = PreservationOutcome.PartiallyPreserved,
        FailureStage = stage,
        FailureCategory = category
    };

    private static readonly string[] HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "mif1", "msf1", "miaf"];

    private static ImageContainer DetectOutputContainer(string path)
    {
        Span<byte> header = stackalloc byte[64];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = fs.Read(header);
            if (read >= 2 && header[0] == 0xFF && header[1] == 0xD8)
            {
                return ImageContainer.Jpeg;
            }
            if (read >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
            {
                // Check major brand
                string majorBrand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4));
                foreach (string b in HeifBrands)
                {
                    if (string.Equals(majorBrand, b, StringComparison.OrdinalIgnoreCase))
                    {
                        return ImageContainer.Heic;
                    }
                }

                // Check compatible brands
                for (int offset = 16; offset + 4 <= read; offset += 4)
                {
                    string brand = System.Text.Encoding.ASCII.GetString(header.Slice(offset, 4));
                    foreach (string b in HeifBrands)
                    {
                        if (string.Equals(brand, b, StringComparison.OrdinalIgnoreCase))
                        {
                            return ImageContainer.Heic;
                        }
                    }
                }
            }
        }
        return ImageContainer.Unknown;
    }
}
