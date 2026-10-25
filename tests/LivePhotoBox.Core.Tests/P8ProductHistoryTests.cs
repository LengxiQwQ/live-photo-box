using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class P8ProductHistoryTests
{
    [Fact]
    public void DiskRoundTripKeepsCompleteOutcomeAndIsIndependentOfPendingQueue()
    {
        using var files = new HistoryFiles();
        string path = Path.Combine(files.Root, "app-roundtrip.log");
        var outcome = Sample() with
        {
            Status = ProductOperationStatus.Success,
            CommittedOutputs = [new() { Role = "PrimaryImage", Path = "actual-suffixed-2.jpg", Sha256 = "observed", ByteLength = 42 },
                new() { Role = "MotionVideo", Path = "actual-suffixed-2.mp4" }],
            PreparationSemantics = NeutralMediaSemantics.Unknown,
            PreparationExecution = new()
            {
                Image = new()
                {
                    InputContainer = ImageContainer.Jpeg, OutputContainer = ImageContainer.Jpeg,
                    Truth = new() { BackendName = "observed-backend", FallbackOccurred = true, FallbackReason = "selected attempt", HdrGainMap = ConversionComponentOutcome.Degraded }
                }
            }
        };
        var currentPath = typeof(LogService).GetField("_currentLogPath", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fileLock = typeof(LogService).GetField("_fileLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        lock (fileLock)
        {
            object? saved = currentPath.GetValue(null);
            try
            {
                currentPath.SetValue(null, path);
                Assert.True(LogService.RecordProductOutcome(outcome));
                for (int i = 0; i < 1100; i++) LogService.Info("queue pressure");
                LogService.ForceFlush();
                var read = Assert.Single(ProductOutcomeHistoryStore.Read(files.Root));
                Assert.Equal(outcome.OperationId, read.OperationId);
                Assert.Equal(2, read.CommittedOutputs.Count);
                Assert.Equal(2, read.CommittedOutputs.Select(x => x.Role).Distinct().Count());
                var imageOutput = Assert.Single(read.CommittedOutputs, x => x.Role == "PrimaryImage");
                Assert.Equal("actual-suffixed-2.jpg", imageOutput.Path);
                Assert.Equal(42, imageOutput.ByteLength);
                Assert.Equal("observed", imageOutput.Sha256);
                Assert.Equal("actual-suffixed-2.mp4", Assert.Single(read.CommittedOutputs, x => x.Role == "MotionVideo").Path);
                Assert.Equal("observed-backend", read.PreparationExecution!.Image!.Truth.BackendName);
                Assert.True(read.PreparationExecution.Image.Truth.FallbackOccurred);
                Assert.Equal(ConversionComponentOutcome.Degraded, read.PreparationExecution.Image.Truth.HdrGainMap);
                Assert.Equal(NeutralEvidenceState.Unknown, read.PreparationSemantics!.PrimaryImage.EvidenceState);
                Assert.Equal(ProductTargetPreservation.Unknown, read.FinalTargetPreservation);
                Assert.Equal(outcome.Diagnostic.Message, read.Diagnostic.Message);
            }
            finally { currentPath.SetValue(null, saved); }
        }
    }

    [Fact]
    public void ReaderIgnoresMalformedTruncatedNewerAndMissingSchemaRecords()
    {
        using var files = new HistoryFiles();
        string path = Path.Combine(files.Root, "app-invalid.log");
        var expected = Sample();
        Assert.True(ProductOutcomeHistoryStore.Append(path, expected));
        string validLine = File.ReadAllLines(path).Single(x => x.StartsWith(ProductOutcomeHistoryStore.Marker));
        string newer = validLine.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2");
        string missing = validLine.Replace("\"SchemaVersion\":1,", "");
        string malformedSchema = validLine.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":\"wrong\"");
        File.AppendAllText(path, $"human log\n{ProductOutcomeHistoryStore.Marker}{{broken}}\n{newer}\n{missing}\n{malformedSchema}\n{validLine[..^4]}", Encoding.UTF8);
        Assert.Equal(expected.OperationId, Assert.Single(ProductOutcomeHistoryStore.Read(files.Root)).OperationId);
    }

    [Theory]
    [InlineData((int)ProductOperationStatus.Failed)]
    [InlineData((int)ProductOperationStatus.Cancelled)]
    [InlineData((int)ProductOperationStatus.Unsupported)]
    public void NoOutputTerminalStatusesPersistWithoutOutputCards(int statusValue)
    {
        var status = (ProductOperationStatus)statusValue;
        using var files = new HistoryFiles();
        string path = Path.Combine(files.Root, "app-status.log");
        Assert.True(ProductOutcomeHistoryStore.Append(path, Sample() with { Status = status }));
        var read = Assert.Single(ProductOutcomeHistoryStore.Read(files.Root));
        Assert.Equal(status, read.Status);
        Assert.Empty(read.CommittedOutputs);
        Assert.False(ProductOutcomeHistoryStore.Append(path, read with { CommittedOutputs = [new() { Role = "foreign", Path = "old.jpg" }] }));
    }

    [Fact]
    public void OwnAppAndCliLogsAreDeduplicatedAndOtherDirectoriesIgnored()
    {
        using var files = new HistoryFiles();
        Directory.CreateDirectory(Path.Combine(files.Root, "CLI"));
        Directory.CreateDirectory(Path.Combine(files.Root, "unrelated"));
        var shared = Sample();
        Assert.True(ProductOutcomeHistoryStore.Append(Path.Combine(files.Root, "app-one.log"), shared));
        Assert.True(ProductOutcomeHistoryStore.Append(Path.Combine(files.Root, "CLI", "cli-two.log"), shared));
        Assert.True(ProductOutcomeHistoryStore.Append(Path.Combine(files.Root, "unrelated", "app-foreign.log"), Sample()));
        Assert.True(ProductOutcomeHistoryStore.Append(Path.Combine(files.Root, "arbitrary.log"), Sample()));
        Assert.Equal(shared.OperationId, Assert.Single(ProductOutcomeHistoryStore.Read(files.Root)).OperationId);
    }

    [Fact]
    public void StorageFailureIsNonfatalAndReadCountIsBounded()
    {
        using var files = new HistoryFiles();
        Assert.False(ProductOutcomeHistoryStore.Append(null, Sample()));
        Assert.False(ProductOutcomeHistoryStore.Append(files.Root, Sample()));
        Assert.False(ProductOutcomeHistoryStore.Append(Path.Combine(files.Root, "absent", "app-no.log"), Sample()));
        string path = Path.Combine(files.Root, "app-bounded.log");
        for (int i = 0; i < 4; i++) Assert.True(ProductOutcomeHistoryStore.Append(path, Sample()));
        Assert.Equal(2, ProductOutcomeHistoryStore.Read(files.Root, 2).Count);
        Assert.Empty(ProductOutcomeHistoryStore.Read(files.Root, 0));
        var successfulMedia = SuccessfulSplit();
        // A durable storage failure does not mutate the immutable media outcome.
        Assert.False(ProductOutcomeHistoryStore.Append(files.Root, successfulMedia));
        Assert.True(successfulMedia.IsSuccess);
    }

    [Theory]
    [InlineData("SplitEmpty")]
    [InlineData("SplitOne")]
    [InlineData("SplitWrongRole")]
    [InlineData("SplitDuplicateRole")]
    [InlineData("SplitDuplicatePath")]
    [InlineData("SplitEquivalentPath")]
    [InlineData("SplitBlankPath")]
    [InlineData("MergeEmpty")]
    [InlineData("MergeWrongRole")]
    [InlineData("MergeTwo")]
    public void MalformedSuccessShapeCannotReplaceEarlierValidRecord(string malformedShape)
    {
        using var files = new HistoryFiles();
        string path = Path.Combine(files.Root, "app-success-shape.log");
        var expected = SuccessfulSplit();
        Assert.True(ProductOutcomeHistoryStore.Append(path, expected));
        var malformed = malformedShape switch
        {
            "SplitEmpty" => expected with { CommittedOutputs = [] },
            "SplitOne" => expected with { CommittedOutputs = [expected.CommittedOutputs[0]] },
            "SplitWrongRole" => expected with { CommittedOutputs = [expected.CommittedOutputs[0], expected.CommittedOutputs[1] with { Role = "TargetLivePhoto" }] },
            "SplitDuplicateRole" => expected with { CommittedOutputs = [expected.CommittedOutputs[0], expected.CommittedOutputs[1] with { Role = "PrimaryImage" }] },
            "SplitDuplicatePath" => expected with { CommittedOutputs = [expected.CommittedOutputs[0], expected.CommittedOutputs[1] with { Path = expected.CommittedOutputs[0].Path }] },
            "SplitEquivalentPath" => expected with { CommittedOutputs = [expected.CommittedOutputs[0], expected.CommittedOutputs[1] with { Path = ".\\HISTORY-IMAGE.JPG" }] },
            "SplitBlankPath" => expected with { CommittedOutputs = [expected.CommittedOutputs[0], expected.CommittedOutputs[1] with { Path = " " }] },
            "MergeEmpty" => expected with { Operation = ProductOperationKind.Merge, CommittedOutputs = [] },
            "MergeWrongRole" => expected with { Operation = ProductOperationKind.Merge, CommittedOutputs = [expected.CommittedOutputs[0]] },
            "MergeTwo" => expected with { Operation = ProductOperationKind.Merge },
            _ => throw new ArgumentException(malformedShape)
        };
        Assert.False(ProductOutcomeHistoryStore.Append(path, malformed));
        // Bypass the writer with a complete, versioned JSON line to exercise the
        // reader's independent validation and newest-duplicate rejection.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        File.AppendAllText(path, ProductOutcomeHistoryStore.Marker + JsonSerializer.Serialize(malformed, options) + "\n", Encoding.UTF8);
        var retained = Assert.Single(ProductOutcomeHistoryStore.Read(files.Root));
        Assert.Equal(expected.OperationId, retained.OperationId);
        Assert.Equal(ProductOperationKind.Split, retained.Operation);
        Assert.Equal(ProductOperationStatus.Success, retained.Status);
        Assert.Equal(expected.CommittedOutputs.ToArray(), retained.CommittedOutputs.ToArray());
    }

    [Fact]
    public void ValidMergeRequiresOnlyHistoricalTargetRoleAndDoesNotInspectCurrentExistence()
    {
        using var files = new HistoryFiles();
        string absentPath = Path.Combine(files.Root, "historical-deleted-target.jpg");
        var outcome = Sample() with
        {
            Operation = ProductOperationKind.Merge, Status = ProductOperationStatus.Success,
            CommittedOutputs = [new() { Role = "TargetLivePhoto", Path = absentPath }]
        };
        Assert.False(File.Exists(absentPath));
        Assert.True(ProductOutcomeHistoryStore.Append(Path.Combine(files.Root, "app-merge.log"), outcome));
        var restored = Assert.Single(ProductOutcomeHistoryStore.Read(files.Root));
        Assert.Equal(absentPath, Assert.Single(restored.CommittedOutputs).Path);
        Assert.Equal(ProductTargetPreservation.Unknown, restored.FinalTargetPreservation);
    }

    private static ProductOperationOutcome SuccessfulSplit() => Sample() with
    {
        Status = ProductOperationStatus.Success,
        CommittedOutputs = [new() { Role = "PrimaryImage", Path = "history-image.jpg" }, new() { Role = "MotionVideo", Path = "history-video.mp4" }]
    };

    private static ProductOperationOutcome Sample() => new()
    {
        OperationId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow.AddSeconds(-1), CompletedUtc = DateTimeOffset.UtcNow,
        Operation = ProductOperationKind.Split, Status = ProductOperationStatus.Failed,
        Inputs = ["input.jpg"], Diagnostic = new() { Stage = ProductOperationStage.Publication, Category = ProductFailureCategory.Io, Message = "line one\nline two" }
    };

    private sealed class HistoryFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lpb-p8-history-" + Guid.NewGuid().ToString("N"));
        public HistoryFiles() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
