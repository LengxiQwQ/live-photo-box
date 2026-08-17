/*
 * VersionInfo.cs
 *
 * 版本与环境信息：只读本地数据，不联网、不启动子进程。
 *
 *   - --version（快速）与 --info（完整）共用同一套字段
 *   - 提供产品名、版本号、构建日期、仓库/反馈入口等信息
 */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Cli.Infrastructure
{
    internal static class VersionInfo
    {
        // 仓库与反馈入口：info 尾部展示（Author/Email/QQ 等联系方式只在 README 提供）
        private const string ProjectUrl = "https://github.com/lengxiqwq/live-photo-box";
        private const string IssuesUrl = "https://github.com/lengxiqwq/live-photo-box/issues";
        public const string Copyright = "© 2026 LengxiQwQ · Licensed under GPL-3.0";

        // 用户可见的产品名（带空格、首字母大写，CLI 加 CLI 后缀）；命令本身保持全小写 livephotobox / lpb
        public static string DisplayName => "Live Photo Box CLI";

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

        // --version：只报版本号，单行，供脚本/用户快速获取。
        // 单行时版本号保持默认色（彩色只会加负担），产品名保留品牌色。
        public static void PrintVersion()
        {
            CliConsole.Write($"{DisplayName} ", CliConsole.TitleRed);
            Console.WriteLine($"v{GetDisplayVersion()}");
        }

        // info：环境信息头部 + 核心字段（外部工具与页脚由调用方追加）。
        // 首行版本号与 --version 一致为默认色，保持两命令视觉统一。
        public static void PrintFull()
        {
            CliConsole.Write($"{DisplayName} ", CliConsole.TitleRed);
            Console.Write($"v{GetDisplayVersion()}");
            Console.WriteLine(" — environment");
            Console.WriteLine();
            PrintCoreFields();
        }

        // info 页脚：仓库 + 反馈入口 + 版权（放最后）
        public static void PrintFooter()
        {
            Console.WriteLine();
            CliConsole.WriteField("Repository", ProjectUrl, width: 11);
            CliConsole.WriteField("Feedback", IssuesUrl, width: 11);
            Console.WriteLine();
            Console.WriteLine(Copyright);
        }

        private static void PrintCoreFields()
        {
            CliConsole.WriteField("Build date", GetBuildDate(), width: 11);
            CliConsole.WriteField("Runtime", GetRuntime(), width: 11);
            CliConsole.WriteField("Platform", GetPlatform(), width: 11);
            CliConsole.WriteField("Channel", InstallChannelDetector.GetChannelDisplay(), width: 11);
            CliConsole.WriteFieldRgb("Location", GetInstallDirectory(), width: 11, valueColor: CliConsole.PathGreen);
        }
    }
}
