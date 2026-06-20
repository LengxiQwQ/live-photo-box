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

        #region Banner Settings

        public List<BannerPreset> BannerPresets { get; } = new()
        {
            new BannerPreset { Name = "预设 1 — 默认风景", Key = "default", AssetPath = "ms-appx:///Assets/Banners/banner_01.jpg" },
            new BannerPreset { Name = "预设 2 — 海岸风光", Key = "scenic",   AssetPath = "ms-appx:///Assets/Banners/banner_02.jpg" },
            new BannerPreset { Name = "预设 3 — 动漫画风", Key = "anime",    AssetPath = "ms-appx:///Assets/Banners/banner_03.jpg" },
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
                    return BannerPresets[BannerPresetIndex].Name;
                return BannerPresets.Count > 0 ? BannerPresets[0].Name : "";
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

        #region Combo Settings

        [ObservableProperty]
        private int _heicDecoderIndex;

        partial void OnHeicDecoderIndexChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(HeicDecoderIndex), value);
            LogService.Info($"HEIC decoder changed to: {(value == 0 ? "Magick.NET" : "Windows BitmapDecoder")}", LogSource.Settings);
        }

        [ObservableProperty]
        private int _comboThreadCount = 4;

        partial void OnComboThreadCountChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("ComboThreadCount", value);
            LogService.Info($"Combo thread count changed to: {value}", LogSource.Settings);
        }

        [RelayCommand]
        private void IncreaseComboThreadCount()
        {
            if (ComboThreadCount < 8) ComboThreadCount++;
        }

        [RelayCommand]
        private void DecreaseComboThreadCount()
        {
            if (ComboThreadCount > 1) ComboThreadCount--;
        }

        #endregion

        #region Split Settings

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
                // 新增：按 codec 独立保存（H.264 / HEVC）
                if (!string.IsNullOrEmpty(value.FfmpegEncoder))
                {
                    string lower = value.FfmpegEncoder.ToLowerInvariant();
                    if (lower.StartsWith("h264_"))
                    {
                        AppSettingsService.SetValue("SplitEncoder_h264", value.FfmpegEncoder);
                    }
                    else if (lower.StartsWith("hevc_"))
                    {
                        AppSettingsService.SetValue("SplitEncoder_hevc", value.FfmpegEncoder);
                    }
                }
                LogService.Split($"Saved encoder to settings: '{value.FfmpegEncoder}'", LogLevel.Debug);
            }
        }

        [ObservableProperty]
        private int _threadCount = 5;

        partial void OnThreadCountChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("SplitThreadCount", value);
            LogService.Info($"Split thread count changed to: {value}", LogSource.Settings);
        }

        [ObservableProperty]
        private int _maxThreadCount = 16;

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
            BannerPresetIndex = AppSettingsService.GetValue(nameof(BannerPresetIndex), 0);
            IsBannerRandomEnabled = AppSettingsService.GetValue(nameof(IsBannerRandomEnabled), false);
            ThreadCount = AppSettingsService.GetValue("SplitThreadCount", 5);
            MaxThreadCount = Math.Min(Environment.ProcessorCount, 16);
            HeicDecoderIndex = AppSettingsService.GetValue(nameof(HeicDecoderIndex), 0);
            ComboThreadCount = AppSettingsService.GetValue("ComboThreadCount", 4);
            IsHeicRepairEnabled = AppSettingsService.GetValue(nameof(IsHeicRepairEnabled), false);
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

                // 初始化完成后，确保编码器被保存
                AppSettingsService.SetValue("SplitHardwareIndex", AvailableHardware.IndexOf(hardwareToSelect));
                AppSettingsService.SetValue("SplitHardwareEncoder", hardwareToSelect.FfmpegEncoder ?? string.Empty);
                // 新增：按 codec 独立保存
                if (!string.IsNullOrEmpty(hardwareToSelect.FfmpegEncoder))
                {
                    string lower = hardwareToSelect.FfmpegEncoder.ToLowerInvariant();
                    if (lower.StartsWith("h264_"))
                    {
                        AppSettingsService.SetValue("SplitEncoder_h264", hardwareToSelect.FfmpegEncoder);
                    }
                    else if (lower.StartsWith("hevc_"))
                    {
                        AppSettingsService.SetValue("SplitEncoder_hevc", hardwareToSelect.FfmpegEncoder);
                    }
                }
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
            LanguageIndex = 0;
            BackdropIndex = 0;
            ElementTheme = 0;

            // 恢复 Banner 设置
            IsBannerRandomEnabled = false;
            BannerPresetIndex = 0;

            // 恢复拆分设置为默认值
            ThreadCount = 5;

            // 清除保存的硬件选择，下次启动时重新检测最佳硬件
            AppSettingsService.SetValue("SplitHardwareIndex", -1);
            AppSettingsService.SetValue("SplitHardwareEncoder", string.Empty);
            AppSettingsService.SetValue("SplitEncoder_h264", string.Empty);
            AppSettingsService.SetValue("SplitEncoder_hevc", string.Empty);

            // 重置拆分页面的视频格式选择（默认视频格式）
            AppViewModel.Instance.Split.SelectedFormatIndex = 0;

            // 重置合成页面的协议版本（默认V2版本）
            AppViewModel.Instance.Combo.SelectedModeIndex = 1;

            // 重置修复页面的输出模式（默认关闭）
            AppViewModel.Instance.Repair.IsOutputToDirectory = false;

            // 重置 HEIC 解码器为默认（Magick.NET）
            HeicDecoderIndex = 0;

            // 重置合成任务并行数
            ComboThreadCount = 4;

            // 重置 HEIC 修复设置（默认关闭）
            IsHeicRepairEnabled = false;

            // 重新选择最佳硬件
            _isInitializing = true;
            var gpu = AvailableHardware.FirstOrDefault(h => h.Type == HardwareService.HardwareType.Gpu && h.IsHardwareEncodingSupported);
            if (gpu != null)
            {
                SelectedHardware = gpu;
            }
            else
            {
                var cpu = AvailableHardware.FirstOrDefault(h => h.Type == HardwareService.HardwareType.Cpu);
                if (cpu != null)
                {
                    SelectedHardware = cpu;
                }
            }
            _isInitializing = false;

            // 刷新设置以应用更改
            LogService.Split("Settings restored to defaults. Hardware selection re-evaluated.", LogLevel.Info);
        }
    }
}