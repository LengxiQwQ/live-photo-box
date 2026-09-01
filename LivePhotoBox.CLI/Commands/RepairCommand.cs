/*
 * RepairCommand.cs
 *
 * 修复命令：分析并修复实况照片/视频的元数据问题（旋转、缩略图、HEIC 方向、视频旋转）。
 *
 *   - 复用 Core 层 LivePhotoRepairService.AnalyzeFileAsync / RepairAsync，与 GUI 修复页同源
 *   - 单文件与批量两种模式，默认只修复 Apple 实况照片（--all-devices 关闭过滤）
 *   - 默认 4 项修复全开，--no-* 关闭；批量确认前列出待修复清单
 */

using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.CommandLine;
using System.Text.Json;

namespace LivePhotoBox.Cli.Commands
{
    internal static class RepairCommand
    {
        // 支持的图片/视频扩展名（单文件参数与批量扫描共用）。
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".heic", ".heif" };
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mov", ".mp4" };

        // 分析分类：Repair=需修复 / Copy=完好需复制（--copy-perfect）/ Skip=跳过 / Error=读取出错。
        private enum RepairClass { Repair, Copy, Skip, Error }

        public static Command Create()
        {
            var filesArg = new Argument<string?>("files") { Description = "One image or video file to repair (.jpg/.jpeg/.heic/.heif/.mov/.mp4), or a folder path (no file extension = batch mode, same as --dir)." };
            filesArg.Arity = ArgumentArity.ZeroOrOne;

            var dirOpt = new Option<DirectoryInfo?>("--dir", "-d") { Description = "Folder with images and videos. Every detected file is analyzed and repaired. For batch mode; a folder path can also be passed as the positional argument." };
            var outputOpt = new Option<DirectoryInfo?>("--output", "-o") { Description = "Output folder. Default: a \"_repaired\" suffix next to the source for single-file; a \"{folder}_repaired\" subfolder inside the input folder for batch mode." };
            var noRotateOpt = new Option<bool>("--no-rotate") { Description = "Disable image rotation fix (jpegtran lossless rotation)." };
            var noThumbnailOpt = new Option<bool>("--no-thumbnail") { Description = "Disable embedded thumbnail stripping." };
            var noHeicOpt = new Option<bool>("--no-heic") { Description = "Disable HEIC/HEIF orientation fix." };
            var noVideoOpt = new Option<bool>("--no-video") { Description = "Disable video rotation bake (FFmpeg re-encode)." };

            var allDevicesOpt = new Option<bool>("--all-devices") { Description = "Repair files from all devices. Default: only Apple Live Photos (ContentIdentifier UUID) are repaired." };

            var repairLongVideosOpt = new Option<bool>("--repair-long-videos") { Description = "Also repair videos longer than 3.5s (not real live photos). Default: skipped." };

            var copyPerfectOpt = new Option<bool>("--copy-perfect") { Description = "Also copy files that need no repair to the output folder (batch mode only)." };

            var parallelOpt = new Option<int>("--parallel", "-j") { DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 5), Description = "How many files to process at once (1-64). More = faster CPU usage." };
            var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip confirmation prompts. Useful for scripts / automation." };
            var jsonOpt = new Option<bool>("--json") { Description = "Output machine-readable JSON to stdout (implies --yes)." };

            var dryRunOpt = new Option<bool>("--dry-run") { Description = "Preview: show what would be done, don't actually process files." };

            var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "Show per-file status messages instead of summary only." };
            var overwriteOpt = new Option<bool>("--overwrite", "-w") { Description = "Replace existing files. Without this, name conflicts get auto-renamed (photo.jpg -> photo (2).jpg)." };
            var recursiveOpt = new Option<bool>("--recursive", "-r") { Description = "Also scan subdirectories inside the input folder." };
            var preserveSubdirsOpt = new Option<bool>("--preserve-subdirs", "-s") { Description = "Keep source subdirectory structure in the output folder." };
            var cmd = new Command("repair",
                "Analyze and repair live photo metadata problems.\n" +
                "Fixes image rotation, embedded thumbnails, HEIC orientation and video rotation.\n" +
                "Images: .jpg .jpeg .heic .heif   Videos: .mov .mp4\n\n" +
                "Single file: lpb repair photo.jpg\n" +
                "             (writes photo_repaired.jpg next to the source)\n" +
                "Batch:       lpb repair ./MyPhotos -y\n" +
                "             (folder auto-detected by missing extension; -d/--dir also works)\n" +
                "             (writes ./MyPhotos/MyPhotos_repaired/)\n" +
                "Disable fix: lpb repair photo.jpg --no-rotate --no-thumbnail\n" +
                "All devices: lpb repair -d ./MyPhotos --all-devices\n" +
                "Copy intact: lpb repair -d ./MyPhotos --copy-perfect\n" +
                "Preview:     lpb repair -d ./MyPhotos --dry-run\n" +
                "Wildcards:   not supported — pass a folder or explicit files")
            {
                filesArg,
                dirOpt, outputOpt,
                noRotateOpt, noThumbnailOpt, noHeicOpt, noVideoOpt,
                allDevicesOpt, repairLongVideosOpt, copyPerfectOpt,
                parallelOpt, yesOpt, jsonOpt, dryRunOpt, verboseOpt,
                overwriteOpt, recursiveOpt, preserveSubdirsOpt
            };

            cmd.SetAction(async (parseResult, cancellationToken) =>
            {
                string? singlePath = parseResult.GetValue(filesArg);
                var dir = parseResult.GetValue(dirOpt);
                var output = parseResult.GetValue(outputOpt);
                var noRotate = parseResult.GetValue(noRotateOpt);
                var noThumbnail = parseResult.GetValue(noThumbnailOpt);
                var noHeic = parseResult.GetValue(noHeicOpt);
                var noVideo = parseResult.GetValue(noVideoOpt);
                var allDevices = parseResult.GetValue(allDevicesOpt);
                var repairLongVideos = parseResult.GetValue(repairLongVideosOpt);
                var copyPerfect = parseResult.GetValue(copyPerfectOpt);
                var parallel = parseResult.GetValue(parallelOpt);
                var yes = parseResult.GetValue(yesOpt);
                var json = parseResult.GetValue(jsonOpt);
                var dryRun = parseResult.GetValue(dryRunOpt);
                var verbose = parseResult.GetValue(verboseOpt);
                var overwrite = parseResult.GetValue(overwriteOpt);
                var recursive = parseResult.GetValue(recursiveOpt);
                var preserveSubdirs = parseResult.GetValue(preserveSubdirsOpt);

                if (singlePath != null)
                {
                    // 通配符在 cmd/PowerShell 下原样传递（Git Bash 会先展开），统一给出友好提示
                    if (CliInputValidator.HasWildcard(singlePath))
                    {
                        CliInputValidator.WriteWildcardNotSupported();
                        Environment.ExitCode = 1;
                        return;
                    }

                    // System.CommandLine 会把未知选项当成位置参数（文件名）吞掉，提前识别避免误导性报错
                    if (CliInputValidator.IsUnknownOption(singlePath, ImageExtensions.Concat(VideoExtensions)))
                    {
                        CliInputValidator.WriteUnknownOptionError(singlePath, cmd);
                        Environment.ExitCode = 1;
                        return;
                    }

                    // 位置参数不带扩展名即自动识别为目录（批量模式，等价 -d/--dir）；
                    // 已存在的目录优先，目录名含点（如 "My.Photos"）也能正确识别。
                    var folderStatus = CliInputValidator.ResolveFolderInput(
                        singlePath, "images need .jpg/.jpeg/.heic/.heif, videos need .mov/.mp4", ref dir);
                    if (folderStatus == CliInputValidator.FolderInputStatus.NotFound)
                    {
                        Environment.ExitCode = 1;
                        return;
                    }
                    if (folderStatus == CliInputValidator.FolderInputStatus.Resolved)
                    {
                        singlePath = null;
                    }
                    else
                    {
                        string ext = Path.GetExtension(singlePath);
                        if (!ImageExtensions.Contains(ext) && !VideoExtensions.Contains(ext))
                        {
                            CliConsole.WriteErrorLine($"Error: Unsupported file type '{ext}'. Supported: .jpg, .jpeg, .heic, .heif, .mov, .mp4");
                            Environment.ExitCode = 1;
                            return;
                        }
                        if (!CliInputValidator.ValidateInputFile(new FileInfo(singlePath)))
                        {
                            Environment.ExitCode = 1;
                            return;
                        }
                    }
                }

                // 批量目录必须存在（-d/--dir 兜底）、输出路径不能是文件、并发数 1..64
                if (!CliInputValidator.ValidateInputDirectory(dir)
                    || !CliInputValidator.ValidateOutputDirectory(output)
                    || !CliInputValidator.ValidateParallel(parallel))
                {
                    Environment.ExitCode = 1;
                    return;
                }

                Environment.ExitCode = await RunAsync(
                    singlePath, dir, output,
                    noRotate, noThumbnail, noHeic, noVideo,
                    allDevices, repairLongVideos, copyPerfect,
                    parallel, yes, json, dryRun, verbose,
                    overwrite, recursive, preserveSubdirs,
                    cancellationToken);
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            string? singlePath, DirectoryInfo? dir, DirectoryInfo? output,
            bool noRotate, bool noThumbnail, bool noHeic, bool noVideo,
            bool allDevices, bool repairLongVideos, bool copyPerfect,
            int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs,
            CancellationToken ct)
        {
            try
            {
                return await ProcessingPipelineRouter.RunAsync("repair", () => RunLegacyAsync(
                    singlePath, dir, output, noRotate, noThumbnail, noHeic, noVideo,
                    allDevices, repairLongVideos, copyPerfect, parallel, yes, json, dryRun,
                    verbose, overwrite, recursive, preserveSubdirs, ct));
            }
            catch (RebuiltPipelineNotReadyException exception)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", errorCode = "rebuilt_not_ready", operation = exception.Operation, error = exception.Message }));
                else
                    CliConsole.WriteErrorLine($"Error: {exception.Message}");
                return 1;
            }
        }

        private static async Task<int> RunLegacyAsync(
            string? singlePath, DirectoryInfo? dir, DirectoryInfo? output,
            bool noRotate, bool noThumbnail, bool noHeic, bool noVideo,
            bool allDevices, bool repairLongVideos, bool copyPerfect,
            int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs,
            CancellationToken ct)
        {
            bool isSingle = singlePath != null;
            bool isBatch = dir != null;

            if (!isSingle && !isBatch)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: Specify a file to repair, or use --dir for batch mode.",
                    "Examples: 'lpb repair photo.jpg' or 'lpb repair ./MyPhotos -y'.");
                return 1;
            }

            if (isSingle && isBatch)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: Cannot use both single-file and --dir batch mode.",
                    "Pass either a single file or a folder, not both.");
                return 1;
            }

            // --copy-perfect 是批量专属选项：单文件模式下静默忽略会误导用户，改成明确报错。
            if (copyPerfect && isSingle)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: --copy-perfect only works in batch mode.",
                    "Use it with a folder, e.g. 'lpb repair -d ./MyPhotos --copy-perfect -y'.");
                return 1;
            }

            // 构建修复选项（默认全开，--no-* 关闭对应项）
            var options = new RepairOptions
            {
                FixImageRotation = !noRotate,
                StripImageThumbnail = !noThumbnail,
                FixHeicOrientation = !noHeic,
                FixVideoRotation = !noVideo
            };

            // 四项全关且未请求复制完好文件 → 无操作可执行
            if (noRotate && noThumbnail && noHeic && noVideo && !copyPerfect)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: All repair options are disabled. Nothing to repair.",
                    "Re-enable at least one fix (omit the --no-* flags) or add --copy-perfect to copy intact files.");
                return 1;
            }

            // Apple 过滤：默认只修 Apple 实况照片（ContentIdentifier），--all-devices 关闭
            bool appleOnly = !allDevices;

            // Save/restore 递归扫描设置，保证 CLI 行为确定（不受 GUI 持久化设置影响）
            bool? originalRecursive = null;
            try
            {
                originalRecursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                AppSettingsService.SetValue("IsRecursiveScanEnabled", recursive);
            }
            catch { /* non-packaged CLI: best effort */ }

            try
            {
                // 输出目录：单文件默认源目录；批量默认 {dir}_repaired 子文件夹。
                string outputDir = output?.FullName ?? (isSingle
                    ? Path.GetDirectoryName(Path.GetFullPath(singlePath!))!
                    : DefaultBatchOutputDirectory(dir!.FullName));

                LogService.Repair(
                    $"Command config: mode={(isSingle ? "single" : "batch")} " +
                    $"rotation={options.FixImageRotation} thumbnail={options.StripImageThumbnail} " +
                    $"heic={options.FixHeicOrientation} video={options.FixVideoRotation} " +
                    $"appleOnly={appleOnly} repairLongVideos={repairLongVideos} copyPerfect={copyPerfect} " +
                    $"output={outputDir} overwrite={overwrite} dryRun={dryRun}");
                if (isBatch)
                    LogService.Repair(
                        $"Batch: dir={dir!.FullName} parallel={parallel} recursive={recursive} " +
                        $"preserveSubdirs={preserveSubdirs}");

                if (!json)
                {
                    // 单文件模式：把文件名放到最顶部，确认界面不再重复打印 Source 全路径
                    if (isSingle)
                        CliConsole.WriteFieldRgb("Filename", Path.GetFileName(singlePath!), width: 10, valueColor: CliConsole.PathGreen);
                    CliConsole.WriteField("Fixes", BuildFixSummary(options), width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteField("Devices", appleOnly ? "Apple only" : "All devices", width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteFieldRgb("Output", outputDir, width: 10, valueColor: CliConsole.PathGreen);
                }

                if (isSingle)
                    return await RepairSingleAsync(
                        singlePath!, outputDir, options,
                        appleOnly, repairLongVideos,
                        yes, json, dryRun, verbose, overwrite, ct);
                else
                    return await RepairBatchAsync(
                        dir!.FullName, outputDir, options,
                        appleOnly, repairLongVideos, copyPerfect,
                        parallel, yes, json, dryRun, verbose,
                        overwrite, preserveSubdirs, ct);
            }
            finally
            {
                // 恢复原始递归扫描设置
                if (originalRecursive.HasValue)
                {
                    try { AppSettingsService.SetValue("IsRecursiveScanEnabled", originalRecursive.Value); }
                    catch { /* best effort */ }
                }
            }
        }

        // 单文件修复：analyze → 分类 → RepairAsync，输出到 {name}_repaired{ext}。
        // json（--json）输出 JSON；否则输出彩色人类可读文本。
        private static async Task<int> RepairSingleAsync(
            string sourcePath, string outputDir, RepairOptions options,
            bool appleOnly, bool repairLongVideos,
            bool yes, bool json, bool dryRun, bool verbose, bool overwrite, CancellationToken ct)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            string ext = Path.GetExtension(sourcePath);
            string outputName = $"{baseName}_repaired{ext}";
            string estimatedPath = Path.Combine(outputDir, outputName);

            if (!json)
            {
                CliConsole.WriteFieldRgb("File", Path.GetFileName(estimatedPath), width: 10, valueColor: CliConsole.PathGreen);
            }

            // Apple 过滤：单文件只检测这一个文件
            HashSet<string>? appleFiles = null;
            if (appleOnly)
                appleFiles = await DetectAppleFilesAsync(new List<string> { sourcePath }, json, ct);

            // 分析 + 分类（单文件不复制完好文件，--copy-perfect 仅批量生效）
            var (analysis, cls, reason) = await ClassifyAsync(sourcePath, appleFiles, repairLongVideos, copyPerfect: false, options, ct);
            string issue = analysis.IssueDescription.Replace("\n", " | ");

            switch (cls)
            {
                case RepairClass.Error:
                    if (json) PrintSingleJson(sourcePath, estimatedPath, "error", reason: issue);
                    else CliConsole.WriteErrorLine($"ERROR: {analysis.IssueDescription}");
                    return 1;
                case RepairClass.Repair:
                    break; // 继续修复
                default: // Skip / Copy（单文件 Copy 不会发生）
                    if (json) PrintSingleJson(sourcePath, estimatedPath, "skipped", reason: reason);
                    else { Console.Write("Skipped: "); CliConsole.WriteLine(reason, CliConsole.Muted); }
                    return 0;
            }

            if (!json)
                CliConsole.WriteField("Issue", issue, width: 10, valueColor: CliConsole.Highlight);

            if (dryRun)
            {
                if (json) PrintSingleJson(sourcePath, estimatedPath, "would-repair", issue: issue);
                else
                {
                    LogService.Repair($"DRY RUN: would repair 1 file.");
                    Console.Write("[DRY RUN] Would repair ");
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
            string actualPath = overwrite
                ? Path.Combine(outputDir, outputName)
                : PathHelper.GetUniqueFilePath(outputDir, outputName);

            try
            {
                var result = await LivePhotoRepairService.RepairAsync(sourcePath, actualPath, analysis, ct, options);
                if (result.Success)
                {
                    if (json) PrintSingleJson(sourcePath, actualPath, "repaired", issue: issue);
                    else
                    {
                        CliConsole.WriteLine("Done", CliConsole.Success);
                        if (verbose) Console.WriteLine($"-> {actualPath}");
                    }
                    return 0;
                }
                else
                {
                    if (json) PrintSingleJson(sourcePath, actualPath, "failed", reason: result.Message);
                    else CliConsole.WriteErrorLine($"FAIL  {Path.GetFileName(sourcePath)}  {result.Message}");
                    return 1;
                }
            }
            catch (OperationCanceledException)
            {
                if (json) PrintSingleJson(sourcePath, actualPath, "cancelled");
                else CliConsole.WriteErrorLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                LogService.Repair($"Repair failed for {baseName}: {ex.Message}", LogLevel.Error, ex);
                if (json) PrintSingleJson(sourcePath, actualPath, "failed", reason: $"{ex.GetType().Name}: {ex.Message}");
                else
                {
                    CliConsole.WriteErrorLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                    if (verbose) Console.Error.WriteLine(ex.StackTrace);
                }
                return 1;
            }
        }

        // 批量修复：扫描 → Apple 过滤 → 并发 analyze/分类 → 列清单 → 确认 → 并发修复/复制。
        private static async Task<int> RepairBatchAsync(
            string inputDir, string outputDir, RepairOptions options,
            bool appleOnly, bool repairLongVideos, bool copyPerfect,
            int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool preserveSubdirs, CancellationToken ct)
        {
            // 1. 扫描媒体文件（脚本模式 --json 静默）
            if (!json)
            {
                if (CliConsole.UseColor)
                {
                    CliConsole.Write("Scanning".PadRight(10), CliConsole.Accent);
                    Console.Write(": ");
                    CliConsole.Write(inputDir, CliConsole.PathGreen);
                    Console.Write(" ... ");
                }
                else
                {
                    Console.Write($"{"Scanning".PadRight(10)}: {inputDir} ... ");
                }
            }
            var files = ScanRepairableFiles(inputDir, ct);
            if (!json)
            {
                Console.WriteLine();
                CliConsole.Write(files.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" media files found");
            }

            if (files.Count == 0)
            {
                if (json)
                    PrintBatchJson(inputDir, outputDir, 0, 0, 0, 0, 0, 0, 0, new List<JsonFileEntry>());
                else
                    CliConsole.WriteErrorLine("No media files found. Nothing to do.");
                return 0;
            }

            // 2. Apple 设备过滤（默认开启；脚本模式 --json 静默）
            HashSet<string>? appleFiles = null;
            if (appleOnly)
            {
                appleFiles = await DetectAppleFilesAsync(files, json, ct);
                if (!json && appleFiles != null)
                {
                    Console.Write("Apple detection: ");
                    CliConsole.Write(appleFiles.Count.ToString(), CliConsole.Highlight);
                    Console.Write(" / ");
                    CliConsole.Write(files.Count.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" Apple files (non-Apple files will be skipped)");
                }
            }

            // 3. 并发分析 + 分类（脚本模式 --json 额外收集 JSON 条目）
            var repairTasks = new List<RepairTaskInfo>();
            var copyTasks = new List<RepairTaskInfo>();
            var jsonFiles = new List<JsonFileEntry>();
            int skipped = 0, errors = 0;
            using (var semaphore = new SemaphoreSlim(Math.Max(1, parallel)))
            {
                var analyzeTasks = files.Select(async path =>
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var (analysis, cls, reason) = await ClassifyAsync(path, appleFiles, repairLongVideos, copyPerfect, options, ct);
                        string issue = analysis.IssueDescription.Replace("\n", " | ");

                        JsonFileEntry? jsonEntry = null;
                        if (json)
                        {
                            jsonEntry = new JsonFileEntry
                            {
                                Path = path,
                                Name = Path.GetFileNameWithoutExtension(path),
                                Status = cls switch
                                {
                                    RepairClass.Repair => dryRun ? "would-repair" : "pending",
                                    RepairClass.Copy => dryRun ? "would-copy" : "pending",
                                    RepairClass.Error => "error",
                                    _ => "skipped"
                                },
                                Issue = cls == RepairClass.Repair ? issue : "",
                                Reason = cls switch
                                {
                                    RepairClass.Skip => reason,
                                    RepairClass.Error => issue,
                                    _ => ""
                                }
                            };
                            lock (jsonFiles) jsonFiles.Add(jsonEntry);
                        }

                        var task = new RepairTaskInfo
                        {
                            SourcePath = path,
                            BaseName = Path.GetFileNameWithoutExtension(path),
                            Analysis = analysis,
                            IssueText = cls == RepairClass.Repair ? issue : reason,
                            Json = jsonEntry
                        };

                        switch (cls)
                        {
                            case RepairClass.Repair:
                                lock (repairTasks) repairTasks.Add(task);
                                break;
                            case RepairClass.Copy:
                                lock (copyTasks) copyTasks.Add(task);
                                break;
                            case RepairClass.Error:
                                Interlocked.Increment(ref errors);
                                break;
                            default:
                                Interlocked.Increment(ref skipped);
                                break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(analyzeTasks);
            }

            repairTasks.Sort((a, b) => string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));
            copyTasks.Sort((a, b) => string.Compare(a.SourcePath, b.SourcePath, StringComparison.OrdinalIgnoreCase));
            jsonFiles.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < repairTasks.Count; i++) repairTasks[i].Index = i + 1;
            for (int i = 0; i < copyTasks.Count; i++) copyTasks[i].Index = i + 1;

            // 统计（脚本模式 --json 静默）
            if (!json)
            {
                Console.Write("Need repair: ");
                CliConsole.Write(repairTasks.Count.ToString(), CliConsole.Highlight);
                if (copyPerfect)
                {
                    Console.Write("  Copy: ");
                    CliConsole.Write(copyTasks.Count.ToString(), CliConsole.Highlight);
                }
                Console.Write("  Skipped: ");
                CliConsole.Write(skipped.ToString(), CliConsole.Highlight);
                Console.Write("  Errors: ");
                CliConsole.Write(errors.ToString(), errors > 0 ? CliConsole.Error : CliConsole.Highlight);
                Console.WriteLine();
            }

            if (repairTasks.Count == 0 && copyTasks.Count == 0)
            {
                if (json)
                    PrintBatchJson(inputDir, outputDir, files.Count, appleFiles?.Count ?? files.Count, 0, 0, 0, skipped, errors, jsonFiles);
                else
                    CliConsole.WriteLine("Nothing to do.", CliConsole.Success);
                return errors > 0 ? 1 : 0;
            }

            // 4. 列出待处理清单（仅人类模式：dry-run 或非 -y 确认前）
            if (!json)
            {
                Console.WriteLine();
                PrintTaskList(repairTasks, copyTasks, copyPerfect);
            }

            // 5. Dry run
            if (dryRun)
            {
                if (json)
                    PrintBatchJson(inputDir, outputDir, files.Count, appleFiles?.Count ?? files.Count, repairTasks.Count, 0, 0, skipped, errors, jsonFiles);
                else
                {
                    LogService.Repair($"DRY RUN: would repair {repairTasks.Count} files, copy {copyTasks.Count} files.");
                    Console.Write("[DRY RUN] Would repair ");
                    CliConsole.Write(repairTasks.Count.ToString(), CliConsole.Highlight);
                    Console.Write(" and copy ");
                    CliConsole.Write(copyTasks.Count.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" files.");
                }
                return 0;
            }

            // 6. Confirmation
            if (!yes && !json)
            {
                Console.Write("\nRepair ");
                CliConsole.Write(repairTasks.Count.ToString(), CliConsole.Highlight);
                if (copyPerfect)
                {
                    Console.Write(" and copy ");
                    CliConsole.Write(copyTasks.Count.ToString(), CliConsole.Highlight);
                }
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

            // 7. Run batch（确认通过后才创建输出目录；脚本模式 --json 静默）
            Directory.CreateDirectory(outputDir);
            int total = repairTasks.Count + copyTasks.Count;
            if (!json)
            {
                Console.Write("\nProcessing ");
                CliConsole.Write(total.ToString(), CliConsole.Highlight);
                Console.Write(" files (parallel=");
                CliConsole.Write(parallel.ToString(), CliConsole.Highlight);
                Console.WriteLine(")...");
                Console.WriteLine();
            }

            int ok = 0, fail = 0, copied = 0, completed = 0;
            using (var semaphore = new SemaphoreSlim(Math.Max(1, parallel)))
            {
                var allTasks = repairTasks.Select(t => (t, IsRepair: true))
                    .Concat(copyTasks.Select(t => (t, IsRepair: false)));
                var runTasks = allTasks.Select(async item =>
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var (task, isRepair) = item;
                        if (verbose && !json)
                            Console.WriteLine($"[{task.Index}/{total}] {Path.GetFileName(task.SourcePath)} ...");

                        string? subDir = preserveSubdirs
                            ? PathHelper.GetRelativeSubDirectory(inputDir, task.SourcePath)
                            : null;
                        string fileName = Path.GetFileName(task.SourcePath);
                        string targetPath = ComputeTargetPath(outputDir, fileName, subDir, overwrite);

                        try
                        {
                            if (isRepair)
                            {
                                var result = await LivePhotoRepairService.RepairAsync(
                                    task.SourcePath, targetPath, task.Analysis, ct, options);
                                if (result.Success)
                                {
                                    task.Status = ProcessStatus.Success;
                                    if (task.Json != null) task.Json.Status = "repaired";
                                    Interlocked.Increment(ref ok);
                                    int c = Interlocked.Increment(ref completed);
                                    if (!json) PrintLine(c, total, "SUCCESS", Path.GetFileName(task.SourcePath), verbose, targetPath, CliConsole.Success);
                                }
                                else
                                {
                                    task.Status = ProcessStatus.Failed;
                                    task.Details = result.Message;
                                    if (task.Json != null) { task.Json.Status = "failed"; task.Json.Reason = result.Message; }
                                    Interlocked.Increment(ref fail);
                                    int c = Interlocked.Increment(ref completed);
                                    if (!json) PrintLine(c, total, "FAIL", Path.GetFileName(task.SourcePath), verbose, targetPath, CliConsole.Error, result.Message);
                                }
                            }
                            else
                            {
                                // 复制完好文件（--copy-perfect）
                                string? outDir = Path.GetDirectoryName(targetPath);
                                if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
                                File.Copy(task.SourcePath, targetPath, overwrite: true);
                                task.Status = ProcessStatus.Success;
                                if (task.Json != null) task.Json.Status = "copied";
                                Interlocked.Increment(ref copied);
                                int c = Interlocked.Increment(ref completed);
                                if (!json) PrintLine(c, total, "COPY", Path.GetFileName(task.SourcePath), verbose, targetPath, CliConsole.Success);
                            }
                        }
                        catch (Exception ex)
                        {
                            task.Status = ProcessStatus.Failed;
                            task.Details = ex.Message;
                            if (task.Json != null) { task.Json.Status = "failed"; task.Json.Reason = ex.Message; }
                            Interlocked.Increment(ref fail);
                            int c = Interlocked.Increment(ref completed);
                            if (!json) PrintLine(c, total, "FAIL", Path.GetFileName(task.SourcePath), verbose, targetPath, CliConsole.Error, ex.Message);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                await Task.WhenAll(runTasks);
            }

            // 8. Summary
            if (json)
                PrintBatchJson(inputDir, outputDir, files.Count, appleFiles?.Count ?? files.Count, repairTasks.Count, ok, fail, skipped, errors, jsonFiles);
            else
            {
                Console.WriteLine();
                CliConsole.Write("Done: ", CliConsole.Success);
                CliConsole.Write(ok.ToString(), CliConsole.Highlight);
                Console.Write(" SUCCESS, ");
                CliConsole.Write(fail.ToString(), CliConsole.Highlight);
                Console.Write(" FAIL, ");
                if (copyPerfect)
                {
                    CliConsole.Write(copied.ToString(), CliConsole.Highlight);
                    Console.Write(" copied, ");
                }
                CliConsole.Write(skipped.ToString(), CliConsole.Highlight);
                Console.Write(" skipped, ");
                CliConsole.Write(total.ToString(), CliConsole.Highlight);
                Console.WriteLine(" total");
            }
            return fail > 0 || errors > 0 ? 1 : 0;
        }

        // 打印待处理清单：需修复文件（含问题）+ 需复制文件（--copy-perfect）。
        private static void PrintTaskList(List<RepairTaskInfo> repairTasks, List<RepairTaskInfo> copyTasks, bool copyPerfect)
        {
            if (repairTasks.Count > 0)
            {
                Console.Write("[Repair] ");
                CliConsole.Write(repairTasks.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" files:");
                foreach (var t in repairTasks)
                {
                    CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                    Console.Write("  ");
                    CliConsole.Write(Path.GetFileName(t.SourcePath), CliConsole.PathGreen);
                    if (!string.IsNullOrEmpty(t.IssueText))
                    {
                        Console.Write("  (");
                        CliConsole.Write(t.IssueText, CliConsole.Muted);
                        Console.Write(")");
                    }
                    Console.WriteLine();
                }
            }
            if (copyPerfect && copyTasks.Count > 0)
            {
                Console.Write("[Copy] ");
                CliConsole.Write(copyTasks.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" files:");
                foreach (var t in copyTasks)
                {
                    CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                    Console.Write("  ");
                    CliConsole.Write(Path.GetFileName(t.SourcePath), CliConsole.PathGreen);
                    Console.WriteLine();
                }
            }
        }

        // 输出单行处理结果（verbose 下额外打印目标路径）。
        private static void PrintLine(int completed, int total, string tag, string fileName,
            bool verbose, string? targetPath, ConsoleColor tagColor, string? detail = null)
        {
            if (verbose)
            {
                CliConsole.Write(tag + "  ", tagColor);
                Console.WriteLine(string.IsNullOrEmpty(detail) ? fileName : $"{fileName}  ({detail})");
                if (!string.IsNullOrEmpty(targetPath))
                    Console.WriteLine($"-> {targetPath}");
            }
            else
            {
                Console.Write($"[{completed}/{total}] ");
                CliConsole.Write(tag + "  ", tagColor);
                Console.WriteLine(string.IsNullOrEmpty(detail) ? fileName : $"{fileName}  ({detail})");
            }
        }

        // 分析单个文件并分类：返回 (分析结果, 分类, 原因文本)。
        // 分类精确匹配 RepairAsync 的修复判定（含 --no-* 开关），确保 dry-run 清单与真实修复一致。
        private static async Task<(RepairAnalysisResult Analysis, RepairClass Class, string Reason)> ClassifyAsync(
            string filePath, HashSet<string>? appleFiles, bool repairLongVideos, bool copyPerfect,
            RepairOptions options, CancellationToken ct)
        {
            var analysis = await LivePhotoRepairService.AnalyzeFileAsync(filePath, null, ct);

            if (appleFiles != null && !appleFiles.Contains(filePath))
                return (analysis, RepairClass.Skip, "non-Apple device");
            if (analysis.IssueType == RepairIssueType.Error)
                return (analysis, RepairClass.Error, analysis.IssueDescription);
            if (analysis.IssueType == RepairIssueType.Perfect)
                return copyPerfect
                    ? (analysis, RepairClass.Copy, "no issues")
                    : (analysis, RepairClass.Skip, "no issues");
            if (!repairLongVideos && analysis.IsVideo
                && analysis.VideoDurationSeconds > LivePhotoConstants.MaxLivePhotoVideoDurationSeconds)
                return (analysis, RepairClass.Skip, "video longer than 3.5s");

            // 视频：--no-video 关闭旋转烘焙则跳过
            if (analysis.IsVideo)
            {
                if (!options.FixVideoRotation)
                    return (analysis, RepairClass.Skip, "video rotation disabled");
                return (analysis, RepairClass.Repair, analysis.IssueDescription);
            }

            // 图片（JPEG/HEIC）：匹配 RepairAsync 的 doJpegRotation / doThumbnailStrip / doHeicOrientFix 判定
            bool isHeic = filePath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                       || filePath.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
            bool doRotation = !isHeic && options.FixImageRotation && analysis.IssueType == RepairIssueType.NeedsRebuild;
            bool doThumbnail = options.StripImageThumbnail && analysis.HasThumbnail;
            bool doHeicOrient = isHeic && options.FixHeicOrientation && analysis.IssueType == RepairIssueType.NeedsRebuild;

            if (!doRotation && !doThumbnail && !doHeicOrient)
                return (analysis, RepairClass.Skip, "fix disabled");

            return (analysis, RepairClass.Repair, analysis.IssueDescription);
        }

        // 序列化 JSON 到 stdout（脚本模式 --json 用，方便脚本稳定解析，不受文件名长度/终端宽度影响）。
        private static void PrintJson(object data)
            => Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

        // 单文件模式的 JSON 结果。
        private static void PrintSingleJson(string input, string output, string status, string issue = "", string reason = "")
            => PrintJson(new { command = "repair", mode = "single", input, output, status, issue, reason });

        // 批量模式的 JSON 结果。
        private static void PrintBatchJson(string input, string output, int scanned, int apple, int needsRepair,
            int repaired, int failed, int skipped, int errors, List<JsonFileEntry> files)
            => PrintJson(new
            {
                command = "repair",
                mode = "batch",
                input,
                output,
                scanned,
                apple,
                needsRepair,
                repaired,
                failed,
                skipped,
                errors,
                files
            });

        // Apple 实况照片检测：读取每个文件的 ContentIdentifier UUID，返回苹果实况照片路径集。
        // exiftool 缺失或检测失败时返回 null（表示不过滤，全部当 Apple 处理）。
        private static async Task<HashSet<string>?> DetectAppleFilesAsync(List<string> filePaths, bool json, CancellationToken ct)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(exifToolPath))
            {
                if (!json) CliConsole.WriteLine("(exiftool not found, skipping Apple detection)", CliConsole.Muted);
                return null;
            }

            try
            {
                using var tool = new PersistentExifTool(exifToolPath);
                return await LivePhotoMetadataMatcher.FilterAppleDevicesAsync(filePaths, tool, ct);
            }
            catch (Exception ex)
            {
                if (!json) CliConsole.WriteLine($"(Apple detection failed: {ex.Message})", CliConsole.Muted);
                return null;
            }
        }

        // 批量扫描：枚举图片 + 视频文件（递归可选）。
        private static List<string> ScanRepairableFiles(string inputDir, CancellationToken ct)
        {
            var result = new List<string>();
            bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            try
            {
                foreach (var path in Directory.EnumerateFiles(inputDir, "*.*", searchOption))
                {
                    ct.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(path);
                    if (ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext))
                        result.Add(path);
                }
            }
            catch (UnauthorizedAccessException) { /* directory partially unreadable */ }
            catch (DirectoryNotFoundException) { throw; }
            catch (IOException) { /* best effort */ }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        // 计算目标路径：overwrite 时直接覆盖；否则用 GetUniqueFilePath 自动重命名。
        private static string ComputeTargetPath(string outputDir, string fileName, string? subDir, bool overwrite)
        {
            if (overwrite)
            {
                if (subDir != null)
                {
                    string dir = Path.Combine(outputDir, subDir);
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, fileName);
                }
                return Path.Combine(outputDir, fileName);
            }
            return PathHelper.GetUniqueFilePath(outputDir, fileName, subDir);
        }

        // 批量模式默认输出目录：在输入目录下新建 {输入目录名}_repaired 子文件夹。
        private static string DefaultBatchOutputDirectory(string inputDir)
        {
            string dirName = Path.GetFileName(inputDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dirName))
                dirName = "output"; // 盘符根目录等极端情况
            return Path.Combine(inputDir, $"{dirName}_repaired");
        }

        // 生成"启用了哪些修复"的可读文本。
        private static string BuildFixSummary(RepairOptions options)
        {
            var list = new List<string>();
            if (options.FixImageRotation) list.Add("rotation");
            if (options.StripImageThumbnail) list.Add("thumbnail");
            if (options.FixHeicOrientation) list.Add("heic-orientation");
            if (options.FixVideoRotation) list.Add("video-rotation");
            return list.Count == 0 ? "(none)" : string.Join(", ", list);
        }

        // 批量修复任务信息。
        private sealed class RepairTaskInfo
        {
            public int Index;
            public string SourcePath = "";
            public string BaseName = "";
            public string IssueText = "";
            public RepairAnalysisResult Analysis = null!;
            public ProcessStatus Status = ProcessStatus.Pending;
            public string Details = "";
            public JsonFileEntry? Json = null; // 脚本模式（--json）关联的 JSON 结果条目
        }

        // 脚本模式（--json）下单个文件的 JSON 结果条目。
        private sealed class JsonFileEntry
        {
            public string Path { get; set; } = "";
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";  // repaired / failed / skipped / would-repair / would-copy
            public string Issue { get; set; } = "";   // 问题描述（修复类）
            public string Reason { get; set; } = "";  // 跳过/失败原因
        }
    }
}
