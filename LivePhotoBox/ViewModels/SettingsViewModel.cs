using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
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

        public SettingsViewModel()
        {
            LoadSettings();
            // 采用异步加载，不再阻塞构造函数，从而解放主线程
            _ = LoadHardwareInfoAsync();
        }

        private void LoadSettings()
        {
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
            ThreadCount = AppSettingsService.GetValue("SplitThreadCount", 5);
            MaxThreadCount = Math.Min(Environment.ProcessorCount, 16);
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