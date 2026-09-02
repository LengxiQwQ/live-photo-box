using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
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
                        "  lpb split photo.jpg                 Split a live photo\n" +
                        "  lpb split ./Photos -y               Batch split a folder\n" +
                         "  lpb repair photo.jpg                Fix live photo metadata\n" +
                         "  lpb repair -d ./Photos -y           Batch fix a folder\n" +
                         "  lpb convert input.mov -o output.mp4 Convert media through Rebuilt Native\n" +
                         "  lpb cover photo.jpg --at 2.5        Change cover frame\n" +
                        "  lpb cover photo.jpg                  View current cover info\n" +
                        "  lpb backend                          View or configure processing backends\n" +
                        "  lpb --info                          Show detailed environment info")
                    {
                        MergeCommand.Create(),
                        SplitCommand.Create(),
                        RepairCommand.Create(),
                        ConvertCommand.Create(),
                        ProtocolsCommand.Create(),
                        UpdateCommand.Create(),
                        UpdateCommand.CreateUpdate(),
                        CoverCommand.Create(),
                        BackendCommand.Create()
                    };

                    // Declare --info so `lpb --help` lists it (the fast path above handles actual invocation).
                    root.Add(new Option<bool>("--info") { Description = "Show detailed environment info (build date, runtime, platform, channel, location, bundled tools)" });

                    var parseResult = root.Parse(args);
                    if (parseResult.Errors.Count > 0)
                    {
                        PrintParseError(parseResult, root);
                        exitCode = 1;
                    }
                    else
                    {
                        try
                        {
                            int invocationExitCode = await parseResult.InvokeAsync();
                            // Command actions in System.CommandLine return 0 even when
                            // they set Environment.ExitCode themselves.
                            exitCode = invocationExitCode != 0
                                ? invocationExitCode
                                : Environment.ExitCode;
                        }
                        catch (Exception ex)
                        {
                            OnUnhandledException(ex, args);
                            exitCode = 1;
                        }
                    }
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

        // 解析错误（未知选项/命令、缺参数、参数类型不对、位置参数过多等）→ 一行错误 + 可能的
        // "Did you mean" 建议 + 帮助提示。不再整篇打印帮助。
        private static void PrintParseError(ParseResult parseResult, RootCommand root)
        {
            string commandName = parseResult.CommandResult?.Command?.Name ?? "";
            if (commandName.Length == 0 || commandName == root.Name) commandName = "lpb";
            var allOptionAliases = root.Options.SelectMany(o => o.Aliases)
                .Concat(root.Subcommands.SelectMany(s => s.Options).SelectMany(o => o.Aliases))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var aliasSet = new HashSet<string>(allOptionAliases, StringComparer.Ordinal);

            // 解析器对"未知选项 + 值"的处理很混乱（会把后面的路径/值报成错误对象），
            // 直接从原始 token 里识别拼错的选项，给出干净的 "Unknown option + Did you mean" 提示。
            var unknownOptions = parseResult.Tokens
                .Where(t => t.Value.StartsWith('-')
                         && t.Value != "--"
                         && !aliasSet.Contains(t.Value)
                         && !double.TryParse(t.Value, out _))
                .Select(t => t.Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (unknownOptions.Count > 0)
            {
                foreach (var opt in unknownOptions)
                    CliConsole.WriteErrorLine($"Error: Unknown option '{opt}'.{CliConsole.DidYouMean(opt, allOptionAliases)}");
            }
            else
            {
                var messages = parseResult.Errors.Select(e => e.Message).ToList();
                // "Required command was not provided." 在已有更具体的"无法识别的命令/参数"错误时是纯噪音
                if (messages.Any(m => m.Contains("Unrecognized command or argument", StringComparison.Ordinal)))
                    messages.RemoveAll(m => m == "Required command was not provided.");

                foreach (var raw in messages)
                {
                    string message = raw.Replace(" as expected type 'System.Int32'", " as a whole number");
                    if (message == "Required command was not provided.")
                        message = "No command specified. Try: convert, merge, split, repair, cover, protocols, backend, update";
                    string? token = ExtractQuotedToken(message);
                    string suggestion = "";
                    if (token != null && !aliasSet.Contains(token))
                    {
                        suggestion = token.StartsWith('-')
                            ? CliConsole.DidYouMean(token, allOptionAliases)
                            : CliConsole.DidYouMean(token, root.Subcommands.Select(s => s.Name));
                    }
                    CliConsole.WriteErrorLine($"Error: {message}{suggestion}");
                }
            }
            CliConsole.WriteHintLine(commandName == "lpb"
                ? "Run 'lpb --help' to see usage and options."
                : $"Run 'lpb {commandName} --help' to see usage and options.");
        }

        private static string? ExtractQuotedToken(string message)
        {
            var match = Regex.Match(message, "'([^']*)'");
            return match.Success ? match.Groups[1].Value : null;
        }

        // 未处理异常（命令处理器抛出的、未被内部 catch 捕获的）→ 记录进日志（含堆栈）+ 输出到 stderr。
        // 退出码由异常处理器中间件置 1；随后 Main 的 finally 会再写一条退出码日志 + CLEAN SHUTDOWN。
        private static void OnUnhandledException(Exception exception, string[] args)
        {
            LogService.Error($"Unhandled CLI exception: {exception}", exception, LogSource.System);
            if (exception is RebuiltPipelineNotReadyException rebuilt)
            {
                string rebuiltMessage = rebuilt.Message;
                if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "failed",
                        errorCode = "rebuilt_not_ready",
                        operation = rebuilt.Operation,
                        error = rebuiltMessage
                    }));
                }
                else
                {
                    CliConsole.WriteErrorLine($"Error: {rebuiltMessage}");
                }
                return;
            }
            string message = exception switch
            {
                FileNotFoundException e => $"File not found: {e.FileName ?? e.Message}",
                DirectoryNotFoundException e => $"Directory not found: {e.Message}",
                UnauthorizedAccessException e => $"Access denied: {e.Message}",
                IOException e => $"I/O error: {e.Message}",
                ArgumentException e => $"Invalid value: {e.Message}",
                _ => $"Unexpected error: {exception.Message}",
            };
            CliConsole.WriteErrorLine($"Error: {message}");
            CliConsole.WriteHintLine("This is likely a bug or an unusual input. Check the log for details (run 'lpb --info' for the log folder) and retry with --verbose.");
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
