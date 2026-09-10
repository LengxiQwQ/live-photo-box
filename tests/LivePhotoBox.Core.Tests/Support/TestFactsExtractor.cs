using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Core.Tests.Support;

/// <summary>
/// Test-assembly-only adapter for synthetic and fault-injection facts tests.
/// It loads the separately built Native test harness; no raw-facts import or
/// compatibility path exists in the production Core assembly.
/// </summary>
internal static class TestFactsExtractor
{
    internal static Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default) =>
        ExtractAsync(facts, primaryPath, secondaryPath, workspace, null, cancellationToken);

    internal static Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        Action<TestNativeContext>? configureContext,
        CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(facts, primaryPath, secondaryPath, workspace, configureContext, cancellationToken);

    internal static Task ExtractNativeAsync(
        string primaryPath,
        string? secondaryPath,
        SourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        CancellationToken cancellationToken = default) =>
        ExtractNativeCoreAsync(
            primaryPath,
            secondaryPath,
            facts,
            outputImagePath,
            outputVideoPath,
            outputGainmapPath,
            cancellationToken);

    private static async Task<ExtractedMediaBundle> ExtractCoreAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        Action<TestNativeContext>? configureContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(primaryPath);
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        facts = await NormalizeLegacyGainMapAsync(facts, primaryPath, cancellationToken).ConfigureAwait(false);

        if (!File.Exists(primaryPath))
        {
            throw new FileNotFoundException("Primary media file not found.", primaryPath);
        }

        long primaryFileLength = new FileInfo(primaryPath).Length;
        if (!facts.PrimaryImage.IsPresent)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                "Primary image must be present in source facts.",
                MediaArtifactKind.PrimaryImage,
                primaryPath);
        }
        if (facts.PrimaryImage.Container == ImageContainer.Unknown)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.UnsupportedLayout,
                "Primary image container format is unknown or unsupported.",
                MediaArtifactKind.PrimaryImage,
                primaryPath);
        }
        ValidateRange(
            facts.PrimaryImage.ByteOffset,
            facts.PrimaryImage.ByteLength,
            primaryFileLength,
            "primary image",
            primaryPath,
            MediaArtifactKind.PrimaryImage);

        string? videoSource = null;
        if (facts.MotionVideo is { IsPresent: true } videoFacts)
        {
            if (videoFacts.Container == VideoContainer.Unknown)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "Motion video container format is unknown or unsupported.",
                    MediaArtifactKind.MotionVideo);
            }
            if (videoFacts.SourceIndex == 0)
            {
                videoSource = primaryPath;
            }
            else if (videoFacts.SourceIndex == 1)
            {
                if (string.IsNullOrWhiteSpace(secondaryPath) || !File.Exists(secondaryPath))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.InvalidFacts,
                        "Motion video specifies secondary source, but secondary file does not exist.",
                        MediaArtifactKind.MotionVideo,
                        secondaryPath);
                }
                videoSource = secondaryPath;
            }
            else
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.InvalidFacts,
                    $"Motion video specifies invalid SourceIndex: {videoFacts.SourceIndex}.",
                    MediaArtifactKind.MotionVideo);
            }
            ValidateRange(
                videoFacts.ByteOffset,
                videoFacts.ByteLength,
                new FileInfo(videoSource).Length,
                "motion video",
                videoSource,
                MediaArtifactKind.MotionVideo);
        }

        if (facts.GainMap is { IsPresent: true } gainMapFacts)
        {
            if (gainMapFacts.Container == ImageContainer.Unknown)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.UnsupportedLayout,
                    "GainMap container format is unknown or unsupported.",
                    MediaArtifactKind.GainMap,
                    primaryPath);
            }
            ValidateRange(
                gainMapFacts.ByteOffset,
                gainMapFacts.ByteLength,
                primaryFileLength,
                "GainMap",
                primaryPath,
                MediaArtifactKind.GainMap);
        }

        if (facts.ProtocolTailLength > 0)
        {
            ValidateRange(
                facts.ProtocolTailOffset,
                facts.ProtocolTailLength,
                primaryFileLength,
                "protocol trailer",
                primaryPath,
                null);
        }

        ValidateSha(facts.PrimarySha256, "Primary");
        string beforePrimarySha = await workspace
            .ComputeFileSha256Async(primaryPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(facts.PrimarySha256.Trim(), beforePrimarySha, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(
                ExtractionFailureCategory.SourceChanged,
                $"Source media file '{primaryPath}' was modified after inspection: SHA-256 mismatch.",
                sourcePath: primaryPath);
        }

        string? beforeSecondarySha = null;
        if (videoSource == secondaryPath && secondaryPath is not null)
        {
            ValidateSha(facts.SecondarySha256, "Secondary");
            beforeSecondarySha = await workspace
                .ComputeFileSha256Async(secondaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(facts.SecondarySha256!.Trim(), beforeSecondarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Secondary source file '{secondaryPath}' was modified after inspection: SHA-256 mismatch.",
                    sourcePath: secondaryPath);
            }
        }
        else if (!string.IsNullOrWhiteSpace(facts.SecondarySha256) &&
                 secondaryPath is not null && File.Exists(secondaryPath))
        {
            beforeSecondarySha = await workspace
                .ComputeFileSha256Async(secondaryPath, cancellationToken)
                .ConfigureAwait(false);
        }

        string imageExtension = facts.PrimaryImage.Container == ImageContainer.Heic ? ".heic" : ".jpg";
        string outputImagePath = workspace.AllocateFilePath("primary", imageExtension);
        string? outputVideoPath = facts.MotionVideo is { IsPresent: true }
            ? workspace.AllocateFilePath("motion", facts.MotionVideo.Container == VideoContainer.Mov ? ".mov" : ".mp4")
            : null;
        string? outputGainmapPath = facts.GainMap is { IsPresent: true }
            ? workspace.AllocateFilePath("gainmap", facts.GainMap.Container == ImageContainer.Heic ? ".heic" : ".jpg")
            : null;

        bool nativeExtractionSucceeded = false;
        try
        {
            await ExtractNativeAsyncWithConfiguration(
                primaryPath,
                secondaryPath,
                facts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath,
                configureContext,
                cancellationToken).ConfigureAwait(false);
            nativeExtractionSucceeded = true;

            if (!File.Exists(outputImagePath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native test harness did not produce a primary image artifact.");
            if (outputVideoPath is not null && !File.Exists(outputVideoPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native test harness did not produce a motion video artifact.");
            if (outputGainmapPath is not null && !File.Exists(outputGainmapPath))
                throw new ExtractionException(ExtractionFailureCategory.OutputWriteFailed, "Native test harness did not produce a GainMap artifact.");

            string afterPrimarySha = await workspace
                .ComputeFileSha256Async(primaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(beforePrimarySha, afterPrimarySha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceChanged,
                    $"Source file immutability violation: primary source '{primaryPath}' was modified during extraction!",
                    sourcePath: primaryPath);
            }

            if (secondaryPath is not null && beforeSecondarySha is not null)
            {
                string afterSecondarySha = await workspace
                    .ComputeFileSha256Async(secondaryPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(beforeSecondarySha, afterSecondarySha, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExtractionException(
                        ExtractionFailureCategory.SourceChanged,
                        $"Source file immutability violation: secondary source '{secondaryPath}' was modified during extraction!",
                        sourcePath: secondaryPath);
                }
            }

            MediaArtifact primaryArtifact = new()
            {
                Path = outputImagePath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = facts.PrimaryImage.Container,
                ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                ByteLength = new FileInfo(outputImagePath).Length,
                SourceOffset = facts.PrimaryImage.ByteOffset,
                Sha256 = await workspace.ComputeFileSha256Async(outputImagePath, cancellationToken).ConfigureAwait(false)
            };

            MediaArtifact? videoArtifact = null;
            if (outputVideoPath is not null && File.Exists(outputVideoPath))
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
                    Sha256 = await workspace.ComputeFileSha256Async(outputVideoPath, cancellationToken).ConfigureAwait(false)
                };
            }

            MediaArtifact? gainmapArtifact = null;
            if (outputGainmapPath is not null && File.Exists(outputGainmapPath))
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
                    Sha256 = await workspace.ComputeFileSha256Async(outputGainmapPath, cancellationToken).ConfigureAwait(false)
                };
            }

            var extractedFacts = new List<RemovedProtocolFact>();
            if (facts.MotionVideo is { IsPresent: true, SourceIndex: 0 })
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Embedded motion video",
                    Description = "Materialized from the test-harness source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }
            if (facts.GainMap is { IsPresent: true })
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Embedded GainMap",
                    Description = "Materialized from the test-harness source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }
            if (facts.ProtocolTailLength > 0)
            {
                extractedFacts.Add(new RemovedProtocolFact
                {
                    ProtocolName = facts.Protocol.ToString(),
                    Component = "Protocol trailer",
                    Description = "Excluded from the primary artifact using the test-harness source range.",
                    Kind = ProtocolFactKind.Extracted
                });
            }

            return new ExtractedMediaBundle
            {
                PrimaryImage = primaryArtifact,
                MotionVideo = videoArtifact,
                GainMap = gainmapArtifact,
                SourceFacts = facts,
                ExtractedProtocolFacts = extractedFacts
            };
        }
        catch (Exception ex)
        {
            if (nativeExtractionSucceeded)
            {
                string? cleanupFailedFile = null;
                Exception? cleanupError = null;
                foreach (string? path in new[] { outputImagePath, outputVideoPath, outputGainmapPath })
                {
                    if (path is null || !File.Exists(path)) continue;
                    try { File.Delete(path); }
                    catch (Exception deleteException)
                    {
                        cleanupFailedFile ??= path;
                        cleanupError ??= deleteException;
                    }
                }
                if (cleanupFailedFile is not null)
                {
                    ExtractionFailureCategory originalCategory = ex is ExtractionException extraction
                        ? extraction.Category
                        : ExtractionFailureCategory.InternalError;
                    throw new ExtractionException(
                        ExtractionFailureCategory.CleanupFailed,
                        $"Post-extraction cleanup failed: unable to delete artifact '{cleanupFailedFile}'. Original error: {ex.Message}",
                        innerException: cleanupError,
                        originalCategory: originalCategory);
                }
            }
            throw;
        }
    }

    private static Task ExtractNativeAsyncWithConfiguration(
        string primaryPath,
        string? secondaryPath,
        SourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        Action<TestNativeContext>? configureContext,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var context = TestNativeContext.Create(cancellationToken);
            configureContext?.Invoke(context);
            cancellationToken.ThrowIfCancellationRequested();
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(facts);
            NativeResult result = context.ExtractMediaFromFacts(
                primaryPath,
                secondaryPath,
                in nativeFacts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath);
            context.ThrowIfFailed(result);
        }, CancellationToken.None);

    private static async Task ExtractNativeCoreAsync(
        string primaryPath,
        string? secondaryPath,
        SourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        CancellationToken cancellationToken)
    {
        SourceMediaFacts normalized = await NormalizeLegacyGainMapAsync(
            facts,
            primaryPath,
            cancellationToken).ConfigureAwait(false);

        await Task.Run(() =>
        {
            using var context = TestNativeContext.Create(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(normalized);
            NativeResult result = context.ExtractMediaFromFacts(
                primaryPath,
                secondaryPath,
                in nativeFacts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath);
            context.ThrowIfFailed(result);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps older synthetic tests on the test-only raw-facts adapter while
    /// production mappings remain strict.  Once a caller supplies any
    /// auxiliary list, even a malformed one, it is never repaired here.
    /// </summary>
    private static async Task<SourceMediaFacts> NormalizeLegacyGainMapAsync(
        SourceMediaFacts facts,
        string primaryPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.GainMap is not { IsPresent: true } gainMap || facts.AuxiliaryItems.Count != 0)
        {
            return facts;
        }

        if (!File.Exists(primaryPath))
        {
            throw new FileNotFoundException("Primary media file not found.", primaryPath);
        }

        ValidateRange(
            gainMap.ByteOffset,
            gainMap.ByteLength,
            new FileInfo(primaryPath).Length,
            "legacy GainMap",
            primaryPath,
            MediaArtifactKind.GainMap);

        uint itemId = gainMap.ItemId == 0 ? 1u : gainMap.ItemId;
        string relationship = string.IsNullOrWhiteSpace(gainMap.Relationship)
            ? "gain-map"
            : gainMap.Relationship;
        AuxiliaryOwnership ownership = gainMap.Ownership;
        string ownerIdentity = ownership == AuxiliaryOwnership.Primary ? "primary:0" : "auxiliary:0";
        string sourceSha = await ComputeSliceSha256Async(
            primaryPath,
            gainMap.ByteOffset,
            gainMap.ByteLength,
            cancellationToken).ConfigureAwait(false);

        var auxiliary = new AuxiliaryMediaFacts
        {
            IsPresent = true,
            Container = gainMap.Container,
            Representation = gainMap.Representation,
            Ownership = ownership,
            ItemId = itemId,
            ByteOffset = gainMap.ByteOffset,
            ByteLength = gainMap.ByteLength,
            Relationship = relationship,
            StableIdentity = $"test:gainmap:{gainMap.ByteOffset}:{gainMap.ByteLength}",
            Semantic = "GainMap",
            OwnerIdentity = ownerIdentity,
            Sha256 = sourceSha,
            Codec = gainMap.Container == ImageContainer.Heic ? AuxiliaryCodec.Hevc : AuxiliaryCodec.Jpeg,
            SourceIndex = 0
        };

        return facts with
        {
            AuxiliaryItems = [auxiliary],
            GainMap = gainMap with
            {
                AuxiliaryIndex = 0,
                ItemId = itemId,
                Relationship = relationship,
                OwnerArtifactRole = ownership == AuxiliaryOwnership.Primary
                    ? MediaArtifactKind.PrimaryImage
                    : MediaArtifactKind.AuxiliaryItem
            }
        };
    }

    private static async Task<string> ComputeSliceSha256Async(
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        stream.Position = offset;
        using var algorithm = SHA256.Create();
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ExtractionException(
                    ExtractionFailureCategory.SourceRangeUnreadable,
                    $"Unable to read legacy GainMap source range at offset {offset}.",
                    MediaArtifactKind.GainMap,
                    path,
                    offset,
                    length);
            }
            algorithm.TransformBlock(buffer, 0, read, buffer, 0);
            remaining -= read;
        }
        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(algorithm.Hash!);
    }

    private static void ValidateSha(string? value, string role)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"{role} source snapshot SHA-256 is required and cannot be empty.");
        }
        byte[] bytes;
        try { bytes = Convert.FromHexString(value.Trim()); }
        catch (FormatException ex)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"{role} source snapshot SHA-256 is malformed: '{value}'.",
                innerException: ex);
        }
        if (bytes.Length != 32)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"{role} source snapshot SHA-256 must be 32 bytes (64 hex characters), got {bytes.Length} bytes.");
        }
        bool allZero = true;
        foreach (byte item in bytes)
        {
            if (item != 0) { allZero = false; break; }
        }
        if (allZero)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"{role} source snapshot SHA-256 cannot be all zeroes.");
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
        if (offset < 0 || length <= 0 || offset > sourceLength || length > sourceLength - offset)
        {
            throw new ExtractionException(
                ExtractionFailureCategory.InvalidFacts,
                $"Inspector returned an invalid {name} range: offset={offset}, length={length}, sourceLength={sourceLength}.",
                kind,
                sourcePath,
                offset,
                length);
        }
    }
}
