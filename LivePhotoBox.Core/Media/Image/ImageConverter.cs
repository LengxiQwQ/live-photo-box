using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

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
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

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
                SourceMediaFacts sourceFacts = await NativeMediaService
                    .InspectMediaAsync(request.SourceArtifact.Path, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                conversionFacts = ImageConversionEligibility.ToConversionFacts(sourceFacts);
                factsOrigin = ConversionFactsOrigin.CompatibilityInspectionOrProbe;
            }
            catch (OperationCanceledException) { throw; }
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
                conversionFacts = await VerifyHdrGainMapBindingAsync(request, hdrBinding, cancellationToken)
                    .ConfigureAwait(false);
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
                SourceMediaFacts observed = await NativeMediaService
                    .InspectMediaAsync(request.SourceArtifact.Path, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                return new ImageConversionResult
                {
                    Success = false,
                    ErrorMessage = $"Fresh Native inspection of the claimed ordinary image failed: {ex.Message}",
                    ExecutionRecord = FailureRecord(request, requestedOperation, factsOrigin,
                        ConversionFailureStage.SourceInspection,
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

        if (conversionFacts.HasGainMap || conversionFacts.HasAuxiliaryMedia)
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
        if (request.PreservationPolicy == PreservationPolicy.Strict &&
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

        try
        {
            bool losslessTransform = request.Transform != ImageTransformKind.None;
            bool reencoded;
            if (losslessTransform)
            {
                await NativeMediaService.TransformJpegLosslesslyAsync(
                    request.SourceArtifact.Path,
                    outPath,
                    ToNativeJpegTransform(request.Transform),
                    cancellationToken).ConfigureAwait(false);
                reencoded = false;
            }
            else
            {
                reencoded = await NativeMediaService.ConvertImageAsync(
                    request.SourceArtifact.Path,
                    outPath,
                    request.TargetContainer,
                    request.Quality,
                    cancellationToken).ConfigureAwait(false);
            }
            backendCompleted = true;

            sw.Stop();

            if (!File.Exists(outPath))
            {
                throw new FileNotFoundException("Converted output image was not found.", outPath);
            }

            // Inspect output header to verify actual container
            ImageContainer actualContainer = DetectOutputContainer(outPath);
            if (actualContainer != request.TargetContainer && request.TargetContainer != ImageContainer.Unknown)
            {
                throw new InvalidOperationException(
                    $"Output image container mismatch: expected {request.TargetContainer}, actual {actualContainer}.");
            }

            if (losslessTransform)
            {
                SourceMediaFacts inputFacts = await NativeMediaService
                    .InspectMediaAsync(request.SourceArtifact.Path, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                SourceMediaFacts outputFacts = await NativeMediaService
                    .InspectMediaAsync(outPath, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                bool swapsDimensions = request.Transform is ImageTransformKind.Rotate90 or ImageTransformKind.Rotate270 or
                    ImageTransformKind.Transpose or ImageTransformKind.Transverse;
                uint expectedWidth = swapsDimensions ? inputFacts.PrimaryImage.Height : inputFacts.PrimaryImage.Width;
                uint expectedHeight = swapsDimensions ? inputFacts.PrimaryImage.Width : inputFacts.PrimaryImage.Height;
                if (outputFacts.PrimaryImage.Width != expectedWidth || outputFacts.PrimaryImage.Height != expectedHeight)
                {
                    throw new InvalidOperationException(
                        $"Lossless JPEG transform dimensions are invalid: expected {expectedWidth}x{expectedHeight}, " +
                        $"actual {outputFacts.PrimaryImage.Width}x{outputFacts.PrimaryImage.Height}.");
                }
            }

            ImagePostValidation? postValidation = reencoded
                ? await ImagePostValidator.VerifyLossyReencodeAsync(
                    request.SourceArtifact.Path,
                    request.SourceArtifact.ImageContainer,
                    outPath,
                    actualContainer,
                    cancellationToken).ConfigureAwait(false)
                : losslessTransform
                    ? await ImagePostValidator.VerifyLosslessJpegTransformAsync(
                        request.SourceArtifact.Path, outPath, cancellationToken).ConfigureAwait(false)
                    : await ImagePostValidator.VerifyExactPassthroughAsync(
                        request.SourceArtifact.Path, outPath, actualContainer, cancellationToken).ConfigureAwait(false);
            if (!reencoded && request.PreservationPolicy == PreservationPolicy.Strict &&
                postValidation!.PreservationOutcome != PreservationOutcome.Preserved)
            {
                throw new InvalidOperationException(
                    "Strict preservation policy could not prove every applicable JPEG preservation component.");
            }

            bool metadataCopied = postValidation!.Metadata == ConversionComponentOutcome.Preserved;
            PreservationOutcome preservationOutcome = !reencoded
                ? postValidation!.PreservationOutcome
                : postValidation!.PreservationOutcome;

            var outArtifact = new MediaArtifact
            {
                Path = outPath,
                Kind = request.SourceArtifact.Kind,
                MimeType = actualContainer == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = actualContainer,
                ImageCodec = actualContainer == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = new FileInfo(outPath).Length
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
            sw.Stop();
            if (!TryDeleteOutput(outPath))
            {
                throw new IOException(
                    "Image conversion was cancelled, but its staged output could not be removed.",
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
    }

    private static bool TryDeleteOutput(string path)
    {
        if (!File.Exists(path)) return true;
        try { File.Delete(path); return !File.Exists(path); }
        catch { return false; }
    }

    private static async Task<ImageConversionSourceFacts> VerifyHdrGainMapBindingAsync(
        ImageConversionRequest request,
        ImageHdrGainMapSourceBinding binding,
        CancellationToken cancellationToken)
    {
        string sourcePath = Path.GetFullPath(request.SourceArtifact.Path);
        if (!string.Equals(sourcePath, Path.GetFullPath(binding.SourcePath), StringComparison.OrdinalIgnoreCase) ||
            request.SourceArtifact.ImageContainer != binding.SourceContainer)
            throw new InvalidDataException("Binding source path or container does not match the requested artifact.");

        WindowsFileIdentity currentIdentity = WindowsFileIdentity.Capture(sourcePath);
        if (!binding.SourceFileIdentity.Matches(currentIdentity) ||
            (request.SourceArtifact.FileIdentity != null && !request.SourceArtifact.FileIdentity.Matches(currentIdentity)))
            throw new InvalidDataException("Source filesystem object identity changed after Neutral binding.");

        await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualSha256, binding.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Source artifact content hash changed after Neutral binding.");
        }

        SourceMediaFacts observed = await NativeMediaService
            .InspectMediaAsync(sourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        GainMapFacts gainMap = observed.GainMap
            ?? throw new InvalidDataException("Native inspection no longer reports the bound GainMap.");
        if (!gainMap.IsPresent || gainMap.AuxiliaryIndex != binding.AuxiliaryIndex ||
            gainMap.ItemId != binding.ItemId || gainMap.Container != binding.SourceContainer ||
            gainMap.ByteOffset != binding.ByteOffset || gainMap.ByteLength != binding.ByteLength ||
            gainMap.Representation != binding.Representation || gainMap.Ownership != binding.Ownership ||
            gainMap.OwnerArtifactRole != binding.OwnerArtifactRole ||
            !string.Equals(gainMap.Relationship, binding.Relationship, StringComparison.Ordinal) ||
            gainMap.AuxiliaryIndex >= observed.AuxiliaryItems.Count)
            throw new InvalidDataException("Native source facts disagree with the bound GainMap item, range, owner, or relationship.");

        AuxiliaryMediaFacts auxiliary = observed.AuxiliaryItems[checked((int)gainMap.AuxiliaryIndex)];
        if (!auxiliary.IsPresent || auxiliary.ItemId != binding.ItemId ||
            auxiliary.Representation != binding.Representation || auxiliary.Ownership != binding.Ownership ||
            auxiliary.ByteOffset != binding.ByteOffset || auxiliary.ByteLength != binding.ByteLength ||
            !string.Equals(auxiliary.StableIdentity, binding.InspectedStableIdentity, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Semantic, binding.Semantic, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.OwnerIdentity, binding.InspectedOwnerIdentity, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Relationship, binding.Relationship, StringComparison.Ordinal) ||
            !string.Equals(auxiliary.Sha256, binding.GainMapSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native auxiliary graph no longer matches the bound GainMap bytes and identity.");

        if (!string.IsNullOrWhiteSpace(observed.PrimarySha256) &&
            !string.Equals(observed.PrimarySha256, binding.PrimaryImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native primary-image SHA-256 differs from the bound inspected image item.");

        return ImageConversionEligibility.ToConversionFacts(observed);
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
        try
        {
            transaction = heicToJpeg
                ? await NativeMediaService.StageHeicGainMapToJpegUltraHdrAsync(
                    request.SourceArtifact.Path, outputPath, binding, request.SourceArtifact.ByteLength,
                    request.Quality, request.HdrOutputPolicy, cancellationToken).ConfigureAwait(false)
                : await NativeMediaService.StageJpegGainMapToHeicAsync(
                    request.SourceArtifact.Path, outputPath, binding, request.SourceArtifact.ByteLength,
                    request.Quality, request.HdrOutputPolicy, cancellationToken).ConfigureAwait(false);
            stageCreated = true;

            ImageHdrGainMapPostValidation post = heicToJpeg
                ? await ImagePostValidator.VerifyHeicToJpegUltraHdrAsync(
                    request.SourceArtifact.Path, transaction, transaction.Result, cancellationToken).ConfigureAwait(false)
                : await ImagePostValidator.VerifyJpegToHeicGainMapAsync(
                    request.SourceArtifact.Path, transaction, transaction.Result, cancellationToken).ConfigureAwait(false);
            // Rebind the source after post-validation and before commit so a
            // changed source cannot be reported as the input of this result.
            await VerifyHdrGainMapBindingAsync(request, binding, cancellationToken).ConfigureAwait(false);

            if (request.PreservationPolicy == PreservationPolicy.Strict &&
                !IsFullyPreserved(post.Preservation.Metadata) ||
                request.PreservationPolicy == PreservationPolicy.Strict &&
                !IsFullyPreserved(post.Preservation.Orientation) ||
                request.PreservationPolicy == PreservationPolicy.Strict &&
                !IsFullyPreserved(post.Preservation.ColorIcc))
            {
                throw new InvalidDataException(
                    "Strict HDR/GainMap conversion could not prove every applicable metadata, orientation, and ICC component before commit.");
            }

            FileInfo stagedFile = new(transaction.StagingPath);
            if (!stagedFile.Exists || stagedFile.Length <= 0)
                throw new FileNotFoundException("Native HDR/GainMap stage disappeared before commit.", transaction.StagingPath);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
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
                    ByteLength = stagedFile.Length
                },
                ExecutionRecord = new ImageExecutionRecord
                {
                    InputContainer = binding.SourceContainer,
                    OutputContainer = request.TargetContainer,
                    PixelReencoded = true,
                    MetadataCopied = false,
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
            if (transaction is not null && !AbortTransaction(transaction))
                throw new IOException("HDR/GainMap conversion was cancelled, but Native could not confirm stage cleanup.");
            transaction = null;
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            bool cleanupSucceeded = transaction is null || AbortTransaction(transaction);
            transaction = null;
            ConversionFailureStage stage = !cleanupSucceeded
                ? ConversionFailureStage.Cleanup
                : stageCreated ? ConversionFailureStage.OutputValidation : ConversionFailureStage.BackendExecution;
            ConversionFailureCategory category = !cleanupSucceeded
                ? ConversionFailureCategory.CleanupFailure
                : stageCreated ? ConversionFailureCategory.OutputValidation :
                    IsBackendUnavailable(ex) ? ConversionFailureCategory.BackendUnavailable : ConversionFailureCategory.BackendFailure;
            return HdrGainMapFailure(request, factsOrigin, ex.Message, stage, category, sw.Elapsed, allowedFallback);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static bool AbortTransaction(NativeHdrGainMapConversionTransaction transaction)
    {
        bool succeeded = true;
        try { transaction.Abort(); }
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
