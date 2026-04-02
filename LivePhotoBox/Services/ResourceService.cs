using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;

namespace LivePhotoBox.Services
{
    public static class ResourceService
    {
        public static string GetString(string key)
        {
            string value = new ResourceLoader().GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }

        public static string Format(string key, params object[] args)
        {
            string format = GetString(key);
            return args.Length == 0
                ? format
                : string.Format(CultureInfo.CurrentCulture, format, args);
        }
    }
}
