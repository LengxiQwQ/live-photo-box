using LivePhotoBox.Services;

namespace LivePhotoBox.Models;

// Queue-specific choices are kept separate from media diagnosis.
internal static class RepairQueuePolicy
{
    internal static string OutcomeForFile(LivePhotoRepairService.RepairResult result, bool isImage, bool imageRepairDisabled)
    {
        if (result.Status is "failed" or "planned") return result.Status;
        string? actual = isImage ? result.ImageStatus : result.VideoStatus;
        if (actual != null) return actual;
        if (!isImage || !imageRepairDisabled || result.Status is "failed" or "planned") return result.Status;
        // The pair may have been repaired/isolated, but these image bytes were
        // deliberately left unchanged. Preserve the fact of publication.
        return result.Image != null && result.Status is "success" or "warning" or "copied"
            ? "copied" : "skipped";
    }

    internal static bool ShouldExecute(RepairIssueType? issue, bool imageRepairDisabled, bool copyPerfect) =>
        !imageRepairDisabled && (issue is RepairIssueType.NeedsStrip or RepairIssueType.NeedsRebuild or RepairIssueType.Limited ||
        (copyPerfect && issue == RepairIssueType.Perfect));

    internal static RepairOptions OptionsFor(RepairOptions selected, bool imageRepairDisabled) => new()
    {
        FixImageRotation = !imageRepairDisabled && selected.FixImageRotation,
        StripImageThumbnail = !imageRepairDisabled && selected.StripImageThumbnail,
        FixHeicOrientation = !imageRepairDisabled && selected.FixHeicOrientation,
        FixVideoRotation = selected.FixVideoRotation
    };

    internal static string DiagnosticOutcome(RepairIssueType? issue, bool imageRepairDisabled) =>
        issue == RepairIssueType.Error ? "failed" :
        issue == RepairIssueType.Perfect && !imageRepairDisabled ? "healthy" : "skipped";

    internal static bool HasSelectedAction(RepairOptions options) => options.FixImageRotation ||
        options.StripImageThumbnail || options.FixHeicOrientation || options.FixVideoRotation;
    internal static uint ActionMask(RepairAnalysisResult analysis, bool imageDisabled, bool isHeif = false)
    {
        if (analysis.IssueType is RepairIssueType.NonApple or RepairIssueType.Error) return 0;
        uint mask = 0;
        if (!imageDisabled && !analysis.IsVideo)
        {
            if (analysis.NeedsImageOrientationRepair) mask |= 1;
            if (analysis.HasThumbnail) mask |= 2;
            if (analysis.HasHeicOrientationConflict) mask |= 4;
            if (analysis.IssueType == RepairIssueType.Limited) mask |= isHeif ? 6u : 3u; // checks remain uncertain
        }
        if (analysis.VideoCheckIncomplete || analysis.NeedsEmbeddedVideoTransform || analysis.AutoplayVideoRotationAngle != 0 ||
            analysis.VideoRotationAngle != 0 || analysis.VideoTrackTransform.Length != 0 ||
            (analysis.IsVideo && analysis.IssueType is RepairIssueType.NeedsRebuild or RepairIssueType.Limited)) mask |= 8;
        return mask;
    }
}
