using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

// Every input is a frozen cache copy. These tests deliberately fail when the
// production Neutral pipeline cannot represent a required asset or timestamp.
// They never remove a carrier or fill unknown capture time from current cover.
public sealed class P9R3RealFamilyTests
{
    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("红米老款-GV1.JPG", TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMp4, false)]
    [InlineData("红米老款-GV1.JPG", TargetProtocol.GoogleMicroVideoV1, OutputMediaFormat.JpegMov, false)]
    [InlineData("红米老款-GV1.JPG", TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4, false)]
    [InlineData("小米.jpg", TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4, true)]
    [InlineData("oppo.jpg", TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4, false)]
    [InlineData("一加.jpg", TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4, true)]
    [InlineData("一加-改了封面照片.jpg", TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4, false)]
    [InlineData("一加-改了封面照片.jpg", TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4, false)]
    public async Task CachedRealSourceThroughActualNeutralPipelineAndStructuredFamily(
        string filename, TargetProtocol target, OutputMediaFormat format, bool requireGainMap)
    {
        string source = GetExistingSamplePath(filename);
        string before = Hash(source);
        string caseId = Safe(filename) + "-" + target + "-" + format;
        string evidence = CaseDirectory(caseId);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle? bundle = null;
        try
        {
            var profile = OutputProfile.Create(target, format);
            bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null,
                workspace, profile.MediaRequirement, PreservationPolicy.BestEffort);
            SavePrepared(bundle, evidence, "before");
            if (filename == "小米.jpg")
            {
                Assert.Null(bundle.Semantics.Orientation.PrimaryImage.ClockwiseRotationDegrees);
                Assert.Equal(NeutralReflection.Unknown, bundle.Semantics.Orientation.PrimaryImage.Reflection);
                Assert.Equal(NeutralEvidenceState.Unknown, bundle.Semantics.Orientation.PrimaryImage.RotationEvidence);
                Assert.Equal(NeutralEvidenceState.Unknown, bundle.Semantics.Orientation.PrimaryImage.ReflectionEvidence);
                Assert.Null(bundle.PreparationExecution?.Image);
            }
            Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path)).Protocol);
            if (requireGainMap) Assert.NotNull(bundle.Semantics.GainMap);
            if (filename.Contains("改了封面", StringComparison.Ordinal))
                Assert.Contains(bundle.Semantics.AuxiliaryMedia, row => row.Semantic == "Original");
            if (target == TargetProtocol.OppoLivePhoto)
                Assert.True(bundle.Semantics.Timing.PrimaryTimestampUs.HasValue,
                    "Actual Neutral preparation did not establish original capture time; do not invent it from cover.");
            if (target == TargetProtocol.GoogleMicroVideoV1)
                Assert.True(bundle.Semantics.Timing.CoverTimestampUs.HasValue,
                    "Actual Neutral preparation did not establish a V1 writable known cover.");
            await PublishAndCheck(bundle, profile, evidence);
            SavePrepared(bundle, evidence, "after");
        }
        catch (Exception ex)
        {
            SaveFailure(evidence, ex, bundle);
            throw;
        }
        finally
        {
            string after = Hash(source);
            WriteJson(Path.Combine(evidence, "source-before-after.json"), new
            { source, before, after, exactBytesUnchanged = before == after, classification = "RealSample cached original; production Neutral preparation" });
            Assert.Equal(before, after);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task RealEmbeddedGainMapAndMaterializedMirrorAreOneSemanticAsset()
    {
        string source = GetExistingSamplePath("小米.jpg"), before = Hash(source);
        string evidence = CaseDirectory(nameof(RealEmbeddedGainMapAndMaterializedMirrorAreOneSemanticAsset));
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle? bundle = null;
        try
        {
            var profile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4);
            bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
                profile.MediaRequirement, PreservationPolicy.BestEffort);
            SavePrepared(bundle, evidence, "initial-neutral");
            NeutralAuxiliarySemantics gain = Assert.IsType<NeutralAuxiliarySemantics>(bundle.Semantics.GainMap);
            Assert.True(gain.Representation is NeutralAuxiliaryRepresentation.Embedded or NeutralAuxiliaryRepresentation.Both,
                "Required real source did not produce an embedded GainMap; cannot fabricate Both ownership.");
            var row = Assert.Single(bundle.Manifest, item => item.Semantic == "GainMap");
            Assert.True(row.OwnerByteOffset.HasValue);
            var facts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
            // Legacy JPEG facts do not populate the HEIF-specific GraphComplete
            // flag. Do not manufacture it: Native will independently reconstruct
            // JPEG Directory/MPF ownership before accepting the Both request.
            var actual = Assert.Single(facts.AuxiliaryItems, item => item.IsPresent &&
                (bundle.PrimaryImage.ImageContainer == ImageContainer.Jpeg || item.GraphComplete) &&
                item.Semantic == "GainMap" && item.OwnerIdentity == row.OwnerIdentity &&
                item.Relationship == row.Relationship && item.StableIdentity == row.StableIdentity &&
                item.ByteOffset == row.OwnerByteOffset && item.ByteLength == row.ByteLength &&
                string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase));
            string mirror = Path.Combine(evidence, "gainmap-materialized.jpg");
            CopyRange(bundle.PrimaryImage.Path, actual.ByteOffset, actual.ByteLength, mirror);
            Assert.Equal(row.Sha256.ToUpperInvariant(), Hash(mirror));
            var artifact = new MediaArtifact
            {
                Path = mirror, Kind = MediaArtifactKind.GainMap, ImageContainer = row.ImageContainer,
                ImageCodec = row.ImageCodec, ByteLength = row.ByteLength, Sha256 = row.Sha256,
                FileIdentity = WindowsFileIdentity.Capture(mirror)
            };
            var descriptors = bundle.AuxiliaryMedia.Where(item => item.Semantic != "GainMap").ToList();
            descriptors.Add(new AuxiliaryMediaDescriptor
            {
                ArtifactRole = MediaArtifactKind.GainMap, StableIdentity = gain.StableIdentity,
                Semantic = gain.Semantic, OwnerIdentity = gain.OwnerIdentity, Relationship = gain.Relationship,
                SourceIndex = actual.SourceIndex, SourceOffset = actual.ByteOffset, SourceLength = actual.ByteLength,
                SourceSha256 = actual.Sha256, ImageContainer = actual.Container, Codec = actual.Codec,
                Representation = AuxiliaryRepresentation.Materialized, Ownership = actual.Ownership,
                GraphComplete = actual.GraphComplete, ItemType = actual.ItemType,
                Dependencies = actual.Dependencies, MaterializedArtifact = artifact,
                PreservationOutcome = row.PreservationOutcome
            });
            bundle = bundle with
            {
                GainMap = artifact, GainMapRepresentation = GainMapRepresentation.Both,
                AuxiliaryMedia = descriptors,
                Manifest = bundle.Manifest.Select(item => item.Semantic == "GainMap" ? item with
                {
                    Path = mirror, SemanticRepresentation = NeutralAuxiliaryRepresentation.Both,
                    GainMapRepresentation = GainMapRepresentation.Both
                } : item).ToArray(),
                Semantics = bundle.Semantics with { GainMap = gain with { Representation = NeutralAuxiliaryRepresentation.Both } }
            };
            SavePrepared(bundle, evidence, "before");
            var report = await PublishAndCheck(bundle, profile, evidence);
            Assert.Equal(1u, report.Observed.ObservedAuxiliaryCount);
            Assert.Equal(1u, report.ObservedV2!.Value.AuxiliaryCount);
            SavePrepared(bundle, evidence, "after");
            Assert.Equal(row.Sha256.ToUpperInvariant(), Hash(mirror));
        }
        catch (Exception ex) { SaveFailure(evidence, ex, bundle); throw; }
        finally
        {
            string after = Hash(source); WriteJson(Path.Combine(evidence, "source-before-after.json"), new { source, before, after });
            Assert.Equal(before, after);
        }
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task ApprovedProductionBackendPreparesRealHeicMovAndStructuredV2Publishes()
    {
        string evidence = CaseDirectory(nameof(ApprovedProductionBackendPreparesRealHeicMovAndStructuredV2Publishes));
        string image = Path.Combine(P9TargetTestInputs.Root, "neutral.jpg");
        string video = Path.Combine(P9TargetTestInputs.Root, "neutral.mov");
        Assert.True(File.Exists(image), image); Assert.True(File.Exists(video), video);
        string imageBefore = Hash(image), videoBefore = Hash(video);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle? bundle = null;
        try
        {
            var profile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.HeicMov);
            bundle = await new NeutralMediaService().CreateNeutralBundleAsync(image, video, workspace,
                profile.MediaRequirement, PreservationPolicy.BestEffort);
            Assert.Equal(ImageContainer.Heic, bundle.PrimaryImage.ImageContainer);
            Assert.Equal(VideoContainer.Mov, bundle.MotionVideo!.VideoContainer);
            Assert.NotNull(bundle.PreparationExecution?.Image);
            Assert.Equal(SourceProtocol.NonLive, (await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path)).Protocol);
            SavePrepared(bundle, evidence, "before");
            var report = await PublishAndCheck(bundle, profile, evidence);
            Assert.Equal(3u, report.Observed.ObservedMediaFormat);
            // Derived negative: retain the actual produced HEVC image and all
            // extents, changing only the same-width major brand to AVIF.
            byte[] wrongBrand = File.ReadAllBytes(Path.Combine(evidence, "target.heic"));
            Assert.Equal("ftyp"u8.ToArray(), wrongBrand.AsSpan(4, 4).ToArray());
            "avif"u8.CopyTo(wrongBrand.AsSpan(8, 4));
            string wrongPath = Path.Combine(evidence, "derived-avif-brand.heic");
            File.WriteAllBytes(wrongPath, wrongBrand);
            using (var wrongReader = File.OpenRead(wrongPath))
            {
                var expected = new TargetValidationExpectations(2, 3, (uint)bundle.MotionVideo.VideoCodec,
                    bundle.Semantics.Timing.CoverTimestampUs) { UseV2 = true };
                var rejected = NativeTargetValidator.Observe(wrongReader, expected, CancellationToken.None);
                Assert.False(rejected.CanPublish);
                Assert.Throws<TargetValidationException>(() => NativeTargetValidator.RequirePass(rejected, expected));
                WriteJson(Path.Combine(evidence, "derived-avif-brand-rejection.json"), new
                { classification = "Synthetic same-width brand mutation of actual prepared HEVC output; not an AVIF sample", rejected });
            }
            SavePrepared(bundle, evidence, "after");
        }
        catch (Exception ex) { SaveFailure(evidence, ex, bundle); throw; }
        finally
        {
            WriteJson(Path.Combine(evidence, "source-before-after.json"), new
            { image, imageBefore, imageAfter = Hash(image), video, videoBefore, videoAfter = Hash(video) });
            Assert.Equal(imageBefore, Hash(image)); Assert.Equal(videoBefore, Hash(video));
        }
    }

    private static async Task<TargetValidationReport> PublishAndCheck(NeutralMediaBundle bundle, OutputProfile profile, string evidence)
    {
        uint target = profile.TargetProtocol switch
        {
            TargetProtocol.GoogleMicroVideoV1 => 1u, TargetProtocol.GoogleMotionPhotoV2 => 2u,
            TargetProtocol.OppoLivePhoto => 3u, _ => throw new InvalidDataException()
        };
        var aux = bundle.Semantics.AuxiliaryMedia.ToList();
        if (bundle.Semantics.GainMap is { } gain) aux.Add(gain);
        // Only protocol-defined target assets have a v2 expectation enum.
        // Ordinary semantic assets remain in the original bundle submitted to
        // production; verify their observed output bytes separately below.
        var expectations = aux.Select(item =>
        {
            var row = Assert.Single(bundle.Manifest, row => row.StableIdentity == item.StableIdentity && row.Semantic == item.Semantic);
            uint code = item.Semantic switch { "GainMap" => 1u, "Original" => 2u,
                "MiCameraSubimage" => 3u, "MiCameraLensWatermark" => 4u, _ => throw new InvalidDataException() };
            return new TargetAuxiliaryExpectation(code,
                (uint)row.ImageContainer, checked((ulong)row.ByteLength), row.Sha256);
        }).ToArray();
        string final = Path.Combine(evidence, profile.Format == OutputMediaFormat.HeicMov ? "target.heic" : "target.jpg");
        var before = new[] { bundle.PrimaryImage, bundle.MotionVideo! }.Select(a => new { a.Path, hash = Hash(a.Path) }).ToArray();
        string staging = Path.Combine(evidence, "stage"); Directory.CreateDirectory(staging);
        bool fresh = false;
        using var hook = new P9TargetPublicationTests.ReaderHook((stage, reader) =>
        { Assert.True(reader.Length > 0); fresh = true; });
        var receipt = await StructuredTargetWriterService.WriteAsync(bundle, profile, final, staging, true, CancellationToken.None);
        Assert.True(fresh);
        using var output = File.OpenRead(final);
        var expected = new TargetValidationExpectations(target, (uint)profile.Format, (uint)bundle.MotionVideo!.VideoCodec,
            bundle.Semantics.Timing.CoverTimestampUs)
        {
            UseV2 = true, PrimaryTimestampUs = target == 3 ? bundle.Semantics.Timing.PrimaryTimestampUs : null,
            Auxiliaries = expectations
        };
        var report = NativeTargetValidator.Observe(output, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(report, expected);
        Assert.Equal(receipt.Sha256, Hash(final)); Assert.Equal(report.Sha256, Hash(final));
        Assert.Equal((uint)expectations.Length, report.Observed.ObservedAuxiliaryCount);
        Assert.Equal(Hash(bundle.MotionVideo.Path), HashRange(final, checked((long)report.Observed.VideoOffset), checked((long)report.Observed.VideoLength)));
        var ordinary = aux.Where(item => item.Semantic is not ("GainMap" or "Original")).ToArray();
        if (ordinary.Length != 0)
        {
            var actualFacts = await new SourceInspector().InspectAsync(final);
            foreach (var semantic in ordinary)
            {
                var wanted = Assert.Single(bundle.Manifest, row => row.StableIdentity == semantic.StableIdentity && row.Semantic == semantic.Semantic);
                var observed = Assert.Single(actualFacts.AuxiliaryItems, item => item.IsPresent &&
                    item.Semantic == semantic.Semantic && item.Relationship == semantic.Relationship &&
                    item.ByteLength == wanted.ByteLength && string.Equals(item.Sha256, wanted.Sha256, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(wanted.Sha256.ToUpperInvariant(), HashRange(final, observed.ByteOffset, observed.ByteLength));
            }
        }
        foreach (var item in before) Assert.Equal(item.hash, Hash(item.Path));
        Assert.Empty(Directory.GetFiles(staging));
        WriteJson(Path.Combine(evidence, "publication-and-fresh-validation.json"), new
        { receipt, expected, actual = report.Observed, actualV2 = report.ObservedV2, final, sha256 = Hash(final), fresh,
            classification = "Actual structured service publication plus separate fresh Native observation; device NotRun" });
        return report;
    }

    private static void SavePrepared(NeutralMediaBundle bundle, string evidence, string label)
    {
        var paths = new[] { bundle.PrimaryImage.Path, bundle.MotionVideo?.Path, bundle.GainMap?.Path }
            .Concat(bundle.AuxiliaryMedia.Select(item => item.MaterializedArtifact?.Path)).Where(path => path is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string directory = Path.Combine(evidence, "prepared"); Directory.CreateDirectory(directory);
        var retained = paths.Select((path, index) =>
        {
            string destination = Path.Combine(directory, index + Path.GetExtension(path));
            File.Copy(path, destination, true);
            return new { originalPath = path, retainedPath = destination, byteLength = new FileInfo(path).Length, sha256 = Hash(path) };
        }).ToArray();
        WriteJson(Path.Combine(evidence, label + "-neutral-manifest.json"), new
        { bundle.Manifest, bundle.Semantics, bundle.PreservationCarriers, bundle.AuxiliaryMedia, bundle.PreparationExecution, retained });
    }
    private static void SaveFailure(string evidence, Exception ex, NeutralMediaBundle? bundle) =>
        WriteJson(Path.Combine(evidence, "failure.json"), new { type = ex.GetType().FullName, ex.Message, ex.StackTrace,
            manifest = bundle?.Manifest, semantics = bundle?.Semantics, carriers = bundle?.PreservationCarriers,
            classification = "Actual failure retained; no metadata/asset removed to qualify the input" });
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value,
        new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    private static string CaseDirectory(string id)
    {
        string configured = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R3_OUTPUTS") ??
            Path.Combine(W2EvidencePaths.RepositoryRoot, ".ai-tmp", "workspace", "P9", "target-writers", "R3", "real-family");
        string path = Path.Combine(Path.GetFullPath(configured), Safe(id)); Directory.CreateDirectory(path); return path;
    }
    private static string GetExistingSamplePath(string filename)
    {
        string path = Path.Combine(W2EvidencePaths.CacheRoot, filename);
        Assert.True(File.Exists(path), "Required cached original is missing: " + path);
        var record = RealSampleCorpusManifest.GetRequiredSample(filename);
        Assert.Equal(record.ByteSize, new FileInfo(path).Length);
        Assert.Equal(record.Sha256.ToUpperInvariant(), Hash(path));
        return path;
    }
    private static string Safe(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string HashRange(string path, long offset, long length)
    {
        using var stream = File.OpenRead(path); stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[65536];
        while (length > 0) { int n = stream.Read(buffer, 0, (int)Math.Min(length, buffer.Length)); Assert.True(n > 0); hash.AppendData(buffer, 0, n); length -= n; }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void CopyRange(string source, long offset, long length, string destination)
    {
        using var input = File.OpenRead(source); input.Position = offset;
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None); byte[] buffer = new byte[65536];
        while (length > 0) { int n = input.Read(buffer, 0, (int)Math.Min(length, buffer.Length)); Assert.True(n > 0); output.Write(buffer, 0, n); length -= n; }
    }
}
