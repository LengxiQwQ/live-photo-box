using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public class RepairQueuePolicyTests
{
    [Theory]
    [InlineData("success", "copied", "success")]
    [InlineData("warning", "copied", "success")]
    [InlineData("warning", "warning", "copied")]
    public void ActualMemberActionsTakePrecedenceOverPairOutcome(string group, string image, string video)
    {
        var result = new LivePhotoRepairService.RepairResult(group, "whole-pair result", "image.heic", "video.mov")
        { ImageStatus = image, VideoStatus = video };
        Assert.Equal(image, RepairQueuePolicy.OutcomeForFile(result, true, false));
        Assert.Equal(video, RepairQueuePolicy.OutcomeForFile(result, false, false));
    }

    [Theory]
    [InlineData("success", "copied")]
    [InlineData("warning", "copied")]
    [InlineData("copied", "copied")]
    [InlineData("failed", "failed")]
    [InlineData("healthy", "skipped")]
    public void DisabledImage_DoesNotInheritVideoRepairButKeepsPublicationOrFailure(string pairStatus, string expected)
    {
        bool published = pairStatus is "success" or "warning" or "copied";
        var result = new LivePhotoRepairService.RepairResult(pairStatus, "paired video action",
            published ? "image.heic" : null, published ? "video.mov" : null);
        Assert.Equal(expected, RepairQueuePolicy.OutcomeForFile(result, true, true));
        Assert.Equal(pairStatus, RepairQueuePolicy.OutcomeForFile(result, false, false));
        Assert.Equal(pairStatus, RepairQueuePolicy.OutcomeForFile(result, true, false));
    }

    [Theory]
    [InlineData(RepairIssueType.Limited, true, false, false)]
    [InlineData(RepairIssueType.NeedsRebuild, true, true, false)]
    [InlineData(RepairIssueType.Perfect, true, true, false)]
    [InlineData(RepairIssueType.Perfect, false, true, true)]
    [InlineData(RepairIssueType.Error, false, true, false)]
    [InlineData(RepairIssueType.NonApple, false, true, false)]
    [InlineData(RepairIssueType.Limited, false, false, true)]
    public void Execution_RespectsDisabledImageAndScopeEvenWhenCopyIsEnabled(RepairIssueType issue, bool disabled, bool copy, bool expected)
        => Assert.Equal(expected, RepairQueuePolicy.ShouldExecute(issue, disabled, copy));

    [Theory]
    [InlineData(null, false, "skipped")]
    [InlineData(RepairIssueType.NonApple, false, "skipped")]
    [InlineData(RepairIssueType.Error, false, "failed")]
    [InlineData(RepairIssueType.Error, true, "failed")]
    [InlineData(RepairIssueType.Perfect, false, "healthy")]
    [InlineData(RepairIssueType.Perfect, true, "skipped")]
    [InlineData(RepairIssueType.Limited, false, "skipped")]
    public void DiagnosisOnly_DoesNotInventSuccessfulChecks(RepairIssueType? issue, bool disabled, string expected)
        => Assert.Equal(expected, RepairQueuePolicy.DiagnosticOutcome(issue, disabled));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisabledImage_LeavesOnlySelectedIndependentVideoAction(bool video)
    {
        var selected = new RepairOptions { FixVideoRotation = video };
        var effective = RepairQueuePolicy.OptionsFor(selected, true);
        Assert.False(effective.FixImageRotation);
        Assert.False(effective.StripImageThumbnail);
        Assert.False(effective.FixHeicOrientation);
        Assert.Equal(video, effective.FixVideoRotation);
        Assert.Equal(video, RepairQueuePolicy.HasSelectedAction(effective));
        Assert.True(selected.FixImageRotation);
        Assert.True(selected.StripImageThumbnail);
        Assert.True(selected.FixHeicOrientation);
    }

    [Fact]
    public void EnabledImage_DoesNotEnableAnUnselectedAction()
    {
        var effective = RepairQueuePolicy.OptionsFor(new RepairOptions
        {
            FixImageRotation = false, StripImageThumbnail = false,
            FixHeicOrientation = false, FixVideoRotation = false
        }, false);
        Assert.False(RepairQueuePolicy.HasSelectedAction(effective));
    }
}
