using LivePhotoBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LanguageService
    {
        public static bool HasEffectiveLanguageChanged(int previousIndex, int currentIndex)
        {
            return HasEffectiveLanguageChanged(GetEffectiveLanguage(previousIndex), GetEffectiveLanguage(currentIndex));
        }

        public static bool HasEffectiveLanguageChanged(string previousLanguageTag, string currentLanguageTag)
        {
            return !string.Equals(previousLanguageTag, currentLanguageTag, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetEffectiveLanguage(int index)
        {
            if (index == 1) return "zh-Hans";
            if (index == 2) return "en-US";

            var systemLangs = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            foreach (var lang in systemLangs)
            {
                var lowerLang = lang.ToLowerInvariant();
                if (lowerLang.StartsWith("zh")) return "zh-Hans";
                if (lowerLang.StartsWith("en")) return "en-US"; // 加上这一句，先匹配到英文就返回英文
            }

            // 如果用户的系统语言既不是中文也不是英文（比如日文），默认回退到英文
            return "en-US";
        }

        public static string GetCurrentLanguageTag()
        {
            var primary = Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrWhiteSpace(primary)) return primary;

            var systemLangs = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (systemLangs.Count > 0) return systemLangs[0];

            return "en-US";
        }

        public static void ApplyLanguageOverride(int languageIndex)
        {
            ApplyLanguageOverride(GetEffectiveLanguage(languageIndex));
        }

        public static void ApplyLanguageOverride(string languageTag)
        {
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = languageTag;
        }

        public static async Task ShowRestartPromptAsync(string targetLang)
        {
            var dispatcher = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
            {
                return;
            }

            var completionSource = new TaskCompletionSource();

            dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    if (App.MainWindow?.Content?.XamlRoot == null)
                    {
                        completionSource.SetResult();
                        return;
                    }

                    var resourceManager = new ResourceManager();
                    var resourceContext = resourceManager.CreateResourceContext();
                    resourceContext.QualifierValues["Language"] = targetLang;

                    var dialog = new ContentDialog
                    {
                        Title = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_Title", resourceContext).ValueAsString,
                        Content = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_Content", resourceContext).ValueAsString,
                        PrimaryButtonText = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_CloseButton", resourceContext).ValueAsString,
                        SecondaryButtonText = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_PrimaryButton", resourceContext).ValueAsString,
                        DefaultButton = ContentDialogButton.Secondary,
                        XamlRoot = App.MainWindow.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Secondary)
                    {
                        AppLogService.Info("Application restart requested after language change.", LogSource.Settings);
                        CrashLogService.MarkCleanShutdown();
                        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                    }
                }
                finally
                {
                    completionSource.SetResult();
                }
            });

            await completionSource.Task;
        }
    }
}
