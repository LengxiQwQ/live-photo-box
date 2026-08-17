/*
 * CliInputValidator.cs
 *
 * CLI 输入校验辅助：目录自动识别、未知选项提示、输入/输出路径与并发数校验。
 *
 *   - merge / split / repair 三个命令共用，保证行为一致、改一处生效
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LivePhotoBox.Cli.Infrastructure
{
    internal static class CliInputValidator
    {
        /// <summary>--parallel 允许的上限（防止过大的并行度拖垮机器）。</summary>
        public const int MaxParallel = 64;

        /// <summary>路径里是否含通配符（* 或 ?）。CLI 不展开通配符：cmd/PowerShell 原样传递，
        /// 而 Git Bash 等 shell 会先展开，在 CLI 内再做展开会造成双重展开/语义含糊。</summary>
        public static bool HasWildcard(string path)
            => path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0;

        /// <summary>打印"不支持通配符"错误并给出替代方案。</summary>
        public static void WriteWildcardNotSupported()
            => CliConsole.WriteErrorWithHint(
                "Error: Wildcards are not supported.",
                "Pass a folder with -d/--dir (e.g. 'lpb split ./Photos -y') or list files explicitly.");

        /// <summary>位置参数解析结果。</summary>
        public enum FolderInputStatus
        {
            /// <summary>不是目录形态（有扩展名且不是已存在目录），调用方应继续按文件处理。</summary>
            NotFolder,
            /// <summary>已解析为批量目录（写入 dir）。</summary>
            Resolved,
            /// <summary>目录形态但不存在（已打印错误），调用方应立即退出。</summary>
            NotFound,
        }

        /// <summary>
        /// 位置参数是否应视为文件夹（批量模式）：已存在目录优先；带尾部分隔符或无扩展名也算目录。
        /// 目录名即使含点（如 "My.Photos"）也会因存在性判断优先而正确识别。
        /// </summary>
        public static bool IsFolderPath(string path)
            => Directory.Exists(path)
               || path.EndsWith(Path.DirectorySeparatorChar)
               || path.EndsWith(Path.AltDirectorySeparatorChar)
               || string.IsNullOrEmpty(Path.GetExtension(path));

        /// <summary>
        /// 尝试把位置参数解析为批量目录。目录不存在时打印带纠正提示的错误（若它其实是个无扩展名文件）。
        /// </summary>
        /// <param name="path">位置参数。</param>
        /// <param name="supportedExtensionsHint">无扩展名文件场景下的提示文案（支持的扩展名）。</param>
        /// <param name="dir">解析成功时写入目录；调用方传入的 -d 目录优先保留。</param>
        public static FolderInputStatus ResolveFolderInput(string path, string supportedExtensionsHint, ref DirectoryInfo? dir)
        {
            if (!IsFolderPath(path)) return FolderInputStatus.NotFolder;
            if (!Directory.Exists(path))
            {
                // 无扩展名按目录处理，但若它其实是个文件，给出提示避免误导。
                string hint = File.Exists(path)
                    ? $" (this is a file without a file extension; {supportedExtensionsHint})"
                    : "";
                CliConsole.WriteErrorLine($"Error: Directory not found: {path}{hint}");
                return FolderInputStatus.NotFound;
            }
            dir ??= new DirectoryInfo(path);
            return FolderInputStatus.Resolved;
        }

        /// <summary>
        /// 未知选项判定：以 - 开头、不是已支持的文件扩展名、也不是已存在目录
        /// （System.CommandLine 会把未知选项吞成位置参数，需提前识别）。
        /// </summary>
        public static bool IsUnknownOption(string value, IEnumerable<string> recognizedExtensions)
            => value.StartsWith('-')
               && !recognizedExtensions.Contains(Path.GetExtension(value))
               && !Directory.Exists(value);

        /// <summary>打印未知选项错误（含 Did you mean 建议 + 帮助提示）。</summary>
        public static void WriteUnknownOptionError(string option, IEnumerable<string> optionAliases, string commandName)
            => CliConsole.WriteErrorLine(
                $"Error: Unknown option '{option}'.{CliConsole.DidYouMean(option, optionAliases)} Run 'lpb {commandName} --help' to see available options.");

        /// <summary>校验 -d/--dir 指向的目录存在（否则打印错误）。返回 false 表示调用方应立即退出。</summary>
        public static bool ValidateInputDirectory(DirectoryInfo? dir)
        {
            if (dir == null || Directory.Exists(dir.FullName)) return true;
            string hint = File.Exists(dir.FullName)
                ? " (this is a file, not a folder)"
                : HasWildcard(dir.FullName) ? " (wildcards are not supported)" : "";
            CliConsole.WriteErrorLine($"Error: Directory not found: {dir.FullName}{hint}");
            return false;
        }

        /// <summary>校验输入文件存在（否则打印错误）。返回 false 表示调用方应立即退出。</summary>
        public static bool ValidateInputFile(FileInfo file)
        {
            if (file.Exists) return true;
            CliConsole.WriteErrorLine($"Error: File not found: {file.FullName}");
            return false;
        }

        /// <summary>校验输出目录不是已存在的文件（否则 Directory.CreateDirectory 会抛 IO 异常）。返回 false 表示调用方应立即退出。</summary>
        public static bool ValidateOutputDirectory(DirectoryInfo? output)
        {
            if (output == null) return true;
            if (HasWildcard(output.FullName))
            {
                CliConsole.WriteErrorWithHint(
                    $"Error: Output path contains wildcards ('*' / '?'), which are not supported: {output.FullName}",
                    "Use an explicit output folder, e.g. -o ./Output");
                return false;
            }
            if (!File.Exists(output.FullName)) return true;
            CliConsole.WriteErrorWithHint(
                $"Error: Output path is a file, not a folder: {output.FullName}",
                "Choose an output folder, or delete/rename that file.");
            return false;
        }

        /// <summary>校验 --parallel 在 1..64（0 或负数会导致并行度设置抛异常，过大值会拖垮机器）。返回 false 表示调用方应立即退出。</summary>
        public static bool ValidateParallel(int parallel)
        {
            if (parallel >= 1 && parallel <= MaxParallel) return true;
            CliConsole.WriteErrorLine($"Error: --parallel must be between 1 and {MaxParallel} (got {parallel}).");
            return false;
        }

        /// <summary>
        /// 解析 --key-timestamp：秒（2.500）、分:秒（1:30.500）、时:分:秒（0:01:30.500）。
        /// 成功时输出微秒。merge 与 split 共用，保证两种命令的输入格式一致。
        /// </summary>
        public static bool TryParseKeyTimestamp(string text, out long microseconds)
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
    }
}
