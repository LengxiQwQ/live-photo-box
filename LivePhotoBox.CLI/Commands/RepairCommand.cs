/*
 * RepairCommand.cs
 *
 * 修复命令：分析并修复实况照片/视频的元数据问题（旋转、缩略图、HEIC 方向、视频旋转）。
 * 使用 Native 诊断和修复服务，按逐项结果发布新文件。
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

        public static Command Create()
        {
            var filesArg = new Argument<string?>("files") { Description = "One image or video file to repair (.jpg/.jpeg/.heic/.heif/.mov/.mp4), or a folder path (no file extension = batch mode, same as --dir)." };
            filesArg.Arity = ArgumentArity.ZeroOrOne;

            var dirOpt = new Option<DirectoryInfo?>("--dir", "-d") { Description = "Folder with images and videos. Every detected file is analyzed and repaired. For batch mode; a folder path can also be passed as the positional argument." };
            var outputOpt = new Option<DirectoryInfo?>("--output", "-o") { Description = "Output folder. Default: a \"_repaired\" suffix next to the source for single-file; a \"{folder}_repaired\" subfolder inside the input folder for batch mode." };
            var noRotateOpt = new Option<bool>("--no-rotate") { Description = "Disable image rotation fix." };
            var noThumbnailOpt = new Option<bool>("--no-thumbnail") { Description = "Disable embedded thumbnail stripping." };
            var noHeicOpt = new Option<bool>("--no-heic") { Description = "Disable HEIC/HEIF orientation fix." };
            var noVideoOpt = new Option<bool>("--no-video") { Description = "Disable video rotation bake." };

            var allDevicesOpt = new Option<bool>("--all-devices") { Description = "Repair files from all devices. Default: Apple Live Photos and Samsung Motion Photos are repaired; other devices are skipped." };

            var repairLongVideosOpt = new Option<bool>("--repair-long-videos") { Description = "Also repair videos longer than 3.5s (not real live photos). Default: skipped." };

            var copyPerfectOpt = new Option<bool>("--copy-perfect") { Description = "Also copy files that need no repair to the output folder (batch mode only)." };

            var parallelOpt = new Option<int>("--parallel", "-j") { DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 5), Description = "Accepted for CLI compatibility (1-64). Repair currently processes file pairs sequentially." };
            var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip confirmation prompts. Useful for scripts / automation." };
            var jsonOpt = new Option<bool>("--json") { Description = "Output machine-readable JSON to stdout (implies --yes)." };

            var dryRunOpt = new Option<bool>("--dry-run") { Description = "Preview: show what would be done, don't actually process files." };

            var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "Show per-file status messages instead of summary only." };
            var overwriteOpt = new Option<bool>("--overwrite", "-w") { Description = "Unavailable for repair. Output name conflicts are resolved by choosing a new name." };
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
            if (overwrite || (copyPerfect && dir == null))
            {
                string message = overwrite
                    ? "Repair creates new files. --overwrite is unavailable."
                    : "--copy-perfect requires a batch input directory.";
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", error = message }));
                else
                    CliConsole.WriteErrorLine(message);
                return 1;
            }

            string root = dir?.FullName ?? Path.GetDirectoryName(Path.GetFullPath(singlePath!))!;
            string destination = output?.FullName ?? (dir == null
                ? root : Path.Combine(root, new DirectoryInfo(root).Name + "_repaired"));
            string destinationPrefix = Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar;
            var files = singlePath != null
                ? new[] { Path.GetFullPath(singlePath) }
                : Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).Where(path =>
                    (string.Equals(root, destination, StringComparison.OrdinalIgnoreCase) ||
                     !path.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase)) &&
                    (ImageExtensions.Contains(Path.GetExtension(path)) || VideoExtensions.Contains(Path.GetExtension(path))))
                  .ToArray();

            if (!yes && !dryRun && !json && !Console.IsInputRedirected)
            {
                Console.Write("Create repaired image copies? [y/N] ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                    return 0;
            }

            var options = new RepairOptions
            {
                FixImageRotation = !noRotate,
                StripImageThumbnail = !noThumbnail,
                FixHeicOrientation = !noHeic,
                FixVideoRotation = !noVideo
            };

            var results = new List<object>();
            int succeeded = 0, failed = 0, skipped = 0, warned = 0, planned = 0, copied = 0;
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(file);
                LivePhotoRepairService.RepairResult result;
                string folder = preserveSubdirs && dir != null
                    ? Path.Combine(destination, Path.GetRelativePath(root, Path.GetDirectoryName(file)!))
                    : destination;
                try
                {
                    if (ImageExtensions.Contains(extension))
                        result = noRotate && noThumbnail && noHeic && noVideo
                            ? new("skipped", "All image repair options were disabled.")
                            : await LivePhotoRepairService.RepairImageFileAsync(file, folder, allDevices, dryRun, options, ct, copyPerfect);
                    else
                        result = await LivePhotoRepairService.RepairVideoFileAsync(
                            file, folder, allDevices, repairLongVideos, dryRun, dir != null, options, ct, copyPerfect);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new("failed", ex.Message);
                }

                switch (result.Status)
                {
                    case "success": succeeded++; break;
                    case "failed": failed++; break;
                    case "warning": warned++; break;
                    case "planned": planned++; break;
                    case "copied": copied++; break;
                    default: skipped++; break;
                }
                string? color = result.Status switch
                {
                    "success" => "green",
                    "warning" => "yellow",
                    "failed" => "red",
                    _ => null
                };
                results.Add(new { file, status = result.Status, color, message = result.Message, image = result.Image, video = result.Video });
                if (!json && (verbose || singlePath != null || result.Status != "skipped"))
                    Console.WriteLine($"[{result.Status}] {file} — {result.Message} {result.Image} {result.Video}");
            }

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new { succeeded, failed, skipped, warned, planned, copied, results }));
            else
                Console.WriteLine($"Repaired: {succeeded}, warning: {warned}, failed: {failed}, skipped: {skipped}, planned: {planned}, copied: {copied}");
            return failed == 0 ? 0 : 1;
        }
    }
}
