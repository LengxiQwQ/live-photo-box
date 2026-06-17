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
    public partial class AppViewModel : ObservableObject
    {
        public static AppViewModel Instance { get; } = new AppViewModel();

        public ComboViewModel Combo { get; }
        public SplitViewModel Split { get; }
        public RepairViewModel Repair { get; }
        public HomeViewModel Home { get; }
        public SettingsViewModel Settings { get; }
        public AboutViewModel About { get; }
        public KeyPhotoViewModel KeyPhoto { get; }

        public event EventHandler<string>? RequestNavigateToPage;

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

        public string CurrentPageStatus => CurrentStatusPageTag switch
        {
            "Combo" => Combo.Status,
            "Split" => Split.Status,
            "Repair" => Repair.Status,
            _ => string.Empty
        };

        public bool IsStatusBarVisible => CurrentStatusPageTag is "Combo" or "Split" or "Repair";

        private int _splitScanTotal;
        private int _splitScanProcessed;
        private int _comboScanTotal;
        private int _comboScanProcessed;
        private int _repairScanTotal;
        private int _repairScanProcessed;

        public ProgressBarState FooterProgressBarState
        {
            get
            {
                return CurrentStatusPageTag switch
                {
                    "Combo" => Combo.ProgressBarState,
                    "Split" => Split.ProgressBarState,
                    "Repair" => Repair.ProgressBarState,
                    _ => ProgressBarState.Idle
                };
            }
        }

        public string FooterStatusText
        {
            get
            {
                return CurrentStatusPageTag switch
                {
                    "Combo" when Combo.IsScanning =>
                        ResourceService.Format("StatusBar_Scanning_Combo", _comboScanProcessed, Math.Max(_comboScanTotal, _comboScanProcessed)),
                    "Split" when Split.IsScanning =>
                        ResourceService.Format("StatusBar_Scanning_Split", _splitScanProcessed, Math.Max(_splitScanTotal, _splitScanProcessed)),
                    "Repair" when Repair.IsScanning =>
                        ResourceService.Format("StatusBar_Scanning_Repair", _repairScanProcessed, Math.Max(_repairScanTotal, _repairScanProcessed)),
                    _ => CurrentPageStatus
                };
            }
        }

        public double FooterProgress
        {
            get
            {
                var tag = CurrentStatusPageTag;
                if (tag == "Combo") return GetProgress(Combo, _comboScanProcessed, _comboScanTotal);
                if (tag == "Split") return GetProgress(Split, _splitScanProcessed, _splitScanTotal);
                if (tag == "Repair") return GetProgress(Repair, _repairScanProcessed, _repairScanTotal);
                return 0;
            }
        }

        private double GetProgress(WorkViewModelBase vm, int scanProcessed, int scanTotal)
        {
            if (vm.IsScanning) return Math.Clamp(scanProcessed * 100.0 / Math.Max(1, scanTotal), 0, 100);
            if (vm.ProgressBarState is ProgressBarState.Processing or ProgressBarState.Paused or ProgressBarState.Success)
                return vm.Progress;
            if (vm.ProgressBarState == ProgressBarState.Cancelled)
                return vm.Progress > 0 ? vm.Progress : Math.Clamp(scanProcessed * 100.0 / Math.Max(1, scanTotal), 0, 100);

            return Math.Clamp(scanProcessed * 100.0 / Math.Max(1, scanTotal), 0, 100);
        }

        public bool FooterIsIndeterminate =>
            (CurrentStatusPageTag == "Combo" && Combo.IsScanning && _comboScanTotal == 0)
            || (CurrentStatusPageTag == "Split" && Split.IsScanning && _splitScanTotal == 0)
            || (CurrentStatusPageTag == "Repair" && Repair.IsScanning && _repairScanTotal == 0);

        public double FooterProgressBarValue => FooterIsIndeterminate ? 0 : FooterProgress;

        public string FooterPercentText
        {
            get
            {
                if (FooterIsIndeterminate)
                {
                    return ResourceService.Format("StatusBar_ScanProgressLabel", "?");
                }

                int percent = (int)Math.Round(FooterProgress);

                var vm = CurrentStatusPageTag switch
                {
                    "Combo" => (WorkViewModelBase)Combo,
                    "Split" => Split,
                    "Repair" => Repair,
                    _ => null
                };

                if (vm != null)
                {
                    // 【终极防护】：只有明确处于“处理/修复”有关的状态（且不能是在扫描目录阶段），才显示“处理中”百分比
                    if (!vm.IsScanning && (vm.IsProcessing || vm.ProgressBarState != ProgressBarState.Idle))
                    {
                        return ResourceService.Format("StatusBar_ProcessProgressLabel", percent);
                    }

                    // 其他任何时候（只要有扫描数据残留、或者是被取消了扫描），统统坚如磐石地显示“扫描中”进度
                    bool hasData = false;
                    if (CurrentStatusPageTag == "Combo") hasData = _comboScanTotal > 0;
                    if (CurrentStatusPageTag == "Split") hasData = _splitScanTotal > 0;
                    if (CurrentStatusPageTag == "Repair") hasData = _repairScanTotal > 0;

                    if (vm.IsScanning || hasData || percent > 0)
                    {
                        return ResourceService.Format("StatusBar_ScanProgressLabel", percent);
                    }
                }

                return string.Empty;
            }
        }

        public Visibility FooterPercentVisibility =>
            string.IsNullOrEmpty(FooterPercentText) ? Visibility.Collapsed : Visibility.Visible;

        public void ApplyComboScanProgress(WorkProgressSnapshot snapshot)
        {
            _comboScanTotal = snapshot.Total;
            _comboScanProcessed = snapshot.Completed;
            NotifyFooterProperties();
        }

        public void ApplySplitScanProgress(WorkProgressSnapshot snapshot)
        {
            _splitScanTotal = snapshot.Total;
            _splitScanProcessed = snapshot.Completed;
            NotifyFooterProperties();
        }

        public void ApplyRepairScanProgress(WorkProgressSnapshot snapshot)
        {
            _repairScanTotal = snapshot.Total;
            _repairScanProcessed = snapshot.Completed;
            NotifyFooterProperties();
        }

        public void BeginComboScanSession()
        {
            _comboScanProcessed = 0;
            _comboScanTotal = 0;
        }

        public void BeginSplitScanSession()
        {
            _splitScanProcessed = 0;
            _splitScanTotal = 0;
        }

        public void BeginRepairScanSession()
        {
            _repairScanProcessed = 0;
            _repairScanTotal = 0;
        }

        public void CompleteFooterWorkSnapshot()
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

        public void ResetFooterScanCounters()
        {
            _comboScanTotal = 0;
            _comboScanProcessed = 0;
            _splitScanTotal = 0;
            _splitScanProcessed = 0;
            _repairScanTotal = 0;
            _repairScanProcessed = 0;
            NotifyFooterProperties();
        }

        public void NotifyFooterProperties()
        {
            OnPropertyChanged(nameof(FooterStatusText));
            OnPropertyChanged(nameof(FooterProgress));
            OnPropertyChanged(nameof(FooterProgressBarValue));
            OnPropertyChanged(nameof(FooterIsIndeterminate));
            OnPropertyChanged(nameof(FooterPercentText));
            OnPropertyChanged(nameof(FooterPercentVisibility));
            OnPropertyChanged(nameof(FooterProgressBarState));
        }

        private AppViewModel()
        {
            Combo = new ComboViewModel();
            Split = new SplitViewModel();
            Repair = new RepairViewModel();
            Home = new HomeViewModel();
            Settings = new SettingsViewModel();
            About = new AboutViewModel();
            KeyPhoto = new KeyPhotoViewModel();

            SubscribeToChildStatusChanges();
            SubscribeHomeNavigation();
            InitializeAsync();
        }

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

        public void SetCurrentStatusPage(string? pageTag)
        {
            CurrentStatusPageTag = pageTag;
        }

        [RelayCommand]
        private void GoToTutorial(string feature)
        {
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }

        private void SubscribeToChildStatusChanges()
        {
            Combo.StatusChanged += OnChildStatusChanged;
            Split.StatusChanged += OnChildStatusChanged;
            Repair.StatusChanged += OnChildStatusChanged;

            Combo.PropertyChanged += OnChildPropertyChangedHandler;
            Split.PropertyChanged += OnChildPropertyChangedHandler;
            Repair.PropertyChanged += OnChildPropertyChangedHandler;

            PropertyChanged += OnPropertyChangedHandler;
        }

        private void OnChildStatusChanged(object? sender, EventArgs e)
        {
            NotifyFooterProperties();
        }

        private void OnChildPropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null) return;

            switch (e.PropertyName)
            {
                case "IsScanning":
                case "IsProcessing":
                case "ComboProgress":
                case "Progress":
                case "Status":
                case "ProgressBarState":
                    NotifyFooterProperties();
                    break;
            }
        }

        private void OnPropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CurrentStatusPageTag))
            {
                NotifyFooterProperties();
            }
        }

        private void SubscribeHomeNavigation()
        {
            Home.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
            Combo.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
            Split.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
            Repair.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
        }

        public void Cleanup()
        {
            Combo.Cleanup();
            Split.Cleanup();
            Repair.Cleanup();
        }
    }
}