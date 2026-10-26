using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;
using NativeFileIdentity = LivePhotoBox.Interop.NativeCleanService.NativeFileIdentity;

namespace LivePhotoBox.Services;

/// <summary>
/// Experimental structured Native target path. Product adapters migrate only after
/// their complete target lifecycle; this service has no fallback or selectable validator.
/// </summary>
internal static class StructuredTargetWriterService
{
    internal static async Task<PublishedOwnedFile> WriteAsync(NeutralMediaBundle bundle,
        OutputProfile profile, string finalPath, string stagingDirectory,
        bool overwriteExisting, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.TargetProtocol != TargetProtocol.GoogleMotionPhotoV2 ||
            profile.Format is not (OutputMediaFormat.JpegMp4 or OutputMediaFormat.JpegMov))
            throw new NotSupportedException("This experimental structured path currently implements Google V2 JPEG only.");
        MediaArtifact video = bundle.MotionVideo ?? throw new InvalidDataException("A prepared motion video is required.");
        if (bundle.GainMap is not null || bundle.GainMapRepresentation != GainMapRepresentation.None ||
            bundle.AuxiliaryMedia.Count != 0 || bundle.PreservationCarriers.Count != 0 ||
            bundle.Semantics.GainMap is not null || bundle.Semantics.AuxiliaryMedia.Count != 0)
            throw new NotSupportedException("Auxiliary/HDR/carrier integration is pending; assets cannot be silently discarded.");
        if (bundle.PrimaryImage.Kind != MediaArtifactKind.PrimaryImage || video.Kind != MediaArtifactKind.MotionVideo ||
            bundle.PrimaryImage.ImageContainer != ImageContainer.Jpeg ||
            video.VideoContainer != profile.MediaRequirement.VideoContainer ||
            video.VideoCodec is not (VideoCodec.H264 or VideoCodec.Hevc))
            throw new InvalidDataException("Prepared artifact roles and formats do not match the structured target profile.");
        RequireManifest(bundle, bundle.PrimaryImage);
        RequireManifest(bundle, video);
        RejectSourceAlias(finalPath, bundle.PrimaryImage);
        RejectSourceAlias(finalPath, video);
        long? cover = bundle.Semantics.Timing.CoverTimestampUs;
        var expected = new TargetValidationExpectations(2, (uint)profile.Format, (uint)video.VideoCodec, cover)
        {
            ImageOrientation = KnownOrientation(bundle.Semantics.Orientation.PrimaryImage),
            VideoOrientation = KnownOrientation(bundle.Semantics.Orientation.MotionVideo)
        };
        using var session = new Session(bundle.PrimaryImage, video, profile, cover, stagingDirectory, token);
        return await WindowsSplitOutputPublisher.PublishTargetAsync(finalPath, overwriteExisting,
            session.CopyToAsync, expected, token).ConfigureAwait(false);
    }

    private static void RequireManifest(NeutralMediaBundle bundle, MediaArtifact artifact)
    {
        if (artifact.FileIdentity is null || artifact.ByteLength <= 0 || artifact.SourceOffset != 0 ||
            artifact.Sha256 is null || artifact.Sha256.Length != 64)
            throw new InvalidDataException("Prepared artifacts require whole-file identity, length and SHA-256 evidence.");
        var matches = bundle.Manifest.Where(row =>
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

    private static uint? KnownOrientation(NeutralOrientationTransform? transform)
    {
        if (transform is null) return null;
        bool knownRotation = transform.ClockwiseRotationDegrees.HasValue &&
            transform.RotationEvidence is not (NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable);
        bool knownReflection = transform.Reflection != NeutralReflection.Unknown &&
            transform.ReflectionEvidence is not (NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable);
        if (!knownRotation && !knownReflection) return null;
        if (!knownRotation || !knownReflection)
            throw new NotSupportedException("Partially established orientation cannot be silently dropped by this experimental path.");
        int angle = transform.ClockwiseRotationDegrees!.Value;
        return (angle, transform.Reflection) switch
        {
            (0, NeutralReflection.None) => 1, (90, NeutralReflection.None) => 6,
            (180, NeutralReflection.None) => 3, (270, NeutralReflection.None) => 8,
            (0, NeutralReflection.Horizontal) => 2, (0, NeutralReflection.Vertical) => 4,
            _ => throw new NotSupportedException("This experimental target path cannot prove the prepared orientation transform.")
        };
    }

    private sealed class Session : IDisposable
    {
        private readonly NativeContext _context;
        private readonly NativeContextLease _operation;
        private nint _plan;
        private NativeTargetWriterOutputV1 _output;

        internal unsafe Session(MediaArtifact image, MediaArtifact video, OutputProfile profile,
            long? cover, string stagingDirectory, CancellationToken token)
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
                    TargetProfile = 2, TargetVersion = 1, MediaFormat = (uint)profile.Format,
                    ArtifactCount = 2, Artifacts = (nint)artifacts,
                    CoverTimestampPresent = cover.HasValue ? 1u : 0, CoverTimestampUs = cover ?? 0
                };
                EncodePath(stagingDirectory, request.StagingDirectoryUtf8, 1024);
                Require(NativeMethods.TargetWriterPrepare(_operation.Handle, &request, out _plan), "prepare");
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
                    output.ArtifactRole != 0 || output.ArtifactIndex != 0 || output.TargetProfile != 2 ||
                    output.TargetVersion != 1 || output.Generation != status.Generation || output.StageId == 0 ||
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

        public void Dispose()
        {
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
