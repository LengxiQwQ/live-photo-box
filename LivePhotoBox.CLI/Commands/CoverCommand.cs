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
            var filesArg = new Argument<string[]>("files") { Description = "Live photo file path. Single file: auto-detected as single-file live photo or dual-file image (auto-pair). Two files: image + video pair." };
            filesArg.Arity = new ArgumentArity(1, 2);

            var atOpt = new Option<string?>("--at", "-a") { Description = "New cover position on the video timeline. Accepts seconds (2.500), mm:ss (1:30.500) or hh:mm:ss (0:01:30.500). Mutually exclusive with --frame." };
            var frameOpt = new Option<int?>("--frame") { Description = "New cover frame number (1-based). 1 = first frame. Mutually exclusive with --at." };

            var outputOpt = new Option<DirectoryInfo?>("--output", "-o") { Description = "Output directory. Default: source file's own directory." };
            var namingOpt = new Option<string>("--naming", "-n") { DefaultValueFactory = _ => "suffix", Description = "Output filename rule. keep (keep original name)|suffix (append protocol suffix)|custom:TEMPLATE.\n" +
                "Template tokens: {name} {protocol} {date} {time} {exif_date} {exif_time} {frame} {counter} {counter:D3}" };
            var overwriteOpt = new Option<bool>("--overwrite", "-w") { Description = "Replace existing files. Without this, name conflicts get auto-renamed." };
            var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip confirmation prompts. Useful for scripts / automation." };
            var dryRunOpt = new Option<bool>("--dry-run") { Description = "Preview only: show current cover info and what would be done, without modifying any files." };

            var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "Show detailed progress messages." };
            var jsonOpt = new Option<bool>("--json") { Description = "Output machine-readable JSON to stdout (implies --yes)." };

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

            command.Aliases.Add("keyphoto");
            command.SetAction(async (parseResult, cancellationToken) =>
            {
                var files = parseResult.GetValue(filesArg);
                var at = parseResult.GetValue(atOpt);
                var frame = parseResult.GetValue(frameOpt);
                var output = parseResult.GetValue(outputOpt);
                var naming = parseResult.GetValue(namingOpt)!;
                var overwrite = parseResult.GetValue(overwriteOpt);
                var yes = parseResult.GetValue(yesOpt);
                var dryRun = parseResult.GetValue(dryRunOpt);
                var verbose = parseResult.GetValue(verboseOpt);
                var json = parseResult.GetValue(jsonOpt);

                string? wildcard = files!.FirstOrDefault(CliInputValidator.HasWildcard);
                if (wildcard != null)
                {
                    CliInputValidator.WriteWildcardNotSupported();
                    Environment.ExitCode = 1;
                    return;
                }

                if (at != null && frame.HasValue)
                {
                    CliConsole.WriteErrorLine("Error: --at and --frame are mutually exclusive. Specify only one.");
                    Environment.ExitCode = 1;
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
                        Environment.ExitCode = 1;
                        return;
                    }
                    timestampUs = parsedUs;
                }

                bool viewOnly = !timestampUs.HasValue && !frameNumber.HasValue;
                if (json)
                    yes = true;

                Environment.ExitCode = await RunAsync(
                    files!,
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
                    cancellationToken);
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
                return await ProcessingPipelineRouter.RunAsync<int>("cover", () =>
                    throw new RebuiltPipelineNotReadyException("cover"));
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
    }
}
