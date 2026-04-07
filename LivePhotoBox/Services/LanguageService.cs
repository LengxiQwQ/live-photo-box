using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LanguageService
    {
        public static string GetEffectiveLanguage(int index)
        {
            if (index == 1) return "zh-Hans";
            if (index == 2) return "en-US";

            var systemLangs = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            foreach (var lang in systemLangs)
            {
                if (lang.ToLowerInvariant().StartsWith("zh")) return "zh-Hans";
            }

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
                        CrashLogService.RecordBreadcrumb("Application restart requested after language change.");
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
