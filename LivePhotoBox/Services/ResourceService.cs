using Microsoft.Windows.ApplicationModel.Resources;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    public static class ResourceService
    {
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

        public static string Format(string key, params object[] args)
        {
            string format = GetString(key);
            return args.Length == 0
                ? format
                : string.Format(CultureInfo.CurrentCulture, format, args);
        }
    }
}
