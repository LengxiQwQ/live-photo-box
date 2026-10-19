using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceInspectorXmpTimingTests
{
    private const string Camera = "http://ns.google.com/photos/1.0/camera/";
    private const string Oppo = "http://ns.oplus.com/photos/1.0/camera/";
    private const string Vivo = "http://ns.vivo.com/photos/1.0/camera/";
    private static string Field(string protocol) => protocol switch
    {
        "v1" => "g:MicroVideoPresentationTimestampUs",
        "oppo-primary" => "o:MotionPhotoPrimaryPresentationTimestampUs",
        _ => "g:MotionPhotoPresentationTimestampUs"
    };

    public static IEnumerable<object[]> InvalidTimingCases()
    {
        foreach (string protocol in new[] { "v1", "v2", "vivo", "oppo", "oppo-primary" })
            foreach (string variant in new[] { "decimal", "overflow", "empty", "duplicate-equal", "duplicate-conflict" })
                yield return [protocol, variant];
    }

    [Theory]
    [MemberData(nameof(InvalidTimingCases))]
    public async Task Inspect_InvalidOptionalTiming_FailsClosed(string protocol, string variant)
    {
        string value = variant switch { "decimal" => "1.5", "overflow" => "9223372036854775808", "empty" => "", _ => "1" };
        string field = Field(protocol);
        string duplicate = variant.StartsWith("duplicate", StringComparison.Ordinal)
            ? $"<{field}>{(variant == "duplicate-conflict" ? "2" : "1")}</{field}>" : "";
        await RejectAsync(Build(protocol, $"{field}=\"{value}\"", duplicate),
            variant.StartsWith("duplicate", StringComparison.Ordinal) ? SourceInspectionFailureCategory.Ambiguous : SourceInspectionFailureCategory.Malformed,
            variant.StartsWith("duplicate", StringComparison.Ordinal) ? SourceInspectionStage.Metadata : SourceInspectionStage.Protocol);
    }

    [Theory]
    [InlineData("v1", false)]
    [InlineData("v1", true)]
    [InlineData("v2", false)]
    [InlineData("v2", true)]
    [InlineData("vivo", false)]
    [InlineData("vivo", true)]
    [InlineData("oppo", false)]
    [InlineData("oppo", true)]
    [InlineData("oppo-primary", false)]
    [InlineData("oppo-primary", true)]
    public async Task Inspect_TimingInAnotherDescription_CannotJoinProtocolOwner(string protocol, bool element)
    {
        string field = Field(protocol);
        string owner = element ? $"<rdf:Description><{field}>1</{field}></rdf:Description>"
            : $"<rdf:Description {field}=\"1\"/>";
        await RejectAsync(Build(protocol, "", "", owner),
            SourceInspectionFailureCategory.Ambiguous, SourceInspectionStage.Metadata);
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("vivo")]
    [InlineData("oppo")]
    [InlineData("oppo-primary")]
    public async Task Inspect_ValidOptionalTiming_IsObserved(string protocol)
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("valid-time", ".jpg");
        await File.WriteAllBytesAsync(input, Build(protocol, $"{Field(protocol)}=\"123\"", ""));
        var facts = await new SourceInspector().InspectAsync(input);
        Assert.Equal(123, facts.Timing.CoverTimestampUs);
        if (protocol == "oppo-primary") Assert.Equal(123, facts.Timing.PrimaryTimestampUs);
    }

    [Fact]
    public async Task Inspect_ValidV2_IgnoresSameOwnerV1CompatibilityTiming()
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("v2-ignores-v1", ".jpg");
        await File.WriteAllBytesAsync(input, Build("v2", "g:MotionPhotoPresentationTimestampUs=\"123\" g:MicroVideoPresentationTimestampUs=\"not-a-number\"", ""));
        var facts = await new SourceInspector().InspectAsync(input);
        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
        Assert.Equal(123, facts.Timing.CoverTimestampUs);
    }

    private static async Task RejectAsync(byte[] bytes, SourceInspectionFailureCategory category, SourceInspectionStage stage)
    {
        using var workspace = new TestFixtureWorkspace();
        string input = workspace.AllocateFilePath("invalid-time", ".jpg");
        await File.WriteAllBytesAsync(input, bytes);
        var inspector = new SourceInspector();
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(input));
        Assert.Equal(category, error.Category);
        Assert.Equal(stage, error.Stage);
        var planError = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectWithPlanAsync(input));
        Assert.Equal(category, planError.Category);
        Assert.Equal(stage, planError.Stage);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(input)));
        Assert.Single(Directory.GetFiles(workspace.RootDirectory));
    }

    private static byte[] Build(string protocol, string timing, string body, string otherOwner = "")
    {
        byte[] video = SyntheticProtocolFixtures.CreateMinimalMp4();
        string attributes = protocol == "v1"
            ? $"g:MicroVideo=\"1\" g:MicroVideoVersion=\"1\" g:MicroVideoOffset=\"{video.Length}\""
            : "g:MotionPhoto=\"1\" g:MotionPhotoVersion=\"1\"";
        if (protocol.StartsWith("oppo", StringComparison.Ordinal))
            attributes += $" o:MotionPhotoOwner=\"oplus\" o:OLivePhotoVersion=\"2\" o:VideoLength=\"{video.Length}\"";
        if (protocol == "vivo") attributes += " v:VMotionPhotoVersion=\"1\"";
        string directory = protocol == "v1" ? "" :
            "<dir:Directory><rdf:Seq>" +
            "<rdf:li><dir:Item i:Semantic=\"Primary\" i:Mime=\"image/jpeg\" i:Length=\"0\"/></rdf:li>" +
            $"<rdf:li><dir:Item i:Semantic=\"MotionPhoto\" i:Mime=\"video/mp4\" i:Length=\"{video.Length}\"/></rdf:li>" +
            "</rdf:Seq></dir:Directory>";
        if (protocol == "vivo")
            directory = directory.Replace(" i:Length=\"0\"", "", StringComparison.Ordinal)
                .Replace($"i:Length=\"{video.Length}\"", $"i:Length=\"{video.Length}\" i:Padding=\"0\"", StringComparison.Ordinal);
        string xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            $"<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:g=\"{Camera}\" xmlns:o=\"{Oppo}\" xmlns:v=\"{Vivo}\" " +
            "xmlns:dir=\"http://ns.google.com/photos/1.0/container/\" xmlns:i=\"http://ns.google.com/photos/1.0/container/item/\">" +
            $"<rdf:Description {attributes} {timing}>{directory}{body}</rdf:Description>{otherOwner}</rdf:RDF></x:xmpmeta>";
        return [.. SyntheticProtocolFixtures.CreateJpegWithXmp(xml), .. video];
    }
}
