using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ImageColorHighBitDepthTests
{
    private const string ManifestRelativePath = "tools/p5-r4-color-high-bit-depth-validation/profiles/P5-R4-v1/manifest.json";
    private static readonly string s_repoRoot = FindRepositoryRoot();
    private static readonly string s_testWorkspaceRoot = Path.Combine(s_repoRoot, ".ai-tmp", "workspace", "P5-R4", "tests");

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R4_AppleP3JpegToHeic()
    {
        R4Sample sample = LoadSample("苹果-双文件.JPG");
        await AssertSampleIdentityAsync(sample);
        string expectedIccSha = sample.ExpectedIndependentFacts.GetProperty("iccSha256").GetString()!;
        string strictTarget = TargetDirectory("apple-p3-strict");
        AssertEmptyDirectory(strictTarget);

        ImageConversionResult strict = await ConvertAsync(
            CreateArtifact(sample.Path, ImageContainer.Jpeg), ImageContainer.Heic,
            strictTarget, PreservationPolicy.Strict);
        AssertNoPublishedOutput(strict, strictTarget);
        Assert.Equal(ConversionFailureStage.PolicyRejection, strict.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.PolicyRejection, strict.ExecutionRecord.Truth.FailureCategory);

        string bestEffortTarget = TargetDirectory("apple-p3-best-effort");
        Directory.CreateDirectory(bestEffortTarget);
        ImageConversionResult result = await ConvertAsync(
            CreateArtifact(sample.Path, ImageContainer.Jpeg), ImageContainer.Heic,
            bestEffortTarget, PreservationPolicy.BestEffort);

        AssertSuccessfulLossyIccCarriage(result, ImageContainer.Heic);
        Assert.NotNull(result.OutputArtifact);
        PreservationObservation outputColor = await NativeMediaService.CapturePreservationObservationAsync(
            result.OutputArtifact.Path, SourceProtocol.Unknown, ImageContainer.Heic);
        Assert.True(outputColor.HasIcc);
        Assert.False(outputColor.IccParseError);
        Assert.Equal(expectedIccSha, outputColor.IccSha256, ignoreCase: true);
        Assert.Equal(new[] { "ICC" }, ReadPrimaryColrKinds(await File.ReadAllBytesAsync(result.OutputArtifact.Path)));
        NativeHeicPrimaryDecodeInfo decoded = await NativeMediaService.DecodeHeicPrimaryAsync(result.OutputArtifact.Path);
        Assert.Equal((byte)1, decoded.HasIcc);
        Assert.Equal((byte)0, decoded.HasNclx);
        await AssertSampleIdentityAsync(sample);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R4_UnprofiledJpegToHeic()
    {
        R4Sample sample = LoadSample("5B_JPEG_SDR.jpg");
        await AssertSampleIdentityAsync(sample);
        Assert.Equal(JsonValueKind.Null, sample.ExpectedIndependentFacts.GetProperty("iccSha256").ValueKind);
        string strictTarget = TargetDirectory("unprofiled-jpeg-strict");
        AssertEmptyDirectory(strictTarget);

        ImageConversionResult strict = await ConvertAsync(
            CreateArtifact(sample.Path, ImageContainer.Jpeg), ImageContainer.Heic,
            strictTarget, PreservationPolicy.Strict);
        AssertNoPublishedOutput(strict, strictTarget);
        Assert.Equal(ConversionFailureStage.PolicyRejection, strict.ExecutionRecord.Truth.FailureStage);

        string bestEffortTarget = TargetDirectory("unprofiled-jpeg-best-effort");
        Directory.CreateDirectory(bestEffortTarget);
        ImageConversionResult result = await ConvertAsync(
            CreateArtifact(sample.Path, ImageContainer.Jpeg), ImageContainer.Heic,
            bestEffortTarget, PreservationPolicy.BestEffort);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.NotApplicable, result.ExecutionRecord.Truth.ColorIcc);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
        PreservationObservation outputColor = await NativeMediaService.CapturePreservationObservationAsync(
            result.OutputArtifact.Path, SourceProtocol.Unknown, ImageContainer.Heic);
        Assert.False(outputColor.HasIcc);
        Assert.False(outputColor.IccParseError);
        Assert.Empty(ReadPrimaryColrKinds(await File.ReadAllBytesAsync(result.OutputArtifact.Path)));
        await AssertSampleIdentityAsync(sample);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R4_HuaweiHeicPrimaryToJpeg()
    {
        R4Sample sample = LoadSample("华为Mate80.heic");
        await AssertSampleIdentityAsync(sample);
        string expectedIccSha = sample.ExpectedIndependentFacts.GetProperty("iccSha256").GetString()!;
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.Path);
        SourceMediaFacts facts = inspected.Facts;
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        using var workspace = new P5R4Workspace("huawei-neutral-primary");
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, sample.Path, null, workspace);
        MediaArtifact primary = bundle.PrimaryImage;
        Assert.Equal(ImageContainer.Heic, primary.ImageContainer);
        Assert.Equal(facts.PrimaryImage.ByteOffset, primary.SourceOffset);
        Assert.Equal(facts.PrimaryImage.ByteLength, primary.ByteLength);
        Assert.Equal(await ComputeRangeSha256Async(sample.Path, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength),
            primary.Sha256, ignoreCase: true);

        PreservationObservation sourceColor = await NativeMediaService.CapturePreservationObservationAsync(
            primary.Path, SourceProtocol.Unknown, ImageContainer.Heic);
        Assert.True(sourceColor.HasIcc);
        Assert.False(sourceColor.IccParseError);
        Assert.Equal(expectedIccSha, sourceColor.IccSha256, ignoreCase: true);

        string strictTarget = TargetDirectory("huawei-primary-strict");
        AssertEmptyDirectory(strictTarget);
        ImageConversionResult strict = await ConvertAsync(
            primary, ImageContainer.Jpeg, strictTarget, PreservationPolicy.Strict);
        AssertNoPublishedOutput(strict, strictTarget);
        Assert.Equal(ConversionFailureStage.PolicyRejection, strict.ExecutionRecord.Truth.FailureStage);

        string bestEffortTarget = TargetDirectory("huawei-primary-best-effort");
        Directory.CreateDirectory(bestEffortTarget);
        ImageConversionResult result = await ConvertAsync(
            primary, ImageContainer.Jpeg, bestEffortTarget, PreservationPolicy.BestEffort);
        AssertSuccessfulLossyIccCarriage(result, ImageContainer.Jpeg);
        Assert.NotNull(result.OutputArtifact);
        PreservationObservation outputColor = await NativeMediaService.CapturePreservationObservationAsync(
            result.OutputArtifact.Path, SourceProtocol.Unknown, ImageContainer.Jpeg);
        Assert.True(outputColor.HasIcc);
        Assert.False(outputColor.IccParseError);
        Assert.Equal(expectedIccSha, outputColor.IccSha256, ignoreCase: true);
        Assert.Equal(sample.Sha256, await ComputeSha256Async(sample.Path), ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R4_TenBitHeicToJpeg()
    {
        R4Sample sample = LoadSample("3B_HEIC_SDR.heic");
        await AssertSampleIdentityAsync(sample);
        NativeHeicPrimaryDecodeInfo input = await NativeMediaService.DecodeHeicPrimaryAsync(sample.Path);
        Assert.True(input.SourceBitDepth > 8);
        Assert.True(input.DecodedSignalBitDepth > 8);
        string strictTarget = TargetDirectory("ten-bit-jpeg-strict");
        string bestEffortTarget = TargetDirectory("ten-bit-jpeg-best-effort");
        AssertEmptyDirectory(strictTarget);
        AssertEmptyDirectory(bestEffortTarget);

        ImageConversionResult strict = await ConvertAsync(
            CreateArtifact(sample.Path, ImageContainer.Heic), ImageContainer.Jpeg,
            strictTarget, PreservationPolicy.Strict);
        AssertNoPublishedOutput(strict, strictTarget);
        Assert.Equal(ConversionFailureStage.PolicyRejection, strict.ExecutionRecord.Truth.FailureStage);

        ImageConversionResult bestEffort = await ConvertAsync(
            CreateArtifact(sample.Path, ImageContainer.Heic), ImageContainer.Jpeg,
            bestEffortTarget, PreservationPolicy.BestEffort);
        AssertNoPublishedOutput(bestEffort, bestEffortTarget);
        Assert.Equal(ConversionOperationKind.Unsupported, bestEffort.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureStage.PolicyRejection, bestEffort.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.Unsupported, bestEffort.ExecutionRecord.Truth.FailureCategory);
        Assert.False(bestEffort.ExecutionRecord.Truth.FallbackOccurred);
        await AssertSampleIdentityAsync(sample);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R4_TenBitHeicPassthrough()
    {
        R4Sample sample = LoadSample("3B_HEIC_SDR.heic");
        await AssertSampleIdentityAsync(sample);
        NativeHeicPrimaryDecodeInfo input = await NativeMediaService.DecodeHeicPrimaryAsync(sample.Path);
        Assert.True(input.SourceBitDepth > 8);
        string strictTarget = TargetDirectory("ten-bit-passthrough-strict");
        string bestEffortTarget = TargetDirectory("ten-bit-passthrough-best-effort");
        Directory.CreateDirectory(strictTarget);
        Directory.CreateDirectory(bestEffortTarget);

        foreach ((PreservationPolicy policy, string target) in new[]
        {
            (PreservationPolicy.Strict, strictTarget),
            (PreservationPolicy.BestEffort, bestEffortTarget)
        })
        {
            ImageConversionResult result = await ConvertAsync(
                CreateArtifact(sample.Path, ImageContainer.Heic), ImageContainer.Heic, target, policy);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(result.OutputArtifact);
            Assert.Equal(ConversionOperationKind.Passthrough, result.ExecutionRecord.Truth.ActualOperationKind);
            Assert.False(result.ExecutionRecord.PixelReencoded);
            Assert.Equal(sample.Sha256, await ComputeSha256Async(result.OutputArtifact.Path), ignoreCase: true);
            NativeHeicPrimaryDecodeInfo output = await NativeMediaService.DecodeHeicPrimaryAsync(result.OutputArtifact.Path);
            Assert.True(output.SourceBitDepth > 8);
            Assert.Equal(input.SourceBitDepth, output.SourceBitDepth);
        }

        await AssertSampleIdentityAsync(sample);
    }

    [Fact]
    public async Task P5R4_TargetProfileTransformUnsupported()
    {
        string[] publicRequestProperties = typeof(ImageConversionRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(publicRequestProperties, name =>
            name.Contains("TargetProfile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TargetColorSpace", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TargetBitDepth", StringComparison.OrdinalIgnoreCase));

        string fixturePath = await CreateNclxOnlyPrimaryFixtureAsync();
        NativeHeicPrimaryDecodeInfo fixtureFacts = await NativeMediaService.DecodeHeicPrimaryAsync(fixturePath);
        Assert.Equal(8u, fixtureFacts.SourceBitDepth);
        Assert.Equal((byte)1, fixtureFacts.HasNclx);
        Assert.Equal((byte)0, fixtureFacts.HasIcc);
        Assert.Equal(new[] { "NCLX" }, ReadPrimaryColrKinds(await File.ReadAllBytesAsync(fixturePath)));

        string target = TargetDirectory("nclx-only-no-transform");
        AssertEmptyDirectory(target);
        ImageConversionResult result = await ConvertAsync(
            CreateArtifact(fixturePath, ImageContainer.Heic), ImageContainer.Jpeg,
            target, PreservationPolicy.BestEffort);

        AssertNoPublishedOutput(result, target);
        Assert.Equal(ConversionOperationKind.Unsupported, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureStage.PolicyRejection, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
    }

    [Fact]
    public async Task P5R4_AmbiguousColorMetadata()
    {
        R4Sample sample = LoadSample("华为Mate80.heic");
        await AssertSampleIdentityAsync(sample);
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.Path);
        using var workspace = new P5R4Workspace("ambiguous-color-input");
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, sample.Path, null, workspace);
        string neutralPrimaryHash = await ComputeSha256Async(bundle.PrimaryImage.Path);
        byte[] ambiguousBytes = DuplicatePrimaryColrAssociation(await File.ReadAllBytesAsync(bundle.PrimaryImage.Path));
        string ambiguousPath = workspace.AllocateFilePath("duplicate-primary-colr", ".heic");
        await File.WriteAllBytesAsync(ambiguousPath, ambiguousBytes);

        PreservationObservation observation = await NativeMediaService.CapturePreservationObservationAsync(
            ambiguousPath, SourceProtocol.Unknown, ImageContainer.Heic);
        Assert.True(observation.IccParseError);

        string target = TargetDirectory("ambiguous-color-output");
        AssertEmptyDirectory(target);
        ImageConversionResult result = await ConvertAsync(
            CreateArtifact(ambiguousPath, ImageContainer.Heic), ImageContainer.Jpeg,
            target, PreservationPolicy.BestEffort);
        AssertNoPublishedOutput(result, target);
        Assert.Equal(neutralPrimaryHash, await ComputeSha256Async(bundle.PrimaryImage.Path), ignoreCase: true);
        await AssertSampleIdentityAsync(sample);
    }

    private static async Task<ImageConversionResult> ConvertAsync(
        MediaArtifact source, ImageContainer target, string targetDirectory, PreservationPolicy policy) =>
        await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = source,
            TargetContainer = target,
            TargetDirectory = targetDirectory,
            Quality = 92,
            PreservationPolicy = policy
        });

    private static MediaArtifact CreateArtifact(string path, ImageContainer container) => new()
    {
        Path = path,
        Kind = MediaArtifactKind.PrimaryImage,
        MimeType = container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
        ImageContainer = container,
        ImageCodec = container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
        ByteLength = new FileInfo(path).Length
    };

    private static void AssertSuccessfulLossyIccCarriage(ImageConversionResult result, ImageContainer target)
    {
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.Equal(target, result.OutputArtifact.ImageContainer);
        Assert.True(result.ExecutionRecord.PixelReencoded);
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.ImageCodec, result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.ColorIcc);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.Truth.PreservationOutcome);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
    }

    private static void AssertNoPublishedOutput(ImageConversionResult result, string targetDirectory)
    {
        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Empty(Directory.EnumerateFileSystemEntries(targetDirectory));
    }

    private static void AssertEmptyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    private static string TargetDirectory(string scenario)
    {
        string path = Path.Combine(s_testWorkspaceRoot, "outputs", scenario);
        string root = Path.GetFullPath(Path.Combine(s_testWorkspaceRoot, "outputs"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("P5-R4 test output escaped its deterministic workspace root.");
        if (Directory.Exists(fullPath))
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("P5-R4 test output directory unexpectedly became a reparse point.");
            Directory.Delete(fullPath, recursive: true);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task AssertSampleIdentityAsync(R4Sample sample)
    {
        var info = new FileInfo(sample.Path);
        Assert.True(info.Exists, $"Required P5-R4 RealSample is missing: {sample.Path}");
        Assert.Equal(sample.ByteSize, info.Length);
        Assert.Equal(sample.Sha256, await ComputeSha256Async(sample.Path), ignoreCase: true);
    }

    private static R4Sample LoadSample(string filename)
    {
        string root = Path.Combine(s_repoRoot, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(root));
        JsonElement item = manifest.RootElement.GetProperty("samples")
            .EnumerateArray()
            .Single(sample => string.Equals(sample.GetProperty("filename").GetString(), filename, StringComparison.Ordinal));
        string relativePath = item.GetProperty("cachePath").GetString()!;
        string fullPath = Path.GetFullPath(Path.Combine(s_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relativeToRoot = Path.GetRelativePath(s_repoRoot, fullPath);
        Assert.False(relativeToRoot.StartsWith("..", StringComparison.Ordinal));
        return new R4Sample(
            fullPath,
            item.GetProperty("byteSize").GetInt64(),
            item.GetProperty("sha256").GetString()!,
            item.GetProperty("expectedIndependentFacts").Clone());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string manifest = Path.Combine(current.FullName, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(manifest)) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository's versioned P5-R4 manifest from the test base directory.");
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<string> ComputeRangeSha256Async(string path, long offset, long length)
    {
        Assert.True(offset >= 0);
        Assert.True(length > 0);
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.InRange(offset, 0, stream.Length - 1);
        Assert.InRange(length, 1, stream.Length - offset);
        stream.Position = offset;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            Assert.True(read > 0, "The inspected Huawei primary range ended unexpectedly.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> CreateNclxOnlyPrimaryFixtureAsync()
    {
        R4Sample sample = LoadSample("华为Mate80.heic");
        await AssertSampleIdentityAsync(sample);
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(sample.Path);
        using var workspace = new P5R4Workspace("target-profile-transform-source");
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan, sample.Path, null, workspace);
        NativeHeicPrimaryDecodeInfo sourceFacts = await NativeMediaService.DecodeHeicPrimaryAsync(bundle.PrimaryImage.Path);
        Assert.Equal(8u, sourceFacts.SourceBitDepth);

        byte[] primaryBytes = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path);
        byte[] fixtureBytes = ReplacePrimaryIccWithNclx(primaryBytes);
        Assert.Equal(new[] { "NCLX" }, ReadPrimaryColrKinds(fixtureBytes));

        string fixtureDirectory = Path.Combine(s_testWorkspaceRoot, "materialized", "target-profile-transform-unsupported");
        Directory.CreateDirectory(fixtureDirectory);
        string fixturePath = Path.Combine(fixtureDirectory, "nclx-only-primary.heic");
        await File.WriteAllBytesAsync(fixturePath, fixtureBytes);
        return fixturePath;
    }

    private static byte[] ReplacePrimaryIccWithNclx(byte[] data)
    {
        HeifPrimaryColorGraph graph = ParseHeifPrimaryColorGraph(data);
        if (graph.PrimaryColorProperties.Count != 1 || graph.PrimaryColorProperties[0].Kind != "ICC")
            throw new InvalidDataException("The deterministic negative fixture source must have one primary ICC property.");

        PreservationTestHelpers.HeifBoxHeader iccBox = graph.PrimaryColorProperties[0].Box;
        byte[] nclxBox = CreateNclxColrBox();
        int freeBoxLength = iccBox.BoxLength - nclxBox.Length;
        if (freeBoxLength < 8)
            throw new InvalidDataException("The source ICC property does not leave enough space for a valid NCLX replacement.");

        int suffixStart = iccBox.BoxStart + iccBox.BoxLength;
        int suffixLength = graph.IpcoBox.BodyStart + graph.IpcoBox.BodyLength - suffixStart;
        int prefixLength = iccBox.BoxStart - graph.IpcoBox.BodyStart;
        byte[] freeBox = new byte[freeBoxLength];
        BinaryPrimitives.WriteUInt32BigEndian(freeBox.AsSpan(0, 4), checked((uint)freeBoxLength));
        "free"u8.CopyTo(freeBox.AsSpan(4, 4));

        byte[] replacementBody = new byte[graph.IpcoBox.BodyLength];
        Buffer.BlockCopy(data, graph.IpcoBox.BodyStart, replacementBody, 0, prefixLength);
        Buffer.BlockCopy(nclxBox, 0, replacementBody, prefixLength, nclxBox.Length);
        Buffer.BlockCopy(data, suffixStart, replacementBody, prefixLength + nclxBox.Length, suffixLength);
        Buffer.BlockCopy(freeBox, 0, replacementBody, prefixLength + nclxBox.Length + suffixLength, freeBox.Length);
        if (replacementBody.Length != graph.IpcoBox.BodyLength)
            throw new InvalidDataException("The negative fixture changed the HEIF property-container length.");

        byte[] result = (byte[])data.Clone();
        Buffer.BlockCopy(replacementBody, 0, result, graph.IpcoBox.BodyStart, replacementBody.Length);
        return result;
    }

    private static byte[] CreateNclxColrBox()
    {
        byte[] box = new byte[19];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), checked((uint)box.Length));
        "colr"u8.CopyTo(box.AsSpan(4, 4));
        "nclx"u8.CopyTo(box.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt16BigEndian(box.AsSpan(12, 2), 1); // BT.709 primaries
        BinaryPrimitives.WriteUInt16BigEndian(box.AsSpan(14, 2), 1); // BT.709 transfer
        BinaryPrimitives.WriteUInt16BigEndian(box.AsSpan(16, 2), 1); // BT.709 matrix
        box[18] = 0; // limited range; reserved bits are zero
        return box;
    }

    private static IReadOnlyList<string> ReadPrimaryColrKinds(byte[] data) =>
        ParseHeifPrimaryColorGraph(data).PrimaryColorProperties.Select(property => property.Kind).ToArray();

    private static HeifPrimaryColorGraph ParseHeifPrimaryColorGraph(byte[] data)
    {
        List<PreservationTestHelpers.HeifBoxHeader> top = PreservationTestHelpers.ParseSequentialBoxes(data, 0, data.Length)
            ?? throw new InvalidDataException("Could not parse HEIF top-level boxes.");
        PreservationTestHelpers.HeifBoxHeader meta = top.Single(box => box.Type == "meta");
        List<PreservationTestHelpers.HeifBoxHeader> metaChildren = PreservationTestHelpers.ParseSequentialBoxes(
            data, meta.BodyStart + 4, meta.BodyLength - 4)
            ?? throw new InvalidDataException("Could not parse HEIF meta children.");
        PreservationTestHelpers.HeifBoxHeader iprp = metaChildren.Single(box => box.Type == "iprp");
        List<PreservationTestHelpers.HeifBoxHeader> iprpChildren = PreservationTestHelpers.ParseSequentialBoxes(
            data, iprp.BodyStart, iprp.BodyLength)
            ?? throw new InvalidDataException("Could not parse HEIF iprp children.");
        PreservationTestHelpers.HeifBoxHeader ipco = iprpChildren.Single(box => box.Type == "ipco");
        PreservationTestHelpers.HeifBoxHeader ipma = iprpChildren.Single(box => box.Type == "ipma");
        List<PreservationTestHelpers.HeifBoxHeader> properties = PreservationTestHelpers.ParseSequentialBoxes(
            data, ipco.BodyStart, ipco.BodyLength)
            ?? throw new InvalidDataException("Could not parse HEIF ipco properties.");
        uint primaryId = PreservationTestHelpers.ExtractHeicPrimaryItemId(data)
            ?? throw new InvalidDataException("Could not resolve HEIF pitm primary item id.");

        var colorKinds = new Dictionary<int, (string Kind, PreservationTestHelpers.HeifBoxHeader Box)>();
        for (int index = 0; index < properties.Count; index++)
        {
            PreservationTestHelpers.HeifBoxHeader property = properties[index];
            if (property.Type != "colr") continue;
            if (property.BodyLength < 4)
                throw new InvalidDataException("HEIF colr property has a truncated color type.");
            ReadOnlySpan<byte> colorType = data.AsSpan(property.BodyStart, 4);
            string kind = colorType.SequenceEqual("prof"u8) || colorType.SequenceEqual("rICC"u8)
                ? "ICC"
                : colorType.SequenceEqual("nclx"u8) ? "NCLX" : "OTHER";
            colorKinds.Add(index + 1, (kind, property));
        }

        if (ipma.BodyLength < 8) throw new InvalidDataException("HEIF ipma box is truncated.");
        byte version = data[ipma.BodyStart];
        int flags = (data[ipma.BodyStart + 1] << 16) | (data[ipma.BodyStart + 2] << 8) | data[ipma.BodyStart + 3];
        if (version > 1 || (flags & ~1) != 0)
            throw new InvalidDataException("HEIF ipma version or flags are unsupported by the structural test reader.");
        bool wide = (flags & 1) != 0;
        int position = ipma.BodyStart + 4;
        int end = ipma.BodyStart + ipma.BodyLength;
        if (position + 4 > end) throw new InvalidDataException("HEIF ipma entry count is truncated.");
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
        position += 4;
        var primaryColors = new List<PrimaryColorProperty>();

        for (uint entry = 0; entry < entryCount; entry++)
        {
            int itemIdWidth = version == 0 ? 2 : 4;
            if (position + itemIdWidth + 1 > end) throw new InvalidDataException("HEIF ipma item entry is truncated.");
            uint itemId = version == 0
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2))
                : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            position += itemIdWidth;
            byte associationCount = data[position++];
            for (int association = 0; association < associationCount; association++)
            {
                int width = wide ? 2 : 1;
                if (position + width > end) throw new InvalidDataException("HEIF ipma property association is truncated.");
                int raw = wide
                    ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2))
                    : data[position];
                position += width;
                int propertyIndex = wide ? raw & 0x7FFF : raw & 0x7F;
                if (propertyIndex == 0) continue;
                if (propertyIndex > properties.Count)
                    throw new InvalidDataException("HEIF ipma refers to a property outside ipco.");
                if (itemId == primaryId && colorKinds.TryGetValue(propertyIndex, out var color))
                    primaryColors.Add(new PrimaryColorProperty(color.Kind, color.Box));
            }
        }
        if (position != end) throw new InvalidDataException("HEIF ipma has trailing bytes.");
        return new HeifPrimaryColorGraph(ipco, primaryColors);
    }

    private static byte[] DuplicatePrimaryColrAssociation(byte[] data)
    {
        List<PreservationTestHelpers.HeifBoxHeader> top =
            PreservationTestHelpers.ParseSequentialBoxes(data, 0, data.Length)
            ?? throw new InvalidDataException("Could not parse the neutral HEIF primary's top-level boxes.");
        PreservationTestHelpers.HeifBoxHeader meta = top.Single(box => box.Type == "meta");
        List<PreservationTestHelpers.HeifBoxHeader> metaChildren =
            PreservationTestHelpers.ParseSequentialBoxes(data, meta.BodyStart + 4, meta.BodyLength - 4)
            ?? throw new InvalidDataException("Could not parse the neutral HEIF meta box.");
        PreservationTestHelpers.HeifBoxHeader iprp = metaChildren.Single(box => box.Type == "iprp");
        List<PreservationTestHelpers.HeifBoxHeader> iprpChildren =
            PreservationTestHelpers.ParseSequentialBoxes(data, iprp.BodyStart, iprp.BodyLength)
            ?? throw new InvalidDataException("Could not parse the neutral HEIF iprp box.");
        PreservationTestHelpers.HeifBoxHeader ipco = iprpChildren.Single(box => box.Type == "ipco");
        PreservationTestHelpers.HeifBoxHeader ipma = iprpChildren.Single(box => box.Type == "ipma");
        List<PreservationTestHelpers.HeifBoxHeader> properties =
            PreservationTestHelpers.ParseSequentialBoxes(data, ipco.BodyStart, ipco.BodyLength)
            ?? throw new InvalidDataException("Could not parse the neutral HEIF ipco box.");
        int colorPropertyIndex = properties
            .Select((property, index) => (property, index: index + 1))
            .Where(entry => entry.property.Type == "colr")
            .Where(entry => IsIccColorProperty(data, entry.property))
            .Select(entry => entry.index)
            .Single();
        uint primaryId = PreservationTestHelpers.ExtractHeicPrimaryItemId(data)
            ?? throw new InvalidDataException("Could not resolve the neutral HEIF primary item id.");

        byte version = data[ipma.BodyStart];
        int flags = (data[ipma.BodyStart + 1] << 16) | (data[ipma.BodyStart + 2] << 8) | data[ipma.BodyStart + 3];
        bool wide = (flags & 1) != 0;
        int p = ipma.BodyStart + 4;
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        p += 4;
        int replacementOffset = -1;
        int replacementValue = -1;
        bool replacementWide = false;

        for (uint entry = 0; entry < entryCount; entry++)
        {
            uint itemId = version < 1
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2))
                : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            p += version < 1 ? 2 : 4;
            byte associationCount = data[p++];
            for (int association = 0; association < associationCount; association++)
            {
                int raw;
                int rawOffset = p;
                if (wide)
                {
                    raw = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                    p += 2;
                }
                else
                {
                    raw = data[p++];
                }

                if (itemId != primaryId) continue;
                int propertyIndex = wide ? raw & 0x7FFF : raw & 0x7F;
                if (propertyIndex == 0 || propertyIndex == colorPropertyIndex) continue;
                string propertyType = properties[propertyIndex - 1].Type;
                if (propertyType is "pasp" or "clap" or "irot" or "imir" or "pixi")
                {
                    replacementOffset = rawOffset;
                    replacementValue = raw;
                    replacementWide = wide;
                    break;
                }
                if (replacementOffset < 0 && propertyType is not "ispe" and not "hvcC" and not "auxC")
                {
                    replacementOffset = rawOffset;
                    replacementValue = raw;
                    replacementWide = wide;
                }
            }
            if (replacementOffset >= 0 && replacementValue >= 0 && itemId == primaryId) break;
        }

        if (replacementOffset < 0 || replacementValue < 0)
            throw new InvalidDataException("No replaceable primary property association was available for the duplicate-colr adversarial fixture.");

        byte[] mutated = (byte[])data.Clone();
        if (replacementWide)
        {
            ushort raw = (ushort)((replacementValue & 0x8000) | colorPropertyIndex);
            BinaryPrimitives.WriteUInt16BigEndian(mutated.AsSpan(replacementOffset, 2), raw);
        }
        else
        {
            mutated[replacementOffset] = (byte)((replacementValue & 0x80) | colorPropertyIndex);
        }
        return mutated;
    }

    private static bool IsIccColorProperty(byte[] data, PreservationTestHelpers.HeifBoxHeader property)
    {
        int payload = property.BodyStart;
        if (property.BodyLength < 4) return false;
        return data.AsSpan(payload, 4).SequenceEqual("rICC"u8) ||
            data.AsSpan(payload, 4).SequenceEqual("prof"u8);
    }

    private sealed record R4Sample(string Path, long ByteSize, string Sha256, JsonElement ExpectedIndependentFacts);
    private sealed record PrimaryColorProperty(
        string Kind, PreservationTestHelpers.HeifBoxHeader Box);
    private sealed record HeifPrimaryColorGraph(
        PreservationTestHelpers.HeifBoxHeader IpcoBox, IReadOnlyList<PrimaryColorProperty> PrimaryColorProperties);

    private sealed class P5R4Workspace : IMediaWorkspace
    {
        public P5R4Workspace(string scenario)
        {
            RootDirectory = Path.Combine(s_testWorkspaceRoot, "materialized", scenario);
            Directory.CreateDirectory(RootDirectory);
        }

        public string RootDirectory { get; }

        public string AllocateFilePath(string prefix, string extension)
        {
            string suffix = extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
            return Path.Combine(RootDirectory, $"{prefix}-{Guid.NewGuid():N}{suffix}");
        }

        public Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default) =>
            ComputeSha256Async(filePath);

        public async Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string actual = await ComputeSha256Async(sourcePath);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("P5-R4 test workspace detected a modified source file.");
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
