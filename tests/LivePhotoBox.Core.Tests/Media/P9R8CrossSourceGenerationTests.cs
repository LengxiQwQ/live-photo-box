using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8CrossSourceGenerationTests
{
    [Theory]
    [InlineData(null, 100000L)]
    [InlineData(50000L, 50000L)]
    public async Task ExplicitNewTargetPolicyInitializesOnlyMissingHistory(long? original, long expectedPrimary)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ExplicitNewTargetPolicyInitializesOnlyMissingHistory) + original);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, 100000);
        bundle = bundle with { Semantics = bundle.Semantics with { Timing = bundle.Semantics.Timing with { PrimaryTimestampUs = original } } };
        string imageHash = Hash(bundle.PrimaryImage.Path), videoHash = Hash(bundle.MotionVideo!.Path);
        var profile = OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4);
        if (original is null)
            await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(bundle, profile,
                Path.Combine(folder.Path, "default-rejected.jpg"), folder.Path, false, CancellationToken.None));
        string output = Path.Combine(folder.Path, "new-target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, folder.Path, false, CancellationToken.None,
            oppoInitialCoverPolicy: OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover);
        var expected = new TargetValidationExpectations(3, 0, 2, 100000) { UseV2 = true, PrimaryTimestampUs = expectedPrimary };
        using (var reader = File.OpenRead(output))
        {
            var observed = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
            NativeTargetValidator.RequirePass(observed, expected);
            Assert.True(observed.ObservedV2.HasValue);
            Assert.Equal(expectedPrimary, observed.ObservedV2.Value.ObservedPrimaryTimestampUs);
            Assert.Equal(100000, observed.Observed.ObservedCoverTimestampUs);
        }
        Assert.Equal(original, bundle.Semantics.Timing.PrimaryTimestampUs);
        Assert.Equal(imageHash, Hash(bundle.PrimaryImage.Path));
        Assert.Equal(videoHash, Hash(bundle.MotionVideo.Path));
        Assert.Equal(Scan(bundle.PrimaryImage.Path), Scan(output));
    }

    [Fact]
    public async Task NewTargetPolicyDoesNotGuessUnknownCover()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(NewTargetPolicyDoesNotGuessUnknownCover));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, null);
        string output = Path.Combine(folder.Path, "rejected.jpg");
        await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4), output, folder.Path, false,
            CancellationToken.None, oppoInitialCoverPolicy: OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task OppoPolicyCannotAffectOtherTarget()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(OppoPolicyCannotAffectOtherTarget));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, 100000);
        string output = Path.Combine(folder.Path, "rejected.jpg");
        await Assert.ThrowsAsync<ArgumentException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4), output, folder.Path, false,
            CancellationToken.None, oppoInitialCoverPolicy: OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover));
        Assert.False(File.Exists(output));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task XiaomiOriginalCreatesOppoWithoutChangingCurrentPhoto()
    {
        string source = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples/小米.jpg"));
        Assert.True(File.Exists(source), "Required Xiaomi RealSample is missing: " + source);
        string sourceHash = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(XiaomiOriginalCreatesOppoWithoutChangingCurrentPhoto));
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace, profile.MediaRequirement, PreservationPolicy.BestEffort);
        Assert.Null(bundle.Semantics.Timing.PrimaryTimestampUs);
        Assert.True(bundle.Semantics.Timing.CoverTimestampUs.HasValue);
        string output = Path.Combine(folder.Path, "xiaomi-to-oppo.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false, CancellationToken.None,
            oppoInitialCoverPolicy: OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover);
        Assert.Equal(Scan(bundle.PrimaryImage.Path), Scan(output));
        Assert.Equal(sourceHash, Hash(source));
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData(TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMp4)]
    [InlineData(TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMov)]
    [InlineData(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4)]
    [InlineData(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMov)]
    [InlineData(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4)]
    public async Task HuaweiMpfOriginalCreatesCrossTargetWithExactAuxiliaryAndSuffix(TargetProtocol target, OutputMediaFormat format)
    {
        string source = Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "../../../../../cache/samples/华为-Mate80.jpg"));
        Assert.True(File.Exists(source), "Required Huawei RealSample is missing: " + source);
        string sourceHash = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(HuaweiMpfOriginalCreatesCrossTargetWithExactAuxiliaryAndSuffix) + target + format);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(target, format);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace, profile.MediaRequirement, PreservationPolicy.Strict);
        var auxiliary = Assert.Single(bundle.Manifest, r => r.Semantic == "MpfAuxiliaryImage");
        var carrier = Assert.Single(bundle.PreservationCarriers);
        Assert.Equal("CameraMetadata", carrier.Semantic);
        string output = Path.Combine(folder.Path, "huawei-cross-target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, output, workspace.RootDirectory, false, CancellationToken.None,
            oppoInitialCoverPolicy: target == TargetProtocol.OppoLivePhoto ? OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover : OppoInitialCoverPolicy.RequireKnownOriginal);
        uint nativeTarget = target == TargetProtocol.GoogleMicroVideoV1 ? 1u : target == TargetProtocol.GoogleMotionPhotoV2 ? 2u : 3u;
        var expected = new TargetValidationExpectations(nativeTarget, (uint)format, (uint)bundle.MotionVideo!.VideoCodec, bundle.Semantics.Timing.CoverTimestampUs)
        {
            UseV2 = true, PrimaryTimestampUs = nativeTarget == 3 ? bundle.Semantics.Timing.PrimaryTimestampUs ?? bundle.Semantics.Timing.CoverTimestampUs : null,
            Auxiliaries = new[] { new TargetAuxiliaryExpectation(5, 1, (ulong)auxiliary.ByteLength, auxiliary.Sha256) }
        };
        using var reader = File.OpenRead(output);
        var observed = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(observed, expected);
        Assert.True(observed.ObservedV2.HasValue);
        var actualAuxiliary = observed.ObservedV2.Value.Auxiliary0;
        byte[] targetBytes = File.ReadAllBytes(output);
        byte[] secondary = targetBytes.AsSpan(checked((int)actualAuxiliary.ByteOffset), checked((int)actualAuxiliary.ByteLength)).ToArray();
        Assert.Equal(auxiliary.Sha256, Convert.ToHexString(SHA256.HashData(secondary)), ignoreCase: true);
        long suffixOffset = checked((long)(actualAuxiliary.ByteOffset + actualAuxiliary.ByteLength));
        long suffixLength = checked((long)observed.Observed.VideoOffset - suffixOffset);
        Assert.Equal(carrier.SourceLength, suffixLength);
        Assert.Equal(carrier.SourceSha256, Convert.ToHexString(SHA256.HashData(targetBytes.AsSpan(checked((int)suffixOffset), checked((int)suffixLength)))), ignoreCase: true);
        Assert.Equal(Scan(bundle.PrimaryImage.Path), Scan(output));
        Assert.Equal(sourceHash, Hash(source));
    }
    private static byte[] Scan(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        Assert.True(data.Length > 4 && data[0] == 255 && data[1] == 216);
        int p = 2;
        while (p + 4 <= data.Length)
        {
            int start = p;
            Assert.Equal(255, data[p++]);
            while (data[p] == 255) p++;
            byte marker = data[p++];
            int size = (data[p] << 8) | data[p + 1];
            Assert.True(size >= 2 && p + size <= data.Length);
            p += size;
            if (marker != 218) continue;
            while (p + 1 < data.Length)
            {
                if (data[p] != 255) { p++; continue; }
                int q = p + 1;
                while (data[q] == 255) q++;
                if (data[q] == 0 || data[q] is >= 208 and <= 215) { p = q + 1; continue; }
                Assert.Equal(217, data[q]);
                return data[start..(q + 1)];
            }
        }
        throw new InvalidDataException("Missing JPEG scan/EOI.");
    }
}
