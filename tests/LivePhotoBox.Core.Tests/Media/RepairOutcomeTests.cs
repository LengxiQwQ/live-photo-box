using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public class RepairOutcomeTests
{
    [Fact]
    public void NoOutputWarningCannotClaimIsolation()
    {
        var result = new LivePhotoRepairService.RepairResult("warning", "Direction not completed")
        { Actions = LivePhotoRepairService.ActionResults(new(), 0, 8, false) };
        Assert.Empty(result.PublishedOutputs);
        Assert.Equal("warning_no_output", result.OutcomeKey);
        Assert.Equal("not_completed", result.Completion);
        Assert.Equal("not_completed", result.Actions.Single(x => x.Action == "video_orientation").State);
        Assert.Equal("video_direction_unverified", result.Actions.Single(x => x.Action == "video_orientation").ReasonCode);
    }

    [Theory]
    [InlineData(0u, "isolated_copy", "warning_copy")]
    [InlineData(2u, "isolated_repaired", "warning_isolated")]
    public void PublishedPartialAndUnchangedCopyHaveDifferentFacts(uint changed, string kind, string key)
    {
        var result = new LivePhotoRepairService.RepairResult("warning", "video remains incomplete", Path.GetFullPath("opaque-name.jpg"))
        { Actions = LivePhotoRepairService.ActionResults(new(), changed, 8, false) };
        Assert.Equal(kind, Assert.Single(result.PublishedOutputs).Kind);
        Assert.Equal(key, result.OutcomeKey);
    }

    [Fact]
    public void PairIsolationDoesNotEraseCopiedMemberOrCompletedVideo()
    {
        var result = new LivePhotoRepairService.RepairResult("warning", "cover unknown", "copy.heic", "changed.mov")
        { ImageStatus = "copied", VideoStatus = "success", Actions = LivePhotoRepairService.ActionResults(new(), 8, 0, false) };
        Assert.Equal("isolated_copy", result.PublishedOutputs[0].Kind);
        Assert.Equal("isolated_repaired", result.PublishedOutputs[1].Kind);
        Assert.Equal("copied", RepairQueuePolicy.OutcomeForFile(result, true, false));
        Assert.Equal("success", RepairQueuePolicy.OutcomeForFile(result, false, false));
    }

    [Fact]
    public void PlannedOrDisabledActionIsNeverReportedCompleted()
    {
        var planned = LivePhotoRepairService.ActionResults(new(), 8, 0, true);
        Assert.Equal("planned", planned[3].State);
        var disabled = LivePhotoRepairService.ActionResults(new() { FixVideoRotation = false }, 0, 8, false);
        Assert.Equal("not_selected", disabled[3].State);
        Assert.Null(disabled[3].ReasonCode);
    }

    [Fact]
    public void EmbeddedVideoDoesNotCountAsHeifOrPhotoRotation()
    {
        var analysis = new RepairAnalysisResult { IssueType = RepairIssueType.NeedsRebuild, NeedsEmbeddedVideoTransform = true, VideoRotationAngle = 90 };
        Assert.Equal(8u, RepairQueuePolicy.ActionMask(analysis, false));
        analysis.HasHeicOrientationConflict = true;
        Assert.Equal(12u, RepairQueuePolicy.ActionMask(analysis, false));
        Assert.Equal(8u, RepairQueuePolicy.ActionMask(analysis, true));
        analysis.IssueType = RepairIssueType.NonApple;
        Assert.Equal(0u, RepairQueuePolicy.ActionMask(analysis, false));
    }

    [Fact]
    public void UncertainVideoRemainsSelectableWithoutInventingAHeifActionForJpeg()
    {
        var analysis = new RepairAnalysisResult { IssueType = RepairIssueType.Limited, VideoCheckIncomplete = true };
        Assert.Equal(11u, RepairQueuePolicy.ActionMask(analysis, false));
        Assert.Equal(14u, RepairQueuePolicy.ActionMask(analysis, false, true));
        Assert.Equal(8u, RepairQueuePolicy.ActionMask(analysis, true));
    }

    [Fact]
    public void LogFailureDoesNotEraseAnAlreadyPublishedMediaResult()
    {
        var result = new LivePhotoRepairService.RepairResult("success", "", "verified.jpg")
        { Actions = LivePhotoRepairService.ActionResults(new(), 2, 0, false), HistoryRecorded = false };
        Assert.Equal("completed", result.Completion);
        Assert.Equal("repaired", Assert.Single(result.PublishedOutputs).Kind);
        Assert.Equal(ProcessStatus.Success, result.ProcessStatus);
    }

    [Fact]
    public async Task MissingSourceProducesNormalFailureAndNoPublishedOutput()
    {
        var result = await LivePhotoRepairService.RepairImageFileAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".heic"),
            Path.GetTempPath(), true, false, new(), default);
        Assert.Equal("failed", result.Status);
        Assert.Empty(result.PublishedOutputs);
        Assert.DoesNotContain(result.Actions, x => x.State == "completed");
    }
}
