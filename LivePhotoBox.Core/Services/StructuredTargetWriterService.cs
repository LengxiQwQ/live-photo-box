using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;
using NativeFileIdentity = LivePhotoBox.Interop.NativeCleanService.NativeFileIdentity;

namespace LivePhotoBox.Services;

// Explicit internal target selection; never inferred from Neutral provenance,
// Make, or an ordinary stabilization UUID. Product choices still await R8.
internal enum HuaweiTargetVariant : uint { HuaweiV6 = 1, HonorV2 = 2 }
internal enum SamsungTargetVariant : uint { Mpv2 = 2, Mpv3 = 3 }

/// <summary>
/// Experimental structured Native target path. Product adapters migrate only after
/// their complete target lifecycle; this service has no fallback or selectable validator.
/// </summary>
internal static class StructuredTargetWriterService
{
    internal static async Task<PublishedOwnedFile> WriteAsync(NeutralMediaBundle bundle,
        OutputProfile profile, string finalPath, string stagingDirectory,
        bool overwriteExisting, CancellationToken token,
        HuaweiTargetVariant huaweiVariant = HuaweiTargetVariant.HuaweiV6,
        SamsungTargetVariant samsungVariant = SamsungTargetVariant.Mpv3)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(profile);
        uint target = profile.TargetProtocol switch
        {
            TargetProtocol.GoogleMicroVideoV1 => 1,
            TargetProtocol.GoogleMotionPhotoV2 => 2,
            TargetProtocol.OppoLivePhoto => 3,
            TargetProtocol.SamsungMotionPhoto => 5,
            TargetProtocol.HuaweiMovingPhoto => 6,
            _ => throw new NotSupportedException("This structured path does not implement the requested target family.")
        };
        MediaArtifact video = bundle.MotionVideo ?? throw new InvalidDataException("A prepared motion video is required.");
        if (!Enum.IsDefined(huaweiVariant) || (target != 6 && huaweiVariant != HuaweiTargetVariant.HuaweiV6))
            throw new ArgumentException("The explicit Huawei/Honor variant does not apply to this target.", nameof(huaweiVariant));
        if (!Enum.IsDefined(samsungVariant) || (target != 5 && samsungVariant != SamsungTargetVariant.Mpv3))
            throw new ArgumentException("Samsung variant does not apply to the selected target.", nameof(samsungVariant));
        uint targetVersion = target == 6 ? (uint)huaweiVariant : target == 5 ? (uint)samsungVariant : 1u;
        if (target == 6 && profile.Format == OutputMediaFormat.HeicMp4Hevc && video.VideoCodec != VideoCodec.Hevc)
            throw new NotSupportedException("The explicit Huawei HEVC profile requires prepared HEVC video.");
        if (bundle.PrimaryImage.Kind != MediaArtifactKind.PrimaryImage || video.Kind != MediaArtifactKind.MotionVideo ||
            bundle.PrimaryImage.ImageContainer != profile.MediaRequirement.ImageContainer ||
            video.VideoContainer != profile.MediaRequirement.VideoContainer ||
            video.VideoCodec is not (VideoCodec.H264 or VideoCodec.Hevc))
            throw new InvalidDataException("Prepared artifact roles and formats do not match the structured target profile.");
        RequireManifest(bundle, bundle.PrimaryImage);
        RequireManifest(bundle, video);
        RejectSourceAlias(finalPath, bundle.PrimaryImage);
        RejectSourceAlias(finalPath, video);
        long? cover = bundle.Semantics.Timing.CoverTimestampUs;
        long? primaryTime = target == 3 ? bundle.Semantics.Timing.PrimaryTimestampUs : null;
        if (target == 3 && !primaryTime.HasValue)
            throw new NotSupportedException("OPPO original capture time is unknown; current cover cannot invent that history.");
        AuxiliaryBinding[] auxiliaries = await BindAuxiliariesAsync(bundle, finalPath, token).ConfigureAwait(false);
        NativeCompoundPrimaryObservation? compound = bundle.PrimaryImage.CompoundPrimary?.Inspect(bundle.PrimaryImage.Path, token).Observation;
        if (compound.HasValue && (target != 6 || profile.Format != OutputMediaFormat.JpegMp4))
            throw new NotSupportedException("Opaque compound primary lineage permits Huawei/Honor whole-JPEG passthrough only.");
        var expected = new TargetValidationExpectations(target, (uint)profile.Format, (uint)video.VideoCodec, cover)
        {
            UseV2 = true, TargetVersion = targetVersion, Compound = compound, PrimaryTimestampUs = primaryTime,
            Auxiliaries = auxiliaries.Select(row => new TargetAuxiliaryExpectation(
                AuxiliarySemanticCode(row.Row.Semantic), (uint)row.Row.ImageContainer,
                checked((ulong)row.Row.ByteLength), row.Row.Sha256)).ToArray(),
            ImageRotationDegrees = KnownRotation(bundle.Semantics.Orientation.PrimaryImage),
            ImageReflection = KnownReflection(bundle.Semantics.Orientation.PrimaryImage),
            VideoRotationDegrees = KnownRotation(bundle.Semantics.Orientation.MotionVideo),
            VideoReflection = KnownReflection(bundle.Semantics.Orientation.MotionVideo)
        };
        using var session = new Session(bundle.PrimaryImage, video, profile, target, targetVersion, cover, primaryTime,
            auxiliaries, stagingDirectory, token);
        return await WindowsSplitOutputPublisher.PublishTargetAsync(finalPath, overwriteExisting,
            session.CopyToAsync, expected, token).ConfigureAwait(false);
    }

    private sealed record AuxiliaryBinding(NeutralArtifactManifest Row, uint Representation,
        uint SelectorKind, uint SelectorId, MediaArtifact? Materialized);

    private static uint AuxiliarySemanticCode(string semantic) => semantic switch
    {
        "GainMap" => 1,
        "Original" => 2,
        "MiCameraSubimage" => 3,
        "MiCameraLensWatermark" => 4,
        "MpfAuxiliaryImage" => 5,
        _ => throw new NotSupportedException("The target cannot represent this auxiliary semantic asset.")
    };

    private static async Task<AuxiliaryBinding[]> BindAuxiliariesAsync(NeutralMediaBundle bundle,
        string finalPath, CancellationToken token)
    {
        var semantics = bundle.Semantics.AuxiliaryMedia.ToList();
        if (bundle.Semantics.GainMap is { } gainMap) semantics.Add(gainMap);
        if (semantics.Count > 2 || semantics.GroupBy(row => row.Semantic).Any(group => group.Count() != 1))
            throw new NotSupportedException("The R3 target requires at most two unique supported auxiliary semantic assets.");
        if (bundle.AuxiliaryMedia.GroupBy(row => row.Semantic).Any(group => group.Count() != 1) ||
            bundle.AuxiliaryMedia.Any(descriptor => !semantics.Any(semantic =>
                descriptor.Semantic == semantic.Semantic && descriptor.OwnerIdentity == semantic.OwnerIdentity &&
                descriptor.Relationship == semantic.Relationship)))
            throw new InvalidDataException("Auxiliary descriptors do not map uniquely to the final semantic inventory.");
        var rows = bundle.Manifest.Where(row => row.Role is not ("PrimaryImage" or "MotionVideo")).ToArray();
        if (rows.Length != semantics.Count ||
            (bundle.GainMap is not null && !semantics.Any(row => row.Semantic == "GainMap")) ||
            (bundle.GainMapRepresentation != GainMapRepresentation.None && !semantics.Any(row => row.Semantic == "GainMap")))
            throw new NotSupportedException("Unrepresented auxiliary/carrier bytes cannot be silently discarded.");
        if (semantics.Count == 0)
        {
            if (bundle.AuxiliaryMedia.Count != 0)
                throw new NotSupportedException("Unbound auxiliary/carrier descriptors cannot authorize target writing.");
        }
        // Only prepared Neutral bytes are re-observed to bind structural selectors.
        // Source provenance never selects a target layout.
        SourceMediaFacts? facts = null;
        if (bundle.PrimaryImage.ImageContainer == ImageContainer.Heic &&
            semantics.Any(row => row.Representation is NeutralAuxiliaryRepresentation.Embedded or NeutralAuxiliaryRepresentation.Both))
        {
            facts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path,
                cancellationToken: token).ConfigureAwait(false);
            if (facts.Protocol != SourceProtocol.NonLive)
                throw new InvalidDataException("Prepared primary still contains source live binding.");
        }
        if (bundle.PreservationCarriers.Count != 0)
        {
            facts ??= bundle.PrimaryImage.CompoundPrimary is { } compoundPrimary
                ? compoundPrimary.Inspect(bundle.PrimaryImage.Path, token).Facts
                : await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path, cancellationToken: token).ConfigureAwait(false);
            if (facts.Protocol != SourceProtocol.NonLive)
                throw new InvalidDataException("Prepared carrier owner retains a source live binding.");
            foreach (var carrier in bundle.PreservationCarriers)
            {
                // Descriptive evidence never authorizes an append. Only an
                // actually observed embedded ordinary camera region is copied
                // as part of the independently leased whole primary artifact.
                bool ordinary = carrier.Kind == PreservationCarrierKind.ContainerMetadata && carrier.Semantic == "CameraMetadata" && carrier.Relationship == "opaque-camera-metadata";
                bool opaque = bundle.PrimaryImage.CompoundPrimary is not null && carrier.Kind == PreservationCarrierKind.OpaqueFragment &&
                    carrier.Semantic == "OpaqueSourcePrefix" && carrier.Relationship == "preserved-source-prefix";
                bool sef = carrier.Kind == PreservationCarrierKind.SamsungSef &&
                    carrier.StableIdentity.StartsWith("samsung:sef:", StringComparison.Ordinal) &&
                    carrier.Relationship.StartsWith("SEFH/SEFT", StringComparison.Ordinal);
                bool index = sef && carrier.StableIdentity == "samsung:sef:index-trailer" && carrier.Semantic == "SamsungSEFIndexTrailer";
                if (carrier.ArtifactRole != MediaArtifactKind.PrimaryImage || (!ordinary && !opaque && !sef) || carrier.OwnerIdentity != "primary:0" ||
                    facts.PreservationCarriers.Count(actual => actual.StableIdentity == carrier.StableIdentity &&
                        actual.Kind == carrier.Kind && (sef || actual.SourceOffset == carrier.SourceOffset) &&
                        (index || (actual.SourceLength == carrier.SourceLength && actual.SourceSha256 == carrier.SourceSha256)) && actual.Semantic == carrier.Semantic &&
                        actual.OwnerIdentity == carrier.OwnerIdentity && actual.Relationship == carrier.Relationship) != 1)
                    throw new NotSupportedException("Carrier is not the observed embedded ordinary primary camera metadata.");
            }
        }
        var result = new List<AuxiliaryBinding>();
        foreach (var semantic in semantics)
        {
            _ = AuxiliarySemanticCode(semantic.Semantic);
            if (
                semantic.EvidenceState is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable ||
                semantic.OwnerIdentity != "primary:0" || semantic.Ownership != AuxiliaryOwnership.Primary)
                throw new NotSupportedException("Auxiliary semantic identity, owner and evidence must be established.");
            var matches = rows.Where(row => row.StableIdentity == semantic.StableIdentity &&
                row.Semantic == semantic.Semantic && row.OwnerIdentity == semantic.OwnerIdentity &&
                row.Relationship == semantic.Relationship && row.SemanticRepresentation == semantic.Representation &&
                row.ImageContainer == semantic.ImageContainer && row.Ownership == semantic.Ownership).ToArray();
            if (matches.Length != 1 || matches[0].ByteLength <= 0 || matches[0].Sha256.Length != 64)
                throw new InvalidDataException("Auxiliary semantic asset does not match its unique final Neutral manifest row.");
            var row = matches[0];
            uint representation = semantic.Representation switch
            {
                NeutralAuxiliaryRepresentation.Embedded => 1,
                NeutralAuxiliaryRepresentation.Detached or NeutralAuxiliaryRepresentation.Materialized => 2,
                NeutralAuxiliaryRepresentation.Both => 3,
                _ => throw new NotSupportedException("Auxiliary representation is unknown.")
            };
            uint selectorKind = 0, selectorId = 0;
            if (representation == 1 && (bundle.AuxiliaryMedia.Any(item => item.Semantic == row.Semantic && item.MaterializedArtifact is not null) ||
                (row.Semantic == "GainMap" && bundle.GainMap is not null)))
                throw new InvalidDataException("An embedded asset with a materialized mirror must declare Both representation.");
            if (representation != 2)
            {
                if (row.OwnerByteOffset is not long embeddedOffset || embeddedOffset < 0 ||
                    embeddedOffset > bundle.PrimaryImage.ByteLength || row.ByteLength > bundle.PrimaryImage.ByteLength - embeddedOffset)
                    throw new InvalidDataException("Embedded auxiliary is outside its whole-file primary owner.");
                if (bundle.PrimaryImage.ImageContainer == ImageContainer.Heic)
                {
                    var observed = facts!.AuxiliaryItems.Where(item => item.IsPresent && item.GraphComplete &&
                        item.StableIdentity == row.StableIdentity && item.Semantic == row.Semantic &&
                        item.OwnerIdentity == row.OwnerIdentity && item.Relationship == row.Relationship &&
                        item.Container == row.ImageContainer && item.ByteOffset == row.OwnerByteOffset &&
                        item.ByteLength == row.ByteLength && string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (observed.Length != 1 || observed[0].ItemId == 0)
                        throw new InvalidDataException("Embedded auxiliary selector does not match the observed prepared primary graph.");
                    selectorKind = 2;
                    selectorId = observed[0].ItemId;
                }
                // JPEG kind/id zero requests Native resolution against its actual
                // semantic/relationship graph. Native freezes the unique observed
                // selector before fingerprinting; range/hash alone grant no authority.
            }
            MediaArtifact? materialized = null;
            if (representation != 1)
            {
                var candidates = bundle.AuxiliaryMedia.Where(item => item.Semantic == row.Semantic &&
                    item.OwnerIdentity == row.OwnerIdentity && item.Relationship == row.Relationship &&
                    item.MaterializedArtifact is not null).Select(item => item.MaterializedArtifact!).ToList();
                if (row.Semantic == "GainMap" && bundle.GainMap is { } image) candidates.Add(image);
                var matching = candidates.Where(item =>
                    string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(row.Path), StringComparison.OrdinalIgnoreCase) &&
                    item.ByteLength == row.ByteLength && item.ImageContainer == row.ImageContainer &&
                    string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 0 || matching.Length != candidates.Count || matching.Any(item => item.FileIdentity is null))
                    throw new InvalidDataException("Detached auxiliary is not a uniquely identified complete Neutral artifact.");
                MediaArtifact first = matching[0];
                if (matching.Any(item => item.FileIdentity != first.FileIdentity ||
                    item.Kind != first.Kind || item.ImageContainer != first.ImageContainer ||
                    item.ImageCodec != first.ImageCodec || item.VideoContainer != first.VideoContainer ||
                    item.VideoCodec != first.VideoCodec))
                    throw new InvalidDataException("Detached auxiliary references disagree on their materialized file identity or media representation.");
                materialized = first;
                // SourceExtractor retains the original source-range offset on an
                // independently materialized artifact. That diagnostic offset is
                // not a slice into this file. Native pins the whole file and checks
                // its complete identity, actual size and SHA against the final row.
                RejectSourceAlias(finalPath, materialized);
            }
            result.Add(new AuxiliaryBinding(row, representation, selectorKind, selectorId, materialized));
        }
        return result.ToArray();
    }

    private static void RequireManifest(NeutralMediaBundle bundle, MediaArtifact artifact)
    {
        if (artifact.FileIdentity is null || artifact.ByteLength <= 0 || artifact.SourceOffset != 0 ||
            artifact.Sha256 is null || artifact.Sha256.Length != 64)
            throw new InvalidDataException("Prepared artifacts require whole-file identity, length and SHA-256 evidence.");
        var matches = bundle.Manifest.Where(row => row.Role == artifact.Kind.ToString() &&
            string.Equals(Path.GetFullPath(row.Path), Path.GetFullPath(artifact.Path), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].ByteLength != artifact.ByteLength ||
            !string.Equals(matches[0].Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared artifact does not match its unique Neutral manifest member.");
    }

    private static void RejectSourceAlias(string finalPath, MediaArtifact artifact)
    {
        if (string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(artifact.Path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Target publication cannot overwrite a prepared input artifact.");
        if (File.Exists(finalPath))
        {
            WindowsFileIdentity actual = WindowsFileIdentity.Capture(finalPath);
            if (actual.VolumeSerialNumber == artifact.FileIdentity!.VolumeSerialNumber &&
                actual.FileIndex == artifact.FileIdentity.FileIndex)
                throw new IOException("Target publication resolves to a prepared input object.");
        }
    }

    private static int? KnownRotation(NeutralOrientationTransform? transform)
    {
        if (transform?.ClockwiseRotationDegrees is not int angle ||
            transform.RotationEvidence is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable) return null;
        if (angle is not (0 or 90 or 180 or 270))
            throw new NotSupportedException("Known orientation rotation is outside the canonical quarter-turn model.");
        return angle;
    }

    private static NeutralReflection? KnownReflection(NeutralOrientationTransform? transform) =>
        transform is null || transform.Reflection == NeutralReflection.Unknown ||
        transform.ReflectionEvidence is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable
            ? null : transform.Reflection;

    private sealed class Session : IDisposable
    {
        private readonly NativeContext _context;
        private readonly NativeContextLease _operation;
        private nint _plan;
        private NativeTargetWriterOutputV1 _output;
        private NativeCompoundPrimary? _compound;
        private bool _compoundRetained;

        internal unsafe Session(MediaArtifact image, MediaArtifact video, OutputProfile profile, uint target, uint targetVersion,
            long? cover, long? primaryTime, AuxiliaryBinding[] bindings, string stagingDirectory, CancellationToken token)
        {
            _context = NativeContext.Create(token);
            _operation = _context.AcquireOperationLease();
            try
            {
                NativeTargetWriterArtifactV1* artifacts = stackalloc NativeTargetWriterArtifactV1[2];
                artifacts[0] = Bind(image, 0);
                artifacts[1] = Bind(video, 1);
                var request = new NativeTargetWriterRequestV1
                {
                    StructSize = (uint)sizeof(NativeTargetWriterRequestV1), Version = 1,
                    TargetProfile = target,
                    TargetVersion = targetVersion, MediaFormat = (uint)profile.Format,
                    ArtifactCount = 2, Artifacts = (nint)artifacts,
                    CoverTimestampPresent = cover.HasValue ? 1u : 0, CoverTimestampUs = cover ?? 0
                };
                EncodePath(stagingDirectory, request.StagingDirectoryUtf8, 1024);
                NativeTargetAuxiliaryV2* auxiliaries = stackalloc NativeTargetAuxiliaryV2[2];
                for (int i = 0; i < bindings.Length; i++)
                {
                    var binding = bindings[i]; var row = binding.Row;
                    auxiliaries[i] = new NativeTargetAuxiliaryV2
                    {
                        StructSize = (uint)sizeof(NativeTargetAuxiliaryV2), Version = 2,
                        Representation = binding.Representation, SelectorKind = binding.SelectorKind,
                        SelectorId = binding.SelectorId, OwnerIndex = 0,
                        EmbeddedOffset = binding.Representation == 2 ? 0 : checked((ulong)row.OwnerByteOffset!.Value),
                        ByteLength = checked((ulong)row.ByteLength),
                        Materialized = binding.Materialized is null ? default : Bind(binding.Materialized, 0)
                    };
                    byte[] hash = Convert.FromHexString(row.Sha256);
                    for (int j = 0; j < 32; j++) auxiliaries[i].Sha256[j] = hash[j];
                    EncodeText(row.StableIdentity, auxiliaries[i].StableIdentity, 128);
                    EncodeText(row.OwnerIdentity, auxiliaries[i].OwnerIdentity, 128);
                    EncodeText(row.Semantic, auxiliaries[i].Semantic, 32);
                    EncodeText(row.Relationship, auxiliaries[i].Relationship, 96);
                }
                var requestV2 = new NativeTargetWriterRequestV2
                {
                    StructSize = (uint)sizeof(NativeTargetWriterRequestV2), Version = 2, Base = request,
                    PrimaryTimestampPresent = primaryTime.HasValue ? 1u : 0, PrimaryTimestampUs = primaryTime ?? 0,
                    AuxiliaryCount = (uint)bindings.Length, Auxiliaries = bindings.Length == 0 ? 0 : (nint)auxiliaries
                };
                EncodeText("primary:0", requestV2.PrimaryIdentity, 128);
                _compound = image.CompoundPrimary;
                if (_compound is not null)
                {
                    _compound.DangerousAddRef(ref _compoundRetained);
                    var requestV3 = new NativeTargetWriterRequestV3 { StructSize = (uint)sizeof(NativeTargetWriterRequestV3),
                        Version = 3, Base = requestV2, CompoundPrimaryToken = unchecked((ulong)_compound.DangerousGetHandle()) };
                    Require(NativeMethods.TargetWriterPrepareV3(_operation.Handle, &requestV3, out _plan), "prepare");
                }
                else Require(NativeMethods.TargetWriterPrepareV2(_operation.Handle, &requestV2, out _plan), "prepare");
                if (_plan == 0) throw new IOException("Native Writer did not issue a plan.");
                Require(NativeMethods.TargetWriterStage(_operation.Handle, _plan), "stage");
                var status = new NativeTargetWriterStatusV1
                { StructSize = (uint)sizeof(NativeTargetWriterStatusV1), Version = 1 };
                var output = new NativeTargetWriterOutputV1
                { StructSize = (uint)sizeof(NativeTargetWriterOutputV1), Version = 1 };
                Require(NativeMethods.TargetWriterGetOutputs(_operation.Handle, _plan, &output, 1,
                    out uint count, ref status), "query");
                if (count != 1 || status.State != 2 || status.OutputCount != 1 || status.FailureCategory != 0 ||
                    output.StructSize != sizeof(NativeTargetWriterOutputV1) || output.Version != 1 ||
                    output.ArtifactRole != 0 || output.ArtifactIndex != 0 || output.TargetProfile != request.TargetProfile ||
                    output.TargetVersion != request.TargetVersion || output.Generation != status.Generation || output.StageId == 0 ||
                    output.ByteLength == 0 || output.ByteLength > long.MaxValue || output.Identity.LinkCount != 1 ||
                    output.Identity.FileSize != output.ByteLength)
                    throw new IOException("Native Writer did not provide one complete sealed target artifact.");
                _output = output;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal async Task CopyToAsync(Stream writer, CancellationToken token)
        {
            byte[] buffer = new byte[128 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (ulong offset = 0; offset < _output.ByteLength;)
            {
                token.ThrowIfCancellationRequested();
                int count = checked((int)Math.Min((ulong)buffer.Length, _output.ByteLength - offset));
                Read(offset, buffer.AsSpan(0, count));
                hash.AppendData(buffer.AsSpan(0, count));
                await writer.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                offset += (uint)count;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), OutputHash()))
                throw new IOException("Transported target bytes do not match the Native sealed artifact.");
        }

        private unsafe void Read(ulong offset, Span<byte> buffer)
        {
            fixed (byte* bytes = buffer)
            {
                Require(NativeMethods.TargetWriterReadStage(_operation.Handle, _plan, _output.StageId,
                    offset, bytes, (uint)buffer.Length, out uint read), "read");
                if (read != buffer.Length) throw new EndOfStreamException("Native target stage transport was incomplete.");
            }
        }

        private unsafe byte[] OutputHash()
        {
            byte[] hash = new byte[32];
            for (int i = 0; i < hash.Length; i++) hash[i] = _output.Sha256[i];
            return hash;
        }

        private void Require(NativeResult result, string stage)
        {
            if (result == NativeResult.Ok) return;
            if (result == NativeResult.Cancelled) throw new OperationCanceledException("Native target Writer cancelled at " + stage);
            if (result == NativeResult.Unsupported) throw new NotSupportedException(_context.GetLastError() ?? "Native target capability is unsupported.");
            throw new IOException($"Native target Writer failed at {stage}: {result}; {_context.GetLastError()}");
        }

        private static unsafe NativeTargetWriterArtifactV1 Bind(MediaArtifact artifact, uint role)
        {
            var identity = artifact.FileIdentity!;
            var native = new NativeTargetWriterArtifactV1
            {
                StructSize = (uint)sizeof(NativeTargetWriterArtifactV1), Version = 1, Role = role,
                Container = role == 0 ? (uint)artifact.ImageContainer : (uint)artifact.VideoContainer,
                VideoCodec = role == 0 ? 0 : (uint)artifact.VideoCodec,
                ExpectedIdentity = new NativeFileIdentity
                {
                    VolumeSerial = identity.VolumeSerialNumber, FileIndex = identity.FileIndex,
                    FileSize = checked((ulong)artifact.ByteLength), LinkCount = identity.LinkCount
                }
            };
            byte[] hash = Convert.FromHexString(artifact.Sha256!);
            for (int i = 0; i < 32; i++) native.Sha256[i] = hash[i];
            EncodePath(artifact.Path, native.PathUtf8, 1024);
            return native;
        }

        private static unsafe void EncodePath(string path, byte* output, int capacity)
        {
            string full = Path.GetFullPath(path);
            if (full.Contains('\0')) throw new ArgumentException("A path cannot contain NUL.", nameof(path));
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(full);
            if (bytes.Length == 0 || bytes.Length >= capacity) throw new PathTooLongException("Native target path exceeds the explicit UTF-8 transport capacity.");
            new Span<byte>(output, capacity).Clear();
            bytes.CopyTo(new Span<byte>(output, bytes.Length));
        }

        private static unsafe void EncodeText(string text, byte* output, int capacity)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new InvalidDataException("Semantic identity text is empty or contains NUL.");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
            if (bytes.Length >= capacity) throw new InvalidDataException("Semantic identity exceeds its explicit ABI capacity.");
            new Span<byte>(output, capacity).Clear();
            bytes.CopyTo(new Span<byte>(output, bytes.Length));
        }

        public void Dispose()
        {
            if (_compoundRetained) { _compoundRetained = false; _compound!.DangerousRelease(); }
            NativeResult release = NativeResult.Ok;
            try
            {
                if (_plan != 0)
                {
                    nint plan = _plan;
                    _plan = 0;
                    release = NativeMethods.TargetWriterRelease(_operation.Handle, plan);
                }
            }
            finally { _operation.Dispose(); _context.Dispose(); }
            if (release != NativeResult.Ok)
                throw new IOException("Native target stage cleanup did not complete: " + release);
        }
    }
}
