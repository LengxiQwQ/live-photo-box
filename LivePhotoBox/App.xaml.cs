using LivePhotoBox.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Threading.Tasks;

namespace LivePhotoBox
{
    public partial class App : Application
    {
        public static Window? MainWindow { get; private set; }

        public static BitmapImage? CachedBannerImage { get; set; }

        public App()
        {
            ApplyLanguageSetting();
            CrashLogService.Initialize(this);
            InitializeComponent();
            CrashLogService.RecordBreadcrumb("Application initialized.");
        }

        private void ApplyLanguageSetting()
        {
            int languageIndex = AppSettingsService.GetValue("LanguageIndex", 0);
            LanguageService.ApplyLanguageOverride(languageIndex);
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            CrashLogService.RecordBreadcrumb("Main window launch started.");
            MainWindow = new MainWindow();
            MainWindow.Activate();
            CrashLogService.RecordBreadcrumb("Main window activated.");
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
                await CrashLogService.ShowPendingCrashDialogAsync(xamlRoot);
            }
        }
    }
}