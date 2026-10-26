using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R3StructuredFamilyTests
{
    [Theory]
    [Trait("Category", "Synthetic")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownLiveKeyInTopMetadataRejectsBeforeWriteAndAtFreshObservation(bool wrapUdta)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(KnownLiveKeyInTopMetadataRejectsBeforeWriteAndAtFreshObservation) + wrapUdta);
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        const string liveKey = "com.apple.quicktime.content.identifier";
        string ordinaryKey = new('x', liveKey.Length);
        byte[] metadata = MdtaMetadata(ordinaryKey);
        if (wrapUdta) metadata = Atom("udta", metadata);
        string videoPath = bundle.MotionVideo!.Path;
        File.WriteAllBytes(videoPath, File.ReadAllBytes(videoPath).Concat(metadata).ToArray());
        bundle = RebindVideo(bundle);
        string good = Path.Combine(folder.Path, "ordinary.jpg");
        var profile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4);
        await StructuredTargetWriterService.WriteAsync(bundle, profile, good, folder.Path, false, CancellationToken.None);
        var expected = new TargetValidationExpectations(2, 0, 2, 0) { UseV2 = true };
        byte[] changed = File.ReadAllBytes(good);
        byte[] ordinary = Encoding.ASCII.GetBytes(ordinaryKey), live = Encoding.ASCII.GetBytes(liveKey);
        int at = changed.AsSpan().IndexOf(ordinary);
        Assert.True(at > 0); Assert.Equal(at, changed.AsSpan().LastIndexOf(ordinary));
        live.CopyTo(changed, at); // Identical width: all box and target lengths remain valid.
        string mutated = Path.Combine(folder.Path, "mutated-for-fresh-validation.jpg");
        File.WriteAllBytes(mutated, changed);
        using (var fresh = File.OpenRead(mutated))
        {
            var rejected = NativeTargetValidator.Observe(fresh, expected, CancellationToken.None);
            Assert.Equal(NativeResult.Ok, rejected.InvocationResult);
            Assert.False(rejected.CanPublish); Assert.Equal(126u, rejected.Observed.RuleId);
            Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(rejected, expected));
        }
        byte[] badVideo = File.ReadAllBytes(videoPath);
        at = badVideo.AsSpan().IndexOf(ordinary); Assert.True(at > 0);
        live.CopyTo(badVideo, at); File.WriteAllBytes(videoPath, badVideo);
        bundle = RebindVideo(bundle);
        string[] files = Directory.GetFiles(folder.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        string imageHash = Hash(bundle.PrimaryImage.Path), videoHash = Hash(videoPath);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(
            bundle, profile, Path.Combine(folder.Path, "rejected.jpg"), folder.Path, false, CancellationToken.None));
        Assert.Contains("Live metadata key", error.Message, StringComparison.Ordinal);
        Assert.Equal(files, Directory.GetFiles(folder.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(imageHash, Hash(bundle.PrimaryImage.Path)); Assert.Equal(videoHash, Hash(videoPath));
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task ConsistentlyForgedDetachedRelationshipCannotAuthorizeMutation()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ConsistentlyForgedDetachedRelationshipCannotAuthorizeMutation));
        var bundle = WithDetachedOriginal(P9TargetTestInputs.Bundle(folder.Path), folder.Path);
        bundle = bundle with
        {
            Semantics = bundle.Semantics with { AuxiliaryMedia = bundle.Semantics.AuxiliaryMedia.Select(a => a with { Relationship = "gain-map" }).ToArray() },
            AuxiliaryMedia = bundle.AuxiliaryMedia.Select(a => a with { Relationship = "gain-map" }).ToArray(),
            Manifest = bundle.Manifest.Select(r => r.Semantic == "Original" ? r with { Relationship = "gain-map" } : r).ToArray()
        };
        string[] files = Directory.GetFiles(folder.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var before = files.ToDictionary(p => p, Hash);
        var error = await Assert.ThrowsAsync<IOException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            Path.Combine(folder.Path, "rejected.jpg"), folder.Path, false, CancellationToken.None));
        Assert.Contains("semantic/relationship", error.Message, StringComparison.Ordinal);
        Assert.Equal(files, Directory.GetFiles(folder.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        foreach (var file in before) Assert.Equal(file.Value, Hash(file.Key));
    }

    private static NeutralMediaBundle RebindVideo(NeutralMediaBundle bundle)
    {
        var video = bundle.MotionVideo! with { ByteLength = new FileInfo(bundle.MotionVideo!.Path).Length,
            Sha256 = Hash(bundle.MotionVideo.Path), FileIdentity = WindowsFileIdentity.Capture(bundle.MotionVideo.Path) };
        return bundle with { MotionVideo = video, Manifest = bundle.Manifest.Select(r => r.Role == "MotionVideo"
            ? r with { ByteLength = video.ByteLength, Sha256 = video.Sha256! } : r).ToArray() };
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static byte[] Atom(string type, params byte[][] parts)
    {
        byte[] body = parts.SelectMany(p => p).ToArray(), result = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, checked((uint)result.Length));
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4); body.CopyTo(result, 8); return result;
    }
    private static byte[] MdtaMetadata(string key)
    {
        byte[] handler = new byte[24]; Encoding.ASCII.GetBytes("mdta").CopyTo(handler, 8);
        return Atom("meta", new byte[4], Atom("hdlr", handler),
            Atom("keys", new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, Atom("mdta", Encoding.ASCII.GetBytes(key))),
            Atom("ilst", Atom("\0\0\0\u0001", Atom("data", new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }, Encoding.ASCII.GetBytes("pair-a")))));
    }

    [Theory]
    [InlineData(1u, 0, NeutralReflection.None)]
    [InlineData(2u, 0, NeutralReflection.Horizontal)]
    [InlineData(3u, 180, NeutralReflection.None)]
    [InlineData(4u, 180, NeutralReflection.Horizontal)]
    [InlineData(5u, 90, NeutralReflection.Horizontal)]
    [InlineData(6u, 90, NeutralReflection.None)]
    [InlineData(7u, 270, NeutralReflection.Horizontal)]
    [InlineData(8u, 270, NeutralReflection.None)]
    [InlineData(4u, 0, NeutralReflection.Vertical)]
    [InlineData(7u, 90, NeutralReflection.Vertical)]
    [InlineData(2u, 180, NeutralReflection.Vertical)]
    [InlineData(5u, 270, NeutralReflection.Vertical)]
    public void EachCanonicalOrientationHonorsSeparateKnownChannels(uint orientation, int rotation, NeutralReflection reflection)
    {
        var report = OrientationReport(orientation, orientation);
        var expected = new TargetValidationExpectations(2, 0, 2, 0)
        {
            ImageRotationDegrees = rotation, ImageReflection = reflection,
            VideoRotationDegrees = rotation, VideoReflection = reflection
        };
        NativeTargetValidator.RequirePass(report, expected);
        Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(report,
            expected with { ImageRotationDegrees = (rotation + 90) % 360 }));
        Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(report,
            expected with { VideoReflection = reflection == NeutralReflection.None ? NeutralReflection.Horizontal : NeutralReflection.None }));
    }

    [Theory]
    [InlineData(0u)] [InlineData(9u)]
    public void UnknownObservedOrientationCannotSatisfyKnownChannel(uint unknown)
    {
        var report = OrientationReport(unknown, unknown);
        NativeTargetValidator.RequirePass(report, new TargetValidationExpectations(2, 0, 2, 0));
        Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(report,
            new TargetValidationExpectations(2, 0, 2, 0) { ImageRotationDegrees = 0 }));
        Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(report,
            new TargetValidationExpectations(2, 0, 2, 0) { VideoReflection = NeutralReflection.None }));
    }

    [Theory]
    [InlineData(0, true)] [InlineData(90, false)]
    public async Task PartiallyKnownVideoRotationIsCheckedAtMandatoryFreshGate(int rotation, bool matches)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(PartiallyKnownVideoRotationIsCheckedAtMandatoryFreshGate) + rotation);
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        bundle = bundle with { Semantics = bundle.Semantics with
        { Orientation = new NeutralOrientationSemantics
        { MotionVideo = new NeutralOrientationTransform
        { ClockwiseRotationDegrees = rotation, RotationEvidence = NeutralEvidenceState.Verified,
            Reflection = NeutralReflection.Unknown, ReflectionEvidence = NeutralEvidenceState.Unknown } } } };
        string final = Path.Combine(folder.Path, "target.jpg");
        Task Write() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None);
        if (matches)
        {
            await Write();
            Assert.True(File.Exists(final));
            using var reader = File.OpenRead(final);
            var expected = new TargetValidationExpectations(2, 0, 2, 0)
            { UseV2 = true, VideoRotationDegrees = 0 };
            NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader, expected, CancellationToken.None), expected);
        }
        else
        {
            await Assert.ThrowsAsync<TargetValidationException>(Write);
            Assert.False(File.Exists(final));
            Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
        }
    }

    private static TargetValidationReport OrientationReport(uint image, uint video) => new(NativeResult.Ok,
        new NativeTargetValidationResultV1
        {
            StructuralStatus = 1, GenericMediaStatus = 1,
            ObservedTargetProfile = 2, ObservedTargetVersion = 1, ObservedMediaFormat = 0,
            ObservedVideoCodec = 2, CoverEncoding = 1, ObservedCoverTimestampUs = 0,
            ImageOrientation = image, VideoOrientation = video
        }, string.Empty);

    [Fact]
    public void VersionTwoTransportLayoutsMatchPairedNativeX64Definitions()
    {
        Assert.Equal(1568, Marshal.SizeOf<NativeTargetAuxiliaryV2>());
        Assert.Equal(456, Marshal.OffsetOf<NativeTargetAuxiliaryV2>(nameof(NativeTargetAuxiliaryV2.Materialized)).ToInt32());
        Assert.Equal(1248, Marshal.SizeOf<NativeTargetWriterRequestV2>());
        Assert.Equal(8, Marshal.OffsetOf<NativeTargetWriterRequestV2>(nameof(NativeTargetWriterRequestV2.Base)).ToInt32());
        Assert.Equal(1104, Marshal.OffsetOf<NativeTargetWriterRequestV2>(nameof(NativeTargetWriterRequestV2.Auxiliaries)).ToInt32());
        Assert.Equal(56, Marshal.SizeOf<NativeTargetAuxiliaryExpectationV2>());
        Assert.Equal(104, Marshal.SizeOf<NativeTargetValidationRequestV2>());
        Assert.Equal(64, Marshal.SizeOf<NativeTargetAuxiliaryObservedV2>());
        Assert.Equal(328, Marshal.SizeOf<NativeTargetValidationResultV2>());
        Assert.Equal(200, Marshal.OffsetOf<NativeTargetValidationResultV2>(nameof(NativeTargetValidationResultV2.Auxiliary0)).ToInt32());
    }

    [Theory]
    [InlineData(TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMp4, 1u)]
    [InlineData(TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMov, 1u)]
    [InlineData(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4, 2u)]
    [InlineData(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMov, 2u)]
    [InlineData(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4, 3u)]
    public async Task ProductionFamilyChainComparesFreshBytesAndIndependentOriginalTime(
        TargetProtocol target, OutputMediaFormat format, uint nativeTarget)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ProductionFamilyChainComparesFreshBytesAndIndependentOriginalTime) + target + format);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, 0, mov: format == OutputMediaFormat.JpegMov);
        if (target == TargetProtocol.OppoLivePhoto)
            bundle = bundle with { Semantics = bundle.Semantics with
            { Timing = bundle.Semantics.Timing with { PrimaryTimestampUs = 100000 } } };
        string final = Path.Combine(folder.Path, "target.jpg");
        var receipt = await StructuredTargetWriterService.WriteAsync(bundle, OutputProfile.Create(target, format),
            final, folder.Path, false, CancellationToken.None);
        using var reader = File.OpenRead(final);
        var expected = new TargetValidationExpectations(nativeTarget, (uint)format, 2, 0)
        { UseV2 = true, PrimaryTimestampUs = target == TargetProtocol.OppoLivePhoto ? 100000 : null };
        var report = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(report, expected);
        Assert.NotNull(report.ObservedV2);
        Assert.Equal(receipt.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(final))));
        Assert.Equal(File.ReadAllBytes(bundle.MotionVideo!.Path),
            File.ReadAllBytes(final).AsSpan(checked((int)report.Observed.VideoOffset), checked((int)report.Observed.VideoLength)).ToArray());
        if (target == TargetProtocol.OppoLivePhoto)
        {
            reader.Position = 0;
            var wrong = expected with { PrimaryTimestampUs = 0 };
            Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(report, wrong));
        }
    }

    [Fact]
    public async Task UnknownOppoOriginalTimeCannotBeInventedFromKnownCover()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(UnknownOppoOriginalTimeCannotBeInventedFromKnownCover));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, 0);
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4), final, folder.Path, false, CancellationToken.None));
        Assert.False(File.Exists(final));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task DetachedOriginalPublishesOnceAndFreshValidatorBindsItsBytes()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(DetachedOriginalPublishesOnceAndFreshValidatorBindsItsBytes));
        var bundle = WithDetachedOriginal(P9TargetTestInputs.Bundle(folder.Path), folder.Path);
        var original = bundle.AuxiliaryMedia.Single().MaterializedArtifact!;
        string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None);
        using var reader = File.OpenRead(final);
        var expected = new TargetValidationExpectations(2, 0, 2, 0)
        {
            UseV2 = true,
            Auxiliaries = [new TargetAuxiliaryExpectation(2, 1, checked((ulong)original.ByteLength), original.Sha256!)]
        };
        var report = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(report, expected);
        Assert.Equal(1u, report.ObservedV2!.Value.AuxiliaryCount);
        var observed = report.ObservedV2.Value.Auxiliary0;
        Assert.Equal(File.ReadAllBytes(original.Path), File.ReadAllBytes(final)
            .AsSpan(checked((int)observed.ByteOffset), checked((int)observed.ByteLength)).ToArray());
        var forged = expected with
        { Auxiliaries = [expected.Auxiliaries[0] with { Sha256 = new string('0', 64) }] };
        Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(report, forged));
    }

    [Fact]
    public async Task ForgedAuxiliaryOwnerCannotPublishOrOverwriteItsInput()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ForgedAuxiliaryOwnerCannotPublishOrOverwriteItsInput));
        var bundle = WithDetachedOriginal(P9TargetTestInputs.Bundle(folder.Path), folder.Path);
        bundle = bundle with { Semantics = bundle.Semantics with
        { AuxiliaryMedia = [bundle.Semantics.AuxiliaryMedia.Single() with { OwnerIdentity = "auxiliary:other" }] } };
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<InvalidDataException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None));
        Assert.False(File.Exists(final));
        Assert.Equal(3, Directory.GetFiles(folder.Path).Length);
    }

    private static NeutralMediaBundle WithDetachedOriginal(NeutralMediaBundle bundle, string directory)
    {
        string path = Path.Combine(directory, "original.jpg");
        File.WriteAllBytes(path, P9TargetTestInputs.Image);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var artifact = new MediaArtifact
        {
            Path = path, Kind = MediaArtifactKind.AuxiliaryItem, ImageContainer = ImageContainer.Jpeg,
            ByteLength = new FileInfo(path).Length, Sha256 = hash, FileIdentity = WindowsFileIdentity.Capture(path)
        };
        var semantic = new NeutralAuxiliarySemantics
        {
            StableIdentity = "synthetic:original:0", Semantic = "Original", OwnerIdentity = "primary:0",
            Relationship = "Original", ImageContainer = ImageContainer.Jpeg, Codec = AuxiliaryCodec.Jpeg,
            Representation = NeutralAuxiliaryRepresentation.Detached, Ownership = AuxiliaryOwnership.Primary,
            EvidenceState = NeutralEvidenceState.Verified
        };
        return bundle with
        {
            AuxiliaryMedia = [new AuxiliaryMediaDescriptor
            {
                StableIdentity = semantic.StableIdentity, Semantic = semantic.Semantic,
                OwnerIdentity = semantic.OwnerIdentity, Relationship = semantic.Relationship,
                SourceSha256 = hash, SourceLength = artifact.ByteLength, ImageContainer = ImageContainer.Jpeg,
                Representation = AuxiliaryRepresentation.Detached, Ownership = AuxiliaryOwnership.Primary,
                MaterializedArtifact = artifact, Codec = AuxiliaryCodec.Jpeg
            }],
            Manifest = bundle.Manifest.Append(new NeutralArtifactManifest
            {
                Role = "Auxiliary", Path = path, Sha256 = hash, ByteLength = artifact.ByteLength,
                StableIdentity = semantic.StableIdentity, Semantic = semantic.Semantic,
                OwnerIdentity = semantic.OwnerIdentity, Relationship = semantic.Relationship,
                ImageContainer = ImageContainer.Jpeg, AuxiliaryCodec = AuxiliaryCodec.Jpeg,
                SemanticRepresentation = NeutralAuxiliaryRepresentation.Detached, Ownership = AuxiliaryOwnership.Primary
            }).ToArray(),
            Semantics = bundle.Semantics with { AuxiliaryMedia = [semantic] }
        };
    }
}
