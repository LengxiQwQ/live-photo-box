using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Cli.Infrastructure
{
    /// <summary>
    /// CLI 手动更新服务：检查 → 询问 → 下载校验 → 便携版替换 / 安装版静默重装。
    /// WinGet 管理的副本不在此列（由 winget 负责），直接拒绝。
    /// </summary>
    internal static class SelfUpdateService
    {
        private static readonly HttpClient _download = new() { Timeout = TimeSpan.FromMinutes(15) };

        private const string TempRootName = "LivePhotoBox_CliUpdate";

        // 主名 + 全部别名：替换前必须等这些进程全部退出
        private static readonly string[] AliasNames =
            { "livephotobox-boot", "livephotobox", "livebox", "lpb", "livephoto" };

        /// <summary>
        /// 执行手动更新流程，返回进程退出码。
        /// </summary>
        /// <param name="yes">true 时跳过确认提示。</param>
        public static async Task<int> RunAsync(bool yes)
        {
            var baseDir = AppContext.BaseDirectory;

            // WinGet 管理的副本由 winget upgrade 负责，内置更新不可用
            if (WingetInstallDetector.IsWingetManaged(baseDir))
            {
                Console.WriteLine("This copy is installed and managed by WinGet.");
                Console.WriteLine("Built-in update is disabled for WinGet-managed installs.");
                Console.WriteLine("Update with: winget upgrade LengxiQwQ.LivePhotoBox");
                return 3;
            }

            LogService.Info("[Update] Checking for updates...", LogSource.System);

            // ── 检查阶段：3 次自动重试；耗尽后（交互模式）再给一次 R 重试机会 ──
            UpdateCheckService.LatestRelease? release;
            for (; ; )
            {
                UpdateCheckService.BeginCheck();
                release = await UpdateCheckService.FetchLatestReleaseAsync(
                    onRetry: UpdateCheckService.WriteCheckRetry);
                if (release is not null) break;

                UpdateCheckService.WriteCheckStatus(
                    $"unreachable ({UpdateCheckService.LastFetchError ?? "network error"})",
                    CliConsole.Error);
                UpdateCheckService.PrintManualDownload();
                LogService.Error(
                    $"[Update] Update check failed: {UpdateCheckService.LastFetchError ?? "network error"}",
                    source: LogSource.System);
                if (yes) return 2;
                if (!PromptRetry()) return 2;
            }
            UpdateCheckService.WriteCheckStatus("OK", CliConsole.Success);

            var current = VersionInfo.GetDisplayVersion();
            if (!UpdateCheckService.TryCompare(current, release.Version, out var cmp))
            {
                CliConsole.WriteLine("Unable to compare versions.", CliConsole.Notice);
                Console.WriteLine($"Latest release: {release.TagName}");
                return 1;
            }

            if (cmp >= 0)
            {
                CliConsole.WriteLine("You are running the latest version.", CliConsole.Notice);
                if (cmp > 0)
                    Console.WriteLine("(This build is newer than the latest stable release.)");
                LogService.Info($"[Update] Already up to date (current v{current}).", LogSource.System);
                return 0;
            }

            var asset = SelectAsset(release, baseDir);
            if (asset is null)
            {
                CliConsole.WriteErrorLine(
                    $"No update package found for this install type in release {release.TagName}.");
                UpdateCheckService.PrintManualDownload();
                return 1;
            }

            CliConsole.Write("A newer version is available: ", CliConsole.Notice);
            CliConsole.WriteLine($"v{current} → v{release.Version}", CliConsole.Highlight);
            CliConsole.WriteField("Install type", DescribeInstallType(baseDir), width: 13);
            CliConsole.WriteField("Package", $"{asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)", width: 13);
            LogService.Info($"[Update] New version available: v{current} → v{release.Version} ({asset.Name}).", LogSource.System);

            if (!yes)
            {
                // 始终显示问题；管道/CI 无输入（EOF）时安全跳过
                Console.Write("Update now? [Y/n] ");
                var answer = Console.ReadLine();
                if (answer is null)
                {
                    Console.WriteLine();
                    Console.WriteLine("(no interactive input detected — update skipped)");
                    Console.WriteLine();
                    Console.Write("To update automatically: ");
                    CliConsole.WriteLine("lpb update -y", CliConsole.CommandPurple);
                    UpdateCheckService.PrintManualDownload();
                    return 0;
                }
                answer = answer.Trim();
                if (answer.Length > 0 &&
                    !answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                    !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Skipped.");
                    UpdateCheckService.PrintManualDownload();
                    return 0;
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), TempRootName, release.Version);
            try
            {
                Directory.CreateDirectory(tempDir);
                var downloadedPath = Path.Combine(tempDir, asset.Name);

                LogService.Info($"[Update] Downloading {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)...", LogSource.System);

                // ── 下载阶段：3 次自动重试 + Range 续传；耗尽后（交互模式）再给 R 机会 ──
                while (!await DownloadAsync(asset, downloadedPath))
                {
                    CliConsole.WriteErrorLine("Download failed or failed integrity check.");
                    UpdateCheckService.PrintManualDownload();
                    LogService.Error("[Update] Download failed.", source: LogSource.System);
                    if (yes) return 1;
                    if (!PromptRetry()) return 1;
                }

                LogService.Info($"[Update] Download complete: {downloadedPath}", LogSource.System);

                // 安装版：静默重装 setup.exe；便携版：替换脚本（交互模式附着当前终端、-y 静默）
                return File.Exists(Path.Combine(baseDir, "unins000.exe"))
                    ? LaunchSetup(downloadedPath)
                    : PreparePortableReplace(downloadedPath, baseDir, tempDir, interactive: !yes,
                        currentVersion: current, newVersion: release.Version);
            }
            catch (Exception ex)
            {
                CliConsole.WriteErrorLine($"Update failed: {ex.Message}");
                return 1;
            }
        }

        // 安装类型展示：纯 CLI / GUI + CLI 便携包 / 安装版
        private static string DescribeInstallType(string baseDir)
        {
            if (File.Exists(Path.Combine(baseDir, "unins000.exe")))
                return "Installer (Inno Setup, GUI + CLI)";
            if (File.Exists(Path.Combine(baseDir, "Live Photo Box.exe")))
                return "Portable bundle (GUI + CLI)";
            return "Portable CLI-only";
        }

        // 按安装类型选择资产：安装版 setup.exe；GUI 便携包 portable.zip；其余 cli.zip
        private static UpdateCheckService.GitHubAsset? SelectAsset(
            UpdateCheckService.LatestRelease release, string baseDir)
        {
            var suffix = File.Exists(Path.Combine(baseDir, "unins000.exe"))
                ? "-setup.exe"
                : File.Exists(Path.Combine(baseDir, "Live Photo Box.exe"))
                    ? "-portable.zip"
                    : "-cli.zip";

            foreach (var a in release.Assets)
            {
                if (a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            foreach (var a in release.Assets)
            {
                if (a.Name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }

        // 下载单个资产：3 次尝试 + HTTP Range 续传 + 整文件 SHA256 校验。
        // 中途断流保留部分文件供下次续传；大小/digest 不符删掉部分文件强制整下。
        private static async Task<bool> DownloadAsync(
            UpdateCheckService.GitHubAsset asset, string destPath, int maxAttempts = 3)
        {
            ConsoleProgress.Begin("Downloading");

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // 上次尝试留下的部分文件（中途断流保留；损坏已删）
                var existing = (attempt > 1 && File.Exists(destPath)) ? new FileInfo(destPath).Length : 0L;

                // 部分文件已等于完整大小（最后一字节后崩溃）：本地验 digest 即可，不再发 HTTP
                if (existing > 0 && asset.Size > 0 && existing == asset.Size)
                {
                    if (VerifyWholeFile(asset, destPath))
                    {
                        ConsoleProgress.Finish("Downloaded");
                        return true;
                    }
                    TryDeletePartial(destPath);
                    existing = 0;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, asset.BrowserDownloadUrl);
                    if (existing > 0)
                        request.Headers.Range = new RangeHeaderValue(existing, null);

                    using var response = await _download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (await EndAttempt(attempt, maxAttempts)) return false;
                        continue;
                    }

                    // Range 被服务器忽略 → 从头重下
                    if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
                        existing = 0;

                    // 206 但 Content-Range 总长与 asset.Size 不符 → 判损坏
                    if (existing > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var total = response.Content.Headers.ContentRange?.Length;
                        if (asset.Size > 0 && total is long t && t != asset.Size)
                        {
                            TryDeletePartial(destPath);
                            if (await EndAttempt(attempt, maxAttempts)) return false;
                            continue;
                        }
                    }

                    var expected = asset.Size > 0
                        ? asset.Size
                        : (response.Content.Headers.ContentLength ?? 0);

                    if (await StreamToFileAsync(asset, response, destPath, existing, expected))
                    {
                        ConsoleProgress.Finish("Downloaded");
                        return true;
                    }
                    // StreamToFileAsync 内部已删损坏部分 / 保留断流部分
                    if (await EndAttempt(attempt, maxAttempts)) return false;
                }
                catch (Exception ex) when (ex is HttpRequestException or HttpIOException
                                            or IOException or TaskCanceledException)
                {
                    // 中途断流：保留部分文件供下次续传
                    if (await EndAttempt(attempt, maxAttempts)) return false;
                }
            }

            return false;
        }

        // 流式写文件：续传时先流式哈希已下载部分，再续写 + 续哈希，整文件 digest 正确。
        // 返回 false 时：大小/digest 不符已删部分文件（下次整下）；中途异常由上层 catch 保留部分文件。
        private static async Task<bool> StreamToFileAsync(
            UpdateCheckService.GitHubAsset asset, HttpResponseMessage response,
            string destPath, long existingSize, long expected)
        {
            var mode = existingSize > 0 ? FileMode.Append : FileMode.Create;
            using var content = await response.Content.ReadAsStreamAsync();
            using var sha = SHA256.Create();

            if (existingSize > 0)
            {
                using var partial = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var seed = new byte[8192];
                int n;
                while ((n = await partial.ReadAsync(seed, 0, seed.Length)) > 0)
                    sha.TransformBlock(seed, 0, n, null, 0);
            }

            using var file = new FileStream(destPath, mode, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true);
            var buffer = new byte[8192];
            long readThisAttempt = 0;
            var sw = Stopwatch.StartNew();
            int read;

            while ((read = await content.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                sha.TransformBlock(buffer, 0, read, null, 0);
                readThisAttempt += read;

                ConsoleProgress.Render(
                    known: expected > 0,
                    done: existingSize + readThisAttempt,
                    total: expected,
                    mibPerSec: readThisAttempt / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1024.0 / 1024.0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            // 末尾强制渲染 100% 帧
            if (expected > 0)
            {
                ConsoleProgress.Render(
                    known: true,
                    done: existingSize + readThisAttempt,
                    total: expected,
                    mibPerSec: readThisAttempt / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1024.0 / 1024.0,
                    force: true);
            }

            if (expected > 0 && existingSize + readThisAttempt != expected)
            {
                TryDeletePartial(destPath);
                return false;
            }

            // GitHub 资产 digest 形如 "sha256:..."，存在则强校验
            if (!string.IsNullOrEmpty(asset.Digest) &&
                asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var expectedHash = asset.Digest.Substring(7).Trim();
                var actualHash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeletePartial(destPath);
                    return false;
                }
            }

            return true;
        }

        // 本地整文件 SHA256 校验（供「部分文件已 == 完整大小」场景复用）
        private static bool VerifyWholeFile(UpdateCheckService.GitHubAsset asset, string path)
        {
            if (string.IsNullOrEmpty(asset.Digest) ||
                !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return true; // 无 digest 可验，视为通过

            var expectedHash = asset.Digest.Substring(7).Trim();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var buffer = new byte[8192];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, n, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            var actualHash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeletePartial(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort */ }
        }

        // 一次尝试失败后的收尾：还有机会则提示重试 + 退避；否则清条返回 true（表示已耗尽）
        private static async Task<bool> EndAttempt(int attempt, int maxAttempts)
        {
            if (attempt < maxAttempts)
            {
                ConsoleProgress.ShowRetryMessage($"Download failed, retrying {attempt + 1}/{maxAttempts} ...");
                await Task.Delay(attempt * 1000); // 1s 后 2s
                return false;
            }
            ConsoleProgress.ClearBar();
            return true;
        }

        // 重试耗尽后的交互选择：R 重试 / 回车退出；管道 EOF 按退出处理
        private static bool PromptRetry()
        {
            Console.Write("Press R to retry, Enter to exit. ");
            var answer = Console.ReadLine();
            if (answer is null)
            {
                Console.WriteLine();
                return false;
            }
            return answer.Trim().Equals("r", StringComparison.OrdinalIgnoreCase);
        }

        // 安装版：静默运行新版 setup.exe（与 GUI 更新同模式）
        private static int LaunchSetup(string setupPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{setupPath}\" /SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
            CliConsole.WriteLine("Installer launched in silent mode.", CliConsole.Notice);
            CliConsole.WriteLine("It will close running applications and install the new version.", CliConsole.Notice);
            return 0;
        }

        // 便携版：解压 zip → 生成等待替换脚本 → 执行（交互模式附着当前终端，-y 静默无窗口）
        private static int PreparePortableReplace(string zipPath, string appDir, string tempDir, bool interactive,
            string currentVersion, string newVersion)
        {
            var extractDir = Path.Combine(tempDir, "extracted");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // AppContext.BaseDirectory 以 \ 结尾，去掉尾部分隔符保持路径规范
            appDir = Path.TrimEndingDirectorySeparator(appDir);

            // 生成 PowerShell 脚本，用 UTF-16LE + Base64 通过 -EncodedCommand 传给 powershell.exe。
            // 脚本内容全程 Unicode，不经过系统 ANSI 代码页 / BOM / 命令行转义，
            // 中文与跨语言路径都不会乱码，且不落盘（无 update.cmd 临时文件）。
            var logPath = LogService.CurrentLogPath ?? string.Empty;
            var script = BuildUpdateScript(appDir, extractDir, logPath, interactive, currentVersion, newVersion);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            // 替换脚本是独立进程，会等所有 livephotobox 进程退出后才动手。
            // 先把「已启动」这条日志 flush 落盘，避免它晚于脚本追加的替换结果写入。
            LogService.Info("[Update] Replacement script launched (portable).", LogSource.System);
            LogService.ForceFlush();

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = !interactive,
                WindowStyle = interactive ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
            });

            if (interactive)
            {
                CliConsole.WriteLine("Updating...", CliConsole.Notice);
                CliConsole.WriteLine("The update will apply in this window once all livephotobox processes exit.", CliConsole.Notice);
                // "Verify with" 不在此打印：更新还没完成，提示过早且会诱导用户立刻验证
                //（替换脚本要等所有 lpb 进程退出，提前运行 lpb --version 反而拖慢替换）。
                // 改由替换脚本在成功后打印（见 BuildUpdateScript 的 interactive 分支）。
            }
            else
            {
                CliConsole.WriteLine("Update started.", CliConsole.Notice);
                CliConsole.WriteFieldRgb("Verify with", "lpb --version", width: 13, valueColor: CliConsole.CommandPurple);
            }
            return 0;
        }

        // 替换脚本：等待全部别名进程退出 → Copy-Item 覆盖 → 追加结果到日志 → 清理临时目录。
        // 路径用 PowerShell 单引号字符串承载（内部单引号转义成 ''），全程无 ANSI 代码页转换。
        // interactive=true 时脚本末尾复用 lpb 所在终端打印结果：
        // 成功给验证命令后直接退出；失败 Read-Host 停留让用户看清错误。
        private static string BuildUpdateScript(string appDir, string extractDir, string logPath, bool interactive,
            string currentVersion, string newVersion)
        {
            static string PS(string s) => "'" + s.Replace("'", "''") + "'";

            var app = PS(appDir);
            var extract = PS(extractDir);
            var log = PS(logPath);
            var names = "@(" + string.Join(",", Array.ConvertAll(AliasNames, n => PS(n))) + ")";

            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine($"$appDir = {app}");
            sb.AppendLine($"$extractDir = {extract}");
            sb.AppendLine($"$logPath = {log}");
            sb.AppendLine($"$names = {names}");
            sb.AppendLine("$ok = $false");
            sb.AppendLine("$message = ''");
            sb.AppendLine("try {");
            sb.AppendLine("  while ($true) {");
            sb.AppendLine("    $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)");
            sb.AppendLine("    if ($running.Count -eq 0) { break }");
            sb.AppendLine("    Start-Sleep -Seconds 2");
            sb.AppendLine("  }");
            sb.AppendLine("  Get-ChildItem -LiteralPath $appDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {");
            sb.AppendLine("    $_.Attributes = $_.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly");
            sb.AppendLine("  }");
            sb.AppendLine("  Get-ChildItem -LiteralPath $extractDir -Force | ForEach-Object {");
            sb.AppendLine("    Copy-Item -LiteralPath $_.FullName -Destination $appDir -Recurse -Force");
            sb.AppendLine("  }");
            sb.AppendLine("  Add-Content -LiteralPath $logPath -Value 'replacement OK'");
            sb.AppendLine("  $ok = $true");
            sb.AppendLine($"  $message = 'Update complete! v{currentVersion} → v{newVersion}'");
            sb.AppendLine("}");
            sb.AppendLine("catch {");
            sb.AppendLine("  $message = 'Update FAILED: ' + $_.Exception.Message");
            sb.AppendLine("  Add-Content -LiteralPath $logPath -Value ('replacement FAILED: ' + $_.Exception.Message)");
            sb.AppendLine("}");
            sb.AppendLine("finally {");
            sb.AppendLine("  Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("}");
            if (interactive)
            {
                // 成功：替换完成后直接给出验证命令并退出，不再停留等回车（多此一举）；
                // 失败：停留让用户看清错误，避免一闪而过。
                // 结果消息按 CLI 配色：成功绿（CliConsole.Success）、失败红（CliConsole.Error）；
                // "lpb --version" 用与 CLI 一致的 CommandPurple (180,140,255) ANSI 上色，
                // UseColor 为假（NO_COLOR / 重定向）时退化为纯文本。
                var verifySuffix = CliConsole.UseColor
                    ? "\x1b[38;2;180;140;255mlpb --version\x1b[0m"
                    : "lpb --version";
                sb.AppendLine("if ($ok) {");
                sb.AppendLine("  Write-Host $message -ForegroundColor Green");
                sb.AppendLine($"  Write-Host 'Verify with  : {verifySuffix}'");
                sb.AppendLine("} else {");
                sb.AppendLine("  Write-Host $message -ForegroundColor Red");
                sb.AppendLine("  Read-Host 'Press Enter to close'");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }
    }
}
