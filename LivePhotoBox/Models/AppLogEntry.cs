using System;
using System.Collections.Generic;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
        Critical = 5
    }

    /// <summary>
    /// 日志来源模块
    /// </summary>
    public enum LogSource
    {
        App,
        Combo,
        Split,
        Repair,
        Scan,
        File,
        Settings,
        UI,
        System
    }

    /// <summary>
    /// 日志条目
    /// </summary>
    public sealed class AppLogEntry
    {
        public DateTimeOffset Timestamp { get; init; }
        public LogLevel Level { get; init; }
        public LogSource Source { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? Details { get; init; }
        public string? ExceptionType { get; init; }
        public string? StackTrace { get; init; }
        public string? FilePath { get; init; }
        public string? OperationId { get; init; }

        public string FormattedMessage => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Source}] {Message}";

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(FormattedMessage);
            if (!string.IsNullOrEmpty(Details))
                sb.AppendLine($"  Details: {Details}");
            if (!string.IsNullOrEmpty(ExceptionType))
                sb.AppendLine($"  ExceptionType: {ExceptionType}");
            if (!string.IsNullOrEmpty(StackTrace))
            {
                sb.AppendLine("  StackTrace:");
                foreach (var line in StackTrace.Split('\n'))
                    sb.AppendLine($"    {line.Trim()}");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 应用状态快照，用于崩溃恢复
    /// </summary>
    public sealed class AppStateSnapshot
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }
        public bool CleanShutdown { get; set; }
        public string CurrentPageTag { get; set; } = string.Empty;
        public string ComboStatus { get; set; } = string.Empty;
        public string SplitStatus { get; set; } = string.Empty;
        public string RepairStatus { get; set; } = string.Empty;
        public bool IsProcessing { get; set; }
        public bool IsPaused { get; set; }
        public int ComboTaskCount { get; set; }
        public int SplitTaskCount { get; set; }
        public int RepairTaskCount { get; set; }
        public double ComboProgress { get; set; }
        public double SplitProgress { get; set; }
        public double RepairProgress { get; set; }
        public string ComboInputDir { get; set; } = string.Empty;
        public string ComboOutputDir { get; set; } = string.Empty;
        public string SplitInputDir { get; set; } = string.Empty;
        public string SplitOutputDir { get; set; } = string.Empty;
        public string RepairInputDir { get; set; } = string.Empty;
        public string RepairOutputDir { get; set; } = string.Empty;
        public int LogCount { get; set; }
        public List<string> RecentMessages { get; set; } = [];
    }
}
