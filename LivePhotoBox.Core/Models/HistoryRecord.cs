using System;
using System.Collections.Generic;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 单条历史操作记录（Core 统一解析结果）。
    /// 由 XmpMarkerService.ParseHistoryEntry 从 dc:subject 条目字符串解析而来，
    /// GUI 历史页与 CLI 共用同一份格式事实源。
    /// </summary>
    public sealed class HistoryRecord
    {
        /// <summary>操作类型：Merge / Split / Repair / Cover 等。</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>操作时间（轻量条目没有时间，为 null）。</summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>执行操作的 LivePhotoBox 版本（3 段）。</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>结构化详情（Key=Value，大小写不敏感；已按转义规则还原）。</summary>
        public Dictionary<string, string> Details { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>人类可读描述（本地化，由 Core 统一构建）。</summary>
        public string Description { get; set; } = string.Empty;
    }
}
