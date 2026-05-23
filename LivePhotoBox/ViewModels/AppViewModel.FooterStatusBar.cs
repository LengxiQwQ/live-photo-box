using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using System;

namespace LivePhotoBox.ViewModels
{
    public enum FooterWorkMode
    {
        Idle,
        Scanning,
        Processing,
        Paused
    }

    public partial class AppViewModel
    {
        private FooterWorkMode _footerWorkMode = FooterWorkMode.Idle;

        private string? _comboStatusKey;
        private string? _splitStatusKey;
        private string? _repairStatusKey;

        private int _splitScanTotal;
        private int _splitScanProcessed;
        private int _comboScanTotal;
        private int _comboScanProcessed;
        private int _repairScanTotal;
        private int _repairScanProcessed;

        private WorkProgressSnapshot _pendingSplitScanSnapshot;
        private long _lastSplitScanUiUpdateMs;

        public string FooterStatusText
        {
            get
            {
                return CurrentStatusPageTag switch
                {
                    "Split" when IsSplitScanning && _splitStatusKey == "SplitPage_Status_Scanning" =>
                        ResourceService.Format(
                            "StatusBar_Scanning_Split",
                            _splitScanProcessed,
                            Math.Max(_splitScanTotal, _splitScanProcessed)),
                    "Combo" when IsScanning && _comboStatusKey == "Status_Scanning" =>
                        ResourceService.Format(
                            "StatusBar_Scanning_Combo",
                            _comboScanProcessed,
                            Math.Max(_comboScanTotal, _comboScanProcessed)),
                    "Repair" when IsRepairScanning && _repairStatusKey == "Status_Scanning" =>
                        ResourceService.Format(
                            "StatusBar_Scanning_Repair",
                            _repairScanProcessed,
                            Math.Max(_repairScanTotal, _repairScanProcessed)),
                    _ => CurrentPageStatus
                };
            }
        }

        public double FooterProgress
        {
            get
            {
                return CurrentStatusPageTag switch
                {
                    "Combo" when IsScanning && _comboScanTotal > 0 =>
                        Math.Clamp(_comboScanProcessed * 100.0 / _comboScanTotal, 0, 100),
                    "Combo" when IsProcessing => ComboProgress,
                    "Split" when IsSplitScanning && _splitScanTotal > 0 =>
                        Math.Clamp(_splitScanProcessed * 100.0 / _splitScanTotal, 0, 100),
                    "Split" when IsSplitProcessing => SplitProgress,
                    "Split" when _splitScanTotal > 0 =>
                        Math.Clamp(_splitScanProcessed * 100.0 / _splitScanTotal, 0, 100),
                    "Repair" when IsRepairScanning && _repairScanTotal > 0 =>
                        Math.Clamp(_repairScanProcessed * 100.0 / _repairScanTotal, 0, 100),
                    "Repair" when IsRepairProcessing => RepairProgress,
                    "Repair" when _repairScanTotal > 0 =>
                        Math.Clamp(_repairScanProcessed * 100.0 / _repairScanTotal, 0, 100),
                    _ => 0
                };
            }
        }

        public bool FooterIsIndeterminate =>
            (CurrentStatusPageTag == "Combo" && IsScanning)
            || (CurrentStatusPageTag == "Split" && IsSplitScanning)
            || (IsRepairScanning && _repairScanTotal <= 0);

        /// <summary>与不确定进度互斥，避免 WinUI ProgressBar 同时绑定 Value 导致崩溃。</summary>
        public double FooterProgressBarValue => FooterIsIndeterminate ? 0 : FooterProgress;

        public string FooterPercentText
        {
            get
            {
                if (FooterIsIndeterminate)
                {
                    return ResourceService.Format("StatusBar_ScanProgressLabel", "…");
                }

                if (FooterProgress <= 0 && !IsScanning && !IsSplitScanning && !IsRepairScanning
                    && !IsProcessing && !IsSplitProcessing && !IsRepairProcessing)
                {
                    return string.Empty;
                }

                int percent = (int)Math.Round(FooterProgress);
                bool isScanPhase = IsScanning || IsSplitScanning || IsRepairScanning
                    || (_splitScanTotal > 0 && !IsSplitProcessing)
                    || (_comboScanTotal > 0 && !IsProcessing)
                    || (_repairScanTotal > 0 && !IsRepairProcessing);

                string key = isScanPhase && !IsProcessing && !IsSplitProcessing && !IsRepairProcessing
                    ? "StatusBar_ScanProgressLabel"
                    : "StatusBar_ProcessProgressLabel";

                return ResourceService.Format(key, percent);
            }
        }

        public Visibility FooterPercentVisibility =>
            string.IsNullOrEmpty(FooterPercentText) ? Visibility.Collapsed : Visibility.Visible;

        private void SubscribeFooterStatusRefresh()
        {
            PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(CurrentStatusPageTag):
                    case nameof(ComboStatus):
                    case nameof(SplitStatus):
                    case nameof(RepairStatus):
                    case nameof(IsScanning):
                    case nameof(IsSplitScanning):
                    case nameof(IsRepairScanning):
                    case nameof(IsProcessing):
                    case nameof(IsSplitProcessing):
                    case nameof(IsRepairProcessing):
                    case nameof(IsPaused):
                    case nameof(IsSplitPaused):
                    case nameof(IsRepairPaused):
                    case nameof(ComboProgress):
                    case nameof(SplitProgress):
                    case nameof(RepairProgress):
                    case nameof(ProgressText):
                    case nameof(SplitProgressText):
                    case nameof(RepairProgressText):
                    case nameof(TotalPairsCount):
                    case nameof(SplitQueuedCount):
                    case nameof(RepairTotalPhotosCount):
                        RefreshFooterStatusBar();
                        break;
                }

            };
        }

        private void NotifyFooterProperties()
        {
            OnPropertyChanged(nameof(FooterStatusText));
            OnPropertyChanged(nameof(FooterProgress));
            OnPropertyChanged(nameof(FooterProgressBarValue));
            OnPropertyChanged(nameof(FooterIsIndeterminate));
            OnPropertyChanged(nameof(FooterPercentText));
            OnPropertyChanged(nameof(FooterPercentVisibility));
        }

        private void RefreshFooterStatusBar()
        {
            if (CurrentStatusPageTag is "Combo" && IsScanning)
            {
                _footerWorkMode = FooterWorkMode.Scanning;
            }
            else if (CurrentStatusPageTag is "Split" && IsSplitScanning)
            {
                _footerWorkMode = FooterWorkMode.Scanning;
            }
            else if (CurrentStatusPageTag is "Repair" && IsRepairScanning)
            {
                _footerWorkMode = FooterWorkMode.Scanning;
            }
            else if (CurrentStatusPageTag is "Combo" && IsProcessing)
            {
                _footerWorkMode = IsPaused ? FooterWorkMode.Paused : FooterWorkMode.Processing;
            }
            else if (CurrentStatusPageTag is "Split" && IsSplitProcessing)
            {
                _footerWorkMode = IsSplitPaused ? FooterWorkMode.Paused : FooterWorkMode.Processing;
            }
            else if (CurrentStatusPageTag is "Repair" && IsRepairProcessing)
            {
                _footerWorkMode = IsRepairPaused ? FooterWorkMode.Paused : FooterWorkMode.Processing;
            }
            else
            {
                _footerWorkMode = FooterWorkMode.Idle;
            }

            NotifyFooterProperties();
        }

        private void ApplySplitScanProgress(WorkProgressSnapshot snapshot)
        {
            _splitScanTotal = snapshot.Total;
            _splitScanProcessed = snapshot.Completed;
            // 扫描过程中不刷新页面统计区，避免大量进度回调卡死 UI；结束后由 ViewModel 写入最终结果
            if (!IsSplitScanning)
            {
                SplitRecognizedCount = snapshot.RecognizedCount;
                SplitSkippedCount = snapshot.SkippedCount;
                RefreshFooterStatusBar();
            }
            else
            {
                NotifyFooterProperties();
            }
        }

        private void ApplyComboScanProgress(WorkProgressSnapshot snapshot)
        {
            _comboScanTotal = snapshot.Total;
            _comboScanProcessed = snapshot.Completed;
            RefreshFooterStatusBar();
        }

        private void ApplyRepairScanProgress(WorkProgressSnapshot snapshot)
        {
            _repairScanTotal = snapshot.Total;
            _repairScanProcessed = snapshot.Completed;
            RefreshFooterStatusBar();
        }

        private void BeginSplitScanSession()
        {
            _splitScanProcessed = 0;
            _splitScanTotal = 0;
            _lastSplitScanUiUpdateMs = 0;
            RefreshFooterStatusBar();
        }

        private void ResetSplitScanProgressCounters()
        {
            _splitScanTotal = 0;
            _splitScanProcessed = 0;
            RefreshFooterStatusBar();
        }

        private void BeginComboScanSession()
        {
            _comboScanProcessed = 0;
            _comboScanTotal = 0;
            RefreshFooterStatusBar();
        }

        private void ResetComboScanProgressCounters()
        {
            _comboScanTotal = 0;
            _comboScanProcessed = 0;
            RefreshFooterStatusBar();
        }

        private void BeginRepairScanSession()
        {
            _repairScanProcessed = 0;
            _repairScanTotal = 0;
            RefreshFooterStatusBar();
        }

        private void ResetRepairScanProgressCounters()
        {
            _repairScanTotal = 0;
            _repairScanProcessed = 0;
            RefreshFooterStatusBar();
        }

        private void CompleteFooterWorkSnapshot()
        {
            switch (CurrentStatusPageTag)
            {
                case "Split":
                    _splitScanProcessed = _splitScanTotal;
                    break;
                case "Combo":
                    _comboScanProcessed = _comboScanTotal;
                    break;
                case "Repair":
                    _repairScanProcessed = _repairScanTotal;
                    break;
            }

            NotifyFooterProperties();
        }

        private IProgress<WorkProgressSnapshot> CreateSplitScanProgressReporter()
        {
            var dispatcher = App.MainWindow?.DispatcherQueue;
            return new Progress<WorkProgressSnapshot>(snapshot =>
            {
                _pendingSplitScanSnapshot = snapshot;
                if (dispatcher == null)
                {
                    return;
                }

                var now = Environment.TickCount64;
                if (_lastSplitScanUiUpdateMs != 0 && now - _lastSplitScanUiUpdateMs < 100)
                {
                    return;
                }

                _lastSplitScanUiUpdateMs = now;
                dispatcher.TryEnqueue(() => ApplySplitScanProgress(_pendingSplitScanSnapshot));
            });
        }

        private IProgress<WorkProgressSnapshot> CreateComboScanProgressReporter() =>
            new Progress<WorkProgressSnapshot>(snapshot =>
                App.MainWindow?.DispatcherQueue.TryEnqueue(() => ApplyComboScanProgress(snapshot)));

        private IProgress<WorkProgressSnapshot> CreateRepairScanProgressReporter() =>
            new Progress<WorkProgressSnapshot>(snapshot =>
                App.MainWindow?.DispatcherQueue.TryEnqueue(() => ApplyRepairScanProgress(snapshot)));
    }
}
