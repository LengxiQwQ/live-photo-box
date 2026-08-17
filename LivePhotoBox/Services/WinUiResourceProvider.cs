using LivePhotoBox.Services;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    /*
     * WinUiResourceProvider.cs
     *
     * WinUI / WinAppSDK MRT Core 资源提供器。
     * 通过 ResourceLoader 从编译后的 resources.pri 加载本地化字符串，
     * 作为 GUI 模式的 ResourceService 后端。
     */
    public sealed class WinUiResourceProvider : IResourceProvider
    {
        private static readonly ResourceManager ResourceManager = new();

        public string GetString(string key)
        {
            try
            {
                var resourceContext = ResourceManager.CreateResourceContext();
                string? value = ResourceManager.MainResourceMap.GetValue($"Resources/{key}", resourceContext)?.ValueAsString;
                return string.IsNullOrWhiteSpace(value) ? key : value;
            }
            catch (COMException)
            {
                return key;
            }
        }

        public string GetStringForLanguage(string languageTag, string key)
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
    }
}
