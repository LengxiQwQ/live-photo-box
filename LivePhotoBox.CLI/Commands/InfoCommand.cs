/*
 * InfoCommand.cs
 *
 * --info 全局选项：打印本地环境报告（公共字段 + 内置外部工具版本），不联网。
 *
 *   - 打印版本、日志路径、外部工具（exiftool/ffmpeg/jpegtran/heif-dec/heif-enc）版本
 *   - 不联网，更新检查交由 update-check 命令
 */

using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands
{
    internal static class InfoCommand
    {
        public static async Task<int> RunAsync()
        {
            VersionInfo.PrintFull();
            PrintLogInfo();
            Console.WriteLine();
            await PrintExternalToolsAsync();
            VersionInfo.PrintFooter();
            return 0;
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

        private static async Task PrintExternalToolsAsync()
        {
            CliConsole.WriteLine("External tools:", CliConsole.Accent);
            await PrintToolAsync("exiftool", ExternalToolLocator.FindExifTool(), "-ver", s => s.Trim());
            await PrintToolAsync("ffmpeg", ExternalToolLocator.FindFFmpeg(), "-version", ParseFFmpegVersion);
            // jpegtran 无版本输出开关（-version 仅打印用法），直接标注 n/a
            PrintToolNoProbe("jpegtran", ExternalToolLocator.FindJpegTran());
            await PrintToolAsync("heif-dec", ExternalToolLocator.FindHeifDec(), "--version", FirstLine);
            await PrintToolAsync("heif-enc", ExternalToolLocator.FindHeifEnc(), "--version", FirstLine);
        }

        private static void PrintToolNoProbe(string name, string? path)
        {
            WriteToolLine(name, "n/a", string.IsNullOrEmpty(path) ? "not found" : path);
        }

        private static async Task PrintToolAsync(string name, string? path,
            string args, Func<string, string> parse)
        {
            if (string.IsNullOrEmpty(path))
            {
                PrintToolNoProbe(name, path);
                return;
            }

            var version = await ProbeVersionAsync(path, args, parse, timeoutMs: 5000);
            WriteToolLine(name, version, path);
        }

        private static void WriteToolLine(string name, string version, string path)
        {
            if (CliConsole.UseColor)
            {
                CliConsole.Write(name.PadRight(10), CliConsole.Accent);
                CliConsole.Write(version.PadRight(8), CliConsole.Highlight);
                CliConsole.Write(path, CliConsole.PathGreen);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"{name.PadRight(10)}{version.PadRight(8)}{path}");
            }
        }

        private static async Task<string> ProbeVersionAsync(
            string exe, string args, Func<string, string> parse, int timeoutMs)
        {
            try
            {
                using var p = new Process();
                p.StartInfo.FileName = exe;
                p.StartInfo.Arguments = args;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.CreateNoWindow = true;

                if (!p.Start()) return "n/a";

                var outputTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { await outputTask; } catch { }
                    return "timeout";
                }

                var output = await outputTask;
                var text = parse(output).Trim();
                return string.IsNullOrEmpty(text) ? "n/a" : text;
            }
            catch
            {
                return "n/a";
            }
        }

        private static string FirstLine(string output) =>
            output.Split('\n')[0].Trim();

        private static string ParseFFmpegVersion(string output)
        {
            var first = output.Split('\n')[0].Trim();
            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "version")
                    return parts[i + 1];
            }
            return first;
        }
    }
}
