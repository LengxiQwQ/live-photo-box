using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Workspace;
using System.Text.Json.Serialization;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8MediaChangeReportTests
{
    private static MediaAssetObservation Track(string id, string codec, char hash) => new()
    {
        Id = id, Kind = "Track", Handler = "soun", Codec = codec, SampleCount = 12,
        Sha256 = new string(hash, 64), DescriptionSha256 = codec
    };
    private static MediaAssetInventory Inventory(params MediaAssetObservation[] assets) => new()
    { SchemaVersion = 1, ArtifactSha256 = new string('F', 64), Assets = assets };

    [Fact]
    public void MainAudioReencodeKeepsSecondaryIdentityAndProjectsOnlyActualChanges()
    {
        var before = Inventory(Track("track:2", "lpcm", 'A'), Track("track:3", "mp4a", 'B'));
        var after = Inventory(Track("track:2", "mp4a", 'C'), Track("track:3", "mp4a", 'B'));
        var changes = MediaChangeReports.Compare(before, after, "VideoConversion");
        Assert.Equal(MediaChangeAction.Reencoded, changes[0].Action);
        Assert.Equal(MediaChangeAction.Copied, changes[1].Action);
        Assert.Equal(before.ArtifactSha256, changes[0].SourceArtifactSha256);
        var report = MediaChangeReports.Finish(changes, [], [], []);
        Assert.Equal("track:2", Assert.Single(report.ToNotCarried()).Item);
    }

    [Fact]
    public void CleanupAuthorityDoesNotAuthorizeChangesToUnrelatedAudioPayload()
    {
        Assert.Throws<InvalidDataException>(() => MediaChangeReports.Compare(
            Inventory(Track("track:2", "mp4a", 'A')),
            Inventory(Track("track:2", "mp4a", 'B')), "SourceCleanup", authorizedCleanup: true));
    }

    [Fact]
    public void StableIdentityCannotSilentlyChangeTrackRole()
    {
        var track = Track("track:2", "mp4a", 'A');
        Assert.Throws<InvalidDataException>(() => MediaChangeReports.Compare(
            Inventory(track), Inventory(track with { Handler = "vide" }), "VideoConversion"));
    }

    [Fact]
    public void MissingSecondaryTrackIsNotReplacedByAnotherAvailableAudioTrack()
    {
        Assert.Throws<InvalidDataException>(() => MediaChangeReports.Compare(
            Inventory(Track("track:2", "lpcm", 'A'), Track("track:3", "mp4a", 'B')),
            Inventory(Track("track:3", "mp4a", 'B')), "VideoConversion"));
    }

    [Fact]
    public void VersionedReportRoundTripsWithHistoryAndDoesNotClaimLossesPreserved()
    {
        var change = new MediaAssetChange
        {
            AssetId = "gainmap:1", Stage = "TargetPreparation", Action = MediaChangeAction.Degraded,
            ReasonCode = MediaChangeReason.ContainerCodecUnsupported, Reason = "Explicit unsupported representation"
        };
        var report = new MediaChangeReport { Items = [change] };
        var outcome = new ProductOperationOutcome { MediaChanges = report, NotCarried = report.ToNotCarried() };
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string json = JsonSerializer.Serialize(outcome, options);
        var restored = JsonSerializer.Deserialize<ProductOperationOutcome>(json, options)!;
        Assert.Equal(1, restored.MediaChanges!.SchemaVersion);
        Assert.Equal(MediaChangeReason.ContainerCodecUnsupported, Assert.Single(restored.MediaChanges.Items).ReasonCode);
        Assert.Equal(PreservationOutcome.DegradedToSdr, Assert.Single(restored.NotCarried).Outcome);
        Assert.DoesNotContain(restored.NotCarried, x => x.Outcome == PreservationOutcome.Preserved);
    }
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CompoundOpaquePrefixMaterializationKeepsExactSuffixAndRejectsCopiedIdentity()
    {
        string source = TestSampleResolver.ResolveSample("荣耀.jpg");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        using var workspace = new MediaWorkspace();
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(source, null, workspace,
            preservationPolicy: PreservationPolicy.Strict);
        var compound = Assert.IsType<NativeCompoundPrimary>(bundle.PrimaryImage.CompoundPrimary);
        var observation = compound.Inspect(bundle.PrimaryImage.Path, CancellationToken.None).Observation;
        byte[] primary = File.ReadAllBytes(bundle.PrimaryImage.Path);
        string output = workspace.AllocateFilePath("prefix-record-test", ".bin");
        var materialized = compound.MaterializeOpaqueRecords(bundle.PrimaryImage.Path, output, CancellationToken.None);
        byte[] raw = File.ReadAllBytes(output);
        int directoryLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(raw.Length - 4)));
        Assert.Equal("lpbr", System.Text.Encoding.ASCII.GetString(raw, raw.Length - 8, 4));
        Assert.Equal(primary.AsSpan(checked((int)observation.KnownLength)).ToArray(), raw.AsSpan(0, raw.Length - directoryLength).ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw)), materialized.Sha256);
        using var directory = JsonDocument.Parse(raw.AsMemory(raw.Length-directoryLength,directoryLength-9));
        var row = Assert.Single(directory.RootElement.EnumerateArray());
        Assert.Equal("SourcePrefixFragment", row.GetProperty("name").GetString());
        Assert.Equal(checked((long)observation.OpaqueLength), row.GetProperty("length").GetInt64());
        using var foreign = new P9TargetTestInputs.Folder(nameof(CompoundOpaquePrefixMaterializationKeepsExactSuffixAndRejectsCopiedIdentity));
        string copied = Path.Combine(foreign.Path, "foreign-prefix.jpg");
        File.Copy(bundle.PrimaryImage.Path, copied);
        string rejected = workspace.AllocateFilePath("rejected-prefix", ".bin");
        Assert.ThrowsAny<Exception>(() => compound.MaterializeOpaqueRecords(copied, rejected, CancellationToken.None));
        Assert.False(File.Exists(rejected));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => compound.MaterializeOpaqueRecords(bundle.PrimaryImage.Path, rejected, cancelled.Token));
        Assert.False(File.Exists(rejected));
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
    }

}
