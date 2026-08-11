using System;

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
            if (UseColor)
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = Error;
                Console.Error.WriteLine(text);
                Console.ForegroundColor = old;
            }
            else
            {
                Console.Error.WriteLine(text);
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
    }
}
