using Windows.Storage;

namespace LivePhotoBox.Services
{
    public static class AppSettingsService
    {
        private static ApplicationDataContainer LocalSettings => ApplicationData.Current.LocalSettings;

        public static T GetValue<T>(string key, T defaultValue)
        {
            return LocalSettings.Values.TryGetValue(key, out var rawValue) && rawValue is T typedValue
                ? typedValue
                : defaultValue;
        }

        public static void SetValue<T>(string key, T value)
        {
            LocalSettings.Values[key] = value;
        }
    }
}
