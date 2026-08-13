using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands
{
    // info — 完整环境报告：公共字段 + 内置外部工具版本 + 尾部联网更新检查（与 update-check 共用一套逻辑）。
    internal static class InfoCommand
    {
        public static Command Create()
        {
            var cmd = new Command("info",
                "Show version, environment and bundled external tool versions");
            cmd.SetHandler(async () =>
            {
                VersionInfo.PrintFull();
                Console.WriteLine();
                await PrintExternalToolsAsync();
                Console.WriteLine();
                CliConsole.WriteLine("Update check:", CliConsole.Accent);
                await PrintUpdateCheckAsync();
                Console.WriteLine();
                Console.WriteLine(VersionInfo.Copyright);
            });
            return cmd;
        }

        private static async Task PrintUpdateCheckAsync()
        {
            // 状态行先打印，联网期间终端有反馈；重试反馈由服务内 \r 覆盖
            UpdateCheckService.BeginCheck();
            var result = await UpdateCheckService.CheckAsync(
                onRetry: UpdateCheckService.WriteCheckRetry);

            if (result.ManagedByWinget)
            {
                UpdateCheckService.WriteCheckStatus("skipped", CliConsole.Notice);
                CliConsole.WriteLine(
                    "This copy is installed and managed by WinGet.", CliConsole.Notice);
                Console.WriteLine("Built-in update is disabled for WinGet-managed installs.");
                Console.WriteLine("Update with: winget upgrade LengxiQwQ.LivePhotoBox");
                return;
            }

            if (!result.Ok)
            {
                UpdateCheckService.WriteCheckStatus($"unreachable ({result.ErrorMessage})", CliConsole.Error);
                UpdateCheckService.PrintManualDownload();
                return;
            }

            UpdateCheckService.WriteCheckStatus("OK", CliConsole.Success);

            if (result.VersionParsed)
            {
                if (result.Comparison < 0)
                {
                    CliConsole.Write("A newer version is available: ", CliConsole.Notice);
                    CliConsole.WriteLine($"v{result.CurrentVersion} → v{result.LatestVersion}", CliConsole.Highlight);
                    Console.Write("To update automatically: ");
                    CliConsole.WriteLine("lpb update -y", CliConsole.CommandPurple);
                }
                else if (result.Comparison == 0)
                {
                    CliConsole.WriteLine("You are running the latest version.", CliConsole.Notice);
                }
                else
                {
                    CliConsole.WriteLine(
                        "You are running a pre-release or development build (newer than the latest stable release).",
                        CliConsole.Notice);
                }
            }
            else
            {
                CliConsole.Write("Latest release: ", CliConsole.Accent);
                CliConsole.WriteLine(result.LatestTag!, CliConsole.Highlight);
            }
        }

        private static async Task PrintExternalToolsAsync()
        {
            Console.WriteLine("External tools:");
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
