/*
 * UpdateCommand.cs
 *
 * 更新相关命令：update-check（专用更新检查）与 update（检查 + 询问 + 自动更新）。
 *
 *   - update-check 只做检查，联网逻辑见 UpdateCheckService
 *   - update 便携版自动替换、安装版静默重装，见 SelfUpdateService
 */

using System;
using System.CommandLine;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Cli.Commands
{
    internal static class UpdateCommand
    {
        public static Command Create()
        {
            var cmd = new Command("update-check", "Check if a newer version is available on GitHub");
            cmd.SetAction(async parseResult =>
            {
                try
                {
                    // 状态行先打印，联网期间终端有反馈；重试反馈由服务内 \r 覆盖
                    UpdateCheckService.BeginCheck();
                    var result = await UpdateCheckService.CheckAsync(
                        onRetry: UpdateCheckService.WriteCheckRetry);
                    Environment.ExitCode = PrintResult(result);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[Update] Update check failed: {ex}", ex, LogSource.System);
                    CliConsole.WriteErrorWithHint(
                        $"Error: Update check failed. {UpdateCheckService.DescribeError(ex)}",
                        "Run 'lpb --info' to view the log folder if this keeps happening.");
                    Environment.ExitCode = 1;
                }
            });
            return cmd;
        }

        // update — 检查 + 询问 + 自动替换（便携版）或静默重装（安装版）
        public static Command CreateUpdate()
        {
            var yesOption = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt and update automatically" };
            var cmd = new Command("update",
                "Check for a newer version and update this copy automatically");
            cmd.Add(yesOption);

            cmd.SetAction(async parseResult =>
            {
                var yes = parseResult.GetValue(yesOption);
                try
                {
                    Environment.ExitCode = await SelfUpdateService.RunAsync(yes);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[Update] Update failed: {ex}", ex, LogSource.System);
                    CliConsole.WriteErrorWithHint(
                        $"Error: Update failed. {UpdateCheckService.DescribeError(ex)}",
                        "Try again later, or download the release manually from the GitHub releases page.");
                    Environment.ExitCode = 1;
                }
            });

            return cmd;
        }

        private static int PrintResult(UpdateCheckService.Result result)
        {
            // "Checking GitHub ... " 已在调用前打印，这里用 WriteCheckStatus 补状态与结论（\r 覆盖一致）
            if (!result.Ok)
            {
                LogService.Error($"[Update] Check failed: {result.ErrorMessage}", source: LogSource.System);
                UpdateCheckService.WriteCheckStatus($"unreachable ({result.ErrorMessage})", CliConsole.Error);
                UpdateCheckService.PrintManualDownload();
                CliConsole.WriteHintLine("Check your network connection and try again.");
                return 2;
            }

            LogService.Info($"[Update] Check OK: current v{result.CurrentVersion}" +
                (result.VersionParsed ? $", latest v{result.LatestVersion} (comparison={result.Comparison})" : $", latest tag {result.LatestTag}"),
                source: LogSource.System);
            UpdateCheckService.WriteCheckStatus("OK", CliConsole.Success);

            if (result.VersionParsed)
            {
                if (result.Comparison < 0)
                {
                    CliConsole.Write("A newer version is available: ", CliConsole.Notice);
                    CliConsole.WriteLine($"v{result.CurrentVersion} → v{result.LatestVersion}", CliConsole.Highlight);
                    if (result.ManagedByWinget)
                    {
                        // WinGet 管理的副本不自更新，直接给 winget upgrade 指令
                        Console.Write("Update with: ");
                        CliConsole.WriteLine("winget upgrade LengxiQwQ.LivePhotoBox", CliConsole.CommandPurple);
                    }
                    else
                    {
                        Console.Write("To update automatically: ");
                        CliConsole.WriteLine("lpb update -y", CliConsole.CommandPurple);
                    }
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
                CliConsole.WriteField("Latest release", result.LatestTag!, valueColor: CliConsole.Highlight);
            }

            return 0;
        }
    }
}
