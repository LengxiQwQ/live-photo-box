using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 应用设置服务 — 持久化用户偏好的键值存储。
    ///
    /// 打包模式（MSIX）：使用 ApplicationData.LocalSettings（系统 API）。
    /// 非打包模式：回退到本地 JSON 文件存储，位于用户数据目录
    /// (%LOCALAPPDATA%\LivePhotoBox\appsettings.json)，保证可写。
    /// </summary>
    public static class AppSettingsService
    {
        /// <summary>
        /// 非打包模式下 JSON 文件的存放目录（用户数据目录，保证可写）。
        /// </summary>
        private static readonly string JsonDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LivePhotoBox");

        private static readonly string? _jsonFilePath;
        private static readonly Dictionary<string, object?> _jsonStore;

        private static ApplicationDataContainer? _localSettings;
        private static bool _localSettingsTried;

        static AppSettingsService()
        {
            try
            {
                _jsonFilePath = Path.Combine(JsonDirectory, "appsettings.json");
                if (File.Exists(_jsonFilePath))
                {
                    var json = File.ReadAllText(_jsonFilePath);
                    _jsonStore = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                                 ?? new Dictionary<string, object?>();
                }
                else
                {
                    _jsonStore = new Dictionary<string, object?>();
                }
            }
            catch
            {
                _jsonStore = new Dictionary<string, object?>();
            }
        }

        /// <summary>
        /// 获取 LocalSettings。打包模式下可用；非打包模式返回 null。
        /// </summary>
        private static ApplicationDataContainer? LocalSettings
        {
            get
            {
                if (_localSettingsTried)
                    return _localSettings;

                _localSettingsTried = true;

                // 非打包模式统一走 JSON 文件，不用 LocalSettings。
                // WinAppSDK 1.5+ 虽可在非打包模式下使用 LocalSettings，
                // 但数据存在 %LocalAppData%\<hash>\ 下，卸载后残留，重装不清。
                if (!App.IsPackaged)
                {
                    _localSettings = null;
                    return null;
                }

                try
                {
                    _localSettings = ApplicationData.Current.LocalSettings;
                }
                catch
                {
                    _localSettings = null;
                }
                return _localSettings;
            }
        }

        /// <summary>
        /// 读取指定键的值，若不存在或类型不匹配则返回 defaultValue。
        /// </summary>
        public static T GetValue<T>(string key, T defaultValue)
        {
            var settings = LocalSettings;
            if (settings != null)
            {
                return settings.Values.TryGetValue(key, out var rawValue) && rawValue is T typedValue
                    ? typedValue
                    : defaultValue;
            }

            // 非打包模式：从 JSON 文件读取
            if (_jsonStore.TryGetValue(key, out var jsonValue))
            {
                // 情况 1：值已经是目标类型 T（来自 SetValue 直接写入，非 JSON 反序列化）
                if (jsonValue is T directValue)
                    return directValue;

                // 情况 2：值是 JsonElement（来自 JSON 文件反序列化），需要转换
                if (jsonValue is JsonElement je)
                {
                    try
                    {
                        return JsonSerializer.Deserialize<T>(je.GetRawText()) ?? defaultValue;
                    }
                    catch
                    {
                        return defaultValue;
                    }
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// 写入指定键的值。值会立即持久化。
        /// </summary>
        public static void SetValue<T>(string key, T value)
        {
            var settings = LocalSettings;
            if (settings != null)
            {
                settings.Values[key] = value;
                return;
            }

            // 非打包模式：写入 JSON 文件
            _jsonStore[key] = value;
            PersistJsonStore();
        }

        /// <summary>
        /// 清空所有设置。
        /// </summary>
        public static void ClearAll()
        {
            var settings = LocalSettings;
            if (settings != null)
            {
                settings.Values.Clear();
                return;
            }

            _jsonStore.Clear();
            PersistJsonStore();
        }

        private static void PersistJsonStore()
        {
            try
            {
                if (_jsonFilePath != null)
                {
                    // 确保用户数据目录存在
                    Directory.CreateDirectory(JsonDirectory);
                    var json = JsonSerializer.Serialize(_jsonStore);
                    File.WriteAllText(_jsonFilePath, json);
                }
            }
            catch
            {
                // 静默处理写入失败（权限不足等）
            }
        }
    }
}
