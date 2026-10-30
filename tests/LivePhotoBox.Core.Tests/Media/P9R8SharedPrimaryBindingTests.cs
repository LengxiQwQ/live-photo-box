using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8SharedPrimaryBindingTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("vivo.jpg", false, false)]
    [InlineData("vivo.jpg", true, false)]
    [InlineData("vivo.jpg", false, true)]
    [InlineData("vivo.jpg", true, true)]
    [InlineData("一加.jpg", false, false)]
    [InlineData("一加.jpg", true, false)]
    [InlineData("一加.jpg", false, true)]
    [InlineData("一加.jpg", true, true)]
    public async Task EmbeddedHeicGainMapUsesObservedRelationship(string filename, bool honor, bool explicitHevc)
    {
        string source = Cached(filename), before = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(EmbeddedHeicGainMapUsesObservedRelationship) + filename + honor + explicitHevc);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto,
            explicitHevc ? OutputMediaFormat.HeicMp4Hevc : OutputMediaFormat.HeicMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            profile.MediaRequirement, PreservationPolicy.BestEffort);
        var gainMap = Assert.Single(bundle.Manifest, row => row.Semantic == "GainMap");
        Assert.Equal(NeutralAuxiliaryRepresentation.Embedded, gainMap.SemanticRepresentation);
        Assert.StartsWith("urn:", gainMap.Relationship);
        byte[] primary = File.ReadAllBytes(bundle.PrimaryImage.Path);
        string output = Path.Combine(folder.Path, "target.heic");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false,
            CancellationToken.None, honor ? HuaweiTargetVariant.HonorV2 : HuaweiTargetVariant.HuaweiV6);
        byte[] actual = File.ReadAllBytes(output);
        Assert.True(actual.Length > primary.Length);
        Assert.Equal(primary, actual[..primary.Length]);
        var expected = new TargetValidationExpectations(6, (uint)profile.Format, (uint)bundle.MotionVideo!.VideoCodec,
            bundle.Semantics.Timing.CoverTimestampUs)
        {
            UseV2 = true, TargetVersion = honor ? 2u : 1u,
            Auxiliaries = [new TargetAuxiliaryExpectation(1, (uint)gainMap.ImageContainer, (ulong)gainMap.ByteLength, gainMap.Sha256)]
        };
        using var reader = File.OpenRead(output);
        NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader, expected, CancellationToken.None), expected);
        Assert.Equal(before, Hash(source));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task ConsistentlyForgedHeicRelationshipStillCannotAuthorizeMutation()
    {
        string source = Cached("vivo.jpg"), before = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(ConsistentlyForgedHeicRelationshipStillCannotAuthorizeMutation));
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.HeicMp4Hevc);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            profile.MediaRequirement, PreservationPolicy.BestEffort);
        Assert.NotNull(bundle.Semantics.GainMap);
        const string forged = "urn:forged:gainmap";
        bundle = bundle with
        {
            Semantics = bundle.Semantics with { GainMap = bundle.Semantics.GainMap with { Relationship = forged } },
            AuxiliaryMedia = bundle.AuxiliaryMedia.Select(row => row.Semantic == "GainMap" ? row with { Relationship = forged } : row).ToArray(),
            Manifest = bundle.Manifest.Select(row => row.Semantic == "GainMap" ? row with { Relationship = forged } : row).ToArray()
        };
        string output = Path.Combine(folder.Path, "rejected.heic");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => StructuredTargetWriterService.WriteAsync(bundle, profile,
            output, workspace.RootDirectory, false, CancellationToken.None));
        Assert.Contains("selector does not match the observed prepared primary graph", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Equal(before, Hash(source));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingletonMpfSelfSizeIsReconciledForNewTarget(bool honor)
    {
        string source = Cached("oppo.jpg"), before = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(SingletonMpfSelfSizeIsReconciledForNewTarget) + honor);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            profile.MediaRequirement, PreservationPolicy.Strict);
        byte[] prepared = File.ReadAllBytes(bundle.PrimaryImage.Path);
        string preparedHash = Hash(bundle.PrimaryImage.Path);
        var (sizeField, little) = SingletonSizeField(prepared);
        byte[] expectedPrimary = (byte[])prepared.Clone();
        uint previous = little ? BinaryPrimitives.ReadUInt32LittleEndian(prepared.AsSpan(sizeField)) : BinaryPrimitives.ReadUInt32BigEndian(prepared.AsSpan(sizeField));
        Assert.NotEqual((uint)prepared.Length, previous);
        if (little) BinaryPrimitives.WriteUInt32LittleEndian(expectedPrimary.AsSpan(sizeField), (uint)prepared.Length);
        else BinaryPrimitives.WriteUInt32BigEndian(expectedPrimary.AsSpan(sizeField), (uint)prepared.Length);
        string output = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false,
            CancellationToken.None, honor ? HuaweiTargetVariant.HonorV2 : HuaweiTargetVariant.HuaweiV6);
        var expected = new TargetValidationExpectations(6, 0, (uint)bundle.MotionVideo!.VideoCodec,
            bundle.Semantics.Timing.CoverTimestampUs) { UseV2 = true, TargetVersion = honor ? 2u : 1u };
        using var reader = File.OpenRead(output);
        NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader, expected, CancellationToken.None), expected);
        Assert.Equal(expectedPrimary, File.ReadAllBytes(output)[..prepared.Length]);
        Assert.Equal(preparedHash, Hash(bundle.PrimaryImage.Path));
        Assert.Equal(before, Hash(source));
    }

    private static (int Field, bool Little) SingletonSizeField(byte[] image)
    {
        Assert.Equal(new byte[] { 255, 216 }, image[..2]);
        for (int p = 2; p + 4 <= image.Length;)
        {
            Assert.Equal(255, image[p++]);
            while (image[p] == 255) p++;
            byte marker = image[p++];
            Assert.NotEqual(218, marker);
            int length = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(p));
            Assert.InRange(length, 2, image.Length - p);
            int payload = p + 2;
            if (marker == 226 && image.AsSpan(payload, Math.Min(length - 2, 4)).SequenceEqual(new byte[] { 77, 80, 70, 0 }))
            {
                int tiff = payload + 4;
                bool little = image[tiff] == 73;
                uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(at));
                ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(at));
                Assert.True(little ? image[tiff + 1] == 73 : image[tiff] == 77 && image[tiff + 1] == 77);
                Assert.Equal(42, U16(tiff + 2));
                int ifd = checked(tiff + (int)U32(tiff + 4)), count = U16(ifd);
                int field = -1; bool singleton = false;
                for (int i = 0; i < count; i++)
                {
                    int entry = ifd + 2 + i * 12;
                    if (U16(entry) == 0xb001) singleton = U32(entry + 8) == 1;
                    if (U16(entry) == 0xb002) { Assert.Equal(16u, U32(entry + 4)); field = checked(tiff + (int)U32(entry + 8) + 4); }
                }
                Assert.True(singleton && field > 0 && field + 4 <= p + length);
                return (field, little);
            }
            p += length;
        }
        throw new InvalidDataException("Required singleton MPF index was not observed in JPEG markers.");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeHeicRelationshipProofIgnoresArtifactOrderAndRejectsForgeries()
    {
        string source = Cached("vivo.jpg"), before = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(NativeHeicRelationshipProofIgnoresArtifactOrderAndRejectsForgeries));
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.HeicMp4Hevc);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            profile.MediaRequirement, PreservationPolicy.BestEffort);
        var facts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        uint item = Assert.Single(facts.AuxiliaryItems, row => row.Semantic == "GainMap").ItemId;
        Assert.True(item > 0);
        string[] initial = Directory.GetFiles(folder.Path);
        foreach (string mode in new[] { "reversed", "relationship", "selector", "range", "hash", "detached-uri", "jpeg-uri" })
            CheckNativeHeicRequest(bundle, item, folder.Path, mode);
        Assert.Equal(initial, Directory.GetFiles(folder.Path));
        Assert.Equal(before, Hash(source));
    }

    private static unsafe void CheckNativeHeicRequest(NeutralMediaBundle bundle, uint selector, string stage, string mode)
    {
        static void Text(string value, byte* output, int size)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value); Assert.True(bytes.Length < size);
            new Span<byte>(output, size).Clear(); bytes.CopyTo(new Span<byte>(output, size));
        }
        static NativeTargetWriterArtifactV1 Artifact(MediaArtifact artifact, uint role)
        {
            var identity = artifact.FileIdentity!;
            var result = new NativeTargetWriterArtifactV1
            {
                StructSize = (uint)sizeof(NativeTargetWriterArtifactV1), Version = 1, Role = role,
                Container = role == 0 ? (uint)artifact.ImageContainer : (uint)artifact.VideoContainer,
                VideoCodec = role == 0 ? 0 : (uint)artifact.VideoCodec,
                ExpectedIdentity = new NativeCleanService.NativeFileIdentity
                {
                    VolumeSerial = identity.VolumeSerialNumber, FileIndex = identity.FileIndex,
                    FileSize = (ulong)artifact.ByteLength, LinkCount = identity.LinkCount
                }
            };
            Convert.FromHexString(artifact.Sha256!).CopyTo(new Span<byte>(result.Sha256, 32));
            Text(artifact.Path, result.PathUtf8, 1024); return result;
        }
        using var context = NativeContext.Create(); using var operation = context.AcquireOperationLease();
        NativeTargetWriterArtifactV1* artifacts = stackalloc NativeTargetWriterArtifactV1[2];
        artifacts[0] = Artifact(bundle.MotionVideo!, 1); artifacts[1] = Artifact(bundle.PrimaryImage, 0);
        if (mode == "jpeg-uri") artifacts[1].Container = (uint)ImageContainer.Jpeg;
        var row = Assert.Single(bundle.Manifest, r => r.Semantic == "GainMap");
        var auxiliary = new NativeTargetAuxiliaryV2
        {
            StructSize = (uint)sizeof(NativeTargetAuxiliaryV2), Version = 2, Representation = 1,
            SelectorKind = 2, SelectorId = selector,
            EmbeddedOffset = (ulong)row.OwnerByteOffset!.Value, ByteLength = (ulong)row.ByteLength
        };
        Text(row.StableIdentity, auxiliary.StableIdentity, 128); Text(row.OwnerIdentity, auxiliary.OwnerIdentity, 128);
        Text(row.Semantic, auxiliary.Semantic, 32); Text(mode == "relationship" ? "urn:forged:gainmap" : row.Relationship, auxiliary.Relationship, 96);
        Convert.FromHexString(row.Sha256).CopyTo(new Span<byte>(auxiliary.Sha256, 32));
        if (mode == "selector") auxiliary.SelectorId++;
        if (mode == "range") auxiliary.EmbeddedOffset++;
        if (mode == "hash") auxiliary.Sha256[0] ^= 1;
        if (mode == "detached-uri") { auxiliary.Representation = 2; auxiliary.SelectorKind = auxiliary.SelectorId = 0; auxiliary.EmbeddedOffset = 0; }
        var request = new NativeTargetWriterRequestV2
        {
            StructSize = (uint)sizeof(NativeTargetWriterRequestV2), Version = 2,
            AuxiliaryCount = 1, Auxiliaries = (nint)(&auxiliary),
            Base = new NativeTargetWriterRequestV1
            {
                StructSize = (uint)sizeof(NativeTargetWriterRequestV1), Version = 1,
                TargetProfile = 6, TargetVersion = 1, MediaFormat = (uint)OutputMediaFormat.HeicMp4Hevc,
                ArtifactCount = 2, Artifacts = (nint)artifacts,
                CoverTimestampPresent = 1, CoverTimestampUs = bundle.Semantics.Timing.CoverTimestampUs!.Value
            }
        };
        Text(stage, request.Base.StagingDirectoryUtf8, 1024); Text("primary:0", request.PrimaryIdentity, 128);
        NativeResult result = NativeMethods.TargetWriterPrepareV2(operation.Handle, &request, out nint plan);
        if (mode == "reversed")
        {
            Assert.True(result == NativeResult.Ok, context.GetLastError()); Assert.NotEqual(0, plan);
            Assert.Equal(NativeResult.Ok, NativeMethods.TargetWriterRelease(operation.Handle, plan));
        }
        else
        {
            Assert.Equal(mode == "hash" ? NativeResult.SourceChanged : NativeResult.InvalidArgument, result);
            Assert.Equal(0, plan);
        }
    }

    private static string Cached(string filename)
    {
        string path = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples", filename));
        Assert.True(File.Exists(path), "Required RealSample is missing: " + path);
        return path;
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
