using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

internal sealed record RepairActionResult(string Action, string State, string? ReasonCode, string Explanation);
internal sealed record RepairLimitation(string ReasonCode, string Explanation);
internal sealed record RepairPublishedOutput(string Role, string Path, string Kind);

public static partial class LivePhotoRepairService
{
    internal static async Task<RepairResult> RepairImageFileAsync(string sourcePath, string outputDirectory, bool allDevices,
        bool dryRun, RepairOptions options, CancellationToken token, bool copyPerfect = false, RepairBatchContext? batch = null)
    {
        using var ownedBatch = batch == null ? new RepairBatchContext() : null;
        var current = batch ?? ownedBatch!;
        return await ExecuteRecordedAsync(sourcePath, options, false, outputDirectory, token, current,
            () => RepairImageFileCoreAsync(sourcePath, outputDirectory, allDevices, dryRun, options, token, copyPerfect, current)).ConfigureAwait(false);
    }

    internal static async Task<RepairResult> RepairVideoFileAsync(string sourcePath, string outputDirectory, bool allDevices,
        bool repairLongVideos, bool dryRun, bool batch, RepairOptions options, CancellationToken token,
        bool copyPerfect = false, RepairBatchContext? batchContext = null)
    {
        using var ownedBatch = batchContext == null ? new RepairBatchContext() : null;
        var current = batchContext ?? ownedBatch!;
        return await ExecuteRecordedAsync(sourcePath, options, true, outputDirectory, token, current,
            () => RepairVideoFileCoreAsync(sourcePath, outputDirectory, allDevices, repairLongVideos, dryRun, batch, options, token, copyPerfect, current)).ConfigureAwait(false);
    }

    private static async Task<RepairResult> ExecuteRecordedAsync(string source, RepairOptions options, bool video,
        string outputDirectory, CancellationToken token, RepairBatchContext batch, Func<Task<RepairResult>> execute)
    {
        using var cleanupScope = WorkspaceCleanupScope.Begin();
        RepairResult result;
        try { result = await execute(); }
        catch (OperationCanceledException) { result = new("cancelled", "用户取消，本项未完成发布。"); }
        catch (Exception ex)
        {
            LogService.Repair("Repair operation failed: " + source, LogLevel.Error, ex);
            result = new("failed", ex.Message);
        }
        var cleanupFailures = cleanupScope.Failures;
        var paths = new[] { result.Image, result.Video }.Where(path => path is not null).Select(path => path!).ToArray();
        string isolationRoot = GetIsolationDirectory(outputDirectory);
        bool alreadyIsolated = paths.Length > 0 && paths.All(path => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(isolationRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (alreadyIsolated) result = result with { IsolationDirectory = isolationRoot };
        if (cleanupFailures.Count > 0)
        {
            var messages = cleanupFailures.ToList();
            if (paths.Length > 0 && !alreadyIsolated)
            {
                var outputs = paths.Select(path => new ProductCommittedArtifact { Role = path == result.Image ? "image" : "video", Path = path }).ToArray();
                var isolation = await CleanupOutputIsolation.TryIsolateAsync(outputs, [source], outputDirectory, token).ConfigureAwait(false);
                result = result with { Image = isolation.Outputs.FirstOrDefault(output => output.Role == "image")?.Path,
                    Video = isolation.Outputs.FirstOrDefault(output => output.Role == "video")?.Path,
                    IsolationDirectory = isolation.Directory };
                if (isolation.Failure is { } isolationFailure) messages.Add("成品隔离迁移未完成，实际位置已保留：" + isolationFailure.Message);
            }
            result = result with { Status = paths.Length > 0 && result.Image is null && result.Video is null ? "failed"
                    : result.Status is "success" or "copied" or "healthy" or "warning" ? "warning" : result.Status,
                Message = result.Message + Environment.NewLine + string.Join(Environment.NewLine, messages) +
                    Environment.NewLine + string.Join(Environment.NewLine, new[] { result.Image, result.Video }.Where(path => path is not null)),
                Limitations = result.Limitations.Concat(messages.Select(message => new RepairLimitation("workspace_cleanup", message))).ToArray() };
        }
        batch.RecordPublished(result.Image, result.Video);
        result = batch.ExplainTrackingFailure(result);
        if (result.Actions.Length == 0)
            result = result with { Actions = ActionResults(options, 0, 0, result.Status == "planned",
                result.Status is "healthy" or "copied" ? "not_needed" : "not_checked", video) };
        return EnsureRecorded(source, options, result);
    }

    internal static RepairResult EnsureRecorded(string source, RepairOptions options, RepairResult result)
    {
        if (result.HistoryRecorded != null) return result;
        return result with { HistoryRecorded = RecordRepairOutcome(source, options, result) };
    }

    internal static RepairActionResult[] ActionResults(RepairOptions options, uint changed, uint pending, bool planned,
        string unchanged = "not_needed", bool videoOnly = false)
    {
        var selected = new[] { options.FixImageRotation && !videoOnly, options.StripImageThumbnail && !videoOnly,
            options.FixHeicOrientation && !videoOnly, options.FixVideoRotation };
        string[] names = ["image_orientation", "thumbnail", "heif_orientation", "video_orientation"];
        string[] reasons = ["lossless_transform_unavailable", "thumbnail_ownership_unverified", "heif_alignment_unverified", "video_direction_unverified"];
        string[] explanations = ["照片方向未完成无损修复，原方向保留。", "缩略图清理未完成，未确认可删除的内容保留。",
            "HEIF 方向对齐未完成，原有显示信息保留。", "视频方向检查或转正未完成，原视频保留。"];
        return Enumerable.Range(0, 4).Select(i => new RepairActionResult(names[i], !selected[i] ? "not_selected"
            : (pending & (1u << i)) != 0 ? "not_completed" : (changed & (1u << i)) != 0 ? planned ? "planned" : "completed" : unchanged,
            selected[i] && (pending & (1u << i)) != 0 ? reasons[i] : null,
            selected[i] && (pending & (1u << i)) != 0 ? explanations[i] : "")).ToArray();
    }

    private static RepairLimitation[] PairLimitations(bool missing, bool cover, bool metadata)
    {
        var reasons = new List<RepairLimitation>();
        if (missing) reasons.Add(new("companion_missing", "未找到确认配对的视频，无法完成整组实况检查。"));
        if (cover) reasons.Add(new("cover_time_unknown", "来源未提供可确认的封面时间，未猜测重建封面。"));
        if (metadata) reasons.Add(new("video_preservation_unverified", "视频已转正，但部分来源媒体信息未完成保留验证。"));
        return reasons.ToArray();
    }

    private static RepairPublishedOutput[] CreatePublishedOutputs(RepairResult result)
    {
        var outputs = new List<RepairPublishedOutput>();
        void Add(string role, string? path, string? member)
        {
            if (path == null) return;
            bool copy = member == "copied" || result.Status == "copied" ||
                (result.Actions.Length != 0 && result.Actions.All(x => x.State is "not_selected" or "not_needed" or "not_completed"));
            outputs.Add(new(role, System.IO.Path.GetFullPath(path), result.IsolationDirectory is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(result.IsolationDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? copy ? "isolated_copy" : "isolated_repaired" : copy ? "copy" : "repaired"));
        }
        Add("image", result.Image, result.ImageStatus); Add("video", result.Video, result.VideoStatus);
        return outputs.ToArray();
    }

    internal static string DescribeResult(RepairResult result)
    {
        string[] names = ["照片方向", "缩略图清理", "HEIF 方向对齐", "视频方向"];
        var lines = new List<string> { Text("Repair_Outcome_" + result.OutcomeKey, result.OutcomeKey) };
        for (int i = 0; i < result.Actions.Length; ++i)
        {
            var action = result.Actions[i];
            string state = Text("Repair_ActionState_" + action.State, action.State);
            lines.Add(Text("Repair_ActionName_" + action.Action, names[i]) + "：" + state + " " + (action.ReasonCode == null ? action.Explanation : Text("Repair_Reason_" + action.ReasonCode, action.Explanation)));
        }
        if (result.Status is "failed" or "skipped") lines.Add(result.Message);
        foreach (var reason in result.Limitations)
            lines.Add(Text("Repair_RemainingReason", "未完成／待确认原因：") + Text("Repair_Reason_" + reason.ReasonCode, reason.Explanation));
        if (result.Status == "warning" && result.Limitations.Length == 0 && !result.Actions.Any(x => x.ReasonCode != null))
            lines.Add(Text("Repair_RemainingReason", "未完成／待确认原因：") + result.Message);
        if (result.PublishedOutputs.Length == 0) lines.Add(Text("Repair_NoOutput", "输出：无，本次未生成文件。"));
        foreach (var output in result.PublishedOutputs) lines.Add(Text("Repair_OutputKind_" + output.Kind, output.Kind) + "：" + output.Path);
        if (result.HistoryRecorded == false) lines.Add(Text("Repair_HistoryMissing", "本项日志未保存；上面的实际媒体处理结果不变。"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static bool RecordRepairOutcome(string source, RepairOptions options, RepairResult result)
    {
        try
        {
            string record = "LPB_REPAIR_OUTCOME_V1 " + JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow,
                source = Path.GetFullPath(source), options, status = result.Status, completion = result.Completion,
                actions = result.Actions, reasons = result.Limitations, outputs = result.PublishedOutputs, diagnostic = result.Message });
            return LogService.AppendRepairOutcome(record);
        }
        catch (Exception ex) { LogService.Repair("Repair outcome could not be recorded: " + ex.Message, LogLevel.Warning); return false; }
    }
}
