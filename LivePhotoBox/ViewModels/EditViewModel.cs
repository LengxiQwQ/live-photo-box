/*
 * EditViewModel.cs
 *
 * 实况照片封面更换页面的 ViewModel。
 * 管理资源浏览（文件夹选择 → 自动扫描 → ListView 文件列表）、
 * 选中文件信息展示、CommandBar 命令及时间轴数据的绑定。
 *
 * 继承 ViewModelBase（轻量基类），不继承 WorkViewModelBase，
 * 因为此页面是"浏览 + 编辑"模式，不是批处理工作流。
 *
 * 扫描触发：TextBox 失去焦点时（LostFocus）或浏览按钮选择文件夹后，
 * 由 View 层调用 TriggerScan()。
 *
 * 属性读取：使用 PersistentExifTool 单实例，选中文件时查询 EXIF/GPS/视频元数据。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Helpers;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Input;

namespace LivePhotoBox.ViewModels
{
    public partial class EditViewModel : ViewModelBase
    {
        // ══════════════════════════════════════════════════════════════
        //  支持的文件扩展名（图片 + 视频）
        // ══════════════════════════════════════════════════════════════
        // Legacy 保持原有托管/外部工具格式范围；Rebuilt 只能暴露 Native 当前真实支持的格式。
        private static readonly HashSet<string> LegacySupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif", ".webp"
        };

        private static readonly HashSet<string> RebuiltSupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif", ".jpg", ".jpeg"
        };

        private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mov", ".mp4"
        };

        private static bool IsSupportedImageExtension(string extension)
        {
            var supported = ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt
                ? RebuiltSupportedImageExtensions
                : LegacySupportedImageExtensions;
            return supported.Contains(extension);
        }

        /// <summary>exiftool 并发数。磁盘 I/O 密集操作，上限 8 避免 HDD 颠簸或 SSD 带宽饱和。</summary>
        private static readonly int ExifToolPoolSize = Math.Min(Environment.ProcessorCount, 8);

        // ══════════════════════════════════════════════════════════════
        //  构造函数 & 生命周期
        // ══════════════════════════════════════════════════════════════

        public EditViewModel()
        {
            // 从设置恢复静音状态（默认不静音）
            _isMuted = AppSettingsService.GetValue("IsLivePhotoMuted", false);
            // 进度前缀默认：导出帧
            ProgressPrefixText = ResourceService.GetString("EditPage_ExportProgressPrefixLabel");

            // 时间轴集合变化时同步 HasOriginalPhotoFrame
            TimelineFrames.CollectionChanged += (_, _) =>
                OnPropertyChanged(nameof(HasOriginalPhotoFrame));
        }

        public override string? PageStatusTag => null;

        /// <summary>页面卸载时清理 exiftool 进程</summary>
        public void Cleanup()
        {
            _propLoadCts?.Cancel();
            _geoCts?.Cancel();
            _timelineCts?.Cancel();
            _timelineDebounceCts?.Cancel();
            _earlyFfmpegCts?.Cancel();
            _earlyFfmpegTask = null;
            _exportCts?.Cancel();
            _exportCts?.Dispose();
            _completionCts?.Cancel();
            _completionCts?.Dispose();
            DisposeExifTool();
            CleanupFrameTempFiles();
            CleanupTempVideo();
            _previewCache.Clear();
            _previewCacheOrder.Clear();
            ThumbnailScheduler.Reset();
        }

        // ══════════════════════════════════════════════════════════════
        //  目录路径
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private string _currentDirectory = string.Empty;

        // ══════════════════════════════════════════════════════════════
        //  扫描状态
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private bool _isScanning;

        private CancellationTokenSource? _scanCts;

        /// <summary>
        /// 选中文件代数（每次 SelectFile 调用时通过 Interlocked.Increment 递增）。
        /// 所有异步回调（exiftool 查询结果投递、ffmpeg 帧提取、大图预览加载）
        /// 在操作执行前检查此值是否匹配：不匹配说明用户已切换到另一个文件，
        /// 旧回调应立即 bail out，避免新旧文件的重量级操作同时抢占 CPU/内存。
        /// </summary>
        private int _selectionGeneration;

        /// <summary>
        /// 当前选中文件的缩略图异步加载监听器。
        /// EditFileItem.Thumbnail 为懒加载（TryGetOrLoad），首次返回 null；
        /// 监听其 PropertyChanged，加载完成后同步到 SelectedFileThumbnail。
        /// </summary>
        private EditFileItem? _thumbnailLoadListener;

        /// <summary>缩略图异步加载完成的回调</summary>
        private void ThumbnailItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditFileItem.Thumbnail) && _thumbnailLoadListener != null)
            {
                SelectedFileThumbnail = _thumbnailLoadListener.Thumbnail;
                _thumbnailLoadListener.PropertyChanged -= ThumbnailItem_PropertyChanged;
                _thumbnailLoadListener = null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  搜索 & 排序（暂时占位，后续适配）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private int _selectedSortIndex = 1; // 默认按日期排序

        [ObservableProperty]
        private bool _isSortAscending = true;

        /// <summary>排序方向图标：升序 ↑ / 降序 ↓</summary>
        public string SortDirectionGlyph => IsSortAscending ? "" : "";

        /// <summary>文件总数</summary>
        [ObservableProperty]
        private int _totalCount;

        /// <summary>完整实况照片数（有协议且配对完整）</summary>
        [ObservableProperty]
        private int _livePhotoCount;

        /// <summary>残缺实况数（有协议但缺失配对文件）</summary>
        [ObservableProperty]
        private int _brokenLiveCount;

        /// <summary>其他文件数（非实况协议）</summary>
        [ObservableProperty]
        private int _otherCount;

        partial void OnTotalCountChanged(int value) { }
        partial void OnLivePhotoCountChanged(int value) { }
        partial void OnBrokenLiveCountChanged(int value) { }
        partial void OnOtherCountChanged(int value) { }

        /// <summary>有残缺实况时显示对应统计项。</summary>
        public bool HasBrokenLive => BrokenLiveCount > 0;

        /// <summary>从 _allFileItems 重新计算所有文件统计数。</summary>
        private void RefreshCounts()
        {
            TotalCount = _allFileItems.Count;
            LivePhotoCount = _allFileItems.Count(f => f.HasConfirmedProtocol && !f.IsPairIncomplete);
            BrokenLiveCount = _allFileItems.Count(f => f.HasConfirmedProtocol && f.IsPairIncomplete);
            OtherCount = _allFileItems.Count(f => !f.HasConfirmedProtocol);
            OnPropertyChanged(nameof(HasBrokenLive));
        }

        /// <summary>文件过滤：0=所有文件 / 1=实况照片 / 2=残缺实况 / 3=普通照片 / 4=普通视频</summary>
        [ObservableProperty]
        private int _selectedFilterIndex;

        partial void OnSelectedFilterIndexChanged(int value) => ApplySortAndFilter();

        // ══════════════════════════════════════════════════════════════
        //  文件列表
        // ══════════════════════════════════════════════════════════════

        public ObservableCollection<EditFileItem> FileItems { get; } = new();

        /// <summary>未过滤的完整文件列表（排序/搜索的后备存储）</summary>
        private List<EditFileItem> _allFileItems = new();

        // ══════════════════════════════════════════════════════════════
        //  排序 & 搜索实现
        // ══════════════════════════════════════════════════════════════

        partial void OnSelectedSortIndexChanged(int value) => ApplySortAndFilter();
        partial void OnSearchTextChanged(string value) => ApplySortAndFilter();
        partial void OnSelectedFilePathChanged(string? value)
        {
            OnPropertyChanged(nameof(HasSelectedFile));
            OnPropertyChanged(nameof(IsSelectedPairIncomplete));
            OnPropertyChanged(nameof(IsTimelineTabDisabled));
            OnPropertyChanged(nameof(ProtocolIconBrush));
            OnPropertyChanged(nameof(IsVideoRowVisible));
            OnPropertyChanged(nameof(CanPlayLivePhoto));
            OnPropertyChanged(nameof(CanExportCurrentFrame));
            OnPropertyChanged(nameof(CanExportMultiFrame));
            ConvertProtocolCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void ToggleSortDirection()
        {
            IsSortAscending = !IsSortAscending;
            OnPropertyChanged(nameof(SortDirectionGlyph));
            ApplySortAndFilter();
        }

        private void ApplySortAndFilter()
        {
            var sorted = SelectedSortIndex switch
            {
                0 => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    : _allFileItems.OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase),
                1 => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.DateTaken)
                    : _allFileItems.OrderByDescending(f => f.DateTaken),
                2 => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.FileSize)
                    : _allFileItems.OrderByDescending(f => f.FileSize),
                _ => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    : _allFileItems.OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            };

            var filtered = string.IsNullOrWhiteSpace(SearchText)
                ? sorted
                : sorted.Where(f => f.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            filtered = SelectedFilterIndex switch
            {
                1 => filtered.Where(f => f.HasConfirmedProtocol && !f.IsPairIncomplete),                            // 实况照片（完整配对）
                2 => filtered.Where(f => f.HasConfirmedProtocol && f.IsPairIncomplete),                             // 残缺实况（缺配对文件）
                3 => filtered.Where(f => !f.HasConfirmedProtocol && !IsVideoExtension(f.FilePath)),                  // 仅普通照片
                4 => filtered.Where(f => !f.HasConfirmedProtocol && IsVideoExtension(f.FilePath)),                   // 仅普通视频
                _ => filtered                                                                                       // 所有文件
            };

            var dispatcher = App.MainWindow?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                FileItems.Clear();
                foreach (var f in filtered) FileItems.Add(f);
                OnPropertyChanged(nameof(HasAnyFiles));
            });
        }

        /// <summary>判断文件是否为视频（.mov / .mp4）</summary>
        /// <summary>在字节数组中搜索子序列</summary>
        private static bool ContainsBytes(byte[] data, ReadOnlySpan<byte> pattern)
        {
            if (pattern.Length == 0) return false;
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                int j;
                for (j = 0; j < pattern.Length; j++)
                    if (data[i + j] != pattern[j]) break;
                if (j == pattern.Length) return true;
            }
            return false;
        }

        private static bool IsVideoExtension(string path) =>
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        // ══════════════════════════════════════════════════════════════
        //  选中文件信息（右下角信息面板绑定）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty] private string _photoFileName = string.Empty;
        [ObservableProperty] private string _fullPhotoFileName = string.Empty;
        [ObservableProperty] private string _photoInfoLine = string.Empty;
        [ObservableProperty] private string _videoInfoLine = string.Empty;
        [ObservableProperty] private string _protocolLine = string.Empty;

        [ObservableProperty] private string _exifCamera = string.Empty;
        /// <summary>设备名后的日期后缀（如 " — 2025/12/12 16:44"），与 ExifCamera 同行显示</summary>
        [ObservableProperty] private string _exifCameraDateSuffix = string.Empty;
        [ObservableProperty] private string _exifLensParams = string.Empty;
        [ObservableProperty] private string _exifShootingParams = string.Empty;
        // 手写属性：XAML 编译器对 [ObservableProperty] 新加属性不稳定，手动实现
        private string _exifPlaceName = string.Empty;
        public string ExifPlaceName { get => _exifPlaceName; set { if (SetProperty(ref _exifPlaceName, value)) OnPropertyChanged(nameof(ExifPlaceName)); } }

        [ObservableProperty] private string _timelineInfo = string.Empty;


        /// <summary>FPS 显示文本，如 "30fps"</summary>
        [ObservableProperty] private string _fpsDisplayText = string.Empty;

        /// <summary>当前帧位置文本，如 "第12帧 / 共89帧" / "Frame 12 of 89"</summary>
        [ObservableProperty] private string _currentFramePositionText = string.Empty;

        // ══════════════════════════════════════════════════════════════
        //  底部信息面板选项卡可见性（多选 ToggleButton 绑定）
        //
        //  互斥规则：
        //  · "实况照片帧" 和 "文件基础信息" 可同时开启
        //  · "更改文件属性" 为独占模式 —— 开启时关闭前两者
        //  · 开启前两者任一 → 关闭"更改文件属性"
        // ══════════════════════════════════════════════════════════════

        /// <summary>"实况照片帧" 面板可见性，默认 true（时间轴 + 帧列表）</summary>
        [ObservableProperty]
        private bool _isFramesPanelVisible = true;

        /// <summary>"文件基础信息" 面板可见性，默认 true（缩略图 + EXIF 等基本信息）</summary>
        [ObservableProperty]
        private bool _isBasicInfoPanelVisible = true;

        /// <summary>"更改文件属性" 面板可见性，默认 false（独占模式，开启时互斥）</summary>
        [ObservableProperty]
        private bool _isDetailPropsPanelVisible = false;

        partial void OnIsFramesPanelVisibleChanged(bool value)
        {
            // 开启 frames / basicInfo → 关闭 detailProps（互斥）
            if (value && IsDetailPropsPanelVisible)
                IsDetailPropsPanelVisible = false;
            OnPropertyChanged(nameof(IsCombinedView));
        }

        partial void OnIsBasicInfoPanelVisibleChanged(bool value)
        {
            // 开启 frames / basicInfo → 关闭 detailProps（互斥）
            if (value && IsDetailPropsPanelVisible)
                IsDetailPropsPanelVisible = false;
            OnPropertyChanged(nameof(IsCombinedView));
        }

        partial void OnIsDetailPropsPanelVisibleChanged(bool value)
        {
            // 开启 detailProps → 独占，关闭 frames 和 basicInfo
            if (value)
            {
                IsFramesPanelVisible = false;
                IsBasicInfoPanelVisible = false;
            }
        }

        /// <summary>组合查看模式（时间轴 + 基础信息同时可见），用于控制分割线显示</summary>
        public bool IsCombinedView => IsFramesPanelVisible && IsBasicInfoPanelVisible;

        // ══════════════════════════════════════════════════════════════
        //  时间轴帧数据
        // ══════════════════════════════════════════════════════════════

        /// <summary>时间轴帧列表（绑定 TimelineListView.ItemsSource）</summary>
        public ObservableCollection<TimelineFrame> TimelineFrames { get; } = new();

        /// <summary>是否有时间轴帧可显示</summary>
        [ObservableProperty] private bool _hasTimelineFrames;

        /// <summary>帧提取是否正在进行中</summary>
        [ObservableProperty] private bool _isTimelineLoading;

        /// <summary>时间轴 loading 透明度（0=隐藏, 1=显示），用 Opacity 而非 Visibility 避免布局跳动</summary>
        public double TimelineLoadingOpacity => IsTimelineLoading ? 1.0 : 0.0;

        /// <summary>胶片模式控件（选中框 + 前后按钮）可见性</summary>
        public Visibility FilmstripControlsVisibility =>
            HasTimelineFrames ? Visibility.Visible : Visibility.Collapsed;

        partial void OnIsTimelineLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(TimelineLoadingOpacity));
        }

        partial void OnHasTimelineFramesChanged(bool value)
        {
            OnPropertyChanged(nameof(FilmstripControlsVisibility));
            OnPropertyChanged(nameof(CanExportMultiFrame));
        }

        /// <summary>时间轴帧提取取消令牌</summary>
        private CancellationTokenSource? _timelineCts;

        /// <summary>时间轴帧提取防抖取消令牌（快速切换时取消上一次待执行提取）</summary>
        private CancellationTokenSource? _timelineDebounceCts;
        private const int TimelineDebounceMs = 200;
        /// <summary>防抖武装标记：0=首次点击直接启动，1=已武装后续点击需防抖</summary>
        private int _timelineDebounceArmed;

        /// <summary>ffmpeg 实际提取的帧数，用于帧位置文字"共 N 帧"显示</summary>
        private int _timelineActualFrameCount;

        /// <summary>初始加载自动滚动时抑制大图预览更新，避免滚过几十帧时大图疯狂切换</summary>
        private bool _isInitialTimelineScroll;

        /// <summary>时间轴正在自动滚动中（初始加载定位封面帧），View 层据此禁用用户滚轮输入</summary>
        public bool IsTimelineAutoScrolling => _isInitialTimelineScroll;

        /// <summary>提前启动的 ffmpeg 帧提取任务（与 exiftool 并行，省 500-700ms）</summary>
        private Task<FrameExtractionResult?>? _earlyFfmpegTask;
        private CancellationTokenSource? _earlyFfmpegCts;

        /// <summary>单文件实况照片的内嵌视频临时文件路径（帧提取完成后清理）</summary>
        private string? _tempVideoPath;
        /// <summary>选文件时已提取的华为/嵌入式临时视频，供 EditPage 播放复用，避免重复提取</summary>
        internal string? CachedTempVideoPath => _tempVideoPath;

        /// <summary>ffmpeg 提取的帧 JPEG 临时目录</summary>
        private string? _frameExtractDir;

        /// <summary>批量导出全部帧的取消令牌</summary>
        private CancellationTokenSource? _exportCts;

        /// <summary>"保存完成"消息停留计时器取消令牌</summary>
        private CancellationTokenSource? _completionCts;

        /// <summary>保存完成后显示对号图标（替代进度圈），短暂停留后自动消失</summary>
        [ObservableProperty]
        private bool _isShowingSaveComplete;

        /// <summary>是否正在导出中（用于 XAML 进度显示和按钮防重入）</summary>
        [ObservableProperty]
        private bool _isExporting;

        /// <summary>导出进度文本，如 "12/80"</summary>
        [ObservableProperty]
        private string _exportProgressText = string.Empty;

        /// <summary>进度前缀文本：导出时显示"正在导出帧…"，保存封面时清空</summary>
        [ObservableProperty]
        private string _progressPrefixText = string.Empty;

        /// <summary>导出进度百分比 0.0-100.0</summary>
        [ObservableProperty]
        private double _exportProgressPercent = 0.0;

        /// <summary>未在导出中（XAML 绑定用，导出时禁用按钮）</summary>
        public bool IsNotExporting => !IsExporting;

        /// <summary>上次导出/保存的输出目录（📂 按钮用）</summary>
        [ObservableProperty]
        private string? _lastExportOutputDir;

        /// <summary>失败时的错误详情（⚠️ 按钮气泡用）</summary>
        [ObservableProperty]
        private string? _lastExportError;

        /// <summary>是否显示失败态（红叉 + 失败文字）</summary>
        [ObservableProperty]
        private bool _isShowingSaveError;

        /// <summary>完成或失败 + 有输出目录 → 显示 📂 按钮</summary>
        public bool IsCompletionWithOutputDir =>
            (IsShowingSaveComplete || IsShowingSaveError) && !string.IsNullOrEmpty(LastExportOutputDir);

        partial void OnIsShowingSaveErrorChanged(bool value)
        {
            OnPropertyChanged(nameof(IsCompletionWithError));
            OnPropertyChanged(nameof(IsCompletionWithOutputDir));
            OnPropertyChanged(nameof(IsSpinnerVisible));
        }

        /// <summary>失败态 + 有错误详情 → 显示 ⚠️ 按钮</summary>
        public bool IsCompletionWithError =>
            IsShowingSaveError && !string.IsNullOrEmpty(LastExportError);

        /// <summary>进度圈可见：非完成、非失败</summary>
        public bool IsSpinnerVisible => IsExporting && !IsShowingSaveComplete && !IsShowingSaveError;

        partial void OnIsShowingSaveCompleteChanged(bool value)
        {
            OnPropertyChanged(nameof(IsCompletionWithOutputDir));
            OnPropertyChanged(nameof(IsSpinnerVisible));
        }

        partial void OnIsExportingChanged(bool value)
            => OnPropertyChanged(nameof(IsSpinnerVisible));

        partial void OnLastExportOutputDirChanged(string? value)
            => OnPropertyChanged(nameof(IsCompletionWithOutputDir));

        partial void OnLastExportErrorChanged(string? value)
            => OnPropertyChanged(nameof(IsCompletionWithError));

        // ══════════════════════════════════════════════════════════════
        //  统一进度 Helper（替换各方法的裸写 Property 赋值）
        // ══════════════════════════════════════════════════════════════

        /// <summary>开始导出/保存：清旧态、设进度文字、显示面板</summary>
        private void BeginExportProgress(string progressText, string? progressPrefix = null)
        {
            _completionCts?.Cancel();
            _completionCts?.Dispose();
            _completionCts = null;
            IsShowingSaveComplete = false;
            IsShowingSaveError = false;
            LastExportOutputDir = null;
            LastExportError = null;
            ExportProgressPercent = 0.0;
            ProgressPrefixText = progressPrefix ?? string.Empty;
            ExportProgressText = progressText;
            IsExporting = true;
        }

        /// <summary>完成：绿勾 + 完成文字 + 存目录，不自动消失</summary>
        private void CompleteExportProgress(string completionText, string? outputDir)
        {
            IsShowingSaveComplete = true;
            IsShowingSaveError = false;
            ExportProgressText = completionText;
            ProgressPrefixText = string.Empty;
            LastExportOutputDir = outputDir;
        }

        /// <summary>失败：红叉 + 失败文字 + 存错误详情，不自动消失。同时写入日志。</summary>
        private void FailExportProgress(string failureText, string errorMessage, string? outputDir = null)
        {
            LogService.FileOp($"Export failed: {failureText} — {errorMessage}", LogLevel.Error);
            IsShowingSaveError = true;
            IsShowingSaveComplete = false;
            IsExporting = true;
            ExportProgressText = failureText;
            ProgressPrefixText = string.Empty;
            LastExportError = errorMessage;
            LastExportOutputDir = outputDir;
        }

        /// <summary>守卫错误：红叉 + 说明文字，无气泡、无文件夹按钮（用户操作问题，非软件故障）</summary>
        private void ShowExportGuardError(string errorText)
        {
            LogService.FileOp($"Export guard: {errorText}", LogLevel.Warning);
            IsShowingSaveError = true;
            IsShowingSaveComplete = false;
            IsExporting = true;
            ExportProgressText = errorText;
            ProgressPrefixText = string.Empty;
            LastExportError = null;
            LastExportOutputDir = null;
        }

        /// <summary>finally 清理：完成态/失败态保持，其他隐藏面板</summary>
        private void FinalizeExportProgress()
        {
            if (!IsShowingSaveComplete && !IsShowingSaveError)
            {
                IsExporting = false;
                ExportProgressText = string.Empty;
                ProgressPrefixText = ResourceService.GetString("EditPage_ExportProgressPrefixLabel");
            }
            ExportProgressPercent = 0.0;
        }

        /// <summary>打开上次导出/保存的输出文件夹</summary>
        [RelayCommand]
        private void OpenExportOutputFolder()
        {
            if (!string.IsNullOrEmpty(LastExportOutputDir))
                FilePickerService.OpenFolderInExplorer(LastExportOutputDir);
        }

        /// <summary>导出选项对话框返回模型</summary>
        private sealed record ExportOptions(string FolderName, bool CopyExif, string ExportPath,
            string FormatExtension = ".jpg", int Quality = 80);

        /// <summary>
        /// 帧缩略图内存缓存：key = "filePath|frameKey", value = ImageSource。
        /// 已加载的缩略图驻留内存，切换回同一文件时瞬间显示（无需重新解码 HEIC 或重读 JPEG）。
        /// frameKey：⭐ 帧 = "star"，视频帧 = 帧序号（如 "3"）。
        /// </summary>
        private readonly Dictionary<string, ImageSource> _thumbnailCache = new();
        private readonly LinkedList<string> _thumbnailCacheOrder = new();  // 插入顺序，用于 LRU 淘汰
        private const int MaxThumbnailCacheSize = 120;  // ~5 个文件的完整时间轴缩略图

        /// <summary>
        /// 大图预览内存缓存：key = filePath, value = ImageSource（DecodePixelWidth=2560）。
        /// 最多保留 MaxPreviewCacheSize 条（当前文件 + 最近访问），
        /// 超过上限时淘汰最旧的条目，避免内存膨胀。
        /// </summary>
        private readonly Dictionary<string, ImageSource> _previewCache = new();
        private readonly List<string> _previewCacheOrder = new();  // 插入顺序，用于淘汰
        private const int MaxPreviewCacheSize = 3;

        public int TimelineThumbnailCount => 14;

        /// <summary>当前选中的时间轴帧（双向绑定到 ListView.SelectedItem）</summary>
        [ObservableProperty]
        private TimelineFrame? _selectedTimelineFrame;

        /// <summary>
        /// "设为封面并保存为副本"按钮是否可用。
        /// 星标帧（IsStillPhoto）已为封面 → 禁用；🖼 原始封面帧 → 可用（可改回原始封面）。
        /// </summary>
        public bool IsSetKeyPhotoEnabled => SelectedTimelineFrame != null && !SelectedTimelineFrame.IsStillPhoto;

        /// <summary>
        /// "前往封面"按钮是否可用（跳转到 ⭐ 封面帧）。
        /// 当前已选中封面帧时禁用（已在目标位置），选中 🖼 原始帧时仍然可用。
        /// </summary>
        public bool IsGoToKeyPhotoEnabled =>
            SelectedTimelineFrame != null && !SelectedTimelineFrame.IsStillPhoto;

        /// <summary>
        /// "前往原始封面"按钮是否可用（跳转到 🖼 原始帧）。
        /// 当前已选中原始帧时禁用（已在目标位置）。
        /// </summary>
        public bool IsGoToOriginalPhotoEnabled =>
            SelectedTimelineFrame != null && !SelectedTimelineFrame.IsOriginalPhoto;

        /// <summary>当前选中的文件是否为"半死不活"的实况照片（有协议但缺配对文件）</summary>
        public bool IsSelectedPairIncomplete
        {
            get
            {
                var item = FileItems.FirstOrDefault(f =>
                    string.Equals(f.FilePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase));
                return item != null
                    && item.HasConfirmedProtocol
                    && item.LivePhotoType == LivePhotoType.DualFile
                    && (string.IsNullOrEmpty(item.PairedVideoPath)
                        || !File.Exists(item.PairedVideoPath));
            }
        }

        /// <summary>不完整实况 → 禁用"组合查看"和"实况照片帧"标签页</summary>
        public bool IsTimelineTabDisabled => IsSelectedPairIncomplete;

        /// <summary>协议图标颜色：正常实况=主题色，非实况/残缺=红色警告</summary>
        public SolidColorBrush ProtocolIconBrush =>
            IsSelectedLivePhoto && !IsSelectedPairIncomplete
                ? (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));

        /// <summary>ConvertProtocol 守卫：配对缺失的实况照片不允许转换协议</summary>
        private bool CanConvertProtocol() => IsSelectedLivePhoto && !IsSelectedPairIncomplete;

        /// <summary>能否播放实况：仅完全实况照片（照片+视频配对齐全）才显示播放按钮</summary>
        public bool CanPlayLivePhoto =>
            IsSelectedLivePhoto && !IsSelectedPairIncomplete;

        /// <summary>可导出单帧：非视频的实况照片（完整的或有照片即可）</summary>
        public bool CanExportCurrentFrame =>
            IsSelectedLivePhoto && !IsSelectedFileVideo;

        /// <summary>可导出多帧/视频/GIF：完整实况有帧，或者视频本身有帧</summary>
        public bool CanExportMultiFrame =>
            IsSelectedLivePhoto && (HasTimelineFrames || IsSelectedFileVideo);

        /// <summary>ViewModel 通知 View 层滚动到指定帧（ItemsRepeater 布局就绪后吸附定位）</summary>
        public event Action<TimelineFrame>? RequestScrollToFrame;

        /// <summary>ViewModel 通知 View 层强制清空 PhotoViewer 双缓冲层（实况→非实况切换时）</summary>
        public event Action? PreviewClearRequested;

        /// <summary>标记：设置页切换模式后，OnNavigatedTo 需要修正滚动位置和初始化</summary>
        public bool NeedsModeSwitchFixup { get; set; }

        /// <summary>标记当前 SelectedTimelineFrame 是否为程序化设置（vs 用户手动点击）。
        /// 为 true 时允许触发滚动，为 false 时跳过滚动（用户手动点击不滚）。</summary>
        private bool _isProgrammaticTimelineSelection;

        partial void OnSelectedTimelineFrameChanged(TimelineFrame? value)
        {
            OnPropertyChanged(nameof(IsSetKeyPhotoEnabled));
            OnPropertyChanged(nameof(IsGoToKeyPhotoEnabled));
            OnPropertyChanged(nameof(IsGoToOriginalPhotoEnabled));

            // 更新帧位置文本
            if (value != null)
            {
                // 使用 ffmpeg 实际提取帧数作为总数，避免 OPPO 合并帧时计数不一致
                // FrameIndex >= 0 表示真实视频帧（含 OPPO 合并模式下打标的 ⭐ 封面帧），
                // FrameIndex == -1 表示插入的特殊帧（⭐ 独立封面 / 🖼 原始封面），不计入总数。
                int totalFrameCount = _timelineActualFrameCount > 0
                    ? _timelineActualFrameCount
                    : TimelineFrames.Count(f => f.FrameIndex >= 0);

                if (value.IsOriginalPhoto)
                {
                    // 原始帧：不显示在视频帧计数中，显示为 "Original"
                    CurrentFramePositionText = ResourceService.Format(
                        "EditPage_TimelineFrameOriginalPhoto", totalFrameCount);
                }
                else if (value.IsStillPhoto)
                {
                    // 封面帧：显示 "Cover · 共 N 帧"
                    CurrentFramePositionText = ResourceService.Format(
                        "EditPage_TimelineFrameKeyPhoto", totalFrameCount);
                }
                else
                {
                    // 普通视频帧：用 FrameIndex >= 0 判定真实视频帧（含 OPPO 合并的 ⭐），
                    // FrameIndex == -1 的特殊帧（独立插入的 ⭐/🖼）不参与排序。
                    var videoFrames = TimelineFrames.Where(f => f.FrameIndex >= 0).ToList();
                    int idx = videoFrames.IndexOf(value);
                    if (idx >= 0)
                    {
                        CurrentFramePositionText = ResourceService.Format(
                            "EditPage_TimelineFramePosition", idx + 1, totalFrameCount);
                    }
                    else
                    {
                        CurrentFramePositionText = string.Empty;
                    }
                }
            }
            else
            {
                CurrentFramePositionText = string.Empty;
            }

            if (value == null) return;

            // 同步 IsSelected 标记到所有帧：仅当前选中帧为 true
            foreach (var f in TimelineFrames)
                f.IsSelected = ReferenceEquals(f, value);

            if (_isProgrammaticTimelineSelection)
            {
                // 程序化选中（初始加载/切换文件后的自动选中）→ 触发滚动吸附
                _isProgrammaticTimelineSelection = false;
                RequestScrollToFrame?.Invoke(value);
            }
            else
            {
                // 初始加载自动滚动时不更新大图（避免滚过几十帧时大图疯狂切换）
                if (!_isInitialTimelineScroll)
                    _ = UpdatePreviewForTimelineFrameAsync(value);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  时间轴行为控制（动画 / 惯性 / 帧导航）
        // ══════════════════════════════════════════════════════════════

        /// <summary>时间轴滑动动画开关（关闭时硬切跳转，无过渡）</summary>
        [ObservableProperty]
        private bool _isTimelineAnimationEnabled = true;

        /// <summary>时间轴滚动惯性开关（关闭时松手即停，无惯性滑行）</summary>
        [ObservableProperty]
        private bool _isTimelineInertiaEnabled = true;

        /// <summary>
        /// 当前选中的关键帧（别名，对应需求中的 CurrentKeyFrame）。
        /// 与 SelectedTimelineFrame 指向同一对象，供外部以明确语义访问。
        /// </summary>
        public TimelineFrame? CurrentKeyFrame
        {
            get => SelectedTimelineFrame;
            set => SelectedTimelineFrame = value;
        }

        /// <summary>切换到上一帧（时间轴中心吸附后自动同步 CurrentKeyFrame）</summary>
        [RelayCommand]
        private void GoToPreviousFrame()
        {
            if (TimelineFrames.Count == 0) return;
            int idx = SelectedTimelineFrame != null
                ? TimelineFrames.IndexOf(SelectedTimelineFrame)
                : 0;
            if (idx > 0)
                SelectTimelineFrameProgrammatically(TimelineFrames[idx - 1]);
        }

        /// <summary>切换到下一帧（时间轴中心吸附后自动同步 CurrentKeyFrame）</summary>
        [RelayCommand]
        private void GoToNextFrame()
        {
            if (TimelineFrames.Count == 0) return;
            int idx = SelectedTimelineFrame != null
                ? TimelineFrames.IndexOf(SelectedTimelineFrame)
                : -1;
            if (idx >= 0 && idx < TimelineFrames.Count - 1)
                SelectTimelineFrameProgrammatically(TimelineFrames[idx + 1]);
        }

        // ══════════════════════════════════════════════════════════════
        //  时间轴模式（读取 SettingsViewModel 的设置）
        // ══════════════════════════════════════════════════════════════

        /// <summary>是否为经典 ListView 模式（0 = 经典模式）</summary>
        public bool IsClassicTimelineMode =>
            AppViewModel.Instance.Settings.TimelineModeIndex == 0;

        /// <summary>是否为胶片模式（1 = 胶片模式，固定选中框 + 逐帧步进）</summary>
        public bool IsFilmstripTimelineMode =>
            AppViewModel.Instance.Settings.TimelineModeIndex == 1;

        /// <summary>
        /// 当设置页切换时间轴模式时调用。
        /// 只打标记，不触发任何 UI 变更。等用户导航回 KeyPhotoPage（前台）时，
        /// 由 OnNavigatedTo 调用 TriggerModeVisibilityUpdate() 正式切换 Visibility，
        /// 避免后台页面 x:Bind 断裂导致点击缩略图不更新封面、滚动条失效。
        /// </summary>
        public void NotifyTimelineModeChanged()
        {
            // 只打标记，不在后台触发 OnPropertyChanged。
            // Visibility 切换推迟到 TriggerModeVisibilityUpdate()，
            // 由 KeyPhotoPage.OnNavigatedTo 在前台调用。
            NeedsModeSwitchFixup = true;
        }

        /// <summary>
        /// 供 View 层在页面回到前台时调用，正式触发 Visibility 切换。
        /// 必须在 OnNavigatedTo 中调用，而不是在 NotifyTimelineModeChanged 中，
        /// 否则 WinUI 3 在后台页面切换 Visibility 会导致 x:Bind 绑定断裂。
        /// </summary>
        public void TriggerModeVisibilityUpdate()
        {
            OnPropertyChanged(nameof(IsClassicTimelineMode));
            OnPropertyChanged(nameof(IsFilmstripTimelineMode));
        }

        /// <summary>
        /// 以程序化方式选中帧（触发滚动吸附 + 大图预览更新）。
        /// 区别于用户手动拖拽吸附：此方法会触发 RequestScrollToFrame 事件。
        /// </summary>
        public void SelectTimelineFrameProgrammatically(TimelineFrame frame)
        {
            if (SelectedTimelineFrame == frame)
            {
                // 已选中同一帧：[ObservableProperty] setter 不会触发 OnChanged，
                // 但调用方期望触发滚动（如首次加载后定位封面帧）。
                // 手动复现 OnSelectedTimelineFrameChanged 的程序化选中路径。
                OnPropertyChanged(nameof(IsSetKeyPhotoEnabled));
                foreach (var f in TimelineFrames)
                    f.IsSelected = ReferenceEquals(f, frame);
                RequestScrollToFrame?.Invoke(frame);
                return;
            }

            _isProgrammaticTimelineSelection = true;
            SelectedTimelineFrame = frame;
        }

        /// <summary>
        /// 以交互方式选中帧（不触发滚动，仅更新大图预览 + CurrentKeyFrame）。
        /// 用于用户拖拽结束后中心吸附选中。
        /// </summary>
        public void SelectTimelineFrameInteractively(TimelineFrame frame)
        {
            _isProgrammaticTimelineSelection = false;
            SelectedTimelineFrame = frame;
        }

        [ObservableProperty] private bool _isModified;

        /// <summary>大图预览的图片源（PhotoViewer 绑定）。通用 ImageSource 类型，不限定 BitmapImage</summary>
        [ObservableProperty]
        private ImageSource? _previewImageSource;

        /// <summary>
        /// 安全设置 PreviewImageSource（带 try-catch 保护）。
        /// x:Bind 会同步调用 PhotoViewer.ImageSource → SetValue(DependencyProperty) → COM，
        /// 若控件正在销毁或线程不对 → COM 异常可能直接杀进程（0xc000027b），
        /// 必须兜底捕获，防止崩到 WinUI 层之外。
        /// </summary>
        private void SetPreviewSafe(ImageSource? source)
        {
            try { PreviewImageSource = source; }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto SetPreviewSafe failed: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Warning);
            }
        }

        /// <summary>预览图加载取消令牌（切换文件时取消上一次加载）</summary>
        private CancellationTokenSource? _previewLoadCts;

        [ObservableProperty]
        private string? _selectedFilePath;

        /// <summary>是否有文件被选中（用于控制信息面板图标和分隔线可见性）</summary>
        public bool HasSelectedFile => !string.IsNullOrEmpty(SelectedFilePath);

        /// <summary>当前目录是否加载了文件（控制折叠按钮可见性）</summary>
        public bool HasAnyFiles => FileItems.Count > 0;

        /// <summary>是否已加载过目录（_allFileItems 有数据），用于区分"未选择目录"和"筛选结果为空"</summary>
        public bool HasFilesLoaded => _allFileItems.Count > 0;

        /// <summary>选中文件是否为独立视频（控制信息面板照片行可见性）</summary>
        private bool _isSelectedFileVideo;
        public bool IsSelectedFileVideo
        {
            get => _isSelectedFileVideo;
            set
            {
                if (SetProperty(ref _isSelectedFileVideo, value))
                {
                    OnPropertyChanged(nameof(IsSelectedFileVideo));
                    OnPropertyChanged(nameof(IsPhotoRowVisible));
                    OnPropertyChanged(nameof(IsVideoRowVisible));
                    OnPropertyChanged(nameof(CanExportCurrentFrame));
                    OnPropertyChanged(nameof(CanExportMultiFrame));
                    OnPropertyChanged(nameof(CanPlayLivePhoto));
                }
            }
        }

        /// <summary>照片信息行可见（非视频文件时显示）</summary>
        public bool IsPhotoRowVisible => !IsSelectedFileVideo;

        /// <summary>选中文件是否为已确认协议的实况照片</summary>
        private bool _isSelectedLivePhoto;
        public bool IsSelectedLivePhoto
        {
            get => _isSelectedLivePhoto;
            set
            {
                if (SetProperty(ref _isSelectedLivePhoto, value))
                {
                    OnPropertyChanged(nameof(IsSelectedLivePhoto));
                    OnPropertyChanged(nameof(IsVideoRowVisible));
                    OnPropertyChanged(nameof(CanExportCurrentFrame));
                    OnPropertyChanged(nameof(CanExportMultiFrame));
                    OnPropertyChanged(nameof(CanPlayLivePhoto));
                    OnPropertyChanged(nameof(ProtocolIconBrush));
                    ConvertProtocolCommand.NotifyCanExecuteChanged();
                }
            }
        }

        /// <summary>静音状态（跨文件选择 + 跨会话持久保持，写入 AppSettings）</summary>
        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    OnPropertyChanged(nameof(IsMuted));
                    AppSettingsService.SetValue("IsLivePhotoMuted", value);
                }
            }
        }

        /// <summary>视频信息行可见（有实际视频数据时才显示，缺失视频的实况不显示）</summary>
        public bool IsVideoRowVisible =>
            IsSelectedFileVideo || (IsSelectedLivePhoto && !IsSelectedPairIncomplete);

        /// <summary>选中文件的缩略图（信息面板用，直接复用列表已加载的）</summary>
        private Microsoft.UI.Xaml.Media.ImageSource? _selectedFileThumbnail;
        public Microsoft.UI.Xaml.Media.ImageSource? SelectedFileThumbnail
        {
            get => _selectedFileThumbnail;
            set { if (SetProperty(ref _selectedFileThumbnail, value)) OnPropertyChanged(nameof(SelectedFileThumbnailPlaceholderVisibility)); }
        }

        /// <summary>信息面板缩略图占位符可见性</summary>
        public Microsoft.UI.Xaml.Visibility SelectedFileThumbnailPlaceholderVisibility =>
            _selectedFileThumbnail == null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        // ══════════════════════════════════════════════════════════════
        //  CommandBar 命令
        // ══════════════════════════════════════════════════════════════

        [RelayCommand] private void GoBack() { }
        [RelayCommand] private void Restore() { }
        /// <summary>
        /// "设为封面并保存为副本"：将时间轴当前选中的帧设为新的封面，
        /// 保留原视频段 + EXIF 信息 + 实况照片协议信息，输出到用户指定位置。
        /// 支持 Google MicroVideo V1、Google Motion Photo V2、OPPO O-Live Photo。
        /// </summary>
        [RelayCommand]
        private async Task Save()
        {
            await ProcessingNotReadyDialogService.ShowAsync("cover");
        }

        private async Task SaveLegacyAsync()
        {
            // ── 1. Guards ──────────────────────────────────────────────────
            var frame = SelectedTimelineFrame;
            if (frame == null || frame.IsStillPhoto)
            {
                LogService.FileOp("KeyPhoto Save: no valid frame selected", LogLevel.Warning);
                return;
            }

            var photoPath = SelectedFilePath;
            if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
            {
                LogService.FileOp("KeyPhoto Save: no file selected or file not found", LogLevel.Warning);
                return;
            }

            // 🖼 原始封面帧：FullFramePath 已指向高清原图临时文件；回退到重新提取
            if (frame.IsOriginalPhoto && (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath)))
            {
                byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(photoPath);
                if (origBytes == null || origBytes.Length == 0)
                {
                    LogService.FileOp("KeyPhoto Save: 🖼 original photo bytes unavailable", LogLevel.Warning);
                    return;
                }
                string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_save_orig_{Guid.NewGuid():N}.jpg");
                File.WriteAllBytes(tempPath, origBytes);
                frame = new TimelineFrame
                {
                    FrameIndex = frame.FrameIndex,
                    Timestamp = frame.Timestamp,
                    FullFramePath = tempPath,
                    IsOriginalPhoto = true
                };
            }

            if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
            {
                LogService.FileOp("KeyPhoto Save: frame FullFramePath not available", LogLevel.Warning);
                return;
            }

            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, photoPath, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                LogService.FileOp("KeyPhoto Save: file item not found in list", LogLevel.Warning);
                return;
            }

            // Apple 双文件实况照片（HEIC + MOV）→ 单独分支
            // 必须精确匹配 Apple 协议，避免 vivo old 等 DualFile 协议误入
            if (item.LivePhotoType == LivePhotoType.DualFile
                && item.DetectedProtocol == LivePhotoProtocolType.Apple)
            {
                await SaveAppleAsync(frame, item, photoPath);
                return;
            }

            // Huawei / Honor Moving Photo（SingleFileHeic / SingleFileJpeg）→ 单独分支
            // 华为/荣耀没有 XMP，使用 LIVE_ 二进制尾标，需走专用导出流程
            if (item.DetectedProtocol == LivePhotoProtocolType.Huawei)
            {
                await SaveHuaweiAsync(frame, item, photoPath);
                return;
            }

            // Motion Photo Fusion（融合协议：V2 + OPPO + VIVO + Samsung Trailer）
            // 必须出现在 Samsung 专属检查之前：Fusion 继承 Samsung 二进制布局，
            // 但需要全量多命名空间 XMP（GCamera+OpCamera+VCamera）+ OPPO EXIF 预处理。
            if (item.DetectedProtocol == LivePhotoProtocolType.Fusion)
            {
                await SaveSamsungAsync(frame, item, photoPath, protocolIndex: 0);
                return;
            }

            // Samsung Motion Photo（JPEG / HEIC）→ 单独分支
            // 三星需要同时写 V2 XMP + Samsung Trailer（SEFH/SEFT），缺一不可
            if (item.DetectedProtocol == LivePhotoProtocolType.Samsung)
            {
                await SaveSamsungAsync(frame, item, photoPath);
                return;
            }

            // vivo 旧格式双文件（≤X200, JPEG+MP4, vivo JSON 尾标配对）
            if (item.DetectedProtocol == LivePhotoProtocolType.Vivo
                && item.LivePhotoType == LivePhotoType.DualFile)
            {
                await SaveVivoOldAsync(frame, item, photoPath);
                return;
            }

            // Google V2 HEIC（单文件 HEIC 实况照片：HEIC + mpvd box + XMP）
            // 需要用 heif-enc 生成 HEIC + WriteLivePhotoAsync 的 HEIC 管线
            if (item.DetectedProtocol == LivePhotoProtocolType.GoogleV2
                && item.LivePhotoType == LivePhotoType.SingleFileHeic)
            {
                await SaveGoogleV2HeicAsync(frame, item, photoPath);
                return;
            }

            // 仅支持 SingleFileJpeg（Google V1/V2、OPPO）
            if (item.LivePhotoType != LivePhotoType.SingleFileJpeg)
            {
                LogService.FileOp(
                    $"KeyPhoto Save: unsupported type {item.LivePhotoType} (only SingleFileJpeg supported)",
                    LogLevel.Warning);
                return;
            }

            if (item.AppendedVideoLength <= 0)
            {
                LogService.FileOp("KeyPhoto Save: AppendedVideoLength is not available", LogLevel.Warning);
                return;
            }

            // ── 2. 先弹出保存对话框，让用户选位置 ────────────────────────
            var photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
            var suggestedName = frame.IsOriginalPhoto
                ? $"{photoBaseName}_原始封面"
                : $"{photoBaseName}_封面帧{frame.FrameIndex + 1}";

            var savedFile = await FilePickerService.PickSaveFileForExportAsync(".JPG", suggestedName);
            if (savedFile == null)
            {
                LogService.FileOp("KeyPhoto Save: cancelled by user", LogLevel.Info);
                return; // 用户取消
            }
            string targetPath = savedFile.Path;

            // ── 3. 显示进度 ───────────────────────────────────────────
            BeginExportProgress(ResourceService.GetString("EditPage_SaveKeyPhotoInProgress"));

            string? tempWorkDir = null;
            string? tempVideoPath = null;

            try
            {
                // ── 4. 协议选择（直接使用 item.DetectedProtocol） ─────────
                // item.DetectedProtocol 在文件加载时已由 LivePhotoProtocolDetector.Detect() 检测好，
                // 属性面板显示的正是这个值。无需重新从 XMP 文本检测。
                int protocolIndex = item.DetectedProtocol switch
                {
                    LivePhotoProtocolType.Fusion => 0,
                    LivePhotoProtocolType.GoogleV1 => 1,
                    LivePhotoProtocolType.GoogleV2 => 2,
                    LivePhotoProtocolType.OPPO => 3,
                    LivePhotoProtocolType.Vivo => 4,
                    LivePhotoProtocolType.Samsung => 5,
                    _ => 2, // default to V2
                };

                LivePhotoProtocol protocol = LivePhotoProtocol.FromIndex(protocolIndex);

                LogService.FileOp(
                    $"KeyPhoto Save: protocol={protocol.Key}, frame=#{frame.FrameIndex} @ {frame.Timestamp}",
                    LogLevel.Info);

                // ── 5. 创建工作目录 ─────────────────────────────────────
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 6. 复制帧 JPEG 到工作目录，注入原图 EXIF ──────────
                string workImagePath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, workImagePath, overwrite: true);

                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", photoPath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    workImagePath);

                // ── 7. 协议预处理（OPPO 注入 oplus_ EXIF 标记） ────────
                string processedImagePath = await protocol.PrepareImageAsync(
                    workImagePath, tempWorkDir, CancellationToken.None);

                // ── 8. 提取视频段到临时文件 ─────────────────────────────
                var fileSize = new FileInfo(photoPath).Length;
                long videoOffset = fileSize - item.AppendedVideoLength;
                long videoExtractLen = item.AppendedVideoLength; // 默认：从 offset 到 EOF（V1/V2/vivo 视频在文件末尾）

                // OPPO 原厂文件在视频后面还有 OnePlus trailer（~846KB）：
                // AppendedVideoLength = Container Item:Length 覆盖"视频+trailer"，
                // 若整段提取，重写后 OpCamera:VideoLength 会被写成"视频+trailer"（应只写纯视频）。
                // 改用 OpCamera:VideoLength 只提取纯 MP4，输出干净的 OPPO 文件。
                if (item.DetectedProtocol == LivePhotoProtocolType.OPPO)
                {
                    long pureLen = 0;
                    try
                    {
                        pureLen = LivePhotoSplitService.GetOppoPureVideoLength(
                            LivePhotoSplitService.ReadMetadataTextSync(photoPath));
                    }
                    catch { pureLen = 0; } // 元数据读取失败 → 退回整段提取（AppendedVideoLength 兜底）
                    if (pureLen > 0 && pureLen <= videoExtractLen)
                        videoExtractLen = pureLen;
                }

                tempVideoPath = Path.Combine(tempWorkDir, "video.mp4");
                using (var src = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.Position = videoOffset;
                    var buf = new byte[81920];
                    long remain = videoExtractLen;
                    while (remain > 0)
                    {
                        int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                        if (r == 0) break;
                        dst.Write(buf, 0, r);
                        remain -= r;
                    }
                }

                long actualVideoSize = new FileInfo(tempVideoPath).Length;

                // ── 9. 构建输出文件 ─────────────────────────────────────
                long presentationTimestampUs = (long)(frame.Timestamp.TotalSeconds * 1_000_000);

                string tempOutputPath;
                if (protocol is SamsungMotionPhotoProtocol)
                {
                    // Samsung 家族协议（Samsung / Fusion）：需要完整管线
                    // XMP 注入 + Samsung Trailer（SEFH/SEFT）+ OPPO EXIF（Fusion）。
                    // WriteLivePhotoAsync 根据 protocolIndex 自动路由到正确的 BuildXmpMetadata。
                    tempOutputPath = Path.Combine(tempWorkDir, "output_samsung.jpg");
                    await LivePhotoMergeService.WriteLivePhotoAsync(
                        processedImagePath, tempVideoPath, tempOutputPath,
                        selectedModeIndex: protocolIndex,
                        CancellationToken.None,
                        presentationTimestampUs);
                }
                else
                {
                    // 标准协议（V1, V2, OPPO, Vivo standalone）：SOI + APP1 XMP + JPEG + video
                    byte[] xmpBytes = protocol.BuildXmpMetadata(actualVideoSize, presentationTimestampUs);

                    tempOutputPath = Path.Combine(tempWorkDir, "output.jpg");
                    await LivePhotoMergeService.WriteNativeAsync(
                        processedImagePath, tempVideoPath, tempOutputPath, xmpBytes, CancellationToken.None);
                }

                // 先写到临时文件，再用 WinRT API 复制到用户选择的路径
                // （直接 FileMode.Create 写入 FileSavePicker 返回的路径可能会因系统句柄导致 0 字节）
                var tempFile = await StorageFile.GetFileFromPathAsync(tempOutputPath);
                await tempFile.CopyAndReplaceAsync(savedFile);

                LogService.FileOp($"KeyPhoto Save: combined file written to '{targetPath}'", LogLevel.Info);

                IsModified = false;

                LogService.FileOp(
                    $"KeyPhoto Save SUCCESS: {Path.GetFileName(photoPath)} frame#{frame.FrameIndex} -> '{targetPath}'",
                    LogLevel.Info);

                                // 修改日期设为当前时间
                    try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                // ── 11. 完成 ──
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoComplete"),
                    Path.GetDirectoryName(targetPath));
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                // 清理可能不完整的输出文件
                try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }

                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                    $"{ResourceService.GetString("EditPage_SaveError")}: {ex.Message}",
                    Path.GetDirectoryName(targetPath));
            }
            finally
            {
                FinalizeExportProgress();
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 用 exiftool 回读已保存文件的 PresentationTimestamp，验证写入正确。
        /// 返回微秒值，读取失败或标签不存在时返回 null。
        private static Task<long?> ReadTimestampFromFileAsync(string filePath) => Task.FromResult<long?>(null);

        /// <summary>
        /// Huawei / Honor Moving Photo 的"设为封面并保存为副本"。
        ///
        /// === 整体流程 ===
        /// 1. 用户选帧 → 提取嵌入 MP4 → 注入 covertime 元数据（新版相册封面定位）
        /// 2. 读原尾部 PPP:QQQQ → 构造新尾部（旧版兼容）
        /// 3. 帧 JPEG 注入原图 EXIF（exiftool -TagsFromFile --xmp:all）
        /// 4. HEIC 输出：帧 JPEG → heif-enc → HEIC → InsertTmapBrand → [HEIC] + [MP4] + [tail]
        /// 5. JPEG 输出：[帧JPEG] + [MP4] + [tail] → exiftool -Make=HUAWEI
        /// 6. 新尾部：v6_fXX=选中帧（旧版相册）、PPP:QQQQ=原始值保留、LIVE_=新MP4字节数+20
        /// 7. MP4 udta：com.openharmony.covertime=帧时间戳(ms)（新版相册封面定位）
        /// </summary>
        private async Task SaveHuaweiAsync(TimelineFrame frame, EditFileItem item, string photoPath)
        {
            string? tempWorkDir = null;
            string? targetPath = null;
            string? tempVideoPath = null;
            try
            {
                // ── 1. 守卫 ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                {
                    LogService.FileOp("KeyPhoto Save[Huawei]: frame FullFramePath not available", LogLevel.Warning);
                    return;
                }

                bool isHeicInput = HeicConverterService.IsHeicFile(photoPath);

                // HEIC 输出需要 heif-enc.exe
                if (isHeicInput)
                {
                    string heifEncPath = Path.Combine(AppContext.BaseDirectory, "Tools", "heif-enc.exe");
                    if (!File.Exists(heifEncPath))
                    {
                        LogService.FileOp("KeyPhoto Save[Huawei]: heif-enc.exe not found", LogLevel.Warning);
                        return;
                    }
                }

                // ── 2. 弹出保存对话框 ───────────────────────────────────
                string photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
                string suggestedName = frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始封面"
                    : $"{photoBaseName}_封面帧{frame.FrameIndex + 1}";

                string saveExt = isHeicInput ? ".HEIC" : ".JPG";
                var savedFile = await FilePickerService.PickSaveFileForExportAsync(
                    saveExt, suggestedName, jpegOption: isHeicInput);
                if (savedFile == null)
                {
                    LogService.FileOp("KeyPhoto Save[Huawei]: cancelled by user", LogLevel.Info);
                    return;
                }
                targetPath = savedFile.Path;
                bool isHeicOutput = targetPath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                                 || targetPath.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

                LogService.FileOp(
                    $"KeyPhoto Save[Huawei]: start — frame=#{frame.FrameIndex + 1} @ {frame.Timestamp.TotalSeconds:F3}s, " +
                    $"target='{targetPath}', heicOut={isHeicOutput}",
                    LogLevel.Info);

                // ── 3. 显示进度 ──────────────────────────────────────────
                BeginExportProgress(ResourceService.GetString("EditPage_SaveKeyPhotoInProgress"));

                // ── 4. 读取原文件尾部 PPP:QQQQ ──────────────────────────
                int originalCoverMs = 0, originalDurationMs = 0;
                var tailInfo = HuaweiMovingPhotoProtocol.ReadTail(photoPath);
                if (tailInfo.HasValue)
                {
                    originalCoverMs = tailInfo.Value.coverMs;
                    originalDurationMs = tailInfo.Value.durationMs;
                }

                // ── 5. 创建 temp 工作目录 ────────────────────────────────
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_hw_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 6. 帧 JPEG 注入原图 EXIF ────────────────────────────
                string workImagePath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, workImagePath, overwrite: true);

                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", photoPath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    workImagePath);

                // ── 7. 提取嵌入 MP4 ─────────────────────────────────────
                var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(photoPath);
                if (range == null)
                    throw new InvalidDataException("Cannot locate embedded MP4 in Huawei file");

                var (videoStart, videoEnd, videoLength) = range.Value;
                tempVideoPath = Path.Combine(tempWorkDir, "video.mp4");

                using (var src = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.Seek(videoStart, SeekOrigin.Begin);
                    var buf = new byte[81920];
                    long remain = videoLength;
                    while (remain > 0)
                    {
                        int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                        if (r == 0) break;
                        dst.Write(buf, 0, r);
                        remain -= r;
                    }
                }

                // ── 7.5 注入 covertime 元数据（新版华为相册封面帧定位）───
                int covertimeMs = (int)frame.Timestamp.TotalMilliseconds;
                tempVideoPath = await LivePhotoMergeService.WriteMp4CovertimeMetadataAsync(
                    tempVideoPath, targetPath, covertimeMs, CancellationToken.None);

                // ── 8. 获取视频总帧数、计算封面帧 ──────────────────────
                int totalFrames = await LivePhotoMergeService.DetectVideoFrameCountAsync(
                    tempVideoPath, CancellationToken.None);
                int coverFrame = frame.FrameIndex; // 0-based timeline frame index

                // ── 9. 构建新尾部 ───────────────────────────────────────
                long actualVideoSize = new FileInfo(tempVideoPath).Length;
                byte[] tail = originalDurationMs > 0
                    ? HuaweiMovingPhotoProtocol.BuildTail(coverFrame, totalFrames, actualVideoSize,
                        originalCoverMs, originalDurationMs)
                    : HuaweiMovingPhotoProtocol.BuildTail(coverFrame, totalFrames, actualVideoSize);

                // ── 10. 组装输出文件 ────────────────────────────────────
                if (isHeicOutput)
                {
                    // 帧 JPEG → HEIC（heif-enc）
                    string tempHeicPath = Path.Combine(tempWorkDir, $"keyframe_{Guid.NewGuid():N}.heic");
                    string heifEncPath = Path.Combine(AppContext.BaseDirectory, "Tools", "heif-enc.exe");

                    var heifPsi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = heifEncPath,
                        Arguments = $"-o \"{tempHeicPath}\" -q 90 \"{workImagePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var heifProc = System.Diagnostics.Process.Start(heifPsi);
                    if (heifProc == null)
                        throw new InvalidOperationException("Failed to start heif-enc.exe");
                    await heifProc.WaitForExitAsync();
                    if (heifProc.ExitCode != 0 || new FileInfo(tempHeicPath).Length == 0)
                        throw new InvalidOperationException(
                            $"heif-enc failed with exit code {heifProc.ExitCode}");

                    // Patch ftyp → 插入 tmap brand
                    byte[] heicData = await File.ReadAllBytesAsync(tempHeicPath);
                    byte[] patched = LivePhotoMergeService.InsertTmapBrand(heicData);

                    // 写入: HEIC + MP4 + tail
                    using (var targetFs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await targetFs.WriteAsync(patched, 0, patched.Length);
                        using var vidFs = new FileStream(tempVideoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await vidFs.CopyToAsync(targetFs);
                        await targetFs.WriteAsync(tail, 0, tail.Length);
                    }
                }
                else
                {
                    // JPEG 输出: 帧 JPEG + MP4 + tail
                    using (var targetFs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        using var imgFs = new FileStream(workImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await imgFs.CopyToAsync(targetFs);
                        using var vidFs = new FileStream(tempVideoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await vidFs.CopyToAsync(targetFs);
                        await targetFs.WriteAsync(tail, 0, tail.Length);
                    }

                    // 写入 HUAWEI EXIF Make 标记
                    try
                    {
                        await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                            "-overwrite_original", "-Make=HUAWEI", targetPath);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"KeyPhoto Save[Huawei]: HUAWEI EXIF Make write failed (non-fatal): {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                IsModified = false;

                // ── 11. 修改日期为当前时间 ────────────────────────────────
                try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                LogService.FileOp(
                    $"KeyPhoto Save[Huawei] SUCCESS: {Path.GetFileName(photoPath)} " +
                    $"frame#{frame.FrameIndex} -> '{targetPath}'",
                    LogLevel.Info);

                string? outputDir = Path.GetDirectoryName(targetPath);
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoComplete"), outputDir);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save[Huawei] FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                try { if (targetPath != null && File.Exists(targetPath)) File.Delete(targetPath); } catch { }

                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                    $"{ResourceService.GetString("EditPage_SaveError")}: {ex.Message}",
                    targetPath != null ? Path.GetDirectoryName(targetPath) : null);
            }
            finally
            {
                FinalizeExportProgress();
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
                try { if (File.Exists(tempVideoPath) && tempVideoPath.Contains("lpb_ct_"))
                    File.Delete(tempVideoPath); } catch { }
            }
        }

        /// <summary>
        /// Samsung Motion Photo 的"设为封面并保存为副本"。
        ///
        /// === 整体流程 ===
        /// 1. 用 exiftool -b -EmbeddedVideoFile 提取嵌入视频（MotionPhoto_Data tag / mpvd box）
        /// 2. 帧 JPEG 注入原图 EXIF
        /// 3. 调用 WriteLivePhotoAsync(protocol=5) → WriteSamsungJpegAsync
        ///    → 写入 V2 XMP + Samsung Trailer(SEFH/SEFT + MotionPhoto_Data) → 完整三星格式
        /// </summary>
        private async Task SaveSamsungAsync(TimelineFrame frame, EditFileItem item, string photoPath, int protocolIndex = 5)
        {
            string logTag = protocolIndex switch { 0 => "Fusion", _ => "Samsung" };
            string? tempWorkDir = null;
            string? targetPath = null;
            try
            {
                // ── 1. 守卫 ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                {
                    LogService.FileOp($"KeyPhoto Save[{logTag}]: frame FullFramePath not available", LogLevel.Warning);
                    return;
                }

                // ── 2. 弹出保存对话框 ───────────────────────────────────
                string photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
                string suggestedName = frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始封面"
                    : $"{photoBaseName}_封面帧{frame.FrameIndex + 1}";

                var savedFile = await FilePickerService.PickSaveFileForExportAsync(
                    ".JPG", suggestedName, jpegOption: false);
                if (savedFile == null)
                {
                    LogService.FileOp($"KeyPhoto Save[{logTag}]: cancelled by user", LogLevel.Info);
                    return;
                }
                targetPath = savedFile.Path;

                LogService.FileOp(
                    $"KeyPhoto Save[{logTag}]: start — frame=#{frame.FrameIndex + 1} @ {frame.Timestamp.TotalSeconds:F3}s",
                    LogLevel.Info);

                // ── 3. 显示进度 ──────────────────────────────────────────
                BeginExportProgress(ResourceService.GetString("EditPage_SaveKeyPhotoInProgress"));

                // ── 4. 创建 temp 工作目录 ────────────────────────────────
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_{logTag.ToLowerInvariant()}_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 5. 提取嵌入视频 ──
                // Samsung JPEG: video 在 Samsung Trailer 的 MotionPhoto_Data tag 内。
                // AppendedVideoLength = trailerSize - 24 (tag header)，即从 tag data 到 EOF。
                // 提取出的数据 = raw MP4 + 尾部残留（MotionPhoto_Version tag + SEFH/SEFT），
                // WriteSamsungJpegAsync 会用 BuildTrailer 重新包装成干净的 Trailer。
                // Samsung HEIC: video 在 mpvd box（AppendedVideoLength=0），需用 mpvd box 定位，
                // 否则会误报 "AppendedVideoLength not available" 导致设为封面失败。
                bool isHeicInput = HeicConverterService.IsHeicFile(photoPath);
                string tempVideoPath = Path.Combine(tempWorkDir, "video.mp4");
                if (item.AppendedVideoLength > 0)
                {
                    // JPEG：从文件尾部提取（MP4 + 尾部残留）
                    var fileSize = new FileInfo(photoPath).Length;
                    long videoOffset = fileSize - item.AppendedVideoLength;
                    using (var src = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.Seek(videoOffset, SeekOrigin.Begin);
                        await src.CopyToAsync(dst);
                    }
                }
                else if (isHeicInput && LivePhotoMergeService.GetMpvdVideoLength(photoPath) > 0)
                {
                    // Samsung HEIC：视频在 mpvd box（sefd 子盒之前的完整 MP4），精确提取
                    long videoStart = LivePhotoMergeService.GetMpvdVideoStart(photoPath);
                    long videoLen = LivePhotoMergeService.GetMpvdVideoLength(photoPath);
                    using (var src = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.Seek(videoStart, SeekOrigin.Begin);
                        var buf = new byte[81920];
                        long remain = videoLen;
                        while (remain > 0)
                        {
                            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (r == 0) break;
                            dst.Write(buf, 0, r);
                            remain -= r;
                        }
                    }
                }
                else
                {
                    throw new InvalidDataException("Samsung AppendedVideoLength not available");
                }

                if (new FileInfo(tempVideoPath).Length == 0)
                    throw new InvalidDataException("Cannot extract embedded video from Samsung file");

                long videoSize = new FileInfo(tempVideoPath).Length;
                LogService.FileOp(
                    $"KeyPhoto Save[{logTag}]: video extracted — {videoSize} bytes",
                    LogLevel.Info);

                // ── 6. 帧 JPEG 注入原图 EXIF ────────────────────────────
                string workImagePath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, workImagePath, overwrite: true);

                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", photoPath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    workImagePath);

                // ── 6.5. 协议特定的图片预处理 ─────────────────────────
                var resolvedProtocol = LivePhotoProtocol.FromIndex(protocolIndex);
                string preparedPath = await resolvedProtocol.PrepareImageAsync(
                    workImagePath, tempWorkDir, CancellationToken.None);
                if (preparedPath != workImagePath)
                {
                    workImagePath = preparedPath;
                }

                // ── 7. 调用 WriteLivePhotoAsync（自动路由到 WriteSamsungJpegAsync） ──
                long presentationTimestampUs = (long)(frame.Timestamp.TotalSeconds * 1_000_000);

                await LivePhotoMergeService.WriteLivePhotoAsync(
                    workImagePath, tempVideoPath, targetPath,
                    selectedModeIndex: protocolIndex,
                    CancellationToken.None,
                    presentationTimestampUs);

                IsModified = false;

                try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                LogService.FileOp(
                    $"KeyPhoto Save[{logTag}] SUCCESS: {Path.GetFileName(photoPath)} " +
                    $"frame#{frame.FrameIndex} -> '{targetPath}'",
                    LogLevel.Info);

                string? outputDir = Path.GetDirectoryName(targetPath);
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoComplete"), outputDir);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save[{logTag}] FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                try { if (targetPath != null && File.Exists(targetPath)) File.Delete(targetPath); } catch { }

                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                    $"{ResourceService.GetString("EditPage_SaveError")}: {ex.Message}",
                    targetPath != null ? Path.GetDirectoryName(targetPath) : null);
            }
            finally
            {
                FinalizeExportProgress();
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Google V2 HEIC 单文件实况照片的"设为封面并保存为副本"。
        /// Google HEIC 格式：静态 HEIC 图片 + mpvd box（视频）+ XMP MotionPhoto V2 元数据。
        /// 与 Samsung HEIC 共享 mpvd box 二进制布局，但 XMP 仅需 GCamera 命名空间。
        ///
        /// === 整体流程 ===
        /// 1. 从原始 HEIC 的 mpvd box 提取嵌入视频
        /// 2. 帧 JPEG → heif-enc → HEIC（新封面图 + 原始 EXIF）
        /// 3. WriteLivePhotoAsync(V2 HEIC + video) → 输出
        /// </summary>
        private async Task SaveGoogleV2HeicAsync(TimelineFrame frame, EditFileItem item, string photoPath)
        {
            string? tempWorkDir = null;
            string? targetPath = null;
            try
            {
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                {
                    LogService.FileOp("KeyPhoto Save[V2-Heic]: frame FullFramePath not available", LogLevel.Warning);
                    return;
                }

                string photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
                string suggestedName = frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始封面"
                    : $"{photoBaseName}_封面帧{frame.FrameIndex + 1}";

                var savedFile = await FilePickerService.PickSaveFileForExportAsync(
                    ".HEIC", suggestedName, jpegOption: false);
                if (savedFile == null)
                {
                    LogService.FileOp("KeyPhoto Save[V2-Heic]: cancelled by user", LogLevel.Info);
                    return;
                }
                targetPath = savedFile.Path;

                LogService.FileOp(
                    $"KeyPhoto Save[V2-Heic]: start — frame=#{frame.FrameIndex + 1} @ {frame.Timestamp.TotalSeconds:F3}s",
                    LogLevel.Info);

                BeginExportProgress(ResourceService.GetString("EditPage_SaveKeyPhotoInProgress"));

                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_v2heic_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 从 mpvd box 提取嵌入视频 ──────────────────────────────
                var videoRange = GetMpvdVideoRange(photoPath);
                if (videoRange == null)
                    throw new InvalidDataException("Cannot locate mpvd box / embedded video in Google HEIC file");

                var (videoStart, videoLength) = videoRange.Value;
                string tempVideoPath = Path.Combine(tempWorkDir, "video.mp4");

                using (var src = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.Seek(videoStart, SeekOrigin.Begin);
                    var buf = new byte[81920];
                    long remain = videoLength;
                    while (remain > 0)
                    {
                        int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                        if (r == 0) break;
                        dst.Write(buf, 0, r);
                        remain -= r;
                    }
                }

                long videoSize = new FileInfo(tempVideoPath).Length;

                // ── 帧 JPEG 注入原图 EXIF ───────────────────────────────
                string workImagePath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, workImagePath, overwrite: true);

                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", photoPath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    workImagePath);

                // ── heif-enc: JPEG → HEIC ────────────────────────────────
                string frameHeicPath = Path.Combine(tempWorkDir, $"keyframe_{Guid.NewGuid():N}.heic");
                string heifEncPath = Path.Combine(AppContext.BaseDirectory, "Tools", "heif-enc.exe");

                if (!File.Exists(heifEncPath))
                    throw new InvalidOperationException("heif-enc.exe not found");

                var heifPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = heifEncPath,
                    Arguments = $"-o \"{frameHeicPath}\" -q 90 \"{workImagePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var heifProc = System.Diagnostics.Process.Start(heifPsi);
                if (heifProc == null)
                    throw new InvalidOperationException("Failed to start heif-enc.exe");
                await heifProc.WaitForExitAsync();
                if (heifProc.ExitCode != 0 || new FileInfo(frameHeicPath).Length == 0)
                    throw new InvalidOperationException($"heif-enc failed (exit {heifProc.ExitCode})");

                // ── heif-enc 不保留 EXIF，手动从 enriched JPEG 拷贝 ──
                // WriteHeicNativeAsync 会注入新的 XMP，此处拷贝全部 EXIF 但排除 XMP
                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", workImagePath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    frameHeicPath);
                // heif-enc 不保留 EXIF，手动从 enriched JPEG 拷贝

                // ── 调用 WriteLivePhotoAsync ──
                // V2 HEIC → is HEIC + is MotionPhotoV2Protocol
                // → WriteHeicNativeAsync（exiftool 注入 XMP + mpvd box video）
                long presentationTimestampUs = (long)(frame.Timestamp.TotalSeconds * 1_000_000);

                await LivePhotoMergeService.WriteLivePhotoAsync(
                    frameHeicPath, tempVideoPath, targetPath,
                    selectedModeIndex: 2,
                    CancellationToken.None,
                    presentationTimestampUs);

                IsModified = false;
                try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                LogService.FileOp(
                    $"KeyPhoto Save[V2-Heic] SUCCESS: {Path.GetFileName(photoPath)} " +
                    $"frame#{frame.FrameIndex} -> '{targetPath}'",
                    LogLevel.Info);

                string? outputDir = Path.GetDirectoryName(targetPath);
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoComplete"), outputDir);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save[V2-Heic] FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);
                try { if (targetPath != null && File.Exists(targetPath)) File.Delete(targetPath); } catch { }
                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                    $"{ResourceService.GetString("EditPage_SaveError")}: {ex.Message}",
                    targetPath != null ? Path.GetDirectoryName(targetPath) : null);
            }
            finally
            {
                FinalizeExportProgress();
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 从 HEIC 文件的 mpvd box 中定位嵌入视频的字节范围。
        /// 返回 (videoStart, videoLength) 或 null。
        /// </summary>
        private static (long videoStart, long videoLength)? GetMpvdVideoRange(string heicPath)
        {
            byte[] data;
            try { data = File.ReadAllBytes(heicPath); }
            catch { return null; }

            for (int i = 0; i < data.Length - 8; i++)
            {
                if (data[i] == 'm' && data[i + 1] == 'p' &&
                    data[i + 2] == 'v' && data[i + 3] == 'd')
                {
                    if (i < 4) continue;
                    uint boxSize = (uint)(data[i - 4] << 24 | data[i - 3] << 16 |
                                          data[i - 2] << 8 | data[i - 1]);
                    long payloadStart = i + 4;
                    long boxEnd = i - 4 + boxSize;
                    if (boxEnd > data.Length) boxEnd = data.Length;

                    long videoEnd = boxEnd;
                    for (int j = (int)payloadStart; j < Math.Min(boxEnd, data.Length - 4); j++)
                    {
                        if (data[j] == 's' && data[j + 1] == 'e' &&
                            data[j + 2] == 'f' && data[j + 3] == 'd')
                        {
                            if (j >= 4) videoEnd = j - 4;
                            break;
                        }
                    }

                    long videoLength = videoEnd - payloadStart;
                    if (videoLength > 0)
                        return (payloadStart, videoLength);
                }
            }
            return null;
        }

        /// <summary>
        /// vivo 旧格式双文件实况照片（≤X200, JPEG+MP4）的"设为封面并保存为副本"。
        ///
        /// === 整体流程 ===
        /// 1. 帧 JPEG 替换原 JPEG并注入原图 EXIF
        /// 2. 原 MP4 静默复制到同目录
        /// 3. 两端重建共享配对 ID；保留 imageTime，按所选帧写 newCoverTime
        /// </summary>
        private async Task SaveVivoOldAsync(TimelineFrame frame, EditFileItem item, string photoPath)
        {
            string? tempWorkDir = null;
            string? targetJpgPath = null;
            try
            {
                // ── 1. 守卫 ──────────────────────────────────────────────
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                {
                    LogService.FileOp("KeyPhoto Save[vivo-old]: frame FullFramePath not available", LogLevel.Warning);
                    return;
                }

                string? pairedVideoPath = item.PairedVideoPath;
                if (string.IsNullOrEmpty(pairedVideoPath) || !File.Exists(pairedVideoPath))
                {
                    LogService.FileOp("KeyPhoto Save[vivo-old]: paired MP4 not found", LogLevel.Warning);
                    return;
                }

                // ── 2. 弹出保存对话框 ───────────────────────────────────
                string photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
                string suggestedName = frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始封面"
                    : $"{photoBaseName}_封面帧{frame.FrameIndex + 1}";

                var savedFile = await FilePickerService.PickSaveFileForExportAsync(
                    ".JPG", suggestedName, jpegOption: false);
                if (savedFile == null)
                {
                    LogService.FileOp("KeyPhoto Save[vivo-old]: cancelled by user", LogLevel.Info);
                    return;
                }
                targetJpgPath = savedFile.Path;

                LogService.FileOp(
                    $"KeyPhoto Save[vivo-old]: start — frame=#{frame.FrameIndex + 1} @ {frame.Timestamp.TotalSeconds:F3}s",
                    LogLevel.Info);

                // ── 3. 显示进度 ──────────────────────────────────────────
                BeginExportProgress(ResourceService.GetString("EditPage_SaveKeyPhotoInProgress"));

                // ── 4. 创建 temp 工作目录 ────────────────────────────────
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_vivo_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 5. 读原始 vivo JSON 尾标 ────────────────────────────
                byte[]? vivoTail = null;
                try
                {
                    // vivo 尾标格式: vivo{...JSON...}cameralbum![ID][FF FF FF FF][签名]
                    // 位于 JPEG 文件末尾；新版样本 cameralbum! 之后仍有数据，
                    // 因此从 vivo{ 一直取到文件末尾。
                    const int tailProbe = 8192;
                    using var srcFs = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    int probeSize = (int)Math.Min(srcFs.Length, tailProbe);
                    byte[] probe = new byte[probeSize];
                    srcFs.Seek(-probeSize, SeekOrigin.End);
                    srcFs.ReadExactly(probe, 0, probeSize);

                    // 从后往前搜 "vivo{"
                    int vivoIdx = -1;
                    for (int i = probeSize - 6; i >= 0; i--)
                    {
                        if (probe[i] == 'v' && probe[i + 1] == 'i'
                            && probe[i + 2] == 'v' && probe[i + 3] == 'o'
                            && probe[i + 4] == '{')
                        { vivoIdx = i; break; }
                    }

                    if (vivoIdx >= 0)
                    {
                        // 搜 "cameralbum!" 结尾
                        byte[] endMarker = "cameralbum!"u8.ToArray();
                        int endIdx = -1;
                        for (int i = vivoIdx; i <= probeSize - endMarker.Length; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < endMarker.Length; j++)
                            {
                                if (probe[i + j] != endMarker[j]) { match = false; break; }
                            }
                            if (match) { endIdx = i + endMarker.Length; break; }
                        }

                        if (endIdx > vivoIdx)
                        {
                            int tailLen = probeSize - vivoIdx;
                            vivoTail = new byte[tailLen];
                            Array.Copy(probe, vivoIdx, vivoTail, 0, tailLen);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"KeyPhoto Save[vivo-old]: vivo tail read failed (non-fatal): {ex.Message}",
                        LogLevel.Warning);
                }

                // ── 6. 帧 JPEG 注入原图 EXIF ────────────────────────────
                string tempJpgPath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, tempJpgPath, overwrite: true);

                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", photoPath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    tempJpgPath);

                // ── 7. 追加 vivo 尾标到新 JPEG ───────────────────────────
                if (vivoTail != null && vivoTail.Length > 0)
                {
                    using var dstFs = new FileStream(tempJpgPath, FileMode.Append, FileAccess.Write, FileShare.None);
                    await dstFs.WriteAsync(vivoTail, 0, vivoTail.Length);
                }

                // ── 8. 复制新 JPEG 到用户选择的位置 ──────────────────────
                var tempFile = await StorageFile.GetFileFromPathAsync(tempJpgPath);
                await tempFile.CopyAndReplaceAsync(savedFile);

                // ── 9. 静默复制配对 MP4 到同目录 ─────────────────────────
                string outputDir = Path.GetDirectoryName(targetJpgPath)!;
                string targetBaseName = Path.GetFileNameWithoutExtension(targetJpgPath);
                string targetMovPath = Path.Combine(outputDir, targetBaseName + ".MP4");

                File.Copy(pairedVideoPath, targetMovPath, overwrite: true);

                // ── 10. 修改日期 ─────────────────────────────────────────
                try { File.SetLastWriteTime(targetJpgPath, DateTime.Now); } catch { }
                try { File.SetLastWriteTime(targetMovPath, DateTime.Now); } catch { }

                IsModified = false;

                LogService.FileOp(
                    $"KeyPhoto Save[vivo-old] SUCCESS: {Path.GetFileName(photoPath)} " +
                    $"frame#{frame.FrameIndex} -> '{targetJpgPath}' (+ '{Path.GetFileName(targetMovPath)}')",
                    LogLevel.Info);

                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoComplete"), outputDir);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save[vivo-old] FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                try { if (targetJpgPath != null && File.Exists(targetJpgPath)) File.Delete(targetJpgPath); } catch { }

                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                    $"{ResourceService.GetString("EditPage_SaveError")}: {ex.Message}",
                    targetJpgPath != null ? Path.GetDirectoryName(targetJpgPath) : null);
            }
            finally
            {
                FinalizeExportProgress();
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 保存/导出失败时显示错误弹窗。
        /// 用户可点击"打开输出目录"在资源管理器中打开目标文件夹，或"我知道了"关闭。
        /// <summary>
        /// Apple 双文件实况照片（HEIC + MOV）的"设为封面并保存为副本"。
        ///
        /// === 整体流程 ===
        /// 1. 用户选帧 → HEIC 重新编码为该帧画面，MOV 静默复制并更新 mebx 轨时间戳
        /// 2. HEIC 管线：帧 JPEG → 注入原图 EXIF → 写 XMP 时间戳 → heif-enc 转 HEIC → 回写 EXIF 兜底
        /// 3. MOV 管线：直接 File.Copy 复制原 MOV → 二进制 patch elst[0].trackDur + tkhd.duration
        ///    （Apple 实况照片的封面位置存在 MOV mebx 轨的 edit list 里，标准工具写不了，只能 patch 二进制）
        /// 4. ContentIdentifier：从原 HEIC 读取 → 显式写回新 HEIC 和新 MOV，保证重新扫描时能配对识别
        /// 5. 所有步骤独立 try-catch，某步失败不阻断整体（用户仍能得到 HEIC + MOV）
        /// </summary>
        private async Task SaveAppleAsync(TimelineFrame frame, EditFileItem item, string photoPath)
        {
            string? tempWorkDir = null;
            string? targetHeicPath = null;
            try
            {
                // ── 1. 守卫检查 ──────────────────────────────────────────
                string? pairedVideoPath = item.PairedVideoPath;
                if (string.IsNullOrEmpty(pairedVideoPath) || !File.Exists(pairedVideoPath))
                {
                    LogService.FileOp("KeyPhoto Save[Apple]: paired MOV not found", LogLevel.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                {
                    LogService.FileOp("KeyPhoto Save[Apple]: frame FullFramePath not available", LogLevel.Warning);
                    return;
                }

                string? heifEncPath = Path.Combine(AppContext.BaseDirectory, "Tools", "heif-enc.exe");
                if (!File.Exists(heifEncPath))
                {
                    LogService.FileOp("KeyPhoto Save[Apple]: heif-enc.exe not found in Tools/", LogLevel.Warning);
                    return;
                }

                // ── 2. 弹出保存对话框（与 Android 一样，先选位置再处理） ──
                string sourceBaseName = Path.GetFileNameWithoutExtension(photoPath);
                string filenameTemplate = ResourceService.GetString("EditPage_SaveAppleFilename");
                string suggestedName = string.Format(filenameTemplate, sourceBaseName, frame.FrameIndex + 1);

                var savedFile = await FilePickerService.PickSaveFileForExportAsync(
                    ".HEIC", suggestedName, jpegOption: false);
                if (savedFile == null)
                {
                    LogService.FileOp("KeyPhoto Save[Apple]: cancelled by user", LogLevel.Info);
                    return;
                }
                targetHeicPath = savedFile.Path;

                LogService.FileOp(
                    $"KeyPhoto Save[Apple]: start — frame=#{frame.FrameIndex + 1} @ {frame.Timestamp.TotalSeconds:F3}s, " +
                    $"target='{targetHeicPath}'",
                    LogLevel.Info);

                // ── 3. 显示进度 ──────────────────────────────────────────
                BeginExportProgress(ResourceService.GetString("EditPage_SaveKeyPhotoInProgress"));

                // ── 3b. 读取原 HEIC 的 ContentIdentifier（Apple 配对 UUID）──
                string? contentIdentifier = null;
                LogService.FileOp(
                    $"KeyPhoto Save[Apple]: original ContentIdentifier = {contentIdentifier ?? "(null)"}",
                    LogLevel.Info);

                // ── 4. 创建工作目录 ──────────────────────────────────────
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_apple_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 5. 把原 HEIC 全部标签写到帧 JPEG（exiftool on JPEG = 成熟可靠） ──
                string enrichedJpegPath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, enrichedJpegPath, overwrite: true);

                // --xmp:all：排除原 HEIC 的 XMP（下面自己写含时间戳的 XMP）
                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", photoPath,
                    "-all:all",
                    "--xmp:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    enrichedJpegPath);

                // ── 6. 写 MotionPhotoPresentationTimestampUs 到帧 JPEG 的 XMP ──
                long presentationTimestampUs = (long)(frame.Timestamp.TotalSeconds * 1_000_000);
                try
                {
                    // exiftool 不能直接写 GCamera 命名空间的 tag，改用原始 XMP 文件方式
                    string xmpFilePath = Path.Combine(tempWorkDir, "timestamp.xmp");
                    string xmpContent =
                        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
                        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
                        "<rdf:Description rdf:about=\"\"\n" +
                        "  xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"\n" +
                        $"  GCamera:MotionPhotoPresentationTimestampUs=\"{presentationTimestampUs}\"/>\n" +
                        "</rdf:RDF>\n" +
                        "</x:xmpmeta>\n" +
                        "<?xpacket end=\"w\"?>";
                    await File.WriteAllTextAsync(xmpFilePath, xmpContent, Encoding.UTF8);

                    await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                        $"-xmp<={xmpFilePath}",
                        "-overwrite_original",
                        "-quiet",
                        enrichedJpegPath);
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: XMP timestamp write failed (non-fatal): {ex.Message}",
                        LogLevel.Warning);
                }

                // ── 7. heif-enc: JPEG → HEIC（libheif + x265，保留 EXIF） ──
                string tempHeicPath = Path.Combine(tempWorkDir, $"keyframe_{Guid.NewGuid():N}.heic");
                string heifArgs = $"-o \"{tempHeicPath}\" -q 90 \"{enrichedJpegPath}\"";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = heifEncPath,
                    Arguments = heifArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc == null)
                    {
                        LogService.FileOp("KeyPhoto Save[Apple]: heif-enc failed to start", LogLevel.Error);
                        FailExportProgress(
                            ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                            "heif-enc failed to start",
                            Path.GetDirectoryName(targetHeicPath));
                        return;
                    }

                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

                    if (proc.ExitCode != 0)
                    {
                        string stderr = await proc.StandardError.ReadToEndAsync();
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: heif-enc exit={proc.ExitCode}, stderr: {stderr.Trim()}",
                            LogLevel.Error);
                        FailExportProgress(
                            ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                            $"heif-enc exited with code {proc.ExitCode}",
                            Path.GetDirectoryName(targetHeicPath));
                        return;
                    }
                }

                long heicSize = new FileInfo(tempHeicPath).Length;
                if (heicSize == 0)
                {
                    LogService.FileOp("KeyPhoto Save[Apple]: HEIC is 0 bytes after heif-enc", LogLevel.Error);
                    FailExportProgress(
                        ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                        "HEIC output is 0 bytes after encoding",
                        Path.GetDirectoryName(targetHeicPath));
                    return;
                }

                // ── 8. 把 enriched JPEG 标签回写到 HEIC（安全兜底，heif-enc 声称保留 EXIF） ──
                try
                {
                    await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                        "-TagsFromFile", enrichedJpegPath,
                        "-all:all",
                        "-Orientation=",
                        "-overwrite_original",
                        "-quiet",
                        tempHeicPath);
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: copy tags to HEIC failed (non-fatal): {ex.Message}",
                        LogLevel.Warning);
                }

                // ── 9. 用 WinRT API 保存 HEIC ──
                var tempHeicFile = await StorageFile.GetFileFromPathAsync(tempHeicPath);
                await tempHeicFile.CopyAndReplaceAsync(savedFile);

                // ── 9b. 显式写回 ContentIdentifier，确保配对识别 ──────
                if (!string.IsNullOrEmpty(contentIdentifier))
                {
                    try
                    {
                        await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                            $"-ContentIdentifier={contentIdentifier}",
                            "-overwrite_original",
                            "-quiet",
                            targetHeicPath);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: ContentIdentifier write to HEIC failed: {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                // ── 10. 静默复制 MOV 到同目录 ──
                string targetDir = Path.GetDirectoryName(targetHeicPath)!;
                string movFileName = Path.GetFileNameWithoutExtension(targetHeicPath) + Path.GetExtension(pairedVideoPath);
                string targetMovPath = Path.Combine(targetDir, movFileName);

                try
                {
                    File.Copy(pairedVideoPath, targetMovPath, overwrite: true);
                    NotifyShellFileCreated(targetMovPath);
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: MOV copy FAILED — {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Error, ex);
                }

                // ── 10b. 更新 MOV mebx 轨的封面时间 ──────────────────────
                try
                {
                    EditTimingService.PatchAppleStillImageTime(
                        targetMovPath, frame.Timestamp.TotalSeconds);
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: MOV StillImageTime patch FAILED — {ex.Message}",
                        LogLevel.Warning);
                }

                // ── 10c. 显式写回 ContentIdentifier 到 MOV ─────────────
                if (!string.IsNullOrEmpty(contentIdentifier))
                {
                    try
                    {
                        await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                            $"-ContentIdentifier={contentIdentifier}",
                            "-overwrite_original",
                            "-quiet",
                            targetMovPath);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: ContentIdentifier write to MOV failed: {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                IsModified = false;

                LogService.FileOp(
                    $"KeyPhoto Save[Apple] SUCCESS: {Path.GetFileName(photoPath)} frame#{frame.FrameIndex + 1} " +
                    $"-> HEIC({heicSize}B) + MOV({new FileInfo(pairedVideoPath).Length}B)",
                    LogLevel.Info);

                                // 修改日期设为当前时间
                    try { File.SetLastWriteTime(targetHeicPath, DateTime.Now); } catch { }
                    try { File.SetLastWriteTime(targetMovPath, DateTime.Now); } catch { }

                // ── 12. 完成 ──
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoComplete"),
                    Path.GetDirectoryName(targetHeicPath));
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save[Apple] FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                string? appleOutputDir = !string.IsNullOrEmpty(targetHeicPath)
                    ? Path.GetDirectoryName(targetHeicPath)
                    : null;
                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveKeyPhotoFailed"),
                    ex.Message, appleOutputDir);
            }
            finally
            {
                FinalizeExportProgress();
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 另存为：弹出 Windows 原生"另存为"对话框，将当前选中的照片保存到用户选择的位置。
        /// 如果该文件有配对的视频（PairedVideoPath），自动一同复制到同一目录。
        /// </summary>
        [RelayCommand]
        private async Task SaveAs()
        {
            var photoPath = SelectedFilePath;
            if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
            {
                LogService.FileOp("SaveAs: no file selected or file not found", LogLevel.Warning);
                return;
            }

            // 弹出另存为对话框保存照片
            var savedPath = await FilePickerService.SaveFileAsAsync(photoPath);
            if (savedPath == null) return; // 用户取消

            // 显示"正在保存…"状态
            BeginExportProgress(ResourceService.GetString("EditPage_SaveAsInProgress"));

            try
            {
                // 直接取 PairedVideoPath，有就一起复制
                var item = FileItems.FirstOrDefault(f =>
                    string.Equals(f.FilePath, photoPath, StringComparison.OrdinalIgnoreCase));
                var pairedVideoPath = item?.PairedVideoPath;
                if (!string.IsNullOrEmpty(pairedVideoPath) && File.Exists(pairedVideoPath))
                {
                    var destDir = Path.GetDirectoryName(savedPath)!;
                    var videoFileName = Path.GetFileNameWithoutExtension(savedPath) + Path.GetExtension(pairedVideoPath);
                    var destVideoPath = PathHelper.GetUniqueFilePath(destDir, videoFileName);
                    File.Copy(pairedVideoPath, destVideoPath, overwrite: true);
                    LogService.FileOp(
                        $"SaveAs: paired video copied: {pairedVideoPath} -> {destVideoPath}",
                        LogLevel.Info);
                    NotifyShellFileCreated(destVideoPath);
                }

                LogService.FileOp($"SaveAs: saved to '{savedPath}'", LogLevel.Info);

                CompleteExportProgress(
                    ResourceService.GetString("EditPage_SaveAsComplete"),
                    Path.GetDirectoryName(savedPath));
            }
            catch (Exception ex)
            {
                LogService.FileOp($"SaveAs FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, ex);
                FailExportProgress(
                    ResourceService.GetString("EditPage_SaveAsFailed"),
                    ex.Message, Path.GetDirectoryName(savedPath));
            }
            finally
            {
                FinalizeExportProgress();
            }
        }
        [RelayCommand] private void Export() { }
        /// <summary>
        /// 导出当前帧：弹出多格式另存为窗口，用户选格式后按需转换。
        /// 支持 JPEG / WebP / BMP / TIFF / HEIC。
        /// </summary>
        [RelayCommand]
        private async Task ExportCurrentFrame()
        {
            var frame = SelectedTimelineFrame;

            // 不完整实况（仅照片，无时间轴）：直接导出照片文件本身
            if (frame == null && IsSelectedLivePhoto && !IsSelectedFileVideo)
            {
                await ExportPhotoAsSingleFrame();
                return;
            }

            if (frame == null) return;

            // 1. 确定源文件路径
            string sourcePath;
            if (frame.IsStillPhoto || frame.IsOriginalPhoto)
            {
                // 封面帧 ⭐ 和原始帧 🖼 都使用其专属的源路径（FullFramePath 或原文件）
                if (frame.IsOriginalPhoto)
                {
                    if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    {
                        // 回退：从容器重新提取 Original JPEG
                        var photoPath = SelectedFilePath;
                        if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath)) return;
                        byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(photoPath);
                        if (origBytes == null || origBytes.Length == 0) return;
                        string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_orig_export_{Guid.NewGuid():N}.jpg");
                        await File.WriteAllBytesAsync(tempPath, origBytes);
                        sourcePath = tempPath;
                    }
                    else
                    {
                        sourcePath = frame.FullFramePath;
                    }
                }
                else
                {
                    var photoPath = SelectedFilePath;
                    if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath)) return;
                    // ⭐ 封面静止帧：单文件容器需提取干净图片（否则 HEIC 导出 0 字节 / JPEG 混入视频）
                    sourcePath = await ResolveStillPhotoSourceAsync(photoPath, CancellationToken.None);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    return;
                sourcePath = frame.FullFramePath;
            }

            // 2. 生成建议文件名
            var photoBaseName = Path.GetFileNameWithoutExtension(SelectedFilePath ?? "photo");
            var suggestedName = frame.IsStillPhoto
                ? photoBaseName
                : frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始帧"
                    : $"{photoBaseName}_帧{frame.FrameIndex + 1}";

            // 3. 弹出多格式另存为窗口
            var targetFile = await FilePickerService.PickSaveFileForExportMultiFormatAsync(suggestedName);
            if (targetFile == null) return;

            // 4. 显示进度
            string targetPath = targetFile.Path;
            BeginExportProgress(ResourceService.GetString("EditPage_ExportCurrentFrameInProgress"));

            try
            {
                // 5. 根据用户选择的格式执行导出
                string targetExt = Path.GetExtension(targetPath);
                bool needsConversion = ImageFormatService.NeedsConversion(sourcePath, targetExt);

                if (needsConversion)
                {
                    await ImageFormatService.ConvertImageAsync(sourcePath, targetPath, quality: 80);
                }
                else
                {
                    var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                    await sourceFile.CopyAndReplaceAsync(targetFile);
                }

                LogService.FileOp(
                    $"ExportCurrentFrame: {Path.GetFileName(sourcePath)} -> {targetPath}",
                    LogLevel.Info);

                // 6. 修改日期为当前时间
                try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                // 7. 完成状态
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_ExportCurrentFrameComplete"),
                    Path.GetDirectoryName(targetPath));
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"ExportCurrentFrame failed: {ex.Message}", LogLevel.Error, ex);
                FailExportProgress(
                    ResourceService.GetString("EditPage_ExportCurrentFrameFailed"),
                    ex.Message, Path.GetDirectoryName(targetPath));
            }
            finally
            {
                FinalizeExportProgress();
            }
        }

        /// <summary>
        /// 不完整实况（仅照片，无时间轴）：直接将照片文件作为单帧导出。
        /// </summary>
        private async Task ExportPhotoAsSingleFrame()
        {
            var photoPath = SelectedFilePath;
            if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath)) return;

            var photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
            var targetFile = await FilePickerService.PickSaveFileForExportMultiFormatAsync(photoBaseName);
            if (targetFile == null) return;

            string targetPath = targetFile.Path;
            BeginExportProgress(ResourceService.GetString("EditPage_ExportCurrentFrameInProgress"));

            try
            {
                string targetExt = Path.GetExtension(targetPath);
                bool needsConversion = ImageFormatService.NeedsConversion(photoPath, targetExt);
                if (needsConversion)
                    await ImageFormatService.ConvertImageAsync(photoPath, targetPath, quality: 80);
                else
                {
                    var sourceFile = await StorageFile.GetFileFromPathAsync(photoPath);
                    await sourceFile.CopyAndReplaceAsync(targetFile);
                }
                try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }
                LogService.FileOp($"ExportPhotoAsSingleFrame: {Path.GetFileName(photoPath)} -> {targetPath}", LogLevel.Info);
                CompleteExportProgress(
                    ResourceService.GetString("EditPage_ExportCurrentFrameComplete"),
                    Path.GetDirectoryName(targetPath));
            }
            catch (Exception ex)
            {
                LogService.FileOp($"ExportPhotoAsSingleFrame failed: {ex.Message}", LogLevel.Error, ex);
                FailExportProgress(
                    ResourceService.GetString("EditPage_ExportCurrentFrameFailed"),
                    ex.Message, Path.GetDirectoryName(targetPath));
            }
            finally
            {
                FinalizeExportProgress();
            }
        }

        /// <summary>
        /// 将原图的 EXIF 信息（相机、日期、GPS 等）复制到导出文件，
        /// 但排除各家实况照片私有协议标签（GCamera、OpCamera、Container 等），
        /// 确保导出的是干净的静态图片。
        /// </summary>
        private static async Task CopyExifForExportAsync(string sourcePath, string targetPath)
        {
            try
            {
                // 先复制原图全部标签到导出文件
                // 排除以下可能造成问题的标签：
                //   Orientation      — 帧像素已正确，复制后导致查看器二次旋转
                //   ExifImageWidth/Height — HEIC 原始尺寸，视频帧尺寸不同，复制后可能干扰查看
                //   ThumbnailImage   — HEIC 内嵌缩略图格式与 JPEG 不兼容
                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-TagsFromFile", sourcePath,
                    "-all:all",
                    "-Orientation=",
                    "-ExifImageWidth=",
                    "-ExifImageHeight=",
                    "-ThumbnailImage=",
                    "-overwrite_original",
                    "-quiet",
                    targetPath);

                // 删除实况照片私有协议标签
                await LivePhotoRepairService.RunExifToolAsync(CancellationToken.None,
                    "-xmp-GCamera:all=",
                    "-xmp-OpCamera:all=",
                    "-xmp-Container:all=",
                    "-ContentIdentifier=",
                    "-overwrite_original",
                    "-quiet",
                    targetPath);

                LogService.FileOp(
                    $"CopyExifForExport: {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)}",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"CopyExifForExport failed: {ex.Message}", LogLevel.Warning);
            }
        }

        /// <summary>
        /// 通知 Windows 资源管理器有文件已创建/修改，强制刷新显示。
        /// 解决 File.Copy 后 Explorer 不自动刷新的问题（如 Apple 双文件的配对 MOV 不显示）。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(
            int wEventId, int uFlags, string dwItem1, IntPtr dwItem2);

        private const int SHCNE_CREATE = 0x2;
        private const int SHCNF_PATHW = 0x0005;
        private const int SHCNF_FLUSH = 0x1000;

        /// <summary>
        /// 通知壳层指定路径的文件已创建，强制 Explorer 刷新。
        /// </summary>
        private static void NotifyShellFileCreated(string filePath)
        {
            try
            {
                SHChangeNotify(SHCNE_CREATE, SHCNF_PATHW | SHCNF_FLUSH, filePath, IntPtr.Zero);
            }
            catch
            {
                // 壳层通知失败不影响功能，静默忽略
            }
        }

        /// <summary>
        /// 导出所有帧：先弹出选项对话框 → 文件夹选择器 → 多线程并行导出所有帧，
        /// 更新进度 UI，导出完成后显示汇总结果。
        /// </summary>
        [RelayCommand]
        private async Task ExportAllFrames()
        {
            // 1. 防重入守卫（完成态不阻塞新操作）
            if (IsExporting && !IsShowingSaveComplete)
            {
                LogService.FileOp("ExportAllFrames: already exporting", LogLevel.Warning);
                return;
            }

            // 2. 守卫条件：无帧或无文件选中
            if (TimelineFrames.Count == 0)
            {
                LogService.FileOp("ExportAllFrames: no frames to export", LogLevel.Warning);
                return;
            }

            var photoPath = SelectedFilePath;
            if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
            {
                LogService.FileOp("ExportAllFrames: no file selected or file not found", LogLevel.Warning);
                return;
            }

            // 3. 默认导出路径 = 当前照片所在目录
            var photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
            var defaultDir = Path.GetDirectoryName(photoPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            // 4. 弹出选项对话框（浏览换路径在对话框内部处理，不关闭弹窗）
            var options = await ShowExportOptionsDialogAsync(photoBaseName, defaultDir);
            if (options == null)
            {
                LogService.FileOp("ExportAllFrames cancelled by user (options dialog)", LogLevel.Info);
                return;
            }

            // 5. 创建不冲突的导出子目录
            var exportDir = GetUniqueFolderPath(options.ExportPath, options.FolderName);
            Directory.CreateDirectory(exportDir);

            LogService.FileOp(
                $"ExportAllFrames started: {TimelineFrames.Count} frames -> '{exportDir}'",
                LogLevel.Info);

            // 6. 初始化导出状态
            _exportCts?.Cancel();
            _exportCts?.Dispose();
            _exportCts = new CancellationTokenSource();
            var token = _exportCts.Token;

            BeginExportProgress($"0/{TimelineFrames.Count}",
                ResourceService.GetString("EditPage_ExportAllFramesInProgress"));

            var semaphore = new SemaphoreSlim(8, 8);
            var tasks = new List<Task>();
            var counters = new ExportCounters();

            try
            {
                // 7. 多线程并行导出
                foreach (var frame in TimelineFrames)
                {
                    token.ThrowIfCancellationRequested();

                    await semaphore.WaitAsync(token);

                    tasks.Add(ExportOneFrameAsync(
                        frame, photoPath, photoBaseName, exportDir,
                        options.CopyExif, options.FormatExtension, options.Quality,
                        token, semaphore, TimelineFrames.Count, counters));
                }

                await Task.WhenAll(tasks);

                // 8. 汇总日志
                LogService.FileOp(
                    $"ExportAllFrames completed: {counters.Success} succeeded, {counters.Fail} failed -> '{exportDir}'",
                    counters.Fail > 0 ? LogLevel.Warning : LogLevel.Info);

                // 9. 完成（内联，替代 ContentDialog）
                if (!token.IsCancellationRequested)
                {
                    CompleteExportProgress(
                        ResourceService.GetString("EditPage_ExportAllFramesComplete"),
                        exportDir);
                }
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp("ExportAllFrames cancelled mid-operation", LogLevel.Warning);
                FailExportProgress(
                    ResourceService.GetString("EditPage_ExportAllFramesFailed"),
                    "Operation was cancelled",
                    exportDir);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"ExportAllFrames fatal error: {ex.Message}", LogLevel.Error, ex);
                FailExportProgress(
                    ResourceService.GetString("EditPage_ExportAllFramesFailed"),
                    ex.Message, exportDir);
            }
            finally
            {
                FinalizeExportProgress();
                _exportCts?.Dispose();
                _exportCts = null;
                semaphore.Dispose();
            }
        }

        /// <summary>
        /// 导出计数器（线程安全，通过 Interlocked 操作）。
        /// </summary>
        private sealed class ExportCounters
        {
            public int Completed;
            public int Success;
            public int Fail;
        }

        /// <summary>
        /// 弹出导出选项设置对话框：包含文件夹名编辑框、导出位置+浏览按钮、EXIF 勾选框。
        /// </summary>
        private async Task<ExportOptions?> ShowExportOptionsDialogAsync(
            string defaultFolderName, string currentFolderPath)
        {
            if (App.MainWindow?.Content?.XamlRoot is not XamlRoot xamlRoot)
                return null;

            // 构建内容面板
            var panel = new StackPanel { Spacing = 8 };

            // 描述文字：告诉用户会自动创建文件夹
            panel.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("EditPage_ExportDialog_Description"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });

            // 导出位置：header + 路径文本框 + 文件夹图标按钮（Grid 保证文本框填满）
            panel.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("EditPage_ExportDialog_FolderPathLabel"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0),
            });

            var pathRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };

            var folderPathBox = new TextBox
            {
                Text = currentFolderPath,
                Header = null, // 不显示重复 header
            };
            Grid.SetColumn(folderPathBox, 0);
            pathRow.Children.Add(folderPathBox);

            var browseButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                Content = new FontIcon { Glyph = "", FontSize = 14 },
            };
            ToolTipService.SetToolTip(browseButton,
                ResourceService.GetString("EditPage_ExportDialog_BrowseTip"));
            Grid.SetColumn(browseButton, 1);
            pathRow.Children.Add(browseButton);

            panel.Children.Add(pathRow);

            // 路径错误提示（默认隐藏）
            var pathErrorText = new TextBlock
            {
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 78, 78)),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 0),
            };
            panel.Children.Add(pathErrorText);

            // 文件夹名称编辑框 + 重置按钮（圆圈箭头）
            panel.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("EditPage_ExportDialog_FolderNameLabel"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 0),
            });

            var nameRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };

            var folderNameBox = new TextBox
            {
                Text = defaultFolderName,
                PlaceholderText = defaultFolderName,
            };
            Grid.SetColumn(folderNameBox, 0);
            nameRow.Children.Add(folderNameBox);

            var resetNameButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                Content = new FontIcon { Glyph = "", FontSize = 14 },
            };
            ToolTipService.SetToolTip(resetNameButton,
                ResourceService.GetString("EditPage_ExportDialog_ResetTip"));
            Grid.SetColumn(resetNameButton, 1);
            nameRow.Children.Add(resetNameButton);

            panel.Children.Add(nameRow);

            // 输出格式选择
            panel.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("EditPage_ExportDialog_FormatLabel"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 0),
            });

            var formatComboBox = new ComboBox
            {
                Items =
                {
                    new ComboBoxItem { Content = "JPEG (.jpg)", Tag = ".jpg" },
                },
                SelectedIndex = 0,
            };
            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
                formatComboBox.Items.Add(new ComboBoxItem { Content = "HEIC (.heic)", Tag = ".heic" });
            else
                formatComboBox.Items.Add(new ComboBoxItem { Content = "WebP (.webp)", Tag = ".webp" });
            panel.Children.Add(formatComboBox);

            // EXIF 勾选框（默认勾选，JPEG 格式时生效）
            var copyExifCheckBox = new CheckBox
            {
                Content = ResourceService.GetString("EditPage_ExportDialog_CopyExifLabel"),
                IsChecked = true,
            };
            panel.Children.Add(copyExifCheckBox);

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("EditPage_ExportDialog_Title"),
                Content = panel,
                PrimaryButtonText = ResourceService.GetString("EditPage_ExportDialog_ExportBtn"),
                CloseButtonText = ResourceService.GetString("Msg_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 440.0;
            dialog.Resources["ContentDialogMinWidth"] = 440.0;

            // 重置按钮：恢复为默认文件夹名称
            var capturedDefaultName = defaultFolderName;
            resetNameButton.Click += (_, _) =>
            {
                folderNameBox.Text = capturedDefaultName;
            };

            // 验证路径是否合法：必须绝对路径、不含非法字符、根驱动器存在
            bool IsPathValid(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                try
                {
                    var invalid = Path.GetInvalidPathChars();
                    if (path.IndexOfAny(invalid) >= 0) return false;
                    if (!Path.IsPathRooted(path)) return false; // 必须是绝对路径
                    var full = Path.GetFullPath(path);
                    // 如果指定了驱动器号，检查驱动器是否存在
                    if (full.Length >= 2 && full[1] == ':')
                    {
                        var drive = char.ToUpperInvariant(full[0]);
                        if (drive < 'A' || drive > 'Z') return false;
                        if (!Directory.Exists($@"{drive}:\")) return false; // 驱动器不存在
                    }
                    return true;
                }
                catch { return false; }
            }

            // 实时更新路径状态：错误文字 + 导出按钮灰态
            var errorText = pathErrorText;
            void UpdatePathState()
            {
                currentFolderPath = folderPathBox.Text.Trim();
                if (IsPathValid(currentFolderPath))
                {
                    errorText.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = true;
                }
                else
                {
                    errorText.Text = ResourceService.GetString("EditPage_ExportDialog_PathInvalidError");
                    errorText.Visibility = Visibility.Visible;
                    dialog.IsPrimaryButtonEnabled = false;
                }
            }

            // 初始检查 + 输入时实时检查
            folderPathBox.Loaded += (_, _) => UpdatePathState();
            folderPathBox.TextChanged += (_, _) => UpdatePathState();

            // 浏览按钮：不关闭弹窗，直接打开文件夹选择器
            browseButton.Click += async (_, _) =>
            {
                try
                {
                    var folder = await FilePickerService.PickFolderAsync();
                    if (folder != null)
                    {
                        currentFolderPath = folder.Path;
                        folderPathBox.Text = currentFolderPath;
                        UpdatePathState();
                    }
                }
                catch (Exception ex)
                {
                    LogService.FileOp($"Browse folder in dialog failed: {ex.Message}", LogLevel.Warning);
                }
            };

            // 导出按钮点击时二次验证（兜底：防止按钮状态未正确更新）
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    var testPath = folderPathBox.Text.Trim();
                    if (!IsPathValid(testPath))
                    {
                        errorText.Text = ResourceService.GetString("EditPage_ExportDialog_PathInvalidError");
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }
                    currentFolderPath = testPath;
                    errorText.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    errorText.Text = ResourceService.GetString("EditPage_ExportDialog_PathInvalidError");
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                string folderName = folderNameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(folderName))
                    folderName = defaultFolderName;
                bool copyExif = copyExifCheckBox.IsChecked ?? true;
                string fmtExt = ((ComboBoxItem)formatComboBox.SelectedItem).Tag as string ?? ".jpg";
                return new ExportOptions(folderName, copyExif, currentFolderPath, fmtExt, 80);
            }

            return null;
        }

        /// <summary>
        /// 在信号量约束下导出单帧到目标目录，并更新进度计数器。
        /// 可被多个任务并行调用，线程安全。
        /// </summary>
        /// <summary>
        /// 解析 ⭐ 静止封面帧的干净图片源。
        /// 单文件实况（图+视频拼在同一个容器里）的 photoPath 是整个容器：
        ///   - HEIC 容器 → Magick 解码到内嵌视频时抛 "Unexpected end of file"（导出 0 字节）
        ///   - JPEG 容器 → 直接复制会把视频/尾标一起带出来
        /// 这里把容器开头的图片部分切片成干净临时文件返回。
        /// 双文件实况（Apple/vivo ≤X200 图、视频分离）photoPath 本身就是干净图片，原样返回。
        /// </summary>
        private static async Task<string> ResolveStillPhotoSourceAsync(string photoPath, CancellationToken token)
        {
            // 1. HEIC + mpvd box（Google V2 / Samsung / vivo X300 HEIC）：图片 = [0, mpvd box size 字段前)
            // 必须先于 HUAWEI 判断——V2 HEIC 的 mpvd 内嵌 MP4 也有 moov/ftyp，
            // GetHuaweiEmbeddedVideoRange 会误报一个视频区间（无 LIVE_ 尾标但能解析出 ftyp/moov）。
            if (HeicConverterService.IsHeicFile(photoPath))
            {
                long mpvdLen = LivePhotoMergeService.GetMpvdVideoLength(photoPath);
                if (mpvdLen > 0)
                {
                    long mpvdStart = LivePhotoMergeService.GetMpvdVideoStart(photoPath); // "mpvd" fourcc 后 = 视频起点
                    long imageEnd = mpvdStart - 8; // 图片结束于 box size 字段之前
                    if (imageEnd > 0)
                        return await SliceContainerPrefixAsync(photoPath, imageEnd, token);
                }
            }

            // 2. 华为/荣耀（HEIC 或 JPEG + 内嵌 MP4 + 60B LIVE_ 尾标）：moov 定位视频起点，图片 = [0, videoStart)
            var hwRange = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(photoPath);
            if (hwRange != null && hwRange.Value.videoStart > 0)
                return await SliceContainerPrefixAsync(photoPath, hwRange.Value.videoStart, token);

            // 3. 单文件 JPEG（V2/OPPO/vivo X300）：视频在文件末尾，图片 = [0, fileSize - videoLen)
            long fileSize = new FileInfo(photoPath).Length;
            long videoLen = 0;
            try
            {
                videoLen = LivePhotoSplitService.GetAppendedVideoLength(
                    LivePhotoSplitService.ReadMetadataTextSync(photoPath));
            }
            catch { videoLen = 0; }
            if (videoLen > 0 && videoLen < fileSize)
                return await SliceContainerPrefixAsync(photoPath, fileSize - videoLen, token);

            // 双文件实况等：photoPath 即干净图片
            return photoPath;
        }

        /// <summary>把文件开头 [0, length) 字节切片成临时文件，返回临时路径。</summary>
        private static async Task<string> SliceContainerPrefixAsync(string sourcePath, long length, CancellationToken token)
        {
            string ext = Path.GetExtension(sourcePath);
            string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_still_{Guid.NewGuid():N}{ext}");
            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long remain = Math.Min(length, src.Length);
                while (remain > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                    if (r == 0) break;
                    dst.Write(buf, 0, r);
                    remain -= r;
                }
            }
            return tempPath;
        }

        private async Task ExportOneFrameAsync(
            TimelineFrame frame, string photoPath, string photoBaseName,
            string exportDir, bool copyExif, string formatExtension, int quality,
            CancellationToken token, SemaphoreSlim semaphore, int totalFrames,
            ExportCounters counters)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                // 1. 确定源文件路径
                string sourcePath;
                if (frame.IsStillPhoto || frame.IsOriginalPhoto)
                {
                    if (frame.IsOriginalPhoto)
                    {
                        if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                        {
                            byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(photoPath);
                            if (origBytes == null || origBytes.Length == 0)
                            {
                                Interlocked.Increment(ref counters.Fail);
                                LogService.FileOp(
                                    "ExportAllFrames: 🖼 original photo bytes unavailable",
                                    LogLevel.Warning);
                                return;
                            }
                            string tempPath = Path.Combine(Path.GetTempPath(),
                                $"lpb_orig_export_{Guid.NewGuid():N}.jpg");
                            await File.WriteAllBytesAsync(tempPath, origBytes, token);
                            sourcePath = tempPath;
                        }
                        else
                        {
                            sourcePath = frame.FullFramePath;
                        }
                    }
                    else
                    {
                        // ⭐ 封面静止帧：单文件容器（HUAWEI/V2/OPPO 等图+视频拼接）的
                        // photoPath 是整个容器——HEIC 容器 Magick 解码会报错（导出 0 字节）、
                        // JPEG 容器直接复制会把视频带出来。提取容器开头的干净图片。
                        sourcePath = await ResolveStillPhotoSourceAsync(photoPath, token);
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    {
                        Interlocked.Increment(ref counters.Fail);
                        LogService.FileOp(
                            $"ExportAllFrames: frame path missing — isStillPhoto=false, path='{frame.FullFramePath ?? "null"}'",
                            LogLevel.Warning);
                        return;
                    }
                    sourcePath = frame.FullFramePath;
                }

                // 2. 生成输出文件名（使用选择的格式扩展名）
                var fileName = frame.IsStillPhoto
                    ? $"{photoBaseName}{formatExtension}"
                    : frame.IsOriginalPhoto
                        ? $"{photoBaseName}_原始帧{formatExtension}"
                        : $"{photoBaseName}_帧{frame.FrameIndex + 1}{formatExtension}";

                // 3. 原子性预留不冲突的文件路径
                var targetPath = PathHelper.GetUniqueFilePath(exportDir, fileName);

                // 4. 按需转换或直接复制
                if (ImageFormatService.NeedsConversion(sourcePath, formatExtension))
                {
                    await ImageFormatService.ConvertImageAsync(sourcePath, targetPath, quality, token);
                }
                else
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                }

                // 5. 复制 EXIF（从原照片复制到导出文件，仅 JPEG 格式）
                if (copyExif && File.Exists(photoPath))
                {
                    await CopyExifForExportAsync(photoPath, targetPath);
                }

                // 6. 修改日期
                try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                Interlocked.Increment(ref counters.Success);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref counters.Fail);
                LogService.FileOp(
                    $"ExportAllFrames: frame {(frame.IsOriginalPhoto ? "🖼" : frame.IsStillPhoto ? "⭐" : $"#{frame.FrameIndex + 1}")} FAILED: {ex.Message}",
                    LogLevel.Error, ex);
            }
            finally
            {
                int done = Interlocked.Increment(ref counters.Completed);
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    // 完成态不覆盖——CompleteExportProgress 已经写了完成文字
                    if (!IsShowingSaveComplete)
                    {
                        ExportProgressText = $"{done}/{totalFrames}";
                        ExportProgressPercent = (double)done / totalFrames * 100.0;
                    }
                });
                semaphore.Release();
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  视频导出
        // ══════════════════════════════════════════════════════════════

        /// <summary>导出为视频 — 打开保存对话框，在文件类型中选择 MP4 或 MOV</summary>
        [RelayCommand]
        private async Task ExportVideo()
        {
            if (IsExporting && !IsShowingSaveComplete) return;

            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase));
            if (item == null || !item.HasConfirmedProtocol)
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_GuardNotLivePhoto"));
                return;
            }

            bool isRebuilt = ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt;
            string? videoPath = isRebuilt
                ? await ProcessingPipelineRouter.RunRebuiltAsync(
                    "edit.video-export",
                    () => ResolveVideoPathForRebuiltExportAsync(item))
                : await ResolveVideoPathForExportAsync(item);
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_GuardNoVideoSource"));
                return;
            }

            // 保存对话框 — 两种格式在文件类型下拉中选
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(SelectedFilePath ?? "video"),
            };
            savePicker.FileTypeChoices.Add("MP4 (H.264 + AAC)", new List<string> { ".mp4" });
            savePicker.FileTypeChoices.Add("MOV (H.265 QuickTime + AAC)", new List<string> { ".mov" });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
            var targetFile = await savePicker.PickSaveFileAsync();
            if (targetFile == null) { CleanupExportTempVideo(); return; }

            BeginExportProgress(ResourceService.GetString("EditPage_ExportVideoInProgress"));

            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                bool isMp4 = Path.GetExtension(targetFile.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
                bool success;
                string? errorMessage;
                if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
                {
                    (success, errorMessage) = await ProcessingPipelineRouter.RunRebuiltAsync(
                        "edit.video-export",
                        () => ExportVideoWithNativeAsync(videoPath, targetFile.Path, cts.Token));
                }
                else
                {
                    VideoTranscodeService.TranscodeResult legacyResult = isMp4
                        ? await VideoTranscodeService.TranscodeToMp4Async(videoPath, targetFile.Path, cts.Token)
                        : await VideoTranscodeService.TranscodeToMovAsync(videoPath, targetFile.Path, cts.Token);
                    success = legacyResult.Success;
                    errorMessage = legacyResult.ErrorMessage;
                }

                if (success)
                {
                    CompleteExportProgress(
                        ResourceService.GetString("EditPage_ExportVideoComplete"),
                        Path.GetDirectoryName(targetFile.Path));
                }
                else
                {
                    FailExportProgress(
                        ResourceService.GetString("EditPage_ExportVideoFailed"),
                        errorMessage ?? ResourceService.GetString("EditPage_UnknownError"),
                        Path.GetDirectoryName(targetFile.Path));
                }
            }
            catch (Exception ex)
            {
                FailExportProgress(
                    ResourceService.GetString("EditPage_ExportVideoFailed"),
                    ex.Message, Path.GetDirectoryName(targetFile.Path));
            }
            finally
            {
                CleanupExportTempVideo();
                FinalizeExportProgress();
            }
        }

        private static async Task<(bool Success, string? ErrorMessage)> ExportVideoWithNativeAsync(
            string inputPath, string outputPath, CancellationToken token)
        {
            bool isMp4 = Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
            VideoContainer targetContainer = isMp4 ? VideoContainer.Mp4 : VideoContainer.Mov;
            VideoCodec targetCodec = isMp4 ? VideoCodec.H264 : VideoCodec.Hevc;

            var converter = new VideoConverter();
            VideoFacts facts = await converter.ProbeAsync(inputPath, token).ConfigureAwait(false);
            var result = await converter.ConvertAsync(new VideoConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = inputPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = facts.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                    VideoContainer = facts.Container,
                    VideoCodec = facts.Codec,
                    ByteLength = new FileInfo(inputPath).Length
                },
                TargetContainer = targetContainer,
                TargetCodec = targetCodec,
                TargetDirectory = Path.GetDirectoryName(outputPath)!,
                Crf = 23
            }, token).ConfigureAwait(false);

            if (!result.Success || result.OutputArtifact == null)
                return (false, result.ErrorMessage ?? "Native video conversion failed.");

            File.Copy(result.OutputArtifact.Path, outputPath, overwrite: true);
            return (true, null);
        }

        // ══════════════════════════════════════════════════════════════
        //  GIF 导出
        // ══════════════════════════════════════════════════════════════

        private sealed record GifOptions(
            int Fps, int Width, int Height, bool UseOriginalSize, int LoopCount, string OutputPath);

        [RelayCommand]
        private async Task ExportGif()
        {
            if (IsExporting && !IsShowingSaveComplete) return;

            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_RebuiltGifUnsupported"));
                return;
            }

            // 1. 验证是实况照片
            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase));
            if (item == null || !item.HasConfirmedProtocol)
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_GuardNotLivePhoto"));
                return;
            }

            // 2. 获取视频源（双文件/内嵌视频/HEIC）
            string? videoPath = await ResolveVideoPathForExportAsync(item);
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_GuardNoVideoSource"));
                return;
            }

            // 3. GIF 参数弹窗（UI 不变）
            var gifOptions = await ShowGifOptionsDialogAsync();
            if (gifOptions == null) { CleanupExportTempVideo(); return; }

            string targetPath = gifOptions.OutputPath;
            BeginExportProgress(ResourceService.GetString("EditPage_ExportGifInProgress"));

            try
            {
                int w = gifOptions.UseOriginalSize ? 0 : gifOptions.Width;
                int h = gifOptions.UseOriginalSize ? 0 : gifOptions.Height;

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var result = await VideoTranscodeService.TranscodeToGifAsync(
                    videoPath, targetPath,
                    gifOptions.Fps, w, h, gifOptions.LoopCount,
                    cts.Token);

                if (result.Success)
                {
                    CompleteExportProgress(
                        ResourceService.GetString("EditPage_ExportGifComplete"),
                        Path.GetDirectoryName(targetPath));
                }
                else
                {
                    FailExportProgress(
                        ResourceService.GetString("EditPage_ExportGifFailed"),
                        result.ErrorMessage ?? ResourceService.GetString("EditPage_UnknownError"),
                        Path.GetDirectoryName(targetPath));
                }
            }
            catch (Exception ex)
            {
                FailExportProgress(
                    ResourceService.GetString("EditPage_ExportGifFailed"),
                    ex.Message, Path.GetDirectoryName(targetPath));
            }
            finally
            {
                CleanupExportTempVideo();
                FinalizeExportProgress();
            }
        }

        /// <summary>GIF 参数设置弹窗（尺寸、帧率、循环、输出路径）</summary>
        private async Task<GifOptions?> ShowGifOptionsDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot is not XamlRoot xamlRoot)
                return null;

            var panel = new StackPanel { Spacing = 8 };

            // 获取原图尺寸
            int origW = 480, origH = 360;
            var selectedFrame = SelectedTimelineFrame;
            if (selectedFrame != null)
            {
                string? src = selectedFrame.IsStillPhoto ? SelectedFilePath : selectedFrame.FullFramePath;
                if (src != null && File.Exists(src))
                {
                    try
                    {
                        using var probe = new ImageMagick.MagickImage(src);
                        probe.AutoOrient();
                        origW = (int)probe.Width;
                        origH = (int)probe.Height;
                    }
                    catch { }
                }
            }
            double ratio = origH / (double)origW;

            // ── 尺寸 ──
            panel.Children.Add(new TextBlock { Text = ResourceService.GetString("EditPage_GifDialog_SizeLabel"), FontSize = 13, FontWeight = FontWeights.SemiBold });

            var widthBox = new TextBox
            {
                Text = "480",
                Width = 100,
                InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
            };
            var heightBox = new TextBox
            {
                Text = ((int)(480 * ratio)).ToString(),
                Width = 100,
                InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } },
            };

            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sizeRow.Children.Add(new TextBlock { Text = ResourceService.GetString("EditPage_GifDialog_WidthLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            sizeRow.Children.Add(widthBox);
            sizeRow.Children.Add(new TextBlock { Text = ResourceService.GetString("EditPage_GifDialog_HeightLabel"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
            sizeRow.Children.Add(heightBox);
            panel.Children.Add(sizeRow);

            var lockCheck = new CheckBox
            {
                Content = ResourceService.GetString("EditPage_GifDialog_LockAspectRatio"),
                IsChecked = true,
                IsEnabled = false,
            };

            bool _updating = false;
            CancellationTokenSource? _debounceCts = null;
            var _dispatcher = DispatcherQueue.GetForCurrentThread();

            // ── 焦点跟踪 + 失焦时空框自动按比例回填 ──
            TextBox? _focusedBox = null;
            widthBox.GotFocus += (_, _) => _focusedBox = widthBox;
            heightBox.GotFocus += (_, _) => _focusedBox = heightBox;
            widthBox.LostFocus += (_, _) =>
            {
                if (ReferenceEquals(_focusedBox, widthBox)) _focusedBox = null;
                if (string.IsNullOrEmpty(widthBox.Text) && int.TryParse(heightBox.Text, out int h) && h > 0)
                    widthBox.Text = Math.Clamp((int)Math.Round(h / ratio), 120, 3840).ToString();
            };
            heightBox.LostFocus += (_, _) =>
            {
                if (ReferenceEquals(_focusedBox, heightBox)) _focusedBox = null;
                if (string.IsNullOrEmpty(heightBox.Text) && int.TryParse(widthBox.Text, out int w) && w > 0)
                    heightBox.Text = Math.Clamp((int)Math.Round(w * ratio), 120, 3840).ToString();
            };

            // ── 纵横比自动纠正（1 秒防抖，单向：只改对方，不改自己）──
            async void ScheduleAspectCorrection(TextBox editedBox, TextBox targetBox, bool isWidth)
            {
                _debounceCts?.Cancel();
                _debounceCts?.Dispose();
                _debounceCts = new CancellationTokenSource();
                var token = _debounceCts.Token;
                try
                {
                    await Task.Delay(1000, token);
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (_updating) return;
                        if (!double.TryParse(editedBox.Text, out double newValue) || newValue <= 0) return;

                        int srcVal = (int)newValue;
                        int tgtVal = isWidth
                            ? (int)Math.Round(newValue * ratio)
                            : (int)Math.Round(newValue / ratio);

                        // 目标越界 → 双方等比调整，保证比例不破
                        if (tgtVal < 120)
                        {
                            tgtVal = 120;
                            srcVal = isWidth
                                ? (int)Math.Round(120.0 / ratio)
                                : (int)Math.Round(120.0 * ratio);
                        }
                        else if (tgtVal > 3840)
                        {
                            tgtVal = 3840;
                            srcVal = isWidth
                                ? (int)Math.Round(3840.0 / ratio)
                                : (int)Math.Round(3840.0 * ratio);
                        }

                        srcVal = Math.Clamp(srcVal, 120, 3840);
                        // 用最终钳好的 srcVal 重新算 tgtVal，确保横竖版比例都对
                        tgtVal = isWidth
                            ? Math.Clamp((int)Math.Round(srcVal * ratio), 120, 3840)
                            : Math.Clamp((int)Math.Round(srcVal / ratio), 120, 3840);

                        _updating = true;
                        if (srcVal.ToString() != editedBox.Text)
                            editedBox.Text = srcVal.ToString();
                        targetBox.Text = tgtVal.ToString();
                        _updating = false;
                    });
                }
                catch (OperationCanceledException) { }
            }

            // ── 输入过滤（只允许数字）──
            bool _filtering = false;
            void FilterDigits(TextBox box)
            {
                if (_filtering) return;
                var digits = new string(box.Text.Where(char.IsDigit).ToArray());
                if (digits == box.Text) return;
                _filtering = true;
                box.Text = digits;
                box.SelectionStart = digits.Length;
                _filtering = false;
            }

            // TextChanged：只有焦点所在的框才启动纠正（避免程序设值触发反弹）
            widthBox.TextChanged += (_, _) =>
            {
                if (_updating) return;
                FilterDigits(widthBox);
                if (ReferenceEquals(_focusedBox, widthBox))
                    ScheduleAspectCorrection(widthBox, heightBox, true);
            };
            heightBox.TextChanged += (_, _) =>
            {
                if (_updating) return;
                FilterDigits(heightBox);
                if (ReferenceEquals(_focusedBox, heightBox))
                    ScheduleAspectCorrection(heightBox, widthBox, false);
            };

            // 纵横比提示 + 原始尺寸 → 同一排
            var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };
            checkRow.Children.Add(lockCheck);
            var originalSizeCheck = new CheckBox { Content = ResourceService.GetString("EditPage_GifDialog_UseOriginalSize"), IsChecked = false };
            checkRow.Children.Add(originalSizeCheck);
            panel.Children.Add(checkRow);
            string _savedWidth = "480", _savedHeight = ((int)(480 * ratio)).ToString();
            originalSizeCheck.Checked += (_, _) =>
            {
                _debounceCts?.Cancel();
                _savedWidth = widthBox.Text;
                _savedHeight = heightBox.Text;
                widthBox.Text = origW.ToString();
                heightBox.Text = origH.ToString();
                widthBox.IsEnabled = false;
                heightBox.IsEnabled = false;
            };
            originalSizeCheck.Unchecked += (_, _) =>
            {
                widthBox.Text = _savedWidth;
                heightBox.Text = _savedHeight;
                widthBox.IsEnabled = true;
                heightBox.IsEnabled = true;
            };

            // ── 帧率 ──
            panel.Children.Add(new TextBlock { Text = ResourceService.GetString("EditPage_GifDialog_FpsLabel"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
            var fpsBox = new NumberBox { Value = 10, Minimum = 1, Maximum = 30 };
            panel.Children.Add(fpsBox);

            // ── 循环 ──
            panel.Children.Add(new TextBlock { Text = ResourceService.GetString("EditPage_GifDialog_LoopLabel"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
            var loopBox = new NumberBox { Value = 0, Minimum = 0, Maximum = 999 };
            panel.Children.Add(loopBox);

            // ── 输出路径（仿 ExportAllFrames 对话框：TextBox + 文件夹图标按钮）──
            panel.Children.Add(new TextBlock { Text = ResourceService.GetString("EditPage_GifDialog_OutputPathLabel"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });

            var defaultDir = Path.GetDirectoryName(SelectedFilePath)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var currentFolder = defaultDir;
            var defaultFileName = "animated.gif";

            var pathRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                }
            };

            var pathBox = new TextBox { Text = Path.Combine(currentFolder, defaultFileName) };
            Grid.SetColumn(pathBox, 0);
            pathRow.Children.Add(pathBox);

            var browseBtn = new Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                Content = new FontIcon { Glyph = "", FontSize = 14 },
            };
            ToolTipService.SetToolTip(browseBtn, ResourceService.GetString("EditPage_GifDialog_BrowseTooltip"));
            browseBtn.Click += async (_, _) =>
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null)
                {
                    currentFolder = folder.Path;
                    string name = Path.GetFileName(pathBox.Text);
                    if (string.IsNullOrWhiteSpace(name)) name = defaultFileName;
                    if (!name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        name += ".gif";
                    pathBox.Text = Path.Combine(currentFolder, name);
                }
            };
            Grid.SetColumn(browseBtn, 1);
            pathRow.Children.Add(browseBtn);
            panel.Children.Add(pathRow);

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("EditPage_GifDialog_Title"),
                Content = panel,
                PrimaryButtonText = ResourceService.GetString("EditPage_GifDialog_PrimaryButton"),
                CloseButtonText = ResourceService.GetString("Msg_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 460.0;
            dialog.Resources["ContentDialogMinWidth"] = 460.0;

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            int.TryParse(widthBox.Text, out int w); if (w < 120) w = 120;
            int.TryParse(heightBox.Text, out int h); if (h < 120) h = 120;
            return new GifOptions(
                (int)fpsBox.Value,
                w,
                h,
                originalSizeCheck.IsChecked == true,
                (int)loopBox.Value,
                pathBox.Text);
        }


        /// <summary>解析实况照片的视频源路径</summary>
        private async Task<string?> ResolveVideoPathForExportAsync(EditFileItem item)
        {
            // 双文件实况照片：直接用配对视频
            if (!string.IsNullOrEmpty(item.PairedVideoPath) && File.Exists(item.PairedVideoPath))
                return item.PairedVideoPath;

            // 不完整实况（仅视频）：文件本身即为视频源
            if (item.HasConfirmedProtocol && item.LivePhotoType == LivePhotoType.DualFile
                && SupportedVideoExtensions.Contains(Path.GetExtension(item.FilePath))
                && File.Exists(item.FilePath))
                return item.FilePath;

            // 单文件 HEIC 实况照片（Google V2 / Samsung / VIVO X300 HEIC）：视频在 mpvd box 内。
            // 必须先于通用 AppendedVideoLength 分支——部分发现路径可能把这类文件的
            // AppendedVideoLength 误设为视频长度（V2 HEIC 的 mpvd 内也有 moov/ftyp，
            // GetHuaweiEmbeddedVideoRange 会误报），而通用分支假定"视频在文件末尾"，
            // 对 mpvd 布局会提取到错误位置。这里从 mpvd box 精确提取真实视频
            //（直接喂 HEIC 给 ffmpeg 时，-map 0:V:0 只会选中静止图像瓦片 1 帧）。
            if (HeicConverterService.IsHeicFile(item.FilePath) && File.Exists(item.FilePath))
            {
                long mpvdLen = LivePhotoMergeService.GetMpvdVideoLength(item.FilePath);
                if (mpvdLen > 0)
                {
                    long mpvdStart = LivePhotoMergeService.GetMpvdVideoStart(item.FilePath);
                    var tempPath = Path.Combine(Path.GetTempPath(), $"lpb_export_vid_{Guid.NewGuid():N}.mp4");
                    _exportTempVideoPath = tempPath;
                    await Task.Run(() =>
                    {
                        using var src = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        src.Seek(mpvdStart, SeekOrigin.Begin);
                        using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                        var buf = new byte[81920];
                        long remain = mpvdLen;
                        while (remain > 0)
                        {
                            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (r == 0) break;
                            dst.Write(buf, 0, r);
                            remain -= r;
                        }
                    });
                    return tempPath;
                }
                // 无 mpvd box → 继续按华为/通用分支判断（HUAWEI HEIC 无 mpvd）
            }

            // 单文件华为/荣耀 Moving Photo（HEIC 或 JPEG + 嵌入 MP4 + 60B LIVE_ 尾标）：
            // 视频不延伸到文件末尾（后面还有 60 字节尾标），若走下面通用分支的
            // offset = fileSize - AppendedVideoLength，会多偏 60 字节、提取出的 MP4 损坏
            // （ffprobe 报 moov atom not found，导出视频/GIF 失败）。
            // 必须用 moov box 精确定位视频区间（与 SaveHuaweiAsync 一致）。
            if (item.DetectedProtocol == LivePhotoProtocolType.Huawei
                && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                var hwRange = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(item.FilePath);
                if (hwRange != null)
                {
                    var (videoStart, _, videoLength) = hwRange.Value;
                    var tempPath = Path.Combine(Path.GetTempPath(), $"lpb_export_vid_{Guid.NewGuid():N}.mp4");
                    _exportTempVideoPath = tempPath;
                    await Task.Run(() =>
                    {
                        using var src = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        src.Seek(videoStart, SeekOrigin.Begin);
                        using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                        var buf = new byte[81920];
                        long remain = videoLength;
                        while (remain > 0)
                        {
                            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (r == 0) break;
                            dst.Write(buf, 0, r);
                            remain -= r;
                        }
                    });
                    return tempPath;
                }
                // 定位失败：继续走通用分支兜底
            }

            // 单文件 JPEG（MicroVideo/MotionPhoto）：从末尾提取嵌入视频
            if (item.AppendedVideoLength > 0 && File.Exists(item.FilePath))
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"lpb_export_vid_{Guid.NewGuid():N}.mp4");
                _exportTempVideoPath = tempPath;
                var fileSize = new FileInfo(item.FilePath).Length;
                long offset = fileSize - item.AppendedVideoLength;
                await Task.Run(() =>
                {
                    using var src = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    src.Seek(offset, SeekOrigin.Begin);
                    using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                    src.CopyTo(dst);
                });
                return tempPath;
            }

            // HEIC 但无 mpvd box（非常见，如精简过的 HEIC 实况）→ 回退为原文件路径（FFmpeg 直读）
            if (HeicConverterService.IsHeicFile(item.FilePath) && File.Exists(item.FilePath))
                return item.FilePath;

            return null;
        }

        /// <summary>
        /// Resolves the video for Rebuilt export through the Native-backed
        /// Inspect -> Extract -> Clean pipeline. It deliberately does not use
        /// the Legacy protocol parsers or FFmpeg extraction helpers.
        /// </summary>
        private async Task<string?> ResolveVideoPathForRebuiltExportAsync(
            EditFileItem item,
            CancellationToken cancellationToken = default)
        {
            CleanupExportMediaWorkspace();

            var workspace = new MediaWorkspace();
            try
            {
                string? secondaryPath = item.LivePhotoType == LivePhotoType.DualFile
                    && !string.IsNullOrWhiteSpace(item.PairedVideoPath)
                    ? item.PairedVideoPath
                    : null;

                NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                    item.FilePath,
                    secondaryPath,
                    workspace,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))
                {
                    workspace.Dispose();
                    return null;
                }

                _exportMediaWorkspace = workspace;
                return bundle.MotionVideo.Path;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
        }

        private string? _exportTempVideoPath;
        private IMediaWorkspace? _exportMediaWorkspace;

        private void CleanupExportTempVideo()
        {
            if (_exportTempVideoPath != null)
            {
                try { if (File.Exists(_exportTempVideoPath)) File.Delete(_exportTempVideoPath); } catch { }
                _exportTempVideoPath = null;
            }
            CleanupExportMediaWorkspace();
        }

        private void CleanupExportMediaWorkspace()
        {
            if (_exportMediaWorkspace != null)
            {
                try { _exportMediaWorkspace.Dispose(); } catch { }
                _exportMediaWorkspace = null;
            }
        }


        /// <summary>
        /// 在指定父目录下生成不冲突的文件夹路径。
        /// 如果文件夹已存在，自动追加 (2)、(3) 等后缀（与 Windows 资源管理器行为一致）。
        /// </summary>
        private static string GetUniqueFolderPath(string parentDir, string baseName)
        {
            var candidate = Path.Combine(parentDir, baseName);
            if (!Directory.Exists(candidate))
                return candidate;

            for (int i = 2; i < 999; i++)
            {
                candidate = Path.Combine(parentDir, $"{baseName} ({i})");
                if (!Directory.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(parentDir, $"{baseName} ({Guid.NewGuid():N})");
        }

        [RelayCommand(CanExecute = nameof(CanConvertProtocol))]
        private void ConvertProtocol() { }

        /// <summary>
        /// 前往封面：滚动到星标帧（IsStillPhoto=true）。
        /// 复用首次加载实况照片时的程序化选中 + 滚动吸附管线。
        /// </summary>
        [RelayCommand]
        private void GoToKeyPhoto()
        {
            var coverFrame = TimelineFrames.FirstOrDefault(f => f.IsStillPhoto);
            if (coverFrame != null)
            {
                // 复用 SelectTimelineFrameProgrammatically，
                // 确保即使已选中封面帧也会重新触发滚动
                SelectTimelineFrameProgrammatically(coverFrame);
            }
        }

        /// <summary>
        /// 前往原始封面 🖼：滚动到原始封面帧（IsOriginalPhoto=true）。
        /// 仅在 OPPO 换过封面（存在原始帧）时可见。
        /// </summary>
        [RelayCommand]
        private void GoToOriginalPhoto()
        {
            var origFrame = TimelineFrames.FirstOrDefault(f => f.IsOriginalPhoto);
            if (origFrame != null)
            {
                SelectTimelineFrameProgrammatically(origFrame);
            }
        }

        /// <summary>时间轴中是否存在原始封面帧 🖼（OPPO 换过封面时）</summary>
        public bool HasOriginalPhotoFrame => TimelineFrames.Any(f => f.IsOriginalPhoto);

        [RelayCommand] private void BrowseFolder() { }


        // ══════════════════════════════════════════════════════════════
        //  文件选中 → 加载属性
        // ══════════════════════════════════════════════════════════════

        /// <summary>属性加载取消令牌</summary>
        private CancellationTokenSource? _propLoadCts;

        /// <summary>反向地理编码速率限制：上次请求完成的时间戳</summary>
        private long _lastGeoRequestTicks;
        private const long GeoCooldownTicks = 5_000_000; // 0.5 秒，Mirror Earth 限速 ~1/s
        private CancellationTokenSource? _geoCts;
        /// <summary>View 层选中变更时调用，异步加载 EXIF 元数据填充信息面板</summary>
        public void SelectFile(string? filePath)
        {
            SelectedFilePath = filePath;

            // 取消之前的属性加载 + 时间轴帧提取 + 预览图加载 + 清理临时文件
            _propLoadCts?.Cancel();
            _propLoadCts?.Dispose();
            _propLoadCts = null;
            _geoCts?.Cancel();
            _timelineCts?.Cancel();
            _timelineDebounceCts?.Cancel();
            _earlyFfmpegCts?.Cancel();
            _earlyFfmpegTask = null;
            _exportCts?.Cancel();
            _previewLoadCts?.Cancel();
            // 后台线程清理旧临时文件（Directory.Delete 含 89 JPEG，同步调用阻塞 UI 200-500ms）
            var oldFrameDir = _frameExtractDir;
            var oldTempVid = _tempVideoPath;
            _frameExtractDir = null;
            _tempVideoPath = null;
            _ = Task.Run(() =>
            {
                try { if (oldFrameDir != null && Directory.Exists(oldFrameDir)) Directory.Delete(oldFrameDir, recursive: true); }
                catch { }
                try { if (oldTempVid != null && File.Exists(oldTempVid)) File.Delete(oldTempVid); }
                catch { }
            });

            // 递增选中代数 —— 所有旧的异步回调（exiftool 查询结果、ffmpeg 提取、
            // 大图预览）在拿到执行权后检查此值，不匹配则 bail out，避免新旧操作抢占资源。
            int myGeneration = Interlocked.Increment(ref _selectionGeneration);

            LogService.FileOp(
                $"KeyPhoto SelectFile: path='{filePath ?? "null"}', generation={myGeneration}",
                LogLevel.Info);

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ClearFileInfo();
                return;
            }

            // 判断文件类型（先记住上一个文件是否为实况，用于时间轴清除判断）
            bool wasLivePhoto = IsSelectedLivePhoto;
            var fileExt = Path.GetExtension(filePath);
            IsSelectedFileVideo = SupportedVideoExtensions.Contains(fileExt);
            IsSelectedLivePhoto = false; // 默认，下面从 item 读取

            // 先从 FileItems 找基础信息（只保留必要即时反馈，详情等异步加载一起刷新）
            // 取消旧的缩略图监听，避免前一张图异步完成后覆盖新图的属性面板缩略图
            if (_thumbnailLoadListener != null)
            {
                _thumbnailLoadListener.PropertyChanged -= ThumbnailItem_PropertyChanged;
                _thumbnailLoadListener = null;
            }

            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                // 双文件实况：校验配对视频是否仍存在，若丢失则记录日志但不降级
                // —— 属性面板会显示协议名 + "(未找到配对视频)"，LIVE 徽标保持显示
                if (item.HasConfirmedProtocol && item.LivePhotoType == LivePhotoType.DualFile
                    && !string.IsNullOrEmpty(item.PairedVideoPath)
                    && !File.Exists(item.PairedVideoPath))
                {
                    LogService.FileOp(
                        $"SelectFile: dual-file paired video missing: '{item.PairedVideoPath}' for '{item.FilePath}'",
                        LogLevel.Warning);
                }

                IsSelectedLivePhoto = item.HasConfirmedProtocol;
                PhotoFileName = EditFileItem.FormatDisplayFileName(
                    item.FileName, item.IsDualFileLivePhoto, item.VideoExtension);
                FullPhotoFileName = EditFileItem.FormatFullDisplayFileName(
                    item.FileName, item.IsDualFileLivePhoto, item.VideoExtension);
                SelectedFileThumbnail = item.Thumbnail;

                // 缩略图为懒加载（TryGetOrLoad 首次返回 null，异步回填）。
                // 若尚未就绪 → 监听 PropertyChanged，加载结束后同步到 SelectedFileThumbnail。
                if (SelectedFileThumbnail == null)
                {
                    _thumbnailLoadListener = item;
                    item.PropertyChanged += ThumbnailItem_PropertyChanged;
                }
            }

            // 大图：视频不加载，直接清空；图片走 LoadPreviewImageAsync 正常加载
            if (IsSelectedFileVideo)
            {
                SetPreviewSafe(null);
                PreviewClearRequested?.Invoke();
            }

            // 时间轴：切到非实况 或 残缺实况（无视频源）时清空
            if ((wasLivePhoto && !IsSelectedLivePhoto) || IsSelectedPairIncomplete)
            {
                TimelineFrames.Clear();
                HasTimelineFrames = false;
                IsTimelineLoading = false;
                TimelineInfo = string.Empty;
                FpsDisplayText = string.Empty;
                CurrentFramePositionText = string.Empty;
                SelectedTimelineFrame = null;
            }

            // 触发大图预览加载（异步，用令牌+代数保护）。视频跳过。
            if (!IsSelectedFileVideo)
                _ = LoadPreviewImageAsync(filePath, myGeneration);

            // 清空信息面板字段，等异步 LoadPropertiesAsync 一次填充（避免旧数据闪烁）
            PhotoInfoLine = string.Empty;
            VideoInfoLine = string.Empty;
            ProtocolLine = string.Empty;
            ExifCamera = string.Empty;
            ExifCameraDateSuffix = string.Empty;
            ExifLensParams = string.Empty;
            ExifShootingParams = string.Empty;
            ExifPlaceName = string.Empty;

            // 异步加载完整属性
            _propLoadCts = new CancellationTokenSource();
            var token = _propLoadCts.Token;
            string? videoPath = null;
            long embeddedVideoLen = 0;
            // 仅已确认协议的实况照片才触发时间轴帧提取。
            // DualFile：需要 Phase 2 exiftool 查出 ContentIdentifier 才算确认（纯文件名配对不算）。
            // SingleFileJpeg/Heic：Phase 1 XMP 标记检测通过即确认。
            if (item?.LivePhotoType == LivePhotoType.DualFile
                && item.HasConfirmedProtocol
                && !string.IsNullOrEmpty(item.PairedVideoPath))
            {
                videoPath = item.PairedVideoPath;
            }
            // 不完整实况（仅视频，缺照片）：文件本身即为视频源
            else if (item?.LivePhotoType == LivePhotoType.DualFile
                && item.HasConfirmedProtocol
                && IsSelectedFileVideo
                && File.Exists(filePath))
            {
                videoPath = filePath;
            }

            if (videoPath != null && ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Legacy)
            {
                LogService.FileOp(
                    $"Timeline[SelectFile]: DualFile, videoPath='{videoPath}', exists={File.Exists(videoPath)}",
                    LogLevel.Info);

                // 提前启动 ffmpeg：与后续 exiftool 查询并行，省 500-700ms
                _earlyFfmpegCts = new CancellationTokenSource();
                _earlyFfmpegTask = VideoFrameExtractionService.ExtractAllFramesAsync(
                    videoPath, _earlyFfmpegCts.Token);
            }
            else if (videoPath != null)
            {
                LogService.FileOp(
                    "Timeline[SelectFile]: Rebuilt Native mode skips the Legacy FFmpeg frame extractor.",
                    LogLevel.Info);
            }
            else if (item?.LivePhotoType == LivePhotoType.SingleFileJpeg && item.AppendedVideoLength > 0)
            {
                embeddedVideoLen = item.AppendedVideoLength;
                LogService.FileOp(
                    $"Timeline[SelectFile]: SingleFileJpeg, embeddedVideoLen={embeddedVideoLen}",
                    LogLevel.Info);
            }
            else
            {
                LogService.FileOp(
                    $"Timeline[SelectFile]: SKIP — type={item?.LivePhotoType}, " +
                    $"HasConfirmedProtocol={item?.HasConfirmedProtocol}, " +
                    $"PairedVideoPath='{item?.PairedVideoPath ?? "null"}', " +
                    $"embeddedVideoLen={item?.AppendedVideoLength}",
                    LogLevel.Info);
            }
            _ = LoadPropertiesAsync(filePath, videoPath, embeddedVideoLen, myGeneration, token);
        }

        /// <summary>清空信息面板</summary>
        private void ClearFileInfo()
        {
            // 取消进行中的属性/帧加载
            _propLoadCts?.Cancel();
            _timelineCts?.Cancel();

            // 取消缩略图异步加载监听
            if (_thumbnailLoadListener != null)
            {
                _thumbnailLoadListener.PropertyChanged -= ThumbnailItem_PropertyChanged;
                _thumbnailLoadListener = null;
            }

            SelectedFilePath = null;
            IsSelectedFileVideo = false;
            IsSelectedLivePhoto = false;
            PhotoFileName = string.Empty;
            FullPhotoFileName = string.Empty;
            PhotoInfoLine = string.Empty;
            VideoInfoLine = string.Empty;
            ProtocolLine = string.Empty;
            ExifCamera = string.Empty;
            ExifLensParams = string.Empty;
            ExifShootingParams = string.Empty;
            ExifPlaceName = string.Empty;
            TimelineInfo = string.Empty;
            FpsDisplayText = string.Empty;
            CurrentFramePositionText = string.Empty;
            SelectedFileThumbnail = null;

            // 清空大图预览
            SetPreviewSafe(null);
            PreviewClearRequested?.Invoke();

            // 清除时间轴帧 + 临时文件
            TimelineFrames.Clear();
            HasTimelineFrames = false;
            IsTimelineLoading = false;
            SelectedTimelineFrame = null;
            CleanupFrameTempFiles();
            CleanupTempVideo();
        }

        // ══════════════════════════════════════════════════════════════
        //  exiftool 属性加载（选中文件时）
        // ══════════════════════════════════════════════════════════════

        private void DisposeExifTool() { }

        /// <summary>全部 exiftool 查询标签（一次查询拿到所有属性）</summary>
        private static readonly string[] PropTags =
        {
            "-j",
            // 图片尺寸 & 格式
            "-ImageWidth", "-ImageHeight", "-MIMEType",
            // 相机
            "-Make", "-Model",
            // 拍摄参数
            "-FocalLength", "-FocalLengthIn35mmFormat", "-FNumber", "-ISO",
            "-ExposureTime", "-ExposureCompensation",
            // 镜头 & HDR
            "-LensModel", "-HDRImageType",
            // 小米私有 JSON 标签（SensorType=rear/front, ZoomMultiple）
            "-SensorType", "-ZoomMultiple",
            // 日期
            "-DateTimeOriginal",
            // GPS
            "-GPSLatitude", "-GPSLatitudeRef", "-GPSLongitude", "-GPSLongitudeRef", "-GPSAltitude",
            // 视频
            "-MediaDuration", "-AvgBitrate", "-CompressorID",
            // 实况照片
            "-ContentIdentifier",
            // 关键帧位置（Google V1 / V2 / OPPO — 微秒）
            "-MotionPhotoPresentationTimestampUs",
            "-MicroVideoPresentationTimestampUs",
        };

        /// <summary>异步加载照片 EXIF + 配对视频属性（并行查询，同时更新）</summary>
        /// <param name="generation">
        /// 选中代数（来自 SelectFile 的 Interlocked.Increment）。
        /// 在 dispatcher.TryEnqueue 回调中检查：如果已过期则跳过 TriggerTimelineExtraction。
        /// </param>
        private async Task LoadPropertiesAsync(string imagePath, string? videoPath, long embeddedVideoLen, int generation, CancellationToken token)
        {
            LogService.FileOp(
                $"Timeline[LoadProps] START: image='{Path.GetFileName(imagePath)}', " +
                $"videoPath='{(videoPath != null ? Path.GetFileName(videoPath) : "null")}', " +
                $"embeddedVideoLen={embeddedVideoLen}",
                LogLevel.Info);

            try
            {
                if (!IsImageOrVideo(imagePath))
                {
                    LogService.FileOp("Timeline[LoadProps] SKIP: not an image or video file", LogLevel.Warning);
                    return;
                }

                await LoadRebuiltPropertiesAsync(imagePath, videoPath, generation, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp("Timeline[LoadProps] CANCELLED (OperationCanceledException)", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Timeline[LoadProps] EXCEPTION: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, ex);
            }
        }
#if false

                // 华为实况照片：视频嵌在文件中间（非尾部），需特殊提取
                // 华为没有 XMP，embeddedVideoLen 始终为 0。先读文件尾检查是否有 LIVE_ 标记。
                if (videoPath == null && embeddedVideoLen == 0)
                {
                    bool isHuawei = false;
                    try
                    {
                        using var probeFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (probeFs.Length > 60)
                        {
                            probeFs.Seek(-60, SeekOrigin.End);
                            byte[] tail = new byte[60];
                            probeFs.ReadExactly(tail, 0, 60);
                            // Check for LIVE_ marker
                            for (int i = 0; i <= 55; i++)
                            {
                                if (tail[i] == 'L' && tail[i + 1] == 'I' && tail[i + 2] == 'V'
                                    && tail[i + 3] == 'E' && tail[i + 4] == '_')
                                { isHuawei = true; break; }
                            }
                        }
                    }
                    catch { /* best-effort */ }

                    if (isHuawei)
                    {
                        LogService.FileOp(
                            $"Timeline[LoadProps] Extracting HUAWEI embedded video from '{Path.GetFileName(imagePath)}'",
                            LogLevel.Info);
                        tempVideoPath = Path.Combine(Path.GetTempPath(), $"lpb_vid_{Guid.NewGuid():N}.mp4");
                        await Task.Run(() =>
                        {
                            var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(imagePath);
                            if (range == null)
                                throw new InvalidDataException("HUAWEI: cannot locate embedded MP4 (moov/ftyp not found)");

                            var (videoStart, videoEnd, videoLen) = range.Value;
                            // Streaming extraction from middle of file
                            using var src = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            src.Seek(videoStart, SeekOrigin.Begin);
                            using var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None);
                            var buf = new byte[81920];
                            long remain = videoLen;
                            while (remain > 0)
                            {
                                int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                                if (r == 0) break;
                                dst.Write(buf, 0, r);
                                remain -= r;
                            }
                        }, token);
                        videoPath = tempVideoPath;
                        LogService.FileOp(
                            $"HUAWEI video extracted: size={new FileInfo(tempVideoPath).Length} bytes",
                            LogLevel.Info);
                    }

                    // Google V2 HEIC（mpvd box 内嵌视频）
                    if (!isHuawei && videoPath == null
                        && HeicConverterService.IsHeicFile(imagePath))
                    {
                        try
                        {
                            var mpvdRange = GetMpvdVideoRange(imagePath);
                            if (mpvdRange != null)
                            {
                                var (mpvdStart, mpvdLen) = mpvdRange.Value;
                                LogService.FileOp(
                                    $"Timeline[LoadProps] Extracting Google V2 HEIC video via mpvd box " +
                                    $"(offset={mpvdStart}, len={mpvdLen}) from '{Path.GetFileName(imagePath)}'",
                                    LogLevel.Info);
                                tempVideoPath = Path.Combine(
                                    Path.GetTempPath(), $"lpb_vid_{Guid.NewGuid():N}.mp4");
                                await Task.Run(() =>
                                {
                                    using var src = new FileStream(
                                        imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    src.Seek(mpvdStart, SeekOrigin.Begin);
                                    using var dst = new FileStream(
                                        tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                    var buf = new byte[81920];
                                    long remain = mpvdLen;
                                    while (remain > 0)
                                    {
                                        int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                                        if (r == 0) break;
                                        dst.Write(buf, 0, r);
                                        remain -= r;
                                    }
                                }, token);
                                videoPath = tempVideoPath;
                                LogService.FileOp(
                                    $"Google V2 HEIC video extracted: size={new FileInfo(tempVideoPath).Length} bytes",
                                    LogLevel.Info);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.FileOp(
                                $"Google V2 HEIC video extraction failed: {ex.Message}",
                                LogLevel.Warning);
                        }
                    }
                }

                // 单文件实况照片：从 JPEG 尾部提取视频段到临时文件
                if (videoPath == null && embeddedVideoLen > 0)
                {
                    LogService.FileOp(
                        $"Timeline[LoadProps] Extracting embedded video: len={embeddedVideoLen} from '{Path.GetFileName(imagePath)}'",
                        LogLevel.Info);
                    tempVideoPath = Path.Combine(Path.GetTempPath(), $"lpb_vid_{Guid.NewGuid():N}.mp4");
                    var fileSize = new FileInfo(imagePath).Length;
                    long offset = fileSize - embeddedVideoLen;
                    await Task.Run(() =>
                    {
                        using var src = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        src.Seek(offset, SeekOrigin.Begin);
                        using var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        var buf = new byte[81920];
                        long remain = embeddedVideoLen;
                        while (remain > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (r == 0) break;
                            dst.Write(buf, 0, r);
                            remain -= r;
                        }
                    }, token);
                    videoPath = tempVideoPath;
                    LogService.FileOp(
                        $"Timeline[LoadProps] Embedded video extracted to temp: exists={File.Exists(tempVideoPath)}, size={new FileInfo(tempVideoPath).Length}",
                        LogLevel.Info);
                }

                // 构建照片查询
                var imgArgs = new string[PropTags.Length + 1];
                Array.Copy(PropTags, imgArgs, PropTags.Length);
                imgArgs[^1] = imagePath;

                // 并行查询照片 + 配对视频 + Apple MOV StillImageTime
                LogService.FileOp(
                    $"Timeline[LoadProps] Querying exiftool: image='{Path.GetFileName(imagePath)}', " +
                    $"video='{(videoPath != null ? Path.GetFileName(videoPath) : "N/A")}'",
                    LogLevel.Info);
                var imgTask = exifTool.SendCommandAsync(token, imgArgs);
                var vidTask = videoPath != null
                    ? exifTool.SendCommandAsync(token,
                        "-j", "-ImageWidth", "-ImageHeight",
                        "-AvgBitrate", "-CompressorID", "-MediaDuration",
                        "-VideoFrameRate", "-PosterTime", "-Duration", videoPath)
                    : Task.FromResult(string.Empty);

                // Apple StillImageTime 查询 — 仅对 Apple Live Photo (.mov) 有意义
                // Google V2 / Samsung / Huawei HEIC 提取出的 MP4 视频不应走此路径：
                // ReadAppleStillImageTime 在普通 MP4 上会错误地返回视频总时长，
                // 覆盖正确读到的 MotionPhotoPresentationTimestampUs。
                var appleTask = (!string.IsNullOrEmpty(videoPath) &&
                                 videoPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                    ? Task.Run(() => EditTimingService.ReadAppleStillImageTime(videoPath))
                    : Task.FromResult<double?>(null);

                await Task.WhenAll(imgTask, vidTask, appleTask);

                // 捕获 exiftool stderr（外部工具警告/错误）
                var stderr = exifTool.FlushStderr();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    LogService.FileOp(
                        $"Timeline[LoadProps] exiftool stderr: {stderr.Trim()}",
                        LogLevel.Warning);
                }

                if (token.IsCancellationRequested)
                {
                    LogService.FileOp("Timeline[LoadProps] CANCELLED", LogLevel.Warning);
                    return;
                }

                var imgJson = imgTask.Result;
                var vidJson = vidTask.Result;

                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null)
                {
                    LogService.FileOp("Timeline[LoadProps] SKIP: DispatcherQueue is null", LogLevel.Error);
                    return;
                }

                var imgProps = ParseExifProperties(imgJson, imagePath);
                var vidProps = ParseVideoProperties(vidJson);
                vidProps.FileSizeBytes = videoPath != null ? new FileInfo(videoPath).Length : 0;

                LogService.FileOp(
                    $"Timeline[LoadProps] Video props parsed: W={vidProps.Width}, H={vidProps.Height}, " +
                    $"Size={vidProps.FileSizeBytes}, BR='{vidProps.AvgBitrate ?? "null"}', " +
                    $"Codec='{vidProps.CompressorID ?? "null"}', MediaDur='{vidProps.MediaDuration ?? "null"}', " +
                    $"Dur='{vidProps.Duration ?? "null"}', FPS='{vidProps.VideoFrameRate ?? "null"}'",
                    LogLevel.Info);

                // ── 后台线程预计算 ──────────────────────────────────────────
                // 以下操作涉及 exiftool Process.Start 或磁盘 I/O，在后台线程执行，
                // 避免在 dispatcher 回调中阻塞 UI 线程 500-1000ms。
                // 预计算完成后，dispatcher 回调只做纯 UI 赋值（毫秒级）。

                // 解析视频时长：取 MediaDuration 和 Duration 中较长的那个
                double durSec = ParseExifDuration(vidProps.MediaDuration);
                double durSec2 = ParseExifDuration(vidProps.Duration);
                if (durSec2 > durSec) durSec = durSec2;

                // 计算关键帧时间偏移（各协议标签，纯 CPU 计算）
                double keyPhotoTimeSeconds = 0;
                // 跟踪是否从协议中成功读取了封面位置（即使值为 0）。
                // 用于阻止兜底逻辑覆盖合法的 0（如 Huawei v6_f0 / Samsung 封面）。
                bool coverFromProtocol = false;
                if (imgProps.MotionPhotoPresentationTimestampUs > 0)
                {
                    keyPhotoTimeSeconds = imgProps.MotionPhotoPresentationTimestampUs / 1_000_000.0;
                    coverFromProtocol = true;
                }
                else if (imgProps.MicroVideoPresentationTimestampUs > 0)
                {
                    keyPhotoTimeSeconds = imgProps.MicroVideoPresentationTimestampUs / 1_000_000.0;
                    coverFromProtocol = true;
                }
                else if (!string.IsNullOrWhiteSpace(vidProps.PosterTime))
                {
                    keyPhotoTimeSeconds = ParseExifDuration(vidProps.PosterTime);
                    coverFromProtocol = true;
                }

                // 视频 FPS 解析（提前，供 Huawei/Samsung 等无 XMP 协议使用）
                double fps = double.TryParse(vidProps.VideoFrameRate,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var f)
                    ? f : 30.0;

                // Huawei / Honor Moving Photo — 封面帧位置
                // 优先读新版 covertime（MP4 udta 毫秒时间戳），fallback 旧版 v6_fXX（尾部帧序号）
                if (keyPhotoTimeSeconds <= 0 && embeddedVideoLen <= 0)
                {
                    // 1. 新版：com.openharmony.covertime（毫秒 → 秒）
                    double? covertimeSec = EditTimingService.ReadHuaweiCovertimeSeconds(imagePath);
                    if (covertimeSec.HasValue) // field exists → use it (0 = first frame, valid)
                    {
                        LogService.FileOp(
                            $"Timeline[LoadProps] KeyPhoto from HUAWEI covertime: {covertimeSec.Value:F4}s",
                            LogLevel.Info);
                        keyPhotoTimeSeconds = covertimeSec.Value;
                        coverFromProtocol = true;
                    }
                    // 2. Fallback 旧版：v6_fXX（帧序号 / fps → 秒）
                    else
                    {
                        int? hwFrame = EditTimingService.ReadHuaweiCoverFrameNumber(imagePath);
                        if (hwFrame.HasValue && fps > 0)
                        {
                            double hwTime = hwFrame.Value / fps;
                            LogService.FileOp(
                                $"Timeline[LoadProps] KeyPhoto from HUAWEI tail v6_f{hwFrame.Value} (fallback): " +
                                $"{hwTime:F4}s (fps={fps:F2})",
                                LogLevel.Info);
                            keyPhotoTimeSeconds = hwTime;
                            coverFromProtocol = true;
                        }
                    }
                }

                // Samsung Motion Photo — 三星相册完全不读 XMP，只读 Trailer
                // 封面帧 = JPEG 静态图本身 = 视频 frame 0
                if (keyPhotoTimeSeconds <= 0)
                {
                    bool isSamsung = false;
                    try
                    {
                        using var probeFs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        if (probeFs.Length > 4096)
                        {
                            probeFs.Seek(-4096, SeekOrigin.End);
                            byte[] tailBytes = new byte[4096];
                            probeFs.ReadExactly(tailBytes, 0, 4096);
                            isSamsung = ContainsBytes(tailBytes, "SEFH"u8)
                                     && ContainsBytes(tailBytes, "SEFT"u8);
                        }
                    }
                    catch { }
                    if (isSamsung)
                    {
                        LogService.FileOp(
                            $"Timeline[LoadProps] Samsung: cover = still image (frame 0)",
                            LogLevel.Info);
                        keyPhotoTimeSeconds = 0;
                        coverFromProtocol = true;
                    }
                }

                // 兜底：所有协议都未能确定封面位置时，默认放到视频开头（frame 0）
                // 而非中间，避免误导用户以为封面帧在某个实际不是的位置。
                if (!coverFromProtocol && keyPhotoTimeSeconds <= 0 && durSec > 0)
                    keyPhotoTimeSeconds = 0;

                // Apple MOV StillImageTime — 已与 PersistentExifTool 并行执行，直接取结果
                double? appleTime = appleTask.Result;
                if (appleTime.HasValue && appleTime.Value > 0)
                {
                    LogService.FileOp(
                        $"Timeline[LoadProps] KeyPhoto from Apple MOV metadata track: " +
                        $"{appleTime.Value:F4}s (was {keyPhotoTimeSeconds:F4}s)",
                        LogLevel.Info);
                    keyPhotoTimeSeconds = appleTime.Value;
                }

                // XMP 文本读取 + 协议 timing 解析（涉及磁盘 I/O）
                string? xmpText = null;
                try { xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath); }
                catch { /* 非 JPEG 或读取失败，跳过 */ }
                var timing = EditTimingService.Resolve(keyPhotoTimeSeconds, xmpText);

                // OPPO 改封面后原始高清图提取
                byte[]? originalPhotoBytes = null;
                if (timing.HasOriginalPhoto)
                    originalPhotoBytes = EditTimingService.ReadOriginalPhotoBytes(imagePath);

                // 视频路径确认（单文件实况 → 临时视频，双文件 → 配对视频）
                string? actualVideoPath = tempVideoPath ?? videoPath;
                bool videoExists = !string.IsNullOrEmpty(actualVideoPath) && File.Exists(actualVideoPath);

                // ── UI 线程：仅做属性赋值 + 触发时间轴提取 ──────────────────
                dispatcher.TryEnqueue(() =>
                {
                    // 代数检查：后台计算耗时期间用户可能已切换到另一个文件
                    if (generation != Volatile.Read(ref _selectionGeneration))
                    {
                        LogService.FileOp(
                            $"Timeline[LoadProps] SKIP: generation mismatch (my={generation}, current={_selectionGeneration})",
                            LogLevel.Warning);
                        return;
                    }

                    ApplyProperties(imgProps);
                    ApplyVideoProperties(vidProps);

                    if (durSec > 0)
                    {
                        _videoFps = fps;
                        FpsDisplayText = ResourceService.Format("EditPage_TimelineFps", fps.ToString("F2"));

                        LogService.FileOp(
                            $"Timeline[LoadProps] Checking trigger: durSec={durSec}, fps={fps}, " +
                            $"actualVideoPath='{actualVideoPath ?? "null"}', exists={videoExists}",
                            LogLevel.Info);
                        if (videoExists)
                        {
                            LogService.FileOp(
                                $"Timeline[LoadProps] → Triggering extraction for '{Path.GetFileName(actualVideoPath!)}'",
                                LogLevel.Info);
                            TriggerTimelineExtractionDebounced(actualVideoPath!, durSec, fps,
                                timing.PhotoTimeSeconds, timing.CoverTimeSeconds,
                                generation,
                                originalPhotoBytes,
                                timing.IsOppo);
                        }
                        else
                        {
                            LogService.FileOp(
                                $"Timeline[LoadProps] SKIP extraction: video file not found at '{actualVideoPath}'",
                                LogLevel.Warning);
                        }
                    }
                    else
                    {
                        LogService.FileOp(
                            $"Timeline[LoadProps] SKIP extraction: MediaDuration invalid — " +
                            $"raw='{vidProps.MediaDuration ?? "null"}', parsed durSec={durSec}",
                            LogLevel.Warning);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp($"Timeline[LoadProps] CANCELLED (OperationCanceledException)", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"Timeline[LoadProps] EXCEPTION: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);
            }
            finally
            {
                // 不立即删除临时视频 — 时间轴帧提取还需要用它
                // 由 CleanupTempVideo() 在帧提取完成后清理
                if (tempVideoPath != null)
                {
                    _tempVideoPath = tempVideoPath;
        }
#endif

        private static string TruncateJson(string json, int maxLen = 300)
        {
            if (string.IsNullOrWhiteSpace(json)) return "(empty)";
            return json.Length <= maxLen ? json : json[..maxLen] + "...";
        }

        private static VideoProperties ParseVideoProperties(string json)
        {
            var vp = new VideoProperties();
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("["))
                return vp;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];
                vp.Width = GetJsonInt(root, "ImageWidth");
                vp.Height = GetJsonInt(root, "ImageHeight");
                vp.AvgBitrate = GetJsonStr(root, "AvgBitrate");
                vp.CompressorID = GetJsonStr(root, "CompressorID");
                vp.MediaDuration = GetJsonStr(root, "MediaDuration");
                vp.VideoFrameRate = GetJsonStr(root, "VideoFrameRate");
                vp.PosterTime = GetJsonStr(root, "PosterTime");
                vp.Duration = GetJsonStr(root, "Duration");
            }
            catch { }
            return vp;
        }

        /// <summary>解析 exiftool 返回的时长字符串（如 "2.93 s"、"0.95 s"），去掉单位后缀</summary>
        private static double ParseExifDuration(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            // 去掉常见后缀：空格 + s, 纯 s, 空格 + seconds
            var cleaned = raw.Trim();
            if (cleaned.EndsWith(" s", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^2].Trim();
            else if (cleaned.EndsWith('s'))
                cleaned = cleaned[..^1].Trim();
            return double.TryParse(cleaned,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        private void ApplyVideoProperties(VideoProperties vp)
        {
            if (vp.Width <= 0 || vp.Height <= 0 || string.IsNullOrWhiteSpace(vp.MediaDuration))
                return;

            var parts = new List<string> { $"{vp.Width} × {vp.Height}" };
            if (vp.FileSizeBytes > 0)
                parts.Add($"{vp.FileSizeBytes / 1_000_000.0:F2} MB");
            if (!string.IsNullOrWhiteSpace(vp.CompressorID))
                parts.Add(CodecToDisplay(vp.CompressorID));
            VideoInfoLine = string.Join("  │  ", parts);

            // ── 时间轴信息（TimelineInfo 由 TriggerTimelineExtraction 用 ffmpeg 真实帧数同步）──
        }

        /// <summary>
        /// 防抖包装：延迟 <see cref="TimelineDebounceMs"/>ms 后再触发时间轴帧提取。
        /// 快速连续切换文件时，每次调用会取消上一次待执行的提取，确保只有最后一次生效。
        /// 智能防抖：首次点击直接启动，200ms 内后续点击才防抖。
        /// 单次点击零延迟；快速切文件时只处理最后一次。
        /// </summary>
        private void TriggerTimelineExtractionDebounced(string videoPath, double durationSeconds,
            double fps, double photoTimeSeconds, double coverTimeSeconds,
            int generation, byte[]? originalPhotoBytes, bool isOppo = false)
        {
            _timelineDebounceCts?.Cancel();
            _timelineDebounceCts = new CancellationTokenSource();
            var debounceToken = _timelineDebounceCts.Token;

            if (Interlocked.CompareExchange(ref _timelineDebounceArmed, 1, 0) == 0)
            {
                // 首次点击：直接启动，零延迟
                TriggerTimelineExtraction(videoPath, durationSeconds, fps,
                    photoTimeSeconds, coverTimeSeconds, generation, originalPhotoBytes, isOppo);

                // 预设 200ms 后解除武装，期间的新点击会走防抖路径
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(TimelineDebounceMs, debounceToken); }
                    catch (OperationCanceledException) { }
                    Interlocked.Exchange(ref _timelineDebounceArmed, 0);
                });
                return;
            }

            // 已武装：防抖，延迟后只执行最后一次
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimelineDebounceMs, debounceToken);
                    if (generation != Volatile.Read(ref _selectionGeneration))
                        return;
                    TriggerTimelineExtraction(videoPath, durationSeconds, fps,
                        photoTimeSeconds, coverTimeSeconds, generation, originalPhotoBytes, isOppo);
                }
                catch (OperationCanceledException)
                {
                    // 被新点击取消，预期行为
                }
            });
        }

        /// <summary>
        /// 触发时间轴帧提取。
        /// 先让 ffmpeg 解码全部帧（原始尺寸），拿到真实帧数后再创建 TimelineFrame，
        /// 避免 Ceil(dur × fps) 估算导致的帧数不匹配（末尾空白帧）。
        /// </summary>
        /// <param name="videoPath">视频文件路径（双文件=配对视频，单文件=提取出的临时视频）</param>
        /// <param name="durationSeconds">视频时长（秒）</param>
        /// <param name="fps">视频帧率（用于计算每帧时间戳）</param>
        /// <param name="keyPhotoTimeSeconds">关键帧时间偏移（秒）</param>
        /// <param name="photoTimeSeconds">静态照片在视频中的时间偏移（秒，⭐ 位置）</param>
        /// <param name="coverTimeSeconds">封面帧/Cover 时间偏移（秒，🔵 选中位置）</param>
        /// <param name="generation">选中代数（过期则跳过所有 UI 更新）</param>
        private void TriggerTimelineExtraction(string videoPath, double durationSeconds, double fps,
            double keyPhotoTimeSeconds = 0, int generation = 0)
        {
            // 兼容旧调用（没传 photo/cover 时，两者都等于 keyPhotoTimeSeconds）
            TriggerTimelineExtraction(videoPath, durationSeconds, fps,
                keyPhotoTimeSeconds, keyPhotoTimeSeconds, generation, null, false);
        }

        private void TriggerTimelineExtraction(string videoPath, double durationSeconds, double fps,
            double photoTimeSeconds, double coverTimeSeconds,
            int generation = 0,
            byte[]? originalPhotoBytes = null,
            bool isOppo = false)
        {
            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
            {
                LogService.FileOp(
                    "Timeline[Extract] SKIP: Rebuilt Native mode does not use the Legacy FFmpeg frame extractor.",
                    LogLevel.Info);
                return;
            }

            bool split = Math.Abs(coverTimeSeconds - photoTimeSeconds) > 0.001;
            LogService.FileOp(
                $"Timeline[Extract] START: video='{Path.GetFileName(videoPath)}', " +
                $"dur={durationSeconds}s, fps={fps}, gen={generation}, " +
                $"photo={photoTimeSeconds}s, cover={coverTimeSeconds}s, split={split}",
                LogLevel.Info);

            // 取消上一次提取
            _timelineCts?.Cancel();
            CleanupFrameTempFiles();
            _timelineCts = new CancellationTokenSource();
            var ct = _timelineCts.Token;

            // 在缓存中索引当前文件路径（用于帧缩略图内存缓存）
            string sourcePath = SelectedFilePath ?? string.Empty;

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null)
            {
                LogService.FileOp("Timeline[Extract] SKIP: DispatcherQueue is null", LogLevel.Error);
                return;
            }

            // 立即显示 loading（旧帧已在 SelectFile 中清空，仅实况→实况不清空）
            dispatcher.TryEnqueue(() =>
            {
                // 代数检查：入队后执行前确认未被切换
                if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                    return;
                TimelineFrames.Clear();
                HasTimelineFrames = true;
                IsTimelineLoading = true;
            });

            // 后台：ffmpeg 解码全部帧（原始尺寸），完成后一次性创建 TimelineFrame
            _ = Task.Run(async () =>
            {
                try
                {
                    // 代数检查：Task.Run 创建到实际执行之间存在调度间隔，
                    // 期间用户可能已切换文件，此时跳过 ffmpeg 调用。
                    if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                    {
                        LogService.FileOp(
                            "Timeline[Extract] SKIP before ffmpeg: generation mismatch",
                            LogLevel.Warning);
                        return;
                    }

                    // 优先使用 SelectFile 中提前启动的 ffmpeg（与 exiftool 并行，省 500-700ms）
                    var earlyTask = _earlyFfmpegTask;
                    _earlyFfmpegTask = null;
                    var earlyCts = _earlyFfmpegCts;
                    _earlyFfmpegCts = null;

                    FrameExtractionResult? result;
                    if (earlyTask != null && !earlyTask.IsFaulted && !earlyTask.IsCanceled)
                    {
                        result = await earlyTask;
                        // 提前任务的 CTS 已用完，释放
                        try { earlyCts?.Dispose(); } catch { }
                    }
                    else
                    {
                        // 提前任务不可用（SingleFileJpeg 或已失败）→ 现场启动
                        result = await VideoFrameExtractionService.ExtractAllFramesAsync(
                            videoPath, ct);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        LogService.FileOp("Timeline[Extract] ffmpeg CANCELLED", LogLevel.Warning);
                        return;
                    }

                    // 代数检查：ffmpeg 提取耗时数秒，完成后再次确认文件未被切换。
                    // 过期时跳过 frame 创建+缩略图加载，清理临时帧文件。
                    if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                    {
                        LogService.FileOp(
                            "Timeline[Extract] SKIP after ffmpeg: generation mismatch",
                            LogLevel.Warning);
                        CleanupFrameTempFiles();
                        return;
                    }

                    if (result == null || result.FrameCount <= 0)
                    {
                        LogService.FileOp(
                            "Timeline[Extract] ffmpeg returned null or 0 frames — extraction failed",
                            LogLevel.Error);
                        dispatcher.TryEnqueue(() =>
                        {
                            IsTimelineLoading = false;
                            HasTimelineFrames = false;
                        });
                        return;
                    }

                    int actualFrameCount = result.FrameCount;
                    LogService.FileOp(
                        $"Timeline[Extract] ffmpeg done: {actualFrameCount} frames in '{result.TempDirectory}'",
                        LogLevel.Info);

                    // 存储临时目录路径以便后续清理
                    _frameExtractDir = result.TempDirectory;

                    // UI 线程：用真实帧数创建 TimelineFrame → 插入照片帧 → 加载缩略图
                    var loadedCount = 0;
                    var failedCount = 0;
                    TimelineFrame? stillFrame = null;
                    var uiTimelineDone = new System.Threading.Tasks.TaskCompletionSource();
                    dispatcher.TryEnqueue(async () =>
                    {
                        try
                        {
                            // 代数检查：入队后执行前，确认文件未被切换。
                            // 若过期则跳过帧创建+缩略图加载（最重的 UI 操作）。
                            if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                            {
                                LogService.FileOp(
                                    "Timeline[Extract] SKIP in UI callback: generation mismatch",
                                    LogLevel.Warning);
                                uiTimelineDone.TrySetResult();
                                return;
                            }

                            // 1. 用 ffmpeg 实际提取到的帧数创建视频帧（同步保存 FullFramePath）
                            _timelineActualFrameCount = actualFrameCount;
                            for (int i = 0; i < actualFrameCount; i++)
                            {
                                TimelineFrames.Add(new TimelineFrame
                                {
                                    FrameIndex = i,
                                    Timestamp = TimeSpan.FromSeconds(i / fps),
                                    // 回填全分辨率帧 JPEG 路径，供 PhotoViewer 大图预览
                                    FullFramePath = i < result.JpegPaths.Count
                                        ? result.JpegPaths[i] : null
                                });
                            }

                            // 2. 插入静态照片帧 ⭐（当前封面，用 coverTimeSeconds 定位）
                            //    找最接近 coverTime 的视频帧位置，而非用 >=（避免系统性偏后）
                            var coverTimestamp = TimeSpan.FromSeconds(coverTimeSeconds);
                            var photoTimestamp = TimeSpan.FromSeconds(photoTimeSeconds);
                            double frameInterval = 1.0 / fps;
                            double dupThreshold = frameInterval * 0.45; // 半帧以内视为重复

                            // ⭐ 帧缩略图：用 SelectedFileThumbnail（Primary item = 当前封面）
                            Microsoft.UI.Xaml.Media.ImageSource? starThumbnail = SelectedFileThumbnail;
                            string starKey = $"{sourcePath}|star";
                            if (_thumbnailCache.TryGetValue(starKey, out var cachedStar))
                            {
                                starThumbnail = cachedStar;
                                LogService.FileOp("Timeline[Extract] ⭐ thumbnail from cache", LogLevel.Info);
                            }
                            else if (starThumbnail != null)
                            {
                                AddToThumbnailCache(starKey, starThumbnail);
                            }

                            // ── 查找 coverTime 最近帧 ──
                            int starInsertPos = 0;
                            double starMinDiff = double.MaxValue;
                            for (int i = 0; i < TimelineFrames.Count; i++)
                            {
                                double diff = (TimelineFrames[i].Timestamp - coverTimestamp).Duration().TotalSeconds;
                                if (diff < starMinDiff) { starMinDiff = diff; starInsertPos = i; }
                            }

                            // OPPO 已换封面：⭐ 来自视频中选中的一帧 → 合并到视频帧上（打标，不删帧）
                            // 其他协议 / OPPO 未修改：⭐ 是相机拍摄的高清大图 → 独立插入
                            if (isOppo && split && starMinDiff < dupThreshold)
                            {
                                var mergedFrame = TimelineFrames[starInsertPos];
                                mergedFrame.IsStillPhoto = true;
                                // 不预设 Thumbnail — 排水泵从 JPEG 加载视频帧缩略图，天然正确
                                stillFrame = mergedFrame;
                                LogService.FileOp(
                                    $"Timeline[Extract] ⭐ merged onto vid frame #{mergedFrame.FrameIndex} " +
                                    $"(OPPO, diff={starMinDiff * 1000:F2}ms, time={coverTimeSeconds}s)",
                                    LogLevel.Debug);
                            }
                            else
                            {
                                if (starInsertPos < TimelineFrames.Count &&
                                    TimelineFrames[starInsertPos].Timestamp < coverTimestamp)
                                    starInsertPos++;

                                stillFrame = new TimelineFrame
                                {
                                    FrameIndex = -1,
                                    Timestamp = coverTimestamp,
                                    IsStillPhoto = true,
                                    Thumbnail = starThumbnail
                                };
                                TimelineFrames.Insert(starInsertPos, stillFrame);

                                LogService.FileOp(
                                    $"Timeline[Extract] Still photo ⭐ at pos={starInsertPos}/{TimelineFrames.Count}, " +
                                    $"time={coverTimeSeconds}s, isOppo={isOppo}, split={split}",
                                    LogLevel.Info);
                            }

                            // 2b. 原始封面帧 🖼（用 photoTimeSeconds，仅 split 时显示）
                            //     永远独立插入，不与视频帧合并——🖼 是相机拍摄的高清大图，不是视频帧。
                            //     把 Original item 的 JPEG 字节写入临时文件，确保导出和大图预览有源路径可用。
                            string? origTempPath = null;
                            if (split && originalPhotoBytes != null && originalPhotoBytes.Length > 0)
                            {
                                Microsoft.UI.Xaml.Media.ImageSource? origThumbnail = null;
                                string origKey = $"{sourcePath}|orig";
                                if (_thumbnailCache.TryGetValue(origKey, out var cachedOrig))
                                {
                                    origThumbnail = cachedOrig;
                                    LogService.FileOp("Timeline[Extract] 🖼 thumbnail from cache", LogLevel.Info);
                                }
                                else
                                {
                                    try
                                    {
                                        var ms = new MemoryStream(originalPhotoBytes);
                                        ms.Position = 0;
                                        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                                            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                                        var source = new SoftwareBitmapSource();
                                        await source.SetBitmapAsync(softwareBitmap);
                                        origThumbnail = source;
                                        AddToThumbnailCache(origKey, source);
                                        LogService.FileOp(
                                            $"Timeline[Extract] 🖼 using Original photo ({originalPhotoBytes.Length} bytes)",
                                            LogLevel.Info);
                                    }
                                    catch (Exception ex)
                                    {
                                        LogService.FileOp(
                                            $"Timeline[Extract] Failed to decode Original photo: {ex.Message}",
                                            LogLevel.Warning);
                                    }
                                }

                                // 将 Original JPEG 写到临时文件，供导出和 PhotoViewer 大图预览
                                try
                                {
                                    origTempPath = Path.Combine(result.TempDirectory,
                                        $"_original_{Path.GetFileNameWithoutExtension(sourcePath)}.jpg");
                                    await File.WriteAllBytesAsync(origTempPath, originalPhotoBytes, ct);
                                }
                                catch (Exception ex)
                                {
                                    LogService.FileOp(
                                        $"Timeline[Extract] Failed to write Original temp file: {ex.Message}",
                                        LogLevel.Warning);
                                    origTempPath = null;
                                }

                                // 查找 photoTime 最近的可插入位置（跳过已有特殊帧）
                                int origInsertPos = 0;
                                double origMinDiff = double.MaxValue;
                                for (int i = 0; i < TimelineFrames.Count; i++)
                                {
                                    if (TimelineFrames[i].IsStillPhoto || TimelineFrames[i].IsOriginalPhoto)
                                        continue;
                                    double diff = (TimelineFrames[i].Timestamp - photoTimestamp).Duration().TotalSeconds;
                                    if (diff < origMinDiff) { origMinDiff = diff; origInsertPos = i; }
                                }
                                if (origInsertPos < TimelineFrames.Count &&
                                    TimelineFrames[origInsertPos].Timestamp < photoTimestamp)
                                    origInsertPos++;

                                var origFrame = new TimelineFrame
                                {
                                    FrameIndex = -1,
                                    Timestamp = photoTimestamp,
                                    IsOriginalPhoto = true,
                                    Thumbnail = origThumbnail,
                                    FullFramePath = origTempPath
                                };
                                TimelineFrames.Insert(origInsertPos, origFrame);

                                LogService.FileOp(
                                    $"Timeline[Extract] Original photo 🖼 at pos={origInsertPos}/{TimelineFrames.Count}, " +
                                    $"time={photoTimeSeconds}s, thumbnail={(origThumbnail != null ? "ok" : "null")}",
                                    LogLevel.Info);
                                OnPropertyChanged(nameof(HasOriginalPhotoFrame));
                            }

                            // 3. TimelineInfo 只存时长（帧计数移到中间显示）
                            TimelineInfo = $"{durationSeconds:F2}s";

                            // 4. 立即选中封面帧并触发滚动 —— 不等缩略图加载！
                            //    帧已全部添加到 TimelineFrames，布局已就绪。
                            //    SelectTimelineFrameProgrammatically → RequestScrollToFrame 事件
                            //    → View 层 ClassicScrollToFrame / FilmstripScrollToFrameIndex。
                            //    缩略图在后台异步加载，不阻塞滚动。
                            var coverTs = TimeSpan.FromSeconds(coverTimeSeconds);
                            TimelineFrame? frameToSelect = stillFrame;
                            if (split)
                            {
                                double minDiff = (stillFrame.Timestamp - coverTs).Duration().TotalSeconds;
                                foreach (var f in TimelineFrames)
                                {
                                    double diff = (f.Timestamp - coverTs).Duration().TotalSeconds;
                                    if (diff < minDiff) { minDiff = diff; frameToSelect = f; }
                                }
                            }

                            IsTimelineLoading = false;
                            // 抑制初始滚动时的大图预览更新，保持封面图不闪烁
                            _isInitialTimelineScroll = true;
                            SelectTimelineFrameProgrammatically(frameToSelect);
                            // 800ms 后自动解除（滚动动画结束后恢复用户手动操作时的大图更新）
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(800);
                                dispatcher.TryEnqueue(() => _isInitialTimelineScroll = false);
                            });

                            // 5. 逐帧加载 JPEG 缩略图 → SoftwareBitmap (Bgra8 Premultiplied) + SoftwareBitmapSource
                            //    后台线程解码 + UI 线程创建 Source。
                            //    排水泵：每提取 4 帧执行一次 Task.Delay(1)，
                            //    强制 WinUI Compositor 在单帧内将已就绪纹理刷入 GPU，
                            //    避免 ItemsRepeater 虚拟化回收与异步解码撞车导致白块。
                            int timelineIdx = 0;
                            for (int jpegIdx = 0; jpegIdx < result.JpegPaths.Count; jpegIdx++)
                            {
                                ct.ThrowIfCancellationRequested();  // 快速切换时立即停止缩略图加载
                                try
                                {
                                // 跳过纯特殊帧（FrameIndex < 0，独立插入的 ⭐ 和 🖼）
                                // 合并帧（FrameIndex >= 0，OPPO 换封面后 ⭐ 打标在原视频帧上）不跳过
                                while (timelineIdx < TimelineFrames.Count
                                       && (TimelineFrames[timelineIdx].IsStillPhoto
                                           || TimelineFrames[timelineIdx].IsOriginalPhoto)
                                       && TimelineFrames[timelineIdx].FrameIndex < 0)
                                    timelineIdx++;
                                if (timelineIdx >= TimelineFrames.Count) break;
                                    string frameKey = $"{sourcePath}|{jpegIdx}";
                                    if (!_thumbnailCache.TryGetValue(frameKey, out var cachedFrame))
                                    {
                                        var jpegPath = result.JpegPaths[jpegIdx];
                                        // 后台线程：BitmapDecoder 解码 JPEG → SoftwareBitmap (Bgra8, Premultiplied)
                                        // 缩放至 224px 宽（4× 56px 卡片尺寸，高分屏锐利），GPU 内存从 ~8MB/帧 降至 ~0.05MB/帧
                                        var softwareBitmap = await Task.Run(() =>
                                        {
                                            using var fs = new FileStream(jpegPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                            var decoder = BitmapDecoder.CreateAsync(fs.AsRandomAccessStream()).GetAwaiter().GetResult();
                                            uint origW = decoder.PixelWidth;
                                            uint origH = decoder.PixelHeight;
                                            double scale = origW > 224 ? 224.0 / origW : 1.0;
                                            uint targetW = scale < 1.0 ? 224 : origW;
                                            uint targetH = scale < 1.0 ? (uint)Math.Max(1, origH * scale) : origH;
                                            var transform = new BitmapTransform
                                            {
                                                ScaledWidth = targetW,
                                                ScaledHeight = targetH,
                                                InterpolationMode = BitmapInterpolationMode.Fant
                                            };
                                            return decoder.GetSoftwareBitmapAsync(
                                                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                                                transform,
                                                ExifOrientationMode.IgnoreExifOrientation,
                                                ColorManagementMode.DoNotColorManage).GetAwaiter().GetResult();
                                        });
                                        // UI 线程：SoftwareBitmap → SoftwareBitmapSource
                                        var source = new SoftwareBitmapSource();
                                        await source.SetBitmapAsync(softwareBitmap);
                                        AddToThumbnailCache(frameKey, source);
                                        cachedFrame = source;
                                    }
                                    // 边界保护：加载期间 TimelineFrames 可能被新 SelectFile 清空
                                    if (timelineIdx < TimelineFrames.Count)
                                        TimelineFrames[timelineIdx].Thumbnail = cachedFrame;
                                    timelineIdx++;
                                    loadedCount++;
                                }
                                catch (Exception ex)
                                {
                                    timelineIdx++;
                                    failedCount++;
                                    if (failedCount <= 3)
                                    {
                                        LogService.FileOp(
                                            $"Timeline[Extract] Failed to load frame #{jpegIdx}: {ex.Message}",
                                            LogLevel.Warning);
                                    }
                                }

                                // 排水泵：每 4 帧让出 UI 线程给 Compositor 刷纹理
                                if (loadedCount > 0 && loadedCount % 4 == 0)
                                    await Task.Delay(1);
                            }

                            LogService.FileOp(
                                $"Timeline[Extract] Thumbnails loaded: {loadedCount} ok, {failedCount} failed (out of {actualFrameCount})",
                                failedCount > 0 ? LogLevel.Warning : LogLevel.Info);

                            // 临时帧文件保留，切换图片时由 SelectFile 清理
                        }
                        catch (Exception ex)
                        {
                            LogService.FileOp(
                                $"Timeline[Extract] UI thread exception: {ex.Message}",
                                LogLevel.Error, ex);
                            IsTimelineLoading = false;
                            HasTimelineFrames = false;
                            CleanupFrameTempFiles();
                            CleanupTempVideo();
                        }
                        finally
                        {
                            uiTimelineDone.TrySetResult();
                        }
                    });

                    // 等待 UI 线程帧创建完成（确保 stillFrame 已赋值）
                    await uiTimelineDone.Task;

                    // ⭐ 帧缩略图未就绪（SelectedFileThumbnail 可能异步加载中）→ 主动独立加载
                    // 不依赖列表缩略图管道（Magick.NET 对华为 HEIC 可能失败），
                    // 用 Windows BitmapDecoder 直接解码，与大图预览一致。
                    if (stillFrame != null && stillFrame.Thumbnail == null
                        && !string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                    {
                        string starKey = $"{sourcePath}|star";
                        try
                        {
                            var loaded = await LoadTimelineCoverThumbnailAsync(sourcePath);
                            if (loaded != null)
                            {
                                AddToThumbnailCache(starKey, loaded);
                                dispatcher.TryEnqueue(() =>
                                {
                                    if (stillFrame != null) stillFrame.Thumbnail = loaded;
                                });
                                LogService.FileOp("Timeline[Extract] ⭐ thumbnail loaded independently", LogLevel.Info);
                            }
                        }
                        catch { /* 加载失败不影响核心功能 */ }
                    }

                    // ⭐ 帧大图预览预加载：时间轴构建完成后主动加载，
                    // 写入 _previewCache，用户后续滚到 ⭐ 帧时缓存命中、瞬间显示。
                    if (stillFrame != null && !string.IsNullOrEmpty(sourcePath))
                    {
                        _ = LoadPreviewImageAsync(sourcePath, generation);
                        LogService.FileOp(
                            "Timeline[Extract] ⭐ large preview preload started", LogLevel.Info);
                    }
                }
                catch (OperationCanceledException)
                {
                    LogService.FileOp("Timeline[Extract] ffmpeg Task CANCELLED (user switched files)", LogLevel.Warning);
                    CleanupFrameTempFiles();
                    CleanupTempVideo();
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"Timeline[Extract] ffmpeg Task EXCEPTION: {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Error, ex);
                    dispatcher.TryEnqueue(() =>
                    {
                        IsTimelineLoading = false;
                        HasTimelineFrames = false;
                    });
                    CleanupFrameTempFiles();
                    CleanupTempVideo();
                }
            }, ct);
        }

        /// <summary>清理 ffmpeg 帧提取临时目录</summary>
        private void CleanupFrameTempFiles()
        {
            if (_frameExtractDir != null)
            {
                try { if (Directory.Exists(_frameExtractDir)) Directory.Delete(_frameExtractDir, recursive: true); }
                catch (Exception ex) { LogService.FileOp($"Cleanup frame dir failed: {ex.Message}", Models.LogLevel.Warning); }
                _frameExtractDir = null;
            }
        }

        /// <summary>清理单文件实况照片的临时视频</summary>
        private void CleanupTempVideo()
        {
            if (_tempVideoPath != null)
            {
                try { if (File.Exists(_tempVideoPath)) File.Delete(_tempVideoPath); }
                catch (Exception ex) { LogService.FileOp($"Cleanup temp video failed: {ex.Message}", Models.LogLevel.Warning); }
                _tempVideoPath = null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  大图预览加载
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 异步加载选中文件的大图预览（DecodePixelWidth=2560）。
        /// HEIC/HEIF：使用 BitmapDecoder + BitmapTransform 在解码阶段直接缩放到目标尺寸，
        ///           转为临时 JPEG 后加载，避免全分辨率解码（参考 ImagePreviewService.LoadHeicPreviewAsync）。
        /// 非 HEIC：使用 StorageFile + BitmapImage.SetSourceAsync 异步解码，不阻塞 UI 线程。
        /// 结果写入 _previewCache，后续同一文件命中缓存直接返回，无需重新解码。
        /// </summary>
        /// <param name="imagePath">图片文件路径</param>
        /// <param name="generation">
        /// 选中代数（来自 SelectFile 的 Interlocked.Increment）。
        /// generation &gt; 0 时，在每次 dispatcher 回调中检查是否过期（!= _selectionGeneration），
        /// 过期则跳过 UI 更新。generation == 0 时不检查（用户手动点击时间轴帧场景）。
        /// </param>
        // 最近一次预览请求的目标路径，用于拦截过期回调（防星标帧 HEIC 慢加载覆盖后续帧的预览）
        private string? _latestPreviewRequestPath;

        /// <summary>
        /// 独立加载时间轴封面帧缩略图（不依赖列表缩略图管道）。
        /// 对 HEIC 使用 Windows BitmapDecoder（与大图预览一致），JPEG 用 BitmapImage 缩放。
        /// </summary>
        private static async Task<ImageSource?> LoadTimelineCoverThumbnailAsync(string imagePath)
        {
            try
            {
                bool isHeic = HeicConverterService.IsHeicFile(imagePath);
                const uint thumbSize = 112;

                if (isHeic)
                {
                    // 与大图预览相同：Windows BitmapDecoder 解码 + 缩放到 112px
                    var file = await StorageFile.GetFileFromPathAsync(imagePath);
                    using var inputStream = await file.OpenAsync(FileAccessMode.Read);
                    var decoder = await BitmapDecoder.CreateAsync(inputStream);

                    double scale = Math.Min((double)thumbSize / decoder.PixelWidth,
                                            (double)thumbSize / decoder.PixelHeight);
                    uint tw = scale < 1.0 ? (uint)Math.Max(1, decoder.PixelWidth * scale) : decoder.PixelWidth;
                    uint th = scale < 1.0 ? (uint)Math.Max(1, decoder.PixelHeight * scale) : decoder.PixelHeight;

                    var transform = new BitmapTransform
                    {
                        ScaledWidth = tw,
                        ScaledHeight = th,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };
                    using var swBmp = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        transform, ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(swBmp);
                    return source;
                }
                else
                {
                    // JPEG/PNG 等：直接用 BitmapImage 缩放
                    var bmp = new BitmapImage { DecodePixelWidth = (int)thumbSize };
                    using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await bmp.SetSourceAsync(fs.AsRandomAccessStream());
                    return bmp;
                }
            }
            catch { return null; }
        }

        private async Task LoadPreviewImageAsync(string imagePath, int generation = 0)
        {
            _latestPreviewRequestPath = imagePath;
            _previewLoadCts?.Cancel();
            _previewLoadCts?.Dispose();
            _previewLoadCts = new CancellationTokenSource();
            var token = _previewLoadCts.Token;

            // 缓存命中 → 直接显示，无需重新解码
            if (_previewCache.TryGetValue(imagePath, out var cached))
            {
                // 代数检查：仅当此加载请求未过期时才写入 PreviewImageSource
                if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                {
                    LogService.FileOp(
                        $"KeyPhoto Preview(cache): stale (gen={generation}, cur={_selectionGeneration}), skip",
                        LogLevel.Info);
                    return;
                }
                // 必须走 UI 线程设值：PreviewImageSource → x:Bind → PhotoViewer.ImageSource
                // → SetValue(DependencyProperty) → COM 调用，非 UI 线程会抛 0x8001010E
                var disp = App.MainWindow?.DispatcherQueue;
                disp?.TryEnqueue(() =>
                {
                    if (_latestPreviewRequestPath != imagePath) return; // 过期请求，跳过
                    SetPreviewSafe(cached);
                });
                return;
            }

            // 不清空 PreviewImageSource —— PhotoViewer 双缓冲层会在新图就绪后自动切换，
            // 旧图保持可见直至新图就绪，杜绝 Source=null 闪白。
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return;

            bool isHeic = HeicConverterService.IsHeicFile(imagePath);

            try
            {
                if (isHeic)
                {
                    // ── HEIC/HEIF：BitmapDecoder 解码阶段缩放 + 临时 JPEG ──
                    string? tempJpegPath = null;
                    try
                    {
                        // 后台线程：BitmapDecoder 解码 + 缩放 + 编码为 JPEG
                        tempJpegPath = await Task.Run(async () =>
                        {
                            token.ThrowIfCancellationRequested();
                            var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(token);
                            using var inputStream = await file.OpenAsync(FileAccessMode.Read).AsTask(token);
                            var decoder = await BitmapDecoder.CreateAsync(inputStream);

                            uint origW = decoder.PixelWidth;
                            uint origH = decoder.PixelHeight;
                            double scale = origW > 2560 ? 2560.0 / origW : 1.0;
                            uint targetW = scale < 1.0 ? 2560 : origW;
                            uint targetH = scale < 1.0 ? (uint)Math.Max(1, origH * scale) : origH;

                            var transform = new BitmapTransform
                            {
                                ScaledWidth = targetW,
                                ScaledHeight = targetH,
                                InterpolationMode = BitmapInterpolationMode.Fant
                            };

                            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                                transform,
                                ExifOrientationMode.RespectExifOrientation,
                                ColorManagementMode.ColorManageToSRgb);

                            token.ThrowIfCancellationRequested();

                            string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_prev_{Guid.NewGuid():N}.jpg");
                            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                            {
                                var encoder = await BitmapEncoder.CreateAsync(
                                    BitmapEncoder.JpegEncoderId, fileStream.AsRandomAccessStream());
                                encoder.SetSoftwareBitmap(softwareBitmap);
                                await encoder.FlushAsync();
                            }

                            softwareBitmap.Dispose();
                            return tempPath;
                        }, token);

                        if (token.IsCancellationRequested) return;
                        if (tempJpegPath == null || !File.Exists(tempJpegPath)) return;

                        // 代数检查：后台解码完成后，在回 UI 线程之前再次确认文件未被切换
                        if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                        {
                            LogService.FileOp(
                                $"KeyPhoto Preview(HEIC): stale after decode (gen={generation}, cur={_selectionGeneration}), skip",
                                LogLevel.Info);
                            return;
                        }

                        // UI 线程：从临时 JPEG 创建 BitmapImage
                        var tcs = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                // 代数检查：回调入队后执行前，确认文件未被切换
                                if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                                {
                                    LogService.FileOp(
                                        $"KeyPhoto Preview(HEIC-dispatch): stale (gen={generation}, cur={_selectionGeneration}), skip",
                                        LogLevel.Info);
                                    tcs.TrySetResult(false); return;
                                }
                                if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                                var bmp = new BitmapImage { DecodePixelWidth = 2560 };
                                using var fs = new FileStream(tempJpegPath, FileMode.Open, FileAccess.Read);
                                bmp.SetSource(fs.AsRandomAccessStream());
                                LogService.FileOp(
                                    $"KeyPhoto Preview(HEIC): set PreviewImageSource for '{Path.GetFileName(imagePath)}'",
                                    LogLevel.Info);
                                if (_latestPreviewRequestPath != imagePath) { tcs.TrySetResult(false); return; }
                                SetPreviewSafe(bmp);
                                AddToPreviewCache(imagePath, bmp);
                                tcs.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                LogService.Debug($"PhotoViewer HEIC decode failed: {ex.Message}", LogSource.UI);
                                tcs.TrySetResult(false);
                            }
                        });
                        await tcs.Task;
                    }
                    finally
                    {
                        if (tempJpegPath != null)
                        {
                            try { File.Delete(tempJpegPath); } catch { }
                        }
                    }
                }
                else
                {
                    // ── 非 HEIC（JPG/PNG 等）：StorageFile + SetSourceAsync 异步解码 ──
                    var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(token);
                    if (token.IsCancellationRequested) return;

                    var tcs = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    dispatcher.TryEnqueue(async () =>
                    {
                        try
                        {
                            // 代数检查：回调入队后执行前，确认文件未被切换
                            if (generation > 0 && generation != Volatile.Read(ref _selectionGeneration))
                            { tcs.TrySetResult(false); return; }
                            if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                            var bmp = new BitmapImage { DecodePixelWidth = 2560 };
                            using (var stream = await file.OpenReadAsync().AsTask(token))
                            {
                                if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                                await bmp.SetSourceAsync(stream);
                            }
                            if (_latestPreviewRequestPath != imagePath) { tcs.TrySetResult(false); return; }
                            SetPreviewSafe(bmp);
                            AddToPreviewCache(imagePath, bmp);
                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            LogService.Debug($"PhotoViewer decode failed: {ex.Message}", LogSource.UI);
                            tcs.TrySetResult(false);
                        }
                    });
                    await tcs.Task;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.Debug($"PhotoViewer load failed: {ex.Message}", LogSource.UI);
            }
        }

        /// <summary>
        /// 将大图预览写入缓存，超过上限时淘汰最旧的条目。
        /// </summary>
        private void AddToPreviewCache(string filePath, ImageSource image)
        {
            // 已在缓存中 → 移到最新位置
            _previewCacheOrder.Remove(filePath);
            _previewCacheOrder.Add(filePath);
            _previewCache[filePath] = image;

            // 超过上限 → 淘汰最旧的一条
            while (_previewCacheOrder.Count > MaxPreviewCacheSize)
            {
                string oldest = _previewCacheOrder[0];
                _previewCacheOrder.RemoveAt(0);
                _previewCache.Remove(oldest);
            }
        }

        /// <summary>
        /// 将帧缩略图写入缓存，超过上限时淘汰最旧的条目。
        /// 与 <see cref="AddToPreviewCache"/> 结构一致。
        /// </summary>
        private void AddToThumbnailCache(string key, ImageSource source)
        {
            _thumbnailCacheOrder.Remove(key);
            _thumbnailCacheOrder.AddLast(key);
            _thumbnailCache[key] = source;

            while (_thumbnailCacheOrder.Count > MaxThumbnailCacheSize)
            {
                string oldest = _thumbnailCacheOrder.First!.Value;
                _thumbnailCacheOrder.RemoveFirst();
                _thumbnailCache.Remove(oldest);
            }
        }

        /// <summary>
        /// 用户手动点击时间轴帧 → 更新大图预览。
        /// 照片帧⭐ → 加载原始照片文件；
        /// 视频帧 → 加载 ffmpeg 提取的全分辨率 JPEG。
        /// </summary>
        private async Task UpdatePreviewForTimelineFrameAsync(TimelineFrame frame)
        {
            string? imagePath = null;

            if (frame.IsStillPhoto)
            {
                // 静态照片帧 ⭐：使用原始照片文件（Primary item）
                imagePath = SelectedFilePath;
            }
            else if (frame.IsOriginalPhoto)
            {
                // 原始帧 🖼：优先使用 FullFramePath（已写入临时文件），回退到重新提取
                if (!string.IsNullOrEmpty(frame.FullFramePath) && File.Exists(frame.FullFramePath))
                    imagePath = frame.FullFramePath;
                else if (!string.IsNullOrEmpty(SelectedFilePath) && File.Exists(SelectedFilePath))
                {
                    byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(SelectedFilePath);
                    if (origBytes != null && origBytes.Length > 0)
                    {
                        string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_preview_orig_{Guid.NewGuid():N}.jpg");
                        await File.WriteAllBytesAsync(tempPath, origBytes);
                        imagePath = tempPath;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(frame.FullFramePath) && File.Exists(frame.FullFramePath))
            {
                // 视频帧：使用 ffmpeg 提取的全分辨率帧 JPEG
                imagePath = frame.FullFramePath;
            }

            if (string.IsNullOrEmpty(imagePath)) return;

            await LoadPreviewImageAsync(imagePath);
        }

        /// <summary>
        /// Rebuilt property path. Only Native facts and the existing small
        /// binary metadata reader are allowed here; the Legacy EXIF process
        /// is intentionally not a fallback.
        /// </summary>
        private async Task LoadRebuiltPropertiesAsync(
            string imagePath, string? videoPath, int generation, CancellationToken token)
        {
            try
            {
                var inspector = new SourceInspector();
                var facts = await inspector.InspectAsync(imagePath, videoPath, token).ConfigureAwait(false);
                VideoFacts? videoFacts = facts.MotionVideo;
                if (IsSelectedFileVideo)
                    videoFacts = await new VideoConverter().ProbeAsync(imagePath, token).ConfigureAwait(false);

                string resolution = string.Empty;
                EditFileItem? selectedItem = FileItems.FirstOrDefault(f =>
                    string.Equals(f.FilePath, imagePath, StringComparison.OrdinalIgnoreCase));
                if (!IsSelectedFileVideo)
                {
                    var (width, height, _) = FastMetadataReader.Read(imagePath);
                    if (width > 0 && height > 0)
                    {
                        resolution = $"{width} × {height}";
                        if (selectedItem != null && string.IsNullOrEmpty(selectedItem.Resolution))
                            selectedItem.Resolution = resolution;
                    }
                    else
                    {
                        resolution = selectedItem?.Resolution ?? string.Empty;
                    }
                }

                string extension = Path.GetExtension(imagePath).TrimStart('.').ToUpperInvariant();
                string photoInfo = IsSelectedFileVideo
                    ? string.Empty
                    : string.IsNullOrEmpty(resolution)
                        ? $"{GetPhotoSizeDisplay(selectedItem)}  │  {extension}"
                        : $"{resolution}  │  {GetPhotoSizeDisplay(selectedItem)}  │  {extension}";

                string videoInfo = string.Empty;
                string timelineInfo = string.Empty;
                string fpsText = string.Empty;
                if (videoFacts is { IsPresent: true })
                {
                    long videoBytes = videoFacts.ByteLength;
                    if (videoBytes <= 0 && videoPath != null && File.Exists(videoPath))
                        videoBytes = new FileInfo(videoPath).Length;
                    var parts = new List<string>();
                    if (videoFacts.Width > 0 && videoFacts.Height > 0)
                        parts.Add($"{videoFacts.Width} × {videoFacts.Height}");
                    if (videoBytes > 0)
                        parts.Add(FileSizeFormatter.Format(videoBytes));
                    parts.Add(videoFacts.Codec switch
                    {
                        VideoCodec.H264 => "H.264",
                        VideoCodec.Hevc => "H.265",
                        _ => VideoCodec.Unknown.ToString()
                    });
                    if (videoFacts.DurationSeconds > 0)
                    {
                        parts.Add($"{videoFacts.DurationSeconds:F2}s");
                        timelineInfo = $"{videoFacts.DurationSeconds:F2}s";
                    }
                    videoInfo = string.Join("  │  ", parts);
                    if (videoFacts.Fps > 0)
                        fpsText = ResourceService.Format("EditPage_TimelineFps", videoFacts.Fps.ToString("F2"));
                }

                string protocol = GetRebuiltProtocolName(facts.Protocol);
                var dispatcher = App.MainWindow?.DispatcherQueue;
                dispatcher?.TryEnqueue(() =>
                {
                    if (generation != _selectionGeneration) return;
                    PhotoInfoLine = photoInfo;
                    VideoInfoLine = videoInfo;
                    ProtocolLine = protocol;
                    TimelineInfo = timelineInfo;
                    FpsDisplayText = fpsText;
                    ExifCamera = ResourceService.GetString("EditPage_UnknownDevice");
                    ExifCameraDateSuffix = string.Empty;
                    ExifLensParams = string.Empty;
                    ExifShootingParams = string.Empty;
                    ExifPlaceName = string.Empty;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.FileOp($"Timeline[LoadProps] Rebuilt Native inspection failed: {ex.Message}", LogLevel.Warning);
            }
        }

        private static string GetRebuiltProtocolName(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.GoogleMicroVideoV1 => ResourceService.GetString("EditPage_Protocol_GoogleV1"),
            SourceProtocol.GoogleMotionPhotoV2 => ResourceService.GetString("EditPage_Protocol_GoogleV2"),
            SourceProtocol.OppoLivePhoto => ResourceService.GetString("EditPage_Protocol_OPPO"),
            SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile => ResourceService.GetString("EditPage_Protocol_Vivo"),
            SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic => ResourceService.GetString("EditPage_Protocol_Samsung"),
            SourceProtocol.HuaweiMovingPhoto or SourceProtocol.HonorMovingPhoto => ResourceService.GetString("EditPage_Protocol_Huawei"),
            SourceProtocol.AppleLivePhoto => ResourceService.GetString("EditPage_Protocol_Apple"),
            _ => ResourceService.GetString("EditPage_Protocol_NonLive")
        };

        /// <summary>解析 exiftool JSON 输出为结构化属性</summary>
        private static ExifProperties ParseExifProperties(string json, string filePath)
        {
            var p = new ExifProperties();

            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("["))
                return p;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];

                // 图片尺寸
                p.ImageWidth = GetJsonInt(root, "ImageWidth");
                p.ImageHeight = GetJsonInt(root, "ImageHeight");

                // 格式
                p.MimeType = GetJsonStr(root, "MIMEType");

                // 相机：厂商 + 型号。若型号已包含厂商则不重复（如 "Xiaomi 14 Pro" 不拼成 "Xiaomi Xiaomi 14 Pro"）
                string? make = GetJsonStr(root, "Make");
                string? model = GetJsonStr(root, "Model");
                if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(make)
                    && model.Contains(make, StringComparison.OrdinalIgnoreCase))
                    p.Camera = model;
                else if (!string.IsNullOrWhiteSpace(make) || !string.IsNullOrWhiteSpace(model))
                    p.Camera = $"{make} {model}".Trim();
                else
                    p.Camera = "";

                // 拍摄参数
                p.FocalLength = GetJsonStr(root, "FocalLength");
                p.FocalLengthIn35mmFormat = GetJsonStr(root, "FocalLengthIn35mmFormat");
                p.FNumber = GetJsonStr(root, "FNumber");
                p.ISO = GetJsonInt(root, "ISO");
                p.ExposureTime = GetJsonStr(root, "ExposureTime");
                p.ExposureCompensation = GetJsonStr(root, "ExposureCompensation");
                // 镜头 & HDR
                p.LensModel = GetJsonStr(root, "LensModel");
                p.HDRImageType = GetJsonStr(root, "HDRImageType");
                // 小米私有字段（SensorType=rear/front, ZoomMultiple）
                p.SensorType = GetJsonStr(root, "SensorType");
                p.ZoomMultiple = GetJsonInt(root, "ZoomMultiple");

                // 日期
                p.DateTimeOriginal = GetJsonStr(root, "DateTimeOriginal");

                // GPS
                p.GpsLatitude = GetJsonStr(root, "GPSLatitude");
                p.GpsLatitudeRef = GetJsonStr(root, "GPSLatitudeRef");
                p.GpsLongitude = GetJsonStr(root, "GPSLongitude");
                p.GpsLongitudeRef = GetJsonStr(root, "GPSLongitudeRef");
                p.GpsAltitude = GetJsonStr(root, "GPSAltitude");

                // 视频
                p.MediaDuration = GetJsonStr(root, "MediaDuration");
                p.AvgBitrate = GetJsonStr(root, "AvgBitrate");
                p.CompressorID = GetJsonStr(root, "CompressorID");

                // 实况照片标识
                p.ContentIdentifier = GetJsonStr(root, "ContentIdentifier");

                // 关键帧位置（各协议）
                p.MotionPhotoPresentationTimestampUs = GetJsonInt(root, "MotionPhotoPresentationTimestampUs");
                p.MicroVideoPresentationTimestampUs = GetJsonInt(root, "MicroVideoPresentationTimestampUs");
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Parse exiftool JSON failed: {ex.Message}", LogLevel.Warning);
            }

            return p;
        }

        /// <summary>将解析好的属性写入绑定字段</summary>
        private void ApplyProperties(ExifProperties p)
        {
            var ext = Path.GetExtension(SelectedFilePath ?? "").TrimStart('.').ToUpperInvariant();
            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase));

            // ── PhotoInfoLine：分辨率 │ 大小 │ 格式 ──
            string resolution;
            if (p.ImageWidth > 0 && p.ImageHeight > 0)
            {
                resolution = $"{p.ImageWidth} × {p.ImageHeight}";
                // 同步到 FileItem（扫描阶段可能没读到）
                if (item != null && string.IsNullOrEmpty(item.Resolution))
                    item.Resolution = resolution;
            }
            else
            {
                resolution = item?.Resolution ?? "";
            }
            var photoSize = GetPhotoSizeDisplay(item);
            PhotoInfoLine = string.IsNullOrEmpty(resolution)
                ? $"{photoSize}  │  {ext}"
                : $"{resolution}  │  {photoSize}  │  {ext}";

            // ── VideoInfoLine：视频分辨率 │ 大小 │ 编码 │ 时长 ──
            // 仅当实际读到视频数据时才更新（双文件实况照片由 LoadVideoFilePropertiesAsync 单独填充）
            if (p.ImageWidth > 0 && p.ImageHeight > 0 && !string.IsNullOrWhiteSpace(p.MediaDuration))
            {
                var parts = new List<string>();
                parts.Add($"{p.ImageWidth} × {p.ImageHeight}");
                if (item != null)
                {
                    // 单文件实况照片：视频大小 = 内嵌视频段长度；其他：整个文件大小
                    long videoBytes = item.AppendedVideoLength > 0
                        ? item.AppendedVideoLength
                        : new FileInfo(item.FilePath).Length;
                    parts.Add($"{videoBytes / 1_000_000.0:F2} MB");
                }
                if (!string.IsNullOrWhiteSpace(p.CompressorID))
                    parts.Add(CodecToDisplay(p.CompressorID));
                VideoInfoLine = string.Join("  │  ", parts);
            }

            // ── ProtocolLine：协议 · 日期 ──
            string date = !string.IsNullOrWhiteSpace(p.DateTimeOriginal)
                ? FormatDateTime(p.DateTimeOriginal) : item?.DateTaken ?? "";
            string? protocol = GetProtocolName(item?.LivePhotoType ?? LivePhotoType.None,
                SelectedFilePath, p.ContentIdentifier);

            // 双文件实况：配对文件缺失时附加提示，保留协议标记
            // 明确是单文件打包的协议（华为/三星/OPPO/GoogleV1/GoogleV2/Fusion），
            // 即使被 CID 误标为 DualFile，也不应显示"缺少视频"
            bool isDefinitelySingleFile = item != null && item.DetectedProtocol is
                LivePhotoProtocolType.Huawei or
                LivePhotoProtocolType.Samsung or
                LivePhotoProtocolType.OPPO or
                LivePhotoProtocolType.GoogleV1 or
                LivePhotoProtocolType.GoogleV2 or
                LivePhotoProtocolType.Fusion;
            if (item != null && item.HasConfirmedProtocol
                && item.LivePhotoType == LivePhotoType.DualFile
                && !isDefinitelySingleFile
                && (string.IsNullOrEmpty(item.PairedVideoPath)
                    || !File.Exists(item.PairedVideoPath)))
            {
                // 根据当前文件类型判断缺失的是视频还是照片
                string missingKey = SupportedVideoExtensions.Contains(
                    Path.GetExtension(SelectedFilePath ?? ""))
                    ? "EditPage_Protocol_MissingPhoto"
                    : "EditPage_Protocol_MissingVideo";
                protocol = (protocol ?? "") + ResourceService.GetString(missingKey);
            }

            ProtocolLine = protocol ?? string.Empty;

            // ── 摄像头位置（后置/前置 + 类型），用于 Line 1 后缀 ──
            string cameraPosition = GetCameraPosition(p);

            // ── ExifCamera（Line 1 粗体）：拍摄设备 ──
            ExifCamera = !string.IsNullOrWhiteSpace(p.Camera)
                ? p.Camera
                : ResourceService.GetString("EditPage_UnknownDevice");

            // ── ExifCameraDateSuffix（Line 1 后缀）：摄像头位置替代原来的日期 ──
            ExifCameraDateSuffix = string.IsNullOrEmpty(cameraPosition)
                ? string.Empty
                : $"  —  {cameraPosition}";

            // ── ExifLensParams（Line 2）：镜头参数 + 拍摄参数合并为一行 ──
            var paramParts = new List<string>();
            // 焦段
            if (!string.IsNullOrWhiteSpace(p.FocalLengthIn35mmFormat))
                paramParts.Add(FormatFocalLength(p.FocalLengthIn35mmFormat));
            // 光圈
            if (!string.IsNullOrWhiteSpace(p.FNumber))
                paramParts.Add(FormatFNumber(p.FNumber));
            // ISO
            if (p.ISO > 0)
                paramParts.Add($"ISO {p.ISO}");
            // EV
            if (!string.IsNullOrWhiteSpace(p.ExposureCompensation))
            {
                var ev = p.ExposureCompensation.Trim();
                if (double.TryParse(ev, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var evVal))
                    paramParts.Add($"EV{(evVal >= 0 ? "+" : "")}{evVal:F1}");
                else
                    paramParts.Add($"EV{ev}");
            }
            // 快门
            if (!string.IsNullOrWhiteSpace(p.ExposureTime))
                paramParts.Add(FormatExposureTime(p.ExposureTime));
            // HDR
            if (!string.IsNullOrWhiteSpace(p.HDRImageType))
                paramParts.Add("HDR");
            ExifLensParams = paramParts.Count > 0
                ? string.Join("  │  ", paramParts)
                : string.Empty;

            // ── ExifShootingParams（Line 3）：日期时间（从 Line 1 移下来）──
            ExifShootingParams = string.IsNullOrEmpty(date) ? string.Empty : date;

            // ── ExifPlaceName：反向地理编码地名 ──
            ExifPlaceName = string.Empty;
            double? lat = DmsToDecimal(p.GpsLatitude, p.GpsLatitudeRef);
            double? lon = DmsToDecimal(p.GpsLongitude, p.GpsLongitudeRef);

            LogService.FileOp(
                $"GPS parsed: lat={lat?.ToString("F6") ?? "null"}, lon={lon?.ToString("F6") ?? "null"}, " +
                $"rawLat='{p.GpsLatitude ?? "null"}', rawLon='{p.GpsLongitude ?? "null"}'",
                LogLevel.Info);

            if (lat != null && lon != null && SelectedFilePath != null)
            {
                // 立即显示加载态，让用户知道正在查询
                ExifPlaceName = ResourceService.GetString("EditPage_GeoLookingUp");
                _ = TriggerGeoLookupAsync(lat.Value, lon.Value, SelectedFilePath);
            }
            else
            {
                // 无 GPS → 占位文字
                ExifPlaceName = ResourceService.GetString("EditPage_NoLocation");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  扫描入口（由 View 层调用）
        // ══════════════════════════════════════════════════════════════

        public void TriggerScan()
        {
            var path = CurrentDirectory;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            if (IsScanning) return;

            _ = ScanDirectoryAsync(path);
        }

        /// <summary>清空当前浏览的全部内容：目录、文件列表和预览。</summary>
        public void ClearAll()
        {
            CurrentDirectory = string.Empty;
            _allFileItems.Clear();
            FileItems.Clear();
            RefreshCounts();
            OnPropertyChanged(nameof(HasFilesLoaded));
            ClearFileInfo();
            ThumbnailService.ClearCache();
            ThumbnailScheduler.Reset();
            OnPropertyChanged(nameof(HasAnyFiles));
        }

        // ══════════════════════════════════════════════════════════════
        //  目录扫描（阶段 1 枚举 + 阶段 2 分辨率）
        // ══════════════════════════════════════════════════════════════

        private async Task ScanDirectoryAsync(string directoryPath)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            IsScanning = true;

            // 切换到新目录 → 清空旧文件帧缩略图缓存 + 大图预览缓存
            _thumbnailCache.Clear();
            _thumbnailCacheOrder.Clear();
            _previewCache.Clear();
            _previewCacheOrder.Clear();

            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                LogService.FileOp($"KeyPhoto scan started: '{directoryPath}'");

                // 阶段 1：文件发现。仅检测单文件实况（JPEG XMP / HEIC 视频轨），
                // 双文件配对不放这里——靠文件名碰运气不严谨，统一在 Phase 2 用 ContentIdentifier 严格匹配。
                var discoveryResult = await Task.Run(
                    () => LivePhotoDiscoveryService.ScanAsync(
                        directoryPath,
                        DiscoveryScanMode.JpegMarkers | DiscoveryScanMode.HeicTrack, token),
                    token);

                if (token.IsCancellationRequested) return;

                // 分离图片和视频：列表只显示图片，视频路径单独收集供 Phase 2 CID 匹配
                // 预建视频大小查找表，双文件实况照片的 FileSize 需合并图片+视频
                var videoSizeLookup = discoveryResult.Items
                    .Where(d => SupportedVideoExtensions.Contains(Path.GetExtension(d.FilePath)))
                    .ToDictionary(d => d.FilePath, d => d.FileSizeBytes, StringComparer.OrdinalIgnoreCase);

                var files = discoveryResult.Items
                    .Where(d => !SupportedVideoExtensions.Contains(Path.GetExtension(d.FilePath)))
                    .Select(d =>
                    {
                        bool confirmed = d.LivePhotoType is LivePhotoType.SingleFileJpeg
                            or LivePhotoType.SingleFileHeic;
                        // 双文件实况照片：计算图片+视频的合并大小
                        long totalBytes = d.FileSizeBytes;
                        if (!string.IsNullOrEmpty(d.PairedVideoPath)
                            && videoSizeLookup.TryGetValue(d.PairedVideoPath, out long vidBytes))
                        {
                            totalBytes += vidBytes;
                        }

                        // 协议检测：单文件实况照片在此阶段即可确定协议
                        var protocol = LivePhotoProtocolType.Unknown;
                        if (confirmed)
                        {
                            try
                            {
                                protocol = LivePhotoProtocolDetector.Detect(
                                    d.FilePath, d.LivePhotoType, d.ContentIdentifier);
                            }
                            catch (Exception ex)
                            {
                                LogService.Scan(
                                    $"Protocol detection failed for '{Path.GetFileName(d.FilePath)}': {ex.Message}",
                                    LogLevel.Warning);
                            }
                        }

                        return new EditFileItem
                        {
                            FileName = Path.GetFileName(d.FilePath),
                            FilePath = d.FilePath,
                            FileSize = FileSizeFormatter.Format(totalBytes),
                            DateTaken = d.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                            Resolution = string.Empty,
                            LivePhotoType = d.LivePhotoType,
                            PairedVideoPath = d.PairedVideoPath,
                            AppendedVideoLength = d.AppendedVideoLength,
                            DetectionMethod = d.DetectionMethod,
                            DetectedProtocol = protocol,
                        };
                    }).ToList();

                var videoPaths = discoveryResult.Items
                    .Where(d => SupportedVideoExtensions.Contains(Path.GetExtension(d.FilePath)))
                    .Select(d => d.FilePath)
                    .ToList();

                int singleJpegCount = files.Count(f => f.LivePhotoType == LivePhotoType.SingleFileJpeg);
                int singleHeicCount = files.Count(f => f.LivePhotoType == LivePhotoType.SingleFileHeic);
                int confirmedCount = singleJpegCount + singleHeicCount;
                LogService.FileOp($"KeyPhoto scan done: {files.Count} images + {videoPaths.Count} videos, " +
                    $"SingleFileJpeg={singleJpegCount}, SingleFileHeic={singleHeicCount}, " +
                    $"Confirmed={confirmedCount}, Unclassified={files.Count - confirmedCount}");

                // ── 阶段 1.5: 同名快速定位 vivo 双文件 ──
                // 文件名只用于缩小查找范围；必须由图片与视频内部的 vivo ID 匹配确认。
                // Apple 留到 Phase 2，通过两边 ContentIdentifier 严格匹配。
                if (videoPaths.Count > 0)
                {
                    var vidByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var vp in videoPaths)
                    {
                        string baseName = Path.GetFileNameWithoutExtension(vp);
                        vidByBase[baseName] = vp;
                    }
                    int pairedCount = 0;
                    foreach (var file in files)
                    {
                        if (file.LivePhotoType != LivePhotoType.None) continue;
                        string baseName = Path.GetFileNameWithoutExtension(file.FilePath);
                        if (vidByBase.TryGetValue(baseName, out var vidPath))
                        {
                            var vivoMatch = LivePhotoMetadataMatcher.MatchVivo(
                                [file.FilePath], [vidPath]);
                            if (vivoMatch.Pairs.Count == 0)
                                continue;

                            file.LivePhotoType = LivePhotoType.DualFile;
                            file.PairedVideoPath = vidPath;
                            file.DetectionMethod = LivePhotoDetectionMethod.VivoLivePhoto;
                            file.DetectedProtocol = LivePhotoProtocolType.Vivo;
                            videoPaths.Remove(vidPath);
                            vidByBase.Remove(baseName);
                            pairedCount++;
                        }
                    }
                    if (pairedCount > 0)
                        LogService.FileOp(
                            $"KeyPhoto scan: metadata-confirmed {pairedCount} same-name vivo pair(s)",
                            LogLevel.Info);
                }

                _allFileItems = files;
                RefreshCounts();
                OnPropertyChanged(nameof(HasFilesLoaded));

                ThumbnailService.ClearCache();
                ClearFileInfo();

                LogService.FileOp($"KeyPhoto scan phase 1: {files.Count} images ({LivePhotoCount} live photos) in '{directoryPath}'");

                // 阶段 2：二进制读宽高+日期 + ContentIdentifier 配对确认
                if (files.Count > 0)
                {
                    await ReadResolutionsAsync(files, videoPaths, token);
                }

                ApplySortAndFilter();
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp("KeyPhoto scan cancelled", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"KeyPhoto scan failed: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                IsScanning = false;
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        /// <summary>
        /// 混合模式读取元数据 + ContentIdentifier 严格配对。
        ///   Phase 1 — C# 读文件头二进制取宽高+日期（失败文件记录，Phase 2 exiftool 兜底）。
        ///   Phase 2 — exiftool 为读取失败项补宽高+日期；仅对同名图片/视频候选读取
        ///             ContentIdentifier，并要求文件名与 UUID 同时匹配。
        /// </summary>
        private async Task ReadResolutionsAsync(List<EditFileItem> files, List<string> videoPaths, CancellationToken token)
        {
            await ReadRebuiltResolutionsAsync(files, token).ConfigureAwait(false);
        }
#if false

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return;

            // ═══════════════════════════════════════════════════════════
            //  Phase 1: C# 二进制读宽高 + EXIF 日期（不走 exiftool）
            // ═══════════════════════════════════════════════════════════
            var p1Sw = System.Diagnostics.Stopwatch.StartNew();
            int fastOk = 0, fastFail = 0, dateSuccess = 0;
            int ioParallelism = Math.Min(Environment.ProcessorCount, 8);

            // 记录 C# 读宽高失败的文件，Phase 2 用 exiftool 兜底
            var fallbackFiles = new ConcurrentBag<(int Index, string Path)>();

            await Task.Run(() =>
            {
                Parallel.ForEach(files, new ParallelOptions
                {
                    MaxDegreeOfParallelism = ioParallelism,
                    CancellationToken = token
                }, file =>
                {
                    var (w, h, date) = FastMetadataReader.Read(file.FilePath);
                    if (w > 0 && h > 0)
                    {
                        file.Resolution = $"{w} × {h}";
                        Interlocked.Increment(ref fastOk);
                    }
                    else
                    {
                        Interlocked.Increment(ref fastFail);
                        // HEIC 等 FastMetadataReader 不支持的格式 → 记录索引供 exiftool 兜底
                        int idx = files.IndexOf(file);
                        if (idx >= 0) fallbackFiles.Add((idx, file.FilePath));
                    }

                    if (!string.IsNullOrWhiteSpace(date))
                    {
                        if (DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                        {
                            file.DateTaken = dt.ToString("yyyy/MM/dd HH:mm");
                            Interlocked.Increment(ref dateSuccess);
                        }
                    }
                });
            }, token);

            LogService.FileOp(
                $"KeyPhoto Phase1 (C# binary): {fastOk} ok, {fastFail} fail ({fallbackFiles.Count} fallback), " +
                $"{dateSuccess} dates, {p1Sw.ElapsedMilliseconds}ms");

            if (token.IsCancellationRequested) return;

            // ═══════════════════════════════════════════════════════════
            //  Phase 2: exiftool 补宽高日期，并只查询同名双文件候选的 ContentIdentifier
            // ═══════════════════════════════════════════════════════════
            // 未分类图片（LivePhotoType == None）—— Phase 2 CID 匹配候选
            var unclassifiedImgs = new List<(int Index, string Path)>();
            // 单文件 HEIC 实况照片（已有内嵌视频轨确认，但缺外部 MOV 配对）—— 也需 CID 匹配
            var heicToPair = new List<(int Index, string Path)>();
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i].LivePhotoType == LivePhotoType.None)
                    unclassifiedImgs.Add((i, files[i].FilePath));
                else if (files[i].LivePhotoType == LivePhotoType.SingleFileHeic
                         && string.IsNullOrEmpty(files[i].PairedVideoPath))
                    heicToPair.Add((i, files[i].FilePath));
            }

            // 文件名是双文件协议的第一道必要条件。先 O(n) 建同名索引，后续只为这些
            // 候选读取 CID，避免为目录内所有普通文件读取协议标识。
            var videoByBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string videoPath in videoPaths)
                videoByBaseName.TryAdd(Path.GetFileNameWithoutExtension(videoPath), videoPath);

            var cidCandidateImages = unclassifiedImgs
                .Concat(heicToPair)
                .Where(entry => videoByBaseName.ContainsKey(
                    Path.GetFileNameWithoutExtension(entry.Path)))
                .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var cidCandidateVideos = cidCandidateImages
                .Select(entry => videoByBaseName[Path.GetFileNameWithoutExtension(entry.Path)])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((path, index) => (Index: index, Path: path))
                .ToList();

            bool needImgQuery = fallbackFiles.Count > 0 || cidCandidateImages.Count > 0;
            bool needVidQuery = videoPaths.Count > 0;

            if (needImgQuery || needVidQuery)
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool()
                    ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");

                if (!File.Exists(exifToolPath))
                {
                    LogService.FileOp("KeyPhoto Phase2: exiftool not found, skipping", LogLevel.Warning);
                    return;
                }

                var p2Sw = System.Diagnostics.Stopwatch.StartNew();
                const int batchSize = 100;
                int poolSize = ExifToolPoolSize;
                var pool = new List<PersistentExifTool>(poolSize);
                try
                {
                    for (int i = 0; i < poolSize; i++)
                    {
                        var tool = new PersistentExifTool(exifToolPath);
                        int toolIdx = i;
                        tool.OnRestarted += (msg) => dispatcher.TryEnqueue(() =>
                            LogService.FileOp($"[KeyPhoto CID exiftool#{toolIdx}] {msg}", LogLevel.Warning));
                        pool.Add(tool);
                    }

                    // ── 查询图片：仅为 Phase 1 失败项补宽高日期 ──
                    var imgResults = new Dictionary<string, (int W, int H, string? Date, string? Cid)>(StringComparer.OrdinalIgnoreCase);
                    {
                        // CID 在后面只针对同名候选单独查询，这里不读取普通图片的协议标识。
                        var toQuery = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (idx, path) in fallbackFiles) toQuery.TryAdd(path, idx);
                        var queryList = toQuery.Select(kv => (kv.Value, kv.Key)).ToList();

                        var batches = BuildBatches(queryList, batchSize);
                        var sem = new SemaphoreSlim(poolSize);
                        var tasks = new List<Task>();
                        int done = 0;

                        LogService.FileOp($"KeyPhoto Phase2 img query: {queryList.Count} files in {batches.Count} batches");

                        for (int bi = 0; bi < batches.Count; bi++)
                        {
                            if (token.IsCancellationRequested) break;
                            await sem.WaitAsync(token);
                            int batchIdx = bi;
                            var batch = batches[bi];
                            var tool = pool[batchIdx % poolSize];

                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    var args = new List<string>(batch.Count + 6)
                                        { "-j", "-ImageWidth", "-ImageHeight", "-DateTimeOriginal" };
                                    foreach (var f in batch) args.Add(f.Path);
                                    string json = await tool.SendCommandAsync(token, args.ToArray());
                                    var results = ParseExifInfoBatch(json);
                                    lock (imgResults)
                                    {
                                        foreach (var (_, path) in batch)
                                        {
                                            if (results.TryGetValue(path, out var info))
                                                imgResults[path] = (info.Width, info.Height, info.DateTaken, info.ContentIdentifier);
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex) { LogService.FileOp($"img query failed: {ex.Message}", LogLevel.Warning); }
                                finally
                                {
                                    sem.Release();
                                    var d = Interlocked.Increment(ref done);
                                    if (d % 10 == 0 || d == batches.Count)
                                        LogService.FileOp($"KeyPhoto CID images: {d}/{batches.Count} batches");
                                }
                            }, token));
                        }
                        await Task.WhenAll(tasks);
                    }

                    if (token.IsCancellationRequested) return;

                    // ── Phase 1 兜底：用 exiftool 结果填充宽高+日期 ──
                    int fallbackOk = 0;
                    foreach (var (idx, path) in fallbackFiles)
                    {
                        if (imgResults.TryGetValue(path, out var info) && info.W > 0 && info.H > 0)
                        {
                            files[idx].Resolution = $"{info.W} × {info.H}";
                            fallbackOk++;
                        }
                        if (!string.IsNullOrWhiteSpace(info.Date))
                        {
                            if (DateTime.TryParseExact(info.Date, "yyyy:MM:dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
                                files[idx].DateTaken = dt.ToString("yyyy/MM/dd HH:mm");
                        }
                    }
                    if (fallbackOk > 0)
                        LogService.FileOp($"KeyPhoto fallback (exiftool): {fallbackOk} resolved");

                    // ── 查询视频宽高（CID 仅对同名候选另查）──
                    var vidResults = new Dictionary<string, (int W, int H, string? Cid)>(StringComparer.OrdinalIgnoreCase);
                    if (needVidQuery)
                    {
                        var vidEntries = videoPaths.Select((p, i) => (Index: i, Path: p)).ToList();
                        var batches = BuildBatches(vidEntries, batchSize);
                        var sem = new SemaphoreSlim(poolSize);
                        var tasks = new List<Task>();
                        int done = 0;

                        LogService.FileOp($"KeyPhoto Phase2 vid query: {videoPaths.Count} files in {batches.Count} batches");

                        for (int bi = 0; bi < batches.Count; bi++)
                        {
                            if (token.IsCancellationRequested) break;
                            await sem.WaitAsync(token);
                            int batchIdx = bi;
                            var batch = batches[bi];
                            var tool = pool[batchIdx % poolSize];

                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    var args = new List<string>(batch.Count + 4)
                                        { "-j", "-ImageWidth", "-ImageHeight" };
                                    foreach (var f in batch) args.Add(f.Path);
                                    string json = await tool.SendCommandAsync(token, args.ToArray());
                                    var results = ParseExifInfoBatch(json);
                                    lock (vidResults)
                                    {
                                        foreach (var (_, path) in batch)
                                        {
                                            if (results.TryGetValue(path, out var info))
                                                vidResults[path] = (info.Width, info.Height, info.ContentIdentifier);
                                        }
                                    }
                                }
                                catch (OperationCanceledException) { }
                                catch (Exception ex) { LogService.FileOp($"vid query failed: {ex.Message}", LogLevel.Warning); }
                                finally
                                {
                                    sem.Release();
                                    var d = Interlocked.Increment(ref done);
                                    if (d % 10 == 0 || d == batches.Count)
                                        LogService.FileOp($"KeyPhoto CID videos: {d}/{batches.Count} batches");
                                }
                            }, token));
                        }
                        await Task.WhenAll(tasks);
                    }

                    if (token.IsCancellationRequested) return;

                    // ── 仅查询同名候选的 ContentIdentifier ──
                    var candidateImageCids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    var candidateVideoCids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    if (cidCandidateImages.Count > 0)
                    {
                        await RunCidBatchAsync(
                            pool, cidCandidateImages, batchSize, candidateImageCids,
                            "KeyPhoto CID candidate img", token);
                        await RunCidBatchAsync(
                            pool, cidCandidateVideos, batchSize, candidateVideoCids,
                            "KeyPhoto CID candidate vid", token);
                    }

                    var matchedVideoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    bool TryGetSameNameAppleVideo(string imagePath, out string videoPath)
                    {
                        videoPath = string.Empty;
                        string baseName = Path.GetFileNameWithoutExtension(imagePath);
                        if (!videoByBaseName.TryGetValue(baseName, out string? candidateVideo)
                            || !candidateImageCids.TryGetValue(imagePath, out string? imageCid)
                            || !candidateVideoCids.TryGetValue(candidateVideo, out string? videoCid)
                            || string.IsNullOrWhiteSpace(imageCid)
                            || string.IsNullOrWhiteSpace(videoCid)
                            || !imageCid.Equals(videoCid, StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        videoPath = candidateVideo;
                        return true;
                    }

                    int liveConfirmed = 0;
                    foreach (var (index, imgPath) in unclassifiedImgs)
                    {
                        if (TryGetSameNameAppleVideo(imgPath, out string matchedVideoPath))
                        {
                            // 文件名 + CID 同时匹配 → 完整的 Apple 实况照片对
                            files[index].LivePhotoType = LivePhotoType.DualFile;
                            files[index].PairedVideoPath = matchedVideoPath;
                            files[index].HasConfirmedProtocol = true;
                            files[index].DetectedProtocol = LivePhotoProtocolType.Apple;
                            files[index].DetectionMethod = LivePhotoDetectionMethod.ContentIdentifier;
                            // 更新文件大小为图片+视频合并值
                            files[index].FileSize = FileSizeFormatter.Format(
                                new FileInfo(files[index].FilePath).Length + new FileInfo(matchedVideoPath).Length);
                            matchedVideoPaths.Add(matchedVideoPath);
                            liveConfirmed++;
                        }
                        // 仅 CID 相同或仅文件名相同都不认定为实况照片。
                    }

                    // ── SingleFileHeic 补 CID 配对 ──
                    // Phase 1 的 HeicTrack 检测已确认 HEIC 内嵌视频轨（HasConfirmedProtocol=true），
                    // 但同目录 MOV 的 ContentIdentifier 未匹配。此处用 CID 再次配对，
                    // 把 LivePhotoType 从 SingleFileHeic 升级为 DualFile，写入 PairedVideoPath。
                    int heicPaired = 0;
                    foreach (var (index, imgPath) in heicToPair)
                    {
                        // 跳过已识别为其他协议的文件（如华为 HEIC 有 LIVE_ 尾标），
                        // 它们不是 Apple 格式，不该被 CID 匹配改写成 DualFile
                        if (files[index].DetectedProtocol != LivePhotoProtocolType.Unknown
                            && files[index].DetectedProtocol != LivePhotoProtocolType.Apple)
                            continue;

                        if (TryGetSameNameAppleVideo(imgPath, out string matchedVideoPath))
                        {
                            files[index].LivePhotoType = LivePhotoType.DualFile;
                            files[index].PairedVideoPath = matchedVideoPath;
                            files[index].DetectedProtocol = LivePhotoProtocolType.Apple;
                            files[index].DetectionMethod = LivePhotoDetectionMethod.ContentIdentifier;
                            // 更新文件大小为图片+视频合并值
                            files[index].FileSize = FileSizeFormatter.Format(
                                new FileInfo(files[index].FilePath).Length + new FileInfo(matchedVideoPath).Length);
                            // HasConfirmedProtocol 已在 Phase 1 设为 true，不重复计数
                            matchedVideoPaths.Add(matchedVideoPath);
                            heicPaired++;
                        }
                    }

                    if (heicPaired > 0)
                        LogService.FileOp($"KeyPhoto CID heic→dual: {heicPaired} HEIC paired with MOV");

                    // ── 未匹配视频也加入列表显示（全部作为普通文件）──
                    int addedVids = 0;
                    foreach (var vPath in videoPaths)
                    {
                        if (matchedVideoPaths.Contains(vPath)) continue; // 已匹配的不重复显示
                        vidResults.TryGetValue(vPath, out var vInfo);
                        files.Add(new EditFileItem
                        {
                            FileName = Path.GetFileName(vPath),
                            FilePath = vPath,
                            FileSize = FileSizeFormatter.Format(new FileInfo(vPath).Length),
                            DateTaken = new FileInfo(vPath).LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                            Resolution = vInfo.W > 0 && vInfo.H > 0 ? $"{vInfo.W} × {vInfo.H}" : string.Empty,
                            LivePhotoType = LivePhotoType.None,
                            HasConfirmedProtocol = false,
                            DetectedProtocol = LivePhotoProtocolType.Unknown,
                            DetectionMethod = LivePhotoDetectionMethod.FilenamePairing,
                        });
                        addedVids++;
                    }

                    if (liveConfirmed > 0)
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            RefreshCounts();
                        });
                    }

                    LogService.FileOp(
                        $"KeyPhoto Phase2 done: {liveConfirmed} pairs + {addedVids} standalone videos, " +
                        $"{p2Sw.ElapsedMilliseconds}ms");
                }
                finally
                {
                    foreach (var t in pool) try { t.Dispose(); } catch { }
                }
            }

            int resSuccess = files.Count(f => !string.IsNullOrEmpty(f.Resolution));
            LogService.FileOp(
                $"KeyPhoto resolution done: {resSuccess} success, {files.Count - resSuccess} failed " +
                $"(out of {files.Count})");
        }
#endif

        /// <summary>将文件列表按 batchSize 分批，供 exiftool 批量查询使用。</summary>
        private static List<List<(int Index, string Path)>> BuildBatches(
            List<(int Index, string Path)> items, int batchSize)
        {
            var batches = new List<List<(int Index, string Path)>>();
            for (int start = 0; start < items.Count; start += batchSize)
            {
                int end = Math.Min(start + batchSize, items.Count);
                batches.Add(items.GetRange(start, end - start));
            }
            return batches;
        }

        /// <summary>
        /// 解析 exiftool 批量查询返回的 JSON 数组，按 SourceFile 路径建立索引。
        /// exiftool 批量命令返回 [{SourceFile, ImageWidth, ImageHeight, ...}, ...]，
        /// 一次解析即可获取整批所有文件的元数据，避免逐文件解析的开销。
        /// </summary>
        private static Dictionary<string, (int Width, int Height, string? DateTaken, string? ContentIdentifier)>
            ParseExifInfoBatch(string json)
        {
            var result = new Dictionary<string, (int, int, string?, string?)>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return result;

            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string? sourceFile = null;
                    if (item.TryGetProperty("SourceFile", out var sf) && sf.ValueKind == JsonValueKind.String)
                        sourceFile = sf.GetString();
                    if (string.IsNullOrWhiteSpace(sourceFile)) continue;
                    // exiftool 返回 /，Windows 路径用 \，统一为反斜杠
                    sourceFile = sourceFile.Replace('/', '\\');

                    int w = 0, h = 0;
                    if (item.TryGetProperty("ImageWidth", out var wp)) w = ParseIntFromJson(wp);
                    if (item.TryGetProperty("ImageHeight", out var hp)) h = ParseIntFromJson(hp);

                    string? dateTaken = null;
                    if (item.TryGetProperty("DateTimeOriginal", out var dto) && dto.ValueKind == JsonValueKind.String)
                    {
                        var raw = dto.GetString();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
                            {
                                dateTaken = dt.ToString("yyyy/MM/dd HH:mm");
                            }
                        }
                    }

                    string? cid = GetJsonStr(item, "ContentIdentifier");
                    result[sourceFile] = (w, h, dateTaken, cid);
                }
            }
            catch { }

            return result;
        }

        private static (int width, int height, string? dateTaken, string? contentIdentifier) ParseExifInfo(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return (0, 0, null, null);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];
                int w = 0, h = 0;
                if (root.TryGetProperty("ImageWidth", out var wp)) w = ParseIntFromJson(wp);
                if (root.TryGetProperty("ImageHeight", out var hp)) h = ParseIntFromJson(hp);

                // 解析 EXIF DateTimeOriginal，格式化为 "yyyy/MM/dd HH:mm"
                string? dateTaken = null;
                if (root.TryGetProperty("DateTimeOriginal", out var dto) && dto.ValueKind == JsonValueKind.String)
                {
                    var raw = dto.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                        {
                            dateTaken = dt.ToString("yyyy/MM/dd HH:mm");
                        }
                    }
                }

                // ContentIdentifier — Apple Live Photo UUID（仅 HEIC/Apple 文件有此字段）
                string? cid = GetJsonStr(root, "ContentIdentifier");

                return (w, h, dateTaken, cid);
            }
            catch { return (0, 0, null, null); }
        }

        // ══════════════════════════════════════════════════════════════
        //  格式化工具方法
        // ══════════════════════════════════════════════════════════════

        private static bool IsImageOrVideo(string path) =>
            IsSupportedImageExtension(Path.GetExtension(path)) ||
            SupportedVideoExtensions.Contains(Path.GetExtension(path));

        private static int ParseIntFromJson(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out var n) ? n : (int)e.GetDouble(),
            JsonValueKind.String => int.TryParse(e.GetString(), out var s) ? s : 0,
            _ => 0
        };

        private static string? GetJsonStr(JsonElement root, string key) =>
            root.TryGetProperty(key, out var p)
                ? p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString(),
                    JsonValueKind.Number => p.GetRawText(),
                    _ => null
                }
                : null;

        private static int GetJsonInt(JsonElement root, string key) =>
            root.TryGetProperty(key, out var p) ? ParseIntFromJson(p) : 0;

        /// <summary>FFmpeg 风格的编码器名 → 可读名称</summary>
        private static string CodecToDisplay(string codec)
        {
            var c = codec.ToLowerInvariant();
            if (c.Contains("hvc") || c.Contains("hev")) return "H.265";
            if (c.Contains("avc") || c.Contains("h264")) return "H.264";
            if (c.Contains("mp4v")) return "MPEG-4";
            return codec;
        }

        /// <summary>exiftool 的 "x.xx s" 或数字字符串 → 格式化快门</summary>
        private static string FormatExposureTime(string val)
        {
            val = val.Trim();
            if (double.TryParse(val, out var d) && d < 1 && d > 0)
                return $"1/{Math.Round(1.0 / d)} s";
            return $"{val} s";
        }

        /// <summary>exiftool 的 "xx mm" 或数字 → "xx mm"</summary>
        private static string FormatFocalLength(string val)
        {
            val = val.Trim().TrimEnd('m', 'M', ' ');
            if (double.TryParse(val, out var d))
                return $"{d:F0} mm";
            return $"{val} mm";
        }

        /// <summary>exiftool 的 f-number 格式化</summary>
        private static string FormatFNumber(string val)
        {
            val = val.Trim();
            if (double.TryParse(val, out var d))
                return $"f/{d:F2}";
            return $"f/{val}";
        }

        /// <summary>镜头类型关键词 → 资源键（按优先级排列，先匹配先生效）</summary>
        private static readonly (string[] Keywords, string ResourceKey)[] LensTypeMap =
        {
            (new[] { "ultrawide", "ultra wide" }, "EditPage_Lens_UltraWide"),
            (new[] { "wide" },                  "EditPage_Lens_Wide"),
            (new[] { "telephoto" },             "EditPage_Lens_Telephoto"),
            (new[] { "tele" },                  "EditPage_Lens_Telephoto"),
            (new[] { "macro" },                 "EditPage_Lens_Macro"),
            (new[] { "main" },                  "EditPage_Lens_Main"),
            (new[] { "periscope" },             "EditPage_Lens_Periscope"),
            (new[] { "depth", "portrait" },     "EditPage_Lens_Depth"),
        };

        /// <summary>在 LensModel 字符串中按优先级匹配镜头类型关键词</summary>
        private static string? MatchLensType(string lens)
        {
            foreach (var (keywords, resourceKey) in LensTypeMap)
            {
                foreach (var kw in keywords)
                {
                    if (lens.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        return ResourceService.GetString(resourceKey);
                }
            }
            return null;
        }

        /// <summary>根据 LensModel / SensorType / ZoomMultiple 解析摄像头位置（后置/前置 + 类型）。
        /// 无任何镜头信息时返回空字符串。</summary>
        private static string GetCameraPosition(ExifProperties p)
        {
            string? lens = p.LensModel;
            bool hasLensInfo = !string.IsNullOrWhiteSpace(lens)
                || !string.IsNullOrWhiteSpace(p.SensorType)
                || p.ZoomMultiple > 0;

            if (!hasLensInfo)
                return string.Empty;

            bool isFront = (!string.IsNullOrWhiteSpace(lens) && lens.Contains("front", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(p.SensorType) && p.SensorType.Equals("front", StringComparison.OrdinalIgnoreCase));

            string position = isFront
                ? ResourceService.GetString("EditPage_Lens_Front")
                : ResourceService.GetString("EditPage_Lens_Rear");

            string? type = lens != null ? MatchLensType(lens) : null;
            if (type == null && p.ZoomMultiple > 0)
            {
                type = p.ZoomMultiple switch
                {
                    <= 1 => ResourceService.GetString("EditPage_Lens_Main"),
                    2 or 3 => ResourceService.GetString("EditPage_Lens_Telephoto"),
                    _ => ResourceService.GetString("EditPage_Lens_Periscope")
                };
            }

            return type != null ? $"{position}{type}" : $"{position}{ResourceService.GetString("EditPage_Lens_Camera")}";
        }

        /// <summary>根据 LensModel / 小米 SensorType+ZoomMultiple / 焦段+光圈构建镜头描述。</summary>
        private static string BuildLensDisplayName(ExifProperties p)
        {
            string? lens = p.LensModel;

            bool hasLensInfo = !string.IsNullOrWhiteSpace(lens)
                || !string.IsNullOrWhiteSpace(p.SensorType)
                || !string.IsNullOrWhiteSpace(p.FocalLengthIn35mmFormat)
                || !string.IsNullOrWhiteSpace(p.FNumber);

            if (!hasLensInfo)
                return ResourceService.GetString("EditPage_UnknownCamera");

            // ── 位置：LensModel 关键词 → 小米 SensorType → 默认后置 ──
            bool isFront = (!string.IsNullOrWhiteSpace(lens) && lens.Contains("front", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(p.SensorType) && p.SensorType.Equals("front", StringComparison.OrdinalIgnoreCase));

            string position = isFront
                ? ResourceService.GetString("EditPage_Lens_Front")
                : ResourceService.GetString("EditPage_Lens_Rear");

            // ── 类型：LensModel 关键词 → 小米 ZoomMultiple ──
            string? type = lens != null ? MatchLensType(lens) : null;

            if (type == null && p.ZoomMultiple > 0)
            {
                type = p.ZoomMultiple switch
                {
                    <= 1 => ResourceService.GetString("EditPage_Lens_Main"),
                    2 or 3 => ResourceService.GetString("EditPage_Lens_Telephoto"),
                    _ => ResourceService.GetString("EditPage_Lens_Periscope")
                };
            }

            var parts = new List<string>
            {
                type != null ? $"{position}{type}" : $"{position}{ResourceService.GetString("EditPage_Lens_Camera")}"
            };

            if (!string.IsNullOrWhiteSpace(p.FocalLengthIn35mmFormat))
                parts.Add(FormatFocalLength(p.FocalLengthIn35mmFormat));
            if (!string.IsNullOrWhiteSpace(p.FNumber))
                parts.Add(FormatFNumber(p.FNumber));

            return string.Join(" · ", parts);
        }

        /// <summary>exiftool 日期 "2024:12:15 14:32:00" → "2024/12/15 14:32"</summary>
        private static string FormatDateTime(string val)
        {
            if (DateTime.TryParseExact(val.Trim(), "yyyy:MM:dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
                return dt.ToString("yyyy/MM/dd HH:mm");
            return val.Trim().Length >= 16 ? val.Trim()[..16] : val.Trim();
        }

        /// <summary>协议类型 → 资源键映射（供 UI 显示用）</summary>
        private static readonly Dictionary<LivePhotoProtocolType, string> ProtocolResourceMap = new()
        {
            [LivePhotoProtocolType.Apple]    = "EditPage_Protocol_Apple",
            [LivePhotoProtocolType.GoogleV1] = "EditPage_Protocol_GoogleV1",
            [LivePhotoProtocolType.GoogleV2] = "EditPage_Protocol_GoogleV2",
            [LivePhotoProtocolType.OPPO]     = "EditPage_Protocol_OPPO",
            [LivePhotoProtocolType.Vivo]     = "EditPage_Protocol_Vivo",
            [LivePhotoProtocolType.Samsung]  = "EditPage_Protocol_Samsung",
            [LivePhotoProtocolType.Huawei]   = "EditPage_Protocol_Huawei",
            // Fusion 隐藏：暂时不在编辑页显示融合协议名。
            // [LivePhotoProtocolType.Fusion]   = "EditPage_Protocol_Fusion",
        };

        /// <summary>
        /// 根据 LivePhotoType + 文件内容，使用优先级检测确定协议显示名。
        /// 检测优先级：私有尾标（华为/三星）→ 厂商 XMP（Fusion/OPPO/vivo/小米）→ 通用 XMP（V1/V2）→ 双文件（Apple/vivo 旧格式）
        /// </summary>
        private static string? GetProtocolName(LivePhotoType type, string? filePath,
            string? contentIdentifier = null)
        {
            if (type == LivePhotoType.None || string.IsNullOrWhiteSpace(filePath))
                return ResourceService.GetString("EditPage_Protocol_NonLive");

            try
            {
                var protocol = LivePhotoProtocolDetector.Detect(filePath, type, contentIdentifier);
                if (protocol != LivePhotoProtocolType.Unknown
                    && ProtocolResourceMap.TryGetValue(protocol, out var resourceKey))
                    return ResourceService.GetString(resourceKey);
            }
            catch { }

            // 兜底：按文件类型给一个粗略描述
            return type switch
            {
                LivePhotoType.SingleFileJpeg => ResourceService.GetString("EditPage_Protocol_JpegGeneric"),
                LivePhotoType.SingleFileHeic => ResourceService.GetString("EditPage_Protocol_HeicEmbedded"),
                LivePhotoType.DualFile => ResourceService.GetString("EditPage_Protocol_NonLive"),
                _ => ResourceService.GetString("EditPage_Protocol_NonLive"),
            };
        }

        /// <summary>获取照片部分的大小（单文件实况照片需扣除视频段）</summary>
        private static string GetPhotoSizeDisplay(EditFileItem? item)
        {
            if (item == null) return "—";
            if (item.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic
                && item.AppendedVideoLength > 0)
            {
                try
                {
                    var totalBytes = new FileInfo(item.FilePath).Length;
                    var photoBytes = totalBytes - item.AppendedVideoLength;
                    if (photoBytes > 0)
                        return FileSizeFormatter.Format(photoBytes);
                }
                catch { }
            }
            return item.FileSize;
        }

        /// <summary>exiftool DMS → 十进制度数。DMS 里可能自带方向字母，ref 仅在没有时才用。</summary>
        private static double? DmsToDecimal(string? dms, string? refStr)
        {
            if (string.IsNullOrWhiteSpace(dms)) return null;
            try
            {
                // 去除 "deg"、多余空格，保留数字和方向字母
                string cleaned = dms.Replace("deg", "°").Trim();
                // 检查末尾是否有方向字母 (N/S/E/W)，有则用它决定正负
                char last = cleaned[cleaned.Length - 1];
                bool negative = false;
                if (last == 'N' || last == 'n') { cleaned = cleaned[..^1]; }
                else if (last == 'S' || last == 's') { cleaned = cleaned[..^1]; negative = true; }
                else if (last == 'E' || last == 'e') { cleaned = cleaned[..^1]; }
                else if (last == 'W' || last == 'w') { cleaned = cleaned[..^1]; negative = true; }
                else if (!string.IsNullOrWhiteSpace(refStr))
                {
                    negative = refStr.Trim().Equals("S", StringComparison.OrdinalIgnoreCase)
                            || refStr.Trim().Equals("W", StringComparison.OrdinalIgnoreCase);
                }

                var parts = cleaned.Split('°', '\'', '"');
                double deg = 0, min = 0, sec = 0;
                if (parts.Length > 0) double.TryParse(parts[0].Trim(), out deg);
                if (parts.Length > 1) double.TryParse(parts[1].Trim(), out min);
                if (parts.Length > 2) double.TryParse(parts[2].Trim(), out sec);
                double result = deg + min / 60.0 + sec / 3600.0;
                return negative ? -result : result;
            }
            catch { return null; }
        }

        /// <summary>反向地理编码：Mirror Earth 主 API → Nominatim OSM 兜底。单次调用内跑完两段。</summary>
        private async Task TriggerGeoLookupAsync(double lat, double lon, string filePath)
        {
            // 位置查询被用户关闭 → 显示"已禁用"提示
            if (!AppSettingsService.GetValue("IsGeoLocationEnabled", true))
            {
                var dq = App.MainWindow?.DispatcherQueue;
                dq?.TryEnqueue(() => ExifPlaceName = ResourceService.GetString("EditPage_GeoLocationDisabled"));
                return;
            }

            // 取消旧的待处理请求
            _geoCts?.Cancel();
            _geoCts = new CancellationTokenSource();
            var token = _geoCts.Token;

            long now = DateTime.UtcNow.Ticks;
            long elapsed = now - _lastGeoRequestTicks;
            long delayTicks = GeoCooldownTicks - elapsed;

            if (delayTicks > 0)
            {
                try { await Task.Delay((int)(delayTicks / TimeSpan.TicksPerMillisecond), token); }
                catch (TaskCanceledException) { return; }
            }

            if (token.IsCancellationRequested) return;
            _lastGeoRequestTicks = DateTime.UtcNow.Ticks;

            string lang = LivePhotoBox.Services.LanguageService.IsChineseUi() ? "zh" : "en";

            //  中文用户 → Mirror Earth 主（返回中文），Nominatim 兜底
            //  英文/其他 → Nominatim 主（尊重 accept-language），Mirror Earth 兜底
            //  主 API 重试 3 次，兜底重试 2 次
            if (lang == "zh")
            {
                bool ok = await TryGeoApiAsync(
                    $"https://api.mirror-earth.com/nominatim/reverse?lat={lat:F6}&lon={lon:F6}&format=jsonv2&accept-language={lang}",
                    token, apiName: "MirrorEarth", maxRetries: 3);
                if (ok) return;

                ok = await TryGeoApiAsync(
                    $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat:F6}&lon={lon:F6}&zoom=10&accept-language={lang}",
                    token, apiName: "Nominatim", maxRetries: 2);
                if (ok) return;
            }
            else
            {
                bool ok = await TryGeoApiAsync(
                    $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat:F6}&lon={lon:F6}&zoom=10&accept-language={lang}",
                    token, apiName: "Nominatim", maxRetries: 3);
                if (ok) return;

                ok = await TryGeoApiAsync(
                    $"https://api.mirror-earth.com/nominatim/reverse?lat={lat:F6}&lon={lon:F6}&format=jsonv2&accept-language={lang}",
                    token, apiName: "MirrorEarth", maxRetries: 2);
                if (ok) return;
            }

            // 两个 API 都失败 → 显示"无位置信息"
            var dq2 = App.MainWindow?.DispatcherQueue;
            dq2?.TryEnqueue(() => ExifPlaceName = ResourceService.GetString("EditPage_NoLocation"));
        }

        /// <summary>调用一次逆地理编码 API，成功时更新 ExifPlaceName。</summary>
        /// <param name="apiName">API 名称（用于日志区分，如 "Nominatim" / "MirrorEarth"）</param>
        /// <param name="maxRetries">最多尝试次数（含首次，默认 2）</param>
        /// <returns>true = 成功获取地名；false = 网络错误或空结果。</returns>
        private async Task<bool> TryGeoApiAsync(string url, CancellationToken token, string? apiName = null, int maxRetries = 2)
        {
            string? result = null;
            Exception? lastError = null;

            for (int attempt = 0; attempt < maxRetries && result == null; attempt++)
            {
                if (token.IsCancellationRequested) return false;
                try
                {
                    using var handler = new System.Net.Http.HttpClientHandler();
                    using var client = new System.Net.Http.HttpClient(handler);
                    client.DefaultRequestHeaders.Add("User-Agent", "LivePhotoBox/2.0");
                    client.Timeout = TimeSpan.FromSeconds(8);
                    var json = await client.GetStringAsync(url, token);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        result = doc.RootElement.TryGetProperty("display_name", out var dn)
                            ? dn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(result))
                            lastError = null; // 成功 → 清除错误
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return false; // 用户切文件导致的取消 → 静默退出
                }
                catch (OperationCanceledException)
                {
                    // token 未取消但抛了取消异常 → HttpClient 超时/连接拒绝
                    lastError = new TimeoutException($"HTTP request timed out after {8}s");
                    if (attempt < maxRetries - 1)
                        await Task.Delay(1000, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < maxRetries - 1)
                        await Task.Delay(1000, CancellationToken.None);
                }
            }

            if (token.IsCancellationRequested) return false;

            if (!string.IsNullOrWhiteSpace(result))
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                dispatcher?.TryEnqueue(() => ExifPlaceName = result);
                return true;
            }

            if (lastError != null)
            {
                LogService.FileOp($"Geo lookup [{apiName ?? "?"}] failed: {lastError.Message}", LogLevel.Warning);
            }
            // else: API 正常返回但无 display_name → 由调用方决定兜底策略
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        //  拖拽单文件加载（右侧面板 Drop）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 加载从右侧面板拖入的文件（支持同时拖入多个）。
        /// 自动通过 LivePhotoDiscoveryService 检测实况照片配对：
        ///   - 照片+视频配对成功 → 以照片为主项加入列表（LIVE 徽标），视频跳过
        ///   - 未配对 → 各自作为普通文件加入
        ///   - 单文件实况 → 直接标记
        /// 最后选中第一个加入的文件。
        /// </summary>
        /// <returns>第一个新文件的路径，用于 View 层触发 ListView 选中；无新文件返回 null</returns>
        public async Task<string?> LoadDroppedFilesAsync(List<string> filePaths)
        {
            if (filePaths.Count == 0) return null;

            return await LoadDroppedFilesRebuiltAsync(filePaths);
        }
#if false
            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) return null;

                // ── Step 1: 扫描目录（单文件实况检测）──
                var dirs = filePaths
                    .Select(p => Path.GetDirectoryName(p) ?? "")
                    .Where(d => Directory.Exists(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                bool autoPair = AppSettingsService.GetValue("IsDragDropAutoPairEnabled", false);

                var discoveryMap = new Dictionary<string, LivePhotoDiscoveryItem>(StringComparer.OrdinalIgnoreCase);
                if (autoPair)
                {
                    // 自动配对开启 → 全目录扫描（供 CID 配对索引使用）
                    foreach (var dir in dirs)
                    {
                        try
                        {
                            var result = await Task.Run(() =>
                                LivePhotoDiscoveryService.ScanAsync(dir,
                                    DiscoveryScanMode.JpegMarkers | DiscoveryScanMode.HeicTrack));
                            foreach (var di in result.Items)
                                discoveryMap[di.FilePath] = di;
                        }
                        catch (Exception ex)
                        {
                            LogService.FileOp($"Drop[Scan] Failed for '{dir}': {ex.Message}", LogLevel.Warning);
                        }
                    }
                }
                else
                {
                    // 自动配对关闭 → 仅检测拖入的文件本身（不扫目录）
                    foreach (var fp in filePaths)
                    {
                        if (!File.Exists(fp)) continue;
                        string ext = Path.GetExtension(fp);
                        bool isJpeg = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                                   || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
                        if (!isJpeg) continue;

                        long fileSize = new FileInfo(fp).Length;
                        if (!LivePhotoSplitScanService.IsLikelyLivePhoto(fp, fileSize))
                            continue;

                        long videoLen = 0;
                        try
                        {
                            var metaText = LivePhotoSplitService.ReadMetadataTextSync(fp);
                            videoLen = LivePhotoSplitService.GetAppendedVideoLength(metaText);
                        }
                        catch { videoLen = 0; }

                        discoveryMap[fp] = new LivePhotoDiscoveryItem
                        {
                            FilePath = fp,
                            FileSizeBytes = fileSize,
                            LivePhotoType = LivePhotoType.SingleFileJpeg,
                            DetectionMethod = LivePhotoDetectionMethod.JpegByteMarkers,
                            AppendedVideoLength = videoLen > 0 ? videoLen : 0,
                        };
                    }

                    // HEIC 实况照片检测：华为 LIVE_ 尾标 或 Google V2 XMP 标记
                    foreach (var fp in filePaths)
                    {
                        if (!File.Exists(fp)) continue;
                        string ext = Path.GetExtension(fp);
                        bool isHeic = ext.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                                   || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);
                        if (!isHeic) continue;

                        // 检查华为格式（LIVE_ 尾标 + 嵌入 MP4）
                        var hwRange = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(fp);
                        if (hwRange != null)
                        {
                            discoveryMap[fp] = new LivePhotoDiscoveryItem
                            {
                                FilePath = fp,
                                FileSizeBytes = new FileInfo(fp).Length,
                                LivePhotoType = LivePhotoType.SingleFileHeic,
                                DetectionMethod = LivePhotoDetectionMethod.HeicVideoTrack,
                                AppendedVideoLength = hwRange.Value.videoLength,
                            };
                            continue;
                        }

                        // 检查 Google V2 / Samsung HEIC（XMP MotionPhoto 标记 + mpvd box）
                        // 没有 LIVE_ 尾标也不嵌入 ftypmp42，但 XMP 里有 GCamera:MotionPhoto
                        try
                        {
                            string xmpText = LivePhotoSplitService.ReadMetadataTextSync(fp);
                            if (xmpText.Contains("GCamera:MotionPhoto", StringComparison.Ordinal) ||
                                xmpText.Contains("Container:Directory", StringComparison.Ordinal) ||
                                xmpText.Contains("GContainer:Directory", StringComparison.Ordinal))
                            {
                                long fileSize = new FileInfo(fp).Length;
                                discoveryMap[fp] = new LivePhotoDiscoveryItem
                                {
                                    FilePath = fp,
                                    FileSizeBytes = fileSize,
                                    LivePhotoType = LivePhotoType.SingleFileHeic,
                                    DetectionMethod = LivePhotoDetectionMethod.HeicVideoTrack,
                                    AppendedVideoLength = 0,
                                };
                            }
                        }
                        catch { /* best-effort */ }
                    }
                }

                string? exifToolPath = ExternalToolLocator.FindExifTool()
                    ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
                bool hasExifTool = File.Exists(exifToolPath);

                // ── Step 2: 拖入文件之间快速定位同名候选（不依赖文件夹扫描） ──
                // 文件名只用于把核验范围缩小到一对文件；Apple 必须两边 CID 一致，
                // vivo 必须两边 com.android.camera.livephoto ID 一致，未知协议绝不配对。
                var dropCandidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // image→video
                var confirmedDropPairs = new Dictionary<string,
                    (string VideoPath, LivePhotoProtocolType Protocol)>(StringComparer.OrdinalIgnoreCase);
                var confirmedDropVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                {
                    var imgsByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var vidsByBase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var fp in filePaths)
                    {
                        if (!File.Exists(fp)) continue;
                        string baseName = Path.GetFileNameWithoutExtension(fp);
                        string ext = Path.GetExtension(fp);
                        if (IsSupportedImageExtension(ext))
                            imgsByBase[baseName] = fp;
                        else if (SupportedVideoExtensions.Contains(ext))
                            vidsByBase[baseName] = fp;
                    }
                    foreach (var (baseName, imgPath) in imgsByBase)
                    {
                        if (vidsByBase.TryGetValue(baseName, out var vidPath))
                        {
                            dropCandidates[imgPath] = vidPath;
                        }
                    }
                }

                foreach (var (imgPath, vidPath) in dropCandidates)
                {
                    // 已确认的单文件实况不能被旁边的同名普通视频覆盖。
                    if (discoveryMap.TryGetValue(imgPath, out var singleMatch)
                        && singleMatch.LivePhotoType is LivePhotoType.SingleFileJpeg
                            or LivePhotoType.SingleFileHeic)
                    {
                        continue;
                    }

                    LivePhotoProtocolType pairProtocol = LivePhotoProtocolType.Unknown;
                    try
                    {
                        pairProtocol = await LivePhotoMetadataMatcher.DetectDualFileProtocolAsync(
                            imgPath, vidPath, hasExifTool ? exifToolPath : null,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"Drop[Pair] metadata validation failed for '{Path.GetFileName(imgPath)}': {ex.Message}",
                            LogLevel.Warning);
                    }

                    if (pairProtocol != LivePhotoProtocolType.Unknown)
                    {
                        confirmedDropPairs[imgPath] = (vidPath, pairProtocol);
                        confirmedDropVideos.Add(vidPath);
                    }
                }

                if (confirmedDropPairs.Count > 0)
                    LogService.FileOp(
                        $"Drop[Pair] Metadata-confirmed {confirmedDropPairs.Count}/{dropCandidates.Count} " +
                        "same-name dual-file pair(s) within dropped batch",
                        LogLevel.Info);

                // ── Step 3: 处理每个拖入文件 ──
                var toAdd = new List<EditFileItem>();
                var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rawPath in filePaths)
                {
                    if (!File.Exists(rawPath) || addedPaths.Contains(rawPath)) continue;

                    // 只有协议元数据已确认时，视频才由图片主项代表。
                    if (confirmedDropVideos.Contains(rawPath)) continue;

                    var filePath = rawPath;
                    var fileName = Path.GetFileName(filePath);
                    var dir = Path.GetDirectoryName(filePath) ?? "";
                    var ext = Path.GetExtension(filePath);

                    LivePhotoType detectedType = LivePhotoType.None;
                    LivePhotoDetectionMethod detectionMethod = LivePhotoDetectionMethod.FilenamePairing;
                    string? pairedVideoPath = null;
                    long appendedVideoLength = 0;
                    bool confirmed = false;
                    var protocol = LivePhotoProtocolType.Unknown;

                    bool isVideo = SupportedVideoExtensions.Contains(ext);
                    bool isImage = IsSupportedImageExtension(ext);

                    // ── 拖入批内已通过协议元数据确认的双文件配对 ──
                    if (isImage && confirmedDropPairs.TryGetValue(filePath, out var dropPair))
                    {
                        detectedType = LivePhotoType.DualFile;
                        pairedVideoPath = dropPair.VideoPath;
                        protocol = dropPair.Protocol;
                        detectionMethod = protocol == LivePhotoProtocolType.Vivo
                            ? LivePhotoDetectionMethod.VivoLivePhoto
                            : LivePhotoDetectionMethod.ContentIdentifier;
                        confirmed = true;
                        LogService.FileOp(
                            $"Drop[Pair] {protocol} metadata matched: {Path.GetFileName(filePath)} ↔ " +
                            Path.GetFileName(dropPair.VideoPath),
                            LogLevel.Info);
                    }
                    if (detectedType == LivePhotoType.None && discoveryMap.TryGetValue(filePath, out var match))
                    {
                        detectedType = match.LivePhotoType;
                        detectionMethod = match.DetectionMethod;
                        pairedVideoPath = match.PairedVideoPath;
                        appendedVideoLength = match.AppendedVideoLength;
                    }
                    // 视频侧的配对信息由照片侧主导（照片为主项）；视频本身作为独立文件添加时仅跳过

                    // ── 单文件实况已确认 ──
                    if (detectedType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic)
                        confirmed = true;

                    // ── 未分类 → 尝试双文件配对 ──
                    if (detectedType == LivePhotoType.None && hasExifTool && autoPair)
                    {
                        // 策略 1: 同名速查 — 同目录找同名异类文件，各读一次 CID O(1)
                        string? oppPath = FindOppositeTypeFile(dir, filePath, isImage, isVideo);
                        bool quickMatched = false;

                        if (oppPath != null)
                        {
                            var (myCid, oppCid) = await QueryTwoCidsAsync(exifToolPath, filePath, oppPath);
                            if (!string.IsNullOrWhiteSpace(myCid) &&
                                !string.IsNullOrWhiteSpace(oppCid) &&
                                myCid.Equals(oppCid, StringComparison.OrdinalIgnoreCase))
                            {
                                quickMatched = true;
                                protocol = LivePhotoProtocolType.Apple;
                                if (isImage)
                                {
                                    // 拖入照片 → 视频为配对文件
                                    pairedVideoPath = oppPath;
                                }
                                else if (isVideo)
                                {
                                    // 拖入视频 → 以照片为主项，视频为配对
                                    pairedVideoPath = rawPath;
                                    filePath = oppPath;
                                    fileName = Path.GetFileName(oppPath);
                                    isVideo = false;
                                }
                            }
                        }

                        if (quickMatched)
                        {
                            detectedType = LivePhotoType.DualFile;
                            detectionMethod = LivePhotoDetectionMethod.ContentIdentifier;
                            confirmed = true;
                        }
                    }

                    if (addedPaths.Contains(filePath)) continue;

                    // 双文件实况照片：合并图片+视频大小
                    long dropTotalBytes = new FileInfo(filePath).Length;
                    if (!string.IsNullOrEmpty(pairedVideoPath) && File.Exists(pairedVideoPath))
                        dropTotalBytes += new FileInfo(pairedVideoPath).Length;

                    // Protocol detection for non-CID files (single-file JPEG/HEIC)
                    if (confirmed && protocol == LivePhotoProtocolType.Unknown
                        && detectionMethod != LivePhotoDetectionMethod.ContentIdentifier)
                    {
                        try
                        {
                            protocol = LivePhotoProtocolDetector.Detect(
                                filePath, detectedType, contentIdentifier: null);
                        }
                        catch (Exception ex)
                        {
                            LogService.FileOp(
                                $"Drop protocol detection failed for '{fileName}': {ex.Message}",
                                LogLevel.Warning);
                        }
                    }

                    var item = new EditFileItem
                    {
                        FileName = fileName,
                        FilePath = filePath,
                        FileSize = FileSizeFormatter.Format(dropTotalBytes),
                        DateTaken = File.GetLastWriteTime(filePath).ToString("yyyy/MM/dd HH:mm"),
                        LivePhotoType = detectedType,
                        PairedVideoPath = pairedVideoPath,
                        AppendedVideoLength = appendedVideoLength,
                        DetectionMethod = detectionMethod,
                        DetectedProtocol = protocol,
                        Resolution = string.Empty
                    };

                    toAdd.Add(item);
                    addedPaths.Add(filePath);
                    if (pairedVideoPath != null) addedPaths.Add(pairedVideoPath);
                }

                if (toAdd.Count == 0) return null;

                // ── Step 3: 后台加载宽高日期 ──
                var vidPaths = toAdd
                    .Where(i => i.HasConfirmedProtocol && i.PairedVideoPath != null)
                    .Select(i => i.PairedVideoPath!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _ = ReadResolutionsAsync(toAdd, vidPaths, CancellationToken.None);

                // ── Step 4: UI 线程加入列表 ──
                var tcs = new TaskCompletionSource<string?>();
                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        string? firstNewPath = null;
                        foreach (var item in toAdd)
                        {
                            var existing = FileItems.FirstOrDefault(f =>
                                string.Equals(f.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase));
                            if (existing != null)
                            {
                                // 升级已有条目：之前可能是单独拖入（无配对），
                                // 现在同批拖入同名视频 → 升级为双文件实况照片
                                if (existing.LivePhotoType == LivePhotoType.None
                                    && item.LivePhotoType == LivePhotoType.DualFile)
                                {
                                    existing.LivePhotoType = item.LivePhotoType;
                                    existing.PairedVideoPath = item.PairedVideoPath;
                                    existing.HasConfirmedProtocol = item.HasConfirmedProtocol;
                                    existing.DetectionMethod = item.DetectionMethod;
                                    existing.DetectedProtocol = item.DetectedProtocol;
                                    LogService.FileOp(
                                        $"Drop[Pair] Upgraded existing item to dual-file: {Path.GetFileName(item.FilePath)}",
                                        LogLevel.Info);
                                }
                                if (firstNewPath == null) firstNewPath = item.FilePath;
                                continue;
                            }

                            _allFileItems.Insert(0, item);
                            FileItems.Insert(0, item);

                            if (firstNewPath == null) firstNewPath = item.FilePath;
                        }

                        RefreshCounts();
                        OnPropertyChanged(nameof(HasAnyFiles));
                        OnPropertyChanged(nameof(HasFilesLoaded));
                        tcs.TrySetResult(firstNewPath);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp($"Drop[Load] dispatch failed: {ex.Message}", LogLevel.Error, ex);
                        tcs.TrySetResult(null);
                    }
                });

                return await tcs.Task;
            }
            finally
            {
                IsScanning = false;
            }
        }
#endif

        private async Task<string?> LoadDroppedFilesRebuiltAsync(List<string> filePaths)
        {
            IsScanning = true;
            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) return null;

                var existingPaths = filePaths
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var images = existingPaths
                    .Where(p => IsSupportedImageExtension(Path.GetExtension(p)))
                    .ToList();
                var videos = existingPaths
                    .Where(p => SupportedVideoExtensions.Contains(Path.GetExtension(p)))
                    .ToList();
                bool autoPair = AppSettingsService.GetValue("IsDragDropAutoPairEnabled", false);
                var videoByBaseName = videos
                    .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                if (autoPair)
                {
                    foreach (string imagePath in images)
                    {
                        string? directory = Path.GetDirectoryName(imagePath);
                        if (directory == null) continue;
                        string baseName = Path.GetFileNameWithoutExtension(imagePath);
                        string? adjacentVideo = Directory.EnumerateFiles(directory)
                            .FirstOrDefault(p => SupportedVideoExtensions.Contains(Path.GetExtension(p)) &&
                                string.Equals(Path.GetFileNameWithoutExtension(p), baseName, StringComparison.OrdinalIgnoreCase));
                        if (adjacentVideo != null)
                            videoByBaseName.TryAdd(baseName, adjacentVideo);
                    }
                }

                var inspector = new SourceInspector();
                var pairedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pairedImages = new Dictionary<string, (string VideoPath, SourceProtocol Protocol)>(StringComparer.OrdinalIgnoreCase);
                foreach (string imagePath in images)
                {
                    string baseName = Path.GetFileNameWithoutExtension(imagePath);
                    if (!videoByBaseName.TryGetValue(baseName, out string? videoPath)) continue;
                    SourceProtocol protocol = SourceProtocol.Unknown;
                    try
                    {
                        var facts = await inspector.InspectAsync(imagePath, videoPath, CancellationToken.None).ConfigureAwait(false);
                        protocol = facts.Protocol;
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp($"Drop[Rebuilt] Native pair inspection failed: {ex.Message}", LogLevel.Warning);
                    }
                    if (protocol is SourceProtocol.AppleLivePhoto or SourceProtocol.VivoLegacyDualFile)
                    {
                        pairedImages[imagePath] = (videoPath, protocol);
                        pairedVideos.Add(videoPath);
                    }
                }

                var toAdd = new List<EditFileItem>();
                var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rawPath in existingPaths)
                {
                    if (addedPaths.Contains(rawPath) || pairedVideos.Contains(rawPath)) continue;
                    string ext = Path.GetExtension(rawPath);
                    bool isImage = IsSupportedImageExtension(ext);
                    LivePhotoType type = LivePhotoType.None;
                    LivePhotoDetectionMethod method = LivePhotoDetectionMethod.FilenamePairing;
                    string? pairedVideoPath = null;
                    long appendedVideoLength = 0;
                    SourceProtocol sourceProtocol = SourceProtocol.NonLive;

                    if (isImage && pairedImages.TryGetValue(rawPath, out var pair))
                    {
                        type = LivePhotoType.DualFile;
                        method = pair.Protocol == SourceProtocol.VivoLegacyDualFile
                            ? LivePhotoDetectionMethod.VivoLivePhoto
                            : LivePhotoDetectionMethod.ContentIdentifier;
                        pairedVideoPath = pair.VideoPath;
                        sourceProtocol = pair.Protocol;
                    }
                    else if (isImage)
                    {
                        try
                        {
                            var facts = await inspector.InspectAsync(rawPath, null, CancellationToken.None).ConfigureAwait(false);
                            sourceProtocol = facts.Protocol;
                            if (facts.MotionVideo?.IsPresent == true && sourceProtocol != SourceProtocol.NonLive)
                            {
                                type = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                                    ? LivePhotoType.SingleFileJpeg
                                    : LivePhotoType.SingleFileHeic;
                                method = type == LivePhotoType.SingleFileJpeg
                                    ? LivePhotoDetectionMethod.JpegByteMarkers
                                    : LivePhotoDetectionMethod.HeicVideoTrack;
                                appendedVideoLength = facts.MotionVideo.ByteLength;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.FileOp($"Drop[Rebuilt] Native inspection failed: {ex.Message}", LogLevel.Warning);
                        }
                    }

                    long totalBytes = new FileInfo(rawPath).Length;
                    if (pairedVideoPath != null && File.Exists(pairedVideoPath))
                        totalBytes += new FileInfo(pairedVideoPath).Length;
                    toAdd.Add(new EditFileItem
                    {
                        FileName = Path.GetFileName(rawPath),
                        FilePath = rawPath,
                        FileSize = FileSizeFormatter.Format(totalBytes),
                        DateTaken = File.GetLastWriteTime(rawPath).ToString("yyyy/MM/dd HH:mm"),
                        LivePhotoType = type,
                        PairedVideoPath = pairedVideoPath,
                        AppendedVideoLength = appendedVideoLength,
                        DetectionMethod = method,
                        DetectedProtocol = MapRebuiltProtocol(sourceProtocol),
                        Resolution = string.Empty
                    });
                    addedPaths.Add(rawPath);
                    if (pairedVideoPath != null) addedPaths.Add(pairedVideoPath);
                }

                if (toAdd.Count == 0) return null;
                _ = ReadResolutionsAsync(toAdd, [], CancellationToken.None);
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        string? firstNewPath = null;
                        foreach (var item in toAdd)
                        {
                            if (FileItems.Any(f => string.Equals(f.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            FileItems.Add(item);
                            _allFileItems.Add(item);
                            firstNewPath ??= item.FilePath;
                        }
                        RefreshCounts();
                        ApplySortAndFilter();
                        tcs.SetResult(firstNewPath);
                    }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                LogService.FileOp($"Drop[Rebuilt] failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
            finally
            {
                IsScanning = false;
            }
        }

        private static LivePhotoProtocolType MapRebuiltProtocol(SourceProtocol protocol) => protocol switch
        {
            SourceProtocol.AppleLivePhoto => LivePhotoProtocolType.Apple,
            SourceProtocol.GoogleMicroVideoV1 => LivePhotoProtocolType.GoogleV1,
            SourceProtocol.GoogleMotionPhotoV2 => LivePhotoProtocolType.GoogleV2,
            SourceProtocol.OppoLivePhoto => LivePhotoProtocolType.OPPO,
            SourceProtocol.VivoLivePhoto or SourceProtocol.VivoLegacyDualFile => LivePhotoProtocolType.Vivo,
            SourceProtocol.SamsungMotionPhotoJpeg or SourceProtocol.SamsungMotionPhotoHeic => LivePhotoProtocolType.Samsung,
            SourceProtocol.HuaweiMovingPhoto or SourceProtocol.HonorMovingPhoto => LivePhotoProtocolType.Huawei,
            _ => LivePhotoProtocolType.Unknown
        };

        private static async Task ReadRebuiltResolutionsAsync(
            List<EditFileItem> files, CancellationToken token)
        {
            await Task.Run(() =>
            {
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var (width, height, _) = FastMetadataReader.Read(file.FilePath);
                        if (width > 0 && height > 0)
                            file.Resolution = $"{width} × {height}";
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }, token).ConfigureAwait(false);
        }

        /// <summary>找同目录同名异类文件：JPG→MOV, MOV→JPG/HEIC。</summary>
        private static string? FindOppositeTypeFile(string dir, string filePath, bool isImage, bool isVideo)
        {
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            if (isImage)
            {
                foreach (var vExt in new[] { ".MOV", ".MP4" })
                {
                    var p = Path.Combine(dir, baseName + vExt);
                    if (File.Exists(p)) return p;
                }
            }
            else if (isVideo)
            {
                foreach (var iExt in new[] { ".JPG", ".JPEG", ".HEIC", ".HEIF" })
                {
                    var p = Path.Combine(dir, baseName + iExt);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

#if false
        /// <summary>快速查询两个文件的 ContentIdentifier（各一次 exiftool，O(1)）。</summary>
        private static async Task<(string? Cid1, string? Cid2)> QueryTwoCidsAsync(
            string exifToolPath, string path1, string path2)
        {
            try
            {
                using var tool = new PersistentExifTool(exifToolPath);
                var t1 = tool.SendCommandAsync(CancellationToken.None, "-j", "-ContentIdentifier", path1);
                var t2 = tool.SendCommandAsync(CancellationToken.None, "-j", "-ContentIdentifier", path2);
                await Task.WhenAll(t1, t2);

                static string? ExtractCid(string json)
                {
                    if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return null;
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.GetArrayLength() > 0 &&
                            root[0].TryGetProperty("ContentIdentifier", out var cid) &&
                            cid.ValueKind == JsonValueKind.String)
                            return cid.GetString();
                    }
                    catch { }
                    return null;
                }

                return (ExtractCid(t1.Result), ExtractCid(t2.Result));
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>批量查询文件的 ContentIdentifier，结果写入 dict。</summary>
        private static async Task RunCidBatchAsync(
            List<PersistentExifTool> pool,
            List<(int Index, string Path)> entries,
            int batchSize,
            Dictionary<string, string?> result,
            string logTag,
            CancellationToken token)
        {
            var batches = BuildBatches(entries, batchSize);
            var sem = new SemaphoreSlim(pool.Count);
            var tasks = new List<Task>();
            int done = 0;

            for (int bi = 0; bi < batches.Count; bi++)
            {
                if (token.IsCancellationRequested) break;
                await sem.WaitAsync(token);
                int batchIdx = bi;
                var batch = batches[bi];
                var tool = pool[batchIdx % pool.Count];

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var args = new List<string>(batch.Count + 3) { "-j", "-ContentIdentifier" };
                        foreach (var f in batch) args.Add(f.Path);
                        string json = await tool.SendCommandAsync(token, args.ToArray());
                        var parsed = ParseExifInfoBatch(json);
                        lock (result)
                        {
                            foreach (var (_, path) in batch)
                                result[path] = parsed.TryGetValue(path, out var info)
                                    ? info.ContentIdentifier : null;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        LogService.FileOp($"{logTag} batch failed: {ex.Message}", LogLevel.Warning);
                    }
                    finally
                    {
                        sem.Release();
                        var d = Interlocked.Increment(ref done);
                        if (d % 10 == 0 || d == batches.Count)
                            LogService.FileOp($"{logTag}: {d}/{batches.Count} batches");
                    }
                }, token));
            }

            await Task.WhenAll(tasks);
        }
#endif

        // ══════════════════════════════════════════════════════════════
        //  结构化属性（exiftool 解析结果）
        // ══════════════════════════════════════════════════════════════

        private class VideoProperties
        {
            public int Width, Height;
            public long FileSizeBytes;
            public string? AvgBitrate, CompressorID, MediaDuration, VideoFrameRate, PosterTime, Duration;
        }

        private class ExifProperties
        {
            public int ImageWidth, ImageHeight;
            public string? MimeType;
            public string? Camera;
            public string? FocalLength, FocalLengthIn35mmFormat, FNumber, ExposureTime, ExposureCompensation;
            public string? LensModel, HDRImageType;
            public string? SensorType;
            public int ZoomMultiple;
            public int ISO;
            public string? DateTimeOriginal;
            public string? GpsLatitude, GpsLatitudeRef, GpsLongitude, GpsLongitudeRef, GpsAltitude;
            public string? MediaDuration, AvgBitrate, CompressorID;
            public string? ContentIdentifier;
            public long MotionPhotoPresentationTimestampUs;
            public long MicroVideoPresentationTimestampUs;
        }
    }
}
