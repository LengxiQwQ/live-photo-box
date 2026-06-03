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

        private string? _latestCrashLogPath;
        private string? _latestCrashDumpPath;
        private string? _latestRecoveredCrashLogPath;

        #endregion

        #region Properties

        public override string? PageStatusTag => null;

        public bool HasCrashArtifacts => GetLatestCrashArtifactPath() != null;

        public string LastCrashFileNameText => GetLatestCrashArtifactPath() is string latestCrashArtifactPath
            ? Path.GetFileName(latestCrashArtifactPath)
            : ResourceService.GetString("SettingsPage_CrashNoCrashValue");

        #endregion

        #region Commands

        public IRelayCommand OpenCrashLogFolderActionCommand => _openCrashLogFolderActionCommand ??= new RelayCommand(OpenCrashLogFolder);
        public IAsyncRelayCommand OpenLatestCrashLogActionCommand => _openLatestCrashLogActionCommand ??= new AsyncRelayCommand(OpenLatestCrashLogAsync, () => HasCrashArtifacts);
        public IAsyncRelayCommand ExportLatestCrashLogActionCommand => _exportLatestCrashLogActionCommand ??= new AsyncRelayCommand(ExportLatestCrashLogAsync, CanExportLatestCrashLog);
        public IRelayCommand ClearCrashLogsActionCommand => _clearCrashLogsActionCommand ??= new RelayCommand(ClearCrashLogs, CanClearCrashLogs);
        public IRelayCommand GenerateTestCrashLogActionCommand => _generateTestCrashLogActionCommand ??= new RelayCommand(GenerateTestCrashLog);
        public IAsyncRelayCommand OpenIssueFeedbackActionCommand => _openIssueFeedbackActionCommand ??= new AsyncRelayCommand(OpenIssueFeedbackAsync);

        private IRelayCommand? _openCrashLogFolderActionCommand;
        private IAsyncRelayCommand? _openLatestCrashLogActionCommand;
        private IAsyncRelayCommand? _exportLatestCrashLogActionCommand;
        private IRelayCommand? _clearCrashLogsActionCommand;
        private IRelayCommand? _generateTestCrashLogActionCommand;
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
            _latestCrashLogPath = CrashLogService.GetLatestCrashLogPath();
            _latestCrashDumpPath = CrashLogService.GetLatestCrashDumpPath();
            _latestRecoveredCrashLogPath = CrashLogService.GetLatestRecoveredCrashLogPath();

            if (!string.IsNullOrWhiteSpace(_latestCrashLogPath) && !File.Exists(_latestCrashLogPath))
            {
                _latestCrashLogPath = null;
            }

            if (!string.IsNullOrWhiteSpace(_latestCrashDumpPath) && !File.Exists(_latestCrashDumpPath))
            {
                _latestCrashDumpPath = null;
            }

            if (!string.IsNullOrWhiteSpace(_latestRecoveredCrashLogPath) && !File.Exists(_latestRecoveredCrashLogPath))
            {
                _latestRecoveredCrashLogPath = null;
            }

            OpenLatestCrashLogActionCommand.NotifyCanExecuteChanged();
            ExportLatestCrashLogActionCommand.NotifyCanExecuteChanged();
            ClearCrashLogsActionCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasCrashArtifacts));
            OnPropertyChanged(nameof(LastCrashFileNameText));
        }

        private void OpenCrashLogFolder()
        {
            string logDirectory = CrashLogService.EnsureLogDirectoryPath();
            AppLogService.Info($"OpenCrashLogFolder requested. Path='{logDirectory}'", LogSource.App);
            FilePickerService.OpenFolderInExplorer(logDirectory);
        }

        private async Task OpenLatestCrashLogAsync()
        {
            string? latestCrashArtifactPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestCrashArtifactPath) || !File.Exists(latestCrashArtifactPath))
            {
                RefreshCrashLogs();
                return;
            }

            AppLogService.Info($"OpenLatestCrashArtifact requested. File='{Path.GetFileName(latestCrashArtifactPath)}'", LogSource.App);
            await FilePickerService.OpenFileAsync(latestCrashArtifactPath);
        }

        private async Task ExportLatestCrashLogAsync()
        {
            string? latestCrashArtifactPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestCrashArtifactPath) || !File.Exists(latestCrashArtifactPath))
            {
                RefreshCrashLogs();
                return;
            }

            AppLogService.Info($"ExportLatestCrashArtifact requested. File='{Path.GetFileName(latestCrashArtifactPath)}'", LogSource.App);
            await FilePickerService.ExportFileCopyAsync(latestCrashArtifactPath, Path.GetFileName(latestCrashArtifactPath));
        }

        private void ClearCrashLogs()
        {
            AppLogService.Info("ClearCrashLogs requested.", LogSource.App);
            CrashLogService.DeleteAllCrashArtifacts();
            RefreshCrashLogs();
        }

        private void GenerateTestCrashLog()
        {
            AppLogService.Info("GenerateTestCrashLog requested.", LogSource.App);
            CrashLogService.GenerateTestCrashLog();
            RefreshCrashLogs();
        }

        private async Task OpenIssueFeedbackAsync()
        {
            AppLogService.Info("OpenIssueFeedback requested.", LogSource.App);
            await FeedbackService.OpenIssuePageAsync();
        }

        private bool CanExportLatestCrashLog() => HasCrashArtifacts;

        private bool CanClearCrashLogs() => HasCrashArtifacts;

        private string? GetLatestCrashArtifactPath()
        {
            return new[] { _latestCrashLogPath, _latestCrashDumpPath, _latestRecoveredCrashLogPath }
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path!))
                .FirstOrDefault();
        }

        #endregion
    }
}
