// <copyright file="AboutViewModel.cs" company="Live Photo Box">
// Copyright (c) Live Photo Box. All rights reserved.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.ViewModels
{
    // 关于页面的 ViewModel，对应 AboutPage。
    // 管理崩溃日志的查看、导出、清除以及反馈跳转等操作。
    public partial class AboutViewModel : ViewModelBase
    {
        #region Fields

        // 上一次会话的日志文件路径（已校验存在性）。
        private string? _latestLogPath;

        // 上一次会话的转储文件路径（已校验存在性）。
        private string? _latestDumpPath;

        #endregion

        #region Properties

        // <inheritdoc/>
        public override string? PageStatusTag => null;

        // 是否存在可用的崩溃产物（日志或转储文件）。
        public bool HasCrashArtifacts => GetLatestCrashArtifactPath() != null;

        // 上一次崩溃文件的显示名称，无可用时显示"无崩溃记录"提示。
        public string LastCrashFileNameText => GetLatestCrashArtifactPath() is string latestPath
            ? Path.GetFileName(latestPath)
            : ResourceService.GetString("SettingsPage_CrashNoCrashValue");

        #endregion

        #region Commands

        // 打开崩溃日志文件夹的命令。
        public IRelayCommand OpenCrashLogFolderActionCommand => _openCrashLogFolderActionCommand ??= new RelayCommand(OpenCrashLogFolder);

        // 打开最新崩溃日志文件的命令。
        public IAsyncRelayCommand OpenLatestCrashLogActionCommand => _openLatestCrashLogActionCommand ??= new AsyncRelayCommand(OpenLatestCrashLogAsync, () => HasCrashArtifacts);

        // 导出最新崩溃日志文件到用户指定位置的命令。
        public IAsyncRelayCommand ExportLatestCrashLogActionCommand => _exportLatestCrashLogActionCommand ??= new AsyncRelayCommand(ExportLatestCrashLogAsync, CanExportLatestCrashLog);

        // 清除所有崩溃日志文件的命令。
        public IRelayCommand ClearCrashLogsActionCommand => _clearCrashLogsActionCommand ??= new RelayCommand(ClearCrashLogs, CanClearCrashLogs);

        // 在浏览器中打开 GitHub Issues 反馈页面的命令。
        public IAsyncRelayCommand OpenIssueFeedbackActionCommand => _openIssueFeedbackActionCommand ??= new AsyncRelayCommand(OpenIssueFeedbackAsync);

        private IRelayCommand? _openCrashLogFolderActionCommand;
        private IAsyncRelayCommand? _openLatestCrashLogActionCommand;
        private IAsyncRelayCommand? _exportLatestCrashLogActionCommand;
        private IRelayCommand? _clearCrashLogsActionCommand;
        private IAsyncRelayCommand? _openIssueFeedbackActionCommand;

        #endregion

        #region Constructor

        public AboutViewModel()
        {
            RefreshCrashLogs();
        }

        #endregion

        #region Methods

        // 刷新崩溃日志列表，重新检测日志文件和转储文件的存在性，
        // 并更新相关命令的可执行状态及绑定属性。
        public void RefreshCrashLogs()
        {
            _latestLogPath = LogService.GetLatestLogPath();
            _latestDumpPath = LogService.GetLatestDumpPath();

            if (!string.IsNullOrWhiteSpace(_latestLogPath) && !File.Exists(_latestLogPath))
            {
                _latestLogPath = null;
            }

            if (!string.IsNullOrWhiteSpace(_latestDumpPath) && !File.Exists(_latestDumpPath))
            {
                _latestDumpPath = null;
            }

            OpenLatestCrashLogActionCommand.NotifyCanExecuteChanged();
            ExportLatestCrashLogActionCommand.NotifyCanExecuteChanged();
            ClearCrashLogsActionCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasCrashArtifacts));
            OnPropertyChanged(nameof(LastCrashFileNameText));
        }

        // 在文件资源管理器中打开日志文件夹。
        private void OpenCrashLogFolder()
        {
            string logDirectory = LogService.LogDirectory;
            LogService.Info($"OpenCrashLogFolder requested. Path='{logDirectory}'", LogSource.App);
            FilePickerService.OpenFolderInExplorer(logDirectory);
        }

        // 用默认程序打开上一次的崩溃日志文件。
        private async Task OpenLatestCrashLogAsync()
        {
            string? latestPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestPath) || !File.Exists(latestPath))
            {
                RefreshCrashLogs();
                return;
            }

            LogService.Info($"OpenLatestCrashArtifact requested. File='{Path.GetFileName(latestPath)}'", LogSource.App);
            await FilePickerService.OpenFileAsync(latestPath);
        }

        // 将最新崩溃日志文件导出到用户指定的位置。
        private async Task ExportLatestCrashLogAsync()
        {
            string? latestPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestPath) || !File.Exists(latestPath))
            {
                RefreshCrashLogs();
                return;
            }

            LogService.Info($"ExportLatestCrashArtifact requested. File='{Path.GetFileName(latestPath)}'", LogSource.App);
            await FilePickerService.ExportFileCopyAsync(latestPath, Path.GetFileName(latestPath));
        }

        // 清除所有日志文件并刷新状态。
        private void ClearCrashLogs()
        {
            LogService.Info("ClearCrashLogs requested.", LogSource.App);
            LogService.DeleteAllLogFiles();
            RefreshCrashLogs();
        }

        // 在默认浏览器中打开 GitHub Issues 反馈页面。
        private async Task OpenIssueFeedbackAsync()
        {
            LogService.Info("OpenIssueFeedback requested.", LogSource.App);
            await FeedbackService.OpenIssuePageAsync();
        }

        // 是否有崩溃产物可导出。
        private bool CanExportLatestCrashLog() => HasCrashArtifacts;

        // 是否有崩溃产物可清除。
        private bool CanClearCrashLogs() => HasCrashArtifacts;

        // Returns the previous session's log file (not the currently active one).
        // Falls back to the most recent non-current log, then to dump file.
        private string? GetLatestCrashArtifactPath()
        {
            // Priority 1: PreviousLogPath — set during LogService init to the previous session's file
            string? previousLog = LogService.PreviousLogPath;
            if (!string.IsNullOrWhiteSpace(previousLog) && File.Exists(previousLog))
            {
                return previousLog;
            }

            // Priority 2: any old log that isn't the current active one
            string? currentLog = LogService.CurrentLogPath;
            string logDir = LogService.LogDirectory;
            if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            {
                var logs = Directory.GetFiles(logDir, "app-*.log")
                    .Where(f => !string.Equals(f, currentLog, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                if (logs.Count > 0)
                    return logs[0];
            }

            // Priority 3: dump file (very rare — only for native crashes)
            return new[] { _latestDumpPath }
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path!))
                .FirstOrDefault();
        }

        #endregion
    }
}
