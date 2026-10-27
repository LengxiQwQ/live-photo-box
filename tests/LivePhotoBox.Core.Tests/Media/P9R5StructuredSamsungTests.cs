using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using LivePhotoBox.Interop;
using System.Buffers.Binary;
using System.Text;
using Xunit;
namespace LivePhotoBox.Core.Tests.Media;
public sealed class P9R5StructuredSamsungTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("三星.jpg", false, false)] [InlineData("三星.jpg", false, true)]
    [InlineData("三星.heic", true, false)] [InlineData("三星.heic", true, true)]
    [InlineData("红米老款-GV1.JPG", false, true)] [InlineData("小米.jpg", false, true)]
    public async Task CachedOriginalThroughActualNeutralSamsungLifecycle(string filename, bool heic, bool mpv3)
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, filename);
        Assert.True(File.Exists(source), "Required real source missing: " + source);
        string hash = Hash(source);
        string root = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R5_OUTPUTS") ?? throw new InvalidOperationException("R5 outputs required");
        string evidence = Path.Combine(root, "real-" + filename.Replace('.', '_') + (mpv3 ? "-mpv3" : "-mpv2"));
        Directory.CreateDirectory(evidence);
        var sourceFacts = await new SourceInspector().InspectAsync(source);
        File.WriteAllText(Path.Combine(evidence, "source-facts.json"), JsonSerializer.Serialize(sourceFacts, new JsonSerializerOptions { WriteIndented = true }));
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, heic ? OutputMediaFormat.HeicMp4 : OutputMediaFormat.JpegMp4);
        try
        {
            var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace, profile.MediaRequirement, PreservationPolicy.Strict);
            File.Copy(bundle.PrimaryImage.Path, Path.Combine(evidence, heic ? "neutral.heic" : "neutral.jpg"), true);
            File.Copy(bundle.MotionVideo!.Path, Path.Combine(evidence, "neutral.mp4"), true);
            File.WriteAllText(Path.Combine(evidence, "neutral.json"), JsonSerializer.Serialize(new { bundle.Manifest, bundle.Semantics, bundle.AuxiliaryMedia, bundle.PreservationCarriers }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path)).Protocol);
            string final = Path.Combine(evidence, heic ? "target.heic" : "target.jpg");
            await StructuredTargetWriterService.WriteAsync(bundle, profile, final, workspace.RootDirectory, true, CancellationToken.None,
                samsungVariant: mpv3 ? SamsungTargetVariant.Mpv3 : SamsungTargetVariant.Mpv2);
            var expected = new TargetValidationExpectations(5, (uint)profile.Format, (uint)bundle.MotionVideo.VideoCodec, bundle.Semantics.Timing.CoverTimestampUs)
            {
                UseV2 = true, TargetVersion = mpv3 ? 3u : 2u,
                Auxiliaries = bundle.Manifest.Where(row => row.Role is not ("PrimaryImage" or "MotionVideo"))
                    .Select(row => new TargetAuxiliaryExpectation(row.Semantic == "GainMap" ? 1u : row.Semantic == "Original" ? 2u : row.Semantic == "MiCameraSubimage" ? 3u : 4u,
                        (uint)row.ImageContainer, (ulong)row.ByteLength, row.Sha256)).ToArray()
            };
            using (var reader = File.OpenRead(final)) NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader, expected, CancellationToken.None), expected);
            // Samsung-compatible EOF locators do not grant strict Google V2 conformance.
            using (var reader = File.OpenRead(final)) Assert.False(NativeTargetValidator.Observe(reader,
                expected with { TargetProfile = 2, TargetVersion = 1, MediaFormat = heic ? 3u : 0u }, CancellationToken.None).CanPublish);
            if (filename.StartsWith("三星", StringComparison.Ordinal)) Mutations(final, expected, evidence, heic);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(evidence, "failure.txt"), ex.ToString()); throw; }
        finally { Assert.Equal(hash, Hash(source)); }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    private static void Mutations(string final, TargetValidationExpectations expected, string evidence, bool heic)
    {
        byte[] original = File.ReadAllBytes(final);
        int table = original.Length - 8 - checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(original.Length - 8)));
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(table + 8)));
        int motionIndex = Enumerable.Range(0, count).Select(i => table + 12 + i * 12)
            .Single(i => BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(i + 2)) == 0xa30);
        int versionIndex = Enumerable.Range(0, count).Select(i => table + 12 + i * 12)
            .Single(i => BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(i + 2)) == 0xa31);
        int motion = table - checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(motionIndex + 4)));
        int version = table - checked((int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(versionIndex + 4)));
        var cases = new List<(string Name, Action<byte[]> Change, uint Rule)>
        {
            ("footer-magic", b => b[^1] ^= 1, 151),
            ("footer-wrong-size", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(b.Length - 8), (uint)(12 + count * 12 + 8)), 151),
            ("sef-version", b => b[table + 4] = 106, 151),
            ("count-width", b => b[table + 8]++, 151),
            ("entry-wrong-base", b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(motionIndex + 4), uint.MaxValue), 151),
            ("entry-size", b => b[motionIndex + 8]++, 151),
            ("motion-name", b => b[motion + 8] ^= 1, 151),
            ("motion-prefix", b => b[motion] = 1, 151),
            ("duplicate-marker", b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(versionIndex + 2), 0xa30), 151),
            ("overlap", b => original.AsSpan(motionIndex + 4, 8).CopyTo(b.AsSpan(versionIndex + 4)), 151),
            ("mpv4-version", b => b[version + 30] = (byte)'4', 152)
        };
        if (heic)
        {
            cases.Add(("pointer-relative-base", b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(motion + 28), 8), 153));
            cases.Add(("pointer-size", b => b[motion + 35] ^= 1, 153));
            cases.Add(("pointer-signature", b => b[motion + 27] = (byte)'3', 153));
            int free = -1, sefd = -1;
            for (int at = 0; at < original.Length;)
            {
                uint small = BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(at));
                int length = small == 1 ? checked((int)BinaryPrimitives.ReadUInt64BigEndian(original.AsSpan(at + 8))) : checked((int)small);
                Assert.True(length >= 8 && length <= original.Length - at);
                string type = Encoding.ASCII.GetString(original, at + 4, 4);
                if (type == "free") free = at; if (type == "sefd") sefd = at;
                at += length;
            }
            Assert.True(free >= 0 && sefd >= 0);
            int shadow = free, sefdHeader = sefd;
            cases.Add(("shadow-sefd", b => "sefd"u8.CopyTo(b.AsSpan(shadow + 4)), 150));
            cases.Add(("shadow-mpvd", b => "mpvd"u8.CopyTo(b.AsSpan(shadow + 4)), 150));
            cases.Add(("implicit-sefd-size", b => b.AsSpan(sefdHeader, 4).Clear(), 150));
        }
        var rows = new List<object>();
        string negatives = Path.Combine(evidence, "mutations"); Directory.CreateDirectory(negatives);
        foreach (var item in cases)
        {
            byte[] changed = original.ToArray(); item.Change(changed);
            string path = Path.Combine(negatives, item.Name + (heic ? ".heic" : ".jpg")); File.WriteAllBytes(path, changed);
            using var reader = File.OpenRead(path);
            var result = NativeTargetValidator.Observe(reader, expected, CancellationToken.None);
            rows.Add(new { item.Name, byteMutated = true, result.InvocationResult, result.Observed.StructuralStatus, result.Observed.RuleId, expectedRule = item.Rule });
            File.WriteAllText(Path.Combine(negatives, "results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal(NativeResult.Ok, result.InvocationResult); Assert.False(result.CanPublish); Assert.Equal(item.Rule, result.Observed.RuleId);
        }
        using (var reader = File.OpenRead(final))
        {
            var result = NativeTargetValidator.Observe(reader, expected with { TargetVersion = expected.TargetVersion == 2 ? 3u : 2u }, CancellationToken.None);
            Assert.Equal(NativeResult.Ok, result.InvocationResult); Assert.Equal(152u, result.Observed.RuleId); Assert.False(result.CanPublish);
            rows.Add(new { Name = "wrong-variant-expectation", byteMutated = false, result.InvocationResult, result.Observed.StructuralStatus, result.Observed.RuleId, expectedRule = 152u });
            File.WriteAllText(Path.Combine(negatives, "results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task SyntheticVersionsAndUnknownCoverRemainExplicit(bool unknownCover, bool mpv3)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(SyntheticVersionsAndUnknownCoverRemainExplicit) + unknownCover + mpv3);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, unknownCover ? null : 0);
        var profile = OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.JpegMp4);
        string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, final, folder.Path, false, CancellationToken.None,
            samsungVariant: mpv3 ? SamsungTargetVariant.Mpv3 : SamsungTargetVariant.Mpv2);
        var expected = new TargetValidationExpectations(5, 0, 2, unknownCover ? null : 0) { UseV2 = true, TargetVersion = mpv3 ? 3u : 2u };
        using var reader = File.OpenRead(final); NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader, expected, CancellationToken.None), expected);
    }

    [Theory]
    [InlineData("share-lock")] [InlineData("stale-hash")] [InlineData("cancelled")] [InlineData("conflict")]
    public async Task SamsungFailureCannotPublishOrChangePreparedInputs(string mode)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(SamsungFailureCannotPublishOrChangePreparedInputs) + mode);
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        string final = Path.Combine(folder.Path, "target.jpg");
        if (mode == "stale-hash") File.AppendAllText(bundle.PrimaryImage.Path, "changed");
        if (mode == "conflict") File.WriteAllBytes(final, [1, 3, 7]);
        byte[] before = File.ReadAllBytes(bundle.PrimaryImage.Path);
        using var cancelled = new CancellationTokenSource(); if (mode == "cancelled") cancelled.Cancel();
        using var locked = mode == "share-lock" ? new FileStream(bundle.PrimaryImage.Path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        Task Write() => StructuredTargetWriterService.WriteAsync(bundle, OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, cancelled.Token);
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<IOException>(Write);
        locked?.Dispose();
        Assert.Equal(before, File.ReadAllBytes(bundle.PrimaryImage.Path)); Assert.Equal(bundle.MotionVideo!.Sha256, Hash(bundle.MotionVideo.Path));
        if (mode == "conflict") Assert.Equal(new byte[] { 1, 3, 7 }, File.ReadAllBytes(final)); else Assert.False(File.Exists(final));
        Assert.Equal(mode == "conflict" ? 3 : 2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task OrdinarySefWithoutAuxiliaryRemainsBoundAndPreserved()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(OrdinarySefWithoutAuxiliaryRemainsBoundAndPreserved));
        byte[] name = Encoding.ASCII.GetBytes("Color_Display_P3");
        byte[] tag = [0, 0, 0xc1, 0x0c, 16, 0, 0, 0, .. name, 1, 2, 3];
        byte[] index = new byte[32]; Encoding.ASCII.GetBytes("SEFH").CopyTo(index, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4), 107); BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(14), 0xcc1);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(16), (uint)tag.Length); BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(20), (uint)tag.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(24), 24); Encoding.ASCII.GetBytes("SEFT").CopyTo(index, 28);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, image: [.. P9TargetTestInputs.Image, .. tag, .. index]);
        var facts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
        Assert.Equal(SourceProtocol.NonLive, facts.Protocol); Assert.Empty(facts.AuxiliaryItems);
        Assert.Equal(2, facts.PreservationCarriers.Count);
        bundle = bundle with { PreservationCarriers = facts.PreservationCarriers };
        string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.JpegMp4), final, folder.Path, false, CancellationToken.None);
        byte[] output = File.ReadAllBytes(final);
        int table = output.Length - 8 - (int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(output.Length - 8));
        int entry = Enumerable.Range(0, (int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(table + 8))).Select(i => table + 12 + i * 12)
            .Single(i => BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(i + 2)) == 0xcc1);
        int at = table - (int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(entry + 4));
        Assert.Equal(tag, output.AsSpan(at, tag.Length).ToArray());
    }

    [Theory]
    [Trait("Category", "DerivedRealSample")]
    [InlineData(5, true)] [InlineData(6, true)] [InlineData(1, false)] [InlineData(9, false)]
    public async Task EquivalentMatrixCodesPreserveBytesButDifferentColorRejects(int matrix, bool accepted)
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, "三星.jpg"); Assert.True(File.Exists(source)); string before = Hash(source);
        var facts = await new SourceInspector().InspectAsync(source);
        byte[] raw = File.ReadAllBytes(source); byte[] video = raw.AsSpan((int)facts.MotionVideo!.ByteOffset, (int)facts.MotionVideo.ByteLength).ToArray();
        int colr = video.AsSpan().IndexOf("colr"u8); Assert.True(colr > 0); Assert.Equal(colr, video.AsSpan().LastIndexOf("colr"u8));
        Assert.Equal("nclx", Encoding.ASCII.GetString(video, colr + 4, 4));
        BinaryPrimitives.WriteUInt16BigEndian(video.AsSpan(colr + 12), (ushort)matrix);
        using var folder = new P9TargetTestInputs.Folder(nameof(EquivalentMatrixCodesPreserveBytesButDifferentColorRejects) + matrix);
        var bundle = P9TargetTestInputs.Bundle(folder.Path); File.WriteAllBytes(bundle.MotionVideo!.Path, video);
        var artifact = bundle.MotionVideo with { VideoCodec = VideoCodec.Hevc, ByteLength = video.Length, Sha256 = Hash(bundle.MotionVideo.Path), FileIdentity = WindowsFileIdentity.Capture(bundle.MotionVideo.Path) };
        bundle = bundle with { MotionVideo = artifact, Manifest = bundle.Manifest.Select(row => row.Role == "MotionVideo" ? row with { ByteLength = artifact.ByteLength, Sha256 = artifact.Sha256! } : row).ToArray() };
        string final = Path.Combine(folder.Path, "target.jpg");
        Task Write() => StructuredTargetWriterService.WriteAsync(bundle, OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.JpegMp4), final, folder.Path, false, CancellationToken.None);
        if (accepted) await Write();
        else { await Assert.ThrowsAsync<IOException>(Write); Assert.False(File.Exists(final)); Assert.Equal(2, Directory.GetFiles(folder.Path).Length); }
        Assert.Equal(video, File.ReadAllBytes(artifact.Path)); Assert.Equal(before, Hash(source));
    }

    [Theory]
    [Trait("Category", "DerivedRealSample")]
    [InlineData(false)] [InlineData(true)]
    public async Task RealHeicPrimaryWithPreparedH264VideoUsesSameSamsungLifecycle(bool mpv3)
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, "三星.heic"); Assert.True(File.Exists(source)); string before = Hash(source);
        using var folder = new P9TargetTestInputs.Folder(nameof(RealHeicPrimaryWithPreparedH264VideoUsesSameSamsungLifecycle) + mpv3);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.HeicMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace, profile.MediaRequirement, PreservationPolicy.Strict);
        string videoPath = Path.Combine(folder.Path, "prepared-h264.mp4"); File.Copy(Path.Combine(P9TargetTestInputs.Root, "neutral-audio.mp4"), videoPath);
        var video = bundle.MotionVideo! with { Path = videoPath, ByteLength = new FileInfo(videoPath).Length,
            Sha256 = Hash(videoPath), VideoCodec = VideoCodec.H264, FileIdentity = WindowsFileIdentity.Capture(videoPath) };
        bundle = bundle with
        {
            MotionVideo = video,
            Manifest = bundle.Manifest.Select(row => row.Role == "MotionVideo" ? row with
                { Path = video.Path, ByteLength = video.ByteLength, Sha256 = video.Sha256!, VideoCodec = VideoCodec.H264,
                    StableIdentity = "sha256:" + video.Sha256!.ToLowerInvariant(), SourceOffset = 0,
                    SourceLength = video.ByteLength, SourceSha256 = video.Sha256! } : row).ToArray(),
            Semantics = bundle.Semantics with
            {
                MotionVideo = new NeutralArtifactSemantics { Role = NeutralArtifactRole.MotionVideo, ContentIdentity = "sha256:" + video.Sha256!.ToLowerInvariant(),
                    Semantic = "MotionVideo", VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, EvidenceState = NeutralEvidenceState.Verified },
                Timing = new NeutralTimingSemantics { EvidenceState = NeutralEvidenceState.Unknown },
                Orientation = bundle.Semantics.Orientation with { MotionVideo = new NeutralOrientationTransform
                    { ClockwiseRotationDegrees = 0, Reflection = NeutralReflection.None, RotationEvidence = NeutralEvidenceState.Verified, ReflectionEvidence = NeutralEvidenceState.Verified } }
            }
        };
        string root = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R5_OUTPUTS") ?? throw new InvalidOperationException("R5 outputs required");
        string evidence = Path.Combine(root, "derived-heic-h264-" + (mpv3 ? "mpv3" : "mpv2")); Directory.CreateDirectory(evidence);
        File.Copy(bundle.PrimaryImage.Path, Path.Combine(evidence, "neutral.heic"), true); File.Copy(videoPath, Path.Combine(evidence, "neutral.mp4"), true);
        File.WriteAllText(Path.Combine(evidence, "classification.json"), JsonSerializer.Serialize(new
            { classification = "Derived: real Samsung HEIC primary after actual Neutral; independent qualified H264/AAC prepared video; not original camera pairing", source, originalSourceSha256 = before,
                preparedVideoSha256 = video.Sha256, unknownCoverExplicit = true }));
        var receipt = await StructuredTargetWriterService.WriteAsync(bundle, profile, Path.Combine(evidence, "target.heic"), workspace.RootDirectory, true, CancellationToken.None,
            samsungVariant: mpv3 ? SamsungTargetVariant.Mpv3 : SamsungTargetVariant.Mpv2);
        Assert.True(File.Exists(receipt.FinalPath)); Assert.Equal(before, Hash(source)); Assert.Equal(video.Sha256, Hash(videoPath));
    }
}
