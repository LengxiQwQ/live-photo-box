using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
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
    private static readonly AsyncLocal<CleanupSourceCopyTestHooks?> s_cleanupSourceCopyTestHooks = new();

    internal static IDisposable PushCleanupSourceCopyTestHooks(
        Action<string, long>? afterChunk,
        Action<string>? afterFailure)
    {
        CleanupSourceCopyTestHooks? previous = s_cleanupSourceCopyTestHooks.Value;
        s_cleanupSourceCopyTestHooks.Value = new CleanupSourceCopyTestHooks(afterChunk, afterFailure);
        return new TestHookScope(previous);
    }

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
        return await ExtractCoreAsync(
            attempt,
            primaryPath,
            secondaryPath,
            workspace,
            configureContext,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExtractedMediaBundle> ExtractCoreAsync(
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

        cancellationToken.ThrowIfCancellationRequested();
        AuxiliaryFactsValidator.Validate(facts);

        if (!File.Exists(primaryPath))
            throw new FileNotFoundException("Primary media file not found.", primaryPath);

        long primaryFileLength = new FileInfo(primaryPath).Length;

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

        ValidateRange(facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength, primaryFileLength, "primary image", primaryPath, MediaArtifactKind.PrimaryImage);

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

            long videoSourceLength = new FileInfo(videoSource).Length;
            ValidateRange(videoFacts.ByteOffset, videoFacts.ByteLength, videoSourceLength, "motion video", videoSource, MediaArtifactKind.MotionVideo);
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

        string beforePrimarySha = await workspace.ComputeFileSha256Async(primaryPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(facts.PrimarySha256.Trim(), beforePrimarySha, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(
                ExtractionFailureCategory.SourceChanged,
                $"Source media file '{primaryPath}' was modified after inspection: SHA-256 mismatch.",
                sourcePath: primaryPath);
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

            beforeSecondarySha = await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(facts.SecondarySha256.Trim(), beforeSecondarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Secondary source file '{secondaryPath}' was modified after inspection: SHA-256 mismatch.",
                    sourcePath: secondaryPath);
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
            beforeSecondarySha = await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false);
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
            ValidateRange(auxiliary.ByteOffset, auxiliary.ByteLength,
                new FileInfo(auxiliarySource).Length, $"auxiliary item {i}", auxiliarySource,
                MediaArtifactKind.AuxiliaryItem);
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

        // Keep a pre-publish expectation for every artifact that this managed
        // layer may later clean up.  The expectation is derived from a real
        // source read handle, not from a path-only post-publish read.  Native
        // validates the same slice while its transaction-owned output handle
        // is still open; this second record lets managed rollback prove that
        // it is still looking at that transaction's object.
        bool nativeExtractionSucceeded = false;
        bool cleanupAuthorityIssued = false;
        try
        {
            await NativeMediaService.ExtractMediaAsync(
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

            nativeExtractionSucceeded = true;

            // Freeze the object identities at the extraction handoff. Native
            // has already recorded and owns these outputs; the immediate
            // Native verification below closes the capture/verification race
            // before the bundle exposes the managed evidence to the cleaner.
            var publishedArtifactIdentities = new Dictionary<string, WindowsFileIdentity>(StringComparer.OrdinalIgnoreCase);
            WindowsFileIdentity CapturePublishedIdentity(string path, MediaArtifactKind kind)
            {
                try
                {
                    WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
                    publishedArtifactIdentities[path] = identity;
                    return identity;
                }
                catch (Exception ex)
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.OutputPublishFailed,
                        $"Unable to capture the Windows file identity for published {kind} artifact '{path}'.",
                        artifactKind: kind,
                        innerException: ex);
                }
            }

            CapturePublishedIdentity(outputImagePath, MediaArtifactKind.PrimaryImage);
            if (outputVideoPath != null)
                CapturePublishedIdentity(outputVideoPath, MediaArtifactKind.MotionVideo);
            if (outputGainmapPath != null)
                CapturePublishedIdentity(outputGainmapPath, MediaArtifactKind.GainMap);
            if (cleanupSourcePath != null)
                CapturePublishedIdentity(cleanupSourcePath, MediaArtifactKind.SourceContainer);
            foreach (string auxiliaryOutputPath in auxiliaryOutputPaths)
                CapturePublishedIdentity(auxiliaryOutputPath, MediaArtifactKind.AuxiliaryItem);

            NativeResult nativeOutputVerification = NativeMethods.VerifyExtractionOutputs(
                attempt.ContextLease.Handle,
                attempt.NativeHandle,
                attempt.Generation);
            attempt.Context.ThrowIfFailed(nativeOutputVerification);

            if (!File.Exists(outputImagePath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a primary image artifact.", MediaArtifactKind.PrimaryImage);
            if (outputVideoPath != null && !File.Exists(outputVideoPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a motion video artifact.", MediaArtifactKind.MotionVideo);
            if (outputGainmapPath != null && !File.Exists(outputGainmapPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native extraction did not produce a GainMap artifact.", MediaArtifactKind.GainMap);
            foreach (string auxiliaryOutputPath in auxiliaryOutputPaths)
            {
                if (!File.Exists(auxiliaryOutputPath))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.OutputWriteFailed,
                        $"Native extraction did not produce the confirmed auxiliary artifact '{auxiliaryOutputPath}'.",
                        MediaArtifactKind.AuxiliaryItem);
                }
            }

            // Verify that source files were not modified in-place
            string afterPrimarySha = await workspace.ComputeFileSha256Async(primaryPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(beforePrimarySha, afterPrimarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Source file immutability violation: primary source '{primaryPath}' was modified during extraction!",
                    sourcePath: primaryPath);
            }

            if (secondaryPath != null && beforeSecondarySha != null)
            {
                string afterSecondarySha = await workspace.ComputeFileSha256Async(secondaryPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(beforeSecondarySha, afterSecondarySha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.SourceChanged,
                        $"Source file immutability violation: secondary source '{secondaryPath}' was modified during extraction!",
                        sourcePath: secondaryPath);
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
                    ByteLength = new FileInfo(cleanupSourcePath).Length,
                    SourceOffset = 0,
                    Sha256 = cleanupSourceSha256,
                    FileIdentity = publishedArtifactIdentities[cleanupSourcePath]
                };
            }

            var primaryArtifact = new MediaArtifact
            {
                Path = outputImagePath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = facts.PrimaryImage.Container,
                ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = new FileInfo(outputImagePath).Length,
                SourceOffset = facts.PrimaryImage.ByteOffset,
                FileIdentity = publishedArtifactIdentities[outputImagePath],
                Sha256 = await workspace.ComputeFileSha256Async(outputImagePath, cancellationToken).ConfigureAwait(false)
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
                    ByteLength = new FileInfo(outputVideoPath).Length,
                    SourceOffset = facts.MotionVideo.ByteOffset,
                    FileIdentity = publishedArtifactIdentities[outputVideoPath],
                    Sha256 = await workspace.ComputeFileSha256Async(outputVideoPath, cancellationToken).ConfigureAwait(false)
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
                    ByteLength = new FileInfo(outputGainmapPath).Length,
                    SourceOffset = facts.GainMap.ByteOffset,
                    FileIdentity = publishedArtifactIdentities[outputGainmapPath],
                    Sha256 = await workspace.ComputeFileSha256Async(outputGainmapPath, cancellationToken).ConfigureAwait(false)
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
                             ByteLength = new FileInfo(path).Length,
                             SourceOffset = auxiliary.ByteOffset,
                             FileIdentity = publishedArtifactIdentities[path],
                             Sha256 = await workspace.ComputeFileSha256Async(path, cancellationToken).ConfigureAwait(false)
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
                    Component = "Protocol trailer",
                    Description = "Excluded from the primary artifact using the Inspector-validated range.",
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
            CleanupPlan? cleanupPlanAuthority = CleanupPlan.IssueFrom(attempt.Plan, cancellationToken);
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
                attempt.Context.ThrowIfFailed(commitResult);
            }
            nativeExtractionSucceeded = false;

            return new ExtractedMediaBundle
            {
                PrimaryImage = primaryArtifact,
                MotionVideo = videoArtifact,
                GainMap = gainmapArtifact,
                CleanupSource = cleanupSourceArtifact,
                SourceFacts = facts,
                ExtractedProtocolFacts = extractedFacts,
                AuxiliaryMedia = finalizedAuxiliaryDescriptors,
                PreservationCarriers = facts.PreservationCarriers,
                CleanupPlan = cleanupPlanAuthority
            };
        }
        catch (Exception ex)
        {
            // If Native failed, Native already rolled back its staged/published outputs.
            // Under no circumstances should C# delete files if Native failed, because any file
            // existing at the output path was NOT published by this transaction (e.g. race/TOCTOU sentinel).
            // Only if Native succeeded and subsequent managed validation failed should C# delete published outputs.
            string? cleanupFailedFile = null;
            Exception? cleanupError = null;

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
            }

            if (cleanupFailedFile != null)
            {
                ExtractionFailureCategory origCat = ex switch
                {
                    ExtractionException ee => ee.Category,
                    OperationCanceledException => ExtractionFailureCategory.Cancelled,
                    _ => ExtractionFailureCategory.InternalError
                };
                throw new ExtractionException(
                    ExtractionFailureCategory.CleanupFailed,
                    $"Post-extraction cleanup failed: unable to delete artifact '{cleanupFailedFile}'. Original error: {ex.Message}",
                    innerException: cleanupError,
                    originalCategory: origCat);
            }

            throw;
        }
    }

    private static async Task CopyVerifiedSourceContainerAsync(
        string sourcePath,
        string destinationPath,
        string expectedSha256,
        CleanupSourceOwnership ownership,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.SequentialScan | FileOptions.Asynchronous
                }))
            await using (var destination = new FileStream(
                destinationPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.SequentialScan | FileOptions.Asynchronous
                }))
            {
                if (!GetFileInformationByHandle(destination.SafeFileHandle, out ByHandleFileInformation info))
                {
                    throw new IOException(
                        $"GetFileInformationByHandle failed for cleanup source with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                ownership.Identity = ToPublishedArtifactIdentity(info);
                byte[] buffer = new byte[64 * 1024];
                long bytesCopied = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    bytesCopied += read;
                    s_cleanupSourceCopyTestHooks.Value?.AfterChunk?.Invoke(destinationPath, bytesCopied);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var verification = new FileStream(
                destinationPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.SequentialScan | FileOptions.Asynchronous
                });
            string actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    "Source container changed while its cleanup copy was being materialized.",
                    sourcePath: sourcePath);
            }
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            try
            {
                s_cleanupSourceCopyTestHooks.Value?.AfterFailure?.Invoke(destinationPath);
            }
            catch
            {
                // Test-only observers must never replace the production failure.
            }

            throw;
        }
        catch (Exception ex)
        {
            try
            {
                s_cleanupSourceCopyTestHooks.Value?.AfterFailure?.Invoke(destinationPath);
            }
            catch
            {
                // Test-only observers must never replace the production failure.
            }

            throw new ExtractionException(
                ExtractionFailureCategory.OutputWriteFailed,
                $"Unable to materialize the cleanup source container '{destinationPath}'.",
                artifactKind: MediaArtifactKind.SourceContainer,
                sourcePath: sourcePath,
                innerException: ex);
        }
    }

    private static async Task AddOwnedArtifactExpectationAsync(
        IDictionary<string, OwnedArtifactExpectation> expectations,
        string outputPath,
        string sourcePath,
        long sourceOffset,
        long sourceLength,
        CancellationToken cancellationToken)
    {
        string expectedSha256 = await ComputeSourceSliceSha256Async(
            sourcePath,
            sourceOffset,
            sourceLength,
            cancellationToken).ConfigureAwait(false);

        var expectation = new OwnedArtifactExpectation(outputPath, sourceLength, expectedSha256);
        if (expectations.TryGetValue(outputPath, out OwnedArtifactExpectation? existing))
        {
            if (existing!.Length != expectation.Length ||
                !string.Equals(existing.Sha256, expectation.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Output path '{outputPath}' was assigned conflicting source ranges.");
            }
            return;
        }

        expectations.Add(outputPath, expectation);
    }

    private static async Task<string> ComputeSourceSliceSha256Async(
        string sourcePath,
        long sourceOffset,
        long sourceLength,
        CancellationToken cancellationToken)
    {
        try
        {
            using var source = new FileStream(
                sourcePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.SequentialScan
                });

            if (sourceOffset < 0 || sourceLength <= 0 || sourceOffset > source.Length ||
                sourceLength > source.Length - sourceOffset)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceRangeUnreadable,
                    $"Source range is outside the source file: offset={sourceOffset}, length={sourceLength}, sourceLength={source.Length}.",
                    sourcePath: sourcePath,
                    offset: sourceOffset,
                    length: sourceLength);
            }

            source.Position = sourceOffset;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long remaining = sourceLength;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"Source range ended before {sourceLength} bytes were read from '{sourcePath}'.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.SourceRangeUnreadable,
                $"Unable to read the expected source slice from '{sourcePath}'.",
                sourcePath: sourcePath,
                offset: sourceOffset,
                length: sourceLength,
                innerException: ex);
        }
    }

    private static async Task<PublishedArtifactIdentity> CapturePublishedArtifactIdentityAsync(
        OwnedArtifactExpectation expectation,
        CancellationToken cancellationToken)
    {
        try
        {
            using FileStream artifact = OpenTransactionArtifactHandle(expectation.Path);
            if (!GetFileInformationByHandle(artifact.SafeFileHandle, out ByHandleFileInformation info))
            {
                throw new IOException(
                    $"GetFileInformationByHandle failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            PublishedArtifactIdentity identity = ToPublishedArtifactIdentity(info);
            if (IsReparsePoint(info) || identity.LinkCount != 1)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.OutputPublishFailed,
                    $"Published artifact '{expectation.Path}' is a reparse point or has an unexpected link count.");
            }

            if (GetFileLength(info) != (ulong)expectation.Length)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.OutputPublishFailed,
                    $"Published artifact '{expectation.Path}' has an unexpected length.");
            }

            artifact.Position = 0;
            string actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(artifact, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualSha256, expectation.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.OutputPublishFailed,
                    $"Published artifact '{expectation.Path}' does not match the expected source slice SHA-256.");
            }

            return identity;
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.OutputPublishFailed,
                $"Unable to validate the published artifact '{expectation.Path}'.",
                innerException: ex);
        }
    }

    private static async Task<CleanupAttempt> TryDeleteOwnedArtifactAsync(
        OwnedArtifactExpectation expectation,
        PublishedArtifactIdentity expectedIdentity,
        CancellationToken cancellationToken,
        bool requireContentMatch = true)
    {
        try
        {
            using FileStream artifact = OpenTransactionArtifactHandle(expectation.Path);
            if (!GetFileInformationByHandle(artifact.SafeFileHandle, out ByHandleFileInformation info))
            {
                return CleanupAttempt.Failed(new IOException(
                    $"GetFileInformationByHandle failed with Win32 error {Marshal.GetLastWin32Error()}.") );
            }

            PublishedArtifactIdentity actualIdentity = ToPublishedArtifactIdentity(info);
            if (IsReparsePoint(info) ||
                actualIdentity.LinkCount != 1 ||
                actualIdentity.VolumeSerialNumber != expectedIdentity.VolumeSerialNumber ||
                actualIdentity.FileIndex != expectedIdentity.FileIndex ||
                (requireContentMatch && GetFileLength(info) != (ulong)expectation.Length))
            {
                // A missing or replaced object is not ours to delete.  The
                // original extraction failure remains the authoritative
                // category; cleanup must never remove a foreign object.
                return CleanupAttempt.Preserved;
            }

            if (requireContentMatch)
            {
                artifact.Position = 0;
                string actualSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(artifact, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualSha256, expectation.Sha256, StringComparison.OrdinalIgnoreCase))
                    return CleanupAttempt.Preserved;
            }

            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            if (!SetFileInformationByHandle(
                    artifact.SafeFileHandle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
            {
                return CleanupAttempt.Failed(new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"SetFileInformationByHandle(FileDispositionInfo) failed for '{expectation.Path}'."));
            }

            return CleanupAttempt.Deleted;
        }
        catch (FileNotFoundException)
        {
            return CleanupAttempt.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return CleanupAttempt.Missing;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CleanupAttempt.Failed(ex);
        }
    }

    private static FileStream OpenTransactionArtifactHandle(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 2)
                throw new FileNotFoundException("Artifact does not exist.", path);
            if (error == 3)
                throw new DirectoryNotFoundException($"Artifact directory does not exist for '{path}'.");
            throw new System.ComponentModel.Win32Exception(error, $"Unable to open artifact '{path}'.");
        }

        try
        {
            return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static PublishedArtifactIdentity ToPublishedArtifactIdentity(ByHandleFileInformation info) =>
        new(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            info.NumberOfLinks);

    private static ulong GetFileLength(ByHandleFileInformation info) =>
        ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;

    private static bool IsReparsePoint(ByHandleFileInformation info) =>
        (info.FileAttributes & ReparsePointAttribute) != 0;

    private static void ValidateRange(
        long offset,
        long length,
        long sourceLength,
        string name,
        string? sourcePath,
        MediaArtifactKind? kind)
    {
        if (offset < 0 || length <= 0 || offset > sourceLength || length > sourceLength - offset)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Inspector returned an invalid {name} range: offset={offset}, length={length}, sourceLength={sourceLength}.",
                artifactKind: kind,
                sourcePath: sourcePath,
                offset: offset,
                length: length);
        }
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

    private sealed record OwnedArtifactExpectation(string Path, long Length, string Sha256);

    private sealed class CleanupSourceOwnership(string path)
    {
        internal string Path { get; } = path;
        internal PublishedArtifactIdentity? Identity { get; set; }
    }

    private sealed record CleanupSourceCopyTestHooks(
        Action<string, long>? AfterChunk,
        Action<string>? AfterFailure);

    private sealed class TestHookScope(CleanupSourceCopyTestHooks? previous) : IDisposable
    {
        public void Dispose() => s_cleanupSourceCopyTestHooks.Value = previous;
    }

    private readonly record struct PublishedArtifactIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint LinkCount);

    private enum CleanupAttemptKind
    {
        Missing,
        Preserved,
        Deleted,
        Failed
    }

    private readonly record struct CleanupAttempt(CleanupAttemptKind Kind, Exception? Error)
    {
        internal static CleanupAttempt Missing => new(CleanupAttemptKind.Missing, null);
        internal static CleanupAttempt Preserved => new(CleanupAttemptKind.Preserved, null);
        internal static CleanupAttempt Deleted => new(CleanupAttemptKind.Deleted, null);
        internal static CleanupAttempt Failed(Exception error) => new(CleanupAttemptKind.Failed, error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public int DeleteFile;
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReparsePointAttribute = 0x00000400;
    private const int FileDispositionInfoClass = 4;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);
}
