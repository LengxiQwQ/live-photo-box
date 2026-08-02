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
            if (args.Length == 0)
                return format;

            // If the format string contains no format placeholder (e.g. when
            // provider is nil and the raw key is returned), show key + args
            // so that error details are never silently dropped.
            if (!format.Contains("{0"))
                return $"{format}: {string.Join(", ", args)}";

            return string.Format(CultureInfo.CurrentCulture, format, args);
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
    // 三层 fallback：磁盘 resw → 嵌入英文 resw → Format 兜底拼接。
    public sealed class ReswResourceProvider : IResourceProvider
    {
        private readonly Dictionary<string, string> _strings;

        // 从指定的 resw 文件目录初始化。
        // reswDir: 包含 en-US/Resources.resw 的目录（可为空或不存在，自动用嵌入资源兜底）。
        public ReswResourceProvider(string reswDir)
        {
            if (!string.IsNullOrEmpty(reswDir) && Directory.Exists(reswDir))
            {
                _strings = new Dictionary<string, string>(StringComparer.Ordinal);
                // English always loaded as base; native language overlays if available
                LoadReswFile(_strings, Path.Combine(reswDir, "en-US", "Resources.resw"));
            }
            else
            {
                // Fallback: load embedded English .resw from Core assembly
                _strings = LoadEmbeddedEnglish();
            }
        }

        public string GetString(string key)
        {
            if (_strings.TryGetValue(key, out var value))
                return value;
            return key;
        }

        public string GetStringForLanguage(string languageTag, string key)
        {
            return _strings.TryGetValue(key, out var value) ? value : key;
        }

        /// <summary>
        /// Load the complete English .resw embedded as a build-time resource.
        /// Guarantees CLI always has a full English string set, even with zero
        /// deployed .resw files alongside the binary.
        /// </summary>
        private static Dictionary<string, string> LoadEmbeddedEnglish()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using var stream = typeof(ReswResourceProvider).Assembly
                    .GetManifestResourceStream("LivePhotoBox.Strings.en-US.Resources.resw");
                if (stream != null)
                {
                    var doc = XDocument.Load(stream);
                    foreach (var data in doc.Descendants("data"))
                    {
                        var name = data.Attribute("name")?.Value;
                        var value = data.Element("value")?.Value;
                        if (!string.IsNullOrEmpty(name) && value != null)
                            result[name] = value;
                    }
                }
            }
            catch
            {
                // Embedded resource failed → empty dict.
                // ResourceService.Format still catches this via its
                // "{0}"-detection fallback (shows "key: args").
            }
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
