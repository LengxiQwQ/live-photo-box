using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Threading.Tasks;

namespace LivePhotoBox
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public static BitmapImage? CachedBannerImage { get; set; }

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
                catch
                {
                    return ElementTheme.Light;
                }
            }
        }

        public App()
        {
            ApplyLanguageSetting();
            LogService.Initialize();
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
            _ = ShowPendingCrashDialogAsync();
        }

        private static async Task ShowPendingCrashDialogAsync()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (MainWindow?.Content?.XamlRoot != null)
                {
                    break;
                }

                await Task.Delay(100);
            }

            if (MainWindow?.Content?.XamlRoot is XamlRoot xamlRoot)
            {
                await CrashHandler.ShowPendingCrashDialogAsync(xamlRoot);
            }
        }
    }
}