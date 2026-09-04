/*
 * InfoCommand.cs
 *
 * --info 全局选项：打印本地环境报告（公共字段 + LivePhotoBox.Native 引擎状态），不联网。
 *
 *   - 打印版本、日志路径、Native 引擎状态（ABI 版本与功能支持）
 *   - 不联网，更新检查交由 update-check 命令
 */

using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Interop;
using LivePhotoBox.Services;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands
{
    internal static class InfoCommand
    {
        public static Task<int> RunAsync()
        {
            VersionInfo.PrintFull();
            PrintLogInfo();
            Console.WriteLine();
            PrintNativeEngineInfo();
            VersionInfo.PrintFooter();
            return Task.FromResult(0);
        }

        // 日志位置：非打包固定 %LOCALAPPDATA%\LivePhotoBox\Logs（CLI 为子目录 CLI）。
        // 日志文件头部已含 OS/Runtime/CPU/内存/语言等系统信息（见 LogService.LogSystemInfo），
        // 排查时让用户把该文件发来即可，故 --info 只给路径入口，不重复打印那些字段。
        private static void PrintLogInfo()
        {
            CliConsole.WriteFieldRgb("Log dir", LogService.LogDirectory, width: 11, valueColor: CliConsole.PathGreen);
            var logFile = LogService.CurrentLogPath;
            var logName = string.IsNullOrEmpty(logFile) ? "n/a" : Path.GetFileName(logFile);
            CliConsole.WriteFieldRgb("Log file", logName, width: 11, valueColor: CliConsole.PathGreen);
        }

        private static void PrintNativeEngineInfo()
        {
            CliConsole.WriteLine("Native engine:", CliConsole.Accent);
            var info = NativeRuntime.Probe();
            if (info.IsAvailable)
            {
                CliConsole.WriteField("Status", "Available", width: 15, valueColor: CliConsole.Success);
                CliConsole.WriteField("ABI Version", info.AbiVersion.ToString(), width: 15);
                if (!string.IsNullOrEmpty(info.Version))
                    CliConsole.WriteField("Engine Version", info.Version, width: 15);
            }
            else
            {
                CliConsole.WriteField("Status", "Unavailable", width: 15, valueColor: CliConsole.Error);
                if (!string.IsNullOrEmpty(info.Diagnostic))
                    CliConsole.WriteField("Diagnostic", info.Diagnostic, width: 15, valueColor: CliConsole.Muted);
            }
        }
    }
}
