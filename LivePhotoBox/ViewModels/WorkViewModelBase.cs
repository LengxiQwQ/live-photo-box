/*
 * WorkViewModelBase.cs
 *
 * 工作流型页面（拆分/修复/合成）的抽象基类。
 *
 *   - 封装扫描、处理、暂停、取消、进度报告等通用生命周期
 *   - 提供按钮文本、进度条状态、对话框显示等公用逻辑
 *   - 子类需实现 OnInitializeRunState / OnFinalizeRunState / OnClearState 等方法
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.ViewModels
{
    public abstract partial class WorkViewModelBase : ViewModelBase
    {
        // 扫描期间定时刷新扫描用时（500ms 间隔）。
        // 进度快照并非连续到达（枚举完成前、慢步骤无快照时），
        // 没有该定时器的话扫描用时会在等待期间停住，直到下一个进度快照才更新。
        private readonly DispatcherTimer _scanElapsedTimer;

        protected WorkViewModelBase()
        {
            _scanElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _scanElapsedTimer.Tick += (_, _) => OnPropertyChanged("ElapsedTimeText");
        }

        // 崩溃日志强制使用的语言标签（英语），确保崩溃信息可读。
        private const string CrashLogLanguageTag = "en-US";

        // 是否正在处理中。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProcessingAllowed))]
        [NotifyPropertyChangedFor(nameof(ActionBtnText))]
        [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
        [NotifyPropertyChangedFor(nameof(CanEditInputConfiguration))]
        [NotifyPropertyChangedFor(nameof(CanEditOutputConfiguration))]
        private bool _isProcessing = false;

        // 是否正在扫描中。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ScanButtonStyle))]
        [NotifyPropertyChangedFor(nameof(IsProcessingAllowed))]
        [NotifyPropertyChangedFor(nameof(IsNotScanning))]
        [NotifyPropertyChangedFor(nameof(CanEditInputConfiguration))]
        private bool _isScanning = false;

        // 是否处于暂停状态。
        [ObservableProperty]
        private bool _isPaused = false;

        // 当前进度（0.0–100.0）。
        [ObservableProperty]
        private double _progress = 0;

        // 是否请求了暂停（等待工作线程完成当前任务后进入暂停）。
        private bool _pauseRequested = false;
        // 是否请求了恢复。
        private bool _resumeRequested = false;
        // 是否正在暂停过渡中（工作线程执行完当前任务后才真正暂停）。
        protected bool _isPausing = false;
        // 是否由用户主动取消（而非自然完成）。
        protected bool _cancelledByUser = false;
        // 页面正在关闭清理中，跳过部分 UI 更新以避免操作已销毁的 XamlRoot。
        protected bool _isCleaningUp = false;
        // 当前活跃的工作线程数，用于判断是否可以安全暂停。
        protected int _activeWorkerCount;

        // 扫描用时秒表：扫描开始 Restart，扫描结束（含取消/异常）Stop 并保留冻结值，
        // 供子类在底部状态栏显示"扫描用时"；开始处理后会切换到处理用时。
        protected readonly Stopwatch ScanStopwatch = new();

        // 是否由用户手动停止（而非自然完成）。页面可据此决定是否跳过完成时的滚动。
        public bool WasStoppedByUser => _cancelledByUser;

        // 进度文本，如"5/100"。
        [ObservableProperty]
        private string _progressText = "0/0";

        // 主操作按钮文本的后备字段。
        private string _actionBtnText = string.Empty;
        // 主操作按钮的文本（扫描/取消/开始处理/停止等），由子类覆盖。
        public virtual string ActionBtnText
        {
            get => _actionBtnText;
            protected set => SetProperty(ref _actionBtnText, value);
        }

        // 进度条状态的后备字段。
        private ProgressBarState _progressBarState = Models.ProgressBarState.Idle;
        // 进度条状态（空闲/处理中/暂停/成功/失败/取消）。
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

        // 是否不在处理中（取反）。
        public bool IsNotProcessing => !IsProcessing;
        // 是否不在扫描中（取反）。
        public bool IsNotScanning => !IsScanning;

        // 队列是否为空（用于显示空状态占位提示）。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EmptyQueueVisibility))]
        private bool _isQueueEmpty = true;

        // 空队列占位可见性：队列为空时 Visible，否则 Collapsed。
        public Visibility EmptyQueueVisibility => IsQueueEmpty ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 由子类在 Tasks 集合变更后调用，同步更新空队列状态。
        /// </summary>
        protected void UpdateIsQueueEmpty(int taskCount)
        {
            IsQueueEmpty = taskCount == 0;
        }

        // 输入配置是否可编辑（处理中和扫描中不可编辑：扫描正在使用输入目录，禁止改动）。
        public bool CanEditInputConfiguration => !IsProcessing && !IsScanning;
        // 输出配置是否可编辑（处理中不可编辑；扫描中是否可编辑由子类决定）。
        // 默认扫描中锁定，合成页覆写为仅处理中锁定，允许扫描时改输出目录。
        public virtual bool CanEditOutputConfiguration => !IsProcessing && !IsScanning;

        // 是否可以开始处理（默认 true，子类可重写限制）。
        public virtual bool IsProcessingAllowed => true;

        // 上次扫描按钮点击的时间戳（防抖用）。
        private long _lastScanClickTimestamp = 0;
        // 扫描按钮点击防抖间隔（毫秒）。
        private const long ScanClickDebounceMs = 200;

        // 扫描按钮点击防抖：200ms 内重复点击只生效一次。
        protected bool TryGuardScanClick()
        {
            var now = Environment.TickCount64;
            if (now - _lastScanClickTimestamp < ScanClickDebounceMs) return false;
            _lastScanClickTimestamp = now;
            return true;
        }

        // 导航到指定功能的教程页面。
        [RelayCommand]
        protected void GoToTutorial(string feature) => RequestNavigateToPage?.Invoke(this, $"Home_{feature}");

        // 页面导航请求事件（参数为目标页标识）。
        public event EventHandler<string>? RequestNavigateToPage;
        // 状态文本变更事件。
        public event EventHandler? StatusChanged;

        // 当前状态文本的后备字段。
        private string _status = string.Empty;
        // 当前状态文本的多语言资源键。
        private string _statusKey = string.Empty;
        // 用于日志的状态文本（始终使用英语以避免乱码）。
        private string _statusForLog = string.Empty;
        // 当前状态文本（覆盖 ViewModelBase.Status）。
        public new string Status => _status;

        protected void SetStatus(string resourceKey, params object[] args)
        {
            _statusKey = resourceKey;
            _status = ResourceService.Format(resourceKey, args);
            _statusForLog = ResourceService.FormatForLanguage(CrashLogLanguageTag, resourceKey, args);
            NotifyStatusChanged();
        }

        // 直接注入文本，打破只能通过多语言键值设置状态文本的限制。
        protected void SetDirectStatus(string text)
        {
            _statusKey = "CustomDirectText";
            _status = text;
            _statusForLog = text;
            NotifyStatusChanged();
        }

        // 在当前状态文本后面追加提示，用竖线分隔。
        // 用于在不打断现有状态（如"正在扫描..."）的前提下叠报警告。
        protected void AppendDirectStatus(string text)
        {
            _status = string.IsNullOrEmpty(_status) ? text : $"{_status} | {text}";
            _statusForLog = _status;
            NotifyStatusChanged();
        }

        protected void NotifyStatusChanged()
        {
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(SecondaryBtnText));
            OnPropertyChanged(nameof(SecondaryBtnGlyph));
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditInputConfiguration));
            OnPropertyChanged(nameof(CanEditOutputConfiguration));
            OnPropertyChanged("ElapsedTimeText");
            OnPropertyChanged("FooterStatusText");
            OnPropertyChanged(nameof(IsScanIndeterminate));
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        // 次要按钮文本：处理中为"暂停/继续"，空闲时为"清空列表"。
        public string SecondaryBtnText => !IsProcessing
            ? ResourceService.GetString("Btn_ClearList")
            : (_isPausing ? ResourceService.GetString("Btn_Pausing")
               : (IsPaused ? ResourceService.GetString("Btn_Resume") : ResourceService.GetString("Btn_Pause")));

        // 次要按钮图标：随状态切换 —— 空闲: 垃圾桶 / 暂停: 暂停⏸ / 继续: 播放▶
        public string SecondaryBtnGlyph => !IsProcessing
            ? ""                           // Delete (clear list)
            : (_isPausing || !IsPaused
                ? ""                        // Pause ⏸
                : "");                      // Play ▶ (resume)

        // 用于日志的状态文本（英语）。
        protected string StatusForLog => _statusForLog;

        #region Scan Progress Management

        // 扫描总文件数。
        private int _scanTotal;
        // 已扫描处理数。
        private int _scanProcessed;
        // 待应用的扫描进度快照（用于节流合并）。
        private WorkProgressSnapshot _pendingScanSnapshot;
        // 上次 UI 更新时间戳（用于节流）。
        private long _lastScanUiUpdateMs;
        // 扫描取消 TokenSource。
        protected CancellationTokenSource? _scanCancellationTokenSource;
        // 扫描是否由用户取消。
        protected bool _scanCancelledByUser = false;

        // 开始一次扫描会话，重置进度状态并调用子类钩子。
        // 注意：ProgressBarState 已由 OnIsScanningChanged(true) 设为 Scanning，此处不覆盖。
        protected void BeginScanSession()
        {
            _scanCancelledByUser = false;
            _scanProcessed = 0;
            _scanTotal = 0;
            _lastScanUiUpdateMs = 0;
            ScanStopwatch.Restart();
            _scanElapsedTimer.Start();
            OnBeginScanSession();
        }

        // 应用扫描进度快照，更新 UI。
        protected void ApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            _scanTotal = snapshot.Total;
            _scanProcessed = snapshot.Completed;
            OnApplyScanProgress(snapshot);
            NotifyStatusChanged();
        }

        // 刷新累积的待处理进度快照到 UI。
        protected void FlushPendingScanProgress() => ApplyScanProgress(_pendingScanSnapshot);

        // 完成扫描快照，标记 100%。
        protected void CompleteScanSnapshot()
        {
            _scanProcessed = _scanTotal;
            OnCompleteScanSnapshot();
            NotifyStatusChanged();
        }

        // 创建扫描进度报告器（自动节流到 UI 线程）。
        protected IProgress<WorkProgressSnapshot> CreateScanProgressReporter()
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            return new Progress<WorkProgressSnapshot>(snapshot => EnqueueThrottledScanProgress(snapshot, dispatcher));
        }

        // 将扫描进度快照排队到 UI 线程（节流：最高每 100ms 一次，完成时强制刷新）。
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

        // 扫描会话开始时的子类钩子。
        protected abstract void OnBeginScanSession();
        // 应用扫描进度时的子类钩子。
        protected abstract void OnApplyScanProgress(WorkProgressSnapshot snapshot);
        // 扫描快照完成时的子类钩子。
        protected abstract void OnCompleteScanSnapshot();
        // 页面导航栏状态标签（子类实现）。
        public abstract override string PageStatusTag { get; }

        #endregion

        #region Processing State Management

        // 处理取消 TokenSource。
        private CancellationTokenSource? _cancellationTokenSource;
        // 暂停/恢复信号量（Reset 时工作线程阻塞在 Wait）。
        protected readonly ManualResetEventSlim PauseEvent = new(true);

        // 初始化运行状态，标记 IsProcessing=true，调用子类钩子。
        protected void InitializeRunState()
        {
            IsProcessing = true;
            IsPaused = false;
            _isPausing = false;
            PauseEvent.Set();
            OnInitializeRunState();
            ProgressBarState = Models.ProgressBarState.Processing;
        }

        // 结束运行状态，根据完成情况设置进度条颜色，释放资源。
        protected void FinalizeRunState()
        {
            IsProcessing = false;
            IsPaused = false;

            _pauseRequested = false;
            _resumeRequested = false;
            _isPausing = false;

            // 在重置 _cancelledByUser 之前调用，让子类能检测到取消状态
            OnFinalizeRunState();

            // 关闭中：跳过 UI 状态更新，只做资源释放
            if (!_isCleaningUp)
            {
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
                NotifyStatusChanged();
            }
            else
            {
                // 关闭流程：先 Set 再 Dispose，保证不会在 Dispose 后还被调用。
                // CleanupTokens() 可能已经在 UI 线程调用了 Dispose，
                // 这里做防御性捕获。
                PauseEvent.Set();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                try { PauseEvent.Dispose(); } catch (ObjectDisposedException) { }
            }
        }

        // 获取或创建新的处理取消 Token。
        protected CancellationToken GetProcessingToken()
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            return _cancellationTokenSource.Token;
        }

        // 取消处理（标记用户取消、释放信号量、触发 Token 取消）。
        protected void CancelProcessing()
        {
            _cancelledByUser = true;
            _isPausing = false;
            _cancellationTokenSource?.Cancel();
            PauseEvent.Set();
        }

        // 取消扫描。
        protected void CancelScanning()
        {
            _scanCancelledByUser = true;
            _cancelledByUser = true;
            _scanCancellationTokenSource?.Cancel();
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        // 获取或创建新的扫描取消 Token。
        protected CancellationToken GetScanningToken()
        {
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = new CancellationTokenSource();
            return _scanCancellationTokenSource.Token;
        }

        // 清理所有 CancellationTokenSource 和暂停信号量（页面关闭时调用）。
        // 注意：FinalizeRunState() 可能在另一个线程上并发执行（中途关闭场景），
        // 因此 PauseEvent.Dispose() 两边都加 try-catch 防御 ObjectDisposedException。
        protected void CleanupTokens()
        {
            _isCleaningUp = true;
            _scanCancellationTokenSource?.Cancel();
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            PauseEvent.Set();
            // PauseEvent 包裹原生 WaitHandle。正常路径（处理已完成）FinalizeRunState
            // 不负责释放它，由这里 Dispose；中途关闭路径 FinalizeRunState 也会尝试
            // Dispose，两边都防御 ObjectDisposedException。
            try { PauseEvent.Dispose(); } catch (ObjectDisposedException) { }
        }

        #endregion

        // 扫描结束时的子类钩子。
        // 默认冻结扫描用时：保持"扫描用时 mm:ss"显示，直到用户开始处理时切换到处理用时。
        protected virtual void OnScanningEnded()
        {
            ScanStopwatch.Stop();
            _scanElapsedTimer.Stop();
        }
        // 初始化运行状态时的子类钩子（子类在此设置进度计数等）。
        protected abstract void OnInitializeRunState();
        // 结束运行状态时的子类钩子（子类在此输出完成统计日志等）。
        protected abstract void OnFinalizeRunState();

        // 每个页面自己提供的"处理中"多语言资源键，恢复暂停时回到处理中文字。
        protected abstract string ProcessingStatusKey { get; }

        // 完整的处理中状态文本（含硬件加速/协议后缀）。子类可覆盖追加额外信息。
        protected virtual string ProcessingStatusText => ResourceService.Format(ProcessingStatusKey);

        // 返回硬件加速后缀文本，如 " | NVENC hardware acceleration" / " | CPU 软件编码"。
        // 根据 Settings.SelectedHardware 自动判断。
        protected static string GetHardwareSuffix()
        {
            var hw = AppViewModel.Instance?.Settings?.SelectedHardware;
            if (hw == null) return string.Empty;

            if (hw.Type == HardwareService.HardwareType.Gpu && hw.IsHardwareEncodingSupported)
            {
                string protocol = GetEncoderProtocolName(hw.FfmpegEncoder);
                return ResourceService.Format("RepairPage_HardwareGpu", protocol);
            }

            return ResourceService.GetString("RepairPage_HardwareCpu");
        }

        // 根据编码器名称返回协议名（NVENC / AMF / QSV / VAAPI / GPU）。
        private static string GetEncoderProtocolName(string? encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return "GPU";
            string lower = encoder.ToLowerInvariant();
            if (lower.Contains("nvenc")) return "NVENC";
            if (lower.Contains("amf")) return "AMF";
            if (lower.Contains("qsv")) return "QSV";
            if (lower.Contains("vaapi")) return "VAAPI";
            return "GPU";
        }

        // 显示空队列对话框，提示用户先扫描添加任务。
        protected async Task ShowEmptyQueueDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var xamlRoot = App.MainWindow.Content.XamlRoot;
                var chosenPrimary = await DialogService.ShowDualAsync(
                    xamlRoot,
                    ResourceService.GetString("Msg_EmptyQueueTitle"),
                    ResourceService.GetString("Msg_EmptyQueue"),
                    primaryText: ResourceService.GetString("Msg_GoToTutorial"),
                    closeText: ResourceService.GetString("Msg_GotIt"));
                if (chosenPrimary)
                    RequestNavigateToPage?.Invoke(this, $"Home_{targetFeature}");
            }
        }

        // 显示"未选择输入目录"对话框。
        protected async Task ShowNoInputDirectoryDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                await DialogService.ShowSingleAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString($"{targetFeature}Page_Msg_NoInputDirectoryTitle"),
                    ResourceService.GetString($"{targetFeature}Page_Msg_NoInputDirectory"),
                    ResourceService.GetString("Msg_GotIt"));
            }
        }

        // 显示"未设置输出目录"对话框。
        protected async Task ShowNoOutputDirectoryDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                await DialogService.ShowSingleAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString($"{targetFeature}Page_Msg_NoOutputDirectoryTitle"),
                    ResourceService.GetString($"{targetFeature}Page_Msg_NoOutputDirectory"),
                    ResourceService.GetString("Msg_GotIt"));
            }
        }

        // 显示"输入目录无效或不存在"对话框。
        protected async Task ShowInvalidInputDirectoryDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                await DialogService.ShowSingleAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString("Msg_InvalidInputDirectoryTitle"),
                    ResourceService.GetString("Msg_InvalidInputDirectory"),
                    ResourceService.GetString("Msg_GotIt"));
            }
        }

        // 显示"队列不为空，请先清空"对话框。
        protected async Task ShowQueueNotEmptyDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                await DialogService.ShowSingleAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString("Msg_QueueNotEmptyTitle"),
                    ResourceService.GetString("Msg_QueueNotEmpty"),
                    ResourceService.GetString("Msg_GotIt"));
            }
        }

        // 显示"另一个页面正在处理中"对话框。
        // 这是所有验证中优先级最低的一项：在其他检查（空目录/输出目录等）通过之后才检查。
        protected async Task ShowOtherPageProcessingDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                await DialogService.ShowSingleAsync(
                    App.MainWindow.Content.XamlRoot,
                    ResourceService.GetString("Msg_OtherPageProcessingTitle"),
                    ResourceService.GetString("Msg_OtherPageProcessing"),
                    ResourceService.GetString("Msg_GotIt"));
            }
        }

        // 缓存的默认按钮样式。
        private static Style? _defaultButtonStyle;
        // 缓存的扫描取消按钮样式。
        private static Style? _scanCancelButtonStyle;

        // 扫描按钮样式：扫描中切换为取消样式，空闲时恢复默认样式。
        public Style ScanButtonStyle => ResolveScanButtonStyle(IsScanning);

        // 扫描中且尚无进度总数（枚举未完成）时显示不确定动画；
        // 一旦拿到进度总数（total > 0），进度条切换为确定模式并显示真实扫描进度。
        public bool IsScanIndeterminate => IsScanning && _scanTotal <= 0;

        // 根据扫描状态解析按钮样式。
        private static Style ResolveScanButtonStyle(bool isCancelAppearance)
        {
            EnsureScanButtonStyles();
            if (isCancelAppearance && _scanCancelButtonStyle != null) return _scanCancelButtonStyle;
            if (_defaultButtonStyle != null) return _defaultButtonStyle;
            return new Style(typeof(Button));
        }

        // 确保按钮样式已从 Application.Resources 中加载并缓存。
        private static void EnsureScanButtonStyles()
        {
            if (_defaultButtonStyle != null && _scanCancelButtonStyle != null) return;
            if (Application.Current?.Resources == null) return;
            var resources = Application.Current.Resources;
            if (_defaultButtonStyle == null && resources.TryGetValue("AccentButtonStyle", out var defaultStyle) && defaultStyle is Style dbs)
                _defaultButtonStyle = dbs;
            if (_scanCancelButtonStyle == null && resources.TryGetValue("ScanCancelButtonStyle", out var cancelStyle) && cancelStyle is Style cbs)
                _scanCancelButtonStyle = cbs;
        }

        // 切换暂停/恢复状态。暂停时阻塞新任务，等待活跃工作线程结束后置 IsPaused=true。
        protected void TogglePause()
        {
            if (IsPaused)
            {
                // Resume
                _pauseRequested = false;
                _resumeRequested = true;
                _isPausing = false;
                PauseEvent.Set();
                SetDirectStatus(ProcessingStatusText);
                ProgressBarState = Models.ProgressBarState.Processing;
                NotifyStatusChanged();
            }
            else if (_isPausing)
            {
                // Cancel pausing — back to processing
                _pauseRequested = false;
                _isPausing = false;
                PauseEvent.Set();
                SetDirectStatus(ProcessingStatusText);
                ProgressBarState = Models.ProgressBarState.Processing;
                NotifyStatusChanged();
            }
            else
            {
                // Request pause — workers still finishing, keep processing state
                _pauseRequested = true;
                _resumeRequested = false;
                _isPausing = true;
                PauseEvent.Reset();
                SetStatus("Status_Pausing");
                ProgressBarState = Models.ProgressBarState.Processing;
                NotifyStatusChanged();
            }
        }

        // 由定时器每帧调用：检查是否所有工作线程已退出，从而安全切换暂停状态。
        protected void CheckAndApplyPendingState()
        {
            if (_pauseRequested && !IsPaused)
            {
                // All workers must finish before transitioning to Paused
                if (Volatile.Read(ref _activeWorkerCount) <= 0)
                {
                    IsPaused = true;
                    _isPausing = false;
                    ProgressBarState = Models.ProgressBarState.Paused;
                    SetStatus("Status_Paused");
                    _pauseRequested = false;
                }
            }
            else if (_resumeRequested && IsPaused)
            {
                IsPaused = false;
                _isPausing = false;
                ProgressBarState = Models.ProgressBarState.Processing;
                SetDirectStatus(ProcessingStatusText);
                _resumeRequested = false;
            }
        }

        // 应用取消状态：将进度条置为 Cancelled（红色），清除取消标志。
        protected void ApplyCancellationState()
        {
            if (_cancelledByUser)
            {
                ProgressBarState = Models.ProgressBarState.Cancelled;
                _cancelledByUser = false;
            }
        }

        // 清除所有状态（重置进度、取消扫描/处理、清空列表）。
        protected void ClearState()
        {
            IsProcessing = false;
            IsPaused = false;
            IsScanning = false;
            _isPausing = false;
            ScanStopwatch.Reset();
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

        // 子类清空状态时的钩子（在此清空 Task 列表、重置计数器等）。
        protected abstract void OnClearState();

        // 子类清理资源的钩子（在此停止定时器等）。
        protected virtual void OnCleanup() { }

        // 页面关闭时调用，清理所有资源。
        // 必须先取消 Token 停止后台任务，再清集合、停定时器，
        // 否则后台任务在清理期间仍可能访问已被清空的集合。
        public void Cleanup()
        {
            _scanElapsedTimer.Stop();
            CleanupTokens();
            OnCleanup();
        }
    }
}
