using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    // 资源字符串服务 — 封装 WinUI 3 的 ResourceLoader，
    // 提供多语言字符串获取与格式化功能。
    // 所有方法均为线程安全的无状态调用。
    public static class ResourceService
    {
        private static readonly ResourceManager ResourceManager = new();

        // 获取当前 UI 语言的资源字符串。
        // 若 key 不存在或发生 COMException，直接返回 key 本身。
        // key: 资源键名。
        // è¿å: 本地化的字符串，或 key 本身（如果资源缺失）。
        public static string GetString(string key)
        {
            try
            {
                string value = new ResourceLoader().GetString(key);
                return string.IsNullOrWhiteSpace(value) ? key : value;
            }
            catch (COMException)
            {
                return key;
            }
        }

        // 获取指定语言的资源字符串。
        // 通过 ResourceManager 手动设置 Language 限定符实现。
        // languageTag: BCP-47 语言标记（如 "zh-CN", "en-US"）。
        // key: 资源键名。
        // è¿å: 指定语言的资源字符串。
        public static string GetStringForLanguage(string languageTag, string key)
        {
            try
            {
                var resourceContext = ResourceManager.CreateResourceContext();
                resourceContext.QualifierValues["Language"] = languageTag;

                string? value = ResourceManager.MainResourceMap.GetValue($"Resources/{key}", resourceContext)?.ValueAsString;
                return string.IsNullOrWhiteSpace(value) ? key : value;
            }
            catch (COMException)
            {
                return key;
            }
        }

        // 获取当前 UI 语言的资源字符串并执行 string.Format。
        // key: 资源键名（资源值应含格式化占位符）。
        // args: 格式化参数。
        // è¿å: 格式化后的本地化字符串。
        public static string Format(string key, params object[] args)
        {
            string format = GetString(key);
            return args.Length == 0
                ? format
                : string.Format(CultureInfo.CurrentCulture, format, args);
        }

        // 获取指定语言的资源字符串并执行 string.Format。
        // languageTag: BCP-47 语言标记。
        // key: 资源键名。
        // args: 格式化参数。
        // è¿å: 格式化后的指定语言字符串。
        public static string FormatForLanguage(string languageTag, string key, params object[] args)
        {
            string format = GetStringForLanguage(languageTag, key);
            CultureInfo culture;

            try
            {
                culture = CultureInfo.GetCultureInfo(languageTag);
            }
            catch (CultureNotFoundException)
            {
                culture = CultureInfo.InvariantCulture;
            }

            return args.Length == 0
                ? format
                : string.Format(culture, format, args);
        }
    }
}
