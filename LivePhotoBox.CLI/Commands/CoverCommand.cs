/*
 * CoverCommand.cs
 *
 * CLI cover 命令：修改已有实况照片的封面帧（Key Photo），或查看当前封面信息。
 * 实际协议写入逻辑统一复用 Core 的 CoverChangeService。
 */

using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands
{
    internal static class CoverCommand
    {
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".heic", ".heif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov"
        };

        public static Command Create()
        {
            var filesArg = new Argument<string[]>("files",
                "Live photo file path. Single file: auto-detected as single-file live photo or dual-file image (auto-pair). Two files: image + video pair.");
            filesArg.Arity = new ArgumentArity(1, 2);

            var atOpt = new Option<string?>("--at",
                "New cover position on the video timeline. Accepts seconds (2.500), mm:ss (1:30.500) or hh:mm:ss (0:01:30.500). Mutually exclusive with --frame.");
            atOpt.AddAlias("-a");

            var frameOpt = new Option<int?>("--frame",
                "New cover frame number (1-based). 1 = first frame. Mutually exclusive with --at.");

            var outputOpt = new Option<DirectoryInfo?>("--output",
                "Output directory. Default: source file's own directory.");
            outputOpt.AddAlias("-o");

            var namingOpt = new Option<string>("--naming", () => "suffix",
                "Output filename rule. keep (keep original name)|suffix (append protocol suffix)|custom:TEMPLATE.\n" +
                "Template tokens: {name} {protocol} {date} {time} {exif_date} {exif_time} {frame} {counter} {counter:D3}");
            namingOpt.AddAlias("-n");

            var overwriteOpt = new Option<bool>("--overwrite",
                "Replace existing files. Without this, name conflicts get auto-renamed.");
            overwriteOpt.AddAlias("-w");

            var yesOpt = new Option<bool>("--yes",
                "Skip confirmation prompts. Useful for scripts / automation.");
            yesOpt.AddAlias("-y");

            var dryRunOpt = new Option<bool>("--dry-run",
                "Preview only: show current cover info and what would be done, without modifying any files.");

            var verboseOpt = new Option<bool>("--verbose",
                "Show detailed progress messages.");
            verboseOpt.AddAlias("-v");

            var jsonOpt = new Option<bool>("--json",
                "Output machine-readable JSON to stdout (implies --yes).");

            var command = new Command("cover",
                "Change the cover frame (Key Photo) of an existing live photo, or view current cover info.\n" +
                "Alias: keyphoto\n\n" +
                "Single file (auto-detect):  lpb cover photo.jpg --at 2.5\n" +
                "Dual file (Apple):          lpb cover photo.heic video.mov --at 2.5\n" +
                "View cover info only:       lpb cover photo.jpg\n" +
                "Preview without changes:    lpb cover photo.jpg --dry-run\n" +
                "Custom naming:              lpb cover photo.jpg --at 2.5 -n \"custom:{name}_cover{frame}\"")
            {
                filesArg,
                atOpt,
                frameOpt,
                outputOpt,
                namingOpt,
                overwriteOpt,
                yesOpt,
                dryRunOpt,
                verboseOpt,
                jsonOpt
            };

            command.AddAlias("keyphoto");

            command.SetHandler(async context =>
            {
                var files = context.ParseResult.GetValueForArgument(filesArg);
                var at = context.ParseResult.GetValueForOption(atOpt);
                var frame = context.ParseResult.GetValueForOption(frameOpt);
                var output = context.ParseResult.GetValueForOption(outputOpt);
                var naming = context.ParseResult.GetValueForOption(namingOpt)!;
                var overwrite = context.ParseResult.GetValueForOption(overwriteOpt);
                var yes = context.ParseResult.GetValueForOption(yesOpt);
                var dryRun = context.ParseResult.GetValueForOption(dryRunOpt);
                var verbose = context.ParseResult.GetValueForOption(verboseOpt);
                var json = context.ParseResult.GetValueForOption(jsonOpt);

                string? wildcard = files.FirstOrDefault(CliInputValidator.HasWildcard);
                if (wildcard != null)
                {
                    CliInputValidator.WriteWildcardNotSupported();
                    context.ExitCode = 1;
                    return;
                }

                if (at != null && frame.HasValue)
                {
                    CliConsole.WriteErrorLine("Error: --at and --frame are mutually exclusive. Specify only one.");
                    context.ExitCode = 1;
                    return;
                }

                long? timestampUs = null;
                int? frameNumber = frame;
                if (at != null)
                {
                    if (!CliInputValidator.TryParseKeyTimestamp(at, out long parsedUs))
                    {
                        CliConsole.WriteErrorWithHint(
                            $"Error: Invalid --at '{at}'.",
                            "Use seconds (e.g. 2.500), mm:ss (e.g. 1:30.500) or hh:mm:ss (e.g. 0:01:30.500).");
                        context.ExitCode = 1;
                        return;
                    }
                    timestampUs = parsedUs;
                }

                bool viewOnly = !timestampUs.HasValue && !frameNumber.HasValue;
                if (json)
                    yes = true;

                context.ExitCode = await RunAsync(
                    files,
                    timestampUs,
                    frameNumber,
                    output,
                    naming,
                    overwrite,
                    yes,
                    dryRun,
                    verbose,
                    json,
                    viewOnly,
                    context.GetCancellationToken());
            });

            return command;
        }

        private static async Task<int> RunAsync(
            string[] files,
            long? timestampUs,
            int? frameNumber,
            DirectoryInfo? output,
            string naming,
            bool overwrite,
            bool yes,
            bool dryRun,
            bool verbose,
            bool json,
            bool viewOnly,
            CancellationToken ct)
        {
            try
            {
                ValidateInputFiles(files, out string? inputError);
                if (inputError != null)
                {
                    CliConsole.WriteErrorLine(inputError);
                    return 1;
                }

                var input = await CoverInputResolver.ResolveAsync(files, ct);
                if (input == null)
                {
                    CliConsole.WriteErrorWithHint(
                        "Error: Cannot detect a supported live photo protocol for the input file(s).",
                        "For dual-file live photos, pass both the image and video files explicitly.");
                    return 1;
                }

                LogService.Info($"[Cover] Input resolved: image='{input.ImagePath}', video='{input.VideoPath ?? "null"}', protocol={input.Protocol}, type={input.LivePhotoType}", LogSource.System);

                string imagePath = input.ImagePath;
                string? pairedVideoPath = input.VideoPath;

                string? previewTempDir = null;
                string? workingVideoPath = pairedVideoPath;
                try
                {
                    if (workingVideoPath == null)
                    {
                        previewTempDir = Path.Combine(Path.GetTempPath(), $"lpb_cover_preview_{Guid.NewGuid():N}");
                        Directory.CreateDirectory(previewTempDir);
                        workingVideoPath = await CoverChangeService.ExtractEmbeddedVideoForPreviewAsync(
                            imagePath, input.Protocol, previewTempDir, ct);

                        if (string.IsNullOrEmpty(workingVideoPath) || !File.Exists(workingVideoPath))
                        {
                            CliConsole.WriteErrorLine("Error: Cannot extract embedded video from the live photo file.");
                            return 1;
                        }
                    }

                    int totalFrames = 0;
                    double videoDurationSec = 0;
                    if (workingVideoPath != null && File.Exists(workingVideoPath))
                    {
                        totalFrames = await LivePhotoMergeService.DetectVideoFrameCountAsync(workingVideoPath, ct);
                        videoDurationSec = await LivePhotoMergeService.DetectVideoDurationAsync(workingVideoPath, ct);
                    }

                    VivoDualFileResolvedTiming? vivoTiming =
                        input.Protocol == LivePhotoProtocolType.Vivo && input.VideoPath != null
                            ? await VivoDualFileMetadataWriter.ResolveCoverTimingAsync(input.VideoPath, ct)
                            : null;

                    long currentCoverTimestampUs = vivoTiming?.CurrentTimestampUs
                        ?? await ReadCurrentCoverTimestampUsAsync(
                            imagePath, input.VideoPath, input.Protocol, ct);

                    long originalCoverTimestampUs = input.Protocol == LivePhotoProtocolType.OPPO
                        ? ReadOriginalCoverTimestampUs(imagePath)
                        : vivoTiming?.OriginalTimestampUs ?? 0;
                    bool hasDistinctOriginalCover =
                        input.Protocol == LivePhotoProtocolType.OPPO && originalCoverTimestampUs > 0
                        || vivoTiming?.HasEditedCover == true &&
                           originalCoverTimestampUs != currentCoverTimestampUs;

                    int currentCoverFrame = 0;
                    if (totalFrames > 0 && videoDurationSec > 0 && currentCoverTimestampUs > 0)
                    {
                        double fps = totalFrames / videoDurationSec;
                        currentCoverFrame = (int)Math.Round(currentCoverTimestampUs / 1_000_000.0 * fps);
                        // OPPO 相册按时间戳定位帧时会向后偏一帧（同 ffmpeg -ss 行为），
                        // 读取时补一帧，让“显示”和“实际封面”一致。
                        if (input.Protocol == LivePhotoProtocolType.OPPO)
                            currentCoverFrame = Math.Min(totalFrames - 1, currentCoverFrame + 1);
                        currentCoverFrame = Math.Clamp(currentCoverFrame, 0, Math.Max(0, totalFrames - 1));
                    }

                    int originalCoverFrame = 0;
                    if (totalFrames > 0 && videoDurationSec > 0 && originalCoverTimestampUs > 0)
                    {
                        double fps = totalFrames / videoDurationSec;
                        originalCoverFrame = (int)Math.Round(originalCoverTimestampUs / 1_000_000.0 * fps);
                        if (input.Protocol == LivePhotoProtocolType.OPPO)
                            originalCoverFrame = Math.Min(totalFrames - 1, originalCoverFrame + 1);
                        originalCoverFrame = Math.Clamp(originalCoverFrame, 0, Math.Max(0, totalFrames - 1));
                    }

                    string protocolDisplay = GetProtocolDisplayName(input.Protocol);
                    string livePhotoTypeDisplay = input.LivePhotoType == LivePhotoType.DualFile
                        ? "Dual-file"
                        : "Single-file";

                    if (viewOnly)
                    {
                        if (!json)
                        {
                            CliConsole.WriteFieldRgb("Photo", Path.GetFileName(imagePath), width: 16, valueColor: CliConsole.PathGreen);
                            if (pairedVideoPath != null)
                                CliConsole.WriteFieldRgb("Video", Path.GetFileName(pairedVideoPath), width: 16, valueColor: CliConsole.PathGreen);
                            CliConsole.WriteField("Protocol", $"{protocolDisplay} ({livePhotoTypeDisplay})", width: 16, valueColor: CliConsole.Highlight);

                            var fileInfo = new FileInfo(imagePath);
                            string sizeStr = FormatFileSize(fileInfo.Length);
                            if (pairedVideoPath != null)
                            {
                                var videoInfo = new FileInfo(pairedVideoPath);
                                sizeStr += $" + {FormatFileSize(videoInfo.Length)}";
                            }
                            CliConsole.WriteField("Size", sizeStr, width: 16, valueColor: CliConsole.Highlight);

                            if (videoDurationSec > 0)
                                CliConsole.WriteField("Duration", $"{videoDurationSec:F1}s / {totalFrames} frames", width: 16, valueColor: CliConsole.Highlight);

                            if (hasDistinctOriginalCover)
                                CliConsole.WriteField("Original cover", $"frame {originalCoverFrame + 1} ({originalCoverTimestampUs / 1_000_000.0:F3}s)", width: 16, valueColor: CliConsole.Highlight);

                            if (currentCoverTimestampUs > 0)
                                CliConsole.WriteField("Current cover", $"frame {currentCoverFrame + 1} ({currentCoverTimestampUs / 1_000_000.0:F3}s)", width: 16, valueColor: CliConsole.Highlight);
                            else
                                CliConsole.WriteField("Current cover", "frame 1 (still image)", width: 16, valueColor: CliConsole.Highlight);
                        }
                        else
                        {
                            PrintJson(new
                            {
                                command = "cover",
                                mode = "view",
                                file = imagePath,
                                video = pairedVideoPath,
                                protocol = protocolDisplay,
                                livePhotoType = livePhotoTypeDisplay,
                                durationSec = videoDurationSec,
                                totalFrames,
                                currentCoverFrame,
                                currentCoverTimestampUs,
                                originalCoverTimestampUs = hasDistinctOriginalCover
                                    ? originalCoverTimestampUs
                                    : (long?)null,
                                originalCoverFrame = hasDistinctOriginalCover
                                    ? originalCoverFrame
                                    : (int?)null
                            });
                        }

                        LogService.Info($"[Cover] View mode: protocol={input.Protocol}, currentCoverTsUs={currentCoverTimestampUs}, frames={totalFrames}", LogSource.System);

                        return 0;
                    }

                    int? resolvedFrameIndex = null;
                    int coverFrameNumber = 0;
                    if (!timestampUs.HasValue && frameNumber.HasValue)
                    {
                        double fps = totalFrames > 0 && videoDurationSec > 0
                            ? totalFrames / videoDurationSec
                            : await LivePhotoMergeService.DetectVideoFpsAsync(workingVideoPath!, ct);
                        long standardUs = (long)((frameNumber.Value - 1) / fps * 1_000_000.0);
                        resolvedFrameIndex = Math.Max(0, frameNumber.Value - 1);
                        // OPPO 相册按时间戳定位时会向后偏一帧：写时间戳时主动往前让一帧，
                        // 这样相册显示/导出的是用户选中的那一帧，和嵌入的封面图一致。
                        if (input.Protocol == LivePhotoProtocolType.OPPO && fps > 0)
                        {
                            long frameIntervalUs = (long)Math.Round(1_000_000.0 / fps);
                            standardUs = Math.Max(0, standardUs - frameIntervalUs);
                        }
                        timestampUs = standardUs;
                    }
                    else if (timestampUs.HasValue &&
                             input.Protocol == LivePhotoProtocolType.OPPO &&
                             totalFrames > 0 && videoDurationSec > 0)
                    {
                        // --at 同样适用 OPPO 偏一帧规则：先按用户给的时间换算成精确帧序号，
                        // 再用该帧的“前一帧时间戳”写入 XMP。
                        double fps = totalFrames / videoDurationSec;
                        int frameIndex = (int)Math.Round(timestampUs.Value / 1_000_000.0 * fps);
                        frameIndex = Math.Clamp(frameIndex, 0, Math.Max(0, totalFrames - 1));
                        resolvedFrameIndex = frameIndex;

                        long frameIntervalUs = (long)Math.Round(1_000_000.0 / fps);
                        timestampUs = Math.Max(0, timestampUs.Value - frameIntervalUs);
                    }

                    if (!timestampUs.HasValue)
                    {
                        CliConsole.WriteErrorLine("Error: Cannot determine cover position. Provide --at or --frame.");
                        return 1;
                    }

                    if (resolvedFrameIndex.HasValue)
                    {
                        coverFrameNumber = resolvedFrameIndex.Value;
                    }
                    else if (totalFrames > 0 && videoDurationSec > 0)
                    {
                        coverFrameNumber = (int)Math.Round(timestampUs.Value / 1_000_000.0 * totalFrames / videoDurationSec);
                        coverFrameNumber = Math.Clamp(coverFrameNumber, 0, Math.Max(0, totalFrames - 1));
                    }

                    if (!json)
                    {
                        CliConsole.WriteFieldRgb("Photo", Path.GetFileName(imagePath), width: 16, valueColor: CliConsole.PathGreen);
                        if (pairedVideoPath != null)
                            CliConsole.WriteFieldRgb("Video", Path.GetFileName(pairedVideoPath), width: 16, valueColor: CliConsole.PathGreen);
                        CliConsole.WriteField("Protocol", $"{protocolDisplay} ({livePhotoTypeDisplay})", width: 16, valueColor: CliConsole.Highlight);

                        var fileInfo = new FileInfo(imagePath);
                        string sizeStr = FormatFileSize(fileInfo.Length);
                        if (pairedVideoPath != null)
                        {
                            var videoInfo = new FileInfo(pairedVideoPath);
                            sizeStr += $" + {FormatFileSize(videoInfo.Length)}";
                        }
                            CliConsole.WriteField("Size", sizeStr, width: 16, valueColor: CliConsole.Highlight);

                        if (videoDurationSec > 0)
                            CliConsole.WriteField("Duration", $"{videoDurationSec:F1}s / {totalFrames} frames", width: 16, valueColor: CliConsole.Highlight);

                        double newTimestampSec = timestampUs.Value / 1_000_000.0;
                        if (input.Protocol == LivePhotoProtocolType.OPPO)
                        {
                            if (hasDistinctOriginalCover)
                                CliConsole.WriteField("Original cover", $"frame {originalCoverFrame + 1} ({originalCoverTimestampUs / 1_000_000.0:F3}s)", width: 16, valueColor: CliConsole.Highlight);
                            CliConsole.WriteField("Current cover", $"frame {coverFrameNumber + 1} ({newTimestampSec:F3}s)", width: 16, valueColor: CliConsole.Highlight);
                        }
                        else
                        {
                            if (currentCoverTimestampUs > 0)
                                CliConsole.WriteField("Current cover", $"frame {currentCoverFrame + 1} ({currentCoverTimestampUs / 1_000_000.0:F3}s)", width: 16, valueColor: CliConsole.Highlight);
                            else
                                CliConsole.WriteField("Current cover", "frame 1 (still image)", width: 16, valueColor: CliConsole.Highlight);

                            CliConsole.WriteField("New cover", $"frame {coverFrameNumber + 1} ({newTimestampSec:F3}s)", width: 16, valueColor: CliConsole.Highlight);
                        }
                    }

                    if (dryRun)
                    {
                        if (json)
                        {
                            PrintJson(new
                            {
                                command = "cover",
                                mode = "dry-run",
                                file = imagePath,
                                protocol = protocolDisplay,
                                wouldModify = true,
                                timestampUs,
                                frameNumber
                            });
                        }
                        else
                        {
                            Console.WriteLine("[DRY RUN] Would change cover. No files were modified.");
                        }

                        LogService.Info($"[Cover] DRY RUN: would change cover to tsUs={timestampUs.Value}, frame={coverFrameNumber + 1}", LogSource.System);

                        return 0;
                    }

                    if (!yes && !json)
                    {
                        Console.Write("Proceed? [Y/n] ");
                        string? key = Console.ReadLine();
                        if (key is null ||
                            string.Equals(key, "n", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(key, "no", StringComparison.OrdinalIgnoreCase))
                        {
                            CliConsole.WriteLine("Cancelled.", CliConsole.Muted);
                            return 0;
                        }
                    }

                    string outputDir = output?.FullName ?? Path.GetDirectoryName(imagePath)!;
                    Directory.CreateDirectory(outputDir);

                    string baseName = Path.GetFileNameWithoutExtension(imagePath);
                    string outputName = BuildOutputFileName(baseName, input.Protocol, naming, outputDir, imagePath, coverFrameNumber);
                    string imageExtension = GetOutputImageExtension(input);
                    string? videoExtension = GetOutputVideoExtension(input);

                    var outputPaths = GetUniqueOutputPaths(
                        outputDir,
                        outputName,
                        imageExtension,
                        videoExtension,
                        overwrite);
                    string outputImagePath = outputPaths.Image;
                    string? outputVideoPath = outputPaths.Video;

                    if (verbose && !json)
                    {
                        CliConsole.WriteFieldRgb("Output image", outputImagePath, width: 12, valueColor: CliConsole.PathGreen);
                        if (outputVideoPath != null)
                            CliConsole.WriteFieldRgb("Output video", outputVideoPath, width: 12, valueColor: CliConsole.PathGreen);
                    }

                    LogService.Info($"[Cover] Changing cover: tsUs={timestampUs.Value}, frame={(resolvedFrameIndex + 1) ?? coverFrameNumber + 1}, output='{outputImagePath}'", LogSource.System);

                    var result = await CoverChangeService.ChangeCoverAsync(
                        new CoverChangeRequest
                        {
                            ImagePath = imagePath,
                            VideoPath = pairedVideoPath,
                            LivePhotoType = input.LivePhotoType,
                            Protocol = input.Protocol,
                            TimestampUs = timestampUs.Value,
                            FrameIndex = resolvedFrameIndex,
                            OutputImagePath = outputImagePath,
                            OutputVideoPath = outputVideoPath
                        },
                        ct);

                    if (json)
                    {
                        PrintJson(new
                        {
                            command = "cover",
                            mode = "success",
                            protocol = protocolDisplay,
                            input = imagePath,
                            outputImage = result.OutputImagePath,
                            outputVideo = result.OutputVideoPath,
                            timestampUs = timestampUs.Value,
                            status = "success"
                        });
                    }
                    else
                    {
                        CliConsole.WriteFieldRgb("Cover saved", Path.GetFileName(result.OutputImagePath), width: 12, valueColor: CliConsole.PathGreen);
                        if (result.OutputVideoPath != null)
                            CliConsole.WriteFieldRgb("", Path.GetFileName(result.OutputVideoPath), width: 12, valueColor: CliConsole.PathGreen);
                        CliConsole.WriteLine("Done", CliConsole.Success);
                    }

                    LogService.Info($"[Cover] Success: output='{result.OutputImagePath}'{(result.OutputVideoPath != null ? $", video='{result.OutputVideoPath}'" : "")}", LogSource.System);

                    return 0;
                }
                finally
                {
                    if (previewTempDir != null && Directory.Exists(previewTempDir))
                    {
                        try { Directory.Delete(previewTempDir, recursive: true); } catch { /* best effort */ }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogService.Info("[Cover] Cancelled by user", LogSource.System);

                if (json)
                    PrintJson(new { command = "cover", status = "cancelled" });
                else
                    CliConsole.WriteErrorLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                LogService.Error($"[Cover] Failed: {ex.GetType().Name}: {ex.Message}", ex, LogSource.System);

                if (json)
                    PrintJson(new { command = "cover", status = "failed", error = ex.Message });
                else
                    CliConsole.WriteErrorLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static void ValidateInputFiles(string[] files, out string? error)
        {
            error = null;
            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    error = $"Error: File not found: {file}";
                    return;
                }
            }

            if (files.Length == 1 && !ImageExtensions.Contains(Path.GetExtension(files[0])))
            {
                error = $"Error: Unsupported file type '{Path.GetExtension(files[0])}'. Provide an image (.jpg/.jpeg/.heic/.heif).";
                return;
            }

            if (files.Length == 2)
            {
                bool hasImage = files.Any(f => ImageExtensions.Contains(Path.GetExtension(f)));
                bool hasVideo = files.Any(f => VideoExtensions.Contains(Path.GetExtension(f)));
                if (!hasImage || !hasVideo)
                {
                    error = "Error: Cannot determine which file is the image and which is the video.";
                    return;
                }
            }
        }

        private static async Task<long> ReadCurrentCoverTimestampUsAsync(
            string imagePath,
            string? videoPath,
            LivePhotoProtocolType protocol,
            CancellationToken token)
        {
            if (videoPath != null)
                return await LivePhotoMergeService.ReadSourceCoverTimestampAsync(videoPath, token);

            if (protocol == LivePhotoProtocolType.Huawei)
            {
                var tailInfo = HuaweiMovingPhotoProtocol.ReadTail(imagePath);
                return tailInfo.HasValue ? tailInfo.Value.coverMs * 1000L : 0;
            }

            string xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath);
            var match = System.Text.RegularExpressions.Regex.Match(
                xmpText,
                @"MotionPhotoPresentationTimestampUs[""=\s-]+(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long motionTimestamp))
                return motionTimestamp;

            match = System.Text.RegularExpressions.Regex.Match(
                xmpText,
                @"MicroVideoPresentationTimestampUs[""=\s-]+(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && long.TryParse(match.Groups[1].Value, out long microTimestamp)
                ? microTimestamp
                : 0;
        }

        private static long ReadOriginalCoverTimestampUs(string imagePath)
        {
            string xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath);
            var match = System.Text.RegularExpressions.Regex.Match(
                xmpText,
                @"MotionPhotoPrimaryPresentationTimestampUs[""=\s-]+(-?\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && long.TryParse(match.Groups[1].Value, out long primaryTimestamp)
                ? primaryTimestamp
                : 0;
        }

        private static string GetOutputImageExtension(CoverInputResolution input)
        {
            return input.Protocol switch
            {
                LivePhotoProtocolType.Apple => ".HEIC",
                LivePhotoProtocolType.Huawei when HeicConverterService.IsHeicFile(input.ImagePath) => ".HEIC",
                LivePhotoProtocolType.GoogleV2 when input.LivePhotoType == LivePhotoType.SingleFileHeic => ".HEIC",
                _ => ".JPG"
            };
        }

        private static string? GetOutputVideoExtension(CoverInputResolution input)
        {
            if (input.VideoPath == null)
                return null;

            return input.Protocol switch
            {
                LivePhotoProtocolType.Apple => Path.GetExtension(input.VideoPath).ToUpperInvariant(),
                LivePhotoProtocolType.Vivo when input.LivePhotoType == LivePhotoType.DualFile => Path.GetExtension(input.VideoPath).ToUpperInvariant(),
                _ => null
            };
        }

        private static string BuildOutputFileName(
            string baseName,
            LivePhotoProtocolType protocol,
            string naming,
            string outputDir,
            string sourceImagePath,
            int coverFrameNumber)
        {
            if (naming.StartsWith("custom:", StringComparison.OrdinalIgnoreCase))
            {
                string template = naming.Substring(7);
                if (!string.IsNullOrWhiteSpace(template))
                {
                    string rendered = LivePhotoMergeService.RenderNamingTemplate(
                        template,
                        baseName,
                        GetProtocolIndex(protocol),
                        1,
                        sourceImagePath,
                        coverFrameNumber + 1);
                    return SanitizeFileName(rendered);
                }
            }

            if (naming.Equals("keep", StringComparison.OrdinalIgnoreCase))
                return baseName;

            return SanitizeFileName($"{baseName}_cover{coverFrameNumber + 1}");
        }

        private static int GetProtocolIndex(LivePhotoProtocolType protocol)
        {
            return protocol switch
            {
                LivePhotoProtocolType.Fusion => 0,
                LivePhotoProtocolType.GoogleV1 => 1,
                LivePhotoProtocolType.GoogleV2 => 2,
                LivePhotoProtocolType.OPPO => 3,
                LivePhotoProtocolType.Vivo => 4,
                LivePhotoProtocolType.Samsung => 5,
                LivePhotoProtocolType.Huawei => 6,
                LivePhotoProtocolType.Apple => 2,
                _ => 2
            };
        }

        private static string GetProtocolDisplayName(LivePhotoProtocolType protocol)
        {
            return protocol switch
            {
                LivePhotoProtocolType.Huawei => "HUAWEI Moving Photo",
                LivePhotoProtocolType.Apple => "Apple Live Photo",
                LivePhotoProtocolType.GoogleV1 => "Google Micro Video",
                LivePhotoProtocolType.GoogleV2 => "Google Motion Photo",
                LivePhotoProtocolType.OPPO => "OPPO Live Photo",
                LivePhotoProtocolType.Samsung => "Samsung Motion Photo",
                LivePhotoProtocolType.Fusion => "Motion Photo Fusion",
                LivePhotoProtocolType.Vivo => "vivo Live Photo",
                _ => "Unknown"
            };
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string GetUniquePath(string targetPath, bool overwrite)
        {
            if (overwrite || !File.Exists(targetPath))
                return targetPath;

            string dir = Path.GetDirectoryName(targetPath)!;
            string baseName = Path.GetFileNameWithoutExtension(targetPath);
            string ext = Path.GetExtension(targetPath);

            int counter = 1;
            while (true)
            {
                string candidate = Path.Combine(dir, $"{baseName}_{counter}{ext}");
                if (!File.Exists(candidate))
                    return candidate;
                counter++;
            }
        }

        private static (string Image, string? Video) GetUniqueOutputPaths(
            string outputDir,
            string outputName,
            string imageExtension,
            string? videoExtension,
            bool overwrite)
        {
            if (overwrite)
            {
                string imagePath = Path.Combine(outputDir, outputName + imageExtension);
                string? videoPath = videoExtension == null
                    ? null
                    : Path.ChangeExtension(imagePath, videoExtension);
                return (imagePath, videoPath);
            }

            string candidateImage = Path.Combine(outputDir, outputName + imageExtension);
            string? candidateVideo = videoExtension == null
                ? null
                : Path.ChangeExtension(candidateImage, videoExtension);

            int counter = 0;
            while (File.Exists(candidateImage) ||
                   (candidateVideo != null && File.Exists(candidateVideo)))
            {
                counter++;
                string numberedName = $"{outputName}_{counter}";
                candidateImage = Path.Combine(outputDir, numberedName + imageExtension);
                candidateVideo = videoExtension == null
                    ? null
                    : Path.ChangeExtension(candidateImage, videoExtension);
            }

            return (candidateImage, candidateVideo);
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }
            return unitIndex == 0 ? $"{bytes} B" : $"{value:F1} {units[unitIndex]}";
        }

        private static void PrintJson(object data)
        {
            Console.WriteLine(JsonSerializer.Serialize(data));
        }
    }
}
