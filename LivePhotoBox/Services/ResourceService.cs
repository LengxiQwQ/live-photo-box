using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    public static class ResourceService
    {
        private static readonly ResourceManager ResourceManager = new();

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
