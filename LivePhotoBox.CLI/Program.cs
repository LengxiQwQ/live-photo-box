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

            // Initialize ResourceService — disk .resw first, embedded English fallback
            InitializeResourceService();

            // Initialize logging
            LogService.Initialize();

            // Build command tree
            var root = new RootCommand(
                "Convert images and videos into phone-compatible live photos.\n\n" +
                "Quick start:\n" +
                "  livephotobox protocols                     List what formats are available\n" +
                "  livephotobox merge -i img.jpg -vid vid.mp4  Convert one pair\n" +
                "  livephotobox merge -d ./Photos -p huawei -y Batch convert a folder")
            {
                MergeCommand.Create(),
                ProtocolsCommand.Create(),
                UpdateCommand.Create()
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
