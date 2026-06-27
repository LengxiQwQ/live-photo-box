/*
 * App.xaml.cs
 *
 * 应用程序入口点。继承 Microsoft.UI.Xaml.Application，负责全局初始化：
 *   - 抑制子进程崩溃弹窗（SetErrorMode）
 *   - 应用语言设置
 *   - 初始化日志系统
 *   - 硬件检测（写入日志）
 *   - 崩溃处理（CrashHandler）
 *   - 构造并激活 MainWindow
 *
 * 对应 ViewModel：无（全局单例 AppViewModel 在 App 层级持有）
 *
 * 生命周期：
 *   - 构造函数 → 环境准备 → 日志初始化 → 硬件检测 → 崩溃处理器注册
 *   - OnLaunched → 创建 MainWindow → 激活窗口
 */

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
        // 主窗口引用，供全局访问（如从 ViewModel 或子页面操作窗口）
        public static Window? MainWindow { get; private set; }

        // 当前首页横幅缓存的 BitmapImage，避免跨页面导航时重复加载
        public static BitmapImage? CachedBannerImage { get; set; }

        // 应用版本号（单一来源）。
        // 优先读取 MSIX 包清单中的版本（随发布/更新同步），
        // 未打包运行时回退到入口程序集版本。
        // 所有需要显示或写入版本号的地方统一使用此属性。
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

        // Refreshes <see cref="CachedBannerImage"/> to the given preset.
        // The home page picks this up on next render.
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

        // 从持久化设置中加载横幅图片。如果启用随机模式，则在预设中随机选择。
        // 在 SettingsViewModel 可用之前（应用启动时）及首页需要刷新时调用。
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

        // 获取当前有效的 <see cref="ElementTheme"/>。
        // 当用户选择 "Default" 时，自动检测系统主题。
        // 若主窗口尚不可用，默认返回浅色主题。
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

        // 构造函数：执行应用级初始化。
        // 包括错误模式抑制、语言设置、日志系统、硬件检测和崩溃处理器注册。
        public App()
        {
            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] App constructor started.");

            // 禁止子进程崩溃时弹出 Windows 错误报告/JIT 调试器对话框。
            // exiftool / ffmpeg 等外部工具遇到损坏文件可能触发 Win32 异常，
            // 主程序有 try-catch 兜底，不需要 OS 弹窗干扰用户。
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] SetErrorMode done, applying language...");
            ApplyLanguageSetting();

            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Initializing log service...");
            LogService.Initialize();

            // 尽早检测硬件，使摘要信息在 UI 消息前出现在日志中
            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Detecting hardware...");
            try { HardwareService.GetAvailableHardware(); }
            catch (Exception ex) { LogService.Warn($"Hardware detection failed: {ex.Message}", source: LogSource.System); }

            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Initializing crash handler...");
            CrashHandler.Initialize(this);

            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Calling InitializeComponent()...");
            InitializeComponent();

            LogService.Info("Application initialized.", LogSource.App);
            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] App constructor completed successfully.");
        }

        // 从持久化设置中读取语言索引并应用语言覆盖
        private void ApplyLanguageSetting()
        {
            int languageIndex = AppSettingsService.GetValue("LanguageIndex", 0);
            System.Diagnostics.Debug.WriteLine($"[LivePhotoBox] LanguageIndex={languageIndex}, applying override...");
            LanguageService.ApplyLanguageOverride(languageIndex);
            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Language override applied successfully.");
        }

        // 应用启动后触发，创建并激活主窗口
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            LogService.Info("Main window launch started.", LogSource.UI);
            MainWindow = new MainWindow();
            MainWindow.Activate();
            LogService.Info("Main window activated.", LogSource.UI);
        }
    }
}
