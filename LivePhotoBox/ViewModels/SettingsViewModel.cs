using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        private bool _isInitializing;

        public override string? PageStatusTag => null;

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
        }

        [ObservableProperty]
        private int _backdropIndex;

        partial void OnBackdropIndexChanged(int value)
        {
            AppSettingsService.SetValue(nameof(BackdropIndex), value);
        }

        #region Split Settings

        [ObservableProperty]
        private ObservableCollection<HardwareService.HardwareInfo> _availableHardware = new();

        [ObservableProperty]
        private HardwareService.HardwareInfo? _selectedHardware;

        partial void OnSelectedHardwareChanged(HardwareService.HardwareInfo? value)
        {
            AppLogService.Split($"[DEBUG] OnSelectedHardwareChanged: _isInitializing={_isInitializing}, value={value?.Name ?? "(null)"}, encoder={value?.FfmpegEncoder ?? "(null)"}", LogLevel.Info);
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
                AppLogService.Split($"[DEBUG] Saved encoder to settings: '{value.FfmpegEncoder}'", LogLevel.Info);
            }
        }

        [ObservableProperty]
        private int _threadCount = 5;

        partial void OnThreadCountChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("SplitThreadCount", value);
        }

        [ObservableProperty]
        private int _maxThreadCount = 16;

        #endregion

        public SettingsViewModel()
        {
            LoadSettings();
            LoadHardwareInfo();
        }

        private void LoadSettings()
        {
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
            ThreadCount = AppSettingsService.GetValue("SplitThreadCount", 5);
            MaxThreadCount = Math.Min(Environment.ProcessorCount, 16);
        }

        private void LoadHardwareInfo()
        {
            var hardware = HardwareService.GetAvailableHardware();
            AvailableHardware.Clear();
            foreach (var h in hardware)
            {
                AvailableHardware.Add(h);
            }

            // 直接设置选中项，让绑定系统处理更新
            SetHardwareSelection();
        }

        private void SetHardwareSelection()
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
                // 自动选择最佳硬件（优先 GPU）
                var recommended = HardwareService.GetRecommendedHardware();
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
            AppLogService.Split("Settings restored to defaults. Hardware selection re-evaluated.", LogLevel.Info);
        }
    }
}
