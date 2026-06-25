using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using LivePhotoBox.Services;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 单张照片的操作历史记录
    /// </summary>
    public class FileHistoryInfo
    {
        public string FilePath { get; set; } = string.Empty;

        public string FileName => Path.GetFileName(FilePath);

        /// <summary>文件的简短摘要（协议 + 状态）</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>是否识别为实况照片</summary>
        public bool IsLivePhoto { get; set; }

        /// <summary>检测到的实况照片协议类型描述</summary>
        public string DetectedProtocol { get; set; } = string.Empty;

        /// <summary>是否由 LivePhotoBox 生成</summary>
        public bool IsLivePhotoBoxGenerated { get; set; }

        /// <summary>Merge 协议标识</summary>
        public string MergeProtocol { get; set; } = string.Empty;

        /// <summary>生成时的版本</summary>
        public string MergeVersion { get; set; } = string.Empty;

        /// <summary>时间线条目（按时间排序）</summary>
        public ObservableCollection<HistoryEntry> Entries { get; set; } = new();

        /// <summary>
        /// 历史条目数量（用于 UI 可见性）
        /// </summary>
        public bool HasEntries => Entries.Count > 0;

        /// <summary>是否为 LivePhotoBox 生成 + 有历史记录</summary>
        public bool HasLivePhotoBoxHistory =>
            IsLivePhotoBoxGenerated && Entries.Any(e => e.Action != "Merge");

        /// <summary>条目的 Summary 文本（如 "处理过 2 次"）</summary>
        public string EntryCountText =>
            Entries.Count == 0 ? string.Empty :
            ResourceService.Format("History_EntryCount", Entries.Count);

        /// <summary>图标 Segoe MDL2 字符</summary>
        public string FileTypeIcon => Path.GetExtension(FilePath)?.ToLowerInvariant() switch
        {
            ".heic" or ".heif" => "", // HEIC file icon
            ".jpg" or ".jpeg" => "",
            ".png" => "",
            ".gif" => "",
            _ => "",
        };
    }

    /// <summary>
    /// 单个历史操作条目
    /// </summary>
    public class HistoryEntry
    {
        /// <summary>操作类型: Merge / Split / Repair</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>操作时间</summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>格式化后的时间文本</summary>
        public string TimestampDisplay => Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "——";

        /// <summary>执行操作的 LivePhotoBox 版本</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>详细描述（协议、格式、修复内容等）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>操作类型对应的颜色</summary>
        public string ActionColor => Action switch
        {
            "Merge" => "#4CAF50",    // 绿色
            "Split" => "#2196F3",    // 蓝色
            "Repair" => "#FF9800",   // 橙色
            _ => "#9E9E9E",          // 灰色（未知）
        };

        /// <summary>操作类型对应的 Segoe MDL2 图标</summary>
        public string ActionIcon => Action switch
        {
            "Merge" => "",     // Merge/Combine
            "Split" => "",     // Split
            "Repair" => "",    // Repair
            _ => "",           // Info
        };

        /// <summary>操作类型对应的本地化名称</summary>
        public string ActionDisplayName => Action switch
        {
            "Merge" => ResourceService.GetString("History_Action_Merge"),
            "Split" => ResourceService.GetString("History_Action_Split"),
            "Repair" => ResourceService.GetString("History_Action_Repair"),
            _ => Action,
        };
    }
}
