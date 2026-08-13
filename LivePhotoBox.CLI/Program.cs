using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
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

            // Fast --version: local-only info, no network or subprocesses.
            if (args.Length == 1 && args[0] == "--version")
            {
                VersionInfo.PrintVersion();
                return 0;
            }

            // Initialize ResourceService — disk .resw first, embedded English fallback
            InitializeResourceService();

            // Initialize logging — CLI logs into its own subdirectory with a `cli` prefix,
            // keeping them distinct from the GUI's `app-*.log` files in the same Logs root.
            LogService.Initialize(subDirectory: "CLI", logFilePrefix: "cli");

            // Build command tree
            var root = new RootCommand(
                "Convert images and videos into phone-compatible live photos.\n\n" +
                "Quick start:\n" +
                "  lpb protocols                      List what formats are available\n" +
                "  lpb merge photo.jpg video.mp4      Convert one pair\n" +
                "  lpb merge -d ./Photos -p huawei -y Batch convert a folder\n" +
                "  lpb info                           Show version and environment info")
            {
                MergeCommand.Create(),
                ProtocolsCommand.Create(),
                InfoCommand.Create(),
                UpdateCommand.Create(),
                UpdateCommand.CreateUpdate()
            };

            var builder = new CommandLineBuilder(root)
                .UseDefaults()
                .UseHelpBuilder(context => new GroupedHelpBuilder(context.Console))
                .Build();

            return await builder.InvokeAsync(args);
        }

        private static void InitializeResourceService()
        {
            // CLI is English-only. The complete English .resw is embedded in
            // LivePhotoBox.Core.dll at build time — no disk .resw needed.
            ResourceService.SetProvider(new ReswResourceProvider(reswDir: ""));
        }
    }
}
