using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 负责会话状态的持久化和恢复
    /// </summary>
    internal static class SessionStateManager
    {
        private const string SessionStateFileName = "crash-session.json";
        private static readonly JsonSerializerOptions SessionStateSerializerOptions = new() { WriteIndented = true };

        public static AppStateSnapshot? LoadSessionState()
        {
            try
            {
                string path = GetSessionStatePath();
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<AppStateSnapshot>(json, SessionStateSerializerOptions);
            }
            catch { return null; }
        }

        public static void SaveSessionState(AppStateSnapshot state)
        {
            string path = GetSessionStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(state, SessionStateSerializerOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        public static AppStateSnapshot StartNewSession()
        {
            var now = DateTimeOffset.Now;
            var sessionState = new AppStateSnapshot
            {
                SessionId = Guid.NewGuid().ToString("N"),
                StartedAt = now,
                LastUpdatedAt = now,
                CleanShutdown = false
            };
            SaveSessionState(sessionState);
            AppLogService.Info($"New session started: {sessionState.SessionId}", LogSource.System);
            return sessionState;
        }

        public static void MarkCleanShutdown()
        {
            try
            {
                var sessionState = LoadSessionState() ?? new AppStateSnapshot();
                sessionState.CleanShutdown = true;
                sessionState.LastUpdatedAt = DateTimeOffset.Now;
                SaveSessionState(sessionState);
                AppLogService.Info("Clean shutdown marked.", LogSource.System);
            }
            catch (Exception ex)
            {
                AppLogService.Error("Failed to mark clean shutdown", ex, LogSource.System);
            }
        }

        public static void UpdateSessionState()
        {
            try
            {
                var state = LoadSessionState() ?? new AppStateSnapshot();
                if (string.IsNullOrWhiteSpace(state.SessionId))
                    state.SessionId = Guid.NewGuid().ToString("N");
                if (state.StartedAt == default)
                    state.StartedAt = DateTimeOffset.Now;

                state.LastUpdatedAt = DateTimeOffset.Now;
                state.CleanShutdown = false;
                state.LogCount = (int)AppLogService.TotalLogCount;

                if (App.MainWindow is MainWindow window)
                {
                    var vm = window.ViewModel;
                    state.CurrentPageTag = vm.CurrentStatusPageTag ?? string.Empty;
                    state.ComboStatus = vm.Combo.Status;
                    state.SplitStatus = vm.Split.Status;
                    state.RepairStatus = vm.Repair.Status;
                    state.IsProcessing = vm.Combo.IsProcessing;
                    state.IsPaused = vm.Combo.IsPaused;
                    state.ComboTaskCount = vm.Combo.Tasks.Count;
                    state.SplitTaskCount = vm.Split.Tasks.Count;
                    state.ComboProgress = vm.Combo.ComboProgress;
                    state.SplitProgress = vm.Split.Progress;
                    state.ComboInputDir = vm.Combo.InputDirectory;
                    state.ComboOutputDir = vm.Combo.OutputDirectory;
                    state.SplitInputDir = vm.Split.InputDirectory;
                    state.SplitOutputDir = vm.Split.OutputDirectory;
                }

                var recentLogs = AppLogService.GetRecentLogs(30);
                state.RecentMessages = recentLogs.Select(l => l.FormattedMessage).ToList();

                SaveSessionState(state);
            }
            catch { }
        }

        public static void RecoverPreviousSessionIfNeeded()
        {
            try
            {
                var sessionState = LoadSessionState();
                if (sessionState == null || sessionState.CleanShutdown) return;
                if (!string.IsNullOrWhiteSpace(CrashLogService.GetPendingCrashLogPath())) return;

                AppLogService.Warn("Previous session did not shutdown cleanly, recovering...", source: LogSource.System);
                string? recoveredPath = CrashLogWriter.WriteRecoveredCrashLog(sessionState);
                CrashLogService.MarkPendingCrash(recoveredPath);
            }
            catch (Exception ex)
            {
                AppLogService.Error("Failed to recover previous session", ex, LogSource.System);
            }
        }

        private static string GetSessionStatePath() => Path.Combine(GetLogDirectory(), SessionStateFileName);

        private static string GetLogDirectory()
        {
            try
            {
                string localPath = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                return Path.Combine(localPath, "Logs");
            }
            catch
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox", "Logs");
            }
        }
    }
}
