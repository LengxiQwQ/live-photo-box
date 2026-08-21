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
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System;

namespace LivePhotoBox.Services
{
    public static class NotificationService
    {
        // 通知阈值（秒）：任务持续超过此值才弹通知。
        // 测试期 = 0 立即触发；上线后改为 120（2 分钟）。
        private const int NotificationThresholdSeconds = 0;

        // 通知激活事件 — 点击通知时触发，参数为页面标签 (Merge/Split/Repair)
        public static event Action<string?>? ActivationRequested;

        private static bool _initialized;
        private static bool _registerFailed;

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
                // 从通知参数中提取页面标签
                string? tag = null;
                if (args.Arguments.TryGetValue("action", out string? value))
                    tag = value;

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
        public static void ShowBatchCompleted(string feature, int succeeded, int failed, double elapsedSeconds)
        {
            if (_registerFailed) return;
            if (!_initialized) return;

            // 阈值判断：短任务不弹通知（用户还在原地等）
            if (elapsedSeconds < NotificationThresholdSeconds)
                return;

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

                // 设置场景为"提醒"：弹窗 + 播放系统提醒音，确保用户能感知到
                builder.SetScenario(AppNotificationScenario.Reminder);
                // 显式指定系统声音事件（Notification.Reminder → 声音方案中的"提醒"音），
                // 按事件名引用而非硬编码文件路径，用户更换声音方案时自动跟随
                builder.SetAudioEvent(AppNotificationSoundEvent.Reminder);

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
