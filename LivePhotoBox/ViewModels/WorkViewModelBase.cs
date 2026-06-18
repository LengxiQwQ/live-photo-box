using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.ViewModels
{
    public abstract partial class WorkViewModelBase : ViewModelBase
    {
        private const string CrashLogLanguageTag = "en-US";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProcessingAllowed))]
        [NotifyPropertyChangedFor(nameof(ActionBtnText))]
        [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
        [NotifyPropertyChangedFor(nameof(CanEditInputConfiguration))]
        [NotifyPropertyChangedFor(nameof(CanEditOutputConfiguration))]
        private bool _isProcessing = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ScanButtonStyle))]
        [NotifyPropertyChangedFor(nameof(IsProcessingAllowed))]
        [NotifyPropertyChangedFor(nameof(IsNotScanning))]
        [NotifyPropertyChangedFor(nameof(CanEditInputConfiguration))]
        private bool _isScanning = false;

        [ObservableProperty]
        private bool _isPaused = false;

        [ObservableProperty]
        private double _progress = 0;

        private bool _pauseRequested = false;
        private bool _resumeRequested = false;
        protected bool _cancelledByUser = false;

        [ObservableProperty]
        private string _progressText = "0/0";

        [ObservableProperty]
        private string _actionBtnText = string.Empty;

        private ProgressBarState _progressBarState = Models.ProgressBarState.Idle;
        public Models.ProgressBarState ProgressBarState
        {
            get => _progressBarState;
            protected set
            {
                if (_progressBarState != value)
                {
                    _progressBarState = value;
                    OnPropertyChanged(nameof(ProgressBarState));
                }
            }
        }

        public bool IsNotProcessing => !IsProcessing;
        public bool IsNotScanning => !IsScanning;

        public bool CanEditInputConfiguration => !IsProcessing && !IsScanning;
        public bool CanEditOutputConfiguration => !IsProcessing;

        public virtual bool IsProcessingAllowed => true;

        private long _lastScanClickTimestamp = 0;
        private const long ScanClickDebounceMs = 200;

        protected bool TryGuardScanClick()
        {
            var now = Environment.TickCount64;
            if (now - _lastScanClickTimestamp < ScanClickDebounceMs) return false;
            _lastScanClickTimestamp = now;
            return true;
        }

        [RelayCommand]
        protected void GoToTutorial(string feature) => RequestNavigateToPage?.Invoke(this, $"Home_{feature}");

        public event EventHandler<string>? RequestNavigateToPage;
        public event EventHandler? StatusChanged;

        private string _status = string.Empty;
        private string _statusKey = string.Empty;
        private string _statusForLog = string.Empty;
        public new string Status => _status;

        protected void SetStatus(string resourceKey, params object[] args)
        {
            _statusKey = resourceKey;
            _status = ResourceService.Format(resourceKey, args);
            _statusForLog = ResourceService.FormatForLanguage(CrashLogLanguageTag, resourceKey, args);
            NotifyStatusChanged();
        }

        // ✨ 新增方法：直接注入文本，打破只能用多语言键值的局限性
        protected void SetDirectStatus(string text)
        {
            _statusKey = "CustomDirectText";
            _status = text;
            _statusForLog = text;
            NotifyStatusChanged();
        }

        protected void NotifyStatusChanged()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(SecondaryBtnText));
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditInputConfiguration));
            OnPropertyChanged(nameof(CanEditOutputConfiguration));
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public string SecondaryBtnText => !IsProcessing
            ? ResourceService.GetString("Btn_ClearList")
            : (IsPaused ? ResourceService.GetString("Btn_Resume") : ResourceService.GetString("Btn_Pause"));

        protected string StatusForLog => _statusForLog;

        private int _scanTotal;
        private int _scanProcessed;
        private WorkProgressSnapshot _pendingScanSnapshot;
        private long _lastScanUiUpdateMs;
        protected CancellationTokenSource? _scanCancellationTokenSource;
        protected bool _scanCancelledByUser = false;

        protected void BeginScanSession()
        {
            ProgressBarState = Models.ProgressBarState.Idle;
            _scanCancelledByUser = false;
            _scanProcessed = 0;
            _scanTotal = 0;
            _lastScanUiUpdateMs = 0;
            OnBeginScanSession();
        }

        protected void ApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            _scanTotal = snapshot.Total;
            _scanProcessed = snapshot.Completed;
            OnApplyScanProgress(snapshot);
            NotifyStatusChanged();
        }

        protected void FlushPendingScanProgress() => ApplyScanProgress(_pendingScanSnapshot);

        protected void CompleteScanSnapshot()
        {
            _scanProcessed = _scanTotal;
            OnCompleteScanSnapshot();
            NotifyStatusChanged();
        }

        protected IProgress<WorkProgressSnapshot> CreateScanProgressReporter()
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            return new Progress<WorkProgressSnapshot>(snapshot => EnqueueThrottledScanProgress(snapshot, dispatcher));
        }

        private void EnqueueThrottledScanProgress(WorkProgressSnapshot snapshot, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
        {
            _pendingScanSnapshot = snapshot;
            if (dispatcher == null) return;

            bool forceApply = snapshot.Total > 0 && snapshot.Completed >= snapshot.Total;
            var now = Environment.TickCount64;
            if (!forceApply && _lastScanUiUpdateMs != 0 && now - _lastScanUiUpdateMs < 100) return;

            _lastScanUiUpdateMs = now;
            var captured = snapshot;
            dispatcher.TryEnqueue(() => ApplyScanProgress(captured));
        }

        protected abstract void OnBeginScanSession();
        protected abstract void OnApplyScanProgress(WorkProgressSnapshot snapshot);
        protected abstract void OnCompleteScanSnapshot();
        public abstract override string PageStatusTag { get; }

        private CancellationTokenSource? _cancellationTokenSource;
        protected readonly ManualResetEventSlim PauseEvent = new(true);

        protected void InitializeRunState()
        {
            IsProcessing = true;
            IsPaused = false;
            PauseEvent.Set();
            OnInitializeRunState();
            ProgressBarState = Models.ProgressBarState.Processing;
        }

        protected void FinalizeRunState()
        {
            IsProcessing = false;
            IsPaused = false;

            _pauseRequested = false;
            _resumeRequested = false;

            if (_cancelledByUser)
            {
                ProgressBarState = Models.ProgressBarState.Cancelled;
                _cancelledByUser = false;
            }
            else
            {
                ProgressBarState = Progress >= 100 ? Models.ProgressBarState.Success : Models.ProgressBarState.Idle;
            }

            PauseEvent.Set();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            OnFinalizeRunState();
            NotifyStatusChanged();
        }

        protected CancellationToken GetProcessingToken()
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            return _cancellationTokenSource.Token;
        }

        protected void CancelProcessing()
        {
            _cancelledByUser = true;
            _cancellationTokenSource?.Cancel();
            PauseEvent.Set();
        }

        protected void CancelScanning()
        {
            _scanCancelledByUser = true;
            _cancelledByUser = true;
            _scanCancellationTokenSource?.Cancel();
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        protected CancellationToken GetScanningToken()
        {
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = new CancellationTokenSource();
            return _scanCancellationTokenSource.Token;
        }

        protected void CleanupTokens()
        {
            _scanCancellationTokenSource?.Cancel();
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            PauseEvent.Dispose();
        }

        protected virtual void OnScanningEnded() { }
        protected abstract void OnInitializeRunState();
        protected abstract void OnFinalizeRunState();

        protected async Task ShowEmptyQueueDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_EmptyQueue"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = ResourceService.GetString("Msg_GoToTutorial"),
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    RequestNavigateToPage?.Invoke(this, $"Home_{targetFeature}");
                }
            }
        }

        protected async Task ShowNoInputDirectoryDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString($"{targetFeature}Page_Msg_NoInputDirectoryTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString($"{targetFeature}Page_Msg_NoInputDirectory"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };
                await dialog.ShowAsync();
            }
        }

        protected async Task ShowInvalidInputDirectoryDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_InvalidInputDirectoryTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_InvalidInputDirectory"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };
                await dialog.ShowAsync();
            }
        }

        protected async Task ShowQueueNotEmptyDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_QueueNotEmptyTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_QueueNotEmpty"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };
                await dialog.ShowAsync();
            }
        }

        private static Style? _defaultButtonStyle;
        private static Style? _scanCancelButtonStyle;

        public Style ScanButtonStyle => ResolveScanButtonStyle(IsScanning);

        private static Style ResolveScanButtonStyle(bool isCancelAppearance)
        {
            EnsureScanButtonStyles();
            if (isCancelAppearance && _scanCancelButtonStyle != null) return _scanCancelButtonStyle;
            if (_defaultButtonStyle != null) return _defaultButtonStyle;
            return new Style(typeof(Button));
        }

        private static void EnsureScanButtonStyles()
        {
            if (_defaultButtonStyle != null && _scanCancelButtonStyle != null) return;
            if (Application.Current?.Resources == null) return;
            var resources = Application.Current.Resources;
            if (_defaultButtonStyle == null && resources.TryGetValue("DefaultButtonStyle", out var defaultStyle) && defaultStyle is Style dbs)
                _defaultButtonStyle = dbs;
            if (_scanCancelButtonStyle == null && resources.TryGetValue("ScanCancelButtonStyle", out var cancelStyle) && cancelStyle is Style cbs)
                _scanCancelButtonStyle = cbs;
        }

        protected void TogglePause()
        {
            if (IsPaused)
            {
                _pauseRequested = false;
                _resumeRequested = true;
                PauseEvent.Set();
            }
            else
            {
                _pauseRequested = true;
                _resumeRequested = false;
                PauseEvent.Reset();
            }
        }

        protected void CheckAndApplyPendingState()
        {
            if (_pauseRequested && !IsPaused)
            {
                IsPaused = true;
                ProgressBarState = Models.ProgressBarState.Paused;
                SetStatus("Status_Paused");
                _pauseRequested = false;
            }
            else if (_resumeRequested && IsPaused)
            {
                IsPaused = false;
                ProgressBarState = Models.ProgressBarState.Processing;
                SetStatus("Status_Resumed");
                _resumeRequested = false;
            }
        }

        protected void ApplyCancellationState()
        {
            if (_cancelledByUser)
            {
                ProgressBarState = Models.ProgressBarState.Cancelled;
                _cancelledByUser = false;
            }
        }

        protected void ClearState()
        {
            IsProcessing = false;
            IsPaused = false;
            IsScanning = false;
            ProgressBarState = Models.ProgressBarState.Idle;
            Progress = 0;
            ProgressText = "0/0";
            _scanTotal = 0;
            _scanProcessed = 0;
            AppViewModel.Instance.ResetFooterScanCounters();
            AppViewModel.Instance.NotifyFooterProperties();
            OnClearState();
            NotifyStatusChanged();
        }

        protected abstract void OnClearState();

        public void Cleanup()
        {
            CleanupTokens();
        }
    }
}