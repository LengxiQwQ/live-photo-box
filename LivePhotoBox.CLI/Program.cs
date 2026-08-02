using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            // UTF-8 console output for Chinese characters
            Console.OutputEncoding = Encoding.UTF8;

            // Initialize ResourceService with CLI resw XML provider
            InitializeResourceService();

            // Initialize logging
            LogService.Initialize();

            // Build command tree
            var root = new RootCommand("Live Photo Box CLI — live photo toolbox")
            {
                MergeCommand.Create(),
                ProtocolsCommand.Create()
            };

            var builder = new CommandLineBuilder(root)
                .UseDefaults()
                .Build();

            return await builder.InvokeAsync(args);
        }

        private static void InitializeResourceService()
        {
            // Try to find resw files relative to the executable
            string exeDir = AppContext.BaseDirectory;

            // Look for Strings/ directory alongside the exe (portable/install layout)
            string reswDir = Path.Combine(exeDir, "Strings");
            if (!Directory.Exists(reswDir))
            {
                // Fallback: look in the project source tree (dotnet run)
                reswDir = Path.Combine(exeDir, "..", "..", "..", "..", "Live Photo Box", "Strings");
            }

            if (Directory.Exists(reswDir))
            {
                ResourceService.SetProvider(new ReswResourceProvider(reswDir));
            }
            // else: provider stays null, GetString returns keys as-is (graceful degradation)
        }
    }
}
