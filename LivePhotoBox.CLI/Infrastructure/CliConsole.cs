using System;
using System.Collections.Generic;
using System.Linq;

namespace LivePhotoBox.Cli.Infrastructure
{
    // 终端着色辅助 — 仅在交互式终端启用颜色；
    // 输出被重定向（管道/文件/CI）或设置了 NO_COLOR 时自动退回纯文本。
    internal static class CliConsole
    {
        // 标签（冒号前的文字）
        public static readonly ConsoleColor Accent = ConsoleColor.Cyan;
        // 版本号 / 小节标题 / 工具版本
        public static readonly ConsoleColor Highlight = ConsoleColor.Yellow;
        // ✅ / 成功信息
        public static readonly ConsoleColor Success = ConsoleColor.Green;
        // 失败 / 错误信息
        public static readonly ConsoleColor Error = ConsoleColor.Red;
        // 提示性文字的海蓝色（RGB 真彩色）
        public static readonly (int R, int G, int B) Notice = (102, 179, 255);
        // ── / 弱化信息
        public static readonly ConsoleColor Muted = ConsoleColor.DarkGray;
        // 软件标题的浅红色（RGB 真彩色，避免 ConsoleColor.Red 的大红）
        public static readonly (int R, int G, int B) TitleRed = (255, 140, 140);
        // Soft grass-green for file/directory paths (same family as Codex).
        public static readonly (int R, int G, int B) PathGreen = (140, 205, 140);
        // Soft purple for command examples shown in help pages.
        public static readonly (int R, int G, int B) CommandPurple = (180, 140, 255);

        public static bool UseColor =>
            !Console.IsOutputRedirected &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        public static void Write(string text, ConsoleColor color)
        {
            if (UseColor)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.Write(text);
                Console.ForegroundColor = old;
            }
            else
            {
                Console.Write(text);
            }
        }

        public static void WriteLine(string text, ConsoleColor color) =>
            Write(text + Environment.NewLine, color);

        public static void WriteLine(string text, (int R, int G, int B) rgb) =>
            Write(text + Environment.NewLine, rgb);

        // 红色错误输出到 stderr（Error/WARN/FAIL 等）
        public static void WriteErrorLine(string text)
        {
            if (!UseColor)
            {
                Console.Error.WriteLine(text);
                return;
            }

            var old = Console.ForegroundColor;
            string trimmed = text.TrimStart();
            int lead = text.Length - trimmed.Length;

            // 只给 "Error:"/"WARN:"/"FAIL" 前缀上色，其余内容保持默认色，
            // 避免报错多时一大片红（WARN 用黄色，Error/FAIL 用红色）。
            bool hasPrefix =
                trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase);

            if (hasPrefix)
            {
                int len;
                ConsoleColor prefixColor;
                if (trimmed.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
                {
                    prefixColor = ConsoleColor.Yellow;
                    len = trimmed.IndexOf(':') + 1;
                }
                else if (trimmed.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase))
                {
                    prefixColor = Error;
                    len = 4; // "FAIL"
                    while (len < trimmed.Length && trimmed[len] == ' ') len++; // 连带标签后的空格
                }
                else
                {
                    prefixColor = Error;
                    len = trimmed.IndexOf(':') + 1; // "Error:" / "ERROR:"
                }

                Console.ForegroundColor = prefixColor;
                Console.Error.Write(text.Substring(0, lead + len));
                Console.ForegroundColor = old;
                Console.Error.WriteLine(text.Substring(lead + len));
            }
            else
            {
                Console.ForegroundColor = Error;
                Console.Error.WriteLine(text);
                Console.ForegroundColor = old;
            }
        }

        // 以 24 位真彩色写入（如浅红标题），不支持或重定向时自动退化为纯文本
        public static void Write(string text, (int R, int G, int B) rgb)
        {
            if (UseColor)
                Console.Write($"\x1b[38;2;{rgb.R};{rgb.G};{rgb.B}m{text}\x1b[0m");
            else
                Console.Write(text);
        }

        // 输出 "标签: 值"，标签上色，冒号与值保持默认色
        public static void WriteField(string label, string value, int width = 0,
            string separator = ": ", ConsoleColor? valueColor = null)
        {
            var padded = width > 0 ? label.PadRight(width) : label;
            if (UseColor)
            {
                Write(padded, Accent);
                Console.Write(separator);
                if (valueColor is { } c) Write(value, c);
                else Console.Write(value);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"{padded}{separator}{value}");
            }
        }

        // Same as WriteField, but the value uses a 24-bit RGB color
        // (e.g. the sea-blue Notice color for highlighted field values).
        public static void WriteFieldRgb(string label, string value, int width = 0,
            string separator = ": ", (int R, int G, int B)? valueColor = null)
        {
            var padded = width > 0 ? label.PadRight(width) : label;
            if (UseColor)
            {
                Write(padded, Accent);
                Console.Write(separator);
                if (valueColor is { } c) Write(value, c);
                else Console.Write(value);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"{padded}{separator}{value}");
            }
        }

        // 为非法输入生成 "Did you mean: ...?" 提示（前缀优先；无前缀匹配且输入 ≥3 字符时用包含匹配；
        // 仍无匹配时用编辑距离 ≤2 的模糊匹配，最多给 3 个候选）
        public static string DidYouMean(string input, IEnumerable<string> validValues)
        {
            string trimmed = input.Trim();
            if (string.IsNullOrEmpty(trimmed)) return "";
            var matches = validValues
                .Where(v => v.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0 && trimmed.Length >= 3)
                matches = validValues
                    .Where(v => v.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            if (matches.Count == 0 && trimmed.Length >= 3)
                matches = validValues
                    .Select(v => (Value: v, Distance: LevenshteinDistance(trimmed.ToLowerInvariant(), v.ToLowerInvariant())))
                    .Where(x => x.Distance <= 2)
                    .OrderBy(x => x.Distance)
                    .Take(3)
                    .Select(x => x.Value)
                    .ToList();
            // 多词值（如 "motion photo"）：输入与其中任意单词接近也算候选，避免长短悬殊匹配不到
            if (matches.Count == 0 && trimmed.Length >= 3)
                matches = validValues
                    .Where(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(w => w.Length >= 3 && LevenshteinDistance(trimmed.ToLowerInvariant(), w.ToLowerInvariant()) <= 2))
                    .Take(3)
                    .ToList();
            return matches.Count > 0 ? $" Did you mean: {string.Join(", ", matches)}?" : "";
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
        }

        // 错误 + 可选纠正提示（提示行弱化为灰色，方便用户一眼区分"哪里错了 / 怎么改"）
        public static void WriteErrorWithHint(string message, string? hint = null)
        {
            WriteErrorLine(message);
            if (!string.IsNullOrEmpty(hint)) WriteHintLine(hint);
        }

        // 弱化提示行（stderr）：用于错误后告诉用户怎么纠正。
        public static void WriteHintLine(string hint)
        {
            if (!UseColor)
            {
                Console.Error.WriteLine($"Hint: {hint}");
                return;
            }
            var old = Console.ForegroundColor;
            Console.ForegroundColor = Muted;
            Console.Error.WriteLine($"Hint: {hint}");
            Console.ForegroundColor = old;
        }
    }
}
