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
    public partial class AboutViewModel : ViewModelBase
    {
        #region Fields

        private string? _latestLogPath;
        private string? _latestDumpPath;

        #endregion

        #region Properties

        public override string? PageStatusTag => null;

        public bool HasCrashArtifacts => GetLatestCrashArtifactPath() != null;

        public string LastCrashFileNameText => GetLatestCrashArtifactPath() is string latestPath
            ? Path.GetFileName(latestPath)
            : ResourceService.GetString("SettingsPage_CrashNoCrashValue");

        #endregion

        #region Commands

        public IRelayCommand OpenCrashLogFolderActionCommand => _openCrashLogFolderActionCommand ??= new RelayCommand(OpenCrashLogFolder);
        public IAsyncRelayCommand OpenLatestCrashLogActionCommand => _openLatestCrashLogActionCommand ??= new AsyncRelayCommand(OpenLatestCrashLogAsync, () => HasCrashArtifacts);
        public IAsyncRelayCommand ExportLatestCrashLogActionCommand => _exportLatestCrashLogActionCommand ??= new AsyncRelayCommand(ExportLatestCrashLogAsync, CanExportLatestCrashLog);
        public IRelayCommand ClearCrashLogsActionCommand => _clearCrashLogsActionCommand ??= new RelayCommand(ClearCrashLogs, CanClearCrashLogs);
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

        private void OpenCrashLogFolder()
        {
            string logDirectory = LogService.LogDirectory;
            LogService.Info($"OpenCrashLogFolder requested. Path='{logDirectory}'", LogSource.App);
            FilePickerService.OpenFolderInExplorer(logDirectory);
        }

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

        private void ClearCrashLogs()
        {
            LogService.Info("ClearCrashLogs requested.", LogSource.App);
            LogService.DeleteAllLogFiles();
            RefreshCrashLogs();
        }

        private async Task OpenIssueFeedbackAsync()
        {
            LogService.Info("OpenIssueFeedback requested.", LogSource.App);
            await FeedbackService.OpenIssuePageAsync();
        }

        private bool CanExportLatestCrashLog() => HasCrashArtifacts;

        private bool CanClearCrashLogs() => HasCrashArtifacts;

        /// <summary>
        /// Returns the most recent log or dump file path, prioritizing the current log.
        /// </summary>
        private string? GetLatestCrashArtifactPath()
        {
            // Priority 1: current session log (or most recent if between sessions)
            string? latestLog = LogService.GetLatestLogPath();
            if (!string.IsNullOrWhiteSpace(latestLog) && File.Exists(latestLog))
            {
                return latestLog;
            }

            // Priority 2: dump file (very rare — only for native crashes)
            return new[] { _latestDumpPath }
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path!))
                .FirstOrDefault();
        }

        #endregion
    }
}
