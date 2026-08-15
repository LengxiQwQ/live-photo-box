using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            // UTF-8 console output
            Console.OutputEncoding = Encoding.UTF8;

            // Fast --version / -v: local-only info, no network or subprocesses.
            // `-v` is a root-level alias; subcommands keep their own `-v` (e.g. merge --verbose).
            if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
            {
                VersionInfo.PrintVersion();
                return 0;
            }

            // Initialize ResourceService — disk .resw first, embedded English fallback
            InitializeResourceService();

            // Initialize logging — CLI logs into its own subdirectory with a `cli` prefix,
            // keeping them distinct from the GUI's `app-*.log` files in the same Logs root.
            LogService.Initialize(subDirectory: "CLI", logFilePrefix: "cli");

            // --info: detailed local environment report (no network, no subprocesses for version info).
            // Mirrors the --version fast path; registered as a global option, not a subcommand.
            // 命令级日志：记录本次调用的完整命令行（可重放）与退出码、总耗时，让日志能还原现场。
            // try/finally：退出前 flush 日志 + 写 CLEAN SHUTDOWN 标记。
            // 没有 finally 这一步，Info 级日志（Scan/Merge 等）要等 5s 后台定时器才写盘，
            // 短命令进程一退出队列就全丢 —— 之前的 cli-*.log 全是 613 字节"空壳"就是这个原因。
            int exitCode = 0;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                LogService.Info($"CLI invoked: {FormatCommandLine(args)}", LogSource.System);

                if (args.Length == 1 && args[0] == "--info")
                {
                    exitCode = await InfoCommand.RunAsync();
                }
                else
                {
                    // Build command tree
                    var root = new RootCommand(
                        "Convert images and videos into phone-compatible live photos.\n\n" +
                        "Quick start:\n" +
                        "  lpb protocols                       List what formats are available\n" +
                        "  lpb merge photo.jpg video.mp4       Convert one pair\n" +
                        "  lpb merge -d ./Photos -p huawei -y  Batch convert a folder\n" +
                        "  lpb repair photo.jpg                Fix live photo metadata\n" +
                        "  lpb repair -d ./Photos -y           Batch fix a folder\n" +
                        "  lpb --info                          Show detailed environment info")
                    {
                        MergeCommand.Create(),
                        SplitCommand.Create(),
                        RepairCommand.Create(),
                        ProtocolsCommand.Create(),
                        UpdateCommand.Create(),
                        UpdateCommand.CreateUpdate()
                    };

                    // Declare --info so `lpb --help` lists it (the fast path above handles actual invocation).
                    root.AddOption(new Option<bool>("--info",
                        "Show detailed environment info (build date, runtime, platform, channel, location, bundled tools)"));

                    // UseDefaults() 展开为下面的链，但默认的 --version 选项只有 --version 一个别名。
                    // 换成 UseVersionOption("--version", "-v") 让 `--help` 也列出 `-v`。
                    // 快路径（上面）仍优先处理单独的 `-v` / `--version`：更快、不产生日志副作用、输出保持 "Live Photo Box CLI vX.Y.Z"。
                    var builder = new CommandLineBuilder(root)
                        .UseVersionOption("--version", "-v")
                        .UseHelp()
                        .UseEnvironmentVariableDirective()
                        .UseParseDirective()
                        .UseSuggestDirective()
                        .RegisterWithDotnetSuggest()
                        .UseTypoCorrections()
                        .UseParseErrorReporting()
                        .UseExceptionHandler(OnUnhandledException)
                        .CancelOnProcessTermination()
                        .UseHelpBuilder(context => new GroupedHelpBuilder(context.Console))
                        .Build();

                    exitCode = await builder.InvokeAsync(args);
                }
            }
            finally
            {
                // 收尾 flush：命令级退出信息写盘 + CLEAN SHUTDOWN 标记。--version 快路径未初始化日志，此处无副作用。
                LogService.Info($"CLI exit code: {exitCode}, elapsed: {stopwatch.Elapsed.TotalSeconds:F2}s", LogSource.System);
                LogService.MarkCleanShutdown();
            }
            return exitCode;
        }

        // 未处理异常（命令处理器抛出的、未被内部 catch 捕获的）→ 记录进日志（含堆栈）+ 输出到 stderr。
        // 退出码由异常处理器中间件置 1；随后 Main 的 finally 会再写一条退出码日志 + CLEAN SHUTDOWN。
        private static void OnUnhandledException(Exception exception, InvocationContext context)
        {
            LogService.Error($"Unhandled CLI exception: {exception.Message}", exception, LogSource.System);
            context.Console.Error.Write($"Unhandled exception: {exception.Message}");
        }

        // 把命令行参数拼成一行可重放的文本（含空格的参数加引号），写进日志便于事后还原现场。
        private static string FormatCommandLine(string[] args)
        {
            return string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }

        private static void InitializeResourceService()
        {
            // CLI is English-only. The complete English .resw is embedded in
            // LivePhotoBox.Core.dll at build time — no disk .resw needed.
            ResourceService.SetProvider(new ReswResourceProvider(reswDir: ""));
        }
    }
}
