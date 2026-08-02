using System;
using System.CommandLine;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands
{
    internal static class UpdateCommand
    {
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/lengxiqwq/live-photo-box/releases/latest";
        private const string ReleasesPageUrl =
            "https://github.com/lengxiqwq/live-photo-box/releases";

        private static readonly HttpClient _http = new()
        {
            DefaultRequestHeaders = { { "User-Agent", "livephotobox-cli" } },
            Timeout = TimeSpan.FromSeconds(10)
        };

        public static Command Create()
        {
            var cmd = new Command("update-check", "Check if a newer version is available on GitHub");
            cmd.SetHandler(async () =>
            {
                try
                {
                    await CheckAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Update check failed: {ex.Message}");
                    Environment.ExitCode = 1;
                }
            });
            return cmd;
        }

        private static async Task CheckAsync()
        {
            var current = GetCurrentVersion();

            Console.WriteLine($"Current version : {current}");
            Console.Write("Checking GitHub ... ");

            string json;
            try
            {
                json = await _http.GetStringAsync(ReleasesApiUrl);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"unreachable ({ex.Message})");
                Console.WriteLine($"Visit {ReleasesPageUrl} to check manually.");
                Environment.ExitCode = 2;
                return;
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("timeout");
                Console.WriteLine($"Visit {ReleasesPageUrl} to check manually.");
                Environment.ExitCode = 2;
                return;
            }

            Console.WriteLine("OK");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var latestTag = root.GetProperty("tag_name").GetString() ?? "";
            var latestName = root.GetProperty("name").GetString() ?? latestTag;
            var latestUrl = root.GetProperty("html_url").GetString() ?? ReleasesPageUrl;

            // Strip leading "v" from tag: "v2.1.1" → "2.1.1"
            var latestVersion = latestTag.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? latestTag[1..]
                : latestTag;

            Console.WriteLine($"Latest version  : {latestVersion}");

            if (TryParseVersion(latestVersion, out var latest) &&
                TryParseVersion(current, out var cur))
            {
                var comparison = CompareVersions(cur, latest);
                if (comparison < 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"A newer version is available: {latestTag}");
                    Console.WriteLine($"  {latestName}");
                    Console.WriteLine($"  Download: {latestUrl}");
                    Console.WriteLine();
                }
                else if (comparison == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("You are running the latest version.");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("You are running a pre-release or development build.");
                    Console.WriteLine($"Latest stable: {latestTag}");
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"Latest release: {latestTag}");
                Console.WriteLine($"  {latestUrl}");
            }
        }

        private static string GetCurrentVersion()
        {
            var ver = Assembly.GetEntryAssembly()?.GetName().Version;
            if (ver is null || ver.Major == 0 && ver.Minor == 0 && ver.Build == 0)
                return "dev";

            // 2.1.1.0 → 2.1.1
            if (ver.Revision == 0)
                return $"{ver.Major}.{ver.Minor}.{ver.Build}";

            return $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}";
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
