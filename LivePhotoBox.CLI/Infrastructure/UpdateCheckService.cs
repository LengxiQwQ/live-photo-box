using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Infrastructure
{
    // 更新检查共享服务 — update-check 与 info 复用同一套联网逻辑
    internal static class UpdateCheckService
    {
        // 默认 GitHub API；可用环境变量 LPB_GITHUB_RELEASES_API_URL 覆盖（本地假服务验证重试/下载用）
        private static readonly string ReleasesApiUrl =
            Environment.GetEnvironmentVariable("LPB_GITHUB_RELEASES_API_URL")
            ?? "https://api.github.com/repos/lengxiqwq/live-photo-box/releases/latest";
        public const string ReleasesPageUrl =
            "https://github.com/lengxiqwq/live-photo-box/releases";

        private static readonly HttpClient _http = new()
        {
            DefaultRequestHeaders = { { "User-Agent", "livephotobox-cli" } },
            Timeout = TimeSpan.FromSeconds(10)
        };

        // 最近一次网络请求失败的原因（仅单进程 CLI 使用，无并发问题）
        private static string? _lastFetchError;

        /// <summary>GitHub Release 资产（zip / setup.exe）。</summary>
        public sealed record GitHubAsset(string Name, long Size, string BrowserDownloadUrl, string? Digest);

        /// <summary>GitHub 最新 Release 摘要（版本 + 资产列表）。</summary>
        public sealed class LatestRelease
        {
            public required string TagName { get; init; }
            public required string Version { get; init; }
            public required IReadOnlyList<GitHubAsset> Assets { get; init; }
        }

        /// <summary>最近一次网络请求失败的原因，供调用方展示。</summary>
        public static string? LastFetchError => _lastFetchError;

        /// <summary>
        /// 打印手动下载链接行（单行、无缩进、与状态行对齐）。
        /// 只在自动更新走不通时展示（检查失败 / 无对应包 / 下载失败）；
        /// 检查成功路径不展示，保持输出干净。
        /// </summary>
        public static void PrintManualDownload()
        {
            Console.WriteLine($"Manual download: {ReleasesPageUrl}");
        }

        private const string CheckPrefix = "Checking GitHub ... ";

        // 交互终端可 \r 原地重写；重定向/管道不行（会产生 \r 字符污染输出）
        private static bool CanRewriteLine => !Console.IsOutputRedirected;

        // 最近一次检查行的可见长度，用于状态覆盖时精确清掉残留的 "retry N/M"
        private static int _checkLineLen;

        /// <summary>开始检查：interactive 先打印前缀占位；redirected 静默（等最终状态打完整行）。</summary>
        public static void BeginCheck()
        {
            if (CanRewriteLine)
                Console.Write(CheckPrefix);
        }

        /// <summary>输出"Checking GitHub ... &lt;status&gt;"整行。interactive 时 \r 重写；redirected 时打完整行。</summary>
        public static void WriteCheckStatus(string status, ConsoleColor color)
            => WriteCheckStatusCore(status, () => CliConsole.Write(status, color));

        /// <summary>RGB 颜色重载（如 CliConsole.Notice）。</summary>
        public static void WriteCheckStatus(string status, (int R, int G, int B) rgb)
            => WriteCheckStatusCore(status, () => CliConsole.Write(status, rgb));

        private static void WriteCheckStatusCore(string status, Action writeStatus)
        {
            if (CanRewriteLine)
            {
                Console.Write("\r" + CheckPrefix);
                writeStatus();
                // 精确清掉可能残留的 "retry N/M"（按上一次行长度补空格）
                var pad = Math.Max(0, _checkLineLen - CheckPrefix.Length - status.Length);
                if (pad > 0) Console.Write(new string(' ', pad));
                Console.WriteLine();
            }
            else
            {
                Console.Write(CheckPrefix);
                writeStatus();
                Console.WriteLine();
            }
        }

        /// <summary>重试反馈：interactive 覆盖整行；redirected 静默（不在 CI 日志刷重试噪音）。</summary>
        public static void WriteCheckRetry(int retryNumber, int maxAttempts)
        {
            if (CanRewriteLine)
            {
                var text = $"retry {retryNumber}/{maxAttempts}   ";
                _checkLineLen = CheckPrefix.Length + text.Length;
                Console.Write($"\r{CheckPrefix}{text}");
            }
        }

        public sealed class Result
        {
            public required string CurrentVersion { get; init; }
            // Ok=false 表示网络失败，ErrorMessage 为原因
            public bool Ok { get; init; }
            public string? ErrorMessage { get; init; }
            // true 表示当前副本由 WinGet 安装管理，内置更新应禁用
            public bool ManagedByWinget { get; init; }
            // 版本号能否解析（非常规版本号时回退为 Latest release 分支）
            public bool VersionParsed { get; init; }
            public string? LatestTag { get; init; }
            public string? LatestVersion { get; init; }
            // <0 有新版本；0 最新；>0 预发布/超前
            public int Comparison { get; init; }
        }

        public static async Task<Result> CheckAsync(Action<int, int>? onRetry = null)
        {
            var current = VersionInfo.GetDisplayVersion();

            // WinGet 管理的副本由 winget upgrade 负责更新，内置更新不可用
            if (WingetInstallDetector.IsWingetManaged())
            {
                return new Result
                {
                    CurrentVersion = current,
                    Ok = true,
                    ManagedByWinget = true
                };
            }

            var release = await FetchLatestReleaseAsync(onRetry: onRetry);
            if (release is null)
            {
                return new Result { CurrentVersion = current, ErrorMessage = _lastFetchError ?? "network error" };
            }

            var parsed = TryCompare(current, release.Version, out var comparison);

            return new Result
            {
                CurrentVersion = current,
                Ok = true,
                VersionParsed = parsed,
                LatestTag = release.TagName,
                LatestVersion = release.Version,
                Comparison = comparison
            };
        }

        /// <summary>
        /// 获取 GitHub 最新 Release（含资产列表）。网络失败返回 null，原因见 <see cref="LastFetchError"/>。
        /// </summary>
        public static async Task<LatestRelease?> FetchLatestReleaseAsync(
            int maxAttempts = 3, Action<int, int>? onRetry = null)
        {
            string json;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    json = await _http.GetStringAsync(ReleasesApiUrl);
                    break;
                }
                catch (HttpRequestException ex)
                {
                    _lastFetchError = DescribeNetworkError(ex);
                }
                catch (TaskCanceledException)
                {
                    _lastFetchError = "timeout";
                }

                if (attempt >= maxAttempts)
                    return null;

                onRetry?.Invoke(attempt + 1, maxAttempts);
                await Task.Delay(attempt * 1000);
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var latestTag = root.GetProperty("tag_name").GetString() ?? "";
                var latestVersion = latestTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? latestTag[1..]
                    : latestTag;

                var assets = new List<GitHubAsset>();
                if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assetsEl.EnumerateArray())
                    {
                        var name = a.GetProperty("name").GetString() ?? "";
                        if (name.Length == 0)
                            continue;

                        var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                        var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";

                        string? digest = null;
                        if (a.TryGetProperty("digest", out var d))
                        {
                            var ds = d.GetString();
                            if (!string.IsNullOrWhiteSpace(ds))
                                digest = ds;
                        }

                        assets.Add(new GitHubAsset(name, size, url, digest));
                    }
                }

                return new LatestRelease
                {
                    TagName = latestTag,
                    Version = latestVersion,
                    Assets = assets
                };
            }
            catch (JsonException)
            {
                _lastFetchError = "invalid response";
                return null;
            }
        }

        /// <summary>
        /// 比较两个版本字符串（v 前缀可选）。无法解析时返回 false。
        /// </summary>
        /// <param name="current">当前版本。</param>
        /// <param name="latest">最新版本。</param>
        /// <param name="comparison">&lt;0 有新版本；0 相同；&gt;0 当前更新。</param>
        public static bool TryCompare(string current, string latest, out int comparison)
        {
            comparison = 0;
            if (!TryParseVersion(current, out var cur) || !TryParseVersion(latest, out var latestVer))
                return false;
            comparison = CompareVersions(cur, latestVer);
            return true;
        }

        // 系统本地化的异常文本（如中文系统的“目标计算机积极拒绝”）不直接透出，
        // 统一映射为固定的英文原因
        private static string DescribeNetworkError(HttpRequestException ex)
        {
            if (ex.InnerException is SocketException sock)
            {
                return sock.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "connection refused",
                    SocketError.HostNotFound => "host not found",
                    SocketError.TimedOut => "timed out",
                    SocketError.NetworkUnreachable => "network unreachable",
                    _ => "network error"
                };
            }
            return "network error";
        }

        private static bool TryParseVersion(string s, out (int Major, int Minor, int Build) v)
        {
            v = default;
            var parts = s.Split('.');
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[0], out int ma)) return false;
            if (!int.TryParse(parts[1], out int mi)) return false;
            if (!int.TryParse(parts[2], out int bu)) return false;
            v = (ma, mi, bu);
            return true;
        }

        private static int CompareVersions(
            (int Major, int Minor, int Build) a,
            (int Major, int Minor, int Build) b)
        {
            if (a.Major != b.Major) return a.Major.CompareTo(b.Major);
            if (a.Minor != b.Minor) return a.Minor.CompareTo(b.Minor);
            return a.Build.CompareTo(b.Build);
        }
    }
}
