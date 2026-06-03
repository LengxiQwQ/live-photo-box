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

        #region Page ViewModels

        public ComboViewModel Combo { get; }
        public SplitViewModel Split { get; }
        public RepairViewModel Repair { get; }
        public HomeViewModel Home { get; }
        public SettingsViewModel Settings { get; }
        public AboutViewModel About { get; }
        public KeyPhotoViewModel KeyPhoto { get; }

        #endregion

        #region Events

        public event EventHandler<string>? RequestNavigateToPage;

        #endregion

        #region Properties

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

        #endregion

        #region Footer Status Bar

        private int _splitScanTotal;
        private int _splitScanProcessed;
        private int _comboScanTotal;
        private int _comboScanProcessed;
        private int _repairScanTotal;
        private int _repairScanProcessed;

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
                return CurrentStatusPageTag switch
                {
                    "Combo" when Combo.IsScanning && _comboScanTotal > 0 =>
                        Math.Clamp(_comboScanProcessed * 100.0 / _comboScanTotal, 0, 100),
                    "Combo" when Combo.IsProcessing => Combo.ComboProgress,
                    "Combo" when _comboScanTotal > 0 =>
                        Math.Clamp(_comboScanProcessed * 100.0 / _comboScanTotal, 0, 100),
                    "Split" when Split.IsScanning && _splitScanTotal > 0 =>
                        Math.Clamp(_splitScanProcessed * 100.0 / _splitScanTotal, 0, 100),
                    "Split" when Split.IsProcessing => Split.Progress,
                    "Split" when _splitScanTotal > 0 =>
                        Math.Clamp(_splitScanProcessed * 100.0 / _splitScanTotal, 0, 100),
                    "Repair" when Repair.IsScanning && _repairScanTotal > 0 =>
                        Math.Clamp(_repairScanProcessed * 100.0 / _repairScanTotal, 0, 100),
                    "Repair" when Repair.IsProcessing => Repair.Progress,
                    "Repair" when _repairScanTotal > 0 =>
                        Math.Clamp(_repairScanProcessed * 100.0 / _repairScanTotal, 0, 100),
                    _ => 0
                };
            }
        }

        public bool FooterIsIndeterminate =>
            (CurrentStatusPageTag == "Combo" && Combo.IsScanning)
            || (CurrentStatusPageTag == "Split" && Split.IsScanning);

        public double FooterProgressBarValue => FooterIsIndeterminate ? 0 : FooterProgress;

        public string FooterPercentText
        {
            get
            {
                if (FooterIsIndeterminate)
                {
                    return ResourceService.Format("StatusBar_ScanProgressLabel", "?");
                }

                if (FooterProgress <= 0 && !Combo.IsScanning && !Split.IsScanning && !Repair.IsScanning
                    && !Combo.IsProcessing && !Split.IsProcessing && !Repair.IsProcessing)
                {
                    return string.Empty;
                }

                int percent = (int)Math.Round(FooterProgress);
                bool isScanPhase = Combo.IsScanning || Split.IsScanning || Repair.IsScanning
                    || (_splitScanTotal > 0 && !Split.IsProcessing)
                    || (_comboScanTotal > 0 && !Combo.IsProcessing)
                    || (_repairScanTotal > 0 && !Repair.IsProcessing);

                string key = isScanPhase && !Combo.IsProcessing && !Split.IsProcessing && !Repair.IsProcessing
                    ? "StatusBar_ScanProgressLabel"
                    : "StatusBar_ProcessProgressLabel";

                return ResourceService.Format(key, percent);
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
        }

        #endregion

        #region Constructor

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

        #endregion

        #region Initialization

        private async void InitializeAsync()
        {
            try
            {
                LanguageService.ApplyLanguageOverride(LanguageService.GetEffectiveLanguage(Settings.LanguageIndex));
                AppLogService.Info("AppViewModel initialized.");
            }
            catch (Exception ex)
            {
                AppLogService.Error("AppViewModel initialization failed", ex, LogSource.App);
            }
            await Task.CompletedTask;
        }

        #endregion

        #region Navigation

        public void SetCurrentStatusPage(string? pageTag)
        {
            CurrentStatusPageTag = pageTag;
        }

        [RelayCommand]
        private void GoToTutorial(string feature)
        {
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }

        #endregion

        #region Status Subscription

        private void SubscribeToChildStatusChanges()
        {
            Combo.StatusChanged += OnChildStatusChanged;
            Split.StatusChanged += OnChildStatusChanged;
            Repair.StatusChanged += OnChildStatusChanged;

            PropertyChanged += OnPropertyChangedHandler;
        }

        private void OnChildStatusChanged(object? sender, EventArgs e)
        {
            NotifyFooterProperties();
        }

        private void OnPropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null) return;

            switch (e.PropertyName)
            {
                case nameof(CurrentStatusPageTag):
                case "IsScanning":
                case "IsProcessing":
                case "ComboProgress":
                case "Progress":
                case "Status":
                    NotifyFooterProperties();
                    break;
            }
        }

        #endregion

        #region Home Navigation Subscription

        private void SubscribeHomeNavigation()
        {
            Home.RequestNavigateToPage += (s, tag) => RequestNavigateToPage?.Invoke(this, tag);
        }

        #endregion

        #region Cleanup

        public void Cleanup()
        {
            Combo.Cleanup();
            Split.Cleanup();
            Repair.Cleanup();
        }

        #endregion
    }
}
