using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public static BitmapImage? CachedBannerImage { get; set; }

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

        public App()
        {
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