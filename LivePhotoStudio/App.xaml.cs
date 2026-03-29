using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace LivePhotoStudio
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public static BitmapImage? CachedBannerImage { get; set; }

        public App()
        {
            // 【关键修复】：在一切 UI 组件初始化之前，先强行注入我们要的语言！
            ApplyLanguageSetting();

            InitializeComponent();
        }

        // [新增方法]：读取本地设置，拦截系统语言
        private void ApplyLanguageSetting()
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            if (settings.TryGetValue("LanguageIndex", out var langObj) && langObj is int langIndex)
            {
                string langCode = langIndex switch { 1 => "zh-CN", 2 => "en-US", _ => "" };
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = langCode;
            }
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
    }
}