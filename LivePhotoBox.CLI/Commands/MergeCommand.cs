using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Cli.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands
{
    internal static class MergeCommand
    {
        public static Command Create()
        {
            var imageOpt = new Option<FileInfo?>("--image", "Source image file path (single-pair mode)");
            imageOpt.AddAlias("-i");

            var videoOpt = new Option<FileInfo?>("--video", "Source video file path (single-pair mode)");
            videoOpt.AddAlias("-vid");

            var dirOpt = new Option<DirectoryInfo?>("--dir", "Source directory to scan for image+video pairs (batch mode)");
            dirOpt.AddAlias("-d");

            var protocolOpt = new Option<string>("--protocol", () => "v2", "Target protocol: fusion|v1|v2|oppo|vivo|samsung|huawei");
            protocolOpt.AddAlias("-p");

            var outputOpt = new Option<DirectoryInfo?>("--output", "Output directory (default: current directory)");
            outputOpt.AddAlias("-o");

            var formatOpt = new Option<string?>("--format", "Output format: jpg+mp4|jpg+mov|heic+mp4|heic+mov|heic+mp4-h265 (default: first available)");
            formatOpt.AddAlias("-f");

            var namingOpt = new Option<string>("--naming", () => "keep", "Naming rule: keep|suffix|custom:<pattern>");
            namingOpt.AddAlias("-n");

            var parallelOpt = new Option<int>("--parallel", () => Math.Min(Environment.ProcessorCount, 5), "Max parallel tasks");
            parallelOpt.AddAlias("-j");

            var yesOpt = new Option<bool>("--yes", "Skip confirmation prompt");
            yesOpt.AddAlias("-y");

            var dryRunOpt = new Option<bool>("--dry-run", "Preview operations without executing");
            var verboseOpt = new Option<bool>("--verbose", "Verbose output");
            verboseOpt.AddAlias("-v");

            var cmd = new Command("merge", "Merge image+video pairs into live photos")
            {
                imageOpt, videoOpt, dirOpt, protocolOpt, outputOpt, formatOpt,
                namingOpt, parallelOpt, yesOpt, dryRunOpt, verboseOpt
            };

            cmd.SetHandler(async context =>
            {
                var image = context.ParseResult.GetValueForOption(imageOpt);
                var video = context.ParseResult.GetValueForOption(videoOpt);
                var dir = context.ParseResult.GetValueForOption(dirOpt);
                var protocolName = context.ParseResult.GetValueForOption(protocolOpt)!;
                var output = context.ParseResult.GetValueForOption(outputOpt);
                var formatName = context.ParseResult.GetValueForOption(formatOpt);
                var naming = context.ParseResult.GetValueForOption(namingOpt)!;
                var parallel = context.ParseResult.GetValueForOption(parallelOpt);
                var yes = context.ParseResult.GetValueForOption(yesOpt);
                var dryRun = context.ParseResult.GetValueForOption(dryRunOpt);
                var verbose = context.ParseResult.GetValueForOption(verboseOpt);

                context.ExitCode = await RunAsync(
                    image, video, dir, protocolName, output, formatName,
                    naming, parallel, yes, dryRun, verbose,
                    context.GetCancellationToken());
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            FileInfo? image, FileInfo? video, DirectoryInfo? dir,
            string protocolName, DirectoryInfo? output, string? formatName,
            string naming, int parallel, bool yes, bool dryRun, bool verbose,
            CancellationToken ct)
        {
            // Validate: need either --image+--video or --dir
            bool isSingle = image != null && video != null;
            bool isBatch = dir != null;

            if (!isSingle && !isBatch)
            {
                Console.Error.WriteLine("Error: Specify either --image and --video (single pair) or --dir (batch mode).");
                return 1;
            }

            if (isSingle && isBatch)
            {
                Console.Error.WriteLine("Error: Cannot use both single-pair (--image/--video) and batch (--dir) mode.");
                return 1;
            }

            // Resolve protocol
            if (!ProtocolNameResolver.TryResolveProtocol(protocolName, out int protocolIndex))
            {
                Console.Error.WriteLine($"Error: Unknown protocol '{protocolName}'. Use 'livephotobox protocols' to list available.");
                return 1;
            }

            // Resolve format
            int formatIndex = ProtocolFormatMatrix.GetDefaultFormat(protocolIndex);
            if (formatName != null)
            {
                if (!ProtocolNameResolver.TryResolveFormat(formatName, out formatIndex))
                {
                    Console.Error.WriteLine($"Error: Unknown format '{formatName}'. Valid: jpg+mp4, jpg+mov, heic+mp4, heic+mov");
                    return 1;
                }

                if (!ProtocolFormatMatrix.IsAvailable(protocolIndex, formatIndex))
                {
                    Console.Error.WriteLine($"Error: Format '{formatName}' is not available for protocol '{protocolName}'.");
                    Console.Error.WriteLine("Use 'livephotobox protocols' to see supported combinations.");
                    return 1;
                }
            }

            // Resolve naming rule
            int namingRuleIndex = 0;
            string? customPattern = null;
            if (naming.Equals("keep", StringComparison.OrdinalIgnoreCase))
                namingRuleIndex = 0;
            else if (naming.Equals("suffix", StringComparison.OrdinalIgnoreCase))
                namingRuleIndex = 1;
            else if (naming.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                namingRuleIndex = 2;
                customPattern = naming.Substring(7);
            }
            else
            {
                Console.Error.WriteLine($"Error: Unknown naming rule '{naming}'. Valid: keep, suffix, custom:<pattern>");
                return 1;
            }

            // Resolve output directory
            string outputDir = output?.FullName ?? Environment.CurrentDirectory;
            string tempDir = Path.Combine(outputDir, "Temp");
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(tempDir);

            // Print summary
            string protoDisplay = ProtocolNameResolver.GetProtocolDisplayName(protocolIndex);
            string fmtDisplay = ProtocolFormatMatrix.FormatNames[formatIndex];
            Console.WriteLine($"Protocol : {protoDisplay}");
            Console.WriteLine($"Format   : {fmtDisplay}");
            Console.WriteLine($"Output   : {outputDir}");

            if (isSingle)
            {
                Console.WriteLine($"Image    : {image!.FullName}");
                Console.WriteLine($"Video    : {video!.FullName}");

                if (dryRun)
                {
                    Console.WriteLine("[DRY RUN] Would merge 1 pair.");
                    return 0;
                }

                if (!yes)
                {
                    Console.Write("Proceed? [y/N] ");
                    var key = Console.ReadLine();
                    if (!string.Equals(key, "y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("Cancelled.");
                        return 0;
                    }
                }

                return await MergeSinglePairAsync(
                    image.FullName, video.FullName, outputDir, tempDir,
                    protocolIndex, formatIndex, namingRuleIndex, customPattern,
                    verbose, ct);
            }
            else
            {
                return await MergeBatchAsync(
                    dir!.FullName, outputDir, tempDir,
                    protocolIndex, formatIndex, namingRuleIndex, customPattern,
                    parallel, yes, dryRun, verbose, ct);
            }
        }

        private static async Task<int> MergeSinglePairAsync(
            string imagePath, string videoPath, string outputDir, string tempDir,
            int protocolIndex, int formatIndex, int namingRuleIndex, string? customPattern,
            bool verbose, CancellationToken ct)
        {
            try
            {
                string baseName = Path.GetFileNameWithoutExtension(imagePath);

                var options = new LivePhotoMergeRunOptions
                {
                    OutputDirectory = outputDir,
                    SelectedModeIndex = protocolIndex,
                    OutputFormatIndex = formatIndex,
                    NamingRuleIndex = namingRuleIndex,
                    CustomNamingPattern = customPattern,
                };

                if (verbose)
                    Console.WriteLine($"Starting merge: {baseName}...");

                var pause = new ManualResetEventSlim(true); // CLI never pauses
                var (isSuccess, details) = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
                    imagePath, videoPath, baseName, taskIndex: 1,
                    options, tempDir, pause, ct);

                if (isSuccess)
                {
                    Console.WriteLine($"OK  {baseName}  ({details})");
                    return 0;
                }
                else
                {
                    Console.Error.WriteLine($"FAIL  {baseName}  {details}");
                    return 1;
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        private static async Task<int> MergeBatchAsync(
            string inputDir, string outputDir, string tempDir,
            int protocolIndex, int formatIndex, int namingRuleIndex, string? customPattern,
            int parallel, bool yes, bool dryRun, bool verbose, CancellationToken ct)
        {
            // 1. Scan
            Console.Write($"Scanning '{inputDir}'... ");
            var scanResult = LivePhotoMergeScanService.Scan(inputDir, ct);
            Console.WriteLine($"{scanResult.Pairs.Count} pairs found, " +
                $"{scanResult.StandaloneImagesCount} standalone images, " +
                $"{scanResult.StandaloneVideosCount} standalone videos");

            if (scanResult.Pairs.Count == 0)
            {
                Console.Error.WriteLine("No image+video pairs found. Nothing to do.");
                return 0;
            }

            // 2. Build task list
            var tasks = new List<CliMergeTask>(scanResult.Pairs.Count);
            for (int i = 0; i < scanResult.Pairs.Count; i++)
            {
                var pair = scanResult.Pairs[i];
                tasks.Add(new CliMergeTask
                {
                    Index = i + 1,
                    ImagePath = pair.ImagePath,
                    VideoPath = pair.VideoPath,
                    BaseName = pair.BaseName,
                });
            }

            // 3. Dry run — just list what would be done
            if (dryRun)
            {
                Console.WriteLine($"\n[DRY RUN] Would merge {tasks.Count} pairs:");
                foreach (var t in tasks)
                    Console.WriteLine($"  #{t.Index}  {t.BaseName}");
                return 0;
            }

            // 4. Confirmation
            if (!yes)
            {
                Console.Write($"\nMerge {tasks.Count} pairs? [y/N] ");
                var key = Console.ReadLine();
                if (!string.Equals(key, "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Cancelled.");
                    return 0;
                }
            }

            // 5. Run batch
            Console.WriteLine($"\nProcessing {tasks.Count} pairs (parallel={parallel})...");
            Console.WriteLine();

            var options = new LivePhotoMergeRunOptions
            {
                OutputDirectory = outputDir,
                SelectedModeIndex = protocolIndex,
                OutputFormatIndex = formatIndex,
                NamingRuleIndex = namingRuleIndex,
                CustomNamingPattern = customPattern,
                MaxDegreeOfParallelism = parallel,
            };

            int ok = 0, fail = 0;
            var pause = new ManualResetEventSlim(true);

            await LivePhotoMergeRunnerService.RunAsync(
                tasks,
                options,
                pause,
                ct,
                onTaskStarted: task =>
                {
                    if (verbose)
                        Console.Write($"  [{task.Index}/{tasks.Count}] {task.BaseName} ... ");
                },
                onTaskCompleted: (task, success, details, completed) =>
                {
                    if (success)
                    {
                        Interlocked.Increment(ref ok);
                        if (verbose)
                            Console.WriteLine("OK");
                        else
                            Console.WriteLine($"  [{completed}/{tasks.Count}] OK  {task.BaseName}");
                    }
                    else
                    {
                        Interlocked.Increment(ref fail);
                        if (verbose)
                            Console.WriteLine($"FAIL ({details})");
                        else
                            Console.WriteLine($"  [{completed}/{tasks.Count}] FAIL  {task.BaseName}  ({details})");
                    }
                });

            // 6. Summary
            Console.WriteLine();
            Console.WriteLine($"Done: {ok} OK, {fail} FAIL, {tasks.Count} total");
            return fail > 0 ? 1 : 0;
        }
    }
}
