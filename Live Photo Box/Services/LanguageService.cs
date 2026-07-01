using LivePhotoBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    // 多语言服务 — 管理应用界面语言的索引映射、语言覆盖切换以及切换后的重启提示。
    // 支持系统跟随模式（自动检测 Windows 显示语言）和 2 种手动指定语言（简体中文/英语）。
    // 语言切换后通过 PrimaryLanguageOverride 持久化，并提示用户重启应用生效。
    public static class LanguageService
    {
        // 通过语言索引判断有效语言是否发生变化。
        // 先将索引转换为语言标签再比较，可正确处理"跟随系统"模式。
        // previousIndex: 之前选择的索引
        // currentIndex: 当前选择的索引
        // è¿å: 有效语言发生变化返回 true
        public static bool HasEffectiveLanguageChanged(int previousIndex, int currentIndex)
        {
            return HasEffectiveLanguageChanged(GetEffectiveLanguage(previousIndex), GetEffectiveLanguage(currentIndex));
        }

        // 直接比较两个语言标签是否不同（忽略大小写）。
        // previousLanguageTag: 之前的语言标签
        // currentLanguageTag: 当前的语言标签
        // è¿å: 两个标签不同返回 true
        public static bool HasEffectiveLanguageChanged(string previousLanguageTag, string currentLanguageTag)
        {
            return !string.Equals(previousLanguageTag, currentLanguageTag, StringComparison.OrdinalIgnoreCase);
        }

        // 将语言索引转换为 BCP-47 语言标签。
        // 索引 0 表示"跟随系统"，会遍历系统首选语言列表匹配支持的语种；
        // 索引 1-2 映射到固定语言（en-US, zh-Hans）。
        // 系统语言匹配优先级：简中 > 英语。
        // index: 语言索引（0=跟随系统，1-2=固定语言）
        // index: 语言索引（0=跟随系统，1-9=固定语言）
        // è¿å: BCP-47 语言标签
        public static string GetEffectiveLanguage(int index)
        {
            // 0 = 跟随系统 (System Default)：检测当前系统 UI 语言，匹配支持语种。
            if (index == 0)
            {
                string systemLang;

                // 打包模式：走 WinRT GlobalizationPreferences（优先级列表，更精准）
                // 非打包：走 .NET CultureInfo.CurrentUICulture（无 WinRT 依赖）
                if (App.IsPackaged)
                {
                    try
                    {
                        var systemLangs = Windows.System.UserProfile.GlobalizationPreferences.Languages;
                        systemLang = systemLangs.Count > 0 ? systemLangs[0] : "en-US";
                    }
                    catch
                    {
                        systemLang = "en-US";
                    }
                }
                else
                {
                    systemLang = System.Globalization.CultureInfo.CurrentUICulture.Name;
                }

                var lowerLang = systemLang.ToLowerInvariant();
                if (lowerLang.StartsWith("zh")) return "zh-Hans";
                if (lowerLang.StartsWith("en")) return "en-US";
                return "en-US";
            }

            return index switch
            {
                1 => "en-US",
                2 => "zh-Hans",
                _ => "en-US",
            };
        }

        // 获取当前生效的语言标签。
        // 优先返回 PrimaryLanguageOverride（用户手动设置），
        // 未设置时回退到系统首选语言的第一个条目，最后兜底为 en-US。
        // è¿å: 当前语言 BCP-47 标签
        // 通过语言索引设置语言覆盖。内部转换为标签后调用 <see cref="ApplyLanguageOverride(string)"/>。
        // languageIndex: 语言索引
        public static void ApplyLanguageOverride(int languageIndex)
        {
            ApplyLanguageOverride(GetEffectiveLanguage(languageIndex));
        }

        // 通过语言标签设置 PrimaryLanguageOverride，写入后立即生效于资源加载。
        // 非打包模式下此 API 不可用，语言切换需重启应用才能完全生效。
        // languageTag: BCP-47 语言标签
        public static void ApplyLanguageOverride(string languageTag)
        {
            if (App.IsPackaged)
            {
                try
                {
                    Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = languageTag;
                }
                catch { /* 极端情况 */ }
            }
            // 非打包模式：PrimaryLanguageOverride 不可用，跳过。
            // 语言切换效果需依赖资源系统的默认行为或重启应用生效。
        }

        // 显示语言切换确认对话框，询问用户是否立即重启应用以使语言切换完全生效。
        // 对话框文本使用目标语言显示（通过 ResourceManager 指定语言 qualifier 加载对应资源）。
        // 用户选择"重启"时标记干净关闭并调用 AppInstance.Restart 重启。
        // targetLang: 目标语言标签，用于加载对应语言的对话框文本
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

                    var title = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_Title", resourceContext).ValueAsString;
                    var content = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_Content", resourceContext).ValueAsString;
                    var restartText = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_PrimaryButton", resourceContext).ValueAsString;
                    var closeText = resourceManager.MainResourceMap.GetValue("Resources/RestartDialog_CloseButton", resourceContext).ValueAsString;

                    bool restart = await DialogService.ShowDualAsync(
                        App.MainWindow.Content.XamlRoot,
                        title,
                        content,
                        primaryText: restartText,
                        closeText: closeText);
                    if (restart)
                    {
                        LogService.Info("Application restart requested after language change.", LogSource.Settings);
                        LogService.MarkCleanShutdown();
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
