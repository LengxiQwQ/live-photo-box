using System;

namespace LivePhotoBox.Cli.Infrastructure
{
    // 终端进度条渲染器 — 只在交互终端（未重定向）时原地 \r 重写；
    // 管道/CI 输出自动降级为纯文本，不产生任何 \r / ANSI 转义。
    // 交互形如 "Downloading: [████░░░░] 45%  1.2 MB/s"：标签 + 进度条同行；
    // 走完 100% 后调用 Finish 换行，进度条留在屏幕不消失。
    internal static class ConsoleProgress
    {
        private static string _label = "";
        private static int _lastPct = -1;
        private static DateTime _lastRenderUtc = DateTime.MinValue;
        private static int _lastWidth;

        // 能否原地重写（\r）：仅交互终端。颜色另外用 CliConsole.UseColor 门控（NO_COLOR 下仍动画但不带色）。
        public static bool Animate => !Console.IsOutputRedirected;

        /// <summary>
        /// 开始一段进度。label 为无冒号的标签词（如 "Downloading"）；
        /// 交互打印 "label: "（不换行，等进度条跟上），重定向打印 "label ..."（一行纯文本）。
        /// </summary>
        public static void Begin(string label)
        {
            _label = label;
            _lastPct = -1;
            _lastRenderUtc = DateTime.MinValue;
            if (Animate)
                Console.Write(label + ": ");
            else
                Console.WriteLine(label + " ...");
        }

        /// <summary>渲染一行进度条，形如 "Downloading: [████░░░░] 45%  1.2 MB/s"。</summary>
        public static void Render(bool known, long done, long total, double mibPerSec, bool force = false)
        {
            if (!Animate || !known) return;

            var pct = (int)Math.Clamp(done * 100.0 / Math.Max(total, 1), 0, 100);

            // 节流：约每 2% 或 100ms 渲染一次
            var now = DateTime.UtcNow;
            if (!force && pct - _lastPct < 2 && (now - _lastRenderUtc).TotalMilliseconds < 100)
                return;
            _lastPct = pct;
            _lastRenderUtc = now;

            var prefix = _label + ": ";
            // 预留后缀 " 100%  999.9 MB/s"（18）+ 两端方括号（2）
            int barLen = Math.Clamp(SafeWindowWidth() - prefix.Length - 20, 10, 80);
            int filled = (int)(barLen * pct / 100.0);

            var filledBlock = new string('█', filled);
            var emptyBlock = new string('░', barLen - filled);
            // 黑白配色：填充段白色、剩余段灰色（ANSI 不影响可见宽度）
            var bar = CliConsole.UseColor
                ? $"\x1b[38;2;255;255;255m{filledBlock}\x1b[38;2;128;128;128m{emptyBlock}\x1b[0m"
                : filledBlock + emptyBlock;

            var suffix = $" {pct,3}%  {mibPerSec,6:F1} MB/s";
            var line = $"\r{prefix}[{bar}]{suffix}";

            // 按可见宽度补齐，保证较短渲染能覆盖较长上一帧
            var visibleLen = prefix.Length + 1 + barLen + 1 + suffix.Length;
            var target = prefix.Length + barLen + 20;
            if (visibleLen < target)
                line += new string(' ', target - visibleLen);
            _lastWidth = target;

            Console.Write(line);
        }

        /// <summary>
        /// 完成：交互终端把当前进度行重绘为完成态（如 "Downloaded: [████] 100%"），
        /// 不再显示下载速度；重定向/管道保持无输出。
        /// </summary>
        public static void Finish(string doneLabel)
        {
            if (Animate)
            {
                // 完成态标签（"Downloaded"）比进行中标签（"Downloading"）短 1 位，
                // 在冒号后补空格，让进度条起点与下载中那一行对齐。
                var pad = Math.Max(0, _label.Length - doneLabel.Length);
                var prefix = doneLabel + ": " + new string(' ', pad);
                int barLen = Math.Clamp(SafeWindowWidth() - prefix.Length - 20, 10, 80);
                var bar = CliConsole.UseColor
                    ? $"\x1b[38;2;255;255;255m{new string('█', barLen)}\x1b[0m"
                    : new string('█', barLen);
                var suffix = " 100%";
                var line = $"\r{prefix}[{bar}]{suffix}";
                // 按目标宽度补白，覆盖上一帧残留的速度后缀
                var target = prefix.Length + barLen + 20;
                var visibleLen = prefix.Length + 1 + barLen + 1 + suffix.Length;
                if (visibleLen < target)
                    line += new string(' ', target - visibleLen);
                Console.Write(line);
                Console.WriteLine();
            }
            _lastPct = -1;
        }

        /// <summary>打印重试提示：清掉当前条后，把提示写到条所在行（换行）。</summary>
        public static void ShowRetryMessage(string text)
        {
            if (!Animate)
            {
                CliConsole.WriteErrorLine(text);
                return;
            }
            ClearBar();
            Console.WriteLine(text);
            _lastPct = -1;
        }

        /// <summary>清空当前进度条行（光标回到行首）。</summary>
        public static void ClearBar()
        {
            if (!Animate) return;
            Console.Write("\r" + new string(' ', Math.Max(_lastWidth, 1)) + "\r");
            _lastPct = -1;
        }

        private static int SafeWindowWidth()
        {
            try
            {
                var w = Console.WindowWidth;
                return w > 0 ? w : 80;
            }
            catch
            {
                return 80; // 非 tty / 异常宿主
            }
        }
    }
}
