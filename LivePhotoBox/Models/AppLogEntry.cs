using System;
using System.Collections.Generic;

namespace LivePhotoBox.Models
{
    // 日志级别
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
        Critical = 5
    }

    // 日志来源模块
    public enum LogSource
    {
        App,
        Merge,
        Split,
        Repair,
        Scan,
        File,
        Settings,
        UI,
        System,
        History
    }

    // 日志条目 — 表示一条应用程序日志，包含时间、级别、来源、消息及可选异常详情。
    public sealed class AppLogEntry
    {
        // 日志产生的时间戳（UTC+8）
        public DateTimeOffset Timestamp { get; init; }
        // 日志级别（Trace / Debug / Info / Warning / Error / Critical）
        public LogLevel Level { get; init; }
        // 日志来源模块
        public LogSource Source { get; init; }
        // 日志消息正文
        public string Message { get; init; } = string.Empty;
        // 可选的详细描述信息
        public string? Details { get; init; }
        // 异常类型名称（如有）
        public string? ExceptionType { get; init; }
        // 异常堆栈跟踪（如有）
        public string? StackTrace { get; init; }
        // 关联的文件路径
        public string? FilePath { get; init; }
        // 关联的操作标识（用于关联多个日志条目）
        public string? OperationId { get; init; }

        // 格式化后的单行日志文本
        public string FormattedMessage => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Source}] {Message}";

        // 返回完整的多行日志字符串，包含可选的 Details、ExceptionType 和 StackTrace
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

    // 应用状态快照，用于崩溃恢复。保存当前处理状态和各模块进度，以便重启后恢复现场。
    public sealed class AppStateSnapshot
    {
        // 当前会话唯一标识
        public string SessionId { get; set; } = string.Empty;
        // 会话启动时间
        public DateTimeOffset StartedAt { get; set; }
        // 最后更新时间
        public DateTimeOffset LastUpdatedAt { get; set; }
        // 是否正常关闭（非崩溃退出）
        public bool CleanShutdown { get; set; }
        // 当前页面标识
        public string CurrentPageTag { get; set; } = string.Empty;
        // 合并模块状态文本
        public string MergeStatus { get; set; } = string.Empty;
        // 拆分模块状态文本
        public string SplitStatus { get; set; } = string.Empty;
        // 修复模块状态文本
        public string RepairStatus { get; set; } = string.Empty;
        // 是否正在处理中
        public bool IsProcessing { get; set; }
        // 是否已暂停
        public bool IsPaused { get; set; }
        // 合并任务总数
        public int MergeTaskCount { get; set; }
        // 拆分任务总数
        public int SplitTaskCount { get; set; }
        // 修复任务总数
        public int RepairTaskCount { get; set; }
        // 合并进度（0.0 ~ 1.0）
        public double MergeProgress { get; set; }
        // 拆分进度（0.0 ~ 1.0）
        public double SplitProgress { get; set; }
        // 修复进度（0.0 ~ 1.0）
        public double RepairProgress { get; set; }
        // 合并输入目录
        public string MergeInputDir { get; set; } = string.Empty;
        // 合并输出目录
        public string MergeOutputDir { get; set; } = string.Empty;
        // 拆分输入目录
        public string SplitInputDir { get; set; } = string.Empty;
        // 拆分输出目录
        public string SplitOutputDir { get; set; } = string.Empty;
        // 修复输入目录
        public string RepairInputDir { get; set; } = string.Empty;
        // 修复输出目录
        public string RepairOutputDir { get; set; } = string.Empty;
        // 日志条目数量
        public int LogCount { get; set; }
        // 最近几条日志消息（用于快速查看）
        public List<string> RecentMessages { get; set; } = [];
    }
}
