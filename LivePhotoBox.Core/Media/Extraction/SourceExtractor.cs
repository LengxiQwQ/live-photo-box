using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Thin control plane wrapper that orchestrates workspace paths and delegates byte extraction to LivePhotoBox.Native.
/// </summary>
public sealed class SourceExtractor : ISourceExtractor
{
    internal static AsyncLocal<Action?> BeforeCleanupPlanIssueTestHook { get; } = new();

    public Task<ExtractedMediaBundle> ExtractAsync(
        ExtractionPlan plan,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        return ExtractAsync(plan, primaryPath, secondaryPath, workspace, configureContext: null, cancellationToken);
    }

    internal async Task<ExtractedMediaBundle> ExtractAsync(
        ExtractionPlan plan,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        Action<NativeContext>? configureContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Claim before any managed argument validation, cancellation check,
        // source preflight, workspace allocation, or SHA computation.
        using ExtractionPlanAttempt attempt = plan.BeginExtractionAttempt(cancellationToken);
        var result = await ExtractCoreAsync(
            attempt,
            primaryPath,
            secondaryPath,
            workspace,
            configureContext,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result.Bundle!;
    }

    internal async Task<ExtractionProductAttempt> TryExtractAsync(ExtractionPlan plan,string primaryPath,
        string? secondaryPath,IMediaWorkspace workspace,CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null,new OperationCanceledException(token));
        using var attempt = plan.BeginExtractionAttempt(token);
        return await ExtractCoreAsync(attempt,primaryPath,secondaryPath,workspace,null,token).ConfigureAwait(false);
    }

    private static async Task<MediaFileHashAttempt> HashProductAsync(IMediaWorkspace workspace,string path,CancellationToken token)
        => workspace is MediaWorkspace
            ? await MediaFileHashAttempt.ComputeAsync(path,token).ConfigureAwait(false)
            : new(await workspace.ComputeFileSha256Async(path,token).ConfigureAwait(false),null);

    private static async Task<ExtractionProductAttempt> ExtractCoreAsync(
        ExtractionPlanAttempt attempt,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        Action<NativeContext>? configureContext,
        CancellationToken cancellationToken)
    {
        SourceMediaFacts facts = attempt.Facts;
        ArgumentNullException.ThrowIfNull(primaryPath);
        ArgumentNullException.ThrowIfNull(workspace);

        if(cancellationToken.IsCancellationRequested) return new(null,new OperationCanceledException(cancellationToken));
        AuxiliaryFactsValidator.Validate(facts);

        if (!File.Exists(primaryPath))
            return new(null, new FileNotFoundException("Primary media file not found.", primaryPath));

        using var primaryRead = SourceReadAttempt.Open(primaryPath);
        if (!primaryRead.Succeeded || !primaryRead.TryGetLength(out long primaryFileLength, out _))
            return new(null, new IOException($"Cannot retain extraction source (system error {primaryRead.Error})."));

        // 1. Validate Primary Image facts & range
        if (!facts.PrimaryImage.IsPresent)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary image must be present in source facts.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        if (facts.PrimaryImage.Container == ImageContainer.Unknown)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.UnsupportedLayout,
                "Primary image container format is unknown or unsupported.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        if (RangeFailure(facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength, primaryFileLength, "primary image", primaryPath, MediaArtifactKind.PrimaryImage) is { } primaryRangeFailure)
            return new(null, primaryRangeFailure);

        // 2. Validate Motion Video facts & range
        string? videoSource = null;
        if (facts.MotionVideo is { IsPresent: true } videoFacts)
        {
            if (videoFacts.Container == VideoContainer.Unknown)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "Motion video container format is unknown or unsupported.",
                    artifactKind: MediaArtifactKind.MotionVideo);
            }

            if (videoFacts.SourceIndex == 1)
            {
                if (string.IsNullOrWhiteSpace(secondaryPath) || !File.Exists(secondaryPath))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.InvalidFacts,
                        "Motion video specifies secondary source, but secondary file does not exist.",
                        artifactKind: MediaArtifactKind.MotionVideo,
                        sourcePath: secondaryPath);
                }
                videoSource = secondaryPath;
            }
            else if (videoFacts.SourceIndex == 0)
            {
                videoSource = primaryPath;
            }
            else
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Motion video specifies invalid SourceIndex: {videoFacts.SourceIndex}.",
                    artifactKind: MediaArtifactKind.MotionVideo);
            }

            using var videoRead = SourceReadAttempt.Open(videoSource);
            if (!videoRead.Succeeded || !videoRead.TryGetLength(out long videoSourceLength, out _))
                return new(null, new IOException($"Cannot retain extraction video (system error {videoRead.Error})."));
            if (RangeFailure(videoFacts.ByteOffset, videoFacts.ByteLength, videoSourceLength, "motion video", videoSource, MediaArtifactKind.MotionVideo) is { } videoRangeFailure)
                return new(null, videoRangeFailure);
        }

        // 3. Validate GainMap facts & range
        if (facts.GainMap is { IsPresent: true } gainMapFacts)
        {
            if (gainMapFacts.Container == ImageContainer.Unknown)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "GainMap container format is unknown or unsupported.",
                    artifactKind: MediaArtifactKind.GainMap,
                    sourcePath: primaryPath);
            }

            ValidateRange(gainMapFacts.ByteOffset, gainMapFacts.ByteLength, primaryFileLength, "GainMap", primaryPath, MediaArtifactKind.GainMap);
        }

        if (facts.ProtocolTailLength > 0)
        {
            ValidateRange(facts.ProtocolTailOffset, facts.ProtocolTailLength, primaryFileLength, "protocol trailer", primaryPath, null);
        }

        if (string.IsNullOrWhiteSpace(facts.PrimarySha256))
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary source snapshot SHA-256 is required and cannot be empty.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        byte[] primShaBytes;
        try
        {
            primShaBytes = Convert.FromHexString(facts.PrimarySha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Primary source snapshot SHA-256 is malformed: '{facts.PrimarySha256}'.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath,
                innerException: ex);
        }

        if (primShaBytes.Length != 32)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Primary source snapshot SHA-256 must be 32 bytes (64 hex characters), got {primShaBytes.Length} bytes.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        bool isAllZero = true;
        for (int i = 0; i < 32; i++)
        {
            if (primShaBytes[i] != 0) { isAllZero = false; break; }
        }
        if (isAllZero)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary source snapshot SHA-256 cannot be all zeroes.",
                artifactKind: MediaArtifactKind.PrimaryImage,
                sourcePath: primaryPath);
        }

        var primaryHash=await HashProductAsync(workspace,primaryPath,cancellationToken).ConfigureAwait(false);
        if(primaryHash.Failure is { } primaryHashFailure) return new(null,primaryHashFailure);
        string beforePrimarySha=primaryHash.Sha256!;
        if (!string.Equals(facts.PrimarySha256.Trim(), beforePrimarySha, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, new ExtractionException(
                ExtractionFailureCategory.SourceChanged,
                $"Source media file '{primaryPath}' was modified after inspection: SHA-256 mismatch.",
                sourcePath: primaryPath));
        }

        string? beforeSecondarySha = null;
        bool auxiliaryUsesSecondary = facts.AuxiliaryItems.Any(item => item.IsPresent && item.SourceIndex == 1);
        if ((videoSource == secondaryPath || auxiliaryUsesSecondary) && secondaryPath != null)
        {
            if (string.IsNullOrWhiteSpace(facts.SecondarySha256))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    "Secondary source snapshot SHA-256 is required when an inspected relationship resides in secondary source.",
                    artifactKind: auxiliaryUsesSecondary ? MediaArtifactKind.AuxiliaryItem : MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath);
            }

            byte[] secShaBytes;
            try
            {
                secShaBytes = Convert.FromHexString(facts.SecondarySha256.Trim());
            }
            catch (FormatException ex)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Secondary source snapshot SHA-256 is malformed: '{facts.SecondarySha256}'.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath,
                    innerException: ex);
            }

            if (secShaBytes.Length != 32)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Secondary source snapshot SHA-256 must be 32 bytes (64 hex characters), got {secShaBytes.Length} bytes.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath);
            }

            bool secIsAllZero = true;
            for (int i = 0; i < 32; i++)
            {
                if (secShaBytes[i] != 0) { secIsAllZero = false; break; }
            }
            if (secIsAllZero)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    "Secondary source snapshot SHA-256 cannot be all zeroes.",
                    artifactKind: MediaArtifactKind.MotionVideo,
                    sourcePath: secondaryPath);
            }

            var secondaryHash1=await HashProductAsync(workspace,secondaryPath,cancellationToken).ConfigureAwait(false);
            if(secondaryHash1.Failure is { } secondaryHashFailure1) return new(null,secondaryHashFailure1);
            beforeSecondarySha=secondaryHash1.Sha256!;
            if (!string.Equals(facts.SecondarySha256.Trim(), beforeSecondarySha, StringComparison.OrdinalIgnoreCase))
            {
                return new(null, new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Secondary source file '{secondaryPath}' was modified after inspection: SHA-256 mismatch.",
                    sourcePath: secondaryPath));
            }
        }
        else if (auxiliaryUsesSecondary)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "An inspected auxiliary relationship specifies secondary source, but no secondary file was provided.",
                artifactKind: MediaArtifactKind.AuxiliaryItem,
                sourcePath: secondaryPath);
        }
        else if (!string.IsNullOrWhiteSpace(facts.SecondarySha256) && secondaryPath != null && File.Exists(secondaryPath))
        {
            var secondaryHash2=await HashProductAsync(workspace,secondaryPath,cancellationToken).ConfigureAwait(false);
            if(secondaryHash2.Failure is { } secondaryHashFailure2) return new(null,secondaryHashFailure2);
            beforeSecondarySha=secondaryHash2.Sha256!;
            if (!string.Equals(facts.SecondarySha256.Trim(), beforeSecondarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Secondary source file '{secondaryPath}' was modified after inspection: SHA-256 mismatch.",
                    sourcePath: secondaryPath);
            }
        }

        // Validate every Inspector-confirmed auxiliary before allocating any
        // output.  The extractor never re-guesses protocol meaning; it only
        // consumes the immutable range/identity/relationship snapshot.
        for (int i = 0; i < facts.AuxiliaryItems.Count; i++)
        {
            AuxiliaryMediaFacts auxiliary = facts.AuxiliaryItems[i];
            if (!auxiliary.IsPresent) continue;
            if (string.IsNullOrWhiteSpace(auxiliary.StableIdentity) ||
                string.IsNullOrWhiteSpace(auxiliary.Semantic) ||
                string.IsNullOrWhiteSpace(auxiliary.OwnerIdentity) ||
                string.IsNullOrWhiteSpace(auxiliary.Relationship) ||
                string.IsNullOrWhiteSpace(auxiliary.Sha256))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Auxiliary item {i} is missing stable identity, semantic, ownership, relationship, or SHA-256.",
                    artifactKind: MediaArtifactKind.AuxiliaryItem,
                    sourcePath: auxiliary.SourceIndex == 1 ? secondaryPath : primaryPath);
            }

            string auxiliarySource = auxiliary.SourceIndex switch
            {
                0 => primaryPath,
                1 when !string.IsNullOrWhiteSpace(secondaryPath) && File.Exists(secondaryPath) => secondaryPath!,
                1 => throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Auxiliary item {i} specifies a missing secondary source.",
                    artifactKind: MediaArtifactKind.AuxiliaryItem,
                    sourcePath: secondaryPath),
                _ => throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Auxiliary item {i} specifies invalid SourceIndex {auxiliary.SourceIndex}.",
                    artifactKind: MediaArtifactKind.AuxiliaryItem)
            };
            using var auxiliaryRead = SourceReadAttempt.Open(auxiliarySource);
            if (!auxiliaryRead.Succeeded || !auxiliaryRead.TryGetLength(out long auxiliaryLength, out _))
                return new(null, new IOException($"Cannot retain extraction auxiliary source (system error {auxiliaryRead.Error})."));
            if (RangeFailure(auxiliary.ByteOffset, auxiliary.ByteLength, auxiliaryLength, $"auxiliary item {i}", auxiliarySource,
                MediaArtifactKind.AuxiliaryItem) is { } auxiliaryRangeFailure) return new(null, auxiliaryRangeFailure);
            ValidateSha256(auxiliary.Sha256, $"Auxiliary item {i}");
        }

        string imgExt = facts.PrimaryImage.Container == ImageContainer.Heic ? ".heic" : ".jpg";
        string outputImagePath = workspace.AllocateFilePath("primary", imgExt);
        if (File.Exists(outputImagePath))
            throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, $"Destination file already exists: {outputImagePath}");

        bool needsCleanupSource = facts.Protocol == SourceProtocol.SamsungMotionPhotoJpeg &&
            facts.PreservationCarriers.Any(carrier =>
                carrier.Kind == PreservationCarrierKind.SamsungSef && carrier.SourceIndex == 0);
        string? cleanupSourcePath = needsCleanupSource
            ? workspace.AllocateFilePath("cleanup-source", imgExt)
            : null;
        string? cleanupSourceSha256 = needsCleanupSource ? beforePrimarySha : null;
        // Samsung's full source-container artifact is now copied by the same
        // Native handle-owned transaction as the extracted media.  Managed
        // code must never establish its ownership by reopening a path.

        string? outputVideoPath = null;
        if (facts.MotionVideo != null && facts.MotionVideo.IsPresent)
        {
            string vidExt = facts.MotionVideo.Container == VideoContainer.Mov ? ".mov" : ".mp4";
            outputVideoPath = workspace.AllocateFilePath("motion", vidExt);
            if (File.Exists(outputVideoPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, $"Destination file already exists: {outputVideoPath}");
        }

        bool canMaterializeTypedGainMap = false;
        if (facts.GainMap is { IsPresent: true } typedGainMap &&
            typedGainMap.AuxiliaryIndex < (uint)facts.AuxiliaryItems.Count)
        {
            AuxiliaryMediaFacts typedAuxiliary = facts.AuxiliaryItems[(int)typedGainMap.AuxiliaryIndex];
            // A JPEG GainMap slice is an independently valid working artifact.
            // HEIF auxiliary items remain Embedded until W3 proves a complete
            // standalone item graph; never emit a pseudo-.heic extent here.
            if (typedAuxiliary.Container == ImageContainer.Heic &&
                typedAuxiliary.Representation != AuxiliaryRepresentation.Embedded)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "HEIF GainMap materialization requires a complete standalone item graph; raw iloc extents are not artifacts.",
                    MediaArtifactKind.GainMap);
            }
            canMaterializeTypedGainMap = typedAuxiliary.Container == ImageContainer.Jpeg;
        }

        string? outputGainmapPath = null;
        if (canMaterializeTypedGainMap)
        {
            string gmExt = facts.GainMap?.Container == ImageContainer.Heic ? ".heic" : ".jpg";
            outputGainmapPath = workspace.AllocateFilePath("gainmap", gmExt);
            if (File.Exists(outputGainmapPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, $"Destination file already exists: {outputGainmapPath}");
        }

        var auxiliaryDescriptors = new List<AuxiliaryMediaDescriptor>(facts.AuxiliaryItems.Count);
        var auxiliaryOutputBindings = new List<NativeMediaService.NativeAuxiliaryOutputBinding>();
        var auxiliaryOutputPaths = new List<string>();
        var auxiliaryOutputByIndex = new Dictionary<int, string?>();
        for (int i = 0; i < facts.AuxiliaryItems.Count; i++)
        {
            AuxiliaryMediaFacts auxiliary = facts.AuxiliaryItems[i];
            if (!auxiliary.IsPresent) continue;

            if (auxiliary.Container == ImageContainer.Heic &&
                auxiliary.Representation == AuxiliaryRepresentation.Materialized)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    $"HEIF auxiliary '{auxiliary.StableIdentity}' does not have a legal standalone materialization.",
                    MediaArtifactKind.AuxiliaryItem);
            }

            bool isGainMap = facts.GainMap is { IsPresent: true } gainMapBinding &&
                gainMapBinding.AuxiliaryIndex == (uint)i;
            bool materialize = (isGainMap && canMaterializeTypedGainMap) ||
                auxiliary.Representation == AuxiliaryRepresentation.Materialized;
            string? materializedPath = isGainMap
                ? outputGainmapPath
                : materialize
                    ? workspace.AllocateFilePath($"aux-{SanitizeArtifactStem(auxiliary.Semantic)}-{i}",
                        AuxiliaryExtension(auxiliary))
                    : null;

            if (materializedPath != null)
            {
                if (File.Exists(materializedPath))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.OutputWriteFailed,
                        $"Destination file already exists: {materializedPath}",
                        artifactKind: isGainMap ? MediaArtifactKind.GainMap : MediaArtifactKind.AuxiliaryItem);
                }
                auxiliaryOutputPaths.Add(materializedPath);
                if (!isGainMap)
                {
                    auxiliaryOutputBindings.Add(new NativeMediaService.NativeAuxiliaryOutputBinding(
                        (uint)i, materializedPath));
                }
            }
            auxiliaryOutputByIndex[i] = materializedPath;

            auxiliaryDescriptors.Add(new AuxiliaryMediaDescriptor
            {
                ArtifactRole = isGainMap ? MediaArtifactKind.GainMap : MediaArtifactKind.AuxiliaryItem,
                StableIdentity = auxiliary.StableIdentity,
                Semantic = auxiliary.Semantic,
                OwnerIdentity = auxiliary.OwnerIdentity,
                Relationship = auxiliary.Relationship,
                SourceIndex = auxiliary.SourceIndex,
                SourceOffset = auxiliary.ByteOffset,
                SourceLength = auxiliary.ByteLength,
                SourceSha256 = auxiliary.Sha256,
                ImageContainer = auxiliary.Container,
                Codec = auxiliary.Codec,
                Representation = auxiliary.Representation,
                Ownership = auxiliary.Ownership,
                ItemType = auxiliary.ItemType,
                GraphComplete = auxiliary.GraphComplete,
                Dependencies = auxiliary.Dependencies
            });
        }

        var carrierOutputBindings=new List<NativeMediaService.NativeCarrierOutputBinding>();
        for(int i=0;i<facts.PreservationCarriers.Count;i++)
        {
            var carrier=facts.PreservationCarriers[i];
            if(!OrdinaryMetadataPreservation.IsRecordCarrier(carrier))continue;
            if(carrier.Kind!=PreservationCarrierKind.OpaqueFragment||carrier.SourceIndex!=0||
                carrier.ArtifactRole!=MediaArtifactKind.PrimaryImage||carrier.OwnerIdentity!="primary:0"||
                carrier.Relationship!=OrdinaryMetadataPreservation.Relationship)
                throw new ExtractionException(ExtractionFailureCategory.InvalidFacts,"Ordinary metadata source ownership is incomplete.");
            ValidateRange(carrier.SourceOffset,carrier.SourceLength,primaryFileLength,"ordinary metadata",primaryPath,MediaArtifactKind.MetadataCarrier);
            carrierOutputBindings.Add(new((uint)i,workspace.AllocateFilePath($"ordinary-metadata-{i}",".bin")));
        }

        // Keep a pre-publish expectation for every artifact that this managed
        // layer may later clean up.  The expectation is derived from a real
        // source read handle, not from a path-only post-publish read.  Native
        // validates the same slice while its transaction-owned output handle
        // is still open; this second record lets managed rollback prove that
        // it is still looking at that transaction's object.
        bool nativeExtractionSucceeded = false;
        bool cleanupAuthorityIssued = false;
        CleanupPlan? unhandedCleanupPlan = null;
        IReadOnlyList<NativeMediaService.NativeExtractionOutputEvidence>? extractionOutputReceipts = null;
        try
        {
            Exception? nativeFailure;
            if(carrierOutputBindings.Count!=0)
                nativeFailure = await NativeMediaService.TryExtractMediaWithCarrierOutputsAsync(attempt,primaryPath,secondaryPath,
                    outputImagePath,outputVideoPath,outputGainmapPath,cleanupSourcePath,auxiliaryOutputBindings,
                    carrierOutputBindings,configureContext,cancellationToken).ConfigureAwait(false);
            else nativeFailure = await NativeMediaService.TryExtractMediaAsync(
                attempt,
                primaryPath,
                secondaryPath,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath,
                auxiliaryOutputBindings,
                cleanupSourcePath,
                configureContext,
                cancellationToken).ConfigureAwait(false);

            if (nativeFailure is not null) return Failed(nativeFailure);
            nativeExtractionSucceeded = true;

            NativeResult nativeOutputVerification = NativeMethods.VerifyExtractionOutputs(
                attempt.ContextLease.Handle,
                attempt.NativeHandle,
                attempt.Generation);
            if (nativeOutputVerification != NativeResult.Ok) return Failed(attempt.Context.CreateFailure(nativeOutputVerification));

            // Ownership comes from Native's creating-handle extraction record,
            // queried before CommitExtractionOutputs releases its authority.
            // A pathname capture paired with a later verification cannot issue
            // a cleanup receipt for a file that took over the pathname.
            var expectedOutputs = new List<NativeMediaService.NativeExtractionOutputBinding>
            {
                new(MediaArtifactKind.PrimaryImage, uint.MaxValue, outputImagePath)
            };
            if (outputVideoPath != null)
                expectedOutputs.Add(new(MediaArtifactKind.MotionVideo, uint.MaxValue, outputVideoPath));
            if (outputGainmapPath != null)
                expectedOutputs.Add(new(MediaArtifactKind.GainMap, uint.MaxValue, outputGainmapPath));
            if (cleanupSourcePath != null)
                expectedOutputs.Add(new(MediaArtifactKind.SourceContainer, uint.MaxValue, cleanupSourcePath));
            foreach (NativeMediaService.NativeAuxiliaryOutputBinding auxiliary in auxiliaryOutputBindings)
                expectedOutputs.Add(new(MediaArtifactKind.AuxiliaryItem, auxiliary.AuxiliaryIndex, auxiliary.Path));
            foreach(var carrier in carrierOutputBindings)
                expectedOutputs.Add(new(MediaArtifactKind.MetadataCarrier,carrier.CarrierIndex,carrier.Path));
            var evidenceAttempt = NativeMediaService.TryCaptureExtractionOutputEvidence(attempt, expectedOutputs);
            if (evidenceAttempt.Failure is { } ownershipFailure)
                return Failed(new ExtractionException(
                    ExtractionFailureCategory.OutputPublishFailed,
                    "Native extraction ownership could not be handed off to the workspace.",
                    innerException: ownershipFailure));
            IReadOnlyList<NativeMediaService.NativeExtractionOutputEvidence> ownedOutputs = evidenceAttempt.Evidence!;
            extractionOutputReceipts = ownedOutputs;
            try
            {
                foreach (var output in ownedOutputs)
                    WorkspaceOwnershipRegistry.RegisterCreatedFile(output.FinalPath, output.FileIdentity,
                        output.ByteLength, output.Sha256, output.ArtifactRole.ToString());
            }
            catch (Exception registryFailure)
            {
                return Failed(new ExtractionException(ExtractionFailureCategory.OutputPublishFailed,
                    "Native extraction ownership could not be handed off to the workspace.", innerException: registryFailure));
            }
            var publishedArtifactIdentities = expectedOutputs.ToDictionary(
                binding => binding.Path,
                binding => ownedOutputs.Single(output =>
                    output.ArtifactRole == binding.ArtifactRole &&
                    output.AuxiliaryIndex == binding.AuxiliaryIndex).FileIdentity,
                StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(outputImagePath))
                return Failed(new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a primary image artifact.", MediaArtifactKind.PrimaryImage));
            if (outputVideoPath != null && !File.Exists(outputVideoPath))
                return Failed(new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a motion video artifact.", MediaArtifactKind.MotionVideo));
            if (outputGainmapPath != null && !File.Exists(outputGainmapPath))
                return Failed(new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a GainMap artifact.", MediaArtifactKind.GainMap));
            foreach (string auxiliaryOutputPath in auxiliaryOutputPaths)
            {
                if (!File.Exists(auxiliaryOutputPath))
                {
                    return Failed(new ExtractionException(
                        ExtractionFailureCategory.OutputWriteFailed,
                        $"Native extraction did not produce the confirmed auxiliary artifact '{auxiliaryOutputPath}'.",
                        MediaArtifactKind.AuxiliaryItem));
                }
            }

            // Verify that source files were not modified in-place
            var primaryAfter=await HashProductAsync(workspace,primaryPath,cancellationToken).ConfigureAwait(false);
            if(primaryAfter.Failure is { } primaryAfterFailure) return Failed(primaryAfterFailure);
            string afterPrimarySha=primaryAfter.Sha256!;
            if (!string.Equals(beforePrimarySha, afterPrimarySha, StringComparison.OrdinalIgnoreCase))
            {
                return Failed(new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Source file immutability violation: primary source '{primaryPath}' was modified during extraction!",
                    sourcePath: primaryPath));
            }

            if (secondaryPath != null && beforeSecondarySha != null)
            {
                var secondaryAfter=await HashProductAsync(workspace,secondaryPath,cancellationToken).ConfigureAwait(false);
                if(secondaryAfter.Failure is { } secondaryAfterFailure) return Failed(secondaryAfterFailure);
                string afterSecondarySha=secondaryAfter.Sha256!;
                if (!string.Equals(beforeSecondarySha, afterSecondarySha, StringComparison.OrdinalIgnoreCase))
                {
                    return Failed(new ExtractionException(
                        ExtractionFailureCategory.SourceChanged,
                        $"Source file immutability violation: secondary source '{secondaryPath}' was modified during extraction!",
                        sourcePath: secondaryPath));
                }
            }

            MediaArtifact? cleanupSourceArtifact = null;
            if (cleanupSourcePath != null)
            {
                cleanupSourceArtifact = new MediaArtifact
                {
                    Path = cleanupSourcePath,
                    Kind = MediaArtifactKind.SourceContainer,
                    MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                    ImageContainer = facts.PrimaryImage.Container,
                    ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteLength = ownedOutputs.Single(output => string.Equals(output.FinalPath, cleanupSourcePath, StringComparison.OrdinalIgnoreCase)).ByteLength,
                    SourceOffset = 0,
                    Sha256 = cleanupSourceSha256,
                    FileIdentity = publishedArtifactIdentities[cleanupSourcePath]
                };
            }

            var finalHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(string hashedPath in new[]{outputImagePath,outputVideoPath,outputGainmapPath}.Concat(auxiliaryOutputPaths)
                .Where(p=>p is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var hashed=await HashProductAsync(workspace,hashedPath,cancellationToken).ConfigureAwait(false);
                if(hashed.Failure is { } hashFailure) return Failed(hashFailure);
                finalHashes[hashedPath]=hashed.Sha256!;
            }

            var primaryArtifact = new MediaArtifact
            {
                Path = outputImagePath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = facts.PrimaryImage.Container,
                ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = ownedOutputs.Single(output => string.Equals(output.FinalPath, outputImagePath, StringComparison.OrdinalIgnoreCase)).ByteLength,
                SourceOffset = facts.PrimaryImage.ByteOffset,
                FileIdentity = publishedArtifactIdentities[outputImagePath],
                Sha256 = finalHashes[outputImagePath]
            };

            MediaArtifact? videoArtifact = null;
            if (outputVideoPath != null && File.Exists(outputVideoPath))
            {
                videoArtifact = new MediaArtifact
                {
                    Path = outputVideoPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = facts.MotionVideo!.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                    VideoContainer = facts.MotionVideo.Container,
                    VideoCodec = facts.MotionVideo.Codec,
                    ByteLength = ownedOutputs.Single(output => string.Equals(output.FinalPath, outputVideoPath, StringComparison.OrdinalIgnoreCase)).ByteLength,
                    SourceOffset = facts.MotionVideo.ByteOffset,
                    FileIdentity = publishedArtifactIdentities[outputVideoPath],
                    Sha256 = finalHashes[outputVideoPath]
                };
            }

            MediaArtifact? gainmapArtifact = null;
            if (outputGainmapPath != null && File.Exists(outputGainmapPath))
            {
                gainmapArtifact = new MediaArtifact
                {
                    Path = outputGainmapPath,
                    Kind = MediaArtifactKind.GainMap,
                    MimeType = facts.GainMap!.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                    ImageContainer = facts.GainMap.Container,
                    ImageCodec = facts.GainMap.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteLength = ownedOutputs.Single(output => string.Equals(output.FinalPath, outputGainmapPath, StringComparison.OrdinalIgnoreCase)).ByteLength,
                    SourceOffset = facts.GainMap.ByteOffset,
                    FileIdentity = publishedArtifactIdentities[outputGainmapPath],
                    Sha256 = finalHashes[outputGainmapPath]
                };
            }

            var finalizedAuxiliaryDescriptors = new List<AuxiliaryMediaDescriptor>(auxiliaryDescriptors.Count);
            int descriptorCursor = 0;
            for (int i = 0; i < facts.AuxiliaryItems.Count; i++)
            {
                AuxiliaryMediaFacts auxiliary = facts.AuxiliaryItems[i];
                if (!auxiliary.IsPresent) continue;

                AuxiliaryMediaDescriptor descriptor = auxiliaryDescriptors[descriptorCursor++];
                MediaArtifact? materializedArtifact = null;
                if (auxiliaryOutputByIndex.TryGetValue(i, out string? path) && path != null)
                {
                    bool isGainMap = facts.GainMap is { IsPresent: true } gainMapBindingFinal &&
                        gainMapBindingFinal.AuxiliaryIndex == (uint)i;
                    if (isGainMap)
                    {
                        materializedArtifact = gainmapArtifact;
                    }
                    else
                    {
                        materializedArtifact = new MediaArtifact
                        {
                            Path = path,
                            Kind = MediaArtifactKind.AuxiliaryItem,
                            MimeType = auxiliary.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                            ImageContainer = auxiliary.Container,
                            ImageCodec = auxiliary.Codec switch
                            {
                                AuxiliaryCodec.Jpeg => ImageCodec.Jpeg,
                                AuxiliaryCodec.Hevc => ImageCodec.Hevc,
                                _ => ImageCodec.Unknown
                             },
                             ByteLength = ownedOutputs.Single(output => string.Equals(output.FinalPath, path, StringComparison.OrdinalIgnoreCase)).ByteLength,
                             SourceOffset = auxiliary.ByteOffset,
                             FileIdentity = publishedArtifactIdentities[path],
                             Sha256 = finalHashes[path]
                         };
                    }

                    if (materializedArtifact?.Sha256 is not { Length: > 0 } materializedSha ||
                        !string.Equals(materializedSha, descriptor.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ExtractionException(
                            ExtractionFailureCategory.SourceChanged,
                            $"Materialized auxiliary '{descriptor.StableIdentity}' does not match the Inspector-confirmed source SHA-256.",
                            artifactKind: descriptor.ArtifactRole,
                            sourcePath: auxiliary.SourceIndex == 1 ? secondaryPath : primaryPath);
                    }
                }
                finalizedAuxiliaryDescriptors.Add(descriptor with { MaterializedArtifact = materializedArtifact });
            }

            var finalizedCarriers=facts.PreservationCarriers.ToArray();
            foreach(var binding in carrierOutputBindings)
            {
                var carrier=finalizedCarriers[binding.CarrierIndex];
                var receipt=ownedOutputs.Single(x=>x.ArtifactRole==MediaArtifactKind.MetadataCarrier&&x.AuxiliaryIndex==binding.CarrierIndex);
                if(receipt.Sha256!=carrier.SourceSha256||receipt.ByteLength!=carrier.SourceLength)
                    throw new ExtractionException(ExtractionFailureCategory.SourceChanged,"Ordinary metadata capture differs from its Inspector-owned source bytes.");
                finalizedCarriers[binding.CarrierIndex]=carrier with
                {
                    Representation=AuxiliaryRepresentation.Materialized,
                    MaterializedArtifact=new MediaArtifact{Path=binding.Path,Kind=MediaArtifactKind.MetadataCarrier,
                        MimeType="application/x-lpb-opaque-camera-records",ByteLength=receipt.ByteLength,
                        SourceOffset=carrier.SourceOffset,Sha256=receipt.Sha256,FileIdentity=receipt.FileIdentity}
                };
            }
            var extractedFacts = new List<RemovedProtocolFact>();
            if (facts.MotionVideo is { IsPresent: true, SourceIndex: 0 })
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Embedded motion video",
                    Description = "Materialized from the Inspector-validated source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }
            if (facts.GainMap is { IsPresent: true })
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Embedded GainMap",
                    Description = "Materialized from the Inspector-validated source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }
            if (facts.ProtocolTailLength > 0)
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = carrierOutputBindings.Count!=0?"Ordinary metadata carrier":"Protocol trailer",
                    Description = carrierOutputBindings.Count!=0
                        ? "Complete ordinary record block materialized with Native creator identity and exact source SHA-256."
                        : "Excluded from the primary artifact using the Inspector-validated range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }

            // All managed verification passed. Before commit, issue the P3
            // cleanup-plan authority from this extraction record: the plan
            // inherits the Native-captured published-artifact identities
            // (volume serial + file index captured while this transaction held
            // the handles), so the Cleaner never has to re-guess ownership
            // from pathnames.  Issuing blocks the extractor rollback path
            // (cleanup_authority_issued) - the exact P2 -> P3 hand-off point.
            // Allocate the caller's bundle before Native transfers authority,
            // using the preallocated private cleanup-plan owner. Failure here
            // therefore leaves the extraction rollback authority intact.
            ExtractedMediaBundle? bundle = null;
            CleanupPlan cleanupPlanAuthority = CleanupPlan.IssueFrom(attempt, cancellationToken, owner =>
            {
                bundle = new ExtractedMediaBundle
                {
                    PrimaryImage = primaryArtifact,
                    MotionVideo = videoArtifact,
                    GainMap = gainmapArtifact,
                    CleanupSource = cleanupSourceArtifact,
                    SourceFacts = facts,
                    ExtractedProtocolFacts = extractedFacts,
                    AuxiliaryMedia = finalizedAuxiliaryDescriptors,
                    PreservationCarriers = finalizedCarriers,
                    CleanupPlan = owner
                };
                BeforeCleanupPlanIssueTestHook.Value?.Invoke();
            });
            unhandedCleanupPlan = cleanupPlanAuthority;
            cleanupAuthorityIssued = true;

            // Commit the Native transaction: this closes the rollback handles
            // so the output files are no longer locked with write+delete
            // access and can be read normally by the caller. After commit the
            // artifacts are owned by the caller/workspace, so a later
            // exception must not roll them back.
            NativeResult commitResult = NativeMethods.CommitExtractionOutputs(
                attempt.ContextLease.Handle,
                attempt.NativeHandle,
                attempt.Generation);
            if (commitResult != NativeResult.Ok)
            {
                return Failed(attempt.Context.CreateFailure(commitResult));
            }
            nativeExtractionSucceeded = false;

            unhandedCleanupPlan = null;
            return bundle!; // Constructed synchronously before Native issuance.
        }
        catch (Exception ex)
        {
            return Failed(ex);
        }
        ExtractionProductAttempt Failed(Exception ex)
        {
            // If Native failed, Native already rolled back its staged/published outputs.
            // Under no circumstances should C# delete files if Native failed, because any file
            // existing at the output path was NOT published by this transaction (e.g. race/TOCTOU sentinel).
            // Only if Native succeeded and subsequent managed validation failed should C# delete published outputs.
            string? cleanupFailedFile = null;
            Exception? cleanupError = null;

            try { unhandedCleanupPlan?.Dispose(); }
            catch (Exception releaseFailure)
            {
                cleanupFailedFile = cleanupSourcePath ?? outputImagePath;
                cleanupError = releaseFailure;
            }

            if (nativeExtractionSucceeded && !cleanupAuthorityIssued)
            {
                NativeResult rollback = NativeMethods.RollbackExtractionOutputs(
                    attempt.ContextLease.Handle,
                    attempt.NativeHandle,
                    attempt.Generation);
                if (rollback != NativeResult.Ok)
                {
                    cleanupFailedFile ??= cleanupSourcePath ?? outputImagePath;
                    cleanupError ??= new IOException(
                        attempt.Context.GetLastError() ??
                        $"Native exact-object rollback returned {rollback}.");
                }
                else if (extractionOutputReceipts is not null)
                {
                    // Native returns Ok only after exact creating-handle
                    // rollback proves every registered extraction object
                    // unlinked. Retire even a partially registered handoff;
                    // pathname absence alone never issues this proof.
                    foreach (var output in extractionOutputReceipts)
                        WorkspaceOwnershipRegistry.RetireFile(output.FinalPath, output.FileIdentity,
                            OwnedObjectCleanupProof.Unlinked);
                }
            }

            if (cleanupFailedFile != null)
            {
                ExtractionFailureCategory origCat = ex switch
                {
                    ExtractionException ee => ee.Category,
                    OperationCanceledException => ExtractionFailureCategory.Cancelled,
                    _ => ExtractionFailureCategory.InternalError
                };
                return new(null,new ExtractionException(
                    ExtractionFailureCategory.CleanupFailed,
                    $"Post-extraction cleanup failed: unable to delete artifact '{cleanupFailedFile}'. Original error: {ex.Message}",
                    innerException: cleanupError,
                    originalCategory: origCat));
            }

            return new(null,ex);
        }
    }

    private static void ValidateRange(
        long offset,
        long length,
        long sourceLength,
        string name,
        string? sourcePath,
        MediaArtifactKind? kind)
    {
        if (RangeFailure(offset, length, sourceLength, name, sourcePath, kind) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception? RangeFailure(long offset, long length, long sourceLength, string name, string? sourcePath, MediaArtifactKind? kind)
    {
        if (offset < 0 || length <= 0 || offset > sourceLength || length > sourceLength - offset)
        {
            return new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Inspector returned an invalid {name} range: offset={offset}, length={length}, sourceLength={sourceLength}.",
                artifactKind: kind,
                sourcePath: sourcePath,
                offset: offset,
                length: length);
        }
        return null;
    }

    private static void ValidateSha256(string value, string name)
    {
        try
        {
            byte[] bytes = Convert.FromHexString(value.Trim());
            if (bytes.Length != 32) throw new FormatException("SHA-256 must contain 32 bytes.");
        }
        catch (FormatException ex)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"{name} SHA-256 is malformed.",
                innerException: ex);
        }
    }

    private static string AuxiliaryExtension(AuxiliaryMediaFacts facts) => facts.Container switch
    {
        ImageContainer.Heic => ".heic",
        ImageContainer.Jpeg => ".jpg",
        _ => throw new ExtractionException(
            ExtractionFailureCategory.UnsupportedLayout,
            $"Auxiliary item '{facts.Semantic}' has an unsupported container.",
            artifactKind: MediaArtifactKind.AuxiliaryItem)
    };

    private static string SanitizeArtifactStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "auxiliary";
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 48)];
        int length = 0;
        foreach (char c in value)
        {
            if (length >= buffer.Length) break;
            buffer[length++] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }
        return length == 0 ? "auxiliary" : new string(buffer[..length]);
    }
}
