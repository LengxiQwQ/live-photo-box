using System;
using System.CommandLine;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Infrastructure;

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
                    context.ExitCode = PrintResult(await UpdateCheckService.CheckAsync());
                }
                catch (Exception ex)
                {
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
                    CliConsole.WriteErrorLine($"Update failed: {ex.Message}");
                    context.ExitCode = 1;
                }
            });

            return cmd;
        }

        private static int PrintResult(UpdateCheckService.Result result)
        {
            Console.Write("Checking GitHub ... ");

            // WinGet 管理的安装：内置更新禁用，交给 winget 管理
            if (result.ManagedByWinget)
            {
                CliConsole.WriteLine("skipped", CliConsole.Notice);
                Console.WriteLine();
                CliConsole.WriteLine(
                    "This copy is installed and managed by WinGet.", CliConsole.Notice);
                Console.WriteLine("Built-in update is disabled for WinGet-managed installs.");
                Console.WriteLine("Update with: winget upgrade LengxiQwQ.LivePhotoBox");
                return 3;
            }

            if (!result.Ok)
            {
                CliConsole.WriteLine($"unreachable ({result.ErrorMessage})", CliConsole.Error);
                Console.WriteLine($"Visit {UpdateCheckService.ReleasesPageUrl} to check manually.");
                return 2;
            }

            CliConsole.WriteLine("OK", CliConsole.Success);
            Console.WriteLine();

            if (result.VersionParsed)
            {
                if (result.Comparison < 0)
                {
                    CliConsole.Write("A newer version is available: ", CliConsole.Notice);
                    CliConsole.WriteLine($"v{result.CurrentVersion} → v{result.LatestVersion}", CliConsole.Highlight);
                    Console.WriteLine(UpdateCheckService.ReleasesPageUrl);
                    Console.WriteLine();
                    Console.WriteLine("To update automatically:");
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
                CliConsole.WriteField("Latest release", result.LatestTag!, valueColor: CliConsole.Highlight);
                Console.WriteLine(UpdateCheckService.ReleasesPageUrl);
            }

            return 0;
        }
    }
}
