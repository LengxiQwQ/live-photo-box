using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class P8ProductOutcomeTests
{
    [Fact]
    public void MergeProjection_UsesCommittedCollisionSuffixAndKeepsHistoryWarningNonfatal()
    {
        var outcome = Outcome(ProductOperationKind.Merge, ProductOperationStatus.Success) with
        {
            RequestedDestination = "C:/output/photo.jpg",
            CommittedOutputs = [new() { Role = "TargetLivePhoto", Path = "C:/output/photo_2.jpg" }],
            HistoryWarning = "Operation history could not be saved."
        };
        using var json = Serialize(MergeCommand.ProjectSingleOutcome("photo.jpg", "video.mp4", outcome));
        Assert.Equal("C:/output/photo_2.jpg", json.RootElement.GetProperty("output").GetString());
        Assert.Equal("merged", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unknown", json.RootElement.GetProperty("outcome").GetProperty("FinalTargetPreservation").GetString());
        Assert.Equal(outcome.HistoryWarning, json.RootElement.GetProperty("historyWarning").GetString());
        Assert.Equal(0, outcome.ExitCode);
    }

    [Theory]
    [InlineData("Unsupported", 1)]
    [InlineData("Failed", 1)]
    [InlineData("Cancelled", 130)]
    public void TerminalFailureProjection_DoesNotInventOutput(string statusName, int exitCode)
    {
        var status = Enum.Parse<ProductOperationStatus>(statusName);
        var outcome = Outcome(ProductOperationKind.Merge, status) with { RequestedDestination = "C:/foreign/photo.jpg" };
        using var merge = Serialize(MergeCommand.ProjectSingleOutcome("photo.jpg", "video.mp4", outcome));
        using var split = Serialize(SplitCommand.ProjectSingleOutcome("live.jpg", outcome));
        Assert.Equal("", merge.RootElement.GetProperty("output").GetString());
        Assert.Equal("", split.RootElement.GetProperty("imageOutput").GetString());
        Assert.Equal("", split.RootElement.GetProperty("videoOutput").GetString());
        Assert.Empty(merge.RootElement.GetProperty("outcome").GetProperty("CommittedOutputs").EnumerateArray());
        Assert.Equal(status.ToString().ToLowerInvariant(), merge.RootElement.GetProperty("status").GetString());
        Assert.Equal(exitCode, outcome.ExitCode);
    }

    [Fact]
    public void SplitProjection_UsesActualArtifactRoles()
    {
        var outcome = Outcome(ProductOperationKind.Split, ProductOperationStatus.Success) with
        {
            CommittedOutputs = [new() { Role = "MotionVideo", Path = "C:/output/live_3.mp4" },
                new() { Role = "PrimaryImage", Path = "C:/output/live_3.jpg" }]
        };
        using var json = Serialize(SplitCommand.ProjectSingleOutcome("live.jpg", outcome));
        Assert.Equal("C:/output/live_3.jpg", json.RootElement.GetProperty("imageOutput").GetString());
        Assert.Equal("C:/output/live_3.mp4", json.RootElement.GetProperty("videoOutput").GetString());
    }

    [Fact]
    public void FailureProjection_RetainsSelectedUnknownFallbackWithoutClaimingActualEncoder()
    {
        var outcome = Outcome(ProductOperationKind.Split, ProductOperationStatus.Failed) with
        {
            PreparationExecution = new() { Videos = [new()
            {
                InputContainer = VideoContainer.Mp4, InputCodec = VideoCodec.H264,
                RequestedContainer = VideoContainer.Mp4, RequestedCodec = VideoCodec.H264,
                OutputContainer = VideoContainer.Unknown, OutputCodec = VideoCodec.Unknown,
                Backend = VideoBackend.MinimalLibav, SelectedEncoder = "minimal-libav/selected-encoder-unknown",
                HardwareFallbackOccurred = true, HardwareFallbackReason = "MFComponentMissing: selected attempt failed; no retry."
            }] }
        };
        using var json = Serialize(SplitCommand.ProjectSingleOutcome("live.jpg", outcome));
        Assert.Equal("", json.RootElement.GetProperty("imageOutput").GetString());
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        var execution = json.RootElement.GetProperty("outcome").GetProperty("PreparationExecution").GetProperty("Videos")[0];
        Assert.Equal("minimal-libav/selected-encoder-unknown", execution.GetProperty("SelectedEncoder").GetString());
        Assert.True(execution.GetProperty("HardwareFallbackOccurred").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllVariantsDryRunJson_IsSingleStructuredDocumentWithNoFabricatedExecution(bool merge)
    {
        string root = CliTestHost.CreateTempDir("lpb-p8-outcome-json-");
        try
        {
            string image = CliTestHost.CreateDummyFile(root, "photo.jpg");
            string video = CliTestHost.CreateDummyFile(root, "video.mp4");
            var args = new List<string> { image };
            if (merge) args.Add(video);
            args.AddRange(["--all-variants", "--dry-run", "--json", "--output", Path.Combine(root, "output")]);
            var result = await CliTestHost.RunAsync(merge ? MergeCommand.Create() : SplitCommand.Create(), args.ToArray());
            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            Assert.Equal("all-variants", json.RootElement.GetProperty("mode").GetString());
            Assert.NotEmpty(json.RootElement.GetProperty("files").EnumerateArray());
            foreach (var row in json.RootElement.GetProperty("files").EnumerateArray())
                Assert.Equal(JsonValueKind.Null, row.GetProperty("Outcome").ValueKind);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProductOperationOutcome Outcome(ProductOperationKind kind, ProductOperationStatus status) => new()
    {
        OperationId = Guid.NewGuid(), Operation = kind, Status = status,
        StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow,
        Diagnostic = new() { Stage = ProductOperationStage.Publication, Message = "Synthetic projection fixture" }
    };

    private static JsonDocument Serialize(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
}
