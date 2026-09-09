using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Interop;

/// <summary>
/// Thin control plane service that invokes LivePhotoBox.Native execution plane media operations.
/// </summary>
public static class NativeMediaService
{
    static NativeMediaService() => ValidateNativeFactsLayout();

    private static void ValidateNativeFactsLayout()
    {
        if (Marshal.SizeOf<NativeGainMapItemFacts>() != 112 ||
            (int)Marshal.OffsetOf<NativeGainMapItemFacts>(nameof(NativeGainMapItemFacts.FileRange)) != 32 ||
            (int)Marshal.OffsetOf<NativeGainMapItemFacts>(nameof(NativeGainMapItemFacts.Relationship)) != 48 ||
            Marshal.SizeOf<NativeSourceMediaFacts>() != 1320 ||
            (int)Marshal.OffsetOf<NativeSourceMediaFacts>(nameof(NativeSourceMediaFacts.GainMap)) != 128 ||
            (int)Marshal.OffsetOf<NativeSourceMediaFacts>(nameof(NativeSourceMediaFacts.Timing)) != 240 ||
            (int)Marshal.OffsetOf<NativeSourceMediaFacts>(nameof(NativeSourceMediaFacts.PrimarySha256)) != 416 ||
            (int)Marshal.OffsetOf<NativeSourceMediaFacts>(nameof(NativeSourceMediaFacts.SecondarySha256)) != 448 ||
            (int)Marshal.OffsetOf<NativeSourceMediaFacts>(nameof(NativeSourceMediaFacts.HasSecondarySource)) != 480 ||
            (int)Marshal.OffsetOf<NativeSourceMediaFacts>(nameof(NativeSourceMediaFacts.AuxiliaryCount)) != 484)
        {
            throw new InvalidOperationException("Managed Native facts layout does not match ABI v4.");
        }
    }

    public static Task<SourceMediaFacts> InspectMediaAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(primaryPath);
        PreflightInspectionPath(primaryPath, "Primary");
        if (secondaryPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreflightInspectionPath(secondaryPath, "Secondary");
        }

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            unsafe
            {
                var nativeFacts = new NativeSourceMediaFacts
                {
                    StructSize = checked((uint)sizeof(NativeSourceMediaFacts)),
                    PrimaryImage = new NativeImageItemFacts { StructSize = checked((uint)sizeof(NativeImageItemFacts)) },
                    MotionVideo = new NativeVideoItemFacts { StructSize = checked((uint)sizeof(NativeVideoItemFacts)) },
                    GainMap = new NativeGainMapItemFacts { StructSize = checked((uint)sizeof(NativeGainMapItemFacts)) },
                    Timing = new NativeTimingFacts { StructSize = checked((uint)sizeof(NativeTimingFacts)) }
                };

                Span<NativeConfirmedResidue> residuesBuf = stackalloc NativeConfirmedResidue[64];
                fixed (NativeConfirmedResidue* pResidues = residuesBuf)
                {
                    for (int i = 0; i < residuesBuf.Length; i++)
                    {
                        pResidues[i].StructSize = checked((uint)sizeof(NativeConfirmedResidue));
                    }

                    NativeResult res = NativeMethods.InspectMediaWithResidues(
                        ctx.Handle,
                        primaryPath,
                        secondaryPath,
                        ref nativeFacts,
                        pResidues,
                        (nuint)residuesBuf.Length,
                        out nuint outResiduesCount);
                    ctx.ThrowIfFailed(res);

                    var residuesList = new List<ConfirmedProtocolResidue>();
                    int count = Math.Min((int)outResiduesCount, residuesBuf.Length);
                    for (int i = 0; i < count; i++)
                    {
                        string resId = ReadFixedUtf8String(pResidues[i].ResidueId, 64);
                        string selector = ReadFixedUtf8String(pResidues[i].Selector, 128);
                        string semantic = ReadFixedUtf8String(pResidues[i].ExpectedSemantic, 64);
                        string fingerprint = ReadFixedUtf8String(pResidues[i].ExpectedFingerprint, 64);

                        residuesList.Add(new ConfirmedProtocolResidue
                        {
                            Id = resId,
                            OwnerProtocol = (SourceProtocol)pResidues[i].OwnerProtocol,
                            ArtifactRole = (MediaArtifactKind)pResidues[i].ArtifactRole,
                            StructureKind = (ResidueStructureKind)pResidues[i].StructureKind,
                            Selector = selector,
                            ExpectedSemantic = string.IsNullOrEmpty(semantic) ? null : semantic,
                            ExpectedFingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint,
                            CoordinateSpace = (CoordinateSpace)pResidues[i].CoordinateSpace,
                            RemovalMode = (ResidueRemovalMode)pResidues[i].RemovalMode,
                            RequiredAfterExtraction = pResidues[i].RequiredAfterExtraction != 0
                        });
                    }

                    return MapFromNativeFacts(nativeFacts, residuesList);
                }
            }
        }, cancellationToken);
    }

    private static void PreflightInspectionPath(string path, string role)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw CreateInspectionIoFailure($"{role} media path is empty or whitespace.");
        }

        try
        {
            // Resolve only to validate path syntax. Keep the caller's path for
            // the ABI call so preflight cannot change alias semantics.
            _ = Path.GetFullPath(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.SequentialScan);
            _ = stream.ReadByte();
        }
        catch (ArgumentException ex)
        {
            throw CreateInspectionIoFailure($"{role} media path is invalid.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw CreateInspectionIoFailure($"{role} media path is invalid.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw CreateInspectionIoFailure($"{role} media path is missing or unreadable.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw CreateInspectionIoFailure($"{role} media path is missing or unreadable.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateInspectionIoFailure($"{role} media path is missing or unreadable.", ex);
        }
        catch (IOException ex)
        {
            throw CreateInspectionIoFailure($"{role} media path is missing or unreadable.", ex);
        }
    }

    private static SourceInspectionException CreateInspectionIoFailure(
        string reason,
        Exception? innerException = null) =>
        innerException is null
            ? new SourceInspectionException(
                SourceInspectionFailureCategory.Io,
                SourceInspectionStage.Read,
                NativeRuntime.FoundationCapability,
                reason)
            : new SourceInspectionException(
                SourceInspectionFailureCategory.Io,
                SourceInspectionStage.Read,
                NativeRuntime.FoundationCapability,
                reason,
                innerException);

    public static Task ExtractMediaAsync(
        string primaryPath,
        string? secondaryPath,
        SourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        CancellationToken cancellationToken = default) =>
        ExtractMediaAsync(primaryPath, secondaryPath, facts, outputImagePath, outputVideoPath, outputGainmapPath, null, cancellationToken);

    internal static Task ExtractMediaAsync(
        string primaryPath,
        string? secondaryPath,
        SourceMediaFacts facts,
        string? outputImagePath,
        string? outputVideoPath,
        string? outputGainmapPath,
        Action<NativeContext>? configureContext,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            configureContext?.Invoke(ctx);
            NativeSourceMediaFacts nativeFacts = MapToNativeFacts(facts);

            NativeResult res = NativeMethods.ExtractMedia(
                ctx.Handle,
                primaryPath,
                secondaryPath,
                in nativeFacts,
                outputImagePath,
                outputVideoPath,
                outputGainmapPath);

            ctx.ThrowIfFailed(res);
        }, cancellationToken);
    }

    public static Task<VideoFacts> ProbeVideoAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found for probe.", videoPath);

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            unsafe
            {
                var nativeFacts = new NativeVideoItemFacts
                {
                    StructSize = checked((uint)sizeof(NativeVideoItemFacts))
                };

                NativeResult res = NativeMethods.ProbeVideo(ctx.Handle, videoPath, ref nativeFacts);
                ctx.ThrowIfFailed(res);

                return MapFromNativeVideoFacts(nativeFacts);
            }
        }, cancellationToken);
    }

    public static Task RemuxVideoAsync(
        string inputVideoPath,
        string outputVideoPath,
        VideoContainer targetContainer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            NativeResult res = NativeMethods.RemuxVideo(
                ctx.Handle,
                inputVideoPath,
                outputVideoPath,
                (int)targetContainer);

            ctx.ThrowIfFailed(res);
        }, cancellationToken);
    }

    public static Task<bool> ConvertImageAsync(
        string inputImagePath,
        string outputImagePath,
        ImageContainer targetContainer,
        int quality,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            NativeResult res = NativeMethods.ConvertImage(
                ctx.Handle,
                inputImagePath,
                outputImagePath,
                (int)targetContainer,
                quality,
                out int outReencoded);

            ctx.ThrowIfFailed(res);

            return outReencoded != 0;
        }, cancellationToken);
    }

    public static Task<string> TranscodeVideoAsync(
        string inputVideoPath,
        string outputVideoPath,
        VideoContainer targetContainer,
        VideoCodec targetCodec,
        int crf,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            Span<byte> encoderBuf = stackalloc byte[128];
            NativeResult res;
            unsafe
            {
                fixed (byte* pBuf = encoderBuf)
                {
                    res = NativeMethods.TranscodeVideo(
                        ctx.Handle,
                        inputVideoPath,
                        outputVideoPath,
                        (int)targetContainer,
                        (int)targetCodec,
                        crf,
                        pBuf,
                        (nuint)encoderBuf.Length);
                }
            }

            ctx.ThrowIfFailed(res);

            int nullIdx = encoderBuf.IndexOf((byte)0);
            if (nullIdx < 0) nullIdx = encoderBuf.Length;
            return Encoding.UTF8.GetString(encoderBuf[..nullIdx]);
        }, cancellationToken);
    }

    internal static unsafe SourceMediaFacts MapFromNativeFacts(
        in NativeSourceMediaFacts native,
        IReadOnlyList<ConfirmedProtocolResidue>? confirmedResidues = null)
    {
        string? pairingId = null;
        fixed (byte* p = native.PairingIdentifier)
        {
            int len = 0;
            while (len < 128 && p[len] != 0) len++;
            if (len > 0)
            {
                pairingId = Encoding.UTF8.GetString(p, len);
            }
        }

        byte[] primarySha = new byte[32];
        fixed (byte* pSha = native.PrimarySha256)
        {
            fixed (byte* pDst = primarySha)
            {
                Buffer.MemoryCopy(pSha, pDst, 32, 32);
            }
        }
        string primaryShaHex = Convert.ToHexString(primarySha);

        string? secondaryShaHex = null;
        if (native.HasSecondarySource != 0)
        {
            byte[] secondarySha = new byte[32];
            fixed (byte* pSha = native.SecondarySha256)
            {
                fixed (byte* pDst = secondarySha)
                {
                    Buffer.MemoryCopy(pSha, pDst, 32, 32);
                }
            }
            secondaryShaHex = Convert.ToHexString(secondarySha);
        }

        if (native.AuxiliaryCount > NativeRuntime.MaxAuxiliaryItems)
        {
            throw new SourceInspectionException(
                SourceInspectionFailureCategory.Unsupported,
                SourceInspectionStage.Container,
                NativeRuntime.FoundationCapability,
                $"Native source facts contain {native.AuxiliaryCount} auxiliary items, exceeding the ABI capacity of {NativeRuntime.MaxAuxiliaryItems}.");
        }

        var auxiliaryItems = new List<AuxiliaryMediaFacts>();
        int auxiliaryCount = checked((int)native.AuxiliaryCount);
        if (auxiliaryCount > 0) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary0));
        if (auxiliaryCount > 1) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary1));
        if (auxiliaryCount > 2) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary2));
        if (auxiliaryCount > 3) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary3));
        if (auxiliaryCount > 4) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary4));
        if (auxiliaryCount > 5) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary5));
        if (auxiliaryCount > 6) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary6));
        if (auxiliaryCount > 7) auxiliaryItems.Add(MapAuxiliary(native.Auxiliary7));

        return new SourceMediaFacts
        {
            Protocol = (SourceProtocol)native.Protocol,
            PrimarySha256 = primaryShaHex,
            SecondarySha256 = secondaryShaHex,
            PrimaryImage = new ImageFacts
            {
                IsPresent = native.PrimaryImage.IsPresent != 0,
                Container = (ImageContainer)native.PrimaryImage.Container,
                Width = native.PrimaryImage.Width,
                Height = native.PrimaryImage.Height,
                ByteOffset = (long)native.PrimaryImage.FileRange.Offset,
                ByteLength = (long)native.PrimaryImage.FileRange.Length
            },
            MotionVideo = native.MotionVideo.IsPresent != 0 ? MapFromNativeVideoFacts(native.MotionVideo) : null,
            GainMap = native.GainMap.IsPresent != 0
                ? MapGainMap(native.GainMap, auxiliaryItems)
                : null,
            AuxiliaryItems = auxiliaryItems,
            Timing = new TimingFacts
            {
                CoverTimestampUs = native.Timing.CoverTimestampUs,
                PrimaryTimestampUs = native.Timing.PrimaryTimestampUs,
                CoverFrameIndex = native.Timing.CoverFrameIndex,
                TotalFrames = native.Timing.TotalFrames
            },
            ProtocolTailOffset = checked((long)native.ProtocolTailRange.Offset),
            ProtocolTailLength = checked((long)native.ProtocolTailRange.Length),
            PairingIdentifier = pairingId,
            ConfirmedResidues = confirmedResidues ?? Array.Empty<ConfirmedProtocolResidue>()
        };
    }

    private static unsafe AuxiliaryMediaFacts MapAuxiliary(NativeAuxiliaryItemFacts native)
    {
        if (native.StructSize < (uint)sizeof(NativeAuxiliaryItemFacts))
        {
            throw new SourceInspectionException(
                SourceInspectionFailureCategory.Ambiguous,
                SourceInspectionStage.Container,
                NativeRuntime.FoundationCapability,
                "Native auxiliary item facts have an incompatible struct_size.");
        }
        string relationship = string.Empty;
        int nativeSize = Marshal.SizeOf<NativeAuxiliaryItemFacts>();
        nint nativePtr = Marshal.AllocHGlobal(nativeSize);
        try
        {
            Marshal.StructureToPtr(native, nativePtr, false);
            int offset = (int)Marshal.OffsetOf<NativeAuxiliaryItemFacts>(nameof(NativeAuxiliaryItemFacts.Relationship));
            byte[] bytes = new byte[64];
            Marshal.Copy(nativePtr + offset, bytes, 0, bytes.Length);
            int length = Array.IndexOf(bytes, (byte)0);
            relationship = Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
        }
        finally
        {
            Marshal.FreeHGlobal(nativePtr);
        }
        return new AuxiliaryMediaFacts
        {
            IsPresent = native.IsPresent != 0,
            Container = (ImageContainer)native.Container,
            Representation = (AuxiliaryRepresentation)native.Representation,
            Ownership = (AuxiliaryOwnership)native.Ownership,
            ItemId = native.ItemId,
            ByteOffset = checked((long)native.FileRange.Offset),
            ByteLength = checked((long)native.FileRange.Length),
            Relationship = relationship
        };
    }

    private static unsafe GainMapFacts MapGainMap(
        NativeGainMapItemFacts native,
        IReadOnlyList<AuxiliaryMediaFacts> auxiliaryItems)
    {
        if (native.StructSize < (uint)sizeof(NativeGainMapItemFacts) ||
            native.AuxiliaryIndex >= (uint)auxiliaryItems.Count)
        {
            throw new SourceInspectionException(
                SourceInspectionFailureCategory.Ambiguous,
                SourceInspectionStage.Container,
                NativeRuntime.FoundationCapability,
                "Native GainMap facts do not identify a unique auxiliary entry.");
        }

        var auxiliary = auxiliaryItems[(int)native.AuxiliaryIndex];
        string relationship;
        byte* pRelationship = native.Relationship;
        relationship = ReadFixedUtf8String(pRelationship, 64);

        if (!auxiliary.IsPresent ||
            auxiliary.Container != (ImageContainer)native.Container ||
            auxiliary.Representation != (AuxiliaryRepresentation)native.Representation ||
            auxiliary.Ownership != (AuxiliaryOwnership)native.Ownership ||
            native.OwnerArtifactRole != (native.Ownership == (int)AuxiliaryOwnership.Primary
                ? (int)MediaArtifactKind.PrimaryImage
                : (int)MediaArtifactKind.AuxiliaryItem) ||
            auxiliary.ItemId != native.ItemId ||
            auxiliary.ByteOffset != checked((long)native.FileRange.Offset) ||
            auxiliary.ByteLength != checked((long)native.FileRange.Length) ||
            !string.Equals(auxiliary.Relationship, relationship, StringComparison.Ordinal))
        {
            throw new SourceInspectionException(
                SourceInspectionFailureCategory.Ambiguous,
                SourceInspectionStage.Container,
                NativeRuntime.FoundationCapability,
                "Native GainMap facts are not bound to the referenced auxiliary entry.");
        }

        return new GainMapFacts
        {
            IsPresent = true,
            Container = (ImageContainer)native.Container,
            Representation = (AuxiliaryRepresentation)native.Representation,
            Ownership = (AuxiliaryOwnership)native.Ownership,
            OwnerArtifactRole = (MediaArtifactKind)native.OwnerArtifactRole,
            AuxiliaryIndex = native.AuxiliaryIndex,
            ItemId = native.ItemId,
            ByteOffset = checked((long)native.FileRange.Offset),
            ByteLength = checked((long)native.FileRange.Length),
            Relationship = relationship
        };
    }

    internal static unsafe string ReadFixedUtf8String(byte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0) len++;
        return len > 0 ? Encoding.UTF8.GetString(ptr, len) : string.Empty;
    }

    private static VideoFacts MapFromNativeVideoFacts(in NativeVideoItemFacts native)
    {
        return new VideoFacts
        {
            IsPresent = native.IsPresent != 0,
            Container = (VideoContainer)native.Container,
            Codec = (VideoCodec)native.Codec,
            Width = native.Width,
            Height = native.Height,
            RotationDegrees = native.RotationDegrees,
            DurationSeconds = native.DurationSeconds,
            Fps = native.Fps,
            HasAudio = native.HasAudio != 0,
            ByteOffset = (long)native.FileRange.Offset,
            ByteLength = (long)native.FileRange.Length,
            SourceIndex = native.SourceIndex
        };
    }

    internal static unsafe NativeSourceMediaFacts MapToNativeFacts(SourceMediaFacts facts)
    {
        var native = new NativeSourceMediaFacts
        {
            StructSize = checked((uint)sizeof(NativeSourceMediaFacts)),
            Protocol = (int)facts.Protocol,
            PrimaryImage = new NativeImageItemFacts
            {
                StructSize = checked((uint)sizeof(NativeImageItemFacts)),
                IsPresent = facts.PrimaryImage.IsPresent ? 1 : 0,
                Container = (int)facts.PrimaryImage.Container,
                Width = facts.PrimaryImage.Width,
                Height = facts.PrimaryImage.Height,
                FileRange = new NativeMediaRange
                {
                    Offset = (ulong)facts.PrimaryImage.ByteOffset,
                    Length = (ulong)facts.PrimaryImage.ByteLength
                }
            }
        };

        if (string.IsNullOrWhiteSpace(facts.PrimarySha256))
        {
            throw new LivePhotoBox.Media.Extraction.ExtractionException(
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                "Primary source snapshot SHA-256 is required and cannot be empty.");
        }

        byte[] primSha;
        try
        {
            primSha = Convert.FromHexString(facts.PrimarySha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new LivePhotoBox.Media.Extraction.ExtractionException(
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                $"Primary source snapshot SHA-256 is malformed: '{facts.PrimarySha256}'.",
                innerException: ex);
        }

        if (primSha.Length != 32)
        {
            throw new LivePhotoBox.Media.Extraction.ExtractionException(
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                $"Primary source snapshot SHA-256 must be 32 bytes (64 hex characters), got {primSha.Length} bytes.");
        }

        bool primIsAllZero = true;
        for (int i = 0; i < 32; i++)
        {
            if (primSha[i] != 0) { primIsAllZero = false; break; }
        }
        if (primIsAllZero)
        {
            throw new LivePhotoBox.Media.Extraction.ExtractionException(
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                "Primary source snapshot SHA-256 cannot be all zeroes.");
        }

        for (int i = 0; i < 32; i++)
        {
            native.PrimarySha256[i] = primSha[i];
        }

        bool requiresSecondary = facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };
        if (requiresSecondary && string.IsNullOrWhiteSpace(facts.SecondarySha256))
        {
            throw new LivePhotoBox.Media.Extraction.ExtractionException(
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                "Secondary source snapshot SHA-256 is required when motion video resides in secondary source.");
        }

        if (!string.IsNullOrWhiteSpace(facts.SecondarySha256))
        {
            byte[] secSha;
            try
            {
                secSha = Convert.FromHexString(facts.SecondarySha256.Trim());
            }
            catch (FormatException ex)
            {
                throw new LivePhotoBox.Media.Extraction.ExtractionException(
                    LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                    $"Secondary source snapshot SHA-256 is malformed: '{facts.SecondarySha256}'.",
                    innerException: ex);
            }

            if (secSha.Length != 32)
            {
                throw new LivePhotoBox.Media.Extraction.ExtractionException(
                    LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                    $"Secondary source snapshot SHA-256 must be 32 bytes (64 hex characters), got {secSha.Length} bytes.");
            }

            bool secIsAllZero = true;
            for (int i = 0; i < 32; i++)
            {
                if (secSha[i] != 0) { secIsAllZero = false; break; }
            }
            if (secIsAllZero)
            {
                throw new LivePhotoBox.Media.Extraction.ExtractionException(
                    LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                    "Secondary source snapshot SHA-256 cannot be all zeroes.");
            }

            native.HasSecondarySource = 1;
            for (int i = 0; i < 32; i++)
            {
                native.SecondarySha256[i] = secSha[i];
            }
        }

        if (!string.IsNullOrEmpty(facts.PairingIdentifier))
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(facts.PairingIdentifier);
            int copyLen = Math.Min(idBytes.Length, 127);
            for (int i = 0; i < copyLen; i++)
            {
                native.PairingIdentifier[i] = idBytes[i];
            }
            native.PairingIdentifier[copyLen] = 0;
        }

        native.MotionVideo.StructSize = checked((uint)sizeof(NativeVideoItemFacts));
        if (facts.MotionVideo != null)
        {
            native.MotionVideo = new NativeVideoItemFacts
            {
                StructSize = checked((uint)sizeof(NativeVideoItemFacts)),
                IsPresent = facts.MotionVideo.IsPresent ? 1 : 0,
                Container = (int)facts.MotionVideo.Container,
                Codec = (int)facts.MotionVideo.Codec,
                Width = facts.MotionVideo.Width,
                Height = facts.MotionVideo.Height,
                RotationDegrees = facts.MotionVideo.RotationDegrees,
                DurationSeconds = facts.MotionVideo.DurationSeconds,
                Fps = facts.MotionVideo.Fps,
                HasAudio = facts.MotionVideo.HasAudio ? 1 : 0,
                FileRange = new NativeMediaRange
                {
                    Offset = (ulong)facts.MotionVideo.ByteOffset,
                    Length = (ulong)facts.MotionVideo.ByteLength
                },
                SourceIndex = facts.MotionVideo.SourceIndex
            };
        }

        native.GainMap.StructSize = checked((uint)sizeof(NativeGainMapItemFacts));
        if (facts.AuxiliaryItems.Count > NativeRuntime.MaxAuxiliaryItems)
        {
            throw new LivePhotoBox.Media.Extraction.ExtractionException(
                LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                $"Auxiliary item count exceeds the native ABI capacity of {NativeRuntime.MaxAuxiliaryItems}.");
        }

        native.AuxiliaryCount = (uint)facts.AuxiliaryItems.Count;
        for (int i = 0; i < facts.AuxiliaryItems.Count; i++)
        {
            NativeAuxiliaryItemFacts auxiliary = MapToNativeAuxiliary(facts.AuxiliaryItems[i]);
            switch (i)
            {
                case 0: native.Auxiliary0 = auxiliary; break;
                case 1: native.Auxiliary1 = auxiliary; break;
                case 2: native.Auxiliary2 = auxiliary; break;
                case 3: native.Auxiliary3 = auxiliary; break;
                case 4: native.Auxiliary4 = auxiliary; break;
                case 5: native.Auxiliary5 = auxiliary; break;
                case 6: native.Auxiliary6 = auxiliary; break;
                case 7: native.Auxiliary7 = auxiliary; break;
            }
        }

        if (facts.GainMap != null)
        {
            if (!facts.GainMap.IsPresent ||
                facts.GainMap.AuxiliaryIndex >= (uint)facts.AuxiliaryItems.Count)
            {
                throw new LivePhotoBox.Media.Extraction.ExtractionException(
                    LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                    "GainMap facts must reference one unique auxiliary item.");
            }

            var auxiliary = facts.AuxiliaryItems[(int)facts.GainMap.AuxiliaryIndex];
            if (!IsGainMapBindingConsistent(facts.GainMap, auxiliary))
            {
                throw new LivePhotoBox.Media.Extraction.ExtractionException(
                    LivePhotoBox.Media.Extraction.ExtractionFailureCategory.InvalidFacts,
                    "GainMap facts do not match their referenced auxiliary item.");
            }

            native.GainMap = new NativeGainMapItemFacts
            {
                StructSize = checked((uint)sizeof(NativeGainMapItemFacts)),
                IsPresent = 1,
                Container = (int)facts.GainMap.Container,
                Representation = (int)facts.GainMap.Representation,
                Ownership = (int)facts.GainMap.Ownership,
                OwnerArtifactRole = (int)facts.GainMap.OwnerArtifactRole,
                AuxiliaryIndex = facts.GainMap.AuxiliaryIndex,
                ItemId = facts.GainMap.ItemId,
                FileRange = new NativeMediaRange
                {
                    Offset = (ulong)facts.GainMap.ByteOffset,
                    Length = (ulong)facts.GainMap.ByteLength
                }
            };
            byte* pRelationship = native.GainMap.Relationship;
            byte[] relationship = Encoding.UTF8.GetBytes(facts.GainMap.Relationship ?? string.Empty);
            int copyLength = Math.Min(relationship.Length, 63);
            for (int i = 0; i < copyLength; i++) pRelationship[i] = relationship[i];
            pRelationship[copyLength] = 0;
        }

        native.Timing = new NativeTimingFacts
        {
            StructSize = checked((uint)sizeof(NativeTimingFacts)),
            CoverTimestampUs = facts.Timing.CoverTimestampUs,
            PrimaryTimestampUs = facts.Timing.PrimaryTimestampUs,
            CoverFrameIndex = facts.Timing.CoverFrameIndex,
            TotalFrames = facts.Timing.TotalFrames
        };

        native.ProtocolTailRange = new NativeMediaRange
        {
            Offset = facts.ProtocolTailOffset < 0 ? 0UL : (ulong)facts.ProtocolTailOffset,
            Length = facts.ProtocolTailLength < 0 ? 0UL : (ulong)facts.ProtocolTailLength
        };

        return native;
    }

    private static unsafe NativeAuxiliaryItemFacts MapToNativeAuxiliary(AuxiliaryMediaFacts facts)
    {
        var native = new NativeAuxiliaryItemFacts
        {
            StructSize = checked((uint)sizeof(NativeAuxiliaryItemFacts)),
            IsPresent = facts.IsPresent ? 1 : 0,
            Container = (int)facts.Container,
            Representation = (int)facts.Representation,
            Ownership = (int)facts.Ownership,
            ItemId = facts.ItemId,
            FileRange = new NativeMediaRange
            {
                Offset = (ulong)facts.ByteOffset,
                Length = (ulong)facts.ByteLength
            }
        };
        byte* pRelationship = native.Relationship;
        byte[] relationship = Encoding.UTF8.GetBytes(facts.Relationship ?? string.Empty);
        int copyLength = Math.Min(relationship.Length, 63);
        for (int i = 0; i < copyLength; i++) pRelationship[i] = relationship[i];
        pRelationship[copyLength] = 0;
        return native;
    }

    private static bool IsGainMapBindingConsistent(GainMapFacts gainMap, AuxiliaryMediaFacts auxiliary)
        => auxiliary.IsPresent &&
           gainMap.OwnerArtifactRole == (gainMap.Ownership == AuxiliaryOwnership.Primary
               ? MediaArtifactKind.PrimaryImage
               : MediaArtifactKind.AuxiliaryItem) &&
           auxiliary.Container == gainMap.Container &&
           auxiliary.Representation == gainMap.Representation &&
           auxiliary.Ownership == gainMap.Ownership &&
           auxiliary.ItemId == gainMap.ItemId &&
           auxiliary.ByteOffset == gainMap.ByteOffset &&
           auxiliary.ByteLength == gainMap.ByteLength &&
           string.Equals(auxiliary.Relationship, gainMap.Relationship, StringComparison.Ordinal);

    public static Task ReassembleJpegGainMapAsync(
        string primaryJpegPath,
        string gainmapJpegPath,
        string outputPath,
        string expectedGainMapSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            NativeResult res = NativeMethods.lpb_reassemble_jpeg_gainmap(
                ctx.Handle,
                primaryJpegPath,
                gainmapJpegPath,
                outputPath,
                expectedGainMapSha256);
            ctx.ThrowIfFailed(res);
        }, cancellationToken);
    }

    public static Task<PreservationObservation> CapturePreservationObservationAsync(
        string mediaPath,
        SourceProtocol protocol,
        ImageContainer container,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException("Preservation target media file not found.", mediaPath);

        return Task.Run(() =>
        {
            using var ctx = NativeContext.Create(cancellationToken);
            var nativeObs = new NativePreservationObservation
            {
                StructSize = checked((uint)Marshal.SizeOf<NativePreservationObservation>())
            };

            NativeResult res = NativeMethods.lpb_capture_preservation_observation(
                ctx.Handle,
                mediaPath,
                (int)protocol,
                (int)container,
                ref nativeObs);

            ctx.ThrowIfFailed(res);
            return PreservationObservation.FromNative(in nativeObs);
        }, cancellationToken);
    }

    internal static IReadOnlyList<NativePreservationVerdict> VerifyPreservation(
        PreservationObservation preImage,
        PreservationObservation postImage,
        PreservationObservation? preVideo,
        PreservationObservation? postVideo,
        SourceProtocol protocol,
        DetachedGainMapVerificationState detachedGainMapState,
        out bool allPassed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preImage);
        ArgumentNullException.ThrowIfNull(postImage);
        cancellationToken.ThrowIfCancellationRequested();

        using var ctx = NativeContext.Create(cancellationToken);
        var preNative = new NativePreservationObservation();
        preImage.ToNative(ref preNative);

        var postNative = new NativePreservationObservation();
        postImage.ToNative(ref postNative);

        if (preVideo != null)
        {
            preNative.Flags |= 0x00000080u; // LPB_POBS_HAS_VIDEO_MDAT
            preNative.VideoMdatSha256 = preVideo.VideoMdatSha256 ?? "";
        }

        if (postVideo != null)
        {
            postNative.Flags |= 0x00000080u; // LPB_POBS_HAS_VIDEO_MDAT
            postNative.VideoMdatSha256 = postVideo.VideoMdatSha256 ?? "";
        }

        var verdicts = new NativePreservationVerdict[16];
        NativeResult res = NativeMethods.lpb_verify_preservation(
            ctx.Handle,
            ref preNative,
            ref postNative,
            (int)protocol,
            (uint)detachedGainMapState,
            verdicts,
            (nuint)verdicts.Length,
            out nuint outCount,
            out byte outOverallPassed);

        ctx.ThrowIfFailed(res);
        allPassed = outOverallPassed != 0;

        int count = Math.Min((int)outCount, verdicts.Length);
        var result = new NativePreservationVerdict[count];
        Array.Copy(verdicts, result, count);
        return result;
    }
}
