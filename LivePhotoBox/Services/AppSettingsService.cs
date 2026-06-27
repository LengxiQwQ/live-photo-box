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
    /// 非打包模式：ApplicationData.Current 需要包标识会抛异常，
    /// 回退到本地 JSON 文件存储（位于 AppContext.BaseDirectory）。
    /// </summary>
    public static class AppSettingsService
    {
        private static readonly string? _jsonFilePath;
        private static readonly Dictionary<string, object?> _jsonStore;

        private static ApplicationDataContainer? _localSettings;
        private static bool _localSettingsTried;

        static AppSettingsService()
        {
            try
            {
                _jsonFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
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
                try
                {
                    _localSettings = ApplicationData.Current.LocalSettings;
                }
                catch (InvalidOperationException)
                {
                    // 非打包模式：无包标识，ApplicationData.Current 不可用
                    _localSettings = null;
                }
                catch (Exception)
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
            if (_jsonStore.TryGetValue(key, out var jsonValue) && jsonValue is JsonElement je)
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
