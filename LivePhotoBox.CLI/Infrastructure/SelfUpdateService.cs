using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

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
            { "livephotobox", "livebox", "lipbox", "lpb", "lpbx", "livephoto" };

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

            var release = await UpdateCheckService.FetchLatestReleaseAsync();
            if (release is null)
            {
                CliConsole.WriteErrorLine(
                    $"Update check failed: {UpdateCheckService.LastFetchError ?? "network error"}");
                Console.WriteLine($"Visit {UpdateCheckService.ReleasesPageUrl} to check manually.");
                return 2;
            }

            var current = VersionInfo.GetDisplayVersion();
            if (!UpdateCheckService.TryCompare(current, release.Version, out var cmp))
            {
                CliConsole.WriteLine("Unable to compare versions.", CliConsole.Notice);
                Console.WriteLine($"Latest release: {release.TagName}");
                Console.WriteLine(UpdateCheckService.ReleasesPageUrl);
                return 0;
            }

            if (cmp >= 0)
            {
                CliConsole.WriteLine("You are running the latest version.", CliConsole.Notice);
                if (cmp > 0)
                    Console.WriteLine("(This build is newer than the latest stable release.)");
                return 0;
            }

            var asset = SelectAsset(release, baseDir);
            if (asset is null)
            {
                CliConsole.WriteErrorLine(
                    $"No update package found for this install type in release {release.TagName}.");
                Console.WriteLine($"Visit {UpdateCheckService.ReleasesPageUrl} to download manually.");
                return 1;
            }

            // 始终展示版本与手动下载链接
            CliConsole.Write("A newer version is available: ", CliConsole.Notice);
            CliConsole.WriteLine($"v{current} → v{release.Version}", CliConsole.Highlight);
            Console.WriteLine($"Install type: {DescribeInstallType(baseDir)}");
            Console.WriteLine($"Download: {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)");
            Console.WriteLine(UpdateCheckService.ReleasesPageUrl);
            Console.WriteLine();

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
                    Console.WriteLine("To update automatically:");
                    CliConsole.WriteLine("lpb update -y", CliConsole.CommandPurple);
                    Console.WriteLine("or download manually from the link above.");
                    return 0;
                }
                answer = answer.Trim();
                if (answer.Length > 0 &&
                    !answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
                    !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Skipped. Download manually from the link above.");
                    return 0;
                }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), TempRootName, release.Version);
            try
            {
                Directory.CreateDirectory(tempDir);
                var downloadedPath = Path.Combine(tempDir, asset.Name);

                Console.WriteLine($"Downloading {asset.Name} ...");
                if (!await DownloadAsync(asset, downloadedPath))
                {
                    CliConsole.WriteErrorLine(
                        "Download failed or failed integrity check. Please download manually from the link above.");
                    return 1;
                }

                // 安装版：静默重装 setup.exe；便携版：后台替换
                return File.Exists(Path.Combine(baseDir, "unins000.exe"))
                    ? LaunchSetup(downloadedPath)
                    : PreparePortableReplace(downloadedPath, baseDir, tempDir);
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

        // 流式下载 + 进度 + 大小/SHA256 校验（GitHub API 提供 digest）
        private static async Task<bool> DownloadAsync(
            UpdateCheckService.GitHubAsset asset, string destPath)
        {
            using var response = await _download.GetAsync(
                asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return false;

            var expected = response.Content.Headers.ContentLength ?? asset.Size;
            using var content = await response.Content.ReadAsStreamAsync();
            using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 8192, useAsync: true);
            using var sha = SHA256.Create();

            var buffer = new byte[8192];
            long total = 0;
            int lastPercent = -1;
            int read;
            while ((read = await content.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                sha.TransformBlock(buffer, 0, read, null, 0);
                total += read;

                if (expected > 0)
                {
                    var percent = (int)(total * 100 / expected);
                    if (percent >= lastPercent + 10)
                    {
                        lastPercent = percent;
                        Console.Write($"\r  {percent}%   ");
                    }
                }
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (lastPercent >= 0)
                Console.Write("\r           \r");

            if (expected > 0 && total != expected)
                return false;

            // GitHub 资产 digest 形如 "sha256:..."，存在则强校验
            if (!string.IsNullOrEmpty(asset.Digest) &&
                asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var expectedHash = asset.Digest.Substring(7).Trim();
                var actualHash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
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
            Console.WriteLine("Installer launched in silent mode.");
            Console.WriteLine("It will close running applications and install the new version.");
            return 0;
        }

        // 便携版：解压 zip → 生成等待替换脚本 → 后台执行
        private static int PreparePortableReplace(string zipPath, string appDir, string tempDir)
        {
            var extractDir = Path.Combine(tempDir, "extracted");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // AppContext.BaseDirectory 以 \ 结尾，写入 bat 时必须去掉，
            // 否则 set "VAR=...\" 会把结尾引号吞掉，导致 robocopy 目标路径损坏
            appDir = Path.TrimEndingDirectorySeparator(appDir);

            var resultFile = Path.Combine(tempDir, "update_result.txt");
            var batPath = Path.Combine(tempDir, "update.cmd");
            // 无 BOM：cmd.exe 对带 BOM 的 .bat 可能把首行识别错误
            File.WriteAllText(batPath, BuildUpdateBat(appDir, extractDir, resultFile),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{batPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Console.WriteLine("Update started in the background.");
            Console.WriteLine("Files will be replaced once all livephotobox processes exit.");
            Console.WriteLine($"Result file: {resultFile}");
            Console.WriteLine("Verify with: lpb --version");
            return 0;
        }

        // 替换脚本：等待全部别名退出 → robocopy 覆盖 → 写结果 → 清理临时目录
        private static string BuildUpdateBat(string appDir, string extractDir, string resultFile)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("title Live Photo Box CLI Update");
            sb.AppendLine($"set \"APP_DIR={appDir}\"");
            sb.AppendLine($"set \"EXTRACT_DIR={extractDir}\"");
            sb.AppendLine($"set \"RESULT_FILE={resultFile}\"");
            sb.AppendLine("echo updating> \"%RESULT_FILE%\"");
            sb.AppendLine(":wait");
            sb.AppendLine("set \"RUNNING=\"");
            sb.AppendLine($"for %%A in ({string.Join(" ", AliasNames)}) do (");
            sb.AppendLine("  tasklist /FI \"IMAGENAME eq %%A.exe\" 2>NUL | find /I \"%%A.exe\" >NUL 2>&1 && set \"RUNNING=1\"");
            sb.AppendLine(")");
            sb.AppendLine("if defined RUNNING (timeout /T 2 /NOBREAK >NUL & goto wait)");
            sb.AppendLine("robocopy \"%EXTRACT_DIR%\" \"%APP_DIR%\" /E /IS /NFL /NDL /NJH /NJS /R:3 /W:2");
            sb.AppendLine("if %ERRORLEVEL% LSS 8 (echo OK> \"%RESULT_FILE%\") else (echo FAILED %ERRORLEVEL%> \"%RESULT_FILE%\")");
            sb.AppendLine($"rmdir /S /Q \"{extractDir}\"");
            sb.AppendLine("exit /b");
            return sb.ToString();
        }
    }
}
