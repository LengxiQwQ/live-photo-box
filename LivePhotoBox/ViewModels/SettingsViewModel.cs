using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private bool _isInitializing;

        public override string? PageStatusTag => null;

        [ObservableProperty]
        private bool _isHardwareLoading; // 新增：用于在UI显示硬件加载状态（可选绑定转圈圈动画）

        [ObservableProperty]
        private int _languageIndex;

        partial void OnLanguageIndexChanged(int value)
        {
            string previousLanguage = LanguageService.GetCurrentLanguageTag();
            string targetLanguage = LanguageService.GetEffectiveLanguage(value);

            AppSettingsService.SetValue(nameof(LanguageIndex), value);

            if (_isInitializing)
            {
                return;
            }

            LogService.Info($"Language changed from {previousLanguage} to {targetLanguage}", LogSource.Settings);

            LanguageService.ApplyLanguageOverride(targetLanguage);

            if (!LanguageService.HasEffectiveLanguageChanged(previousLanguage, targetLanguage))
            {
                return;
            }

            _ = LanguageService.ShowRestartPromptAsync(targetLanguage);
        }

        [ObservableProperty]
        private int _elementTheme;

        partial void OnElementThemeChanged(int value)
        {
            AppSettingsService.SetValue(nameof(ElementTheme), value);
            LogService.Info($"Theme changed to: {(ElementTheme)value}", LogSource.Settings);
        }

        [ObservableProperty]
        private int _backdropIndex;

        partial void OnBackdropIndexChanged(int value)
        {
            AppSettingsService.SetValue(nameof(BackdropIndex), value);
            LogService.Info($"Backdrop changed to index: {value}", LogSource.Settings);
        }

        /// <summary>Acrylic 着色浓度 (0.0–1.0)，仅在 BackdropIndex 为 2/3 时生效</summary>
        [ObservableProperty]
        private double _acrylicTintOpacity = 0.5;

        partial void OnAcrylicTintOpacityChanged(double value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(AcrylicTintOpacity), value);
            OnPropertyChanged(nameof(AcrylicTintOpacityText));
            LogService.Info($"Acrylic tint opacity: {value:F2}", LogSource.Settings);
        }

        /// <summary>窗口整体透明度 (0.1–1.0)，1.0 = 完全不透明</summary>
        [ObservableProperty]
        private double _windowOpacity = 1.0;

        partial void OnWindowOpacityChanged(double value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(WindowOpacity), value);
            OnPropertyChanged(nameof(WindowOpacityText));
            LogService.Info($"Window opacity: {value:F2}", LogSource.Settings);
        }

        /// <summary>窗口透明度的步长（0.05）— 供 Slider 使用</summary>
        public double OpacityStepFrequency => 0.05;

        public string AcrylicTintOpacityText => $"{AcrylicTintOpacity * 100:F0}%";
        public string WindowOpacityText => $"{WindowOpacity * 100:F0}%";

        #region Banner Settings

        public List<BannerPreset> BannerPresets { get; } = new()
        {
            new BannerPreset { Name = "BannerPreset_Name_default", Key = "default", AssetPath = "ms-appx:///Assets/Banners/banner_01.jpg" },
            new BannerPreset { Name = "BannerPreset_Name_scenic", Key = "scenic",   AssetPath = "ms-appx:///Assets/Banners/banner_02.jpg" },
            new BannerPreset { Name = "BannerPreset_Name_anime", Key = "anime",    AssetPath = "ms-appx:///Assets/Banners/banner_03.jpg" },
        };

        /// <summary>预加载的 Banner BitmapImage，切换时只改引用不重新解码</summary>
        private readonly List<BitmapImage> _preloadedBanners = new();

        [ObservableProperty]
        private int _bannerPresetIndex;

        partial void OnBannerPresetIndexChanged(int value)
        {
            if (_isInitializing) return;

            if (value < 0 || value >= BannerPresets.Count)
            {
                value = 0;
                _bannerPresetIndex = 0;
            }

            AppSettingsService.SetValue(nameof(BannerPresetIndex), value);
            App.RefreshBannerImage(BannerPresets[value]);
            OnPropertyChanged(nameof(CurrentBannerPresetName));
            OnPropertyChanged(nameof(Banner0Visible));
            OnPropertyChanged(nameof(Banner1Visible));
            OnPropertyChanged(nameof(Banner2Visible));
            LogService.Info($"Banner preset changed to: {BannerPresets[value].Name} (index {value})", LogSource.Settings);
        }

        [ObservableProperty]
        private bool _isBannerRandomEnabled;

        partial void OnIsBannerRandomEnabledChanged(bool value)
        {
            AppSettingsService.SetValue(nameof(IsBannerRandomEnabled), value);
            LogService.Info($"Banner random mode: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        public string CurrentBannerPresetName
        {
            get
            {
                if (BannerPresetIndex >= 0 && BannerPresetIndex < BannerPresets.Count)
                    return ResourceService.GetString(BannerPresets[BannerPresetIndex].Name);
                return BannerPresets.Count > 0 ? ResourceService.GetString(BannerPresets[0].Name) : "";
            }
        }

        /// <summary>三张预加载 Banner 的图片源，供 Image 控件直接绑定（切换时不换 Source，只换 Visibility）</summary>
        public BitmapImage? BannerImage0 => _preloadedBanners.Count > 0 ? _preloadedBanners[0] : null;
        public BitmapImage? BannerImage1 => _preloadedBanners.Count > 1 ? _preloadedBanners[1] : null;
        public BitmapImage? BannerImage2 => _preloadedBanners.Count > 2 ? _preloadedBanners[2] : null;

        public Visibility Banner0Visible => BannerPresetIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility Banner1Visible => BannerPresetIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility Banner2Visible => BannerPresetIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        public void PrevBanner()
        {
            if (BannerPresets.Count == 0) return;
            int newIndex = BannerPresetIndex - 1;
            if (newIndex < 0) newIndex = BannerPresets.Count - 1;
            BannerPresetIndex = newIndex;
        }

        public void NextBanner()
        {
            if (BannerPresets.Count == 0) return;
            int newIndex = BannerPresetIndex + 1;
            if (newIndex >= BannerPresets.Count) newIndex = 0;
            BannerPresetIndex = newIndex;
        }

        #endregion

        #region Merge Settings

        [ObservableProperty]
        private int _heicDecoderIndex;

        partial void OnHeicDecoderIndexChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(HeicDecoderIndex), value);
            LogService.Info($"HEIC decoder changed to: {(value == 0 ? "Magick.NET" : "Windows BitmapDecoder")}", LogSource.Settings);
        }

        [ObservableProperty]
        private bool _isGoogleProtocolForceMp4;

        partial void OnIsGoogleProtocolForceMp4Changed(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsGoogleProtocolForceMp4), value);
            LogService.Info($"Google protocol force-MP4: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        [ObservableProperty]
        private int _mergeThreadCount = 5;

        partial void OnMergeThreadCountChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("MergeThreadCount", value);
            LogService.Info($"Merge thread count changed to: {value}", LogSource.Settings);
        }

        /// <summary>合成并行数最大值</summary>
        public int MaxMergeThreadCount => 10;

        [RelayCommand]
        private void IncreaseMergeThreadCount()
        {
            if (MergeThreadCount < MaxMergeThreadCount) MergeThreadCount++;
        }

        [RelayCommand]
        private void DecreaseMergeThreadCount()
        {
            if (MergeThreadCount > 1) MergeThreadCount--;
        }

        /// <summary>实况照片配对方式：文件名+元数据 / 仅文件名 / 仅元数据</summary>
        [ObservableProperty]
        private int _metadataMatchingModeIndex;

        partial void OnMetadataMatchingModeIndexChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(MetadataMatchingModeIndex), value);
            LogService.Info($"Metadata matching mode changed to index: {value}", LogSource.Settings);
        }

        #endregion

        #region Split Settings

        [ObservableProperty]
        private int _splitFormatIndex;

        partial void OnSplitFormatIndexChanged(int value)
        {
            if (_isInitializing) return;
            // Use same key as SplitViewModel.SelectedFormatIndex so they share the setting
            AppSettingsService.SetValue("SelectedFormatIndex", value);
            LogService.Info($"Split default format changed to index: {value}", LogSource.Settings);
        }

        [ObservableProperty]
        private ObservableCollection<HardwareService.HardwareInfo> _availableHardware = new();

        [ObservableProperty]
        private HardwareService.HardwareInfo? _selectedHardware;

        partial void OnSelectedHardwareChanged(HardwareService.HardwareInfo? value)
        {
            LogService.Split($"OnSelectedHardwareChanged: _isInitializing={_isInitializing}, value={value?.Name ?? "(null)"}, encoder={value?.FfmpegEncoder ?? "(null)"}", LogLevel.Debug);
            if (_isInitializing || value == null) return;
            int index = AvailableHardware.IndexOf(value);
            if (index >= 0)
            {
                AppSettingsService.SetValue("SplitHardwareIndex", index);
                AppSettingsService.SetValue("SplitHardwareEncoder", value.FfmpegEncoder);
                EncoderHelper.SaveEncoderForBothCodecs(value.FfmpegEncoder);
                LogService.Split($"Saved encoder to settings: '{value.FfmpegEncoder}'", LogLevel.Debug);
            }
        }

        /// <summary>
        /// 根据一个 codec 的编码器名称，同时保存 H.264 和 HEVC 两个 codec 的编码器设置。
        /// 委托给 EncoderHelper（集中管理编码器逻辑，不再依赖 VideoTranscodeService）。
        /// </summary>
        // SaveEncoderForBothCodecs → EncoderHelper.SaveEncoderForBothCodecs

        [ObservableProperty]
        private int _threadCount = 8;

        partial void OnThreadCountChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("SplitThreadCount", value);
            LogService.Info($"Split thread count changed to: {value}", LogSource.Settings);
        }

        [ObservableProperty]
        private int _maxThreadCount = 20;

        [ObservableProperty]
        private int _heicConcurrency = 8;

        partial void OnHeicConcurrencyChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("HeicConcurrency", value);
            LogService.Info($"HEIC concurrency changed to: {value}", LogSource.Settings);
        }

        public int MaxHeicConcurrency => 64;

        #endregion

        #region Repair Settings

        [ObservableProperty]
        private bool _isHeicRepairEnabled;

        partial void OnIsHeicRepairEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsHeicRepairEnabled), value);
            LogService.Info($"HEIC repair setting changed to: {(value ? "enabled" : "disabled")}", LogSource.Settings);
        }

        /// <summary>修复输出模式 — 开启时修复到单独目录，关闭时原地替换</summary>
        [ObservableProperty]
        private bool _isRepairOutputToDirectory;

        partial void OnIsRepairOutputToDirectoryChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("IsOutputToDirectory", value);
            // 同步到 RepairViewModel（防御性 null 检查，初始化顺序可能导致 Repair 尚未创建）
            if (AppViewModel.Instance?.Repair != null)
                AppViewModel.Instance.Repair.IsOutputToDirectory = value;
            LogService.Info($"Repair output mode: {(value ? "separate directory" : "in-place")}", LogSource.Settings);
        }

        /// <summary>修复非实况照片的视频 — 开启后同时修复 > 3.5s 的普通长视频</summary>
        [ObservableProperty]
        private bool _isNonLivePhotoVideoRepairEnabled;

        partial void OnIsNonLivePhotoVideoRepairEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsNonLivePhotoVideoRepairEnabled), value);
            LogService.Info($"Repair non-live-photo video setting: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        #endregion

        #region History / Inspector Settings

        /// <summary>是否在导航栏显示"照片历史"页面（默认隐藏）</summary>
        [ObservableProperty]
        private bool _isHistoryPageVisible;

        partial void OnIsHistoryPageVisibleChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsHistoryPageVisible), value);
            LogService.Info($"History page visibility: {(value ? "shown" : "hidden")}", LogSource.Settings);
        }

        #endregion

        #region Debug / Test Tools

        /// <summary>修复页面扫描时加载视频缩略图（默认关 = 不加载）</summary>
        [ObservableProperty]
        private bool _isRepairScanLoadThumbnail;

        partial void OnIsRepairScanLoadThumbnailChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsRepairScanLoadThumbnail), value);
            LogService.Info($"Repair scan load thumbnail: {(value ? "enabled" : "disabled")}", LogSource.Settings);
        }

        /// <summary>更严格的实况照片扫描 — 通过 ContentIdentifier UUID 匹配（默认关 = 文件名匹配）</summary>
        [ObservableProperty]
        private bool _isStrictLivePhotoScanEnabled;

        partial void OnIsStrictLivePhotoScanEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsStrictLivePhotoScanEnabled), value);
            LogService.Info($"Strict Live Photo scan: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        /// <summary>
        /// 详细操作记录开关（默认关闭）
        /// 关闭后仅标记经本软件处理过（合成/拆分/修复），不通过 dc:subject 写入具体更改内容
        /// </summary>
        [ObservableProperty]
        private bool _isDetailedHistoryEnabled;

        partial void OnIsDetailedHistoryEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsDetailedHistoryEnabled), value);
            LogService.Info($"Detailed history recording: {(value ? "enabled" : "disabled")}", LogSource.Settings);
        }

        #endregion

        public SettingsViewModel()
        {
            LoadSettings();
            // 硬件信息异步加载（Banner 预加载延迟到打开设置页面时再触发）
            _ = LoadHardwareInfoAsync();
        }

        /// <summary>
        /// 进入设置页面时调用：预加载 Banner → 通知 UI。
        /// 只执行一次（_preloadedBanners 非空则跳过）。
        /// 使用 SetSourceAsync 强制立即解码，避免 UriSource 懒加载导致切换闪烁。
        /// </summary>
        public async Task EnsureBannersPreloadedAsync()
        {
            if (_preloadedBanners.Count > 0) return;  // 已加载，跳过
            await PreloadBannersAsync();
            OnPropertyChanged(nameof(BannerImage0));
            OnPropertyChanged(nameof(BannerImage1));
            OnPropertyChanged(nameof(BannerImage2));
            OnPropertyChanged(nameof(Banner0Visible));
        }

        /// <summary>用 SetSourceAsync 强制解码所有 Banner，返回时图片已在内存中</summary>
        private async Task PreloadBannersAsync()
        {
            foreach (var preset in BannerPresets)
            {
                var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(
                    new Uri(preset.AssetPath));
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 640 };
                await bitmap.SetSourceAsync(stream);
                _preloadedBanners.Add(bitmap);
            }
        }

        private void LoadSettings()
        {
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
            WindowOpacity = AppSettingsService.GetValue(nameof(WindowOpacity), 1.0);
            AcrylicTintOpacity = AppSettingsService.GetValue(nameof(AcrylicTintOpacity), 0.2);
            BannerPresetIndex = AppSettingsService.GetValue(nameof(BannerPresetIndex), 0);
            IsBannerRandomEnabled = AppSettingsService.GetValue(nameof(IsBannerRandomEnabled), false);
            ThreadCount = AppSettingsService.GetValue("SplitThreadCount", 8);
            MaxThreadCount = Math.Min(Environment.ProcessorCount, 20);
            HeicConcurrency = AppSettingsService.GetValue("HeicConcurrency", 8);
            HeicDecoderIndex = AppSettingsService.GetValue(nameof(HeicDecoderIndex), 0);
            IsGoogleProtocolForceMp4 = AppSettingsService.GetValue(nameof(IsGoogleProtocolForceMp4), false);
            MergeThreadCount = AppSettingsService.GetValue("MergeThreadCount", 5);
            MetadataMatchingModeIndex = AppSettingsService.GetValue(nameof(MetadataMatchingModeIndex), 0);
            IsHeicRepairEnabled = AppSettingsService.GetValue(nameof(IsHeicRepairEnabled), false);
            IsRepairOutputToDirectory = AppSettingsService.GetValue("IsOutputToDirectory", false);
            IsRepairScanLoadThumbnail = AppSettingsService.GetValue(nameof(IsRepairScanLoadThumbnail), false);
            IsStrictLivePhotoScanEnabled = AppSettingsService.GetValue(nameof(IsStrictLivePhotoScanEnabled), false);
            IsNonLivePhotoVideoRepairEnabled = AppSettingsService.GetValue(nameof(IsNonLivePhotoVideoRepairEnabled), false);
            SplitFormatIndex = AppSettingsService.GetValue("SelectedFormatIndex", 0);
            IsHistoryPageVisible = AppSettingsService.GetValue(nameof(IsHistoryPageVisible), false);
            IsDetailedHistoryEnabled = AppSettingsService.GetValue(nameof(IsDetailedHistoryEnabled), false);
        }

        private async Task LoadHardwareInfoAsync()
        {
            IsHardwareLoading = true;
            try
            {
                // 后台线程重型计算：读取 WMI 和启动 FFmpeg
                var hardware = await HardwareService.GetAvailableHardwareAsync();

                // 为了防止跨线程操作 UI 绑定的集合，确保跑在 UI 线程上
                if (App.MainWindow?.DispatcherQueue != null)
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        ApplyHardwareList(hardware);
                    });
                }
                else
                {
                    ApplyHardwareList(hardware);
                }
            }
            catch (Exception ex)
            {
                LogService.Split($"Failed to load hardware async: {ex.Message}", LogLevel.Error);
                IsHardwareLoading = false;
            }
        }

        private void ApplyHardwareList(List<HardwareService.HardwareInfo> hardware)
        {
            AvailableHardware.Clear();
            foreach (var h in hardware)
            {
                AvailableHardware.Add(h);
            }

            SetHardwareSelection(hardware);
            IsHardwareLoading = false;
        }

        private void SetHardwareSelection(List<HardwareService.HardwareInfo> hardware)
        {
            if (AvailableHardware.Count == 0) return;

            HardwareService.HardwareInfo? hardwareToSelect = null;

            // 如果有保存的选择，使用保存的值
            int savedIndex = AppSettingsService.GetValue("SplitHardwareIndex", -1);
            if (savedIndex >= 0 && savedIndex < AvailableHardware.Count)
            {
                hardwareToSelect = AvailableHardware[savedIndex];
            }
            else
            {
                // 自动选择最佳硬件，传入已获取的列表避免再次触发WMI卡顿
                var recommended = HardwareService.GetRecommendedHardwareFromList(hardware);
                if (recommended != null)
                {
                    hardwareToSelect = AvailableHardware.FirstOrDefault(h =>
                        h.Name == recommended.Name && h.Type == recommended.Type);

                    // 如果找不到完全匹配的，选择第一个支持的 GPU
                    if (hardwareToSelect == null)
                    {
                        hardwareToSelect = AvailableHardware.FirstOrDefault(h =>
                            h.Type == HardwareService.HardwareType.Gpu && h.IsHardwareEncodingSupported);
                    }
                }

                // 如果没有找到合适的 GPU，选择第一个硬件
                hardwareToSelect ??= AvailableHardware[0];
            }

            if (hardwareToSelect != null)
            {
                _isInitializing = true;
                SelectedHardware = hardwareToSelect;
                _isInitializing = false;

                // 初始化完成后，确保编码器被保存（两个 codec 都要存）
                AppSettingsService.SetValue("SplitHardwareIndex", AvailableHardware.IndexOf(hardwareToSelect));
                AppSettingsService.SetValue("SplitHardwareEncoder", hardwareToSelect.FfmpegEncoder ?? string.Empty);
                EncoderHelper.SaveEncoderForBothCodecs(hardwareToSelect.FfmpegEncoder);
            }
        }

        [RelayCommand]
        private async Task RefreshHardwareAsync()
        {
            IsHardwareLoading = true;
            try
            {
                // 清除硬件缓存，强制重新检测
                HardwareService.ClearHardwareCache();
                // 重新检测硬件
                await LoadHardwareInfoAsync();
            }
            catch (Exception ex)
            {
                LogService.Split($"Failed to refresh hardware: {ex.Message}", LogLevel.Error);
                IsHardwareLoading = false;
            }
        }

        [RelayCommand]
        private void IncreaseThreadCount()
        {
            if (ThreadCount < MaxThreadCount)
            {
                ThreadCount++;
            }
        }

        [RelayCommand]
        private void DecreaseThreadCount()
        {
            if (ThreadCount > 1)
            {
                ThreadCount--;
            }
        }

        [RelayCommand]
        private void RestoreDefaultSettings()
        {
            // 1. 清空所有已保存设置 → 下次读取全部走默认值
            AppSettingsService.ClearAll();

            // 2. 重新从默认值加载 → UI 刷新 + OnChanged 回写默认值
            LoadSettings();

            // 3. 重置硬件选择（LoadSettings 不覆盖的复杂设置）
            AppSettingsService.SetValue("SplitHardwareIndex", -1);
            AppSettingsService.SetValue("SplitHardwareEncoder", string.Empty);
            AppSettingsService.SetValue("SplitEncoder_h264", string.Empty);
            AppSettingsService.SetValue("SplitEncoder_hevc", string.Empty);

            AppViewModel.Instance.Split.SelectedFormatIndex = 0;
            AppViewModel.Instance.Merge.SelectedModeIndex = 1;
            AppViewModel.Instance.Repair.IsOutputToDirectory = false;

            // 4. 重置页面偏好为现代版
            AppSettingsService.SetValue("UseClassicSettingsPage", false);

            // 5. 重新选择最佳硬件
            _isInitializing = true;
            var gpu = AvailableHardware.FirstOrDefault(h => h.Type == HardwareService.HardwareType.Gpu && h.IsHardwareEncodingSupported);
            if (gpu != null)
                SelectedHardware = gpu;
            else
            {
                var cpu = AvailableHardware.FirstOrDefault(h => h.Type == HardwareService.HardwareType.Cpu);
                if (cpu != null) SelectedHardware = cpu;
            }
            _isInitializing = false;

            AppSettingsService.SetValue("SplitHardwareIndex", AvailableHardware.IndexOf(SelectedHardware!));
            AppSettingsService.SetValue("SplitHardwareEncoder", SelectedHardware?.FfmpegEncoder ?? string.Empty);
            EncoderHelper.SaveEncoderForBothCodecs(SelectedHardware?.FfmpegEncoder);

            LogService.Split("All settings restored to defaults via ClearAll+LoadSettings.", LogLevel.Info);
        }
    }
}