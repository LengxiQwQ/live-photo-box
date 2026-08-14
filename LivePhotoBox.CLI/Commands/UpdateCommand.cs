using System;
using System.CommandLine;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Cli.Commands
{
    // update-check — 专用更新检查命令；联网逻辑见 UpdateCheckService
    internal static class UpdateCommand
    {
        public static Command Create()
        {
            var cmd = new Command("update-check", "Check if a newer version is available on GitHub");
            cmd.SetHandler(async context =>
            {
                try
                {
                    // 状态行先打印，联网期间终端有反馈；重试反馈由服务内 \r 覆盖
                    UpdateCheckService.BeginCheck();
                    var result = await UpdateCheckService.CheckAsync(
                        onRetry: UpdateCheckService.WriteCheckRetry);
                    context.ExitCode = PrintResult(result);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[Update] Update check failed: {ex.Message}", ex, LogSource.System);
                    CliConsole.WriteErrorLine($"Update check failed: {ex.Message}");
                    context.ExitCode = 1;
                }
            });
            return cmd;
        }

        // update — 检查 + 询问 + 自动替换（便携版）或静默重装（安装版）
        public static Command CreateUpdate()
        {
            var yesOption = new Option<bool>(
                "--yes", "Skip the confirmation prompt and update automatically");
            yesOption.AddAlias("-y");

            var cmd = new Command("update",
                "Check for a newer version and update this copy automatically");
            cmd.AddOption(yesOption);

            cmd.SetHandler(async context =>
            {
                var yes = context.ParseResult.GetValueForOption(yesOption);
                try
                {
                    context.ExitCode = await SelfUpdateService.RunAsync(yes);
                }
                catch (Exception ex)
                {
                    LogService.Error($"[Update] Update failed: {ex.Message}", ex, LogSource.System);
                    CliConsole.WriteErrorLine($"Update failed: {ex.Message}");
                    context.ExitCode = 1;
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
