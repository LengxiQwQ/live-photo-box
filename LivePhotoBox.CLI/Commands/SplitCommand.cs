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

            var protocolOpt = new Option<string>("--protocol", () => "none",
                "Target phone format (metadata pairing). none (split only)|apple (Apple Live Photo)|vivo (vivo Live Photo, ≤ X200).\n" +
                "Default: none. This iteration only splits the file — pairing metadata is not written yet.");
            protocolOpt.AddAlias("-p");

            var formatOpt = new Option<string>("--format", () => "keep",
                "Output format. keep (no conversion)|jpg+mp4 (H.264)|jpg+mov (H.265)|heic+mov (H.265).\nDefault: keep.");
            formatOpt.AddAlias("-f");

            var outputOpt = new Option<DirectoryInfo?>("--output",
                "Output folder. Default: the source file's own directory for a single file; a \"{folder}_split\" subfolder inside the input folder for batch mode.");
            outputOpt.AddAlias("-o");

            var namingOpt = new Option<string>("--naming", () => "keep",
                "Output filename. keep (same name)|custom:TEMPLATE.\nTemplate tokens: {name} {date} {date:yyyy-MM-dd} {time} {exif_date} {exif_time} {counter} {counter:D3}");
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

            var afterOpt = new Option<string>("--after", () => "none",
                "After successful split: none (keep source)|move:PATH (move to folder)|recycle (Windows recycle bin).");

            var cmd = new Command("split",
                "Split single-file live photos into separate image and video files.\n" +
                "Input: one live photo file (.jpg/.jpeg/.heic/.heif) with an appended video.\n\n" +
                "Single file: lpb split photo.jpg\n" +
                "             (writes photo.jpg and photo.mov next to the source)\n" +
                "Batch:      lpb split -d ./MyPhotos -y\n" +
                "             (writes ./MyPhotos/MyPhotos_split/)\n" +
                "Convert:    lpb split photo.jpg -f jpg+mp4\n" +
                "Preview:    lpb split -d ./MyPhotos --dry-run\n" +
                "Protocols:  lpb protocols")
            {
                filesArg,
                dirOpt, protocolOpt, formatOpt, outputOpt, namingOpt,
                parallelOpt, yesOpt, dryRunOpt, verboseOpt,
                overwriteOpt, recursiveOpt, preserveSubdirsOpt, afterOpt
            };

            cmd.SetHandler(async context =>
            {
                string? singlePath = context.ParseResult.GetValueForArgument(filesArg);
                var dir = context.ParseResult.GetValueForOption(dirOpt);
                var protocolName = context.ParseResult.GetValueForOption(protocolOpt)!;
                var formatName = context.ParseResult.GetValueForOption(formatOpt)!;
                var output = context.ParseResult.GetValueForOption(outputOpt);
                var naming = context.ParseResult.GetValueForOption(namingOpt)!;
                var parallel = context.ParseResult.GetValueForOption(parallelOpt);
                var yes = context.ParseResult.GetValueForOption(yesOpt);
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
                    singlePath, dir, protocolName, formatName, output, naming,
                    parallel, yes, dryRun, verbose,
                    overwrite, recursive, preserveSubdirs, after,
                    context.GetCancellationToken());
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            string? singlePath, DirectoryInfo? dir,
            string protocolName, string formatName, DirectoryInfo? output,
            string naming, int parallel, bool yes, bool dryRun, bool verbose,
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

            // Resolve split format
            if (!SplitFormatMap.TryGetValue(formatName.Trim().Replace(" ", ""), out int formatIndex))
            {
                CliConsole.WriteErrorLine($"Error: Unknown format '{formatName}'. Valid: keep, jpg+mp4, jpg+mov, heic+mov.");
                return 1;
            }

            // Resolve naming rule (split has no "suffix" — only keep / custom:TEMPLATE)
            string? customPattern = null;
            if (naming.Equals("keep", StringComparison.OrdinalIgnoreCase))
            {
                // outputBaseName = null → reuse the source base name.
            }
            else if (naming.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                customPattern = naming.Substring(7);
            }
            else
            {
                CliConsole.WriteErrorLine($"Error: Unknown naming rule '{naming}'. Valid: keep, custom:<pattern>.");
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
                    $"naming={naming} output={outputDir} overwrite={overwrite} dryRun={dryRun}");
                if (isBatch)
                    LogService.Split(
                        $"Batch: dir={dir!.FullName} parallel={parallel} recursive={recursive} " +
                        $"preserveSubdirs={preserveSubdirs} after={after}");

                CliConsole.WriteField("Protocol", SplitProtocolDisplayNames[protocolIndex], width: 10, valueColor: CliConsole.Highlight);
                CliConsole.WriteField("Format", SplitFormatDisplayNames[formatIndex], width: 10, valueColor: CliConsole.Highlight);
                CliConsole.WriteFieldRgb("Output", outputDir, width: 10, valueColor: CliConsole.PathGreen);

                if (isSingle)
                {
                    CliConsole.WriteFieldRgb("Source", singlePath!, width: 10, valueColor: CliConsole.PathGreen);

                    if (dryRun)
                    {
                        LogService.Split("DRY RUN: would split 1 file.");
                        Console.Write("[DRY RUN] Would split ");
                        CliConsole.Write("1", CliConsole.Highlight);
                        Console.WriteLine(" file.");
                        return 0;
                    }

                    if (!yes)
                    {
                        Console.Write("Proceed? [y/N] ");
                        var key = Console.ReadLine();
                        if (!string.Equals(key, "y", StringComparison.OrdinalIgnoreCase))
                        {
                            CliConsole.WriteLine("Cancelled.", CliConsole.Muted);
                            return 0;
                        }
                    }

                    Directory.CreateDirectory(outputDir);
                    return await SplitSingleAsync(
                        singlePath!, outputDir, protocolIndex, formatIndex, customPattern,
                        overwrite, verbose, afterMoveDir, afterRecycle, ct);
                }
                else
                {
                    return await SplitBatchAsync(
                        dir!.FullName, outputDir, protocolIndex, formatIndex, customPattern,
                        parallel, yes, dryRun, verbose,
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

        private static async Task<int> SplitSingleAsync(
            string sourcePath, string outputDir, int protocolIndex, int formatIndex,
            string? customPattern, bool overwrite, bool verbose,
            string? afterMoveDir, bool afterRecycle, CancellationToken ct)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            try
            {
                string? outputBaseName = ComputeOutputBaseName(baseName, customPattern, protocolIndex, 1, sourcePath);
                if (verbose)
                    Console.WriteLine($"Splitting: {baseName} ...");

                var result = await LivePhotoSplitService.SplitAsync(
                    sourcePath, outputDir, protocolIndex, formatIndex, ct,
                    inputDirectory: null, outputBaseName: outputBaseName, overwriteExisting: overwrite);

                if (verbose)
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

                ApplyAfterAction(sourcePath, baseName, afterMoveDir, afterRecycle);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CliConsole.WriteErrorLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                LogService.Error($"Split failed for {baseName}: {ex.Message}", ex, LogSource.Split);
                CliConsole.WriteErrorLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                if (verbose) Console.Error.WriteLine(ex.StackTrace);
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
            int protocolIndex, int formatIndex, string? customPattern,
            int parallel, bool yes, bool dryRun, bool verbose,
            bool overwrite, bool preserveSubdirs,
            string? afterMoveDir, bool afterRecycle, CancellationToken ct)
        {
            // 1. Scan — single-file live photos only (mirrors the GUI SplitViewModel scan).
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
            var discovery = await LivePhotoDiscoveryService.ScanAsync(inputDir, DiscoveryScanMode.SplitOnly, ct);
            var liveItems = discovery.Items
                .Where(i => i.LivePhotoType == LivePhotoType.SingleFileJpeg
                         || i.LivePhotoType == LivePhotoType.SingleFileHeic)
                .ToList();

            CliConsole.Write(liveItems.Count.ToString(), CliConsole.Highlight);
            Console.WriteLine(" single-file live photos found");

            if (liveItems.Count == 0)
            {
                CliConsole.WriteErrorLine("No single-file live photos found. Nothing to do.");
                return 0;
            }

            // 2. Build task list
            var tasks = new List<SplitTaskInfo>(liveItems.Count);
            for (int i = 0; i < liveItems.Count; i++)
            {
                var item = liveItems[i];
                tasks.Add(new SplitTaskInfo
                {
                    Index = i + 1,
                    SourcePath = item.FilePath,
                    BaseName = Path.GetFileNameWithoutExtension(item.FilePath),
                });
            }

            // 3. Dry run
            if (dryRun)
            {
                LogService.Split($"DRY RUN: would split {tasks.Count} files.");
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
                return 0;
            }

            // 4. Confirmation
            if (!yes)
            {
                Console.Write("\nSplit ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.Write(" files? [y/N] ");
                var key = Console.ReadLine();
                if (!string.Equals(key, "y", StringComparison.OrdinalIgnoreCase))
                {
                    CliConsole.WriteLine("Cancelled.", CliConsole.Muted);
                    return 0;
                }
            }

            // 5. Run batch（实际处理前才创建输出目录，dry-run / 取消不产生副作用）
            Directory.CreateDirectory(outputDir);
            Console.Write("\nProcessing ");
            CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
            Console.Write(" files (parallel=");
            CliConsole.Write(parallel.ToString(), CliConsole.Highlight);
            Console.WriteLine(")...");
            Console.WriteLine();

            int ok = 0, fail = 0, completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, parallel));
            var running = tasks.Select(async task =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (verbose)
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
                        Interlocked.Increment(ref ok);
                        int c = Interlocked.Increment(ref completed);
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
                    catch (Exception ex)
                    {
                        task.Status = ProcessStatus.Failed;
                        task.Details = ex.Message;
                        Interlocked.Increment(ref fail);
                        int c = Interlocked.Increment(ref completed);
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
                        CliConsole.WriteErrorLine($"  WARN: Failed to move '{task.BaseName}': {ex.Message}");
                    }
                }
                Console.Write("  Moved ");
                CliConsole.Write(moved.ToString(), CliConsole.Highlight);
                Console.WriteLine(" source files.");
            }
            else if (afterRecycle)
            {
                Console.WriteLine("\nMoving source files to recycle bin...");
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
                        CliConsole.WriteErrorLine($"  WARN: Failed to recycle '{task.BaseName}': {ex.Message}");
                    }
                }
                Console.Write("  Recycled ");
                CliConsole.Write(recycled.ToString(), CliConsole.Highlight);
                Console.WriteLine(" source files.");
            }

            // 7. Summary
            Console.WriteLine();
            CliConsole.Write("Done: ", CliConsole.Accent);
            CliConsole.Write(ok.ToString(), CliConsole.Highlight);
            Console.Write(" OK, ");
            CliConsole.Write(fail.ToString(), CliConsole.Highlight);
            Console.Write(" FAIL, ");
            CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
            Console.WriteLine(" total");
            return fail > 0 ? 1 : 0;
        }

        private static void ApplyAfterAction(string sourcePath, string baseName, string? afterMoveDir, bool afterRecycle)
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
                    CliConsole.WriteErrorLine($"  WARN: Failed to move '{baseName}': {ex.Message}");
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
                    CliConsole.WriteErrorLine($"  WARN: Failed to recycle '{baseName}': {ex.Message}");
                }
            }
        }

        private sealed class SplitTaskInfo
        {
            public int Index;
            public string SourcePath = "";
            public string BaseName = "";
            public ProcessStatus Status = ProcessStatus.Pending;
            public string Details = "";
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
