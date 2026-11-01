using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LivePhotoBox.ViewModels
{
    /// <summary>
    /// 供 MergePage / SplitPage 任务队列区域展示扫描诊断、孤立与冲突文件的适配模型。
    /// 复用现有列表的行样式、状态点颜色与原 ErrorDetailTip 交互，不伪造任务。
    /// </summary>
    public sealed partial class ScanDiagnosticDisplayItem : ObservableObject
    {
        public required string DiagnosticCode { get; init; }
        public required string PrimaryPath { get; init; }
        public IReadOnlyList<string> RelatedPaths { get; init; } = Array.Empty<string>();
        public required string DisplayFileName { get; init; }
        public required string DisplayStatusText { get; init; }
        public required ProcessStatus Status { get; init; }
        public required ScanDiagnosticSeverity Severity { get; init; }
        public required ScanDiagnosticStage Stage { get; init; }
        public required string OutcomeSummary { get; init; }
        public required string Details { get; init; }
        public string? GroupKey { get; init; }
        public string DisplayFileSize { get; init; } = string.Empty;
        public bool IsImage { get; init; }
        public bool IsVideo { get; init; }
        public bool IsDirectory { get; init; }

        public static ScanDiagnosticDisplayItem FromDiagnostic(ScanDiagnostic d)
        {
            string fileName = Path.GetFileName(d.PrimaryPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = d.PrimaryPath;

            bool isDir = d.Stage == ScanDiagnosticStage.Enumeration && Directory.Exists(d.PrimaryPath);
            bool isImg = d.PrimaryPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         d.PrimaryPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         d.PrimaryPath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
                         d.PrimaryPath.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
            bool isVid = d.PrimaryPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                         d.PrimaryPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

            string fileSize = string.Empty;
            if (!isDir && File.Exists(d.PrimaryPath))
            {
                try
                {
                    fileSize = FileSizeFormatter.Format(new FileInfo(d.PrimaryPath).Length);
                }
                catch { }
            }

            ProcessStatus status = d.Severity == ScanDiagnosticSeverity.Error
                ? ProcessStatus.Failed
                : ProcessStatus.Pending;

            string statusText = d.DiagnosticCode switch
            {
                "ConflictAlreadyQueued" or "LivePhotoCannotBeCompanion" => ResourceService.GetString("ScanStatus_Conflict"),
                "NotSelected" or "ProtocolFiltered" or "ReparsePointSkipped" => ResourceService.GetString("ScanStatus_NotSelected"),
                "ConflictMultipleMedia" => ResourceService.GetString("ScanStatus_Conflict") ?? "同名冲突",
                "ConflictDuplicateIdentity" => ResourceService.GetString("ScanStatus_IdentityConflict") ?? "身份冲突",
                "UnmatchedImage" => ResourceService.GetString("ScanStatus_Unmatched") ?? "未配对",
                "UnmatchedVideo" => ResourceService.GetString("ScanStatus_Unmatched") ?? "未配对",
                "DirectoryAccessDenied" or "AccessDenied" => ResourceService.GetString("ScanStatus_AccessDenied") ?? "无权访问",
                "DirectoryNotFound" or "FileNotFound" => ResourceService.GetString("ScanStatus_NotFound") ?? "不存在",
                "UnsupportedExtension" => ResourceService.GetString("ScanStatus_Unsupported") ?? "不支持",
                "InvalidCompanionVideo" or "VideoProbeFailed" => ResourceService.GetString("ScanStatus_VideoError") ?? "视频错误",
                "ImageInspectionFailed" or "SingleFileInspectionFailed" => ResourceService.GetString("ScanStatus_InspectionFailed") ?? "解析失败",

                _ => d.Severity == ScanDiagnosticSeverity.Error
                    ? (ResourceService.GetString("ScanStatus_Failed") ?? "失败")
                    : (ResourceService.GetString("ScanStatus_Unpaired") ?? "未配对")
            };

            string localized = ResourceService.GetString(d.MessageKey);
            string reason = localized == d.MessageKey ? d.Summary : localized;
            string summary = reason + "\n" + d.PrimaryPath;
            if (d.Severity == ScanDiagnosticSeverity.Error && !string.IsNullOrWhiteSpace(d.Summary) && d.Summary != reason)
                summary += "\n" + d.Summary;
            if (d.RawCategory != null) summary += $"\n{d.RawCategory} / {d.RawStage}";
            if (d.RelatedPaths.Count > 0)
                summary += "\n" + ResourceService.GetString("ScanDiagnostic_RelatedFiles") + "\n" +
                           string.Join("\n", d.RelatedPaths.Where(p => !string.Equals(p, d.PrimaryPath, StringComparison.OrdinalIgnoreCase)));

            return new ScanDiagnosticDisplayItem
            {
                DiagnosticCode = d.DiagnosticCode,
                PrimaryPath = d.PrimaryPath,
                RelatedPaths = d.RelatedPaths,
                DisplayFileName = fileName,
                DisplayStatusText = statusText,
                Status = status,
                Severity = d.Severity,
                Stage = d.Stage,
                OutcomeSummary = summary,
                Details = summary,
                GroupKey = d.GroupKey,
                DisplayFileSize = fileSize,
                IsImage = isImg,
                IsVideo = isVid,
                IsDirectory = isDir
            };
        }
    }
}
