using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
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
    internal static class SplitCommand
    {
        // Recognized single-file live photo extensions (positional argument).
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".heic", ".heif" };

        // Split-specific protocol name → index map.
        // Deliberately separate from ProtocolNameResolver: the merge page's vivo=4 conflicts with
        // split's vivo=2 (global split protocolIndex: 0=none / 1=Apple / 2=vivo).
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

        // Split protocol → format availability matrix (single source of truth in ProtocolFormatMatrix).
        // protocolIndex: 0=none / 1=Apple / 2=vivo (see SplitProtocolMap).
        // formatIndex:   0=keep / 1=jpg+mov / 2=heic+mov / 3=jpg+mp4 (see SplitFormatMap).
        // Mirrors the GUI SplitPage matrix.
        internal static readonly bool[][] SplitFormatMatrix = ProtocolFormatMatrix.SplitMatrix;

        // Split format short names, ordered by outputFormatIndex (for the protocols command matrix / JSON).
        internal static readonly string[] SplitFormatNames = ProtocolFormatMatrix.SplitFormatNames;

        // 默认格式：该协议的第一个可用格式（none→keep）。
        private static int GetSplitDefaultFormat(int protocolIndex)
            => ProtocolFormatMatrix.GetDefaultSplitFormat(protocolIndex);

        private static bool IsSplitFormatAvailable(int protocolIndex, int formatIndex)
            => ProtocolFormatMatrix.IsSplitAvailable(protocolIndex, formatIndex);

        // Split-specific pairing filter: protocol name → LivePhotoProtocolType (null = no filter).
        // Mirrors the GUI SplitViewModel.MatchProtocolType mapping (0=all → null, then
        // Fusion/GoogleV1/GoogleV2/OPPO/Vivo/Samsung/Huawei). all keeps the current
        // "scan everything" behavior.
        private static readonly Dictionary<string, LivePhotoProtocolType?> SplitPairingMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["all"]     = null,
            ["apple"]   = LivePhotoProtocolType.Apple,
            // Fusion 隐藏：CLI 不再暴露 fusion 配对过滤。
            // ["fusion"]  = LivePhotoProtocolType.Fusion,
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
            var filesArg = new Argument<string?>("files") { Description = "Live photo file (.jpg/.jpeg/.heic/.heif) or folder path. A path without a file extension is auto-detected as a folder (same as --dir)." };
            filesArg.Arity = ArgumentArity.ZeroOrOne;

            var dirOpt = new Option<DirectoryInfo?>("--dir", "-d") { Description = "Folder with single-file or natively paired dual-file live photos. All detected live photos are split. For batch mode; a folder path can also be passed as the positional argument." };
            var companionOpt = new Option<FileInfo?>("--companion") { Description = "Explicit companion video for a dual-file source. Native inspection must confirm the source binding; no filename guessing." };
            var pairingOpt = new Option<string>("--pairing") { DefaultValueFactory = _ => "all", Description = "Only split live photos of this protocol. all (no filter)|apple|v1 (MicroVideo)|v2 (MotionPhoto)|oppo|vivo|samsung|huawei." };

            var protocolOpt = new Option<string>("--protocol", "-p") { DefaultValueFactory = _ => "none", Description = "Target output: protocol-free media (none), Apple dual file (apple), or legacy vivo dual file (vivo)." };
            var formatOpt = new Option<string?>("--format", "-f") { Description = "Output format. keep (no conversion)|jpg+mov (H.265)|heic+mov (H.265)|jpg+mp4 (H.264).\nDefault: first available for the chosen protocol." };
            var keyTimestampOpt = new Option<string?>("--key-timestamp") { Description = "Override the key photo (cover) position on the video timeline (Apple conversion, single-file mode).\n" +
                "Accepts seconds (2.500), mm:ss (1:30.500) or hh:mm:ss (0:01:30.500).\n" +
                "Default: follow the source live photo's own timeline." };

            var outputOpt = new Option<DirectoryInfo?>("--output", "-o") { Description = "Output folder. Default: the source file's own directory for a single file; a \"{folder}_split\" subfolder inside the input folder for batch mode." };
            var namingOpt = new Option<string>("--naming", "-n") { DefaultValueFactory = _ => "keep", Description = "Output filename. keep (same name)|suffix (append _split)|custom:TEMPLATE.\nTemplate tokens: {name} {date} {date:yyyy-MM-dd} {time} {exif_date} {exif_time} {counter} {counter:D3}" };
            var parallelOpt = new Option<int>("--parallel", "-j") { DefaultValueFactory = _ => Math.Min(Environment.ProcessorCount, 5), Description = "How many files to process at once (1-64). More = faster CPU usage." };
            var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip confirmation prompts. Useful for scripts / automation." };
            var jsonOpt = new Option<bool>("--json") { Description = "Output machine-readable JSON to stdout (implies --yes)." };

            var dryRunOpt = new Option<bool>("--dry-run") { Description = "Preview: show what would be done, don't actually process files." };

            var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "Show per-file status messages instead of summary only." };
            var overwriteOpt = new Option<bool>("--overwrite", "-w") { Description = "Replace existing files. Without this, name conflicts get auto-renamed (photo.jpg -> photo (2).jpg)." };
            var recursiveOpt = new Option<bool>("--recursive", "-r") { Description = "Also scan subdirectories inside the input folder." };
            var preserveSubdirsOpt = new Option<bool>("--preserve-subdirs", "-s") { Description = "Keep source subdirectory structure in the output folder." };
            var afterOpt = new Option<string>("--after") { DefaultValueFactory = _ => "none", Description = "After successful split: none (keep source)|move:PATH (move to folder)|recycle (Windows recycle bin)." };

            var allVariantsOpt = new Option<bool>("--all-variants") { Description = "Export ALL split variants from a single live photo (single-file mode only). Exports the 4 protocol-free media variants (none_keep, none_jpg+mov, none_heic+mov, none_jpg+mp4).\n" +
                "Output goes to {output}/split_{name}_All_Variants/. Files are named {protocol}_{format}.ext." };

            var cmd = new Command("split",
                "Export single-file or paired dual-file live photos as protocol-free image and video files.\n" +
                "Input: .jpg .jpeg .heic .heif; dual files: image --companion video.mov\n\n" +
                "Single file:  lpb split photo.jpg\n" +
                "              (writes photo.jpg + photo.mov next to the source)\n" +
                "Batch folder: lpb split ./MyPhotos -y\n" +
                "              (folder auto-detected by missing extension; -d/--dir also works)\n" +
                "              (writes ./MyPhotos/MyPhotos_split/)\n" +
                "Convert:      lpb split photo.jpg -f jpg+mp4\n" +
                "All variants: lpb split photo.jpg --all-variants\n" +
                "Preview:      lpb split -d ./MyPhotos --dry-run\n" +
                "Wildcards:    not supported — pass a folder or explicit files\n" +
                "Formats:      lpb protocols")
            {
                filesArg,
                dirOpt, companionOpt, pairingOpt, protocolOpt, formatOpt, keyTimestampOpt, outputOpt, namingOpt,
                parallelOpt, yesOpt, jsonOpt, dryRunOpt, verboseOpt,
                overwriteOpt, recursiveOpt, preserveSubdirsOpt, afterOpt,
                allVariantsOpt
            };

            cmd.SetAction(async (parseResult, cancellationToken) =>
            {
                string? singlePath = parseResult.GetValue(filesArg);
                var dir = parseResult.GetValue(dirOpt);
                string? companionPath = parseResult.GetValue(companionOpt)?.FullName;
                var pairingName = parseResult.GetValue(pairingOpt)!;
                var protocolName = parseResult.GetValue(protocolOpt)!;
                var formatName = parseResult.GetValue(formatOpt);
                var keyTimestampText = parseResult.GetValue(keyTimestampOpt);
                var output = parseResult.GetValue(outputOpt);
                var naming = parseResult.GetValue(namingOpt)!;
                // 用户是否显式传了 --naming（未传时单文件默认 suffix、批量默认 keep）
                bool namingExplicit = parseResult.Tokens.Any(t =>
                    t.Value == "--naming" || t.Value == "-n"
                    || t.Value.StartsWith("--naming=", StringComparison.Ordinal)
                    || t.Value.StartsWith("-n=", StringComparison.Ordinal));
                var parallel = parseResult.GetValue(parallelOpt);
                var yes = parseResult.GetValue(yesOpt);
                var json = parseResult.GetValue(jsonOpt);
                var dryRun = parseResult.GetValue(dryRunOpt);
                var verbose = parseResult.GetValue(verboseOpt);
                var overwrite = parseResult.GetValue(overwriteOpt);
                var recursive = parseResult.GetValue(recursiveOpt);
                var preserveSubdirs = parseResult.GetValue(preserveSubdirsOpt);
                var after = parseResult.GetValue(afterOpt)!;
                var allVariants = parseResult.GetValue(allVariantsOpt);

                long? keyTimestampUs = null;
                if (keyTimestampText != null)
                {
                    if (!CliInputValidator.TryParseKeyTimestamp(keyTimestampText, out long parsedUs))
                    {
                        CliConsole.WriteErrorWithHint(
                            $"Error: Invalid --key-timestamp '{keyTimestampText}'.",
                            "Use seconds (e.g. 2.500), mm:ss (e.g. 1:30.500) or hh:mm:ss (e.g. 0:01:30.500).");
                        Environment.ExitCode = 1;
                        return;
                    }
                    keyTimestampUs = parsedUs;
                }

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
                    if (CliInputValidator.IsUnknownOption(singlePath, ImageExtensions))
                    {
                        CliInputValidator.WriteUnknownOptionError(singlePath, cmd);
                        Environment.ExitCode = 1;
                        return;
                    }

                    // 位置参数不带扩展名即自动识别为目录（批量模式，等价 -d/--dir）；
                    // 已存在的目录优先，目录名含点（如 "My.Photos"）也能正确识别。
                    var folderStatus = CliInputValidator.ResolveFolderInput(
                        singlePath, "supported files need .jpg/.jpeg/.heic/.heif", ref dir);
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
                        if (!ImageExtensions.Contains(ext))
                        {
                            CliConsole.WriteErrorLine($"Error: Unsupported file type '{ext}'. Supported: .jpg, .jpeg, .heic, .heif");
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
                    if (json && dir != null && !Directory.Exists(dir.FullName))
                        PrintBatchJson(dir.FullName, output?.FullName ?? string.Empty, 0, 0, 0, 0, new List<SplitJsonFileEntry>(),
                            new[] { LivePhotoScanSupport.Failure(dir.FullName,
                                new IOException("The selected input directory is unavailable or cannot be accessed."),
                                "DirectoryEnumerationFailed", stage: ScanDiagnosticStage.Enumeration) }, scanStatus: "failed");
                    Environment.ExitCode = 1;
                    return;
                }

                Environment.ExitCode = await RunAsync(
                    singlePath, dir, pairingName, protocolName, formatName, output, naming, namingExplicit,
                    parallel, yes, json, dryRun, verbose,
                    overwrite, recursive, preserveSubdirs, after, allVariants, keyTimestampUs,
                    cancellationToken, companionPath);
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            string? singlePath, DirectoryInfo? dir,
            string pairingName, string protocolName, string? formatName, DirectoryInfo? output,
            string naming, bool namingExplicit, int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs, string after, bool allVariants, long? keyTimestampUs,
            CancellationToken ct, string? companionPath)
        {
            try
            {
                return await ProcessingPipelineRouter.RunAsync("split", () => RunSplitAsync(
                    singlePath, dir, pairingName, protocolName, formatName, output, naming, namingExplicit,
                    parallel, yes, json, dryRun, verbose, overwrite, recursive, preserveSubdirs,
                    after, allVariants, keyTimestampUs, ct, companionPath));
            }
            catch (OperationCanceledException)
            {
                if (json) PrintJson(new { operation = "split", status = "cancelled" });
                else CliConsole.WriteErrorLine("Cancelled.");
                return 130;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (json) PrintJson(new { command = "split", status = "failed", reason = exception.Message });
                else CliConsole.WriteErrorLine(exception.Message);
                return 1;
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

        private static async Task<int> RunSplitAsync(
            string? singlePath, DirectoryInfo? dir,
            string pairingName, string protocolName, string? formatName, DirectoryInfo? output,
            string naming, bool namingExplicit, int parallel, bool yes, bool json, bool dryRun, bool verbose,
            bool overwrite, bool recursive, bool preserveSubdirs, string after, bool allVariants, long? keyTimestampUs,
            CancellationToken ct, string? companionPath)
        {
            bool isSingle = singlePath != null;
            bool isBatch = dir != null;
            const bool isRebuilt = true;
            if (companionPath is not null && !isSingle)
            {
                CliConsole.WriteErrorLine("Error: --companion requires one source image.");
                return 1;
            }

            // --all-variants 优先给出专属错误（避免先报通用的"缺少输入"误导用户）
            if (allVariants && !isSingle && !isBatch)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: --all-variants requires a single live photo file.",
                    "Example: 'lpb split photo.jpg --all-variants'");
                return 1;
            }

            if (!isSingle && !isBatch)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: Specify a live photo file, or use --dir for batch mode.",
                    "Examples: 'lpb split photo.jpg' or 'lpb split ./MyPhotos -y'.");
                return 1;
            }

            if (isSingle && isBatch)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: Cannot use both single-file and --dir batch mode.",
                    "Pass either a single file or a folder, not both.");
                return 1;
            }

            // --key-timestamp 只支持单文件（批量时每张照片的源时间戳不同，无法共用同一个覆盖值）
            if (keyTimestampUs.HasValue && !isSingle)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: --key-timestamp only works with a single live photo file (not --dir batch mode).",
                    "Example: 'lpb split photo.jpg -p apple --key-timestamp 2.500'");
                return 1;
            }

            // ── --all-variants path ─────────────────────────────────
            if (allVariants)
            {
                if (dir != null)
                {
                    CliConsole.WriteErrorLine("Error: --all-variants only works with a single live photo file (not --dir batch mode).");
                    return 1;
                }
                if (singlePath == null)
                {
                    CliConsole.WriteErrorLine("Error: --all-variants requires a single live photo file.");
                    return 1;
                }
                if (keyTimestampUs.HasValue)
                {
                    CliConsole.WriteErrorWithHint(
                        "Error: --key-timestamp is not supported with --all-variants.",
                        "Use it with a single Apple conversion, e.g. 'lpb split photo.jpg -p apple --key-timestamp 2.500'.");
                    return 1;
                }

                // Default output to the source file's directory (not cwd)
                string outputDir = output?.FullName ?? Path.GetDirectoryName(Path.GetFullPath(singlePath))!;
                Directory.CreateDirectory(outputDir);
                return await RunSplitAllVariantsAsync(singlePath, outputDir, parallel, dryRun, verbose, json, isRebuilt, ct, companionPath);
            }

            // Resolve split protocol
            if (!SplitProtocolMap.TryGetValue(protocolName.Trim(), out int protocolIndex))
            {
                CliConsole.WriteErrorLine($"Error: Unknown protocol '{protocolName}'. Valid: none, apple, vivo.{CliConsole.DidYouMean(protocolName, ["none", "apple", "vivo"])}");
                return 1;
            }

            // A live target writes cover semantics; protocol-free export has no live binding.
            if (keyTimestampUs.HasValue && protocolIndex == 0)
            {
                CliConsole.WriteErrorWithHint(
                    "Error: --key-timestamp requires an Apple or vivo live target; protocol none has no live cover binding.",
                    "Example: 'lpb split photo.jpg -p apple --key-timestamp 2.500'");
                return 1;
            }

            // Resolve split format (default: the protocol's first available format).
            int formatIndex = GetSplitDefaultFormat(protocolIndex);
            if (formatName != null)
            {
                if (!SplitFormatMap.TryGetValue(formatName.Trim().Replace(" ", ""), out formatIndex))
                {
                    string[] validFormats = ["keep", "jpg+mp4", "jpg+mov", "heic+mov"];
                    var matches = validFormats
                        .Where(v => v.StartsWith(formatName.Trim(), StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    CliConsole.WriteErrorLine(
                        matches.Count > 0
                            ? $"Error: Unknown format '{formatName}'. Did you mean: {string.Join(", ", matches)}?"
                            : $"Error: Unknown format '{formatName}'. Valid: {string.Join(", ", validFormats)}.");
                    return 1;
                }

                if (!IsSplitFormatAvailable(protocolIndex, formatIndex))
                {
                    string available = string.Join(", ", Enumerable.Range(0, SplitFormatNames.Length)
                        .Where(f => SplitFormatMatrix[protocolIndex][f])
                        .Select(f => SplitFormatDisplayNames[f]));
                    CliConsole.WriteErrorLine($"Error: Format '{formatName}' is not available for protocol '{protocolName}'. Available: {available}");
                    return 1;
                }
            }

            // Resolve pairing filter (null = no filter, keep the current scan-everything behavior)
            if (!SplitPairingMap.TryGetValue(pairingName.Trim(), out LivePhotoProtocolType? pairingProtocol))
            {
                CliConsole.WriteErrorLine($"Error: Unknown pairing '{pairingName}'. Valid: all, apple, v1, v2, oppo, vivo, samsung, huawei.{CliConsole.DidYouMean(pairingName, ["all", "apple", "v1", "v2", "oppo", "vivo", "samsung", "huawei"])}");
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
                if (string.IsNullOrWhiteSpace(customPattern))
                {
                    CliConsole.WriteErrorWithHint(
                        "Error: custom: naming template cannot be empty.",
                        "Example: -n \"custom:{name}_split_{date:yyyy-MM-dd}\"");
                    return 1;
                }
            }
            else
            {
                CliConsole.WriteErrorLine($"Error: Unknown naming rule '{naming}'. Valid: keep, suffix, custom:<pattern>.{CliConsole.DidYouMean(naming, ["keep", "suffix", "custom:TEMPLATE"])}");
                return 1;
            }

            // Resolve after-completion action
            string? afterMoveDir = null;
            bool afterRecycle = false;
            if (after.StartsWith("move:", StringComparison.OrdinalIgnoreCase))
            {
                afterMoveDir = after.Substring(5);
                if (string.IsNullOrWhiteSpace(afterMoveDir))
                {
                    CliConsole.WriteErrorWithHint(
                        "Error: --after move: requires a non-empty folder path.",
                        "Example: --after \"move:./Archived\"");
                    return 1;
                }
            }
            else if (after.Equals("recycle", StringComparison.OrdinalIgnoreCase))
                afterRecycle = true;
            else if (!after.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                CliConsole.WriteErrorLine($"Error: Unknown after-completion action '{after}'. Valid: none, move:<dir>, recycle.{CliConsole.DidYouMean(after, ["none", "move:PATH", "recycle"])}");
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
                    // 单文件模式：把文件名放到最顶部，确认界面不再重复打印 Source 全路径
                    if (isSingle)
                        CliConsole.WriteFieldRgb("Filename", Path.GetFileName(singlePath!), width: 10, valueColor: CliConsole.PathGreen);
                    CliConsole.WriteField("Protocol", SplitProtocolDisplayNames[protocolIndex], width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteField("Format", SplitFormatDisplayNames[formatIndex], width: 10, valueColor: CliConsole.Highlight);
                    if (pairingProtocol != null)
                        CliConsole.WriteField("Pairing", pairingName, width: 10, valueColor: CliConsole.Highlight);
                    CliConsole.WriteFieldRgb("Output", outputDir, width: 10, valueColor: CliConsole.PathGreen);
                }

                if (isSingle)
                {
                    // 配对协议过滤（单文件模式，等价 GUI 的 PassesProtocolFilter）
                    var filterInspection = pairingProtocol is null ? null : await new SourceInspector()
                        .InspectForDiscoveryAsync(singlePath!, companionPath, ct);
                    if (filterInspection?.Failure is { } refusal)
                    {
                        if (json) PrintSingleJson(singlePath!, "", "", "failed", reason: refusal.Reason);
                        else CliConsole.WriteErrorLine(refusal.Reason);
                        return 1;
                    }
                    if (pairingProtocol is not null && MapNativeProtocol(filterInspection!.Facts!.Protocol) != pairingProtocol.Value)
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
                        overwrite, verbose, json, afterMoveDir, afterRecycle, keyTimestampUs, ct, companionPath);
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

        private static LivePhotoProtocolType MapNativeProtocol(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
            SourceProtocol.GoogleMicroVideoV1 => LivePhotoProtocolType.GoogleV1,
            SourceProtocol.GoogleMotionPhotoV2 => LivePhotoProtocolType.GoogleV2,
            SourceProtocol.OppoLivePhoto => LivePhotoProtocolType.OPPO,
            SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile => LivePhotoProtocolType.Vivo,
            SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic => LivePhotoProtocolType.Samsung,
            SourceProtocol.HuaweiMovingPhoto or SourceProtocol.HonorMovingPhoto => LivePhotoProtocolType.Huawei,
            _ => LivePhotoProtocolType.Unknown
        };

        private static async Task<int> SplitSingleAsync(
            string sourcePath, string outputDir, int protocolIndex, int formatIndex,
            string? customPattern, bool overwrite, bool verbose, bool json,
            string? afterMoveDir, bool afterRecycle, long? keyTimestampUs, CancellationToken ct, string? companionPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            try
            {
                string? outputBaseName = ComputeOutputBaseName(baseName, customPattern, protocolIndex, 1, sourcePath);
                if (verbose && !json)
                    Console.WriteLine($"Splitting: {Path.GetFileName(sourcePath)} ...");

                var capability = ProductCapabilityQuery.Query(ProductSelectionAdapter.Split(protocolIndex, formatIndex));
                if (!json) Console.WriteLine($"Preparation capability: {capability.Diagnostic}");
                var outcome = await LivePhotoSplitService.SplitWithOutcomeAsync(
                    sourcePath, outputDir, ProductSelectionAdapter.Split(protocolIndex, formatIndex), ct,
                    inputDirectory: null, outputBaseName: outputBaseName, overwriteExisting: overwrite, secondaryPath: companionPath, keyTimestampUs: keyTimestampUs);
                var afterAction = outcome.IsSuccess && (afterMoveDir is not null || afterRecycle)
                    ? MergeCommand.ApplySourceAction(sourcePath, companionPath ?? "", outcome, afterMoveDir, ct) : null;
                if (json) PrintJson(ProjectSingleOutcome(sourcePath, outcome, capability, afterAction));
                else
                {
                    if (outcome.IsSuccess) CliConsole.WriteLine("Done", CliConsole.Success);
                    else CliConsole.WriteErrorLine($"{OutcomeStatus(outcome)}: {outcome.Details}");
                    PrintOutcome(outcome);
                }
                if (afterAction?.Status == "failed") { if (!json) CliConsole.WriteErrorLine("Source post-processing failed: " + afterAction.Reason); return 1; }
                return afterAction?.Status == "cancelled" ? 130 : outcome.ExitCode;
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
        }

        // ══════════════════════════════════════════════════════════════
        //  --all-variants: 从单个单文件实况照片导出全部拆分变体
        //  （Apple 双文件 / vivo 双文件 / 无协议 keep 原样，各一组同名图片+视频）
        // ══════════════════════════════════════════════════════════════

        private static async Task<int> RunSplitAllVariantsAsync(
            string sourcePath, string outputDir, int parallel, bool dryRun, bool verbose, bool json, bool rebuiltOnly, CancellationToken ct, string? companionPath)
        {
            string originalBaseName = Path.GetFileNameWithoutExtension(sourcePath);
            string variantsDir = Path.Combine(outputDir, $"split_{originalBaseName}_All_Variants");
            Directory.CreateDirectory(variantsDir);
            var variants = new (int Proto, int Fmt, string BaseName, string Label)[]
            {
                (0, 0, "none_keep", "No protocol (keep original)"),
                (0, 1, "none_jpg+mov", "No protocol (JPG+MOV)"),
                (0, 2, "none_heic+mov", "No protocol (HEIC+MOV)"),
                (0, 3, "none_jpg+mp4", "No protocol (JPG+MP4)"),
            };
            var entries = variants.Select(v => new SplitJsonFileEntry { Path = sourcePath, CompanionPath = companionPath, Name = v.BaseName,
                Status = dryRun ? "would-split" : "unprocessed" }).ToArray();
            if (dryRun)
            {
                if (json) PrintJson(new { command = "split", mode = "all-variants", output = variantsDir, status = "dry-run", files = entries });
                else { Console.WriteLine($"[DRY RUN] Would generate {variants.Length} variants in {variantsDir}:"); foreach (var v in variants) Console.WriteLine(v.BaseName); }
                return 0;
            }
            int ok = 0, fail = 0, completed = 0;
            using var semaphore = new SemaphoreSlim(Math.Max(1, parallel));
            var tasks = variants.Select(async (v, index) =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    entries[index].Capability = ProductCapabilityQuery.Query(ProductSelectionAdapter.Split(v.Proto, v.Fmt));
                    if (!json) Console.WriteLine($"Preparation capability: {entries[index].Capability!.Diagnostic}");
                    var outcome = await LivePhotoSplitService.SplitWithOutcomeAsync(
                        sourcePath, variantsDir, ProductSelectionAdapter.Split(v.Proto, v.Fmt), ct,
                        inputDirectory: null, outputBaseName: v.BaseName, overwriteExisting: true, secondaryPath: companionPath);
                    var entry = entries[index];
                    entry.Outcome = outcome; entry.Status = OutcomeStatus(outcome);
                    entry.ImageOutput = CommittedPath(outcome, "PrimaryImage"); entry.VideoOutput = CommittedPath(outcome, "MotionVideo");
                    entry.Reason = outcome.IsSuccess ? "" : outcome.Details; entry.HistoryWarning = outcome.HistoryWarning;
                    if (outcome.Status == ProductOperationStatus.Cancelled) throw new OperationCanceledException(ct);
                    if (outcome.IsSuccess) Interlocked.Increment(ref ok); else if (outcome.Status != ProductOperationStatus.Warning) Interlocked.Increment(ref fail);
                    int count = Interlocked.Increment(ref completed);
                    if (!json) { Console.WriteLine($"[{count}/{variants.Length}] {entry.Status}  {v.Label}  {entry.Reason}"); PrintOutcome(outcome); }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    entries[index].Status = "failed"; entries[index].Reason = ex.Message;
                    Interlocked.Increment(ref fail); Interlocked.Increment(ref completed);
                    if (!json) CliConsole.WriteErrorLine($"ERROR  {v.Label}  ({ex.Message})");
                }
                finally { semaphore.Release(); }
            });
            bool cancelled = false;
            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { cancelled = true; }
            if (json) PrintJson(new { command = "split", mode = "all-variants", output = variantsDir,
                status = cancelled ? "cancelled" : "completed", scanned = variants.Length, split = ok, failed = fail, warnings = entries.Count(e => e.Status == "warning"), files = entries });
            else Console.WriteLine($"{(cancelled ? "Cancelled" : "Done")}: {ok} SUCCESS, {fail} FAIL, {variants.Length} total");
            return cancelled ? 130 : fail > 0 ? 1 : entries.Any(e => e.Status == "warning") ? 2 : 0;
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
            var discovery = await LivePhotoDiscoveryService.ScanAsync(inputDir, DiscoveryScanMode.SplitOnly, ct);
            var scanDiagnostics = discovery.Diagnostics.ToList();
            var liveItems = discovery.Items
                .Where(i => i.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic || i.LivePhotoType == LivePhotoType.DualFile && i.IsImage)
                .Where(i => pairingProtocol == null || MapNativeProtocol(i.Protocol) == pairingProtocol.Value)
                .ToList();

            foreach (var item in discovery.Items.Where(i =>
                (i.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic || i.LivePhotoType == LivePhotoType.DualFile && i.IsImage) &&
                pairingProtocol != null && MapNativeProtocol(i.Protocol) != pairingProtocol.Value))
                scanDiagnostics.Add(LivePhotoScanSupport.Info(item.FilePath, "ProtocolFiltered", "This source protocol is not selected."));

            if (discovery.Status == ScanOperationStatus.Cancelled)
            {
                if (json) PrintBatchJson(inputDir, outputDir, 0, 0, 0, 0, new List<SplitJsonFileEntry>(),
                    scanDiagnostics, scanStatus: "cancelled");
                else CliConsole.WriteErrorLine("Scan cancelled; no processing started.");
                return 130;
            }
            var scanSummary = new
            {
                totalItems = discovery.Items.Count,
                livePhotosCount = discovery.LivePhotoCount,
                diagnosticsCount = scanDiagnostics.Count,
                isPartial = discovery.IsPartial
            };
            string scanStatus = discovery.Status.ToString().ToLowerInvariant();
            bool hasScanErrors = discovery.Status == ScanOperationStatus.Failed || scanDiagnostics.Any(d => d.Severity == ScanDiagnosticSeverity.Error);

            if (!json)
            {
                Console.WriteLine();
                CliConsole.Write(liveItems.Count.ToString(), CliConsole.Highlight);
                Console.Write(" live photo sources found, ");
                CliConsole.Write(scanDiagnostics.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" diagnostics");

                if (scanDiagnostics.Count > 0)
                {
                    foreach (var diag in scanDiagnostics)
                    {
                        var color = diag.Severity switch
                        {
                            ScanDiagnosticSeverity.Error => CliConsole.Error,
                            ScanDiagnosticSeverity.Warning => CliConsole.Highlight,
                            _ => CliConsole.Muted
                        };
                        CliConsole.Write($"  [{diag.Severity}] ", color);
                        Console.WriteLine($"{Path.GetFileName(diag.PrimaryPath)}: {diag.Summary}");
                    }
                }
            }

            if (liveItems.Count == 0)
            {
                if (json)
                    PrintBatchJson(inputDir, outputDir, 0, 0, 0, 0, new List<SplitJsonFileEntry>(),
                        scanDiagnostics, scanSummary, scanStatus);
                else
                {
                    if (scanDiagnostics.Count > 0)
                    {
                        Console.WriteLine();
                        CliConsole.WriteLine("Scan diagnostics:", CliConsole.Muted);
                        foreach (var diag in scanDiagnostics)
                        {
                            var color = diag.Severity switch
                            {
                                ScanDiagnosticSeverity.Error => CliConsole.Error,
                                ScanDiagnosticSeverity.Warning => CliConsole.Highlight,
                                _ => CliConsole.Muted
                            };
                            CliConsole.Write($"  [{diag.Severity}] ", color);
                            Console.WriteLine($"{Path.GetFileName(diag.PrimaryPath)}: {diag.Summary}");
                        }
                    }
                    CliConsole.WriteErrorLine("No single-file live photos found. Nothing to do.");
                }
                return hasScanErrors ? 1 : 0;
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
                    SourcePath = item.FilePath, SecondaryPath = item.PairedVideoPath,
                    BaseName = Path.GetFileNameWithoutExtension(item.FilePath),
                };
                if (json)
                {
                    task.Json = new SplitJsonFileEntry
                    {
                        Path = item.FilePath, CompanionPath = item.PairedVideoPath,
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
                    PrintBatchJson(inputDir, outputDir, tasks.Count, 0, 0, 0, jsonFiles,
                        scanDiagnostics, scanSummary, scanStatus);
                else
                {
                    Console.WriteLine();
                    Console.Write("[DRY RUN] Would split ");
                    CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                    Console.WriteLine(" files:");
                    foreach (var t in tasks)
                    {
                        CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                        Console.Write("  ");
                        CliConsole.Write(Path.GetFileName(t.SourcePath), CliConsole.PathGreen);
                        Console.WriteLine();
                    }
                    if (scanDiagnostics.Count > 0)
                    {
                        Console.WriteLine();
                        CliConsole.WriteLine("Scan diagnostics:", CliConsole.Muted);
                        foreach (var diag in scanDiagnostics)
                        {
                            var color = diag.Severity switch
                            {
                                ScanDiagnosticSeverity.Error => CliConsole.Error,
                                ScanDiagnosticSeverity.Warning => CliConsole.Highlight,
                                _ => CliConsole.Muted
                            };
                            CliConsole.Write($"  [{diag.Severity}] ", color);
                            Console.WriteLine($"{Path.GetFileName(diag.PrimaryPath)}: {diag.Summary}");
                        }
                    }
                }
                return hasScanErrors ? 1 : 0;
            }

            // 4. Confirmation（交互模式先列出匹配文件，确认后再处理；-y / --json 静默跳过）
            if (!yes && !json)
            {
                Console.WriteLine();
                Console.Write("Matched ");
                CliConsole.Write(tasks.Count.ToString(), CliConsole.Highlight);
                Console.WriteLine(" files:");
                foreach (var t in tasks)
                {
                    CliConsole.Write($"#{t.Index}", CliConsole.Highlight);
                    Console.Write("  ");
                    CliConsole.Write(Path.GetFileName(t.SourcePath), CliConsole.PathGreen);
                    Console.WriteLine();
                }
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
            var capability = ProductCapabilityQuery.Query(ProductSelectionAdapter.Split(protocolIndex, formatIndex));
            if (!json) Console.WriteLine($"Preparation capability: {capability.Diagnostic}");
            foreach (var entry in jsonFiles) entry.Capability = capability;
            using var originals = new SourceInputProtection(tasks.SelectMany(t => new[] { t.SourcePath, t.SecondaryPath }));
            using var semaphore = new SemaphoreSlim(Math.Max(1, parallel));
            var running = tasks.Select(async task =>
            {
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (verbose && !json)
                        Console.WriteLine($"[{task.Index}/{tasks.Count}] {Path.GetFileName(task.SourcePath)} ...");

                    string? outputBaseName = ComputeOutputBaseName(
                        task.BaseName, customPattern, protocolIndex, task.Index, task.SourcePath);

                    try
                    {
                        var outcome = await LivePhotoSplitService.SplitWithOutcomeAsync(
                            task.SourcePath, outputDir, ProductSelectionAdapter.Split(protocolIndex, formatIndex), ct,
                            inputDirectory: preserveSubdirs ? inputDir : null,
                            outputBaseName: outputBaseName,
                            overwriteExisting: overwrite, secondaryPath: task.SecondaryPath);

                        task.Outcome = outcome;
                        if (task.Json != null) { task.Json.Outcome = outcome; task.Json.Status = OutcomeStatus(outcome);
                            task.Json.ImageOutput = CommittedPath(outcome, "PrimaryImage"); task.Json.VideoOutput = CommittedPath(outcome, "MotionVideo");
                            task.Json.Reason = outcome.IsSuccess ? "" : outcome.Details; task.Json.HistoryWarning = outcome.HistoryWarning; }
                        if (!json) PrintOutcome(outcome);
                        if (outcome.Status == ProductOperationStatus.Cancelled)
                        {
                            task.Status = ProcessStatus.Cancelled;
                            throw new OperationCanceledException(ct);
                        }
                        if (outcome.Status == ProductOperationStatus.Warning)
                        {
                            task.Status = ProcessStatus.Warning;
                            int count = Interlocked.Increment(ref completed);
                            if (!json) CliConsole.WriteLine($"[{count}/{tasks.Count}] WARNING  {Path.GetFileName(task.SourcePath)}  ({outcome.Details})", CliConsole.Notice);
                            return;
                        }
                        if (!outcome.IsSuccess)
                        {
                            task.Status = ProcessStatus.Failed;
                            task.Details = outcome.Details;
                            Interlocked.Increment(ref fail);
                            int failedCount = Interlocked.Increment(ref completed);
                            if (!json) CliConsole.WriteErrorLine($"[{failedCount}/{tasks.Count}] {OutcomeStatus(outcome)}  {Path.GetFileName(task.SourcePath)}  ({outcome.Details})");
                            return;
                        }
                        task.Status = ProcessStatus.Success;
                        Interlocked.Increment(ref ok);
                        int c = Interlocked.Increment(ref completed);
                        if (!json)
                        {
                            if (verbose)
                            {
                                CliConsole.WriteLine("SUCCESS", CliConsole.Success);
                            }
                            else
                            {
                                Console.Write($"[{c}/{tasks.Count}] ");
                                CliConsole.Write("SUCCESS  ", CliConsole.Success);
                                Console.WriteLine(Path.GetFileName(task.SourcePath));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        task.Status = ProcessStatus.Cancelled;
                        if (task.Json != null) task.Json.Status = "cancelled";
                        throw;
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
                                Console.Write($"[{c}/{tasks.Count}] ");
                                CliConsole.Write("FAIL  ", CliConsole.Error);
                                Console.WriteLine($"{Path.GetFileName(task.SourcePath)}  ({ex.Message})");
                            }
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            try
            {
                await Task.WhenAll(running);
            }
            catch (OperationCanceledException)
            {
                foreach (var task in tasks.Where(task => task.Status == ProcessStatus.Pending))
                    if (task.Json != null) task.Json.Status = "unprocessed";
                if (json) PrintBatchJson(inputDir, outputDir, tasks.Count, ok, fail, tasks.Count(task => task.Status == ProcessStatus.Pending), jsonFiles,
                    scanDiagnostics, scanSummary, "cancelled");
                else CliConsole.WriteErrorLine($"Cancelled: {ok} succeeded, {fail} failed.");
                return 130;
            }

            originals.Dispose();

            int afterFailed = 0;
            if (afterMoveDir is not null || afterRecycle)
                foreach (var task in tasks.Where(t => t.Status == ProcessStatus.Success))
                {
                    var action = MergeCommand.ApplySourceAction(task.SourcePath, task.SecondaryPath ?? "", task.Outcome!, afterMoveDir, ct);
                    if (task.Json is not null) task.Json.AfterAction = action;
                    if (action.Status == "failed")
                    {
                        afterFailed++;
                        if (!json) CliConsole.WriteErrorLine("Source post-processing failed: " + action.Reason);
                    }
                    if (action.Status == "cancelled")
                    {
                        if (json) PrintBatchJson(inputDir, outputDir, tasks.Count, ok, fail, 0, jsonFiles, scanDiagnostics, scanSummary, "cancelled");
                        return 130;
                    }
                }

            // 7. Summary
            if (json)
                PrintBatchJson(inputDir, outputDir, tasks.Count, ok, fail, 0, jsonFiles,
                    scanDiagnostics, scanSummary, scanStatus);
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
                Console.WriteLine($"{tasks.Count(t => t.Status == ProcessStatus.Warning)} isolated exports (not counted as SUCCESS).");
            }
            return (fail > 0 || afterFailed > 0 || hasScanErrors) ? 1 : tasks.Any(t => t.Status == ProcessStatus.Warning) ? 2 : 0;
        }

        // 序列化 JSON 到 stdout（脚本模式 --json 用，方便脚本稳定解析，不受文件名长度/终端宽度影响）。
        private static void PrintJson(object data)
            => Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));

        // 单文件模式的 JSON 结果。
        private static void PrintSingleJson(string input, string imageOutput, string videoOutput, string status, string reason = "")
            => PrintJson(new { command = "split", mode = "single", input, imageOutput, videoOutput, status, reason });

        // 批量模式的 JSON 结果。
        private static void PrintBatchJson(string input, string output, int scanned, int split, int failed, int skipped, List<SplitJsonFileEntry> files,
            IReadOnlyList<ScanDiagnostic>? scanDiagnostics = null,
            object? scanSummary = null,
            string scanStatus = "completed")
            => PrintJson(new {
                command = "split",
                mode = "batch",
                input,
                output,
                status = scanStatus == "cancelled" ? "cancelled" : (scanStatus == "failed" || failed > 0 || files.Any(f => f.AfterAction?.Status == "failed") || (scanDiagnostics != null && scanDiagnostics.Any(d => d.Severity == ScanDiagnosticSeverity.Error))) ? "failed" : files.Any(f => f.Status == "warning") ? "warning" : "completed",
                warnings = files.Count(f => f.Status == "warning"),
                afterFailed = files.Count(f => f.AfterAction?.Status == "failed"),
                scanned,
                split,
                failed,
                skipped,
                scanStatus,
                scanSummary,
                scanDiagnostics = scanDiagnostics?.Select(d => new
                {
                    code = d.DiagnosticCode,
                    severity = d.Severity.ToString().ToLowerInvariant(),
                    stage = d.Stage.ToString().ToLowerInvariant(),
                    primaryPath = d.PrimaryPath,
                    relatedPaths = d.RelatedPaths,
                    groupKey = d.GroupKey,
                    summary = d.Summary,
                    messageKey = d.MessageKey,
                    category = d.RawCategory?.ToString(),
                    inspectionStage = d.RawStage?.ToString(),
                    capability = d.RawCapability
                }).ToList() ?? (object)Array.Empty<object>(),
                files
            });

        private sealed class SplitTaskInfo
        {
            public int Index;
            public string SourcePath = "";
            public string? SecondaryPath;
            public string BaseName = "";
            public ProcessStatus Status = ProcessStatus.Pending;
            public string Details = "";
            public ProductOperationOutcome? Outcome;
            public SplitJsonFileEntry? Json = null; // 脚本模式（--json）关联的 JSON 结果条目
        }

        // 脚本模式（--json）下单个文件的 JSON 结果条目。
        private sealed class SplitJsonFileEntry
        {
            public string Path { get; set; } = "";
            public string? CompanionPath { get; set; }
            public string Name { get; set; } = "";
            public string Status { get; set; } = "";  // split / would-split / failed / skipped
            public string Reason { get; set; } = "";  // 跳过/失败原因
            public string ImageOutput { get; set; } = "";
            public string VideoOutput { get; set; } = "";
            public ProductOperationOutcome? Outcome { get; set; }
            public string Color => Outcome?.Status == ProductOperationStatus.Cancelled ? "neutral" : Status is "merged" or "split" ? "green" : Status == "warning" ? "yellow" : Status is "failed" or "unsupported" ? "red" : "neutral";
            public string? HistoryWarning { get; set; }
            public MergeCommand.SourceActionResult? AfterAction { get; set; }
            public ProductCapabilityAssessment? Capability { get; set; }
        }

        private static string OutcomeStatus(ProductOperationOutcome outcome) => outcome.IsSuccess ? "split" : outcome.Status.ToString().ToLowerInvariant();
        private static string CommittedPath(ProductOperationOutcome outcome, string role) => outcome.HasUsableOutputs ? outcome.CommittedOutputs.FirstOrDefault(item => item.Role == role)?.Path ?? "" : "";
        internal static object ProjectSingleOutcome(string input, ProductOperationOutcome outcome, ProductCapabilityAssessment? capability = null, object? afterAction = null) =>
            new { command = "split", mode = "single", input, imageOutput = CommittedPath(outcome, "PrimaryImage"),
                videoOutput = CommittedPath(outcome, "MotionVideo"), status = OutcomeStatus(outcome),
                color = outcome.Status == ProductOperationStatus.Cancelled ? "neutral" : outcome.IsSuccess ? "green" : outcome.Status == ProductOperationStatus.Warning ? "yellow" : "red",
                isolationDirectory = outcome.IsolationDirectory, warnings = outcome.Warnings, reason = outcome.IsSuccess ? "" : outcome.Details, outcome, capability, afterAction, historyWarning = outcome.HistoryWarning };

        private static void PrintOutcome(ProductOperationOutcome outcome)
        {
            foreach (var artifact in outcome.CommittedOutputs) Console.WriteLine($"-> {artifact.Path}");
            Console.WriteLine($"Final target preservation: {outcome.FinalTargetPreservation}");
            if (outcome.PreparationExecution is { } execution)
            {
                if (execution.Image is { } image)
                    Console.WriteLine($"Image preparation backend: {image.Truth.BackendName}; prepared media outcome: {image.Truth.PreservationOutcome}");
                foreach (var record in execution.Videos)
                    Console.WriteLine($"Video preparation backend: {record.Truth.BackendName}; encoder: {record.SelectedEncoder}; fallback selected: {record.HardwareFallbackOccurred}; prepared media outcome: {record.Truth.PreservationOutcome}; {record.HardwareFallbackReason}");
                if (execution.Image is null && execution.Videos.Count == 0) Console.WriteLine("Preparation backend: not executed or unknown.");
            }
            else Console.WriteLine("Preparation backend: not executed or unknown.");
            if (!outcome.IsSuccess) Console.WriteLine($"Diagnostic: {outcome.Diagnostic.Stage}/{outcome.Diagnostic.Category}; {outcome.Details}");
            if (!string.IsNullOrEmpty(outcome.HistoryWarning)) CliConsole.WriteErrorLine($"WARN: {outcome.HistoryWarning}");
        }

    }
}
