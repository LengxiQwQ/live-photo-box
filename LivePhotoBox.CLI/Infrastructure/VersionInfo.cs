using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Cli.Infrastructure
{
    // 版本与环境信息 — 只读本地数据，不联网、不启动子进程。
    // --version（快速）与 info（完整）共用同一套字段。
    internal static class VersionInfo
    {
        private const string ProjectUrl = "https://github.com/lengxiqwq/live-photo-box";
        private const string License = "GPL-3.0";
        private const string IssuesUrl = "https://github.com/lengxiqwq/live-photo-box/issues";

        // 用户可见的产品名（带空格、首字母大写）；命令本身保持全小写 livephotobox / lpb
        public static string DisplayName => "Live Photo Box";
        public static string Author => "LengxiQwQ (冷汐OωO)";
        public static string Email => "lengxiowo@gmail.com";
        public static string QQ => "3197635836";
        public const string Copyright = "© 2026 LengxiQwQ · Licensed under GPL-3.0";

        // 2.1.4.0 → 2.1.4；Revision 非零时保留完整四位
        public static string GetDisplayVersion()
        {
            var ver = Assembly.GetEntryAssembly()?.GetName().Version;
            if (ver is null || ver.Major == 0 && ver.Minor == 0 && ver.Build == 0)
                return "dev";

            return ver.Revision == 0
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
                : $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
        }

        // 编译期注入的构建日期（yyyy-MM-dd，UTC）
        public static string GetBuildDate()
        {
            var attrs = Assembly.GetEntryAssembly()
                ?.GetCustomAttributes<AssemblyMetadataAttribute>()
                ?? Array.Empty<AssemblyMetadataAttribute>();

            var value = attrs.FirstOrDefault(a => a.Key == "BuildDate")?.Value;
            return string.IsNullOrEmpty(value) ? "n/a" : value;
        }

        public static string GetRuntime() =>
            $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";

        public static string GetPlatform() =>
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";

        // 当前副本所在目录（安装目录 / 便携解压目录），去掉结尾分隔符
        public static string GetInstallDirectory() =>
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        // --version：快版
        public static void PrintVersion()
        {
            CliConsole.Write($"{DisplayName} ", CliConsole.TitleRed);
            CliConsole.WriteLine($"v{GetDisplayVersion()}", CliConsole.Highlight);
            Console.WriteLine();
            PrintCoreFields();
            Console.WriteLine();
            WriteTipField();
            Console.WriteLine();
            Console.WriteLine(Copyright);
        }

        // Tip 行：命令示例 'lpb info' 用紫色渲染（与 update 提示一致），其余保持默认色
        private static void WriteTipField()
        {
            if (CliConsole.UseColor)
            {
                CliConsole.Write("Tip".PadRight(11), CliConsole.Accent);
                Console.Write(": run '");
                CliConsole.Write("lpb info", CliConsole.CommandPurple);
                Console.WriteLine("' for full details");
            }
            else
            {
                Console.WriteLine($"{"Tip".PadRight(11)}: run 'lpb info' for full details");
            }
        }

        // info：完整版头部 + 公共字段（外部工具与提示由调用方追加）
        public static void PrintFull()
        {
            CliConsole.Write($"{DisplayName} ", CliConsole.TitleRed);
            CliConsole.Write($"v{GetDisplayVersion()}", CliConsole.Highlight);
            Console.WriteLine(" — full environment info");
            Console.WriteLine();
            PrintCoreFields();
        }

        private static void PrintCoreFields()
        {
            CliConsole.WriteField("Build date", GetBuildDate(), width: 11);
            CliConsole.WriteField("Runtime", GetRuntime(), width: 11);
            CliConsole.WriteField("Platform", GetPlatform(), width: 11);
            CliConsole.WriteField("Channel", InstallChannelDetector.GetChannelDisplay(), width: 11);
            CliConsole.WriteFieldRgb("Location", GetInstallDirectory(), width: 11, valueColor: CliConsole.PathGreen);
            CliConsole.WriteField("Project", ProjectUrl, width: 11);
            CliConsole.WriteField("License", License, width: 11);
            CliConsole.WriteField("Author", Author, width: 11);
            CliConsole.WriteField("Email", Email, width: 11);
            CliConsole.WriteField("QQ", QQ, width: 11);
            CliConsole.WriteField("Feedback", IssuesUrl, width: 11);
        }
    }
}
