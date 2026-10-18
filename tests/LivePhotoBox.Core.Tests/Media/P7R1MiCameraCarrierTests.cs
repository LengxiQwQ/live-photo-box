using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R1MiCameraCarrierTests
{
    private const string GCameraNamespace = "http://ns.google.com/photos/1.0/camera/";
    private const string MiCameraNamespace = "http://ns.xiaomi.com/photos/1.0/camera/";
    private const string WrongMiCameraNamespace = "https://example.invalid/not-micamera/";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_ValidMiCameraClaimReturnsTwoEmbeddedPrimaryOwnedAuxiliaries()
    {
        Candidate candidate = CreateCandidate("valid");
        SourceMediaFacts? facts = null;
        Exception? error = null;
        try
        {
            facts = await new SourceInspector().InspectAsync(candidate.Path);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        bool onlyInputExists = Directory.GetFiles(candidate.DirectoryPath).SequenceEqual(new[] { candidate.Path }, StringComparer.OrdinalIgnoreCase);
        WriteEvidence("05-micamera-valid.json", new
        {
            caseId = "MiCamera-valid",
            input = FileIdentitySnapshot(candidate.Path),
            expectedPrimaryEnd = candidate.VideoStart,
            expectedVideoStart = candidate.VideoStart,
            expectedSubimage = new { offset = candidate.SubimageStart, length = candidate.SubimageLength, sha256 = candidate.SubimageSha256 },
            expectedLenswatermark = new { offset = candidate.LenswatermarkStart, length = candidate.LenswatermarkLength, sha256 = candidate.LenswatermarkSha256 },
            facts = FactsSnapshot(facts),
            noProductOutput = onlyInputExists,
            operationError = error is null ? null : new { type = error.GetType().FullName, error.Message }
        });

        try
        {
            Assert.Null(error);
            Assert.NotNull(facts);
            Assert.Equal(SourceProtocol.GoogleMicroVideoV1, facts.Protocol);
            Assert.Equal(candidate.VideoStart, facts.PrimaryImage.ByteLength);
            Assert.Equal(candidate.VideoStart, facts.MotionVideo?.ByteOffset);
            Assert.Equal(2, facts.AuxiliaryItems.Count);
            AssertAuxiliary(facts.AuxiliaryItems.Single(item => item.Semantic == "MiCameraSubimage"),
                "MiCameraSubimage", "micamera:xmpmeta:subimage", "micamera-xmpmeta-subimage",
                AuxiliaryCodec.Jpeg, candidate.SubimageStart, candidate.SubimageLength, candidate.SubimageSha256);
            AssertAuxiliary(facts.AuxiliaryItems.Single(item => item.Semantic == "MiCameraLensWatermark"),
                "MiCameraLensWatermark", "micamera:xmpmeta:lenswatermark", "micamera-xmpmeta-lenswatermark",
                AuxiliaryCodec.Copy, candidate.LenswatermarkStart, candidate.LenswatermarkLength, candidate.LenswatermarkSha256);
            Assert.True(onlyInputExists, "Inspector created an undeclared product output for a source-only check.");
        }
        finally
        {
            RemoveCandidate(candidate);
        }
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_MalformedOrIncompleteXmpMetaFailsClosed()
    {
        var malformed = await InspectRejectedAsync("malformed", new CandidateOptions { MalformedInnerXml = true });
        var incomplete = await InspectRejectedAsync("incomplete", new CandidateOptions { OmitLenswatermark = true });
        WriteEvidence("06-micamera-malformed-incomplete.json", new
        {
            caseId = "MiCamera-malformed-and-incomplete",
            malformed,
            incomplete
        });
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_WrongNamespaceXmpMetaFailsClosed()
    {
        var result = await InspectRejectedAsync("wrong-namespace", new CandidateOptions { WrongNamespace = true });
        WriteEvidence("07-micamera-wrong-namespace.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_DuplicateXmpMetaOwnershipFailsClosed()
    {
        var result = await InspectRejectedAsync("duplicate-owner", new CandidateOptions { DuplicateOwner = true });
        WriteEvidence("08-micamera-duplicate-owner.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_OutOfFileRangeFailsClosed()
    {
        var result = await InspectRejectedAsync("out-of-file", new CandidateOptions { SubimageOffsetOverride = 1_000_000_000 });
        WriteEvidence("09-micamera-out-of-file.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_OverlappingRangesFailClosed()
    {
        var result = await InspectRejectedAsync("overlap", new CandidateOptions { SubimageOffsetDelta = -1 });
        WriteEvidence("10-micamera-overlap.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_UnexpectedElementFailsClosed()
    {
        var result = await InspectRejectedAsync("unexpected-element", new CandidateOptions { IncludeUnexpectedElement = true });
        WriteEvidence("11-micamera-unexpected-element.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_UnclassifiedGapFailsClosed()
    {
        var result = await InspectRejectedAsync("unclassified-gap", new CandidateOptions { PrefixGapBytes = 1 });
        WriteEvidence("12-micamera-unclassified-gap.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_ClaimedNonJpegBytesFailClosed()
    {
        var result = await InspectRejectedAsync("not-a-jpeg", new CandidateOptions { InvalidSubimage = true });
        WriteEvidence("13-micamera-invalid-jpeg.json", result);
    }

    [Fact]
    [Trait("P7", "Unit2-BX007")]
    public async Task Inspector_MalformedOrTruncatedPngFailsClosed()
    {
        var malformed = await InspectRejectedAsync("malformed-png", new CandidateOptions { InvalidLenswatermarkPng = true });
        var truncated = await InspectRejectedAsync("truncated-png", new CandidateOptions { TruncatedLenswatermarkPng = true });
        WriteEvidence("14-micamera-invalid-png.json", new
        {
            caseId = "MiCamera-malformed-and-truncated-png",
            malformed,
            truncated
        });
    }

    private static async Task<object> InspectRejectedAsync(string caseName, CandidateOptions options)
    {
        Candidate candidate = CreateCandidate(caseName, options);
        Exception? error = null;
        try
        {
            await new SourceInspector().InspectAsync(candidate.Path);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        bool onlyInputExists = Directory.GetFiles(candidate.DirectoryPath).SequenceEqual(new[] { candidate.Path }, StringComparer.OrdinalIgnoreCase);
        var result = new
        {
            caseId = $"MiCamera-{caseName}",
            input = FileIdentitySnapshot(candidate.Path),
            rejected = error is not null,
            errorType = error?.GetType().FullName,
            errorMessage = error?.Message,
            noProductOutput = onlyInputExists
        };
        try
        {
            Assert.NotNull(error);
            Assert.True(onlyInputExists, "Malformed MiCamera claims must fail before producing any product output.");
        }
        finally
        {
            RemoveCandidate(candidate);
        }
        return result;
    }

    private static Candidate CreateCandidate(string name, CandidateOptions? options = null)
    {
        options ??= new CandidateOptions();
        string workspace = RequiredEnvironment("P7_R1_BX007_WORKSPACE");
        string directory = Path.Combine(workspace, "mi-camera-cases", name);
        if (Directory.Exists(directory))
        {
            throw new IOException($"P7 MiCamera test input collision: '{directory}'.");
        }
        Directory.CreateDirectory(directory);

        byte[] subimage = options.InvalidSubimage ? [0x11, 0x22, 0x33, 0x44, 0x55] : SyntheticProtocolFixtures.CreateMinimalJpeg();
        byte[] lenswatermark = CreateMinimalPng();
        if (options.InvalidLenswatermarkPng) lenswatermark[0] = 0;
        if (options.TruncatedLenswatermarkPng) lenswatermark = lenswatermark[..^12];
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        byte[] prefixGap = Enumerable.Repeat((byte)0xA5, options.PrefixGapBytes).ToArray();
        byte[] betweenGap = Enumerable.Repeat((byte)0x5A, options.BetweenGapBytes).ToArray();
        byte[] suffixGap = Enumerable.Repeat((byte)0x3C, options.SuffixGapBytes).ToArray();

        long subimageOffset = checked((long)subimage.Length + betweenGap.Length + lenswatermark.Length + suffixGap.Length + video.Length);
        if (options.SubimageOffsetOverride is long overridden)
        {
            subimageOffset = overridden;
        }
        else
        {
            subimageOffset = checked(subimageOffset + options.SubimageOffsetDelta);
        }
        long lenswatermarkOffset = checked((long)lenswatermark.Length + suffixGap.Length + video.Length);
        string innerXml = BuildInnerXml(subimageOffset, subimage.Length, lenswatermarkOffset,
            lenswatermark.Length, options);
        string xmp = BuildOuterXmp(innerXml, options.WrongNamespace, options.DuplicateOwner, video.Length);
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(xmp);
        long subimageStart = checked((long)jpeg.Length + prefixGap.Length);
        long lenswatermarkStart = checked(subimageStart + subimage.Length + betweenGap.Length);
        long videoStart = checked(lenswatermarkStart + lenswatermark.Length + suffixGap.Length);

        string path = Path.Combine(directory, "candidate.jpg");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(jpeg);
            output.Write(prefixGap);
            output.Write(subimage);
            output.Write(betweenGap);
            output.Write(lenswatermark);
            output.Write(suffixGap);
            output.Write(video);
            output.Flush(flushToDisk: true);
        }

        return new Candidate(
            directory,
            path,
            jpeg.Length,
            videoStart,
            subimageStart,
            subimage.Length,
            Hash(subimage),
            lenswatermarkStart,
            lenswatermark.Length,
            Hash(lenswatermark));
    }

    private static string BuildInnerXml(
        long subimageOffset,
        int subimageLength,
        long lenswatermarkOffset,
        int lenswatermarkLength,
        CandidateOptions options)
    {
        if (options.MalformedInnerXml)
        {
            return $"<?xml version='1.0' encoding='UTF-8'?><subimage offset='{subimageOffset}' length='{subimageLength}'/><lenswatermark";
        }

        var elements = new StringBuilder();
        if (!options.OmitSubimage)
        {
            elements.Append($"<subimage offset='{subimageOffset}' length='{subimageLength}' width='16' height='16' paddingx='0' paddingy='0' rotation='0'/>");
        }
        if (!options.OmitLenswatermark)
        {
            elements.Append($"<lenswatermark offset='{lenswatermarkOffset}' length='{lenswatermarkLength}' width='16' height='16' paddingx='0' paddingy='0'/>");
        }
        if (options.IncludeUnexpectedElement)
        {
            elements.Append("<thumbnail offset='1' length='1' width='1' height='1'/>");
        }
        return $"<?xml version='1.0' encoding='UTF-8' standalone='yes' ?>{elements}";
    }

    private static string BuildOuterXmp(string innerXml, bool wrongNamespace, bool duplicateOwner, int videoLength)
    {
        XNamespace xmp = "adobe:ns:meta/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace google = GCameraNamespace;
        XNamespace miCamera = wrongNamespace ? WrongMiCameraNamespace : MiCameraNamespace;

        XElement Description(bool includeGoogleFields) => new(rdf + "Description",
            new XAttribute(rdf + "about", string.Empty),
            new XAttribute(XNamespace.Xmlns + "MiCamera", miCamera.NamespaceName),
            new XAttribute(miCamera + "XMPMeta", innerXml),
            includeGoogleFields ? new XAttribute(XNamespace.Xmlns + "GCamera", google.NamespaceName) : null,
            includeGoogleFields ? new XAttribute(google + "MicroVideo", "1") : null,
            includeGoogleFields ? new XAttribute(google + "MicroVideoVersion", "1") : null,
            includeGoogleFields ? new XAttribute(google + "MicroVideoOffset", "" ) : null);

        XElement primaryDescription = Description(includeGoogleFields: true);
        // Google MicroVideoOffset is the EOF-relative MP4 range length. The XML
        // packet may change size without affecting this suffix length.
        primaryDescription.SetAttributeValue(google + "MicroVideoOffset", videoLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        XElement rdfElement = new(rdf + "RDF",
            new XAttribute(XNamespace.Xmlns + "rdf", rdf.NamespaceName),
            primaryDescription);
        if (duplicateOwner)
        {
            rdfElement.Add(Description(includeGoogleFields: false));
        }

        return new XElement(xmp + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", xmp.NamespaceName),
            rdfElement).ToString(SaveOptions.DisableFormatting);
    }

    private static void AssertAuxiliary(
        AuxiliaryMediaFacts item,
        string semantic,
        string stableIdentity,
        string relationship,
        AuxiliaryCodec codec,
        long offset,
        long length,
        string sha256)
    {
        Assert.True(item.IsPresent);
        Assert.Equal(semantic, item.Semantic);
        Assert.Equal(stableIdentity, item.StableIdentity);
        Assert.Equal(relationship, item.Relationship);
        Assert.Equal("primary:0", item.OwnerIdentity);
        Assert.Equal(0, item.SourceIndex);
        Assert.Equal(ImageContainer.Jpeg, item.Container);
        Assert.Equal(codec, item.Codec);
        Assert.Equal(AuxiliaryRepresentation.Embedded, item.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, item.Ownership);
        Assert.Equal(offset, item.ByteOffset);
        Assert.Equal(length, item.ByteLength);
        Assert.Equal(sha256, item.Sha256);
    }

    private static object FactsSnapshot(SourceMediaFacts? facts) => facts is null
        ? new { returned = false }
        : new
        {
            returned = true,
            protocol = facts.Protocol.ToString(),
            primaryImage = new { facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength },
            motionVideo = facts.MotionVideo is null ? null : new { facts.MotionVideo.ByteOffset, facts.MotionVideo.ByteLength },
            auxiliaryItems = facts.AuxiliaryItems.Select(item => new
            {
                item.Semantic,
                item.StableIdentity,
                item.OwnerIdentity,
                item.Relationship,
                item.SourceIndex,
                container = item.Container.ToString(),
                codec = item.Codec.ToString(),
                representation = item.Representation.ToString(),
                ownership = item.Ownership.ToString(),
                item.ByteOffset,
                item.ByteLength,
                item.Sha256
            }).ToArray()
        };

    private static object FileIdentitySnapshot(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return new { path = Path.GetFullPath(path), bytes = stream.Length, sha256 = Convert.ToHexString(sha.ComputeHash(stream)) };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] CreateMinimalPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");

    private static string RequiredEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Required environment variable '{name}' is missing.");
        return Path.GetFullPath(value);
    }

    private static void WriteEvidence(string fileName, object value)
    {
        string directory = RequiredEnvironment("P7_R1_BX007_EVIDENCE_DIRECTORY");
        string path = Path.Combine(directory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, EvidenceJsonOptions);
        stream.Flush(flushToDisk: true);
    }

    private static void RemoveCandidate(Candidate candidate)
    {
        if (Directory.Exists(candidate.DirectoryPath)) Directory.Delete(candidate.DirectoryPath, recursive: true);
    }

    private sealed record Candidate(
        string DirectoryPath,
        string Path,
        long JpegEnd,
        long VideoStart,
        long SubimageStart,
        long SubimageLength,
        string SubimageSha256,
        long LenswatermarkStart,
        long LenswatermarkLength,
        string LenswatermarkSha256);

    private sealed record CandidateOptions
    {
        public bool MalformedInnerXml { get; init; }
        public bool OmitSubimage { get; init; }
        public bool OmitLenswatermark { get; init; }
        public bool WrongNamespace { get; init; }
        public bool DuplicateOwner { get; init; }
        public bool IncludeUnexpectedElement { get; init; }
        public bool InvalidSubimage { get; init; }
        public bool InvalidLenswatermarkPng { get; init; }
        public bool TruncatedLenswatermarkPng { get; init; }
        public int PrefixGapBytes { get; init; }
        public int BetweenGapBytes { get; init; }
        public int SuffixGapBytes { get; init; }
        public long SubimageOffsetDelta { get; init; }
        public long? SubimageOffsetOverride { get; init; }
    }
}
