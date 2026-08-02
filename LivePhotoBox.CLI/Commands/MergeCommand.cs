using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Cli.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.CommandLine;

namespace LivePhotoBox.Cli.Commands
{
    internal static class MergeCommand
    {
        public static Command Create()
        {
            var imageOpt = new Option<FileInfo?>("--image",
                "Image file (JPEG, HEIC, PNG). Use with --video for single-pair mode.");
            imageOpt.AddAlias("-i");

            var videoOpt = new Option<FileInfo?>("--video",
                "Video file (MP4, MOV). Use with --image for single-pair mode.");
            videoOpt.AddAlias("-vid");

            var dirOpt = new Option<DirectoryInfo?>("--dir",
                "Folder with images+ videos. Files with matching names are paired. For batch mode.");
            dirOpt.AddAlias("-d");

            var protocolOpt = new Option<string>("--protocol", () => "v2",
                "Target phone format. fusion (universal Android)|v1 (Google old)|v2 (Google, default)|oppo|vivo|samsung|huawei.\nUse 'protocols' command to see all supported combinations.");
            protocolOpt.AddAlias("-p");

            var outputOpt = new Option<DirectoryInfo?>("--output",
                "Output folder (default: current directory).");
            outputOpt.AddAlias("-o");

            var formatOpt = new Option<string?>("--format",
                "Container format. jpg+mp4 (most compatible)|jpg+mov (Apple-style)|heic+mp4 (compact)|heic+mov|heic+mp4-h265 (HUAWEI native, HEVC).\nDefault: first available for the chosen protocol.");
            formatOpt.AddAlias("-f");

            var namingOpt = new Option<string>("--naming", () => "keep",
                "Output filename. keep (same name)|suffix (append protocol)|custom:TEMPLATE.\nTemplate tokens: {name} {protocol} {date} {date:yyyy-MM-dd} {time} {exif_date} {exif_time} {counter} {counter:D3}");
            namingOpt.AddAlias("-n");

            var parallelOpt = new Option<int>("--parallel",
                () => Math.Min(Environment.ProcessorCount, 5),
                "How many files to process at once. More = faster CPU usage.");
            parallelOpt.AddAlias("-j");

            var yesOpt = new Option<bool>("--yes",
                "Skip confirmation prompts. Useful for scripts / automation.");
            yesOpt.AddAlias("-y");

            var dryRunOpt = new Option<bool>("--dry-run",
                "Preview: show what would be done, don't actually process files.");

            var verboseOpt = new Option<bool>("--verbose",
                "Show per-file status messages instead of summary only.");
            verboseOpt.AddAlias("-v");

            var overwriteOpt = new Option<bool>("--overwrite",
                "Replace existing files. Without this, name conflicts get auto-renamed (photo.jpg -> photo (2).jpg).");
            overwriteOpt.AddAlias("-w");

            var recursiveOpt = new Option<bool>("--recursive",
                "Also scan subdirectories inside the input folder.");
            recursiveOpt.AddAlias("-r");

            var preserveSubdirsOpt = new Option<bool>("--preserve-subdirs",
                "Keep source subdirectory structure in the output folder.");
            preserveSubdirsOpt.AddAlias("-s");

            var pairingOpt = new Option<string>("--pairing", () => "name",
                "How to match images with videos. name (same filename)|cid (Apple ContentIdentifier UUID)|vivo (vivo camera ID).");

            var afterOpt = new Option<string>("--after", () => "none",
                "After successful merge: none (keep source)|move:PATH (move to folder)|recycle (Windows recycle bin).");

            var cmd = new Command("merge",
                "Combine images and videos into phone-compatible live photos.\n\n" +
                "Single pair:  livephotobox merge -i photo.jpg -vid video.mp4 -p huawei\n" +
                "Batch folder: livephotobox merge -d ./MyPhotos -p v2 -o ./Output -y\n" +
                "Preview:      livephotobox merge -d ./MyPhotos --dry-run\n" +
                "Formats:      livephotobox protocols")
            {
                imageOpt, videoOpt, dirOpt, protocolOpt, outputOpt, formatOpt,
                namingOpt, parallelOpt, yesOpt, dryRunOpt, verboseOpt,
                overwriteOpt, recursiveOpt, preserveSubdirsOpt, pairingOpt, afterOpt
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
                var overwrite = context.ParseResult.GetValueForOption(overwriteOpt);
                var recursive = context.ParseResult.GetValueForOption(recursiveOpt);
                var preserveSubdirs = context.ParseResult.GetValueForOption(preserveSubdirsOpt);
                var pairing = context.ParseResult.GetValueForOption(pairingOpt)!;
                var after = context.ParseResult.GetValueForOption(afterOpt)!;

                context.ExitCode = await RunAsync(
                    image, video, dir, protocolName, output, formatName,
                    naming, parallel, yes, dryRun, verbose,
                    overwrite, recursive, preserveSubdirs, pairing, after,
                    context.GetCancellationToken());
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            FileInfo? image, FileInfo? video, DirectoryInfo? dir,
            string protocolName, DirectoryInfo? output, string? formatName,
            string naming, int parallel, bool yes, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs,
            string pairing, string after, CancellationToken ct)
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
                    Console.Error.WriteLine($"Error: Unknown format '{formatName}'. Valid: jpg+mp4, jpg+mov, heic+mp4, heic+mov, heic+mp4-h265");
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

            // Resolve pairing method
            bool useCid = pairing.Equals("cid", StringComparison.OrdinalIgnoreCase);
            bool useVivo = pairing.Equals("vivo", StringComparison.OrdinalIgnoreCase);
            bool useName = pairing.Equals("name", StringComparison.OrdinalIgnoreCase);
            if (!useName && !useCid && !useVivo)
            {
                Console.Error.WriteLine($"Error: Unknown pairing method '{pairing}'. Valid: name, cid, vivo");
                return 1;
            }

            // Resolve after-completion action
            string? afterMoveDir = null;
            bool afterRecycle = false;
            if (after.StartsWith("move:", StringComparison.OrdinalIgnoreCase))
                afterMoveDir = after.Substring(5);
            else if (after.Equals("recycle", StringComparison.OrdinalIgnoreCase))
                afterRecycle = true;
            else if (!after.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Error: Unknown after-completion action '{after}'. Valid: none, move:<dir>, recycle");
                return 1;
            }

            // Set recursive scan preference (restore before exit)
            bool? originalRecursiveSetting = null;
            try
            {
                originalRecursiveSetting = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                AppSettingsService.SetValue("IsRecursiveScanEnabled", recursive);
            }
            catch { /* non-packaged CLI: best effort */ }

            try
            {
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
                if (isBatch)
                    Console.WriteLine($"Pairing  : {pairing}");

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
                        overwrite, verbose, ct);
                }
                else
                {
                    return await MergeBatchAsync(
                        dir!.FullName, outputDir, tempDir,
                        protocolIndex, formatIndex, namingRuleIndex, customPattern,
                        parallel, yes, dryRun, verbose,
                        overwrite, preserveSubdirs, useCid, useVivo,
                        afterMoveDir, afterRecycle, ct);
                }
            }
            finally
            {
                // Restore original recursive scan setting
                if (originalRecursiveSetting.HasValue)
                {
                    try { AppSettingsService.SetValue("IsRecursiveScanEnabled", originalRecursiveSetting.Value); }
                    catch { /* best effort */ }
                }
            }
        }

        private static async Task<int> MergeSinglePairAsync(
            string imagePath, string videoPath, string outputDir, string tempDir,
            int protocolIndex, int formatIndex, int namingRuleIndex, string? customPattern,
            bool overwrite, bool verbose, CancellationToken ct)
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
                    OverwriteExisting = overwrite,
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
            int parallel, bool yes, bool dryRun, bool verbose,
            bool overwrite, bool preserveSubdirs, bool useCid, bool useVivo,
            string? afterMoveDir, bool afterRecycle, CancellationToken ct)
        {
            // 1. Scan — filename-based pairing (always)
            Console.Write($"Scanning '{inputDir}'... ");
            var scanResult = LivePhotoMergeScanService.Scan(inputDir, ct);

            var allPairs = new List<(string ImagePath, string VideoPath, string BaseName)>();

            // Add filename pairs
            foreach (var pair in scanResult.Pairs)
                allPairs.Add((pair.ImagePath, pair.VideoPath, pair.BaseName));

            // 2. Metadata-based pairing on unmatched files (cid / vivo)
            int metaPairs = 0;
            if (useCid && scanResult.StandaloneImagePaths.Count > 0 && scanResult.StandaloneVideoPaths.Count > 0)
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (!string.IsNullOrEmpty(exifToolPath) && File.Exists(exifToolPath))
                {
                    Console.Write("CID matching... ");
                    var metaResult = await LivePhotoMetadataMatcher.MatchAsync(
                        scanResult.StandaloneImagePaths, scanResult.StandaloneVideoPaths,
                        exifToolPath, ct);
                    foreach (var mp in metaResult.Pairs)
                        allPairs.Add((mp.ImagePath, mp.VideoPath, Path.GetFileNameWithoutExtension(mp.ImagePath)));
                    metaPairs = metaResult.Pairs.Count;
                }
                else
                {
                    Console.Write("(exiftool not found, skip CID) ");
                }
            }
            else if (useVivo && scanResult.StandaloneImagePaths.Count > 0 && scanResult.StandaloneVideoPaths.Count > 0)
            {
                Console.Write("vivo matching... ");
                var metaResult = LivePhotoMetadataMatcher.MatchVivo(
                    scanResult.StandaloneImagePaths, scanResult.StandaloneVideoPaths);
                foreach (var mp in metaResult.Pairs)
                    allPairs.Add((mp.ImagePath, mp.VideoPath, Path.GetFileNameWithoutExtension(mp.ImagePath)));
                metaPairs = metaResult.Pairs.Count;
            }

            int standaloneImg = scanResult.StandaloneImagesCount - metaPairs;
            int standaloneVid = scanResult.StandaloneVideosCount - metaPairs;
            Console.WriteLine($"{scanResult.Pairs.Count} filename pairs, {metaPairs} meta pairs, " +
                $"{standaloneImg} standalone images, {standaloneVid} standalone videos");

            if (allPairs.Count == 0)
            {
                Console.Error.WriteLine("No image+video pairs found. Nothing to do.");
                return 0;
            }

            // 3. Build task list
            var tasks = new List<CliMergeTask>(allPairs.Count);
            for (int i = 0; i < allPairs.Count; i++)
            {
                var p = allPairs[i];
                tasks.Add(new CliMergeTask
                {
                    Index = i + 1,
                    ImagePath = p.ImagePath,
                    VideoPath = p.VideoPath,
                    BaseName = p.BaseName,
                });
            }

            // 4. Dry run
            if (dryRun)
            {
                Console.WriteLine($"\n[DRY RUN] Would merge {tasks.Count} pairs:");
                foreach (var t in tasks)
                    Console.WriteLine($"  #{t.Index}  {t.BaseName}");
                return 0;
            }

            // 5. Confirmation
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

            // 6. Run batch
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
                OverwriteExisting = overwrite,
                PreserveSubfolders = preserveSubdirs,
                InputDirectory = preserveSubdirs ? inputDir : null,
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
                    task.Status = success ? ProcessStatus.Success : ProcessStatus.Failed;
                    task.Details = details;
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

            // 7. After-completion actions (only on successful tasks)
            if (!string.IsNullOrEmpty(afterMoveDir))
            {
                Console.WriteLine($"\nMoving source files to '{afterMoveDir}'...");
                Directory.CreateDirectory(afterMoveDir);
                int moved = 0;
                foreach (var task in tasks.Where(t => t.Status == ProcessStatus.Success))
                {
                    try
                    {
                        if (File.Exists(task.ImagePath))
                        {
                            File.Move(task.ImagePath, Path.Combine(afterMoveDir, Path.GetFileName(task.ImagePath)));
                            moved++;
                        }
                        if (!string.IsNullOrEmpty(task.VideoPath) && File.Exists(task.VideoPath))
                        {
                            File.Move(task.VideoPath, Path.Combine(afterMoveDir, Path.GetFileName(task.VideoPath)));
                            moved++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  WARN: Failed to move '{task.BaseName}': {ex.Message}");
                    }
                }
                Console.WriteLine($"  Moved {moved} source files.");
            }
            else if (afterRecycle)
            {
                Console.WriteLine("\nMoving source files to recycle bin...");
                int recycled = 0;
                foreach (var task in tasks.Where(t => t.Status == ProcessStatus.Success))
                {
                    try
                    {
                        if (File.Exists(task.ImagePath))
                        {
                            MoveToRecycleBin(task.ImagePath);
                            recycled++;
                        }
                        if (!string.IsNullOrEmpty(task.VideoPath) && File.Exists(task.VideoPath))
                        {
                            MoveToRecycleBin(task.VideoPath);
                            recycled++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  WARN: Failed to recycle '{task.BaseName}': {ex.Message}");
                    }
                }
                Console.WriteLine($"  Recycled {recycled} source files.");
            }

            // 8. Summary
            Console.WriteLine();
            Console.WriteLine($"Done: {ok} OK, {fail} FAIL, {tasks.Count} total");
            return fail > 0 ? 1 : 0;
        }

        // ══════════════════════════════════════════════════════════════
        //  Recycle Bin via SHFileOperationW
        // ══════════════════════════════════════════════════════════════

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

        private static void MoveToRecycleBin(string path)
        {
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
            };
            int result = SHFileOperationW(ref shf);
            if (result != 0)
                throw new IOException($"SHFileOperationW failed with code {result}");
        }
    }
}
