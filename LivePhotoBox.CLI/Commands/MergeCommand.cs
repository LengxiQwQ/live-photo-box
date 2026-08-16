using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Cli.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.CommandLine;
using System.Text.Json;

namespace LivePhotoBox.Cli.Commands
{
    internal static class MergeCommand
    {
        // Recognized file extensions for auto-detection
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".heic", ".heif" };
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mov" };

        public static Command Create()
        {
            // Positional: just drop two files, auto-detect image/video by extension
            var filesArg = new Argument<string[]>("files",
                "Image + video file pair (.jpg/.heic/.png + .mp4/.mov). Auto-detected by extension.");
            filesArg.Arity = new ArgumentArity(0, 2);

            var dirOpt = new Option<DirectoryInfo?>("--dir",
                "Folder with images + videos. Files with matching names are paired. For batch mode.");
            dirOpt.AddAlias("-d");

            var protocolOpt = new Option<string>("--protocol", () => "motion photo",
                "Target phone format. micro video (V1)|motion photo (V2)|oppo|vivo|samsung|huawei.\n" +
                "Multi-word names also work without spaces (no quotes): microvideo, motionphoto.\n" +
                "Use 'protocols' command to see all supported combinations.");
            protocolOpt.AddAlias("-p");

            var outputOpt = new Option<DirectoryInfo?>("--output",
                "Output folder. Default: image's own directory for a single pair; a \"{folder}_<protocol>\" subfolder inside the input folder for batch mode.");
            outputOpt.AddAlias("-o");

            var formatOpt = new Option<string?>("--format",
                "Container format. jpg+mp4 (most compatible)|jpg+mov (Apple-style)|heic+mp4 (compact)|heic+mov|heic+mp4-h265 (HUAWEI native, HEVC).\nDefault: first available for the chosen protocol.");
            formatOpt.AddAlias("-f");

            var namingOpt = new Option<string>("--naming", () => "keep",
                "Output filename. keep (same name)|suffix (append protocol; default for single-pair)|custom:TEMPLATE.\nTemplate tokens: {name} {protocol} {date} {date:yyyy-MM-dd} {time} {exif_date} {exif_time} {counter} {counter:D3}");
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

            var pairingOpt = new Option<string>("--pairing", () => "name",
                "How to match images with videos. name (same filename)|cid (Apple ContentIdentifier UUID)|vivo (vivo camera ID).");

            var afterOpt = new Option<string>("--after", () => "none",
                "After successful merge: none (keep source)|move:PATH (move to folder)|recycle (Windows recycle bin).");

            var allVariantsOpt = new Option<bool>("--all-variants",
                "Generate for ALL supported protocol×format combos (single-pair mode only).\n" +
                "Output goes to {output}/{name}_variants/ (default: input file's directory). Files are named {name}_{Protocol}_{Format}.ext.");

            var keyTimestampOpt = new Option<string?>("--key-timestamp",
                "Set the key photo position on the video timeline (single-pair mode only).\n" +
                "Accepts seconds (1.5), mm:ss (1:30) or hh:mm:ss (0:01:30).\n" +
                "Default: follow the source video's own timeline (Apple MOV / vivo metadata).");

            var cmd = new Command("merge",
                "Combine images and videos into phone-compatible live photos.\n" +
                "Images: .jpg .jpeg .heic .heif   Videos: .mp4 .mov\n\n" +
                "Single pair:  lpb merge photo.jpg video.mp4 -p huawei\n" +
                "              (writes next to photo.jpg as photo_huawei.jpg)\n" +
                "Batch folder: lpb merge -d ./MyPhotos -p motionphoto -y\n" +
                "              (writes ./MyPhotos/MyPhotos_motionphoto/)\n" +
                "Preview:      lpb merge -d ./MyPhotos --dry-run\n" +
                "All variants: lpb merge photo.jpg video.mp4 --all-variants\n" +
                "Key time:     lpb merge photo.jpg video.mp4 --key-timestamp 1.5\n" +
                "Formats:      lpb protocols")
            {
                filesArg,
                dirOpt, protocolOpt, outputOpt, formatOpt,
                namingOpt, parallelOpt, yesOpt, jsonOpt, dryRunOpt, verboseOpt,
                overwriteOpt, recursiveOpt, preserveSubdirsOpt, pairingOpt, afterOpt,
                allVariantsOpt, keyTimestampOpt
            };

            cmd.SetHandler(async context =>
            {
                var dir = context.ParseResult.GetValueForOption(dirOpt);

                // Auto-detect image/video from positional file arguments
                FileInfo? image = null;
                FileInfo? video = null;
                var files = context.ParseResult.GetValueForArgument(filesArg);
                // System.CommandLine 会把未知选项当成位置参数（文件名）吞掉，提前识别避免误导性报错
                if (files is { Length: > 0 })
                {
                    string? unknown = files.FirstOrDefault(f =>
                        f.StartsWith('-') && !ImageExtensions.Contains(Path.GetExtension(f)) && !VideoExtensions.Contains(Path.GetExtension(f)));
                    if (unknown != null)
                    {
                        CliConsole.WriteErrorLine($"Error: Unknown option '{unknown}'. Run 'lpb merge --help' to see available options.");
                        context.ExitCode = 1;
                        return;
                    }
                }
                if (files is { Length: 2 })
                {
                    var resolved = ResolveImageVideo(files[0], files[1]);
                    if (resolved == null)
                    {
                        CliConsole.WriteErrorLine("Error: Cannot determine which file is the image and which is the video.");
                        Console.Error.WriteLine("Supported image formats: .jpg, .jpeg, .heic, .heif");
                        Console.Error.WriteLine("Supported video formats: .mp4, .mov");
                        context.ExitCode = 1;
                        return;
                    }
                    image ??= resolved.Value.Image;
                    video ??= resolved.Value.Video;
                }
                else if (files is { Length: 1 })
                {
                    CliConsole.WriteErrorLine("Error: Provide TWO files (image + video), or use --dir for batch mode.");
                    context.ExitCode = 1;
                    return;
                }

                var protocolName = context.ParseResult.GetValueForOption(protocolOpt)!;
                var output = context.ParseResult.GetValueForOption(outputOpt);
                var formatName = context.ParseResult.GetValueForOption(formatOpt);
                var naming = context.ParseResult.GetValueForOption(namingOpt)!;
                // 用户是否显式传了 --naming（未传时按模式用默认值：单文件=suffix，批量=keep）
                // 注意：beta4 版 FindResultFor 会把带默认值的选项也物化成结果，无法据此判断"是否显式传入"，
                // 只能扫描命令行 token 判断。
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
                var pairing = context.ParseResult.GetValueForOption(pairingOpt)!;
                var after = context.ParseResult.GetValueForOption(afterOpt)!;
                var allVariants = context.ParseResult.GetValueForOption(allVariantsOpt);
                var keyTimestampText = context.ParseResult.GetValueForOption(keyTimestampOpt);
                long? keyTimestampUs = null;
                if (keyTimestampText != null)
                {
                    if (!TryParseKeyTimestamp(keyTimestampText, out long parsedUs))
                    {
                        CliConsole.WriteErrorLine($"Error: Invalid --key-timestamp '{keyTimestampText}'.");
                        Console.Error.WriteLine("Use seconds (e.g. 1.5), mm:ss (e.g. 1:30) or hh:mm:ss (e.g. 0:01:30).");
                        context.ExitCode = 1;
                        return;
                    }
                    keyTimestampUs = parsedUs;
                }

                context.ExitCode = await RunAsync(
                    image, video, dir, protocolName, output, formatName,
                    naming, namingExplicit, parallel, yes, json, dryRun, verbose,
                    overwrite, recursive, preserveSubdirs, pairing, after,
                    allVariants, keyTimestampUs,
                    context.GetCancellationToken());
            });

            return cmd;
        }

        /// <summary>
        /// Auto-detect which of two files is the image and which is the video,
        /// based on extension. Returns null if the pair is ambiguous.
        /// </summary>
        private static (FileInfo Image, FileInfo Video)? ResolveImageVideo(string path1, string path2)
        {
            string ext1 = Path.GetExtension(path1);
            string ext2 = Path.GetExtension(path2);

            bool is1Image = ImageExtensions.Contains(ext1);
            bool is2Image = ImageExtensions.Contains(ext2);
            bool is1Video = VideoExtensions.Contains(ext1);
            bool is2Video = VideoExtensions.Contains(ext2);

            // Both are images or both are videos — ambiguous
            if (is1Image && is2Image) return null;
            if (is1Video && is2Video) return null;

            // One is image, one is video
            if (is1Image && is2Video) return (new FileInfo(path1), new FileInfo(path2));
            if (is1Video && is2Image) return (new FileInfo(path2), new FileInfo(path1));

            // Unknown extension(s)
            return null;
        }

        private static async Task<int> RunAsync(
            FileInfo? image, FileInfo? video, DirectoryInfo? dir,
            string protocolName, DirectoryInfo? output, string? formatName,
            string naming, bool namingExplicit, int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs,
            string pairing, string after, bool allVariants, long? keyTimestampUs, CancellationToken ct)
        {
            // ── --all-variants path ─────────────────────────────────
            if (allVariants)
            {
                if (dir != null)
                {
                    CliConsole.WriteErrorLine("Error: --all-variants only works with a single image+video pair (not --dir batch mode).");
                    return 1;
                }
                if (image == null || video == null)
                {
                    CliConsole.WriteErrorLine("Error: --all-variants requires an image and video file.");
                    return 1;
                }

                // Default output to the input image's directory (not cwd)
                string outputDir = output?.FullName ?? Path.GetDirectoryName(image.FullName)!;
                string tempDir = Path.Combine(outputDir, "Temp");
                Directory.CreateDirectory(outputDir);
                Directory.CreateDirectory(tempDir);

                try
                {
                    return await RunAllVariantsAsync(
                        image.FullName, video.FullName, outputDir, tempDir,
                        parallel, dryRun, keyTimestampUs, ct);
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                    catch { /* best effort */ }
                }
            }

            // Validate: need either --image+--video or --dir
            bool isSingle = image != null && video != null;
            bool isBatch = dir != null;

            if (!isSingle && !isBatch)
            {
                CliConsole.WriteErrorLine("Error: Specify two files (image+video) for single-pair, or --dir for batch mode.");
                return 1;
            }

            if (isSingle && isBatch)
            {
                CliConsole.WriteErrorLine("Error: Cannot use both single-pair (--image/--video) and batch (--dir) mode.");
                return 1;
            }

            if (keyTimestampUs.HasValue && isBatch)
            {
                CliConsole.WriteErrorLine("Error: --key-timestamp only works with a single image+video pair, not batch (--dir) mode.");
                return 1;
            }

            // Resolve protocol
            if (!ProtocolNameResolver.TryResolveProtocol(protocolName, out int protocolIndex))
            {
                CliConsole.WriteErrorLine($"Error: Unknown protocol '{protocolName}'. Use 'lpb protocols' to list available.{CliConsole.DidYouMean(protocolName, ["micro video", "motion photo", "oppo", "vivo", "samsung", "huawei"])}");
                if (protocolName.Contains("apple", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine("Note: Apple Live Photo is a split target (lpb split ... -p apple), not a merge protocol.");
                return 1;
            }

            // Resolve format
            int formatIndex = ProtocolFormatMatrix.GetDefaultFormat(protocolIndex);
            if (formatName != null)
            {
                if (!ProtocolNameResolver.TryResolveFormat(formatName, out formatIndex))
                {
                    string[] validFormats = ["jpg+mp4", "jpg+mov", "heic+mp4", "heic+mov", "heic+mp4-h265"];
                    var matches = validFormats
                        .Where(v => v.StartsWith(formatName.Trim(), StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    CliConsole.WriteErrorLine(
                        matches.Count > 0
                            ? $"Error: Unknown format '{formatName}'. Did you mean: {string.Join(", ", matches)}?"
                            : $"Error: Unknown format '{formatName}'. Valid: {string.Join(", ", validFormats)}");
                    return 1;
                }

                if (!ProtocolFormatMatrix.IsAvailable(protocolIndex, formatIndex))
                {
                    string available = string.Join(", ", ProtocolFormatMatrix
                        .GetAvailableFormats(protocolIndex)
                        .Select(f => ProtocolFormatMatrix.FormatNames[f]));
                    CliConsole.WriteErrorLine($"Error: Format '{formatName}' is not available for protocol '{protocolName}'. Available: {available}");
                    return 1;
                }
            }

            // 用户未显式传 --naming 时按模式给默认值：
            //   单文件合成 → suffix（输出默认在照片原目录，加协议后缀避免覆盖源文件；仍可用 --naming 改）
            //   批量合成   → keep（输出默认进独立子文件夹，文件名不变不会重名，协议后缀体现在文件夹名）
            if (!namingExplicit)
                naming = isSingle ? "suffix" : "keep";

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
                CliConsole.WriteErrorLine($"Error: Unknown naming rule '{naming}'. Valid: keep, suffix, custom:<pattern>{CliConsole.DidYouMean(naming, ["keep", "suffix", "custom:TEMPLATE"])}");
                return 1;
            }

            // Resolve pairing method
            bool useCid = pairing.Equals("cid", StringComparison.OrdinalIgnoreCase);
            bool useVivo = pairing.Equals("vivo", StringComparison.OrdinalIgnoreCase);
            bool useName = pairing.Equals("name", StringComparison.OrdinalIgnoreCase);
            if (!useName && !useCid && !useVivo)
            {
                CliConsole.WriteErrorLine($"Error: Unknown pairing method '{pairing}'. Valid: name, cid, vivo{CliConsole.DidYouMean(pairing, ["name", "cid", "vivo"])}");
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
                CliConsole.WriteErrorLine($"Error: Unknown after-completion action '{after}'. Valid: none, move:<dir>, recycle{CliConsole.DidYouMean(after, ["none", "move:PATH", "recycle"])}");
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
                // Resolve output directory:
                //   未显式传 -o 时 —— 单文件模式默认输出到照片（图片）所在目录，以照片为准
                //   （照片和视频可能不在同一文件夹）；批量模式默认在输入目录下新建
                //   {输入目录名}_{协议后缀} 子文件夹（如 ./MyPhotos/MyPhotos_motionphoto/）。
                string outputDir = output?.FullName ?? (isSingle
                    ? Path.GetDirectoryName(image!.FullName)!
                    : DefaultBatchOutputDirectory(dir!.FullName, protocolIndex));
                string tempDir = Path.Combine(outputDir, "Temp");

                // Print summary
                string protoDisplay = ProtocolNameResolver.GetProtocolDisplayName(protocolIndex);
                string fmtDisplay = ProtocolFormatMatrix.FormatNames[formatIndex];

                // 命令层日志：完整有效配置，让日志能还原"当时跑了什么命令、什么参数"。
                LogService.Merge(
                    $"Command config: mode={(isSingle ? "single" : "batch")} " +
                    $"protocol={protoDisplay}({protocolIndex}) format={fmtDisplay}({formatIndex}) " +
                    $"naming={naming} output={outputDir} overwrite={overwrite} dryRun={dryRun} " +
                    $"keyTimestamp={(keyTimestampUs.HasValue ? $"{keyTimestampUs.Value / 1_000_000.0:F3}s" : "auto")}");
                if (isBatch)
                    LogService.Merge(
                        $"Batch: dir={dir!.FullName} pairing={pairing} parallel={parallel} recursive={recursive} " +
                        $"preserveSubdirs={preserveSubdirs} after={after}");
                else
                    LogService.Merge($"Sources: image={image!.FullName}, video={video!.FullName}");

                if (!json)
                {
                    // 双文件合成：图片/视频两个文件名放到最顶部（含扩展名），不再重复打印全路径
                    if (isSingle)
                    {
                        CliConsole.WriteFieldRgb("Image", Path.GetFileName(image!.FullName), width: 10, valueColor: CliConsole.PathGreen);
                        CliConsole.WriteFieldRgb("Video", Path.GetFileName(video!.FullName), width: 10, valueColor: CliConsole.PathGreen);
                    }
                    CliConsole.WriteField("Protocol", protoDisplay, width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteField("Format", fmtDisplay, width: 10, valueColor: CliConsole.Highlight);
                    if (isBatch)
                        CliConsole.WriteField("Pairing", pairing, width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteFieldRgb("Output", outputDir, width: 10, valueColor: CliConsole.PathGreen);
                }

                if (isSingle)
                {
                    if (!json)
                    {
                        if (keyTimestampUs.HasValue)
                            CliConsole.WriteField("Key photo", $"{keyTimestampUs.Value / 1_000_000.0:F3}s (custom)",
                                width: 10, valueColor: CliConsole.Highlight);
                        else
                            CliConsole.WriteField("Key photo", "auto (from source video)", width: 10, valueColor: CliConsole.Highlight);
                    }

                    // 预估最终输出文件名（与 Runner 内部逻辑一致：按输出格式决定 JPG/HEIC 扩展名），
                    // 让用户在确认前知道会生成哪个文件。
                    string imgForExt = (formatIndex is 2 or 3 or ProtocolFormatMatrix.FormatHeicMp4H265)
                        ? Path.ChangeExtension(image!.FullName, ".heic")
                        : Path.ChangeExtension(image!.FullName, ".jpg");
                    string outputName = LivePhotoMergeService.CreateOutputFileName(
                        Path.GetFileNameWithoutExtension(image!.FullName),
                        protocolIndex, imgForExt, formatIndex, namingRuleIndex,
                        customPattern: namingRuleIndex == 2 ? customPattern : null,
                        taskIndex: namingRuleIndex == 2 ? 1 : null);
                    string estimatedOutput = Path.Combine(outputDir, outputName);
                    if (!json)
                        CliConsole.WriteFieldRgb("File", Path.GetFileName(estimatedOutput), width: 10, valueColor: CliConsole.PathGreen);

                    if (dryRun)
                    {
                        LogService.Merge("DRY RUN: would merge 1 pair.");
                        if (json) PrintSingleJson(image!.FullName, video!.FullName, estimatedOutput, "would-merge");
                        else
                        {
                            Console.Write("[DRY RUN] Would merge ");
                            CliConsole.Write("1", CliConsole.Highlight);
                            Console.WriteLine(" pair.");
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

                    // 确认通过后才创建目录，dry-run / 取消不产生任何副作用
                    Directory.CreateDirectory(outputDir);
                    Directory.CreateDirectory(tempDir);
                    return await MergeSinglePairAsync(
                        image!.FullName, video!.FullName, outputDir, tempDir,
                        protocolIndex, formatIndex, namingRuleIndex, customPattern,
                        keyTimestampUs, overwrite, verbose, json, estimatedOutput, ct);
                }
                else
                {
                    return await MergeBatchAsync(
                        dir!.FullName, outputDir,
                        protocolIndex, formatIndex, namingRuleIndex, customPattern,
                        parallel, yes, json, dryRun, verbose,
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

        // 批量模式默认输出目录：在输入目录下新建 {输入目录名}_{协议后缀} 子文件夹。
        // 例: merge -d ./MyPhotos -p motionphoto → ./MyPhotos/MyPhotos_motionphoto/
        private static string DefaultBatchOutputDirectory(string inputDir, int protocolIndex)
        {
            string dirName = Path.GetFileName(inputDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(dirName))
                dirName = "output"; // 盘符根目录等极端情况
            string suffix = LivePhotoMergeService.GetProtocolSuffixName(protocolIndex) ?? "livephoto";
            return Path.Combine(inputDir, $"{dirName}_{suffix}");
        }

        private static async Task<int> MergeSinglePairAsync(
            string imagePath, string videoPath, string outputDir, string tempDir,
            int protocolIndex, int formatIndex, int namingRuleIndex, string? customPattern,
            long? keyTimestampUs, bool overwrite, bool verbose, bool json, string outputPath,
            CancellationToken ct)
        {
            // 放 try 外：catch 里也要记录文件名（baseName 在 catch 作用域不可见）
            string baseName = Path.GetFileNameWithoutExtension(imagePath);
            try
            {
                var options = new LivePhotoMergeRunOptions
                {
                    OutputDirectory = outputDir,
                    SelectedModeIndex = protocolIndex,
                    OutputFormatIndex = formatIndex,
                    NamingRuleIndex = namingRuleIndex,
                    CustomNamingPattern = customPattern,
                    KeyPhotoTimestampUs = keyTimestampUs,
                    OverwriteExisting = overwrite,
                };

                if (verbose && !json)
                    Console.WriteLine($"Starting merge: {Path.GetFileName(imagePath)}...");

                var pause = new ManualResetEventSlim(true); // CLI never pauses
                var (isSuccess, details) = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
                    imagePath, videoPath, baseName, taskIndex: 1,
                    options, tempDir, pause, ct);

                if (isSuccess)
                {
                    if (json) PrintSingleJson(imagePath, videoPath, outputPath, "merged");
                    else
                    {
                        CliConsole.WriteLine("Done", CliConsole.Success);
                    }
                    return 0;
                }
                else
                {
                    if (json) PrintSingleJson(imagePath, videoPath, outputPath, "failed", reason: details);
                    else CliConsole.WriteErrorLine($"FAIL  {Path.GetFileName(imagePath)}  {details}");
                    return 1;
                }
            }
            catch (OperationCanceledException)
            {
                if (json) PrintSingleJson(imagePath, videoPath, outputPath, "cancelled");
                else CliConsole.WriteErrorLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                LogService.Error($"Merge failed for {baseName}: {ex.Message}", ex, LogSource.Merge);
                if (json) PrintSingleJson(imagePath, videoPath, outputPath, "failed", reason: $"{ex.GetType().Name}: {ex.Message}");
                else
                {
                    CliConsole.WriteErrorLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                    if (verbose) Console.Error.WriteLine(ex.StackTrace);
                }
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        private static async Task<int> MergeBatchAsync(
            string inputDir, string outputDir,
            int protocolIndex, int formatIndex, int namingRuleIndex, string? customPattern,
            int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool preserveSubdirs, bool useCid, bool useVivo,
            string? afterMoveDir, bool afterRecycle, CancellationToken ct)
        {
            // 1. Scan — filename-based pairing (always)
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
                    if (!json) Console.Write("CID matching... ");
                    var metaResult = await LivePhotoMetadataMatcher.MatchAsync(
                        scanResult.StandaloneImagePaths, scanResult.StandaloneVideoPaths,
                        exifToolPath, ct);
                    foreach (var mp in metaResult.Pairs)
                        allPairs.Add((mp.ImagePath, mp.VideoPath, Path.GetFileNameWithoutExtension(mp.ImagePath)));
                    metaPairs = metaResult.Pairs.Count;
                }
                else
                {
                    if (!json) Console.Write("(exiftool not found, skip CID) ");
                }
            }
            else if (useVivo && scanResult.StandaloneImagePaths.Count > 0 && scanResult.StandaloneVideoPaths.Count > 0)
            {
                if (!json) Console.Write("vivo matching... ");
                var metaResult = LivePhotoMetadataMatcher.MatchVivo(
                    scanResult.StandaloneImagePaths, scanResult.StandaloneVideoPaths);
                foreach (var mp in metaResult.Pairs)
                    allPairs.Add((mp.ImagePath, mp.VideoPath, Path.GetFileNameWithoutExtension(mp.ImagePath)));
                metaPairs = metaResult.Pairs.Count;
            }

            int standaloneImg = scanResult.StandaloneImagesCount - metaPairs;
            int standaloneVid = scanResult.StandaloneVideosCount - metaPairs;
            if (!json)
            {
                Console.WriteLine();
                CliConsole.Write(scanResult.Pairs.Count.ToString(), CliConsole.Highlight);
                Console.Write(" filename pairs, ");
                CliConsole.Write(metaPairs.ToString(), CliConsole.Highlight);
                Console.Write(" meta pairs, ");
                CliConsole.Write(standaloneImg.ToString(), CliConsole.Highlight);
                Console.Write(" standalone images, ");
                CliConsole.Write(standaloneVid.ToString(), CliConsole.Highlight);
                Console.WriteLine(" standalone videos");
            }

            if (allPairs.Count == 0)
            {
                if (json)
                    PrintBatchJson(inputDir, outputDir, 0, 0, 0, new List<MergeJsonFileEntry>());
                else
                    CliConsole.WriteErrorLine("No image+video pairs found. Nothing to do.");
                return 0;
            }

            // 3. Build task list
            var tasks = new List<CliMergeTask>(allPairs.Count);
            var jsonFiles = new List<MergeJsonFileEntry>();
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
                if (json)
                {
                    jsonFiles.Add(new MergeJsonFileEntry
                    {
                        Image = p.ImagePath,
                        Video = p.VideoPath,
                        Name = p.BaseName,
                        Status = dryRun ? "would-merge" : "pending",
                    });
                }
            }

            // 4. Dry run
            if (dryRun)
            {
                LogService.Merge($"DRY RUN: would merge {tasks.Count} pairs.");
                if (json)
                    PrintBatchJson(inputDir, outputDir, tasks.Count, 0, 0, jsonFiles);
                else
                {
                    Console.WriteLine();
                    Console.Write("[DRY RUN] Would merge ");
                    CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" pairs:");
                    foreach (var t in tasks)
                    {
                        CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                        Console.Write("  ");
                        CliConsole.Write(Path.GetFileName(t.ImagePath), CliConsole.PathGreen);
                        Console.Write("  +  ");
                        CliConsole.Write(Path.GetFileName(t.VideoPath), CliConsole.PathGreen);
                        Console.WriteLine();
                    }
                }
                return 0;
            }

            // 5. Confirmation（交互模式先列出匹配对，确认后再处理；-y / --json 静默跳过）
            if (!yes && !json)
            {
                Console.WriteLine();
                Console.Write("Matched ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" pairs:");
                foreach (var t in tasks)
                {
                    Console.Write("  ");
                    CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                    Console.Write("  ");
                    CliConsole.Write(Path.GetFileName(t.ImagePath), CliConsole.PathGreen);
                    Console.Write("  +  ");
                    CliConsole.Write(Path.GetFileName(t.VideoPath), CliConsole.PathGreen);
                    Console.WriteLine();
                }
                Console.Write("\nMerge ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.Write(" pairs? [Y/n] ");
                var key = Console.ReadLine();
                if (key is null ||
                    string.Equals(key, "n", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "no", StringComparison.OrdinalIgnoreCase))
                {
                    CliConsole.WriteLine("Cancelled.", CliConsole.Muted);
                    return 0;
                }
            }

            // 6. Run batch（实际处理前才创建输出目录，dry-run / 取消不产生副作用）
            Directory.CreateDirectory(outputDir);
            if (!json)
            {
                Console.Write("\nProcessing ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.Write(" pairs (parallel=");
                CliConsole.Write(parallel.ToString(), CliConsole.Highlight);
                Console.WriteLine(")...");
                Console.WriteLine();
            }

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
                    if (verbose && !json)
                        Console.Write($"[{task.Index}/{tasks.Count}] {Path.GetFileName(task.ImagePath)} ... ");
                },
                onTaskCompleted: (task, success, details, completed) =>
                {
                    task.Status = success ? ProcessStatus.Success : ProcessStatus.Failed;
                    task.Details = details;
                    var entry = json ? jsonFiles[task.Index - 1] : null;
                    if (success)
                    {
                        Interlocked.Increment(ref ok);
                        if (entry != null) entry.Status = "merged";
                        if (!json)
                        {
                            if (verbose)
                                CliConsole.WriteLine("SUCCESS", CliConsole.Success);
                            else
                            {
                                Console.Write($"[{completed}/{tasks.Count}] ");
                                CliConsole.Write("SUCCESS  ", CliConsole.Success);
                                Console.WriteLine(Path.GetFileName(task.ImagePath));
                            }
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref fail);
                        if (entry != null) { entry.Status = "failed"; entry.Reason = details; }
                        if (!json)
                        {
                            if (verbose)
                                CliConsole.WriteLine($"FAIL ({details})", CliConsole.Error);
                            else
                            {
                                Console.Write($"[{completed}/{tasks.Count}] ");
                                CliConsole.Write("FAIL  ", CliConsole.Error);
                                Console.WriteLine($"{Path.GetFileName(task.ImagePath)}  ({details})");
                            }
                        }
                    }
                });

            // 7. After-completion actions (only on successful tasks)
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
                        if (!json) CliConsole.WriteErrorLine($"WARN: Failed to move '{Path.GetFileName(task.ImagePath)}': {ex.Message}");
                    }
                }
                if (!json)
                {
                    Console.Write("Moved ");
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
                        if (!json) CliConsole.WriteErrorLine($"WARN: Failed to recycle '{Path.GetFileName(task.ImagePath)}': {ex.Message}");
                    }
                }
                if (!json)
                {
                    Console.Write("Recycled ");
                    CliConsole.Write(recycled.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" source files.");
                }
            }

            // 8. Summary
            if (json)
                PrintBatchJson(inputDir, outputDir, tasks.Count, ok, fail, jsonFiles);
            else
            {
                Console.WriteLine();
                CliConsole.Write("Done: ", CliConsole.Success);
                CliConsole.Write(ok.ToString(), CliConsole.Highlight);
                Console.Write(" SUCCESS, ");
                CliConsole.Write(fail.ToString(), CliConsole.Highlight);
                Console.Write(" FAIL, ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" total");
            }
            return fail > 0 ? 1 : 0;
        }

        // 序列化 JSON 到 stdout（脚本模式 --json 用，方便脚本稳定解析，不受文件名长度/终端宽度影响）。
        private static void PrintJson(object data)
            => Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

        // 单文件模式的 JSON 结果。
        private static void PrintSingleJson(string image, string video, string output, string status, string reason = "")
            => PrintJson(new { command = "merge", mode = "single", image, video, output, status, reason });

        // 批量模式的 JSON 结果。
        private static void PrintBatchJson(string input, string output, int scanned, int merged, int failed, List<MergeJsonFileEntry> files)
            => PrintJson(new { command = "merge", mode = "batch", input, output, scanned, merged, failed, files });

        // 脚本模式（--json）下单个文件的 JSON 结果条目。
        private sealed class MergeJsonFileEntry
        {
            public string Image { get; set; } = "";
            public string Video { get; set; } = "";
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";  // merged / would-merge / failed
            public string Reason { get; set; } = "";  // 失败原因
        }

        // ══════════════════════════════════════════════════════════════
        //  --all-variants: generate all protocol × format combos
        // ══════════════════════════════════════════════════════════════

        private static async Task<int> RunAllVariantsAsync(
            string imagePath, string videoPath, string outputDir, string tempDir,
            int parallel, bool dryRun, long? keyTimestampUs, CancellationToken ct)
        {
            string originalBaseName = Path.GetFileNameWithoutExtension(imagePath);

            // Auto-create subfolder: {outputDir}/{name}_variants/
            string variantsDir = Path.Combine(outputDir, $"{originalBaseName}_variants");
            Directory.CreateDirectory(variantsDir);

            // Build job list from the Matrix (single source of truth)
            var combos = new List<(int Proto, int Fmt, string BaseName, string Label)>();
            for (int p = 1; p < ProtocolFormatMatrix.Matrix.Length; p++)
            {
                foreach (int f in ProtocolFormatMatrix.GetAvailableFormats(p))
                {
                    // {originalName}_{Protocol}_{Format}
                    // 文件名里去掉显示用的空格（JPEG + MP4 → JPEG+MP4）
                    string name = $"{originalBaseName}_{ProtocolNameResolver.ProtocolNames[p]}_{ProtocolFormatMatrix.FormatNames[f].Replace(" + ", "+")}";
                    string label = $"{ProtocolNameResolver.ProtocolNames[p]} {ProtocolFormatMatrix.FormatNames[f]}";
                    combos.Add((p, f, name, label));
                }
            }

            LogService.Merge(
                $"All-variants: output={variantsDir} parallel={parallel} dryRun={dryRun} " +
                $"combos={combos.Count} keyTimestamp={(keyTimestampUs.HasValue ? $"{keyTimestampUs.Value / 1_000_000.0:F3}s" : "auto")}");

            if (dryRun)
            {
                LogService.Merge($"DRY RUN: would generate {combos.Count} variants.");
                CliConsole.WriteFieldRgb("Output", variantsDir, width: 10, valueColor: CliConsole.PathGreen);
                Console.WriteLine();
                Console.Write("Would generate ");
                CliConsole.Write(combos.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" variants:");
                foreach (var c in combos)
                {
                    CliConsole.Write($"{c.BaseName}{(c.Fmt is 2 or 3 or ProtocolFormatMatrix.FormatHeicMp4H265 ? ".heic" : ".jpg")}", CliConsole.PathGreen);
                    Console.WriteLine();
                }
                return 0;
            }

            CliConsole.WriteFieldRgb("Output", variantsDir, width: 10, valueColor: CliConsole.PathGreen);
            CliConsole.WriteField("Combos", combos.Count.ToString(), width: 10, valueColor: CliConsole.Highlight);
            Console.WriteLine();

            int ok = 0, fail = 0, completed = 0;
            var semaphore = new SemaphoreSlim(Math.Max(1, parallel));
            var pause = new ManualResetEventSlim(true); // CLI never pauses

            var tasks = combos.Select(async c =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var options = new LivePhotoMergeRunOptions
                    {
                        OutputDirectory = variantsDir,
                        SelectedModeIndex = c.Proto,
                        OutputFormatIndex = c.Fmt,
                        NamingRuleIndex = 0,
                        KeyPhotoTimestampUs = keyTimestampUs,
                        OverwriteExisting = true,
                    };

                    var (success, details) = await LivePhotoMergeRunnerService
                        .ProcessSinglePairAsync(imagePath, videoPath, c.BaseName,
                            taskIndex: 0, options, tempDir, pause, ct);

                    if (success)
                    {
                        Interlocked.Increment(ref ok);
                        // 完成顺序编号：谁先跑完谁就是 [1/N]，打印自上而下单调递增。
                        int idx = Interlocked.Increment(ref completed);
                        Console.Write($"[{idx}/{combos.Count}] ");
                        CliConsole.Write("SUCCESS  ", CliConsole.Success);
                        Console.WriteLine(c.Label);
                    }
                    else
                    {
                        Interlocked.Increment(ref fail);
                        int idx = Interlocked.Increment(ref completed);
                        Console.Write($"[{idx}/{combos.Count}] ");
                        CliConsole.Write("FAIL  ", CliConsole.Error);
                        Console.WriteLine($"{c.Label}  ({details})");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref fail);
                    CliConsole.WriteErrorLine($"[{Interlocked.Increment(ref completed)}/{combos.Count}] ERROR  {c.Label}  ({ex.Message})");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            Console.WriteLine();
            CliConsole.Write("Done: ", CliConsole.Success);
            CliConsole.Write(ok.ToString(), CliConsole.Highlight);
            Console.Write(" SUCCESS, ");
            CliConsole.Write(fail.ToString(), CliConsole.Highlight);
            Console.Write(" FAIL, ");
            CliConsole.Write(combos.Count.ToString(), CliConsole.Highlight);
            Console.WriteLine(" total");
            return fail > 0 ? 1 : 0;
        }

        // Parse a user-supplied key photo timestamp into microseconds.
        // Accepts decimal seconds (1.5), mm:ss (1:30), mm:ss.fff (1:30.500)
        // or hh:mm:ss (0:01:30). Returns false on malformed / negative input.
        private static bool TryParseKeyTimestamp(string text, out long microseconds)
        {
            microseconds = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();

            double seconds;
            if (text.Contains(':'))
            {
                string[] parts = text.Split(':');
                if (parts.Length is < 2 or > 3)
                    return false;

                double total = 0;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 0)
                        return false;
                    total = total * 60 + v;
                }

                if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double last) || last < 0)
                    return false;
                seconds = total * 60 + last;
            }
            else
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || seconds < 0)
                    return false;
            }

            microseconds = (long)Math.Round(seconds * 1_000_000.0);
            return true;
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
