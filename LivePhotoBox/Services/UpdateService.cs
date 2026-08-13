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
        private const string GitHubTokenKey = "GitHubApiToken";
        private const int CheckIntervalDays = 2;

        // ── Microsoft Store 产品页（商店版「前往商店更新」入口）───────
        public const string StoreProductId = "9N3D1QNRTVCH";
        public const string StorePageUrl = "https://apps.microsoft.com/detail/9N3D1QNRTVCH";
        // ms-windows-store 协议可直接唤起 Microsoft Store 应用并定位到产品页（不经过浏览器）
        public const string StorePageProtocolUri = $"ms-windows-store://pdp/?ProductId={StoreProductId}";

        // ── 静态字段 ──────────────────────────────────────────────────
        private static readonly HttpClient _httpClient;      // 仅用于 GitHub API 请求
        private static readonly HttpClient _downloadClient;  // 仅用于下载资产（无 API 头，避免计入配额）

        /// <summary>是否为 MSIX 打包模式（打包模式下不启用自动更新）。</summary>
        public static bool IsPackagedMode { get; }

        /// <summary>是否启用自动更新（仅非打包模式）。</summary>
        public static bool IsUpdateEnabled => !IsPackagedMode;

        /// <summary>是否已设置 GitHub API Token（绕过未认证限流 60次/小时）。</summary>
        public static bool HasApiToken => !string.IsNullOrWhiteSpace(_gitHubToken);

        /// <summary>当前使用的 GitHub API Token 前缀（仅显示前 6 位，避免完整泄露）。</summary>
        public static string TokenDisplayText =>
            string.IsNullOrWhiteSpace(_gitHubToken) ? string.Empty
            : _gitHubToken.Length <= 6 ? _gitHubToken
            : _gitHubToken.Substring(0, 6) + "…";

        private static string? _gitHubToken;

        // ── 静态构造函数 ──────────────────────────────────────────────

        static UpdateService()
        {
            // 打包模式判断：统一通过 App.IsPackaged（引用 App.xaml.cs 单一来源）。
            // 避免各处重复 try Package.Current 的写法。
            IsPackagedMode = App.IsPackaged;

            // 日志仅用于启动时告知用户当前更新策略，不影响其他逻辑。
            LogService.Info(
                IsPackagedMode
                    ? "UpdateService: Running in PACKAGED mode (MSIX). Auto-update DISABLED."
                    : "UpdateService: Running in UNPACKAGED mode. Auto-update ENABLED.",
                LogSource.System);

            // 初始化 API 客户端（必须设置 User-Agent 和 API Accept 头）
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LivePhotoBox-Update/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            // 下载客户端：只用 User-Agent，不带 API Accept 头，避免下载请求被计入 API 配额
            _downloadClient = new HttpClient();
            _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("LivePhotoBox-Update/1.0");
            _downloadClient.Timeout = TimeSpan.FromMinutes(15);

            // 尝试加载之前保存的 GitHub API Token（调试工具区可设置）
            _gitHubToken = AppSettingsService.GetValue(GitHubTokenKey, "");
            ApplyToken();

            LogService.Debug($"UpdateService initialized. API URL: {GitHubApiUrl}, Check interval: {CheckIntervalDays} days" +
                (HasApiToken ? $", API token: {TokenDisplayText}" : ", no API token (unauthenticated)"),
                LogSource.System);
        }

        // ── GitHub API Token 管理（调试用，绕过 60次/小时限流）─────

        /// <summary>
        /// 设置 GitHub Personal Access Token，用于 API 认证（5000 次/小时）。
        /// 传入 null 或空字符串可清除已设置的 Token。
        /// Token 会持久化到 AppSettings，下次启动自动加载。
        /// </summary>
        public static void SetApiToken(string? token)
        {
            // 去掉常见的复制粘贴误触（如开头结尾的空格、换行）
            var trimmed = token?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                trimmed = null;

            _gitHubToken = trimmed;
            AppSettingsService.SetValue(GitHubTokenKey, trimmed ?? "");
            ApplyToken();

            if (trimmed != null)
                LogService.Info($"UpdateService: GitHub API token set → {TokenDisplayText}", LogSource.System);
            else
                LogService.Info("UpdateService: GitHub API token cleared.", LogSource.System);
        }

        /// <summary>
        /// 将当前 Token 应用到 _httpClient 的 Authorization 头。
        /// 无 Token 时移除 Authorization 头，恢复未认证模式。
        /// </summary>
        private static void ApplyToken()
        {
            _httpClient.DefaultRequestHeaders.Remove("Authorization");
            if (!string.IsNullOrWhiteSpace(_gitHubToken))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_gitHubToken}");
            }
        }

        // ── 安装类型检测 ──────────────────────────────────────────────

        /// <summary>
        /// 检测当前是否为 Inno Setup 安装版。
        /// 判定统一走 InstallChannelDetector（按卸载器身份识别，
        /// 见 InstallChannelDetector.IsInnoSetupUninstaller 的签名说明）。
        /// 便携版不会包含卸载器。
        /// </summary>
        public static bool IsInnoSetupInstall()
        {
            try
            {
                bool result = InstallChannelDetector.IsInnoSetup();
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

        // ── 限流头解析 ──────────────────────────────────────────────

        /// <summary>
        /// 从 API 响应中提取 X-RateLimit-* 头并记录日志。
        /// 用于排查配额消耗问题（未认证：60次/小时）。
        /// </summary>
        private static void LogRateLimitHeaders(HttpResponseMessage response)
        {
            try
            {
                string? remaining = null, limit = null, reset = null;
                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var r)) remaining = string.Join(",", r);
                if (response.Headers.TryGetValues("X-RateLimit-Limit", out var l)) limit = string.Join(",", l);
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var rs)) reset = string.Join(",", rs);

                if (remaining != null || limit != null)
                {
                    var resetTime = "N/A";
                    if (reset != null && long.TryParse(reset, out var epoch))
                    {
                        try { resetTime = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime().ToString("HH:mm:ss"); }
                        catch { resetTime = reset; }
                    }
                    LogService.Debug(
                        $"UpdateService: Rate limit → {remaining}/{limit} remaining, resets at {resetTime}",
                        LogSource.System);
                }
            }
            catch { /* 读取限流头失败不影响主流程 */ }
        }

        // ── 联网检测 ──────────────────────────────────────────────

        /// <summary>
        /// 快速检测本机是否能访问互联网（尝试连接 github.com，超时 5 秒）。
        /// 用于区分"没网"和"GitHub API 挂了/限流"两种情况，给用户更精准的提示。
        /// </summary>
        public static async Task<bool> CheckInternetConnectivityAsync()
        {
            try
            {
                // 用 Bing 测基础联网（国内国外都通），GitHub 可达性单独由 API 调用验证
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.bing.com");
                var response = await _httpClient.SendAsync(request, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
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

                // 记录最终请求 URL（若与初始 URL 不同，说明发生了重定向，每跳都计入配额）
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "(unknown)";
                var wasRedirected = !string.Equals(finalUrl, GitHubApiUrl, StringComparison.OrdinalIgnoreCase);
                LogService.Debug(
                    $"UpdateService: GitHub API response → {(int)statusCode} {statusCode}" +
                    (wasRedirected ? $" (redirected to {finalUrl})" : ""),
                    LogSource.System);

                // 记录限流头，方便排查配额消耗
                LogRateLimitHeaders(response);

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

        /// <summary>更新检查的版本关系三态。</summary>
        public enum UpdateRelation
        {
            /// <summary>远程 Release 比当前版本新，可更新。</summary>
            UpdateAvailable,
            /// <summary>当前版本与最新 Release 相同，已是最新。</summary>
            UpToDate,
            /// <summary>当前版本比最新 Release 还新（开发版 / 预览版）。</summary>
            Ahead
        }

        /// <summary>
        /// 比较当前应用版本与 GitHub Release 的 tag 版本，返回三态关系。
        /// 统一按 主.次.修订 三段比较：GitHub tag 只有 3 段，而打包版 AppVersion 是 4 段（Revision=0），
        /// 直接按 System.Version 逐段比会因缺省段按 -1 处理，把"与正式版同版本"误判成超前。
        /// </summary>
        public static UpdateRelation CompareWithLatest(GitHubReleaseResponse release)
        {
            if (release == null)
            {
                LogService.Debug("UpdateService: CompareWithLatest → UpToDate (release is null)", LogSource.System);
                return UpdateRelation.UpToDate;
            }

            // 去掉 tag 前缀 'v'，如 "v1.14.11" → "1.14.11"
            var tagVersion = release.TagName?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tagVersion, out var latestVersion))
            {
                LogService.Warn(
                    $"UpdateService: CompareWithLatest → UpToDate (cannot parse tag version '{release.TagName}')",
                    source: LogSource.System);
                return UpdateRelation.UpToDate;
            }

            var currentVersionStr = App.AppVersion;
            if (!Version.TryParse(currentVersionStr, out var currentVersion))
            {
                LogService.Warn(
                    $"UpdateService: CompareWithLatest → UpToDate (cannot parse current version '{currentVersionStr}')",
                    source: LogSource.System);
                return UpdateRelation.UpToDate;
            }

            // 只保留三段参与比较（Revision 参与会受 3/4 段差异干扰，且 tag 始终只有三段）
            var current3 = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(currentVersion.Build, 0));
            var latest3 = new Version(latestVersion.Major, latestVersion.Minor, Math.Max(latestVersion.Build, 0));

            var relation = current3 > latest3 ? UpdateRelation.Ahead
                : current3 < latest3 ? UpdateRelation.UpdateAvailable
                : UpdateRelation.UpToDate;

            LogService.Info(
                $"UpdateService: Version comparison → current={current3}, latest={latest3}, relation={relation}",
                LogSource.System);
            return relation;
        }

        /// <summary>
        /// 是否有新版本可用（便捷布尔判断，等价于 <see cref="CompareWithLatest"/> == UpdateAvailable）。
        /// </summary>
        public static bool IsNewerVersion(GitHubReleaseResponse release)
            => CompareWithLatest(release) == UpdateRelation.UpdateAvailable;

        // ── 资产选择 ──────────────────────────────────────────────────

        /// <summary>
        /// 根据安装类型选择合适的下载资产。
        /// Inno Setup 安装版选 -setup.exe，便携版选 -portable.zip。
        /// 匹配不到时返回 null。
        /// </summary>
        /// <summary>
        /// 获取即将下载的资产大小（字节），供 UI 显示下载进度。无匹配资产时返回 0。
        /// </summary>
        public static long GetAssetSize(GitHubReleaseResponse release)
        {
            return SelectAsset(release)?.Size ?? 0;
        }

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

        // ── 临时文件清理 ──────────────────────────────────────────

        /// <summary>
        /// 删除指定临时目录（包括未完成的下载、上次崩溃残留等）。
        /// 不会抛异常，清理失败仅记录日志。
        /// </summary>
        private static void CleanupTempDir(string tempDir)
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                    LogService.Debug($"UpdateService: Cleaned up temp dir: {tempDir}", LogSource.System);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"UpdateService: Failed to clean temp dir: {ex.Message}", source: LogSource.System);
            }
        }

        /// <summary>
        /// 清理所有更新相关的临时文件。建议在应用启动时调用一次，删除上次可能残留的垃圾。
        /// 安全操作，不影响正常功能。
        /// </summary>
        public static void CleanupUpdateTempFiles()
        {
            CleanupTempDir(Path.Combine(Path.GetTempPath(), "LivePhotoBox_Update"));
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
                // 清理上次可能残留的临时文件（上次崩溃/中断留下的垃圾）
                string tempDir = Path.Combine(Path.GetTempPath(), "LivePhotoBox_Update");
                CleanupTempDir(tempDir);

                Directory.CreateDirectory(tempDir);

                string destPath = Path.Combine(tempDir, asset.Name);
                LogService.Info(
                    $"UpdateService: Downloading {asset.Name} ({asset.Size / 1024.0 / 1024.0:F1} MB) " +
                    $"from {asset.BrowserDownloadUrl} → {destPath}",
                    LogSource.System);

                // 发送下载请求（用 _downloadClient，不带 API Accept 头，避免计入 API 配额）
                using var response = await _downloadClient.GetAsync(
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
                CleanupTempDir(Path.Combine(Path.GetTempPath(), "LivePhotoBox_Update"));
                return null;
            }
            catch (HttpRequestException ex)
            {
                LogService.Error(
                    $"UpdateService: Download HTTP error → {ex.GetType().Name}: {ex.Message}",
                    exception: ex,
                    source: LogSource.System);
                CleanupTempDir(Path.Combine(Path.GetTempPath(), "LivePhotoBox_Update"));
                return null;
            }
            catch (Exception ex)
            {
                LogService.Error(
                    $"UpdateService: Download failed → {ex.GetType().Name}: {ex.Message}",
                    exception: ex,
                    source: LogSource.System);
                CleanupTempDir(Path.Combine(Path.GetTempPath(), "LivePhotoBox_Update"));
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
        /// <summary>
        /// 预处理更新包（解压 zip、写 .bat），返回一个可瞬间启动的路径。
        /// 「关闭时更新」在点按钮时就调用此方法，关窗口时只负责启动，实现秒关。
        /// </summary>
        /// <returns>可执行路径（.bat 或 .exe），null 表示准备失败</returns>
        public static string? PrepareInstaller(string downloadedPath, bool isSetup, bool restartAfterUpdate)
        {
            LogService.Info(
                $"UpdateService: Preparing {(isSetup ? "Inno Setup installer" : "portable updater")}... " +
                $"restartAfterUpdate={restartAfterUpdate}",
                LogSource.System);

            if (isSetup)
                return downloadedPath; // setup.exe 不需要预处理

            return PreparePortableUpdater(downloadedPath, restartAfterUpdate);
        }

        /// <summary>
        /// 立即执行已准备好的更新程序（仅启动进程，不做任何耗时操作）。
        /// </summary>
        public static void ExecutePreparedInstaller(string preparedPath, bool isSetup)
        {
            LogService.Info($"UpdateService: Executing prepared installer → {preparedPath}", LogSource.System);

            if (isSetup)
            {
                LaunchSetupInstaller(preparedPath);
            }
            else
            {
                LaunchPortableBat(preparedPath);
            }
        }

        /// <summary>
        /// 启动 Inno Setup 安装包，安装完成后自动启动新版本。
        /// 使用 cmd /c 串联：先静默安装，完了 start 启动应用。
        /// </summary>
        private static void LaunchSetupInstaller(string setupPath)
        {
            var appExe = Path.Combine(AppContext.BaseDirectory, "Live Photo Box.exe");
            var args = $"/c \"\"{setupPath}\" /SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART && start \"\" \"{appExe}\"\"";

            LogService.Info(
                $"UpdateService: Starting Inno Setup → cmd /c ... {setupPath}",
                LogSource.System);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
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

        /// <param name="restartAfterUpdate">更新完成后是否重启应用（关闭时更新=false，立即重启=true）</param>
        public static void LaunchInstaller(string downloadedPath, bool isSetup, bool restartAfterUpdate = true)
        {
            var prepared = PrepareInstaller(downloadedPath, isSetup, restartAfterUpdate);
            if (prepared != null)
                ExecutePreparedInstaller(prepared, isSetup);
        }

        /// <summary>
        /// 预处理便携版更新：解压 zip → 写 .bat，返回 .bat 路径（可瞬间启动）。
        /// 「关闭时更新」在点按钮时就调用，关窗口时只需 Process.Start → 秒关。
        /// </summary>
        private static string? PreparePortableUpdater(string zipPath, bool restartAfterUpdate)
        {
            string tempDir = Path.GetDirectoryName(zipPath)!;
            string extractDir = Path.Combine(tempDir, "extracted");

            if (Directory.Exists(extractDir))
            {
                try { Directory.Delete(extractDir, true); }
                catch { /* 残留清理失败不影响 */ }
            }

            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                LogService.Info("UpdateService: Portable zip extracted for deferred update.", LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Error("UpdateService: Failed to extract zip for portable update!", exception: ex, source: LogSource.System);
                return null;
            }

            string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string batPath = Path.Combine(tempDir, "update.bat");

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
                (restartAfterUpdate
                    ? "    echo Update complete! Starting Live Photo Box...\r\n" +
                      $"    start \"\" \"{Path.Combine(appDir, "Live Photo Box.exe")}\"\r\n"
                    : "    echo Update complete!\r\n") +
                ") else (\r\n" +
                "    echo.\r\n" +
                "    echo Update failed with error. Please download manually:\r\n" +
                "    echo https://github.com/LengxiQwQ/live-photo-box/releases\r\n" +
                "    pause\r\n" +
                ")\r\n" +
                $"rmdir /S /Q \"{tempDir}\"\r\n" +
                "exit\r\n";

            File.WriteAllText(batPath, batContent, Encoding.UTF8);
            LogService.Info($"UpdateService: Portable update prepared → {batPath}", LogSource.System);
            return batPath;
        }

        /// <summary>
        /// 启动已生成好的便携版 .bat 脚本（仅 Process.Start，无耗时操作）。
        /// 通过 cmd /c 启动 .bat 并以隐藏窗口运行，与安装版保持一致的静默体验。
        /// </summary>
        private static void LaunchPortableBat(string batPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{batPath}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                LogService.Info("UpdateService: Portable .bat launched (hidden window).", LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Error("UpdateService: Failed to launch update.bat!", exception: ex, source: LogSource.System);
            }
        }
    }
}
