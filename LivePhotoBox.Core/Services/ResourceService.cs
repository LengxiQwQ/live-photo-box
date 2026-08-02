using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace LivePhotoBox.Services
{
    // 资源字符串提供器接口 —— GUI 用 MRT ResourceLoader，CLI 用 resw XML 解析。
    public interface IResourceProvider
    {
        string GetString(string key);
        string GetStringForLanguage(string languageTag, string key);
    }

    // 资源字符串服务 —— 通过可替换的 provider 支持多种后端。
    public static class ResourceService
    {
        private static IResourceProvider? _provider;

        // 设置资源提供器。GUI（MRT）和 CLI（resw XML）在启动时各自调用。
        public static void SetProvider(IResourceProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        // 获取当前 UI 语言的资源字符串。未设置 provider 或 key 缺失时返回 key 本身。
        public static string GetString(string key)
        {
            if (_provider == null) return key;
            try { return _provider.GetString(key) ?? key; }
            catch { return key; }
        }

        // 获取指定语言的资源字符串。
        public static string GetStringForLanguage(string languageTag, string key)
        {
            if (_provider == null) return key;
            try { return _provider.GetStringForLanguage(languageTag, key) ?? key; }
            catch { return key; }
        }

        // string.Format 包装
        public static string Format(string key, params object[] args)
        {
            string format = GetString(key);
            return args.Length == 0
                ? format
                : string.Format(CultureInfo.CurrentCulture, format, args);
        }

        public static string FormatForLanguage(string languageTag, string key, params object[] args)
        {
            string format = GetStringForLanguage(languageTag, key);
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(languageTag); }
            catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
            return args.Length == 0
                ? format
                : string.Format(culture, format, args);
        }
    }

    // ── CLI 模式：直接解析 .resw XML ──────────────────────────────

    // 从 resw XML 文件加载资源字典的 CLI 资源提供器。
    public sealed class ReswResourceProvider : IResourceProvider
    {
        private readonly Dictionary<string, string> _strings;

        // 从指定的 resw 文件目录初始化。自动按 CultureInfo.CurrentUICulture 选择语言。
        // reswDir: 包含 zh-Hans/Resources.resw 和 en-US/Resources.resw 的目录。
        public ReswResourceProvider(string reswDir)
        {
            _strings = LoadResw(reswDir, ResolveLanguageTag());
        }

        public string GetString(string key)
        {
            return _strings.TryGetValue(key, out var value) ? value : key;
        }

        public string GetStringForLanguage(string languageTag, string key)
        {
            return _strings.TryGetValue(key, out var value) ? value : key;
        }

        private static string ResolveLanguageTag()
        {
            var culture = CultureInfo.CurrentUICulture;
            return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en-US";
        }

        private static Dictionary<string, string> LoadResw(string reswDir, string langTag)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            // 先加载英文（fallback），再加载目标语言（覆盖）
            LoadReswFile(result, Path.Combine(reswDir, "en-US", "Resources.resw"));
            if (!langTag.Equals("en-US", StringComparison.OrdinalIgnoreCase))
                LoadReswFile(result, Path.Combine(reswDir, langTag, "Resources.resw"));

            return result;
        }

        private static void LoadReswFile(Dictionary<string, string> dict, string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                var doc = XDocument.Load(filePath);
                foreach (var data in doc.Descendants("data"))
                {
                    var name = data.Attribute("name")?.Value;
                    var value = data.Element("value")?.Value;
                    if (!string.IsNullOrEmpty(name) && value != null)
                        dict[name] = value;
                }
            }
            catch
            {
                // 解析失败静默跳过
            }
        }
    }
}
