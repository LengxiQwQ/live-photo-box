using Windows.Storage;

namespace LivePhotoBox.Services
{
    // 应用设置服务 — 基于 Windows.ApplicationData.LocalSettings 的轻量键值存储。
    // 提供类型安全的泛型读写操作，设置值持久化在本地，应用重启后保留。
    // 整个应用中所有需要持久化用户偏好的模块均通过此服务存取设置。
    public static class AppSettingsService
    {
        private static ApplicationDataContainer LocalSettings => ApplicationData.Current.LocalSettings;

        // 读取指定键的值，若不存在或类型不匹配则返回 defaultValue。
        // T: 值的类型
        // key: 设置键名
        // defaultValue: 键不存在时的默认值
        // è¿å: 存储的值或 defaultValue
        public static T GetValue<T>(string key, T defaultValue)
        {
            return LocalSettings.Values.TryGetValue(key, out var rawValue) && rawValue is T typedValue
                ? typedValue
                : defaultValue;
        }

        // 写入指定键的值。值会立即持久化到本地存储。
        // T: 值的类型
        // key: 设置键名
        // value: 要写入的值
        public static void SetValue<T>(string key, T value)
        {
            LocalSettings.Values[key] = value;
        }

        // 清空所有设置，下次读取时全部走默认值
        public static void ClearAll()
        {
            LocalSettings.Values.Clear();
        }
    }
}
