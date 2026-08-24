/*
 * NotificationService.cs
 *
 * 系统通知服务 — 任务队列完成后弹出 Windows Toast 通知。
 *
 *   - 使用 Windows App SDK 的 AppNotificationManager（WinAppSDK 1.8 内建）
 *   - 商店版（MSIX）开箱即用；Inno Setup 安装版启动时 Register() 即可；
 *     便携版静默降级，应用内弹窗兜底
 *   - 内置阈值判断（测试期 0 秒，后续改 120 秒）
 */

using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    public static class NotificationService
    {
        // 通知激活事件 — 点击通知时触发，参数为页面标签 (Merge/Split/Repair)
        public static event Action<string?>? ActivationRequested;

        // 通知"打开输出文件夹"按钮事件 — 点击按钮时触发，参数为输出目录路径
        public static event Action<string?>? OpenOutputRequested;

        private static bool _initialized;
        private static bool _registerFailed;
        private static IntPtr _windowHandle = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // 设置应用主窗口句柄，用于前台检测。
        public static void SetWindowHandle(IntPtr handle) => _windowHandle = handle;

        // 初始化通知服务 — 注册 AUMID 并挂载激活事件。
        // 商店版自动注册；非打包版调用 Register() 写注册表；
        // 便携版无 Start Menu 链接会失败，静默跳过。
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // 必须先挂事件再 Register：WinAppSDK 要求事件处理器先于 Register() 存在，
                // 否则 Register() 抛"Must register event handlers before calling Register()"。
                AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
                AppNotificationManager.Default.Register();
                LogService.Info("Notification service registered successfully.", LogSource.App);
            }
            catch (Exception ex)
            {
                _registerFailed = true;
                LogService.Debug($"Notification service registration skipped (portable mode?): {ex.Message}", LogSource.App);
            }
        }

        // 通知被点击时，从激活参数中提取页面标签并触发事件。
        private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            try
            {
                // 从通知参数中提取操作类型和页面标签
                string? tag = null;
                if (args.Arguments.TryGetValue("action", out string? value))
                    tag = value;

                // 如果用户点击的是"打开输出文件夹"按钮，触发目录打开事件
                if (tag == "open_output")
                {
                    string? outputDir = null;
                    if (args.Arguments.TryGetValue("outputDir", out string? dir))
                        outputDir = dir;

                    LogService.Debug($"Notification open output folder: {outputDir}", LogSource.App);
                    OpenOutputRequested?.Invoke(outputDir);
                    return;
                }

                LogService.Debug($"Notification activated: {tag}", LogSource.App);
                ActivationRequested?.Invoke(tag);
            }
            catch (Exception ex)
            {
                LogService.Debug($"Notification activation handler error: {ex.Message}", LogSource.App);
            }
        }

        // 显示批量处理完成的系统通知。
        // 仅当任务持续时间超过阈值时触发。
        public static void ShowBatchCompleted(string feature, int succeeded, int failed, double elapsedSeconds, string? outputDir = null)
        {
            if (_registerFailed) return;
            if (!_initialized) return;

            // 通知频率设置（来自设置页"通知与声音"）：0=每次任务都通知，1=>15 秒，2=>30 秒，3=>1 分钟，4=>2 分钟，5=>5 分钟，6=永不通知。
            int frequencyIndex = AppSettingsService.GetValue("NotificationFrequencyIndex", 0);
            int thresholdSeconds = frequencyIndex switch
            {
                1 => 15,
                2 => 30,
                3 => 60,
                4 => 120,
                5 => 300,
                6 => int.MaxValue, // 永不显示通知
                _ => 0
            };

            // 阈值判断：短任务不弹通知（用户还在原地等）
            if (elapsedSeconds < thresholdSeconds)
                return;

            // "仅后台通知"开关（设置页"通知与声音"）：开启后，应用在前台时不弹通知。
            // 默认关闭（false）= 不管在前台后台都通知。
            if (AppSettingsService.GetValue("IsBackgroundOnlyNotification", false)
                && IsAppInForeground())
            {
                LogService.Debug("Notification suppressed: app is in foreground (background-only mode)", LogSource.App);
                return;
            }

            try
            {
                // 构造通知正文
                string body = failed > 0
                    ? ResourceService.Format("Notif_BatchDone_Body_WithFailed", feature, succeeded, failed, FormatElapsed(elapsedSeconds))
                    : ResourceService.Format("Notif_BatchDone_Body", feature, succeeded, FormatElapsed(elapsedSeconds));

                string title = ResourceService.GetString("Notif_BatchDone_Title");

                // 构建 toast XML 负载，附带页面标签参数（点击导航用）
                string? pageTag = feature switch
                {
                    _ when feature == ResourceService.GetString("Notif_Feature_Merge") => "Merge",
                    _ when feature == ResourceService.GetString("Notif_Feature_Split") => "Split",
                    _ when feature == ResourceService.GetString("Notif_Feature_Repair") => "Repair",
                    _ => null
                };

                var builder = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(body);

                // 设置场景为"提醒"：弹窗更持久、优先级更高，确保用户能感知到
                builder.SetScenario(AppNotificationScenario.Reminder);

                // 0=邮件（默认），1=通知铃声，2=日历，3=即时消息，4=闹钟
                int soundIndex = AppSettingsService.GetValue("NotificationSoundIndex", 0);
                var soundEvent = soundIndex switch
                {
                    0 => AppNotificationSoundEvent.Mail,
                    1 => AppNotificationSoundEvent.Default,
                    2 => AppNotificationSoundEvent.Reminder,
                    3 => AppNotificationSoundEvent.IM,
                    4 => AppNotificationSoundEvent.Alarm,
                    _ => AppNotificationSoundEvent.Mail,
                };
                builder.SetAudioEvent(soundEvent);

                // 添加"打开输出文件夹"按钮（如果提供了输出目录）
                if (!string.IsNullOrWhiteSpace(outputDir))
                {
                    builder.AddButton(new AppNotificationButton(ResourceService.GetString("Notif_OpenOutputFolder"))
                        .AddArgument("action", "open_output")
                        .AddArgument("outputDir", outputDir));
                }

                // 设置激活参数（点击通知时传递页面标签）
                if (pageTag != null)
                    builder.AddArgument("action", pageTag);

                var notification = builder.BuildNotification();
                AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                LogService.Debug($"Failed to show notification: {ex.Message}", LogSource.App);
            }
        }

        // 根据声音设置索引返回系统声音方案中对应事件名（用于试听读取 wav 路径）。
        private static string GetSoundEventName(int soundIndex) => soundIndex switch
        {
            0 => "Notification.Mail",
            1 => "Notification.Default",
            2 => "Notification.Reminder",
            3 => "Notification.IM",
            4 => "Notification.Looping.Alarm",
            _ => "Notification.Mail",
        };

        // 试听用：读取当前系统声音方案为指定事件配置的音频文件路径（跟随用户声音方案，不硬编码）。
        // 事件被设为"无"或读取失败时返回 null。
        public static string? GetSoundEventWavPath(int soundIndex)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"AppEvents\Schemes\Apps\.Default\{GetSoundEventName(soundIndex)}\.current");
                string? path = key?.GetValue(null) as string;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch (Exception ex)
            {
                LogService.Debug($"Failed to read sound event path: {ex.Message}", LogSource.App);
                return null;
            }
        }

        // 检查应用窗口当前是否在前台（拥有焦点）。
        // 比较当前前台窗口句柄与已注册的主窗口句柄。
        private static bool IsAppInForeground()
        {
            if (_windowHandle == IntPtr.Zero) return false;
            IntPtr foreground = GetForegroundWindow();
            return foreground == _windowHandle;
        }

        // 格式化用时显示（秒 → 友好可读）
        private static string FormatElapsed(double seconds)
        {
            if (seconds < 60)
                return ResourceService.Format("Time_Seconds", (int)seconds);

            int minutes = (int)(seconds / 60);
            int secs = (int)(seconds % 60);
            return ResourceService.Format("Time_MinutesSeconds", minutes, secs);
        }
    }
}
