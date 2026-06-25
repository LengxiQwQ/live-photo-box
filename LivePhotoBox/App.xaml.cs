using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LivePhotoBox
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public static BitmapImage? CachedBannerImage { get; set; }

        /// <summary>
        /// 应用版本号（单一来源）。
        /// 优先读取 MSIX 包清单中的版本（随发布/更新同步），
        /// 未打包运行时回退到入口程序集版本。
        /// 所有需要显示或写入版本号的地方统一使用此属性。
        /// </summary>
        public static string AppVersion
        {
            get
            {
                try
                {
                    var v = Windows.ApplicationModel.Package.Current.Id.Version;
                    return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                }
                catch
                {
                    var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName()?.Version;
                    return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
                }
            }
        }

        /// <summary>
        /// Refreshes <see cref="CachedBannerImage"/> to the given preset.
        /// The home page picks this up on next render.
        /// </summary>
        public static void RefreshBannerImage(BannerPreset preset)
        {
            try
            {
                CachedBannerImage = new BitmapImage(new Uri(preset.AssetPath));
                LogService.Debug($"Banner image refreshed to: {preset.Name}", LogSource.Settings);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to load banner preset '{preset.Key}': {ex.Message}", source: LogSource.Settings);
            }
        }

        /// <summary>
        /// Load banner image from persisted settings. If random mode is on,
        /// picks a random preset each time. Called on app start before the
        /// Settings VM is available, and when the home page needs a refresh.
        /// </summary>
        public static BitmapImage LoadBannerImageFromSettings()
        {
            bool random = AppSettingsService.GetValue("IsBannerRandomEnabled", false);
            int index;
            if (random)
            {
                index = Random.Shared.Next(3);
            }
            else
            {
                index = AppSettingsService.GetValue("BannerPresetIndex", 0);
            }

            string path = index switch
            {
                1 => "ms-appx:///Assets/Banners/banner_02.jpg",
                2 => "ms-appx:///Assets/Banners/banner_03.jpg",
                _ => "ms-appx:///Assets/Banners/banner_01.jpg",
            };

            return new BitmapImage(new Uri(path));
        }

        /// <summary>
        /// Gets the current effective <see cref="ElementTheme"/> for the application.
        /// When the user has selected "Default", detects the system theme.
        /// Returns <see cref="ElementTheme.Light"/> if the main window is not yet available.
        /// </summary>
        public static ElementTheme CurrentTheme
        {
            get
            {
                if (MainWindow?.Content is FrameworkElement rootElement && rootElement.RequestedTheme != ElementTheme.Default)
                {
                    return rootElement.RequestedTheme;
                }

                try
                {
                    var settings = new Windows.UI.ViewManagement.UISettings();
                    var backgroundColor = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                    return backgroundColor.R < 128 ? ElementTheme.Dark : ElementTheme.Light;
                }
                catch (Exception ex)
                {
                    LogService.Debug($"UISettings theme detection failed, defaulting to Light: {ex.Message}", LogSource.System);
                    return ElementTheme.Light;
                }
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint uMode);

        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

        public App()
        {
            // 禁止子进程崩溃时弹出 Windows 错误报告/JIT 调试器对话框。
            // exiftool / ffmpeg 等外部工具遇到损坏文件可能触发 Win32 异常，
            // 主程序有 try-catch 兜底，不需要 OS 弹窗干扰用户。
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

            ApplyLanguageSetting();
            LogService.Initialize();

            // Detect hardware early so its summary appears in the log before App/UI messages
            try { HardwareService.GetAvailableHardware(); }
            catch (Exception ex) { LogService.Warn($"Hardware detection failed: {ex.Message}", source: LogSource.System); }

            CrashHandler.Initialize(this);
            InitializeComponent();
            LogService.Info("Application initialized.", LogSource.App);
        }

        private void ApplyLanguageSetting()
        {
            int languageIndex = AppSettingsService.GetValue("LanguageIndex", 0);
            LanguageService.ApplyLanguageOverride(languageIndex);
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            LogService.Info("Main window launch started.", LogSource.UI);
            MainWindow = new MainWindow();
            MainWindow.Activate();
            LogService.Info("Main window activated.", LogSource.UI);
        }
    }
}