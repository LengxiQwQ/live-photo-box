using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Protocols.Cleaning;
using System.Text.Json;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R4StructuredHuaweiTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("华为-Mate80.jpg", false, false, false)]
    [InlineData("华为Mate80.heic", true, false, false)]
    [InlineData("华为Mate80.heic", true, false, true)]
    [InlineData("荣耀.jpg", false, true, false)]
    [InlineData("红米老款-GV1.JPG", false, false, false)]
    public async Task CachedOriginalUsesActualNeutralAndExplicitHuaweiHonorTarget(string filename, bool heic, bool honor, bool explicitHevc)
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, filename);
        Assert.True(File.Exists(source), "Required RealSample is missing: " + source);
        string before = Hash(source);
        var original = RealSampleCorpusManifest.GetRequiredSample(filename);
        Assert.Equal(original.ByteSize, new FileInfo(source).Length);
        Assert.Equal(original.Sha256.ToUpperInvariant(), before);
        string root = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R4_OUTPUTS")
            ?? throw new InvalidOperationException("R4 evidence directory required");
        string evidence = Path.Combine(root, "real-" + filename.Replace('.', '_') + (explicitHevc ? "-explicit-hevc" : ""));
        Directory.CreateDirectory(evidence);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle? bundle = null;
        try
        {
            var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto,
                explicitHevc ? OutputMediaFormat.HeicMp4Hevc : heic ? OutputMediaFormat.HeicMp4 : OutputMediaFormat.JpegMp4);
            bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null,
                workspace, profile.MediaRequirement, PreservationPolicy.Strict);
            Assert.True(bundle.Semantics.Timing.CoverTimestampUs.HasValue);
            if (explicitHevc) Assert.Equal(VideoCodec.Hevc, bundle.MotionVideo!.VideoCodec);
            if (bundle.PrimaryImage.CompoundPrimary is { } compound)
            {
                await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(bundle.PrimaryImage.Path));
                Assert.Equal(SourceProtocol.NonLive, compound.Inspect(bundle.PrimaryImage.Path, CancellationToken.None).Facts.Protocol);
            }
            else Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path)).Protocol);
            string final = Path.Combine(evidence, heic ? "target.heic" : "target.jpg");
            File.Copy(bundle.PrimaryImage.Path, Path.Combine(evidence, heic ? "neutral.heic" : "neutral.jpg"), true);
            File.Copy(bundle.MotionVideo!.Path, Path.Combine(evidence, "neutral.mp4"), true);
            File.WriteAllText(Path.Combine(evidence, "neutral.json"), JsonSerializer.Serialize(new
                { bundle.Manifest, bundle.Semantics, bundle.AuxiliaryMedia, bundle.PreservationCarriers, bundle.PreparationExecution },
                new JsonSerializerOptions { WriteIndented = true }));
            await StructuredTargetWriterService.WriteAsync(bundle, profile, final, workspace.RootDirectory,
                true, CancellationToken.None, honor ? HuaweiTargetVariant.HonorV2 : HuaweiTargetVariant.HuaweiV6);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new
                { error = ex.ToString(), manifest = bundle?.Manifest, semantics = bundle?.Semantics },
                new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally { Assert.Equal(before, Hash(source)); }
    }

    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

    [Theory]
    [InlineData("valid")] [InlineData("duplicate-live")] [InlineData("missing-live")]
    [InlineData("duplicate-keys")] [InlineData("wrong-type")] [InlineData("locale")] [InlineData("overflow")]
    public async Task TwoMetadataOwnersAndMixedSideMdatsCleanByActualAuthority(string mode)
    {
        string inputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R4_INPUTS")
            ?? Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root, "..", "..", "R4", "inputs"));
        string source = Path.Combine(inputs, "mdta-" + mode + ".jpg");
        Assert.True(File.Exists(source), "Required independently constructed input is missing: " + source);
        string before = Hash(source);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4);
        if (mode != "valid")
        {
            await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectWithPlanAsync(source));
            Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
        }
        else
        {
            var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
                profile.MediaRequirement, PreservationPolicy.Strict);
            Assert.Equal(100000, bundle.Semantics.Timing.CoverTimestampUs);
            byte[] video = File.ReadAllBytes(bundle.MotionVideo!.Path);
            Assert.Contains("R4-ordinary-direct-stco-co64", Encoding.ASCII.GetString(video));
            Assert.Contains("R4-ordinary-nested-co64-stco", Encoding.ASCII.GetString(video));
            Assert.Contains("R4-ordinary-trailing", Encoding.ASCII.GetString(video));
            Assert.DoesNotContain("com.openharmony.covertime", Encoding.ASCII.GetString(video));
            string evidence = Path.Combine(Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R4_OUTPUTS")
                ?? throw new InvalidOperationException("R4 outputs required"), "mixed-side-mdats");
            Directory.CreateDirectory(evidence);
            File.Copy(bundle.MotionVideo.Path, Path.Combine(evidence, "cleaned.mp4"));
            await StructuredTargetWriterService.WriteAsync(bundle, profile, Path.Combine(evidence, "target.jpg"),
                workspace.RootDirectory, false, CancellationToken.None);
        }
        Assert.Equal(before, Hash(source));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SourceLiveMarkerAllowsAbsentLegacyFrameButRejectsMalformedOwnedFrame(bool malformed)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(SourceLiveMarkerAllowsAbsentLegacyFrameButRejectsMalformedOwnedFrame) + malformed);
        byte[] movie = File.ReadAllBytes(Path.Combine(P9TargetTestInputs.Root, "neutral-audio.mp4"));
        byte[] footer = Enumerable.Repeat((byte)' ', 60).ToArray();
        Encoding.ASCII.GetBytes("700:1300").CopyTo(footer, 20);
        Encoding.ASCII.GetBytes("LIVE_" + (movie.Length + 20)).CopyTo(footer, 40);
        if (malformed) Encoding.ASCII.GetBytes("v6_fNaN").CopyTo(footer, 0);
        string path = Path.Combine(folder.Path, "source.jpg");
        File.WriteAllBytes(path, [.. P9TargetTestInputs.Image, .. movie, .. footer]);
        if (malformed) await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(path));
        else
        {
            var facts = await new SourceInspector().InspectAsync(path);
            Assert.Equal(SourceProtocol.HuaweiMovingPhoto, facts.Protocol);
            Assert.Equal(-1, facts.Timing.CoverTimestampUs);
            Assert.Equal(movie.Length, facts.MotionVideo!.ByteLength);
        }
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("copy")] [InlineData("replace")] [InlineData("changed")] [InlineData("released")] [InlineData("cancelled")]
    public async Task CompoundLineageRejectsForeignChangedReleasedOrCancelledObjects(string mode)
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, "荣耀.jpg");
        Assert.True(File.Exists(source)); string original = Hash(source);
        using var foreign = new P9TargetTestInputs.Folder(nameof(CompoundLineageRejectsForeignChangedReleasedOrCancelledObjects) + mode);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            profile.MediaRequirement, PreservationPolicy.Strict);
        var proof = Assert.IsType<NativeCompoundPrimary>(bundle.PrimaryImage.CompoundPrimary);
        Assert.Equal(SourceProtocol.NonLive, proof.Inspect(bundle.PrimaryImage.Path, CancellationToken.None).Facts.Protocol);
        string path = bundle.PrimaryImage.Path;
        string saved = Path.Combine(foreign.Path, "original.jpg");
        if (mode == "copy") { path = Path.Combine(foreign.Path, "foreign.jpg"); File.Copy(bundle.PrimaryImage.Path, path); }
        if (mode == "replace")
        {
            File.Move(path, saved); File.Copy(saved, path);
        }
        if (mode == "changed")
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = stream.Length - 1; int value = stream.ReadByte(); stream.Position--;
            stream.WriteByte((byte)(value ^ 1)); stream.Flush(true);
        }
        if (mode == "released")
        { proof.Dispose(); Assert.Throws<ObjectDisposedException>(() => proof.Inspect(path, CancellationToken.None)); }
        else if (mode == "cancelled")
        { using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); Assert.ThrowsAny<OperationCanceledException>(() => proof.Inspect(path, cancelled.Token)); }
        else
        {
            try { Assert.Throws<IOException>(() => proof.Inspect(path, CancellationToken.None)); }
            finally
            {
                // The replacement belongs to this test; restore the exact registered
                // object so normal workspace cleanup can retain its identity gate.
                if (mode == "replace") { File.Delete(path); File.Move(saved, path); }
            }
        }
        Assert.Equal(original, Hash(source));
    }

    [Theory]
    [InlineData("share-lock")] [InlineData("stale-hash")] [InlineData("cancelled")]
    public async Task HuaweiFailureCannotPublishOrChangePreparedInputs(string mode)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(HuaweiFailureCannotPublishOrChangePreparedInputs) + mode);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, 100000, audio: true);
        if (mode == "stale-hash") File.AppendAllText(bundle.PrimaryImage.Path, "changed");
        byte[] before = File.ReadAllBytes(bundle.PrimaryImage.Path);
        string final = Path.Combine(folder.Path, "target.jpg");
        using var cancelled = new CancellationTokenSource(); if (mode == "cancelled") cancelled.Cancel();
        using var locked = mode == "share-lock" ? new FileStream(bundle.PrimaryImage.Path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        Task Write() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, cancelled.Token);
        if (mode == "cancelled") await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<IOException>(Write);
        locked?.Dispose();
        Assert.False(File.Exists(final)); Assert.Equal(before, File.ReadAllBytes(bundle.PrimaryImage.Path));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeCompoundRevocationBlocksAlreadyStagedReadAndGainMap()
    {
        string source = Path.Combine(W2EvidencePaths.CacheRoot, "荣耀.jpg");
        Assert.True(File.Exists(source)); string before = Hash(source);
        using var workspace = new MediaWorkspace();
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4);
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            profile.MediaRequirement, PreservationPolicy.Strict);
        CheckRevokedNativeConsumers(bundle, workspace.RootDirectory);
        Assert.Equal(before, Hash(source));
    }

    private static unsafe void CheckRevokedNativeConsumers(NeutralMediaBundle bundle, string stage)
    {
        static void Text(string value, byte* output, int size)
        { var bytes = Encoding.UTF8.GetBytes(value); Assert.True(bytes.Length < size); new Span<byte>(output, size).Clear(); bytes.CopyTo(new Span<byte>(output, size)); }
        static NativeTargetWriterArtifactV1 Artifact(MediaArtifact artifact, uint role)
        {
            var identity = artifact.FileIdentity!;
            var result = new NativeTargetWriterArtifactV1 { StructSize = (uint)sizeof(NativeTargetWriterArtifactV1), Version = 1,
                Role = role, Container = role == 0 ? (uint)artifact.ImageContainer : (uint)artifact.VideoContainer,
                VideoCodec = role == 0 ? 0 : (uint)artifact.VideoCodec,
                ExpectedIdentity = new NativeCleanService.NativeFileIdentity { VolumeSerial = identity.VolumeSerialNumber,
                    FileIndex = identity.FileIndex, FileSize = (ulong)artifact.ByteLength, LinkCount = identity.LinkCount } };
            Convert.FromHexString(artifact.Sha256!).CopyTo(new Span<byte>(result.Sha256, 32)); Text(artifact.Path, result.PathUtf8, 1024); return result;
        }
        using var context = NativeContext.Create(CancellationToken.None); using var operation = context.AcquireOperationLease();
        var proof = Assert.IsType<NativeCompoundPrimary>(bundle.PrimaryImage.CompoundPrimary);
        ulong token = unchecked((ulong)proof.DangerousGetHandle());
        NativeTargetWriterArtifactV1* artifacts = stackalloc NativeTargetWriterArtifactV1[2];
        artifacts[0] = Artifact(bundle.PrimaryImage, 0); artifacts[1] = Artifact(bundle.MotionVideo!, 1);
        var row = Assert.Single(bundle.Manifest.Where(r => r.Role == "GainMap"));
        var auxiliary = new NativeTargetAuxiliaryV2 { StructSize = (uint)sizeof(NativeTargetAuxiliaryV2), Version = 2,
            Representation = 1, EmbeddedOffset = (ulong)row.OwnerByteOffset!.Value, ByteLength = (ulong)row.ByteLength };
        Text(row.StableIdentity, auxiliary.StableIdentity, 128); Text(row.OwnerIdentity, auxiliary.OwnerIdentity, 128);
        Text(row.Semantic, auxiliary.Semantic, 32); Text(row.Relationship, auxiliary.Relationship, 96);
        Convert.FromHexString(row.Sha256).CopyTo(new Span<byte>(auxiliary.Sha256, 32));
        var request = new NativeTargetWriterRequestV3 { StructSize = (uint)sizeof(NativeTargetWriterRequestV3), Version = 3,
            CompoundPrimaryToken = token, Base = new NativeTargetWriterRequestV2 { StructSize = (uint)sizeof(NativeTargetWriterRequestV2), Version = 2,
                AuxiliaryCount = 1, Auxiliaries = (nint)(&auxiliary), Base = new NativeTargetWriterRequestV1 {
                    StructSize = (uint)sizeof(NativeTargetWriterRequestV1), Version = 1, TargetProfile = 6, TargetVersion = 2,
                    ArtifactCount = 2, Artifacts = (nint)artifacts, CoverTimestampPresent = 1,
                    CoverTimestampUs = bundle.Semantics.Timing.CoverTimestampUs!.Value } } };
        Text(stage, request.Base.Base.StagingDirectoryUtf8, 1024); Text("primary:0", request.Base.PrimaryIdentity, 128);
        Assert.Equal(NativeResult.Ok, NativeMethods.TargetWriterPrepareV3(operation.Handle, &request, out nint plan));
        try
        {
            Assert.Equal(NativeResult.Ok, NativeMethods.TargetWriterStage(operation.Handle, plan));
            Assert.Equal(NativeResult.PlanReplayed, NativeMethods.TargetWriterStage(operation.Handle, plan));
            var output = new NativeTargetWriterOutputV1 { StructSize = (uint)sizeof(NativeTargetWriterOutputV1), Version = 1 };
            var status = new NativeTargetWriterStatusV1 { StructSize = (uint)sizeof(NativeTargetWriterStatusV1), Version = 1 };
            Assert.Equal(NativeResult.Ok, NativeMethods.TargetWriterGetOutputs(operation.Handle, plan, &output, 1, out uint required, ref status));
            Assert.Equal(1u, required);
            Assert.Equal(NativeResult.Ok, NativeMethods.CompoundRelease(token));
            byte value = 0;
            Assert.Equal(NativeResult.AuthorityViolation, NativeMethods.TargetWriterReadStage(operation.Handle, plan, output.StageId, 0, &value, 1, out uint read));
            Assert.Equal(0u, read);
            var metadata = new NativeGainMapMetadataV1 { StructSize = (uint)sizeof(NativeGainMapMetadataV1), ApiVersion = 1 };
            Assert.Equal(NativeResult.AuthorityViolation, NativeMethods.CompoundGainMap(operation.Handle, token, bundle.PrimaryImage.Path, null, ref metadata));
        }
        finally { Assert.Equal(NativeResult.Ok, NativeMethods.TargetWriterRelease(operation.Handle, plan)); }
    }

    [Theory]
    [InlineData(0L, false, false)] [InlineData(100000L, true, false)] [InlineData(599999L, false, false)]
    [InlineData(100000L, true, true)]
    public async Task JpegAudioMoviePublishesOnlyAfterFreshHuaweiRules(long cover, bool ordinaryXmp, bool honor)
    {
        string name = nameof(JpegAudioMoviePublishesOnlyAfterFreshHuaweiRules) + cover + ordinaryXmp + honor;
        using var folder = new P9TargetTestInputs.Folder(name);
        const string ordinary = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:t=\"urn:r4:ordinary\" t:Keep=\"preserve\"/></rdf:RDF></x:xmpmeta>";
        var bundle = P9TargetTestInputs.Bundle(folder.Path, cover,
            ordinaryXmp ? P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image, ordinary) : null, audio: true);
        string final = Path.Combine(folder.Path, "target.jpg");
        var receipt = await StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None, honor ? HuaweiTargetVariant.HonorV2 : HuaweiTargetVariant.HuaweiV6);
        using var stream = File.OpenRead(final);
        var expected = new TargetValidationExpectations(6, 0, 2, cover) { UseV2 = true, TargetVersion = honor ? 2u : 1u };
        var report = NativeTargetValidator.Observe(stream, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(report, expected);
        Assert.Equal(1u, report.Observed.StructuralStatus); Assert.Equal(1u, report.Observed.GenericMediaStatus);
        Assert.Equal(0u, report.Observed.PreservationStatus); Assert.Equal(0u, report.Observed.DeviceStatus);
        byte[] bytes = File.ReadAllBytes(final), primary = File.ReadAllBytes(bundle.PrimaryImage.Path);
        Assert.Equal(primary, bytes.AsSpan(0, primary.Length).ToArray());
        Assert.Equal((ulong)primary.Length, report.Observed.VideoOffset);
        Assert.Equal((ulong)bytes.Length, report.Observed.VideoOffset + report.Observed.VideoLength + 60);
        Assert.StartsWith(honor ? "v2_f" : "v6_f", Encoding.ASCII.GetString(bytes.AsSpan(bytes.Length - 60, 20)));
        Assert.Equal(report.Observed.VideoLength + 20,
            ulong.Parse(Encoding.ASCII.GetString(bytes.AsSpan(bytes.Length - 15, 15)).Trim()));
        Assert.Equal(bundle.MotionVideo!.Sha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle.MotionVideo.Path))));
        Assert.Equal(receipt.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        string evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R4_OUTPUTS")
            ?? throw new InvalidOperationException("LIVEPHOTOBOX_P9_R4_OUTPUTS is required for retained R4 production-path evidence.");
        Directory.CreateDirectory(evidence);
        File.Copy(final, Path.Combine(evidence, name + ".jpg"), true);
        File.Copy(bundle.PrimaryImage.Path, Path.Combine(evidence, name + ".primary.jpg"), true);
        File.Copy(bundle.MotionVideo.Path, Path.Combine(evidence, name + ".input.mp4"), true);
    }

    [Fact]
    public async Task UnknownCoverCannotInventZeroOrPublish()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(UnknownCoverCannotInventZeroOrPublish));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, null, audio: true);
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None));
        Assert.False(File.Exists(final)); Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task HuaweiWithoutAacCannotPublishOrModifyInputs()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(HuaweiWithoutAacCannotPublishOrModifyInputs));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, 0, audio: false);
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4),
            final, folder.Path, false, CancellationToken.None));
        Assert.False(File.Exists(final)); Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
        Assert.Equal(bundle.PrimaryImage.Sha256, Hash(bundle.PrimaryImage.Path));
        Assert.Equal(bundle.MotionVideo!.Sha256, Hash(bundle.MotionVideo.Path));
    }
}
