// <copyright file="AppViewModel.cs" company="Live Photo Box">
// Copyright (c) Live Photo Box. All rights reserved.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.ViewModels
{
    // 应用程序主 ViewModel，作为所有子页面的统一入口和管理者。
    // 负责子 ViewModel 的生命周期管理、底部状态栏的进度信息聚合
    // 以及页面间导航事件的转发。
    public partial class AppViewModel : ViewModelBase
    {
        // 全局单例实例。
        public static AppViewModel Instance { get; } = new AppViewModel();

        // 合并页面 ViewModel（MergePage）。
        public MergeViewModel Merge { get; }

        // 拆分页面 ViewModel（SplitPage）。
        public SplitViewModel Split { get; }

        // 修复页面 ViewModel（RepairPage）。
        public RepairViewModel Repair { get; }

        // 首页 ViewModel（HomePage）。
        public HomeViewModel Home { get; }

        // 设置页面 ViewModel（SettingsPage）。
        public SettingsViewModel Settings { get; }

        // 关于页面 ViewModel（AboutPage）。
        public AboutViewModel About { get; }

        // 实况照片编辑页面 ViewModel（EditPage）。
        public EditViewModel Edit { get; }

        // 照片分类页面 ViewModel（PhotoClassifyPage）。
        public PhotoClassifyViewModel PhotoClassify { get; }

        // 历史记录页面 ViewModel（HistoryPage）。
        public HistoryViewModel History { get; }

        // 请求导航到特定页面的事件，供 Shell/主窗口订阅处理。
        public event EventHandler<string>? RequestNavigateToPage;

        // 当前活动页面的标签，用于确定底部状态栏显示哪个页面的状态。
        private string? _currentStatusPageTag;
        public string? CurrentStatusPageTag
        {
            get => _currentStatusPageTag;
            private set
            {
                if (!SetProperty(ref _currentStatusPageTag, value)) return;
                OnPropertyChanged(nameof(CurrentPageStatus));
                OnPropertyChanged(nameof(IsStatusBarVisible));
                NotifyFooterProperties();
            }
        }

        // 当前活动页面的状态文本（合并/拆分/修复页面的 Status 属性）。
        public string CurrentPageStatus => CurrentStatusPageTag switch
        {
            "Merge" => Merge.Status,
            "Split" => Split.Status,
            "Repair" => Repair.Status,
            _ => string.Empty
        };

        // 当前页面是否有底部状态栏（仅合并/拆分/修复页面显示）。
        public bool IsStatusBarVisible => CurrentStatusPageTag is "Merge" or "Split" or "Repair";

        // 拆分页面的扫描总进度计数。
        private int _splitScanTotal;

        // 拆分页面的已扫描文件计数。
        private int _splitScanProcessed;

        // 合并页面的扫描总进度计数。
        private int _mergeScanTotal;

        // 合并页面的已扫描文件计数。
        private int _mergeScanProcessed;

        // 修复页面的扫描总进度计数。
        private int _repairScanTotal;

        // 修复页面的已扫描文件计数。
        private int _repairScanProcessed;

        // 底部状态栏的进度条状态（空闲/处理中/暂停/成功/取消等）。
        public ProgressBarState FooterProgressBarState
        {
            get
            {
                return CurrentStatusPageTag switch
                {
                    "Merge" => Merge.ProgressBarState,
                    "Split" => Split.ProgressBarState,
                    "Repair" => Repair.ProgressBarState,
                    _ => ProgressBarState.Idle
                };
            }
        }

        // 底部状态栏的文本描述，扫描中显示扫描进度，否则显示当前状态。
        public string FooterStatusText
        {
            get
            {
                return CurrentStatusPageTag switch
                {
                    "Merge" when Merge.IsScanning =>
                        ResourceService.Format("StatusBar_Scanning_Merge", _mergeScanProcessed, Math.Max(_mergeScanTotal, _mergeScanProcessed)),
                    "Split" when Split.IsScanning =>
                        ResourceService.Format("StatusBar_Scanning_Split", _splitScanProcessed, Math.Max(_splitScanTotal, _splitScanProcessed)),
                    "Repair" when Repair.IsScanning =>
                        ResourceService.Format("StatusBar_Scanning_Repair", _repairScanProcessed, Math.Max(_repairScanTotal, _repairScanProcessed)),
                    _ => CurrentPageStatus
                };
            }
        }

        // 底部进度条的数值（0~100），综合考虑扫描进度和任务处理进度。
        public double FooterProgress
        {
            get
            {
                var tag = CurrentStatusPageTag;
                if (tag == "Merge") return GetProgress(Merge, _mergeScanProcessed, _mergeScanTotal);
                if (tag == "Split") return GetProgress(Split, _splitScanProcessed, _splitScanTotal);
                if (tag == "Repair") return GetProgress(Repair, _repairScanProcessed, _repairScanTotal);
                return 0;
            }
        }

        // 计算工作表（WorkViewModelBase）的进度值。
        // 扫描中取扫描进度，处理中/暂停/成功/取消时取 vm.Progress，
        // 空闲时若有已扫描数据则显示扫描结果，否则 fallback 到 vm.Progress。
        private double GetProgress(WorkViewModelBase vm, int scanProcessed, int scanTotal)
        {
            if (vm.IsScanning) return Math.Clamp(scanProcessed * 100.0 / Math.Max(1, scanTotal), 0, 100);
            if (vm.ProgressBarState is ProgressBarState.Processing or ProgressBarState.Pausing or ProgressBarState.Paused or ProgressBarState.Success)
                return vm.Progress;
            if (vm.ProgressBarState == ProgressBarState.Cancelled)
                return vm.Progress > 0 ? vm.Progress : Math.Clamp(scanProcessed * 100.0 / Math.Max(1, scanTotal), 0, 100);
            // Idle — after scan show scan result (100%), otherwise vm.Progress
            return scanTotal > 0 ? Math.Clamp(scanProcessed * 100.0 / Math.Max(1, scanTotal), 0, 100) : vm.Progress;
        }

        // 进度条是否为不确定状态（扫描中但总数为0）。
        public bool FooterIsIndeterminate =>
            (CurrentStatusPageTag == "Merge" && Merge.IsScanning && _mergeScanTotal == 0)
            || (CurrentStatusPageTag == "Split" && Split.IsScanning && _splitScanTotal == 0)
            || (CurrentStatusPageTag == "Repair" && Repair.IsScanning && _repairScanTotal == 0);

        // 最终传递给 ProgressBar 控件绑定的值，不确定状态时为 0。
        public double FooterProgressBarValue => FooterIsIndeterminate ? 0 : FooterProgress;

        // 底部状态栏的百分比文本（如"已扫描 45%"或"处理中 50%"），根据页面状态动态切换。
        public string FooterPercentText
        {
            get
            {
                if (FooterIsIndeterminate)
                {
                    return ResourceService.Format("StatusBar_ScanProgressLabel", "?");
                }

                var vm = CurrentStatusPageTag switch
                {
                    "Merge" => (WorkViewModelBase)Merge,
                    "Split" => Split,
                    "Repair" => Repair,
                    _ => null
                };

                // 扫描中用截断避免 99.9% 四舍五入成 100%
                int percent = (vm?.IsScanning == true)
                    ? (int)FooterProgress
                    : (int)Math.Round(FooterProgress);

                if (vm != null)
                {
                    // Scanning - show scan progress
                    if (vm.IsScanning)
                    {
                        return ResourceService.Format("StatusBar_ScanProgressLabel", percent);
                    }

                    // Non-scanning - show state-specific labels
                    switch (vm.ProgressBarState)
                    {
                        case ProgressBarState.Processing:
                        case ProgressBarState.Pausing:
                            // Pausing keeps "Processing" label since main text already shows "Pausing..."
                            return ResourceService.Format("StatusBar_ProcessProgressLabel", percent);

                        case ProgressBarState.Paused:
                            return ResourceService.Format("StatusBar_PausedLabel", percent);

                        case ProgressBarState.Cancelled:
                            return ResourceService.Format("StatusBar_StoppedLabel", percent);

                        case ProgressBarState.Idle:
                            // After scan, before processing -> "Ready"
                            bool hasData = CurrentStatusPageTag switch
                            {
                                "Merge" => _mergeScanTotal > 0,
                                "Split" => _splitScanTotal > 0,
                                "Repair" => _repairScanTotal > 0,
                                _ => false
                            };
                            if (hasData)
                                return ResourceService.Format("StatusBar_ReadyLabel", percent);
                            break;

                        case ProgressBarState.Success:
                            return ResourceService.Format("StatusBar_CompletedLabel", percent);

                        default:
                            break;
                    }

                    // Fallback: show scan progress if there's residual scan data
                    bool fallbackHasData = false;
                    if (CurrentStatusPageTag == "Merge") fallbackHasData = _mergeScanTotal > 0;
                    if (CurrentStatusPageTag == "Split") fallbackHasData = _splitScanTotal > 0;
                    if (CurrentStatusPageTag == "Repair") fallbackHasData = _repairScanTotal > 0;

                    if (fallbackHasData || percent > 0)
                    {
                        return ResourceService.Format("StatusBar_ScanProgressLabel", percent);
                    }
                }

                return string.Empty;
            }
        }

        // 底部状态栏百分比标签的前缀部分（如"已扫描""处理中""已暂停"等），不含具体数值。
        public string FooterPercentLabel
        {
            get
            {
                // Same state logic as FooterPercentText, but returns only the label prefix
                if (FooterIsIndeterminate)
                    return ResourceService.GetString("StatusBar_ScanProgressLabel_Lbl");

                var vm = CurrentStatusPageTag switch
                {
                    "Merge" => (WorkViewModelBase)Merge,
                    "Split" => Split,
                    "Repair" => Repair,
                    _ => null
                };

                if (vm != null)
                {
                    if (vm.IsScanning)
                        return ResourceService.GetString("StatusBar_ScanProgressLabel_Lbl");

                    switch (vm.ProgressBarState)
                    {
                        case ProgressBarState.Processing:
                        case ProgressBarState.Pausing:
                            return ResourceService.GetString("StatusBar_ProcessProgressLabel_Lbl");
                        case ProgressBarState.Paused:
                            return ResourceService.GetString("StatusBar_PausedLabel_Lbl");
                        case ProgressBarState.Cancelled:
                            return ResourceService.GetString("StatusBar_StoppedLabel_Lbl");
                        case ProgressBarState.Idle:
                            bool hasData = CurrentStatusPageTag switch
                            {
                                "Merge" => _mergeScanTotal > 0,
                                "Split" => _splitScanTotal > 0,
                                "Repair" => _repairScanTotal > 0,
                                _ => false
                            };
                            if (hasData) return ResourceService.GetString("StatusBar_ReadyLabel_Lbl");
                            break;
                        case ProgressBarState.Success:
                            return ResourceService.GetString("StatusBar_CompletedLabel_Lbl");
                        default: break;
                    }
                }

                return string.Empty;
            }
        }

        // 底部状态栏显示的百分比数值字符串（如"45%"），与 FooterPercentLabel 配合使用。
        public string FooterPercentNumber
        {
            get
            {
                if (string.IsNullOrEmpty(FooterPercentLabel)) return string.Empty;

                var vm = CurrentStatusPageTag switch
                {
                    "Merge" => (WorkViewModelBase)Merge,
                    "Split" => Split,
                    "Repair" => Repair,
                    _ => null
                };

                int percent = (vm?.IsScanning == true)
                    ? (int)FooterProgress
                    : (int)Math.Round(FooterProgress);

                return $"{percent}%";
            }
        }

        // 底部百分比区域是否可见（无内容时折叠）。
        public Visibility FooterPercentVisibility =>
            string.IsNullOrEmpty(FooterPercentLabel) ? Visibility.Collapsed : Visibility.Visible;

        #region Footer Progress Updaters

        // 应用合并页面的扫描进度快照到底部状态栏。
        public void ApplyMergeScanProgress(WorkProgressSnapshot snapshot)
        {
            _mergeScanTotal = snapshot.Total;
            _mergeScanProcessed = snapshot.Completed;
            NotifyFooterProperties();
        }

        // 应用拆分页面的扫描进度快照到底部状态栏。
        public void ApplySplitScanProgress(WorkProgressSnapshot snapshot)
        {
            _splitScanTotal = snapshot.Total;
            _splitScanProcessed = snapshot.Completed;
            NotifyFooterProperties();
        }

        // 应用修复页面的扫描进度快照到底部状态栏。
        public void ApplyRepairScanProgress(WorkProgressSnapshot snapshot)
        {
            _repairScanTotal = snapshot.Total;
            _repairScanProcessed = snapshot.Completed;
            NotifyFooterProperties();
        }

        // 开始新的合并扫描会话，重置进度计数器。
        public void BeginMergeScanSession()
        {
            _mergeScanProcessed = 0;
            _mergeScanTotal = 0;
        }

        // 开始新的拆分扫描会话，重置进度计数器。
        public void BeginSplitScanSession()
        {
            _splitScanProcessed = 0;
            _splitScanTotal = 0;
        }

        // 开始新的修复扫描会话，重置进度计数器。
        public void BeginRepairScanSession()
        {
            _repairScanProcessed = 0;
            _repairScanTotal = 0;
        }

        // 标记当前页面的扫描已完成，将已处理数设为总数并刷新 UI。
        public void CompleteFooterWorkSnapshot()
        {
            switch (CurrentStatusPageTag)
            {
                case "Split":
                    _splitScanProcessed = _splitScanTotal;
                    break;
                case "Merge":
                    _mergeScanProcessed = _mergeScanTotal;
                    break;
                case "Repair":
                    _repairScanProcessed = _repairScanTotal;
                    break;
            }
            NotifyFooterProperties();
        }

        // 重置所有页面的扫描计数器并刷新底部状态栏。
        public void ResetFooterScanCounters()
        {
            _mergeScanTotal = 0;
            _mergeScanProcessed = 0;
            _splitScanTotal = 0;
            _splitScanProcessed = 0;
            _repairScanTotal = 0;
            _repairScanProcessed = 0;
            NotifyFooterProperties();
        }

        // 通知底部状态栏所有相关属性已变更。
        public void NotifyFooterProperties()
        {
            OnPropertyChanged(nameof(FooterStatusText));
            OnPropertyChanged(nameof(FooterProgress));
            OnPropertyChanged(nameof(FooterProgressBarValue));
            OnPropertyChanged(nameof(FooterIsIndeterminate));
            OnPropertyChanged(nameof(FooterPercentText));
            OnPropertyChanged(nameof(FooterPercentLabel));
            OnPropertyChanged(nameof(FooterPercentNumber));
            OnPropertyChanged(nameof(FooterPercentVisibility));
            OnPropertyChanged(nameof(FooterProgressBarState));
        }

        #endregion

        #region Constructor & Initialization

        private AppViewModel()
        {
            Merge = new MergeViewModel();
            Split = new SplitViewModel();
            Repair = new RepairViewModel();
            Home = new HomeViewModel();
            Settings = new SettingsViewModel();
            About = new AboutViewModel();
            Edit = new EditViewModel();
            PhotoClassify = new PhotoClassifyViewModel();
            History = new HistoryViewModel();

            SubscribeToChildStatusChanges();
            SubscribeHomeNavigation();
            InitializeAsync();
        }

        // 异步初始化：应用语言覆盖设置并记录日志。
        private async void InitializeAsync()
        {
            try
            {
                LanguageService.ApplyLanguageOverride(LanguageService.GetEffectiveLanguage(Settings.LanguageIndex));
                LogService.Info("AppViewModel initialized.");
            }
            catch (Exception ex)
            {
                LogService.Error("AppViewModel initialization failed", ex, LogSource.App);
            }
            await Task.CompletedTask;
        }

        #endregion

        #region Navigation & Status

        // 设置当前活动页面的标签，用于底部状态栏切换。
        public void SetCurrentStatusPage(string? pageTag)
        {
            CurrentStatusPageTag = pageTag;
        }

        // 导航到首页上的指定功能教程区域。
        [RelayCommand]
        private void GoToTutorial(string feature)
        {
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }

        #endregion

        #region Child ViewModel Event Subscriptions

        // 订阅子 ViewModel 的状态和属性变更事件，以更新底部状态栏。
        private void SubscribeToChildStatusChanges()
        {
            Merge.StatusChanged += OnChildStatusChanged;
            Split.StatusChanged += OnChildStatusChanged;
            Repair.StatusChanged += OnChildStatusChanged;

            Merge.PropertyChanged += OnChildPropertyChangedHandler;
            Split.PropertyChanged += OnChildPropertyChangedHandler;
            Repair.PropertyChanged += OnChildPropertyChangedHandler;

            PropertyChanged += OnPropertyChangedHandler;
        }

        // 子 ViewModel 状态变更时刷新底部状态栏。
        private void OnChildStatusChanged(object? sender, EventArgs e)
        {
            NotifyFooterProperties();
        }

        // 子 ViewModel 关键属性变更时刷新底部状态栏。
        private void OnChildPropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null) return;

            switch (e.PropertyName)
            {
                case "IsScanning":
                case "IsProcessing":
                case "MergeProgress":
                case "Progress":
                case "Status":
                case "ProgressBarState":
                    NotifyFooterProperties();
                    break;
            }
        }

        // 当前页面标签变更时刷新底部状态栏。
        private void OnPropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CurrentStatusPageTag))
            {
                NotifyFooterProperties();
            }
        }

        // 订阅子页面的导航请求事件，转发到 AppViewModel 的统一导航事件。
        private void SubscribeHomeNavigation()
        {
            Home.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
            Merge.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
            Split.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
            Repair.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
        }

        // 释放子 ViewModel 资源：停止 DispatcherTimer、解除 Tick 回调、
        // 取消 CancellationToken、释放 ManualResetEventSlim、清空任务集合。
        // 在窗口关闭时由 MainWindow.Closed 调用。
        public void Cleanup()
        {
            Merge.Cleanup();
            Split.Cleanup();
            Repair.Cleanup();
            Edit.Cleanup();
        }

        #endregion
    }
}