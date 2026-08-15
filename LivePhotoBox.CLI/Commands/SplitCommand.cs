using LivePhotoBox.Cli.Infrastructure;
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
using System.Text.Json;

namespace LivePhotoBox.Cli.Commands
{
    internal static class SplitCommand
    {
        // Recognized single-file live photo extensions (positional argument).
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".heic", ".heif" };

        // Split-specific protocol name → index map.
        // Deliberately separate from ProtocolNameResolver: the merge page's vivo=4 conflicts with
        // split's vivo=2 (global split protocolIndex: 0=none / 1=Apple / 2=vivo, placeholder only).
        private static readonly Dictionary<string, int> SplitProtocolMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["none"]  = 0,
            ["apple"] = 1,
            ["vivo"]  = 2,
        };

        // Split-specific format name → global outputFormatIndex map (see LivePhotoSplitService):
        //   0 = keep original (no image/audio conversion)  1 = JPG+MOV (H.265)
        //   2 = HEIC+MOV (H.265)                            3 = JPG+MP4 (H.264)
        private static readonly Dictionary<string, int> SplitFormatMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["keep"]     = 0,
            ["jpg+mov"]  = 1,
            ["heic+mov"] = 2,
            ["jpg+mp4"]  = 3,
        };

        // Split protocol → format availability matrix (single source of truth for CLI split).
        // protocolIndex: 0=none / 1=Apple / 2=vivo (see SplitProtocolMap).
        // formatIndex:   0=keep / 1=jpg+mov / 2=heic+mov / 3=jpg+mp4 (see SplitFormatMap).
        // Mirrors the GUI SplitPage matrix.
        internal static readonly bool[][] SplitFormatMatrix =
        [
            [true,  true,  true,  true ],  // none:  keep / jpg+mov / heic+mov / jpg+mp4
            [false, true,  true,  false],  // apple: jpg+mov / heic+mov
            [false, false, false, true ],  // vivo:  jpg+mp4
        ];

        // Split format short names, ordered by outputFormatIndex (for the protocols command matrix / JSON).
        internal static readonly string[] SplitFormatNames = ["keep", "jpg+mov", "heic+mov", "jpg+mp4"];

        // 默认格式：该协议的第一个可用格式（none→keep、apple→jpg+mov、vivo→jpg+mp4）。
        private static int GetSplitDefaultFormat(int protocolIndex)
        {
            var row = SplitFormatMatrix[protocolIndex];
            for (int f = 0; f < row.Length; f++)
                if (row[f]) return f;
            return 0;
        }

        private static bool IsSplitFormatAvailable(int protocolIndex, int formatIndex)
            => SplitFormatMatrix[protocolIndex][formatIndex];

        // Split-specific pairing filter: protocol name → LivePhotoProtocolType (null = no filter).
        // Mirrors the GUI SplitViewModel.MatchProtocolType mapping (0=all → null, then
        // Fusion/GoogleV1/GoogleV2/OPPO/Vivo/Samsung/Huawei). all keeps the current
        // "scan everything" behavior.
        private static readonly Dictionary<string, LivePhotoProtocolType?> SplitPairingMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["all"]     = null,
            ["fusion"]  = LivePhotoProtocolType.Fusion,
            ["v1"]      = LivePhotoProtocolType.GoogleV1,
            ["v2"]      = LivePhotoProtocolType.GoogleV2,
            ["oppo"]    = LivePhotoProtocolType.OPPO,
            ["vivo"]    = LivePhotoProtocolType.Vivo,
            ["samsung"] = LivePhotoProtocolType.Samsung,
            ["huawei"]  = LivePhotoProtocolType.Huawei,
        };

        private static readonly string[] SplitProtocolDisplayNames =
            ["None (split only)", "Apple Live Photo", "vivo Live Photo"];

        private static readonly string[] SplitFormatDisplayNames =
            ["keep original", "JPG + MOV (H.265)", "HEIC + MOV (H.265)", "JPG + MP4 (H.264)"];

        public static Command Create()
        {
            var filesArg = new Argument<string?>("files",
                "One live photo file to split (.jpg/.jpeg/.heic/.heif with an appended video).");
            filesArg.Arity = ArgumentArity.ZeroOrOne;

            var dirOpt = new Option<DirectoryInfo?>("--dir",
                "Folder with single-file live photos. All detected live photos are split. For batch mode.");
            dirOpt.AddAlias("-d");

            var pairingOpt = new Option<string>("--pairing", () => "all",
                "Only split live photos of this protocol. all (no filter)|fusion|v1 (MicroVideo)|v2 (MotionPhoto)|oppo|vivo|samsung|huawei.");

            var protocolOpt = new Option<string>("--protocol", () => "none",
                "Target phone format. none (split only)|apple (Apple Live Photo)|vivo (vivo Live Photo, ≤ X200).\n" +
                "This iteration only splits the file — pairing metadata is not written yet.");
            protocolOpt.AddAlias("-p");

            var formatOpt = new Option<string?>("--format",
                "Output format. keep (no conversion)|jpg+mov (H.265)|heic+mov (H.265)|jpg+mp4 (H.264).\nDefault: first available for the chosen protocol.");
            formatOpt.AddAlias("-f");

            var outputOpt = new Option<DirectoryInfo?>("--output",
                "Output folder. Default: the source file's own directory for a single file; a \"{folder}_split\" subfolder inside the input folder for batch mode.");
            outputOpt.AddAlias("-o");

            var namingOpt = new Option<string>("--naming", () => "keep",
                "Output filename. keep (same name)|suffix (append _split)|custom:TEMPLATE.\nTemplate tokens: {name} {date} {date:yyyy-MM-dd} {time} {exif_date} {exif_time} {counter} {counter:D3}");
            namingOpt.AddAlias("-n");

            var parallelOpt = new Option<int>("--parallel",
                () => Math.Min(Environment.ProcessorCount, 5),
                "How many files to process at once. More = faster CPU usage.");
            parallelOpt.AddAlias("-j");

            var yesOpt = new Option<bool>("--yes",
                "Skip confirmation prompts. Useful for scripts / automation.");
            yesOpt.AddAlias("-y");

            var jsonOpt = new Option<bool>("--json",
                "Output machine-readable JSON to stdout (implies --yes).");

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

            var afterOpt = new Option<string>("--after", () => "none",
                "After successful split: none (keep source)|move:PATH (move to folder)|recycle (Windows recycle bin).");

            var cmd = new Command("split",
                "Split single-file live photos into separate image and video files.\n" +
                "Input: .jpg .jpeg .heic .heif (single-file live photos with an appended video)\n\n" +
                "Single file:  lpb split photo.jpg\n" +
                "              (writes photo.jpg + photo.mov next to the source)\n" +
                "Batch folder: lpb split -d ./MyPhotos -y\n" +
                "              (writes ./MyPhotos/MyPhotos_split/)\n" +
                "Convert:      lpb split photo.jpg -f jpg+mp4\n" +
                "Preview:      lpb split -d ./MyPhotos --dry-run\n" +
                "Formats:      lpb protocols")
            {
                filesArg,
                dirOpt, pairingOpt, protocolOpt, formatOpt, outputOpt, namingOpt,
                parallelOpt, yesOpt, jsonOpt, dryRunOpt, verboseOpt,
                overwriteOpt, recursiveOpt, preserveSubdirsOpt, afterOpt
            };

            cmd.SetHandler(async context =>
            {
                string? singlePath = context.ParseResult.GetValueForArgument(filesArg);
                var dir = context.ParseResult.GetValueForOption(dirOpt);
                var pairingName = context.ParseResult.GetValueForOption(pairingOpt)!;
                var protocolName = context.ParseResult.GetValueForOption(protocolOpt)!;
                var formatName = context.ParseResult.GetValueForOption(formatOpt);
                var output = context.ParseResult.GetValueForOption(outputOpt);
                var naming = context.ParseResult.GetValueForOption(namingOpt)!;
                // 用户是否显式传了 --naming（未传时单文件默认 suffix、批量默认 keep）
                bool namingExplicit = context.ParseResult.Tokens.Any(t =>
                    t.Value == "--naming" || t.Value == "-n"
                    || t.Value.StartsWith("--naming=", StringComparison.Ordinal)
                    || t.Value.StartsWith("-n=", StringComparison.Ordinal));
                var parallel = context.ParseResult.GetValueForOption(parallelOpt);
                var yes = context.ParseResult.GetValueForOption(yesOpt);
                var json = context.ParseResult.GetValueForOption(jsonOpt);
                var dryRun = context.ParseResult.GetValueForOption(dryRunOpt);
                var verbose = context.ParseResult.GetValueForOption(verboseOpt);
                var overwrite = context.ParseResult.GetValueForOption(overwriteOpt);
                var recursive = context.ParseResult.GetValueForOption(recursiveOpt);
                var preserveSubdirs = context.ParseResult.GetValueForOption(preserveSubdirsOpt);
                var after = context.ParseResult.GetValueForOption(afterOpt)!;

                if (singlePath != null)
                {
                    string ext = Path.GetExtension(singlePath);
                    if (!ImageExtensions.Contains(ext))
                    {
                        CliConsole.WriteErrorLine($"Error: Unsupported file type '{ext}'. Supported: .jpg, .jpeg, .heic, .heif");
                        context.ExitCode = 1;
                        return;
                    }
                    if (!File.Exists(singlePath))
                    {
                        CliConsole.WriteErrorLine($"Error: File not found: {singlePath}");
                        context.ExitCode = 1;
                        return;
                    }
                }

                context.ExitCode = await RunAsync(
                    singlePath, dir, pairingName, protocolName, formatName, output, naming, namingExplicit,
                    parallel, yes, json, dryRun, verbose,
                    overwrite, recursive, preserveSubdirs, after,
                    context.GetCancellationToken());
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            string? singlePath, DirectoryInfo? dir,
            string pairingName, string protocolName, string? formatName, DirectoryInfo? output,
            string naming, bool namingExplicit, int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs, string after,
            CancellationToken ct)
        {
            bool isSingle = singlePath != null;
            bool isBatch = dir != null;

            if (!isSingle && !isBatch)
            {
                CliConsole.WriteErrorLine("Error: Specify a live photo file, or use --dir for batch mode.");
                return 1;
            }

            if (isSingle && isBatch)
            {
                CliConsole.WriteErrorLine("Error: Cannot use both single-file and --dir batch mode.");
                return 1;
            }

            // Resolve split protocol
            if (!SplitProtocolMap.TryGetValue(protocolName.Trim(), out int protocolIndex))
            {
                CliConsole.WriteErrorLine($"Error: Unknown protocol '{protocolName}'. Valid: none, apple, vivo.");
                return 1;
            }

            // Resolve split format (default: the protocol's first available format).
            int formatIndex = GetSplitDefaultFormat(protocolIndex);
            if (formatName != null)
            {
                if (!SplitFormatMap.TryGetValue(formatName.Trim().Replace(" ", ""), out formatIndex))
                {
                    CliConsole.WriteErrorLine($"Error: Unknown format '{formatName}'. Valid: keep, jpg+mp4, jpg+mov, heic+mov.");
                    return 1;
                }

                if (!IsSplitFormatAvailable(protocolIndex, formatIndex))
                {
                    CliConsole.WriteErrorLine($"Error: Format '{formatName}' is not available for protocol '{protocolName}'.");
                    Console.Error.WriteLine("Use 'lpb protocols' to see supported combinations.");
                    return 1;
                }
            }

            // Resolve pairing filter (null = no filter, keep the current scan-everything behavior)
            if (!SplitPairingMap.TryGetValue(pairingName.Trim(), out LivePhotoProtocolType? pairingProtocol))
            {
                CliConsole.WriteErrorLine($"Error: Unknown pairing '{pairingName}'. Valid: all, fusion, v1, v2, oppo, vivo, samsung, huawei.");
                return 1;
            }

            // 单文件拆分默认加 _split 后缀避免覆盖源文件；批量默认 keep（输出在子文件夹，不会重名）
            if (!namingExplicit)
                naming = isSingle ? "suffix" : "keep";

            // Resolve naming rule: keep / suffix / custom:TEMPLATE
            string? customPattern = null;
            if (naming.Equals("keep", StringComparison.OrdinalIgnoreCase))
            {
                // outputBaseName = null → reuse the source base name.
            }
            else if (naming.Equals("suffix", StringComparison.OrdinalIgnoreCase))
            {
                customPattern = "{name}_split";
            }
            else if (naming.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                customPattern = naming.Substring(7);
            }
            else
            {
                CliConsole.WriteErrorLine($"Error: Unknown naming rule '{naming}'. Valid: keep, suffix, custom:<pattern>.");
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
                CliConsole.WriteErrorLine($"Error: Unknown after-completion action '{after}'. Valid: none, move:<dir>, recycle.");
                return 1;
            }

            // Save/restore scan settings so the CLI behaves deterministically regardless of the GUI's persisted values.
            bool? originalRecursive = null;
            bool? originalPreserve = null;
            try
            {
                originalRecursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                AppSettingsService.SetValue("IsRecursiveScanEnabled", recursive);
                originalPreserve = AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false);
                AppSettingsService.SetValue("IsOutputPreserveSubfolderStructure", preserveSubdirs);
            }
            catch { /* non-packaged CLI: best effort */ }

            try
            {
                // Resolve output directory:
                //   单文件模式默认输出到源文件所在目录；批量模式默认在输入目录下新建 {目录名}_split 子文件夹。
                string outputDir = output?.FullName ?? (isSingle
                    ? Path.GetDirectoryName(Path.GetFullPath(singlePath!))!
                    : DefaultBatchOutputDirectory(dir!.FullName));

                LogService.Split(
                    $"Command config: mode={(isSingle ? "single" : "batch")} " +
                    $"protocol={SplitProtocolDisplayNames[protocolIndex]}({protocolIndex}) " +
                    $"format={SplitFormatDisplayNames[formatIndex]}({formatIndex}) " +
                    $"pairing={pairingName} naming={naming} output={outputDir} overwrite={overwrite} dryRun={dryRun}");
                if (isBatch)
                    LogService.Split(
                        $"Batch: dir={dir!.FullName} parallel={parallel} recursive={recursive} " +
                        $"preserveSubdirs={preserveSubdirs} after={after}");

                if (!json)
                {
                    CliConsole.WriteField("Protocol", SplitProtocolDisplayNames[protocolIndex], width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteField("Format", SplitFormatDisplayNames[formatIndex], width: 10, valueColor: CliConsole.Highlight);
                    if (pairingProtocol != null)
                        CliConsole.WriteField("Pairing", pairingName, width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteFieldRgb("Output", outputDir, width: 10, valueColor: CliConsole.PathGreen);
                }

                if (isSingle)
                {
                    if (!json)
                        CliConsole.WriteFieldRgb("Source", singlePath!, width: 10, valueColor: CliConsole.PathGreen);

                    // 配对协议过滤（单文件模式，等价 GUI 的 PassesProtocolFilter）
                    if (!PassesPairingFilter(singlePath!, GetSingleFileType(singlePath!), pairingProtocol))
                    {
                        if (json) PrintSingleJson(singlePath!, "", "", "skipped", reason: $"not a {pairingName} live photo");
                        else CliConsole.WriteErrorLine($"Skipped: '{Path.GetFileName(singlePath)}' is not a {pairingName} live photo.");
                        return 0;
                    }

                    if (dryRun)
                    {
                        LogService.Split("DRY RUN: would split 1 file.");
                        if (json) PrintSingleJson(singlePath!, "", "", "would-split");
                        else
                        {
                            Console.Write("[DRY RUN] Would split ");
                            CliConsole.Write("1", CliConsole.Highlight);
                            Console.WriteLine(" file.");
                        }
                        return 0;
                    }

                    if (!yes && !json)
                    {
                        Console.Write("Proceed? [Y/n] ");
                        var key = Console.ReadLine();
                        if (key is null ||
                            string.Equals(key, "n", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(key, "no", StringComparison.OrdinalIgnoreCase))
                        {
                            CliConsole.WriteLine("Cancelled.", CliConsole.Muted);
                            return 0;
                        }
                    }

                    Directory.CreateDirectory(outputDir);
                    return await SplitSingleAsync(
                        singlePath!, outputDir, protocolIndex, formatIndex, customPattern,
                        overwrite, verbose, json, afterMoveDir, afterRecycle, ct);
                }
                else
                {
                    return await SplitBatchAsync(
                        dir!.FullName, outputDir, pairingProtocol, protocolIndex, formatIndex, customPattern,
                        parallel, yes, json, dryRun, verbose,
                        overwrite, preserveSubdirs, afterMoveDir, afterRecycle, ct);
                }
            }
            finally
            {
                // Restore original scan settings
                if (originalRecursive.HasValue)
                {
                    try { AppSettingsService.SetValue("IsRecursiveScanEnabled", originalRecursive.Value); }
                    catch { /* best effort */ }
                }
                if (originalPreserve.HasValue)
                {
                    try { AppSettingsService.SetValue("IsOutputPreserveSubfolderStructure", originalPreserve.Value); }
                    catch { /* best effort */ }
                }
            }
        }

        // 批量模式默认输出目录：在输入目录下新建 {输入目录名}_split 子文件夹。
        private static string DefaultBatchOutputDirectory(string inputDir)
        {
            string dirName = Path.GetFileName(inputDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dirName))
                dirName = "output"; // 盘符根目录等极端情况
            return Path.Combine(inputDir, $"{dirName}_split");
        }

        // 渲染拆分输出基本名：keep 返回 null（用源名）；custom:TEMPLATE 用模板渲染（拆分模板不含 {protocol} 片段）。
        private static string? ComputeOutputBaseName(
            string baseName, string? customPattern, int protocolIndex, int taskIndex, string sourcePath)
        {
            if (customPattern == null)
                return null;

            string rendered = LivePhotoMergeService.RenderNamingTemplate(
                customPattern, baseName, protocolIndex, taskIndex, sourcePath);
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(rendered.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return cleaned.Trim('_', '-', ' ', '+');
        }

        // 单文件模式按扩展名推断容器类型（扩展名已在入参处校验为 .jpg/.jpeg/.heic/.heif）。
        private static LivePhotoType GetSingleFileType(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase)
                ? LivePhotoType.SingleFileHeic
                : LivePhotoType.SingleFileJpeg;
        }

        // 配对协议过滤：target 为 null（--pairing all）不过滤；否则用 Core 的协议检测比对。
        // 等价 GUI SplitViewModel.PassesProtocolFilter。
        private static bool PassesPairingFilter(string filePath, LivePhotoType type, LivePhotoProtocolType? target)
        {
            if (target == null) return true;
            return LivePhotoProtocolDetector.Detect(filePath, type) == target.Value;
        }

        private static async Task<int> SplitSingleAsync(
            string sourcePath, string outputDir, int protocolIndex, int formatIndex,
            string? customPattern, bool overwrite, bool verbose, bool json,
            string? afterMoveDir, bool afterRecycle, CancellationToken ct)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            try
            {
                string? outputBaseName = ComputeOutputBaseName(baseName, customPattern, protocolIndex, 1, sourcePath);
                if (verbose && !json)
                    Console.WriteLine($"Splitting: {baseName} ...");

                var result = await LivePhotoSplitService.SplitAsync(
                    sourcePath, outputDir, protocolIndex, formatIndex, ct,
                    inputDirectory: null, outputBaseName: outputBaseName, overwriteExisting: overwrite);

                if (json)
                {
                    PrintSingleJson(sourcePath, result.ImageOutputPath, result.VideoOutputPath, "split");
                }
                else if (verbose)
                {
                    CliConsole.Write("OK  ", CliConsole.Success);
                    Console.WriteLine(baseName);
                    Console.WriteLine($"    -> {result.ImageOutputPath}");
                    Console.WriteLine($"    -> {result.VideoOutputPath}");
                }
                else
                {
                    CliConsole.Write("OK  ", CliConsole.Success);
                    Console.WriteLine(baseName);
                }

                ApplyAfterAction(sourcePath, baseName, afterMoveDir, afterRecycle, json);
                return 0;
            }
            catch (OperationCanceledException)
            {
                if (json) PrintSingleJson(sourcePath, "", "", "cancelled");
                else CliConsole.WriteErrorLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                LogService.Error($"Split failed for {baseName}: {ex.Message}", ex, LogSource.Split);
                if (json) PrintSingleJson(sourcePath, "", "", "failed", reason: $"{ex.GetType().Name}: {ex.Message}");
                else
                {
                    CliConsole.WriteErrorLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                    if (verbose) Console.Error.WriteLine(ex.StackTrace);
                }
                return 1;
            }
            finally
            {
                // SplitService 会清理自身临时文件但保留 Temp 目录（供并发任务共享），此处单文件模式收尾清掉空目录。
                try { if (Directory.Exists(Path.Combine(outputDir, "Temp"))) Directory.Delete(Path.Combine(outputDir, "Temp"), recursive: true); }
                catch { /* best effort */ }
            }
        }

        private static async Task<int> SplitBatchAsync(
            string inputDir, string outputDir,
            LivePhotoProtocolType? pairingProtocol, int protocolIndex, int formatIndex, string? customPattern,
            int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool preserveSubdirs,
            string? afterMoveDir, bool afterRecycle, CancellationToken ct)
        {
            // 1. Scan — single-file live photos only (mirrors the GUI SplitViewModel scan).
            if (!json)
            {
                if (CliConsole.UseColor)
                {
                    CliConsole.Write("Scanning", CliConsole.Accent);
                    Console.Write(": ");
                    CliConsole.Write(inputDir, CliConsole.PathGreen);
                    Console.Write(" ... ");
                }
                else
                {
                    Console.Write($"Scanning: {inputDir} ... ");
                }
            }
            var discovery = await LivePhotoDiscoveryService.ScanAsync(inputDir, DiscoveryScanMode.SplitOnly, ct);
            var liveItems = discovery.Items
                .Where(i => i.LivePhotoType == LivePhotoType.SingleFileJpeg
                         || i.LivePhotoType == LivePhotoType.SingleFileHeic)
                .Where(i => PassesPairingFilter(i.FilePath, i.LivePhotoType, pairingProtocol))
                .ToList();

            if (!json)
            {
                CliConsole.Write(liveItems.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" single-file live photos found");
            }

            if (liveItems.Count == 0)
            {
                if (json)
                    PrintBatchJson(inputDir, outputDir, 0, 0, 0, 0, new List<SplitJsonFileEntry>());
                else
                    CliConsole.WriteErrorLine("No single-file live photos found. Nothing to do.");
                return 0;
            }

            // 2. Build task list
            var tasks = new List<SplitTaskInfo>(liveItems.Count);
            var jsonFiles = new List<SplitJsonFileEntry>();
            for (int i = 0; i < liveItems.Count; i++)
            {
                var item = liveItems[i];
                var task = new SplitTaskInfo
                {
                    Index = i + 1,
                    SourcePath = item.FilePath,
                    BaseName = Path.GetFileNameWithoutExtension(item.FilePath),
                };
                if (json)
                {
                    task.Json = new SplitJsonFileEntry
                    {
                        Path = item.FilePath,
                        Name = Path.GetFileNameWithoutExtension(item.FilePath),
                        Status = dryRun ? "would-split" : "pending",
                    };
                    jsonFiles.Add(task.Json);
                }
                tasks.Add(task);
            }

            // 3. Dry run
            if (dryRun)
            {
                LogService.Split($"DRY RUN: would split {tasks.Count} files.");
                if (json)
                    PrintBatchJson(inputDir, outputDir, tasks.Count, 0, 0, 0, jsonFiles);
                else
                {
                    Console.WriteLine();
                    Console.Write("[DRY RUN] Would split ");
                    CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" files:");
                    foreach (var t in tasks)
                    {
                        Console.Write("  ");
                        CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                        Console.Write("  ");
                        CliConsole.Write(t.BaseName, CliConsole.PathGreen);
                        Console.WriteLine();
                    }
                }
                return 0;
            }

            // 4. Confirmation
            if (!yes && !json)
            {
                Console.Write("\nSplit ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.Write(" files? [Y/n] ");
                var key = Console.ReadLine();
                if (key is null ||
                    string.Equals(key, "n", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "no", StringComparison.OrdinalIgnoreCase))
                {
                    CliConsole.WriteLine("Cancelled.", CliConsole.Muted);
                    return 0;
                }
            }

            // 5. Run batch（实际处理前才创建输出目录，dry-run / 取消不产生副作用）
            Directory.CreateDirectory(outputDir);
            if (!json)
            {
                Console.Write("\nProcessing ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.Write(" files (parallel=");
                CliConsole.Write(parallel.ToString(), CliConsole.Highlight);
                Console.WriteLine(")...");
                Console.WriteLine();
            }

            int ok = 0, fail = 0, completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, parallel));
            var running = tasks.Select(async task =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (verbose && !json)
                        Console.WriteLine($"  [{task.Index}/{tasks.Count}] {task.BaseName} ...");

                    string? outputBaseName = ComputeOutputBaseName(
                        task.BaseName, customPattern, protocolIndex, task.Index, task.SourcePath);

                    try
                    {
                        var result = await LivePhotoSplitService.SplitAsync(
                            task.SourcePath, outputDir, protocolIndex, formatIndex, ct,
                            inputDirectory: preserveSubdirs ? inputDir : null,
                            outputBaseName: outputBaseName,
                            overwriteExisting: overwrite);

                        task.Status = ProcessStatus.Success;
                        if (task.Json != null) task.Json.Status = "split";
                        Interlocked.Increment(ref ok);
                        int c = Interlocked.Increment(ref completed);
                        if (!json)
                        {
                            if (verbose)
                            {
                                CliConsole.WriteLine("OK", CliConsole.Success);
                                Console.WriteLine($"    -> {result.ImageOutputPath}");
                                Console.WriteLine($"    -> {result.VideoOutputPath}");
                            }
                            else
                            {
                                Console.Write($"  [{c}/{tasks.Count}] ");
                                CliConsole.Write("OK  ", CliConsole.Success);
                                Console.WriteLine(task.BaseName);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        task.Status = ProcessStatus.Failed;
                        task.Details = ex.Message;
                        if (task.Json != null) { task.Json.Status = "failed"; task.Json.Reason = ex.Message; }
                        Interlocked.Increment(ref fail);
                        int c = Interlocked.Increment(ref completed);
                        if (!json)
                        {
                            if (verbose)
                                CliConsole.WriteLine($"FAIL ({ex.Message})", CliConsole.Error);
                            else
                            {
                                Console.Write($"  [{c}/{tasks.Count}] ");
                                CliConsole.Write("FAIL  ", CliConsole.Error);
                                Console.WriteLine($"{task.BaseName}  ({ex.Message})");
                            }
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(running);

            // 清理 SplitService 遗留的 Temp 目录（SplitService 已删除自身临时文件，仅剩空目录）
            try { if (Directory.Exists(Path.Combine(outputDir, "Temp"))) Directory.Delete(Path.Combine(outputDir, "Temp"), recursive: true); }
            catch { /* best effort */ }

            // 6. After-completion actions (only on successful tasks)
            if (!string.IsNullOrEmpty(afterMoveDir))
            {
                if (!json)
                {
                    if (CliConsole.UseColor)
                    {
                        Console.WriteLine();
                        Console.Write("Moving source files to '");
                        CliConsole.Write(afterMoveDir, CliConsole.PathGreen);
                        Console.WriteLine("'...");
                    }
                    else
                    {
                        Console.WriteLine($"\nMoving source files to '{afterMoveDir}'...");
                    }
                }
                Directory.CreateDirectory(afterMoveDir);
                int moved = 0;
                foreach (var task in tasks.Where(t => t.Status == ProcessStatus.Success))
                {
                    try
                    {
                        if (File.Exists(task.SourcePath))
                        {
                            File.Move(task.SourcePath, Path.Combine(afterMoveDir, Path.GetFileName(task.SourcePath)));
                            moved++;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!json) CliConsole.WriteErrorLine($"  WARN: Failed to move '{task.BaseName}': {ex.Message}");
                    }
                }
                if (!json)
                {
                    Console.Write("  Moved ");
                    CliConsole.Write(moved.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" source files.");
                }
            }
            else if (afterRecycle)
            {
                if (!json) Console.WriteLine("\nMoving source files to recycle bin...");
                int recycled = 0;
                foreach (var task in tasks.Where(t => t.Status == ProcessStatus.Success))
                {
                    try
                    {
                        if (File.Exists(task.SourcePath))
                        {
                            MoveToRecycleBin(task.SourcePath);
                            recycled++;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!json) CliConsole.WriteErrorLine($"  WARN: Failed to recycle '{task.BaseName}': {ex.Message}");
                    }
                }
                if (!json)
                {
                    Console.Write("  Recycled ");
                    CliConsole.Write(recycled.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" source files.");
                }
            }

            // 7. Summary
            if (json)
                PrintBatchJson(inputDir, outputDir, tasks.Count, ok, fail, 0, jsonFiles);
            else
            {
                Console.WriteLine();
                CliConsole.Write("Done: ", CliConsole.Accent);
                CliConsole.Write(ok.ToString(), CliConsole.Highlight);
                Console.Write(" OK, ");
                CliConsole.Write(fail.ToString(), CliConsole.Highlight);
                Console.Write(" FAIL, ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" total");
            }
            return fail > 0 ? 1 : 0;
        }

        private static void ApplyAfterAction(string sourcePath, string baseName, string? afterMoveDir, bool afterRecycle, bool json)
        {
            if (!string.IsNullOrEmpty(afterMoveDir))
            {
                Directory.CreateDirectory(afterMoveDir);
                try
                {
                    if (File.Exists(sourcePath))
                        File.Move(sourcePath, Path.Combine(afterMoveDir, Path.GetFileName(sourcePath)));
                }
                catch (Exception ex)
                {
                    if (!json) CliConsole.WriteErrorLine($"  WARN: Failed to move '{baseName}': {ex.Message}");
                }
            }
            else if (afterRecycle)
            {
                try
                {
                    if (File.Exists(sourcePath))
                        MoveToRecycleBin(sourcePath);
                }
                catch (Exception ex)
                {
                    if (!json) CliConsole.WriteErrorLine($"  WARN: Failed to recycle '{baseName}': {ex.Message}");
                }
            }
        }

        // 序列化 JSON 到 stdout（脚本模式 --json 用，方便脚本稳定解析，不受文件名长度/终端宽度影响）。
        private static void PrintJson(object data)
            => Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

        // 单文件模式的 JSON 结果。
        private static void PrintSingleJson(string input, string imageOutput, string videoOutput, string status, string reason = "")
            => PrintJson(new { command = "split", mode = "single", input, imageOutput, videoOutput, status, reason });

        // 批量模式的 JSON 结果。
        private static void PrintBatchJson(string input, string output, int scanned, int split, int failed, int skipped, List<SplitJsonFileEntry> files)
            => PrintJson(new { command = "split", mode = "batch", input, output, scanned, split, failed, skipped, files });

        private sealed class SplitTaskInfo
        {
            public int Index;
            public string SourcePath = "";
            public string BaseName = "";
            public ProcessStatus Status = ProcessStatus.Pending;
            public string Details = "";
            public SplitJsonFileEntry? Json = null; // 脚本模式（--json）关联的 JSON 结果条目
        }

        // 脚本模式（--json）下单个文件的 JSON 结果条目。
        private sealed class SplitJsonFileEntry
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";  // split / would-split / failed / skipped
            public string Reason { get; set; } = "";  // 跳过/失败原因
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
