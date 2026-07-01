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
using LivePhotoBox.Views;
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

        // 检测当前是否运行在 MSIX 打包模式下（商店版本）。
        // 商店版（打包）返回 true，便携版和安装版（非打包）返回 false。
        // 单一来源，所有需要判断打包模式的地方统一引用此属性。
        public static bool IsPackaged
        {
            get
            {
                try { _ = Windows.ApplicationModel.Package.Current; return true; }
                catch { return false; }
            }
        }

        // 获取当前部署模式的本地化资源键值。
        // 商店版 → "AboutPage_Mode_Store"
        // 安装版 → "AboutPage_Mode_Installer"（Inno Setup 会在应用目录生成 unins000.exe）
        // 便携版 → "AboutPage_Mode_Portable"
        public static string DeploymentModeResourceKey
        {
            get
            {
                if (IsPackaged) return "AboutPage_Mode_Store";

                // Inno Setup 安装版必定在应用目录下生成 unins000.exe + unins000.dat，
                // 便携版（zip 直接解压）没有这两个文件，以此区分两种非打包模式。
                string appDir = AppContext.BaseDirectory;
                if (System.IO.File.Exists(System.IO.Path.Combine(appDir, "unins000.exe")) ||
                    System.IO.File.Exists(System.IO.Path.Combine(appDir, "unins000.dat")))
                    return "AboutPage_Mode_Installer";

                return "AboutPage_Mode_Portable";
            }
        }

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

            // WebView2 默认把 UserDataFolder 放在 EXE 所在目录的 <exe>.WebView2\ 下。
            // Inno Setup 安装到 Program Files 后该目录只读，导致 EnsureCoreWebView2Async()
            // 初始化失败，更新日志 (markdown) 无法渲染。
            // 通过 WEBVIEW2_USER_DATA_FOLDER 环境变量重定向到 %LocalAppData%，
            // 便携版和安装版均可用，且必须在首次创建 WebView2 之前设置。
            try
            {
                string wv2Path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox", "WebView2");
                System.IO.Directory.CreateDirectory(wv2Path);
                Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", wv2Path);
                System.Diagnostics.Debug.WriteLine($"[LivePhotoBox] WebView2 UDF set to: {wv2Path}");
            }
            catch { /* 设置失败不影响应用启动，WebView2 会走降级方案 */ }

            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] SetErrorMode done, applying language...");
            ApplyLanguageSetting();

            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Initializing log service...");
            LogService.Initialize();

            // 尽早检测硬件（后台线程，不阻塞 UI）。
            // 硬件信息被缓存后所有页面均可用；SettingsViewModel 侧也有独立的异步加载逻辑。
            System.Diagnostics.Debug.WriteLine("[LivePhotoBox] Detecting hardware (background)...");
            try { _ = Task.Run(() => HardwareService.GetAvailableHardware()); }
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

            // 清理上次可能残留的更新临时文件（上次崩溃/中断留下的垃圾）
            Task.Run(() => UpdateService.CleanupUpdateTempFiles());

            // 非打包模式下，启动时后台静默检查更新（fire-and-forget，不阻塞 UI）
            // 仅在距上次检查 >= 3 天时触发，有新版且未被跳过时弹窗提示
            _ = StartUpdateCheckOnLaunchAsync();
        }

        /// <summary>
        /// 启动时后台更新检查。仅在非打包模式下生效，按 3 天间隔检查。
        /// 网络错误或 API 异常静默处理，不影响用户正常使用。
        /// 窗口可能需要一点时间完成渲染，延迟 2 秒再弹出更新对话框以优化体验。
        /// </summary>
        private static async Task StartUpdateCheckOnLaunchAsync()
        {
            try
            {
                // 仅非打包模式、且距上次检查 >= 3 天
                if (!UpdateService.IsUpdateEnabled)
                {
                    LogService.Debug("Startup update check: DISABLED (packaged/MSIX mode).", LogSource.App);
                    return;
                }
                if (!UpdateService.ShouldCheckForUpdate())
                {
                    LogService.Debug("Startup update check: SKIPPED (within 3-day interval).", LogSource.App);
                    return;
                }

                LogService.Info($"Startup update check: Checking for updates... (token: {(UpdateService.HasApiToken ? "yes" : "no")})", LogSource.App);

                var release = await UpdateService.FetchLatestReleaseAsync();

                if (release == null)
                {
                    LogService.Warn("Startup update check: API returned null — silent exit.", source: LogSource.App);
                    return; // API 失败不记录时间，下次启动可重试
                }

                UpdateService.RecordCheckTime();

                if (!UpdateService.IsNewerVersion(release))
                {
                    LogService.Info(
                        $"Startup update check: No new version. Current={App.AppVersion}, Latest={release.TagName}",
                        LogSource.App);
                    return;
                }

                if (UpdateService.IsVersionSkipped(release.TagName))
                {
                    LogService.Info($"Startup update check: Version {release.TagName} was skipped — silent exit.", LogSource.App);
                    return;
                }

                LogService.Info(
                    $"Startup update check: NEW VERSION {release.TagName}! Scheduling dialog...",
                    LogSource.App);

                // UI 线程弹出对话框。延迟 2 秒等主窗口完全渲染好。
                // 传入已获取的 release，不再重复请求 API（避免启动路径中两次调用浪费配额）。
                var capturedRelease = release;
                if (MainWindow?.DispatcherQueue != null)
                {
                    MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            await Task.Delay(2000);
                            if (MainWindow?.Content?.XamlRoot != null)
                            {
                                LogService.Info("Startup update check: Showing update dialog now.", LogSource.App);
                                await SettingsPage.HandleUpdateCheckResultAsync(
                                    MainWindow.Content.XamlRoot, capturedRelease);
                            }
                            else
                            {
                                LogService.Warn("Startup update check: XamlRoot is null — cannot show dialog.",
                                    source: LogSource.App);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Debug($"Startup update dialog error (non-fatal): {ex.Message}", LogSource.App);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Startup update check failed: {ex.GetType().Name}: {ex.Message}", source: LogSource.App);
            }
        }
    }
}
