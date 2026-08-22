using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using LivePhotoBox.Services;

namespace LivePhotoBox.Models
{
    // 单张照片的操作历史记录
    public class FileHistoryInfo
    {
        // 文件完整路径
        public string FilePath { get; set; } = string.Empty;

        // 文件名（不含目录）
        public string FileName => Path.GetFileName(FilePath);

        // 文件的简短摘要（协议 + 状态）
        public string Summary { get; set; } = string.Empty;

        // 是否识别为实况照片
        public bool IsLivePhoto { get; set; }

        // 检测到的实况照片协议类型描述
        public string DetectedProtocol { get; set; } = string.Empty;

        // 是否由 LivePhotoBox 生成
        public bool IsLivePhotoBoxGenerated { get; set; }

        // Merge 协议标识
        public string MergeProtocol { get; set; } = string.Empty;

        // 生成时的版本
        public string MergeVersion { get; set; } = string.Empty;

        // 时间线条目（按时间排序）
        public ObservableCollection<HistoryEntry> Entries { get; set; } = new();

        // 历史条目数量（用于 UI 可见性）
        public bool HasEntries => Entries.Count > 0;

        // 是否为 LivePhotoBox 生成且含有非 Merge 的操作历史
        public bool HasLivePhotoBoxHistory =>
            IsLivePhotoBoxGenerated && Entries.Any(e => e.Action != "Merge");

        // 条目的 Summary 文本（如 "处理过 2 次"）
        public string EntryCountText =>
            Entries.Count == 0 ? string.Empty :
            ResourceService.Format("History_EntryCount", Entries.Count);

        // 根据文件扩展名返回对应的 Segoe MDL2 图标字符
        public string FileTypeIcon => Path.GetExtension(FilePath)?.ToLowerInvariant() switch
        {
            ".heic" or ".heif" => "", // HEIC file icon
            ".jpg" or ".jpeg" => "",
            ".png" => "",
            ".gif" => "",
            _ => "",
        };
    }

    // 单个历史操作条目
    public class HistoryEntry
    {
        // 操作类型: Merge / Split / Repair
        public string Action { get; set; } = string.Empty;

        // 操作时间
        public DateTime? Timestamp { get; set; }

        // 格式化后的时间文本
        public string TimestampDisplay => Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "——";

        // 执行操作的 LivePhotoBox 版本
        public string Version { get; set; } = string.Empty;

        // 详细描述（协议、格式、修复内容等）
        public string Description { get; set; } = string.Empty;

        // 结构化的详细信息（机器可读，Key=Value; 解析而来）
        public System.Collections.Generic.Dictionary<string, string> Details { get; set; } =
            new(System.StringComparer.OrdinalIgnoreCase);

        // 操作类型对应的颜色
        public string ActionColor => Action switch
        {
            "Cover" => "#E91E63",    // 粉色
            "Merge" => "#4CAF50",    // 绿色
            "Split" => "#2196F3",    // 蓝色
            "Repair" => "#FF9800",   // 橙色
            _ => "#9E9E9E",          // 灰色（未知）
        };

        // 操作类型对应的 Segoe MDL2 图标
        public string ActionIcon => Action switch
        {
            "Cover" => "\uE8B9",     // Picture
            "Merge" => "",     // Merge/Combine
            "Split" => "",     // Split
            "Repair" => "",    // Repair
            _ => "",           // Info
        };

        // 操作类型对应的本地化名称
        public string ActionDisplayName => Action switch
        {
            "Cover" => ResourceService.GetString("History_Action_Cover"),
            "Merge" => ResourceService.GetString("History_Action_Merge"),
            "Split" => ResourceService.GetString("History_Action_Split"),
            "Repair" => ResourceService.GetString("History_Action_Repair"),
            _ => Action,
        };
    }
}
