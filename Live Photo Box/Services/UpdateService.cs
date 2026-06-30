/*
 * UpdateService.cs
 *
 * 应用自动更新服务。负责：
 *   - 检测当前运行模式（MSIX 打包 vs 非打包）
 *   - 按 3 天间隔自动检查 GitHub Releases 中的新版本
 *   - 下载新版本资产（setup.exe 或 portable.zip）
 *   - 启动安装程序（Inno Setup 静默安装 或 便携版 .bat 替换脚本）
 *
 * 仅在非打包模式（unpackaged）下生效。MSIX 打包由 Windows Store 负责更新。
 *
 * 对应 API：GET https://api.github.com/repos/LengxiQwQ/live-photo-box/releases/latest
 * 无需 Token（公开仓库），免登录。
 *
 * 日志：所有关键路径均有 LogService 日志输出，便于排查网络/API/下载/安装等环节问题。
 */

using LivePhotoBox.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 应用自动更新服务（静态类）。
    /// 提供从 GitHub Releases 检测、下载到安装的完整流程。
    /// </summary>
    public static class UpdateService
    {
        // ── 常量 ──────────────────────────────────────────────────────
        private const string GitHubApiUrl = "https://api.github.com/repos/LengxiQwQ/live-photo-box/releases/latest";
        private const string LastCheckKey = "UpdateLastCheckTime";
        private const string SkippedVersionKey = "UpdateSkippedVersion";
        private const int CheckIntervalDays = 3;

        // ── 静态字段 ──────────────────────────────────────────────────
        private static readonly HttpClient _httpClient;

        /// <summary>是否为 MSIX 打包模式（打包模式下不启用自动更新）。</summary>
        public static bool IsPackagedMode { get; }

        /// <summary>是否启用自动更新（仅非打包模式）。</summary>
        public static bool IsUpdateEnabled => !IsPackagedMode;

        // ── 静态构造函数 ──────────────────────────────────────────────

        static UpdateService()
        {
            // 检测打包模式：与 App.AppVersion 使用相同的方式
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                IsPackagedMode = true;
                LogService.Info("UpdateService: Running in PACKAGED mode (MSIX). Auto-update DISABLED.", LogSource.System);
            }
            catch
            {
                IsPackagedMode = false;
                LogService.Info("UpdateService: Running in UNPACKAGED mode. Auto-update ENABLED.", LogSource.System);
            }

            // 初始化 HTTP 客户端（GitHub API 必须设置 User-Agent）
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LivePhotoBox-Update/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            LogService.Debug($"UpdateService initialized. API URL: {GitHubApiUrl}, Check interval: {CheckIntervalDays} days",
                LogSource.System);
        }

        // ── 安装类型检测 ──────────────────────────────────────────────

        /// <summary>
        /// 检测当前是否为 Inno Setup 安装版（通过查找同目录下的 unins000.exe）。
        /// 便携版不会包含此文件。
        /// </summary>
        public static bool IsInnoSetupInstall()
        {
            try
            {
                bool result = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
                LogService.Debug($"UpdateService: Install type detection → {(result ? "Inno Setup" : "Portable")}" +
                    $" (base dir: {AppContext.BaseDirectory})", LogSource.System);
                return result;
            }
            catch (Exception ex)
            {
                LogService.Warn($"UpdateService: Failed to detect install type: {ex.Message}", source: LogSource.System);
                return false;
            }
        }

        // ── 检查间隔管理 ──────────────────────────────────────────────

        /// <summary>
        /// 判断是否应执行更新检查。条件：非打包模式 + 距上次检查 >= 3 天。
        /// 首次安装后立即检查（无上次检查记录）。
        /// </summary>
        public static bool ShouldCheckForUpdate()
        {
            if (!IsUpdateEnabled)
            {
                LogService.Debug("UpdateService: ShouldCheck → false (packaged mode)", LogSource.System);
                return false;
            }

            var lastCheckStr = AppSettingsService.GetValue(LastCheckKey, "");
            if (string.IsNullOrEmpty(lastCheckStr))
            {
                LogService.Info("UpdateService: ShouldCheck → true (first check ever, no previous record)", LogSource.System);
                return true;
            }

            if (DateTime.TryParse(lastCheckStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var lastCheck))
            {
                var daysSince = (DateTime.Now - lastCheck).TotalDays;
                bool shouldCheck = daysSince >= CheckIntervalDays;
                LogService.Info(
                    $"UpdateService: ShouldCheck → {(shouldCheck ? "true" : "false")} " +
                    $"(last: {lastCheck:yyyy-MM-dd HH:mm}, {daysSince:F1} days ago, interval: {CheckIntervalDays}d)",
                    LogSource.System);
                return shouldCheck;
            }

            LogService.Warn($"UpdateService: ShouldCheck → true (failed to parse last check time '{lastCheckStr}')",
                source: LogSource.System);
            return true;
        }

        /// <summary>
        /// 记录本次检查时间（无论有没有发现新版本都记录）。
        /// </summary>
        public static void RecordCheckTime()
        {
            var now = DateTime.Now;
            AppSettingsService.SetValue(LastCheckKey, now.ToString("o"));
            LogService.Debug($"UpdateService: Check time recorded → {now:yyyy-MM-dd HH:mm:ss}", LogSource.System);
        }

        // ── 跳过版本管理 ──────────────────────────────────────────────

        /// <summary>
        /// 检查指定版本是否已被用户标记为"忽略"。
        /// </summary>
        public static bool IsVersionSkipped(string tagName)
        {
            var skipped = AppSettingsService.GetValue(SkippedVersionKey, "");
            bool isSkipped = string.Equals(skipped, tagName, StringComparison.OrdinalIgnoreCase);
            if (isSkipped)
                LogService.Info($"UpdateService: Version '{tagName}' was previously skipped by user.", LogSource.System);
            return isSkipped;
        }

        /// <summary>
        /// 将指定版本标记为"忽略"，本次会话及以后都不会再提示该版本。
        /// </summary>
        public static void SkipVersion(string tagName)
        {
            AppSettingsService.SetValue(SkippedVersionKey, tagName);
            LogService.Info($"UpdateService: User skipped version '{tagName}'. Will not prompt again.", LogSource.System);
        }

        /// <summary>
        /// 清除被忽略的版本记录。手动检查时调用，确保用户能看到所有可用更新。
        /// </summary>
        public static void ClearSkippedVersion()
        {
            var was = AppSettingsService.GetValue(SkippedVersionKey, "");
            AppSettingsService.SetValue(SkippedVersionKey, "");
            if (!string.IsNullOrEmpty(was))
                LogService.Info($"UpdateService: Cleared skipped version '{was}'.", LogSource.System);
        }

        // ── GitHub API 请求 ──────────────────────────────────────────

        /// <summary>
        /// 从 GitHub API 获取最新 Release 信息。
        /// 网络不可用或 API 异常时返回 null，并输出详细日志以便排查。
        /// </summary>
        public static async Task<GitHubReleaseResponse?> FetchLatestReleaseAsync()
        {
            LogService.Info($"UpdateService: Fetching latest release from {GitHubApiUrl}...", LogSource.System);

            try
            {
                var response = await _httpClient.GetAsync(GitHubApiUrl);
                var statusCode = response.StatusCode;

                LogService.Debug($"UpdateService: GitHub API response → {(int)statusCode} {statusCode}", LogSource.System);

                if (!response.IsSuccessStatusCode)
                {
                    // 读取 GitHub 返回的错误详情（如果有）
                    string? errorBody = null;
                    try { errorBody = await response.Content.ReadAsStringAsync(); }
                    catch { /* 读取失败就算了 */ }

                    LogService.Error(
                        $"UpdateService: GitHub API returned {(int)statusCode} {statusCode}. " +
                        $"URL: {GitHubApiUrl}",
                        exception: null,
                        source: LogSource.System);

                    if (!string.IsNullOrWhiteSpace(errorBody))
                    {
                        // 截断过长响应，保护日志文件大小
                        var truncated = errorBody.Length > 500 ? errorBody.Substring(0, 500) + "..." : errorBody;
                        LogService.Warn($"UpdateService: GitHub API error body → {truncated}", source: LogSource.System);
                    }

                    // 对常见错误码给出具体原因
                    switch (statusCode)
                    {
                        case HttpStatusCode.Forbidden:
                        case (HttpStatusCode)429: // Too Many Requests
                            LogService.Error(
                                "UpdateService: GitHub API rate limit likely exceeded (60 req/hour for unauthenticated). " +
                                "Wait and retry later.", source: LogSource.System);
                            break;
                        case HttpStatusCode.NotFound:
                            LogService.Error(
                                "UpdateService: GitHub release not found. Check repository name and tag format.",
                                source: LogSource.System);
                            break;
                    }

                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(json);

                if (release == null)
                {
                    LogService.Error("UpdateService: Failed to deserialize GitHub API JSON response.",
                        source: LogSource.System);
                    return null;
                }

                LogService.Info(
                    $"UpdateService: Latest release → tag={release.TagName}, " +
                    $"name={release.Name}, assets={release.Assets?.Count ?? 0}, " +
                    $"prerelease={release.Prerelease}",
                    LogSource.System);

                return release;
            }
            catch (HttpRequestException ex)
            {
                // 网络层错误：DNS 解析失败、连接拒绝、TLS 握手失败、代理问题等
                LogService.Error(
                    $"UpdateService: HTTP request failed. " +
                    $"This usually means network/DNS/proxy issues. " +
                    $"URL: {GitHubApiUrl}, Error: {ex.GetType().Name}: {ex.Message}",
                    exception: ex,
                    source: LogSource.System);

                if (ex.InnerException != null)
                {
                    LogService.Warn(
                        $"UpdateService: Inner exception → {ex.InnerException.GetType().Name}: {ex.InnerException.Message}",
                        source: LogSource.System);
                }

                // 输出 HttpRequestException 的 StatusCode（如果有的话）
                if (ex.StatusCode.HasValue)
                {
                    LogService.Warn($"UpdateService: HTTP status code from exception → {ex.StatusCode}",
                        source: LogSource.System);
                }

                return null;
            }
            catch (TaskCanceledException ex)
            {
                // 超时（_httpClient.Timeout = 30s）
                LogService.Error(
                    $"UpdateService: Request TIMED OUT after {_httpClient.Timeout.TotalSeconds:F0}s. " +
                    $"Check network connectivity to api.github.com.",
                    exception: ex,
                    source: LogSource.System);
                return null;
            }
            catch (JsonException ex)
            {
                LogService.Error(
                    $"UpdateService: Failed to parse GitHub API JSON response. " +
                    $"The API response format may have changed.",
                    exception: ex,
                    source: LogSource.System);
                return null;
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Unexpected error fetching release. Type={ex.GetType().Name}",
                    exception: ex,
                    source: LogSource.System);
                return null;
            }
        }

        // ── 版本比较 ──────────────────────────────────────────────────

        /// <summary>
        /// 比较当前应用版本与 GitHub Release 的 tag 版本。
        /// 返回 true 表示有新版本可用。
        /// </summary>
        public static bool IsNewerVersion(GitHubReleaseResponse release)
        {
            if (release == null)
            {
                LogService.Debug("UpdateService: IsNewerVersion → false (release is null)", LogSource.System);
                return false;
            }

            // 去掉 tag 前缀 'v'，如 "v1.14.11" → "1.14.11"
            var tagVersion = release.TagName?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tagVersion, out var latestVersion))
            {
                LogService.Warn(
                    $"UpdateService: IsNewerVersion → false (cannot parse tag version '{release.TagName}')",
                    source: LogSource.System);
                return false;
            }

            var currentVersionStr = App.AppVersion;
            if (!Version.TryParse(currentVersionStr, out var currentVersion))
            {
                LogService.Warn(
                    $"UpdateService: IsNewerVersion → false (cannot parse current version '{currentVersionStr}')",
                    source: LogSource.System);
                return false;
            }

            bool isNewer = latestVersion > currentVersion;
            LogService.Info(
                $"UpdateService: Version comparison → current={currentVersion}, latest={latestVersion}, " +
                $"isNewer={isNewer}",
                LogSource.System);
            return isNewer;
        }

        // ── 资产选择 ──────────────────────────────────────────────────

        /// <summary>
        /// 根据安装类型选择合适的下载资产。
        /// Inno Setup 安装版选 -setup.exe，便携版选 -portable.zip。
        /// 匹配不到时返回 null。
        /// </summary>
        private static GitHubAsset? SelectAsset(GitHubReleaseResponse release)
        {
            bool isSetup = IsInnoSetupInstall();
            string targetType = isSetup ? "setup.exe" : "portable.zip";

            LogService.Debug(
                $"UpdateService: Selecting asset for {(isSetup ? "Inno Setup" : "Portable")} install... " +
                $"Available assets: [{string.Join(", ", release.Assets.ConvertAll(a => a?.Name ?? "null"))}]",
                LogSource.System);

            // 精确匹配：文件名以 -setup.exe 或 -portable.zip 结尾
            foreach (var asset in release.Assets)
            {
                if (asset?.Name == null)
                    continue;

                if (isSetup && asset.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Info(
                        $"UpdateService: Selected asset → {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)",
                        LogSource.System);
                    return asset;
                }

                if (!isSetup && asset.Name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Info(
                        $"UpdateService: Selected asset → {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB)",
                        LogSource.System);
                    return asset;
                }
            }

            // Fallback：包含对应后缀即可
            foreach (var asset in release.Assets)
            {
                if (asset?.Name == null)
                    continue;

                string targetSuffix = isSetup ? "setup.exe" : "portable.zip";
                if (asset.Name.Contains(targetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warn(
                        $"UpdateService: Fallback asset selected → {asset.Name} (no exact suffix match)",
                        source: LogSource.System);
                    return asset;
                }
            }

            LogService.Error(
                $"UpdateService: No {targetType} asset found in release {release.TagName}. " +
                $"Available: [{string.Join(", ", release.Assets.ConvertAll(a => a?.Name ?? "null"))}]",
                source: LogSource.System);
            return null;
        }

        // ── 下载 ──────────────────────────────────────────────────────

        /// <summary>
        /// 下载选定的 GitHub Release 资产到临时目录，支持进度报告和取消。
        /// 返回值：下载完成后的文件路径；失败或取消时返回 null。
        /// </summary>
        /// <param name="release">Release 信息（用于选择资产）</param>
        /// <param name="progress">下载进度报告器（0-100）</param>
        /// <param name="ct">取消令牌</param>
        public static async Task<string?> DownloadAssetAsync(
            GitHubReleaseResponse release,
            IProgress<double> progress,
            CancellationToken ct)
        {
            var asset = SelectAsset(release);
            if (asset == null)
            {
                LogService.Error("UpdateService: Download aborted — no matching asset to download.",
                    source: LogSource.System);
                return null;
            }

            try
            {
                // 准备临时目录
                string tempDir = Path.Combine(Path.GetTempPath(), "LivePhotoBox_Update");
                Directory.CreateDirectory(tempDir);

                string destPath = Path.Combine(tempDir, asset.Name);
                LogService.Info(
                    $"UpdateService: Downloading {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB) " +
                    $"from {asset.BrowserDownloadUrl} → {destPath}",
                    LogSource.System);

                // 发送请求（仅读取 Header，不立即下载 Body）
                using var response = await _httpClient.GetAsync(
                    asset.BrowserDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                LogService.Debug(
                    $"UpdateService: Download started. Content-Length = {(totalBytes > 0 ? $"{totalBytes / 1024.0 / 1024.0:F1} MB" : "unknown")}",
                    LogSource.System);

                // 检查磁盘空间（仅当能获取到文件大小时）
                if (totalBytes > 0)
                {
                    try
                    {
                        var driveInfo = new DriveInfo(Path.GetPathRoot(tempDir)!);
                        var availableMb = driveInfo.AvailableFreeSpace / 1024.0 / 1024.0;
                        var neededMb = totalBytes / 1024.0 / 1024.0;
                        if (driveInfo.AvailableFreeSpace < totalBytes + 50 * 1024 * 1024) // 额外 50MB 余量
                        {
                            LogService.Error(
                                $"UpdateService: Insufficient disk space. " +
                                $"Needed: ~{neededMb + 50:F0} MB, Available: {availableMb:F0} MB",
                                source: LogSource.System);
                            return null;
                        }
                        LogService.Debug($"UpdateService: Disk space OK — available {availableMb:F0} MB, needed ~{neededMb + 50:F0} MB",
                            LogSource.System);
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"UpdateService: Disk space check failed (non-fatal): {ex.Message}",
                            source: LogSource.System);
                    }
                }

                // 流式下载，逐块报告进度
                using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 8192, useAsync: true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;
                int lastReportedPercent = -1;

                var sw = Stopwatch.StartNew();

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        int percent = (int)((double)totalRead / totalBytes * 100.0);
                        // 每 10% 记录一次日志，避免日志洪泛
                        if (percent >= lastReportedPercent + 10)
                        {
                            lastReportedPercent = percent;
                            var elapsed = sw.Elapsed;
                            var speed = totalRead / (elapsed.TotalSeconds > 0 ? elapsed.TotalSeconds : 0.001) / 1024.0 / 1024.0;
                            LogService.Debug(
                                $"UpdateService: Download progress → {percent}% " +
                                $"({totalRead / 1024.0 / 1024.0:F1}/{totalBytes / 1024.0 / 1024.0:F1} MB, {speed:F1} MB/s)",
                                LogSource.System);
                        }
                        progress.Report((double)totalRead / totalBytes * 100.0);
                    }
                }

                await fileStream.FlushAsync(ct);

                // 验证下载完整性（大小对比）
                var actualSize = new FileInfo(destPath).Length;
                if (totalBytes > 0 && actualSize != totalBytes)
                {
                    LogService.Error(
                        $"UpdateService: Download size mismatch! Expected {totalBytes}, got {actualSize}. File may be corrupted.",
                        source: LogSource.System);
                    return null;
                }

                LogService.Info(
                    $"UpdateService: Download complete → {actualSize / 1024.0 / 1024.0:F1} MB " +
                    $"in {sw.Elapsed.TotalSeconds:F1}s ({actualSize / (sw.Elapsed.TotalSeconds > 0 ? sw.Elapsed.TotalSeconds : 0.001) / 1024.0 / 1024.0:F1} MB/s)",
                    LogSource.System);

                return destPath;
            }
            catch (OperationCanceledException)
            {
                LogService.Info("UpdateService: Download cancelled by user or timeout.", LogSource.System);
                return null;
            }
            catch (HttpRequestException ex)
            {
                LogService.Error(
                    $"UpdateService: Download HTTP error → {ex.GetType().Name}: {ex.Message}",
                    exception: ex,
                    source: LogSource.System);
                return null;
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Download failed → {ex.GetType().Name}: {ex.Message}",
                    exception: ex,
                    source: LogSource.System);
                return null;
            }
        }

        // ── 安装启动 ──────────────────────────────────────────────────

        /// <summary>
        /// 启动更新安装程序。根据安装类型自动选择：
        ///   - Inno Setup 安装版 → 静默运行 setup.exe（/VERYSILENT）
        ///   - 便携版 → 解压 zip，创建 update.bat 等待主进程退出后替换文件
        ///
        /// 调用后应立即退出应用（Application.Current.Exit()）。
        /// </summary>
        /// <param name="downloadedPath">下载完成的文件路径</param>
        /// <param name="isSetup">是否为 Inno Setup 安装版</param>
        public static void LaunchInstaller(string downloadedPath, bool isSetup)
        {
            LogService.Info(
                $"UpdateService: Launching {(isSetup ? "Inno Setup installer" : "portable updater")}... " +
                $"File: {downloadedPath} ({new FileInfo(downloadedPath).Length / 1024.0 / 1024.0:F1} MB)",
                LogSource.System);

            if (isSetup)
            {
                LaunchSetupInstaller(downloadedPath);
            }
            else
            {
                LaunchPortableUpdater(downloadedPath);
            }
        }

        /// <summary>
        /// 启动 Inno Setup 安装包。参数：
        ///   /VERYSILENT — 完全静默，不显示任何窗口
        ///   /SUPPRESSMSGBOXES — 抑制消息框
        ///   /CLOSEAPPLICATIONS — 自动关闭正在运行的应用再安装
        ///   /NORESTART — 不自动重启系统
        /// </summary>
        private static void LaunchSetupInstaller(string setupPath)
        {
            var args = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART";
            LogService.Info(
                $"UpdateService: Starting Inno Setup → \"{setupPath}\" {args}",
                LogSource.System);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = args,
                    UseShellExecute = true
                });
                LogService.Info("UpdateService: Inno Setup installer launched successfully.", LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Failed to launch Inno Setup installer!",
                    exception: ex,
                    source: LogSource.System);
            }
        }

        /// <summary>
        /// 启动便携版更新流程：
        /// 1. 将下载的 zip 解压到临时目录
        /// 2. 生成 update.bat（等待主进程退出 → robocopy 替换文件 → 重启应用）
        /// 3. 启动 update.bat
        /// </summary>
        private static void LaunchPortableUpdater(string zipPath)
        {
            string tempDir = Path.GetDirectoryName(zipPath)!;
            string extractDir = Path.Combine(tempDir, "extracted");

            // 清理上次的残留（如果有的话）
            if (Directory.Exists(extractDir))
            {
                try
                {
                    Directory.Delete(extractDir, true);
                    LogService.Debug($"UpdateService: Cleaned up stale extract dir: {extractDir}", LogSource.System);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"UpdateService: Failed to clean stale extract dir: {ex.Message}",
                        source: LogSource.System);
                }
            }

            // 解压新版文件
            try
            {
                LogService.Info($"UpdateService: Extracting {zipPath} → {extractDir}...", LogSource.System);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                LogService.Info("UpdateService: Extraction complete.", LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Failed to extract zip for portable update!",
                    exception: ex,
                    source: LogSource.System);
                return;
            }

            string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string batPath = Path.Combine(tempDir, "update.bat");

            // 生成更新脚本。流程：
            //   a) 等待 Live Photo Box.exe 进程退出
            //   b) 用 robocopy 将新版文件覆盖到应用目录
            //   c) 启动新版本
            //   d) 清理临时文件
            string batContent = "@echo off\r\n" +
                "title Live Photo Box Update\r\n" +
                "echo ============================================\r\n" +
                "echo   Live Photo Box - Updating...\r\n" +
                "echo ============================================\r\n" +
                "echo.\r\n" +
                "echo Waiting for Live Photo Box to close...\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"IMAGENAME eq Live Photo Box.exe\" 2>NUL | find /I \"Live Photo Box.exe\" >NUL 2>&1\r\n" +
                "if \"%ERRORLEVEL%\"==\"0\" (\r\n" +
                "    timeout /T 2 /NOBREAK >NUL\r\n" +
                "    goto wait\r\n" +
                ")\r\n" +
                "echo.\r\n" +
                "echo Installing new version...\r\n" +
                $"robocopy \"{extractDir}\" \"{appDir}\" /E /IS /NFL /NDL /NJH /NJS /R:3 /W:2\r\n" +
                "if %ERRORLEVEL% LSS 8 (\r\n" +
                "    echo.\r\n" +
                "    echo Update complete! Starting Live Photo Box...\r\n" +
                $"    start \"\" \"{Path.Combine(appDir, "Live Photo Box.exe")}\"\r\n" +
                ") else (\r\n" +
                "    echo.\r\n" +
                "    echo Update failed with error. Please download manually:\r\n" +
                "    echo https://github.com/LengxiQwQ/live-photo-box/releases\r\n" +
                "    pause\r\n" +
                ")\r\n" +
                $"rmdir /S /Q \"{tempDir}\"\r\n" +
                "exit\r\n";

            try
            {
                File.WriteAllText(batPath, batContent, Encoding.UTF8);
                LogService.Debug($"UpdateService: Update script written → {batPath} ({batContent.Length} bytes)",
                    LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Failed to write update.bat!",
                    exception: ex,
                    source: LogSource.System);
                return;
            }

            // 启动批处理脚本（独立窗口，不等待）
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                });
                LogService.Info(
                    $"UpdateService: Portable update script launched. " +
                    $"Source: {extractDir}, Target: {appDir}",
                    LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Failed to launch update.bat!",
                    exception: ex,
                    source: LogSource.System);
            }
        }
    }
}
