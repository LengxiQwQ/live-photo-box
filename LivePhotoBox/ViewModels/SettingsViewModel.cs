using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.ViewModels
{
    // 设置页面的 ViewModel。
    // 管理所有应用设置项（语言、主题、背景、Banner、合并/拆分/修复参数、硬件编码等），
    // 提供默认值加载、持久化保存和 UI 双向绑定支持。
    // 继承自 ViewModelBase（无扫描/处理生命周期）。
    public partial class SettingsViewModel : ViewModelBase
    {
        // 初始化中标志位，避免初始化期间触发的 OnChanged 重复写入设置。
        private bool _isInitializing;

        // 该页面不在导航栏显示状态标签，返回 null。
        public override string? PageStatusTag => null;

        // 硬件信息是否正在加载中（用于 UI 显示加载动画）。
        [ObservableProperty]
        private bool _isHardwareLoading;

        // 当前选择的语言索引，写入 AppSettings 并应用语言覆盖。
        [ObservableProperty]
        private int _languageIndex;

        partial void OnLanguageIndexChanged(int value)
        {
            // 用上一次保存的 LanguageIndex 推算切换前的有效语言，
            // 不依赖 WinRT 的 GetCurrentLanguageTag()（非打包模式下不可用，永远返回 "en-US"）。
            int previousIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            string previousLanguage = LanguageService.GetEffectiveLanguage(previousIndex);
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

        // 当前选择的主题索引（0=默认, 1=浅色, 2=深色）。
        [ObservableProperty]
        private int _elementTheme;

        partial void OnElementThemeChanged(int value)
        {
            AppSettingsService.SetValue(nameof(ElementTheme), value);
            LogService.Info($"Theme changed to: {(ElementTheme)value}", LogSource.Settings);
        }

        // 窗口背景（Backdrop）效果索引：0= 无, 1= Mica, 2= Acrylic, 3= AcrylicThin。
        [ObservableProperty]
        private int _backdropIndex;

        partial void OnBackdropIndexChanged(int value)
        {
            AppSettingsService.SetValue(nameof(BackdropIndex), value);
            LogService.Info($"Backdrop changed to index: {value}", LogSource.Settings);
        }

        // Acrylic 着色浓度 (0.0–1.0)，仅在 BackdropIndex 为 2/3 时生效
        [ObservableProperty]
        private double _acrylicTintOpacity = 0.5;

        partial void OnAcrylicTintOpacityChanged(double value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(AcrylicTintOpacity), value);
            OnPropertyChanged(nameof(AcrylicTintOpacityText));
            LogService.Info($"Acrylic tint opacity: {value:F2}", LogSource.Settings);
        }

        // 窗口整体透明度 (0.1–1.0)，1.0 = 完全不透明
        [ObservableProperty]
        private double _windowOpacity = 1.0;

        partial void OnWindowOpacityChanged(double value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(WindowOpacity), value);
            OnPropertyChanged(nameof(WindowOpacityText));
            LogService.Info($"Window opacity: {value:F2}", LogSource.Settings);
        }

        // 窗口透明度的步长（0.05）— 供 Slider 使用
        public double OpacityStepFrequency => 0.05;

        // Acrylic 着色浓度的 UI 百分比文本。
        public string AcrylicTintOpacityText => $"{AcrylicTintOpacity * 100:F0}%";
        // 窗口透明度的 UI 百分比文本。
        public string WindowOpacityText => $"{WindowOpacity * 100:F0}%";

        #region Banner Settings

        public List<BannerPreset> BannerPresets { get; } = new()
        {
            new BannerPreset { Name = "BannerPreset_Name_default", Key = "default", AssetPath = "ms-appx:///Assets/Banners/banner_01.jpg" },
            new BannerPreset { Name = "BannerPreset_Name_scenic", Key = "scenic",   AssetPath = "ms-appx:///Assets/Banners/banner_02.jpg" },
            new BannerPreset { Name = "BannerPreset_Name_anime", Key = "anime",    AssetPath = "ms-appx:///Assets/Banners/banner_03.jpg" },
        };

        // 预加载的 Banner BitmapImage，切换时只改引用不重新解码
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

        // 当前选中的 Banner 预设名称（已本地化），用于 UI 显示。
        public string CurrentBannerPresetName
        {
            get
            {
                if (BannerPresetIndex >= 0 && BannerPresetIndex < BannerPresets.Count)
                    return ResourceService.GetString(BannerPresets[BannerPresetIndex].Name);
                return BannerPresets.Count > 0 ? ResourceService.GetString(BannerPresets[0].Name) : "";
            }
        }

        // 三张预加载 Banner 的图片源，供 Image 控件直接绑定（切换时不换 Source，只换 Visibility）
        // 三张预加载 Banner 的 BitmapImage 源（索引 0）。
        public BitmapImage? BannerImage0 => _preloadedBanners.Count > 0 ? _preloadedBanners[0] : null;
        // 三张预加载 Banner 的 BitmapImage 源（索引 1）。
        public BitmapImage? BannerImage1 => _preloadedBanners.Count > 1 ? _preloadedBanners[1] : null;
        // 三张预加载 Banner 的 BitmapImage 源（索引 2）。
        public BitmapImage? BannerImage2 => _preloadedBanners.Count > 2 ? _preloadedBanners[2] : null;

        // Banner 预设 0 的可见性。
        public Visibility Banner0Visible => BannerPresetIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Banner 预设 1 的可见性。
        public Visibility Banner1Visible => BannerPresetIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        // Banner 预设 2 的可见性。
        public Visibility Banner2Visible => BannerPresetIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 切换到上一个 Banner 预设。
        public void PrevBanner()
        {
            if (BannerPresets.Count == 0) return;
            int newIndex = BannerPresetIndex - 1;
            if (newIndex < 0) newIndex = BannerPresets.Count - 1;
            BannerPresetIndex = newIndex;
        }

        // 切换到下一个 Banner 预设。
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
            LogService.Info($"HEIC decoder changed to: {(value == 0 ? "Magick.NET" : "heif-dec")}", LogSource.Settings);
        }

        // 合成并行线程数。
        [ObservableProperty]
        private int _mergeThreadCount = 5;

        partial void OnMergeThreadCountChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue("MergeThreadCount", value);
            LogService.Info($"Merge thread count changed to: {value}", LogSource.Settings);
        }

        // 合成并行数最大值
        public int MaxMergeThreadCount => 10;

        // 增加合成并行线程数。
        [RelayCommand]
        private void IncreaseMergeThreadCount()
        {
            if (MergeThreadCount < MaxMergeThreadCount) MergeThreadCount++;
        }

        // 减少合成并行线程数。
        [RelayCommand]
        private void DecreaseMergeThreadCount()
        {
            if (MergeThreadCount > 1) MergeThreadCount--;
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
                EncoderHelper.SaveEncoderForBothCodecs(value.FfmpegEncoder);
                LogService.Split($"Saved encoder to settings: '{value.FfmpegEncoder}'", LogLevel.Debug);
            }
        }

        // 根据一个 codec 的编码器名称，同时保存 H.264 和 HEVC 两个 codec 的编码器设置。
        // 委托给 EncoderHelper（集中管理编码器逻辑，不再依赖 VideoTranscodeService）。
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

        // 修复输出模式 — 开启时修复到单独目录，关闭时原地替换
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

        // 修复非实况照片的视频 — 开启后同时修复 > 3.5s 的普通长视频
        [ObservableProperty]
        private bool _isNonLivePhotoVideoRepairEnabled;

        partial void OnIsNonLivePhotoVideoRepairEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsNonLivePhotoVideoRepairEnabled), value);
            LogService.Info($"Repair non-live-photo video setting: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        // 输出目录模式下同时复制无需修复的文件 — 开启后完美文件也会复制到输出目录
        [ObservableProperty]
        private bool _isCopyPerfectToOutput;

        partial void OnIsCopyPerfectToOutputChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsCopyPerfectToOutput), value);
            LogService.Info($"Copy perfect files to output: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        #endregion

        #region History / Inspector Settings

        // 是否在导航栏显示"照片历史"页面（默认隐藏）
        [ObservableProperty]
        private bool _isHistoryPageVisible;

        partial void OnIsHistoryPageVisibleChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsHistoryPageVisible), value);
            LogService.Info($"History page visibility: {(value ? "shown" : "hidden")}", LogSource.Settings);
        }

        #endregion

        #region General Settings

        [ObservableProperty]
        private bool _isRecursiveScanEnabled;

        partial void OnIsRecursiveScanEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsRecursiveScanEnabled), value);
            LogService.Info($"Recursive scan: {(value ? "ON" : "OFF")}", LogSource.Settings);
            OnPropertyChanged(nameof(OutputPreserveSubfolderVisibility));
        }

        // 拖拽时自动搜索配对视频（Apple 双文件实况照片 CID 匹配）
        [ObservableProperty]
        private bool _isDragDropAutoPairEnabled;

        partial void OnIsDragDropAutoPairEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsDragDropAutoPairEnabled), value);
            LogService.Info($"Drag-drop auto pair: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        // 输出目录按照子文件夹结构 — 仅在递归扫描开启时可见
        [ObservableProperty]
        private bool _isOutputPreserveSubfolderStructure;

        partial void OnIsOutputPreserveSubfolderStructureChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsOutputPreserveSubfolderStructure), value);
            LogService.Info($"Output preserve subfolder structure: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        // "输出目录按照子文件夹结构"仅在递归扫描开启时可见
        public Visibility OutputPreserveSubfolderVisibility =>
            IsRecursiveScanEnabled ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// HEIC 缩略图解码方案：0 = Magick.NET（当前方案），1 = MagicScaler（实验性，高性能）。
        /// 切换后自动清空缩略图缓存，强制重新加载以对比效果。
        /// </summary>
        [ObservableProperty]
        private int _thumbnailProviderIndex;

        partial void OnThumbnailProviderIndexChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(ThumbnailProviderIndex), value);
            ThumbnailService.ClearCache();
            LogService.Info($"Thumbnail provider: {(value == 0 ? "Magick.NET" : "MagicScaler")}", LogSource.Settings);
        }

        #endregion

        #region KeyPhoto Timeline Settings

        /// <summary>
        /// 关键帧时间轴模式：0 = 经典 ListView（原有），1 = 胶片模式（固定选中框 + 逐帧步进）。
        /// 设置在设置页面调整，KeyPhotoPage 读取后分别激活对应模式。
        /// </summary>
        [ObservableProperty]
        private int _timelineModeIndex;

        partial void OnTimelineModeIndexChanged(int value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(TimelineModeIndex), value);
            LogService.Info($"Timeline mode changed to: {(value == 0 ? "Classic ListView" : "Filmstrip")}", LogSource.Settings);
        }

        #endregion

        #region Debug / Test Tools

        // 修复页面扫描时加载视频缩略图（默认关 = 不加载）
        [ObservableProperty]
        private bool _isRepairScanLoadThumbnail;

        partial void OnIsRepairScanLoadThumbnailChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsRepairScanLoadThumbnail), value);
            LogService.Info($"Repair scan load thumbnail: {(value ? "enabled" : "disabled")}", LogSource.Settings);
        }

        // 更严格的实况照片扫描 — 通过 ContentIdentifier UUID 匹配（默认关 = 文件名匹配）
        [ObservableProperty]
        private bool _isStrictLivePhotoScanEnabled;

        partial void OnIsStrictLivePhotoScanEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsStrictLivePhotoScanEnabled), value);
            LogService.Info($"Strict Live Photo scan: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        // 仅扫描 Apple 实况照片 — 开启后仅在元数据中检测到 Apple 设备特征的文件才参与扫描
        [ObservableProperty]
        private bool _isAppleOnlyScanEnabled;

        partial void OnIsAppleOnlyScanEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsAppleOnlyScanEnabled), value);
            LogService.Info($"Apple-only scan: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        // 位置查询 — 开启后根据照片 GPS 坐标联网获取地名（逆地理编码）
        [ObservableProperty]
        private bool _isGeoLocationEnabled = true;

        partial void OnIsGeoLocationEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            AppSettingsService.SetValue(nameof(IsGeoLocationEnabled), value);
            LogService.Info($"Geo location lookup: {(value ? "ON" : "OFF")}", LogSource.Settings);
        }

        // 详细操作记录开关（默认关闭）
        // 关闭后仅标记经本软件处理过（合成/拆分/修复），不通过 dc:subject 写入具体更改内容
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
            RefreshCrashLogs();
            // 硬件信息异步加载（Banner 预加载延迟到打开设置页面时再触发）
            _ = LoadHardwareInfoAsync();
        }

        // 进入设置页面时调用：预加载 Banner → 通知 UI。
        // 只执行一次（_preloadedBanners 非空则跳过）。
        // 使用 BitmapImage(UriSource) 在打包和未打包模式下均能正确加载，
        // DecodePixelWidth 让系统尽早开始解码，减少切换时的解码延迟。
        public async Task EnsureBannersPreloadedAsync()
        {
            if (_preloadedBanners.Count > 0) return;  // 已加载，跳过
            await PreloadBannersAsync();
            OnPropertyChanged(nameof(BannerImage0));
            OnPropertyChanged(nameof(BannerImage1));
            OnPropertyChanged(nameof(BannerImage2));
            OnPropertyChanged(nameof(Banner0Visible));
        }

        // 预加载所有 Banner，创建 BitmapImage 后立即设置 DecodePixelWidth 让系统开始解码。
        // 使用 UriSource 直接加载（而非 StorageFile.GetFileFromApplicationUriAsync + SetSourceAsync），
        // 因为 StorageFile API 在 unpackaged (F5) 模式下无法解析 ms-appx:/// URI，
        // 而 BitmapImage(UriSource) 在打包和未打包模式下均能正确加载。
        private Task PreloadBannersAsync()
        {
            foreach (var preset in BannerPresets)
            {
                var bitmap = new BitmapImage(new Uri(preset.AssetPath))
                {
                    DecodePixelWidth = 640,
                    CreateOptions = BitmapCreateOptions.None
                };
                _preloadedBanners.Add(bitmap);
            }
            return Task.CompletedTask;
        }

        // 从 AppSettingsService 加载所有设置项到 ViewModel 属性。
        private void LoadSettings()
        {
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
            WindowOpacity = AppSettingsService.GetValue(nameof(WindowOpacity), 1.0);
            AcrylicTintOpacity = AppSettingsService.GetValue(nameof(AcrylicTintOpacity), 0.2);
            BannerPresetIndex = AppSettingsService.GetValue(nameof(BannerPresetIndex), 0);
            IsBannerRandomEnabled = AppSettingsService.GetValue(nameof(IsBannerRandomEnabled), false);
            ThreadCount = AppSettingsService.GetValue("SplitThreadCount", 4);
            MaxThreadCount = Math.Min(Environment.ProcessorCount, 20);
            HeicConcurrency = AppSettingsService.GetValue("HeicConcurrency", 8);
            HeicDecoderIndex = AppSettingsService.GetValue(nameof(HeicDecoderIndex), 0);
            MergeThreadCount = AppSettingsService.GetValue("MergeThreadCount", 4);
            IsHeicRepairEnabled = AppSettingsService.GetValue(nameof(IsHeicRepairEnabled), false);
            IsRepairOutputToDirectory = AppSettingsService.GetValue("IsOutputToDirectory", false);
            IsRepairScanLoadThumbnail = AppSettingsService.GetValue(nameof(IsRepairScanLoadThumbnail), false);
            IsStrictLivePhotoScanEnabled = AppSettingsService.GetValue(nameof(IsStrictLivePhotoScanEnabled), false);
            IsAppleOnlyScanEnabled = AppSettingsService.GetValue(nameof(IsAppleOnlyScanEnabled), true);
            IsGeoLocationEnabled = AppSettingsService.GetValue(nameof(IsGeoLocationEnabled), true);
            IsNonLivePhotoVideoRepairEnabled = AppSettingsService.GetValue(nameof(IsNonLivePhotoVideoRepairEnabled), false);
            IsCopyPerfectToOutput = AppSettingsService.GetValue(nameof(IsCopyPerfectToOutput), false);
            IsHistoryPageVisible = AppSettingsService.GetValue(nameof(IsHistoryPageVisible), false);
            IsDetailedHistoryEnabled = AppSettingsService.GetValue(nameof(IsDetailedHistoryEnabled), false);
            IsRecursiveScanEnabled = AppSettingsService.GetValue(nameof(IsRecursiveScanEnabled), true);
            IsDragDropAutoPairEnabled = AppSettingsService.GetValue(nameof(IsDragDropAutoPairEnabled), false);
            IsOutputPreserveSubfolderStructure = AppSettingsService.GetValue(nameof(IsOutputPreserveSubfolderStructure), true);
            ThumbnailProviderIndex = AppSettingsService.GetValue(nameof(ThumbnailProviderIndex), 0);
            TimelineModeIndex = AppSettingsService.GetValue(nameof(TimelineModeIndex), 1);
        }

        // 异步加载硬件编码信息（WMI + FFmpeg 检测），完成后设置 SelectedHardware。
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

        // 将检测到的硬件列表应用到 UI 绑定的集合，并设置当前选择。
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

        // 根据上次保存的设置或自动推荐，选定当前的硬件编码器。
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

        // 强制重新检测硬件编码器（清除缓存后重新加载）。
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

        // 增加拆分线程数。
        [RelayCommand]
        private void IncreaseThreadCount()
        {
            if (ThreadCount < MaxThreadCount)
            {
                ThreadCount++;
            }
        }

        // 减少拆分线程数。
        [RelayCommand]
        private void DecreaseThreadCount()
        {
            if (ThreadCount > 1)
            {
                ThreadCount--;
            }
        }

        // 将所有设置恢复为默认值，包括硬件选择、合成/拆分/修复参数等。
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
            AppViewModel.Instance.Merge.SelectedModeIndex = 2;
            AppViewModel.Instance.Merge.OutputFormatIndex = 0;
            AppViewModel.Instance.Repair.IsOutputToDirectory = false;
            AppViewModel.Instance.Edit.IsMuted = false;

            // 4. 重新选择最佳硬件
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

        #region External Tool Check

        // 单个外部工具的检测结果。
        public class ToolCheckResult
        {
            // 工具显示名称（如 "ExifTool"、"FFmpeg"）。
            public string DisplayName { get; init; } = "";
            // 是否找到可执行文件。
            public bool Found { get; init; }
            // 可执行文件完整路径（未找到时为 null）。
            public string? Path { get; init; }
            // 版本字符串（运行成功时获取，失败或超时时为 null）。
            public string? Version { get; init; }
            // 运行失败或超时的错误信息（成功时为 null）。
            public string? Error { get; init; }
        }

        // 检测所有外部工具（exiftool / ffmpeg / jpegtran）是否可用。
        // 整个检测过程在后台线程执行：路径定位 + 进程启动 + 输出收集均不阻塞 UI。
        // 对每个工具依次：定位路径 → 尝试运行获取版本 → 返回结构化结果列表。
        // 单个工具检测超时 5 秒，超时或异常不影响其他工具的检测。
        public static async Task<List<ToolCheckResult>> CheckAllExternalToolsAsync()
        {
            // 全部工作放到后台线程，避免 PATH 扫描和进程 Wait 阻塞 UI 线程
            return await Task.Run(() =>
            {
                var results = new List<ToolCheckResult>();
                results.Add(CheckSingleTool("ExifTool", () => ExternalToolLocator.FindExifTool(), "-ver"));
                results.Add(CheckSingleTool("FFmpeg", () => ExternalToolLocator.FindFFmpeg(), "-version"));
                // jpegtran 不支持 -version，只验证可执行性，不提取版本号
                results.Add(CheckSingleTool("jpegtran", () => ExternalToolLocator.FindJpegTran(), "", maxVersionLines: -1));
                results.Add(CheckSingleTool("heif-enc", () => ExternalToolLocator.FindHeifEnc(), "--version"));
                results.Add(CheckSingleTool("heif-dec", () => ExternalToolLocator.FindHeifDec(), "--version"));
                return results;
            });
        }

        // 检测单个工具（同步版本，仅从 Task.Run 内部调用，运行在后台线程上）。
        // 核心目标：验证工具不仅存在于磁盘，还能被当前进程环境成功调用
        //（MSIX 打包 / 权限限制 / 依赖缺失 都可能导致找到文件但无法执行）。
        // versionArg 为获取版本的参数（exiftool -ver / ffmpeg -version），
        // jpegtran 传 "" 表示不带参数运行（会输出用法到 stdout，exit 1 属正常）。
        // maxVersionLines: 版本信息最大行数，0 = 不限制（用于有版本命令的工具），1 = 只取第一行（用于 jpegtran 等无版本命令的工具）。
        // 注意：stdoutBuilder / stderrBuilder 使用 lock 保护，消除事件处理器在 ThreadPool 线程上的竞态条件。
        private static ToolCheckResult CheckSingleTool(
            string displayName,
            Func<string?> pathResolver,
            string versionArg,
            int maxVersionLines = 0)
        {
            string? path;
            try
            {
                path = pathResolver();
            }
            catch (Exception ex)
            {
                return new ToolCheckResult
                {
                    DisplayName = displayName,
                    Found = false,
                    Error = $"Path resolution error: {ex.Message}"
                };
            }

            if (string.IsNullOrEmpty(path))
            {
                return new ToolCheckResult
                {
                    DisplayName = displayName,
                    Found = false,
                    Error = ResourceService.GetString("SettingsPage_CheckTools_NotFound")
                };
            }

            // 实际执行工具 — 验证不仅能找到文件，还能被系统成功调用
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = versionArg,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };

                var tcs = new TaskCompletionSource<(int exitCode, string stdout, string stderr)>();
                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();
                var lockObj = new object();  // 保护两个 StringBuilder：所有事件处理器均运行在 ThreadPool 线程上

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (lockObj)
                        {
                            if (stdoutBuilder.Length > 0) stdoutBuilder.Append('\n');
                            stdoutBuilder.Append(e.Data);
                        }
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (lockObj)
                        {
                            if (stderrBuilder.Length > 0) stderrBuilder.Append('\n');
                            stderrBuilder.Append(e.Data);
                        }
                    }
                };

                process.Exited += (_, _) =>
                {
                    string stdout, stderr;
                    lock (lockObj)
                    {
                        stdout = stdoutBuilder.ToString().Trim();
                        stderr = stderrBuilder.ToString().Trim();
                    }
                    tcs.TrySetResult((process.ExitCode, stdout, stderr));
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 5 秒超时 — 在后台线程上阻塞等待（不阻塞 UI）
                var completedTask = Task.WhenAny(tcs.Task, Task.Delay(5000)).GetAwaiter().GetResult();
                if (completedTask != tcs.Task)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new ToolCheckResult
                    {
                        DisplayName = displayName,
                        Found = true,
                        Path = path,
                        Error = ResourceService.GetString("SettingsPage_CheckTools_Timeout")
                    };
                }

                var (exitCode, stdout, stderr) = tcs.Task.Result;

                // 工具成功运行（进程启动并正常退出，无论 exit code 是否为 0）
                // 版本号从 stdout 提取，stdout 为空时回退到 stderr
                // maxVersionLines: 0 = 全部行, N = 前 N 行, -1 = 不提取版本号（直接显示"未知"）
                string? version = null;
                if (maxVersionLines >= 0)
                {
                    string versionSource = stdout.Length > 0 ? stdout : stderr;
                    if (versionSource.Length > 0)
                    {
                        if (maxVersionLines == 0)
                        {
                            version = versionSource;
                        }
                        else
                        {
                            var lines = versionSource.Split('\n');
                            version = string.Join('\n', lines.Take(maxVersionLines));
                        }
                    }
                }

                // 额外诊断：exit code 非 0 时附注（jpegtran 除外，这是预期行为）
                string? warning = null;
                if (exitCode != 0 && displayName != "jpegtran")
                {
                    warning = $"Exit code: {exitCode}";
                }

                return new ToolCheckResult
                {
                    DisplayName = displayName,
                    Found = true,
                    Path = path,
                    Version = version,
                    Error = warning
                };
            }
            catch (Exception ex)
            {
                // 无法启动进程 — 工具文件存在但无法执行（权限/打包限制/依赖缺失等）
                return new ToolCheckResult
                {
                    DisplayName = displayName,
                    Found = true,
                    Path = path,
                    Error = ex.Message
                };
            }
        }

        #endregion

        #region Crash Log Management

        // 上一次会话的日志文件路径（已校验存在性）。
        private string? _latestLogPath;

        // 上一次会话的转储文件路径（已校验存在性）。
        private string? _latestDumpPath;

        // 是否存在可用的崩溃产物（日志或转储文件）。
        public bool HasCrashArtifacts => GetLatestCrashArtifactPath() != null;

        // 本次会话日志文件显示名称。
        public string CurrentLogFileNameText
        {
            get
            {
                string? path = LogService.CurrentLogPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return Path.GetFileName(path);
                return ResourceService.GetString("SettingsPage_CrashNoCrashValue");
            }
        }

        // 上一次日志文件的显示名称，无可用时显示"暂无"。
        public string PreviousLogFileNameText => GetLatestCrashArtifactPath() is string latestPath
            ? Path.GetFileName(latestPath)
            : ResourceService.GetString("SettingsPage_CrashNoCrashValue");

        // ── 命令 ──

        // 打开崩溃日志文件夹的命令。
        [RelayCommand]
        private void OpenCrashLogFolderAction()
        {
            string logDirectory = LogService.LogDirectory;
            LogService.Info($"OpenCrashLogFolder requested. Path='{logDirectory}'", LogSource.App);
            FilePickerService.OpenFolderInExplorer(logDirectory);
        }

        // 打开本次日志文件的命令。
        [RelayCommand(CanExecute = nameof(HasCurrentLog))]
        private async Task OpenCurrentLogActionAsync()
        {
            string? path = LogService.CurrentLogPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                RefreshCrashLogs();
                return;
            }
            LogService.Info($"OpenCurrentLog requested. File='{Path.GetFileName(path)}'", LogSource.App);
            await FilePickerService.OpenFileAsync(path);
        }

        // 打开上一次崩溃日志文件的命令。
        [RelayCommand(CanExecute = nameof(HasCrashArtifacts))]
        private async Task OpenPreviousLogActionAsync()
        {
            string? latestPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestPath) || !File.Exists(latestPath))
            {
                RefreshCrashLogs();
                return;
            }
            LogService.Info($"OpenPreviousLog requested. File='{Path.GetFileName(latestPath)}'", LogSource.App);
            await FilePickerService.OpenFileAsync(latestPath);
        }

        // 导出本次日志文件到用户指定位置的命令。
        [RelayCommand(CanExecute = nameof(HasCurrentLog))]
        private async Task ExportCurrentLogActionAsync()
        {
            string? path = LogService.CurrentLogPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                RefreshCrashLogs();
                return;
            }
            LogService.Info($"ExportCurrentLog requested. File='{Path.GetFileName(path)}'", LogSource.App);
            await FilePickerService.ExportFileCopyAsync(path, Path.GetFileName(path));
        }

        // 导出上一次崩溃日志文件到用户指定位置的命令。
        [RelayCommand(CanExecute = nameof(CanExportPreviousLog))]
        private async Task ExportPreviousLogActionAsync()
        {
            string? latestPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestPath) || !File.Exists(latestPath))
            {
                RefreshCrashLogs();
                return;
            }
            LogService.Info($"ExportPreviousLog requested. File='{Path.GetFileName(latestPath)}'", LogSource.App);
            await FilePickerService.ExportFileCopyAsync(latestPath, Path.GetFileName(latestPath));
        }

        // 清除所有崩溃日志文件的命令。
        [RelayCommand(CanExecute = nameof(CanClearCrashLogs))]
        private void ClearCrashLogsAction()
        {
            LogService.Info("ClearCrashLogs requested.", LogSource.App);
            LogService.DeleteAllLogFiles();
            RefreshCrashLogs();
        }

        // 在浏览器中打开 GitHub Issues 反馈页面的命令。
        [RelayCommand]
        private async Task OpenIssueFeedbackActionAsync()
        {
            LogService.Info("OpenIssueFeedback requested.", LogSource.App);
            await FeedbackService.OpenIssuePageAsync();
        }

        // ── 公开方法 ──

        // 刷新崩溃日志状态，重新检测日志文件和转储文件的存在性，
        // 并更新相关命令的可执行状态及绑定属性。
        public void RefreshCrashLogs()
        {
            _latestLogPath = LogService.GetLatestLogPath();
            _latestDumpPath = LogService.GetLatestDumpPath();

            if (!string.IsNullOrWhiteSpace(_latestLogPath) && !File.Exists(_latestLogPath))
                _latestLogPath = null;

            if (!string.IsNullOrWhiteSpace(_latestDumpPath) && !File.Exists(_latestDumpPath))
                _latestDumpPath = null;

            OpenCurrentLogActionCommand.NotifyCanExecuteChanged();
            OpenPreviousLogActionCommand.NotifyCanExecuteChanged();
            ExportCurrentLogActionCommand.NotifyCanExecuteChanged();
            ExportPreviousLogActionCommand.NotifyCanExecuteChanged();
            ClearCrashLogsActionCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasCrashArtifacts));
            OnPropertyChanged(nameof(CurrentLogFileNameText));
            OnPropertyChanged(nameof(PreviousLogFileNameText));
        }

        // ── 私有辅助方法 ──

        // 本次日志文件是否存在。
        private bool HasCurrentLog
        {
            get
            {
                string? path = LogService.CurrentLogPath;
                return !string.IsNullOrEmpty(path) && File.Exists(path);
            }
        }

        // 是否有上一次日志可导出。
        private bool CanExportPreviousLog() => HasCrashArtifacts;

        // 是否有崩溃产物可清除。
        private bool CanClearCrashLogs() => HasCrashArtifacts;

        // Returns the previous session's log file (not the currently active one).
        // Falls back to the most recent non-current log, then to dump file.
        private string? GetLatestCrashArtifactPath()
        {
            // Priority 1: PreviousLogPath — set during LogService init to the previous session's file
            string? previousLog = LogService.PreviousLogPath;
            if (!string.IsNullOrWhiteSpace(previousLog) && File.Exists(previousLog))
                return previousLog;

            // Priority 2: any old log that isn't the current active one
            string? currentLog = LogService.CurrentLogPath;
            string logDir = LogService.LogDirectory;
            if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            {
                var logs = Directory.GetFiles(logDir, "app-*.log")
                    .Where(f => !string.Equals(f, currentLog, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                if (logs.Count > 0)
                    return logs[0];
            }

            // Priority 3: dump file (very rare — only for native crashes)
            return new[] { _latestDumpPath }
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path!))
                .FirstOrDefault();
        }

        #endregion
    }
}