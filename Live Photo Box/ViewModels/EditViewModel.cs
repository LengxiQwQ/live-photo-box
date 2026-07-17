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
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace LivePhotoBox.ViewModels
{
    public partial class EditViewModel : ViewModelBase
    {
        // ══════════════════════════════════════════════════════════════
        //  支持的文件扩展名（图片 + 视频）
        // ══════════════════════════════════════════════════════════════
        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp"
        };

        private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mov", ".mp4"
        };

        /// <summary>exiftool 并行实例数（扫描阶段分辨率读取用）</summary>
        private const int ExifToolPoolSize = 2;

        // ══════════════════════════════════════════════════════════════
        //  构造函数 & 生命周期
        // ══════════════════════════════════════════════════════════════

        public EditViewModel()
        {
            // 从设置恢复静音状态（默认不静音）
            _isMuted = AppSettingsService.GetValue("IsLivePhotoMuted", false);
            // 进度前缀默认：导出帧
            ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
        }

        public override string? PageStatusTag => null;

        /// <summary>页面卸载时清理 exiftool 进程</summary>
        public void Cleanup()
        {
            _propLoadCts?.Cancel();
            _geoCts?.Cancel();
            _timelineCts?.Cancel();
            _exportCts?.Cancel();
            _exportCts?.Dispose();
            _completionCts?.Cancel();
            _completionCts?.Dispose();
            DisposeExifTool();
            CleanupFrameTempFiles();
            CleanupTempVideo();
            _previewCache.Clear();
            _previewCacheOrder.Clear();
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
        private int _selectedSortIndex;

        [ObservableProperty]
        private bool _isSortAscending = true;

        /// <summary>排序方向图标：升序 ↑ / 降序 ↓</summary>
        public string SortDirectionGlyph => IsSortAscending ? "" : "";

        /// <summary>实况照片总数（仅已确认协议的）</summary>
        [ObservableProperty]
        private int _livePhotoCount;

        /// <summary>其他文件数（非实况 + 未确认协议的）</summary>
        [ObservableProperty]
        private int _otherCount;

        partial void OnLivePhotoCountChanged(int value) { }
        partial void OnOtherCountChanged(int value) { }

        /// <summary>照片过滤：0=所有照片 / 1=实况照片 / 2=普通照片</summary>
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
        partial void OnSelectedFilePathChanged(string? value) => OnPropertyChanged(nameof(HasSelectedFile));

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
                1 => filtered.Where(f => f.HasConfirmedProtocol),       // 仅实况照片
                2 => filtered.Where(f => !f.HasConfirmedProtocol),     // 仅普通照片
                _ => filtered                                          // 所有照片
            };

            var dispatcher = App.MainWindow?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                FileItems.Clear();
                foreach (var f in filtered) FileItems.Add(f);
                OnPropertyChanged(nameof(HasAnyFiles));
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  选中文件信息（右下角信息面板绑定）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty] private string _photoFileName = string.Empty;
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

        /// <summary>视频帧率数值（fps），用于绑定 FpsDisplayText 计算</summary>
        private double _videoFps = 30.0;

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
        }

        /// <summary>时间轴帧提取取消令牌</summary>
        private CancellationTokenSource? _timelineCts;

        /// <summary>单文件实况照片的内嵌视频临时文件路径（帧提取完成后清理）</summary>
        private string? _tempVideoPath;

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

        /// <summary>导出选项对话框返回模型</summary>
        private sealed record ExportOptions(string FolderName, bool CopyExif, string ExportPath);

        /// <summary>
        /// 帧缩略图内存缓存：key = "filePath|frameKey", value = ImageSource。
        /// 已加载的缩略图驻留内存，切换回同一文件时瞬间显示（无需重新解码 HEIC 或重读 JPEG）。
        /// frameKey：⭐ 帧 = "star"，视频帧 = 帧序号（如 "3"）。
        /// </summary>
        private readonly Dictionary<string, ImageSource> _thumbnailCache = new();

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
        /// 星标帧（IsStillPhoto）已为封面 → 禁用；数字角标帧 → 可用。
        /// </summary>
        public bool IsSetCoverEnabled => SelectedTimelineFrame != null && !SelectedTimelineFrame.IsStillPhoto;

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
            OnPropertyChanged(nameof(IsSetCoverEnabled));

            // 更新帧位置文本
            if (value != null)
            {
                if (value.IsStillPhoto)
                {
                    // 封面帧：显示 "Cover · 共 N 帧"
                    var videoFrames = TimelineFrames.Where(f => !f.IsStillPhoto).ToList();
                    CurrentFramePositionText = ResourceService.Format(
                        "KeyPhoto_TimelineFrameKeyPhoto", videoFrames.Count);
                }
                else
                {
                    // 普通视频帧：排除封面帧后计算序号和总数
                    var videoFrames = TimelineFrames.Where(f => !f.IsStillPhoto).ToList();
                    int idx = videoFrames.IndexOf(value);
                    if (idx >= 0)
                    {
                        CurrentFramePositionText = ResourceService.Format(
                            "KeyPhoto_TimelineFramePosition", idx + 1, videoFrames.Count);
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
                // 用户手动点击/吸附选中 → 更新大图预览 + 同步 CurrentKeyFrame
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
                OnPropertyChanged(nameof(IsSetCoverEnabled));
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

        /// <summary>视频信息行可见（实况照片或有视频数据的独立视频时显示）</summary>
        public bool IsVideoRowVisible => IsSelectedFileVideo || IsSelectedLivePhoto;

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
            if (item.LivePhotoType == LivePhotoType.DualFile && item.HasConfirmedProtocol)
            {
                await SaveAppleAsync(frame, item, photoPath);
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
            var suggestedName = $"{photoBaseName}_封面帧{frame.FrameIndex + 1}";

            var savedFile = await FilePickerService.PickSaveFileForExportAsync(".JPG", suggestedName);
            if (savedFile == null)
            {
                LogService.FileOp("KeyPhoto Save: cancelled by user", LogLevel.Info);
                return; // 用户取消
            }
            string targetPath = savedFile.Path;

            // ── 3. 显示进度（复用标题栏进度条） ─────────────────────────
            // 清空上一次的完成状态和"正在导出帧…"前缀，改为纯"正在保存…"
            IsShowingSaveComplete = false;
            ProgressPrefixText = string.Empty;
            ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveInProgress");
            IsExporting = true;

            string? tempWorkDir = null;
            string? tempVideoPath = null;

            try
            {
                // ── 4. 检测协议（从 XMP 文本判断） ──────────────────────
                string metadataText = LivePhotoSplitService.ReadMetadataTextSync(photoPath);

                LivePhotoProtocol protocol;
                if (metadataText.Contains("OpCamera:", StringComparison.Ordinal))
                    protocol = LivePhotoProtocol.FromIndex(2); // OPPO O-Live Photo
                else if (metadataText.Contains("GCamera:MicroVideoOffset", StringComparison.Ordinal))
                    protocol = LivePhotoProtocol.FromIndex(0); // Google MicroVideo V1
                else
                    protocol = LivePhotoProtocol.FromIndex(1); // Google Motion Photo V2（兜底）

                LogService.FileOp(
                    $"KeyPhoto Save: protocol={protocol.Key}, frame=#{frame.FrameIndex} @ {frame.Timestamp}",
                    LogLevel.Info);

                // ── 5. 创建工作目录 ─────────────────────────────────────
                tempWorkDir = Path.Combine(Path.GetTempPath(), $"lpb_save_{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempWorkDir);

                // ── 6. 复制帧 JPEG 到工作目录，注入原图 EXIF ──────────
                string workImagePath = Path.Combine(tempWorkDir, $"frame_{Guid.NewGuid():N}.jpg");
                File.Copy(frame.FullFramePath, workImagePath, overwrite: true);

                // 从原图复制全部可用 EXIF 标签（-all:all），但显式排除 XMP 组
                // （因为后续 WriteNativeAsync 会写入全新的协议 XMP，旧 XMP 会冲突）
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

                LogService.FileOp("KeyPhoto Save: EXIF copied to frame JPEG", LogLevel.Info);

                // ── 7. 协议预处理（OPPO 注入 oplus_ EXIF 标记） ────────
                string processedImagePath = await protocol.PrepareImageAsync(
                    workImagePath, tempWorkDir, CancellationToken.None);

                // ── 8. 提取视频段到临时文件 ─────────────────────────────
                var fileSize = new FileInfo(photoPath).Length;
                long videoOffset = fileSize - item.AppendedVideoLength;

                tempVideoPath = Path.Combine(tempWorkDir, "video.mp4");
                using (var src = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    src.Position = videoOffset;
                    await src.CopyToAsync(dst);
                }

                long actualVideoSize = new FileInfo(tempVideoPath).Length;
                LogService.FileOp($"KeyPhoto Save: video extracted ({actualVideoSize} bytes)", LogLevel.Info);

                // ── 9. 构建协议 XMP（含选中帧时间戳）并合成到临时文件 ──
                long presentationTimestampUs = (long)(frame.Timestamp.TotalSeconds * 1_000_000);
                byte[] xmpBytes = protocol.BuildXmpMetadata(actualVideoSize, presentationTimestampUs);

                // 日志：输出生成的 XMP 文本（前 600 字符），便于排查时间戳是否写入
                string xmpText = System.Text.Encoding.UTF8.GetString(xmpBytes);
                LogService.FileOp(
                    $"KeyPhoto Save: XMP generated ({xmpText.Length} chars), " +
                    $"presentationTimestampUs={presentationTimestampUs}μs (≈{frame.Timestamp.TotalSeconds:F4}s). " +
                    $"XMP preview: [{xmpText[..Math.Min(xmpText.Length, 600)]}]",
                    LogLevel.Info);

                // 先写到临时文件，再用 WinRT API 复制到用户选择的路径
                // （直接 FileMode.Create 写入 FileSavePicker 返回的路径可能会因系统句柄导致 0 字节）
                string tempOutputPath = Path.Combine(tempWorkDir, "output.jpg");
                await LivePhotoMergeService.WriteNativeAsync(
                    processedImagePath, tempVideoPath, tempOutputPath, xmpBytes, CancellationToken.None);

                var tempFile = await StorageFile.GetFileFromPathAsync(tempOutputPath);
                await tempFile.CopyAndReplaceAsync(savedFile);

                LogService.FileOp($"KeyPhoto Save: combined file written to '{targetPath}'", LogLevel.Info);

                // ── 10. 验证：用 exiftool 回读刚保存文件的 PresentationTimestamp ──
                string? verifyExifPath = ExternalToolLocator.FindExifTool();
                if (!string.IsNullOrEmpty(verifyExifPath))
                {
                    try
                    {
                        long? readBackTimestamp = await ReadTimestampFromFileAsync(targetPath);
                        LogService.FileOp(
                            $"KeyPhoto Save: verify timestamp after write: " +
                            $"expect={presentationTimestampUs}, readback={(readBackTimestamp.HasValue ? readBackTimestamp.Value.ToString() : "N/A")}",
                            readBackTimestamp == presentationTimestampUs ? LogLevel.Info : LogLevel.Warning);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"KeyPhoto Save: timestamp verify failed: {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                IsModified = false;

                LogService.FileOp(
                    $"KeyPhoto Save SUCCESS: {Path.GetFileName(photoPath)} frame#{frame.FrameIndex} -> '{targetPath}'",
                    LogLevel.Info);

                                // 修改日期设为当前时间
                    try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                // ── 11. 完成消息：显示对号 + "保存完成"，停留 5 秒后自动消失 ──
                IsShowingSaveComplete = true;
                ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveComplete");
                _ = HoldSaveCompleteMessageAsync();
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                // 清理可能不完整的输出文件
                try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }

                // 失败时立即隐藏进度指示，弹出错误弹窗
                IsShowingSaveComplete = false;
                IsExporting = false;
                ExportProgressText = string.Empty;
                ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
                _ = ShowSaveErrorDialogAsync(
                    $"{ResourceService.GetString("KeyPhotoPage_SaveError")}: {ex.Message}",
                    Path.GetDirectoryName(targetPath));
            }
            finally
            {
                // 清理临时文件和工作目录
                if (!string.IsNullOrEmpty(tempWorkDir) && Directory.Exists(tempWorkDir))
                    try { Directory.Delete(tempWorkDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 用 exiftool 回读已保存文件的 PresentationTimestamp，验证写入正确。
        /// 返回微秒值，读取失败或标签不存在时返回 null。
        /// </summary>
        private static async Task<long?> ReadTimestampFromFileAsync(string filePath)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath)) return null;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-MotionPhotoPresentationTimestampUs -MicroVideoPresentationTimestampUs -s -s -S \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return null;

                string output = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(5000);

                // exiftool -s -s -S 输出格式：每行 "TagName: Value"
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("MotionPhotoPresentationTimestampUs:", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("MicroVideoPresentationTimestampUs:", StringComparison.OrdinalIgnoreCase))
                    {
                        var colonIdx = trimmed.IndexOf(':');
                        if (colonIdx >= 0 && long.TryParse(trimmed[(colonIdx + 1)..].Trim(), out long val))
                            return val;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存完成后显示"保存完成"消息，5 秒后自动清除进度指示。
        /// </summary>
        private async Task HoldSaveCompleteMessageAsync()
        {
            _completionCts?.Cancel();
            _completionCts?.Dispose();
            _completionCts = new CancellationTokenSource();
            var ct = _completionCts.Token;

            try
            {
                await Task.Delay(4000, ct);

                var dispatcher = App.MainWindow?.DispatcherQueue;
                dispatcher?.TryEnqueue(() =>
                {
                    IsShowingSaveComplete = false;
                    IsExporting = false;
                    ExportProgressText = string.Empty;
                    ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
                });
            }
            catch (TaskCanceledException) { /* 新的保存开始，已取消旧计时器 */ }
        }

        /// <summary>
        /// 保存/导出失败时显示错误弹窗。
        /// 用户可点击"打开输出目录"在资源管理器中打开目标文件夹，或"我知道了"关闭。
        /// </summary>
        /// <param name="errorMessage">错误描述文本</param>
        /// <param name="outputDir">可选：输出目录路径，用于"打开"按钮</param>
        private async Task ShowSaveErrorDialogAsync(string errorMessage, string? outputDir = null)
        {
            if (App.MainWindow?.Content?.XamlRoot is not XamlRoot xamlRoot)
            {
                LogService.FileOp("ShowSaveErrorDialog: MainWindow XamlRoot unavailable", LogLevel.Warning);
                return;
            }

            var openDir = await DialogService.ShowDualAsync(
                xamlRoot,
                ResourceService.GetString("KeyPhotoPage_SaveError"),
                errorMessage,
                primaryText: ResourceService.GetString("Msg_OpenOutputFolder"),
                closeText: ResourceService.GetString("Msg_GotIt"));

            if (openDir && !string.IsNullOrEmpty(outputDir) && Directory.Exists(outputDir))
            {
                FilePickerService.OpenFolderInExplorer(outputDir);
            }
        }

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
                string filenameTemplate = ResourceService.GetString("KeyPhotoPage_SaveAppleFilename");
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
                IsShowingSaveComplete = false;
                ProgressPrefixText = string.Empty;
                ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveInProgress");
                IsExporting = true;

                // ── 3b. 读取原 HEIC 的 ContentIdentifier（Apple 配对 UUID）──
                //     后续显式写回 HEIC 和 MOV，确保重新扫描时能识别为实况照片
                string? contentIdentifier = null;
                try
                {
                    string? exifPath = ExternalToolLocator.FindExifTool();
                    if (!string.IsNullOrEmpty(exifPath))
                    {
                        var cidPsi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exifPath,
                            Arguments = $"-j -ContentIdentifier \"{photoPath}\"",
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        };
                        using var cidProc = System.Diagnostics.Process.Start(cidPsi);
                        if (cidProc != null)
                        {
                            string cidJson = await cidProc.StandardOutput.ReadToEndAsync();
                            cidProc.WaitForExit(5000);
                            using var doc = JsonDocument.Parse(cidJson);
                            var root = doc.RootElement[0];
                            if (root.TryGetProperty("ContentIdentifier", out var cidEl))
                                contentIdentifier = cidEl.GetString();
                        }
                    }
                }
                catch { /* non-fatal */ }
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

                LogService.FileOp("KeyPhoto Save[Apple]: EXIF + MakerNote copied to frame JPEG", LogLevel.Info);

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
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: timestamp {presentationTimestampUs}μs written to JPEG XMP",
                        LogLevel.Info);
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: XMP timestamp write failed (non-fatal): {ex.Message}",
                        LogLevel.Warning);
                }

                // ── 7. heif-enc: JPEG → HEIC（libheif + x265，保留 EXIF） ──
                string tempHeicPath = Path.Combine(tempWorkDir, $"keyframe_{Guid.NewGuid():N}.heic");
                // -q 90 = 高质量（1-100），-p x265:preset=fast 平衡速度和质量
                string heifArgs = $"-o \"{tempHeicPath}\" -q 90 \"{enrichedJpegPath}\"";

                LogService.FileOp($"KeyPhoto Save[Apple]: heif-enc args: {heifArgs}", LogLevel.Info);

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
                        IsExporting = false;
                        ExportProgressText = string.Empty;
                        ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
                        return;
                    }

                    await proc.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

                    if (proc.ExitCode != 0)
                    {
                        string stderr = await proc.StandardError.ReadToEndAsync();
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: heif-enc exit={proc.ExitCode}, stderr: {stderr.Trim()}",
                            LogLevel.Error);
                        IsExporting = false;
                        ExportProgressText = string.Empty;
                        ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
                        return;
                    }
                }

                long heicSize = new FileInfo(tempHeicPath).Length;
                LogService.FileOp($"KeyPhoto Save[Apple]: HEIC encoded ({heicSize} bytes)", LogLevel.Info);
                if (heicSize == 0)
                {
                    LogService.FileOp("KeyPhoto Save[Apple]: HEIC is 0 bytes after heif-enc", LogLevel.Error);
                    IsExporting = false;
                    ExportProgressText = string.Empty;
                    ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
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
                    LogService.FileOp("KeyPhoto Save[Apple]: tags copied from JPEG -> HEIC", LogLevel.Info);
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

                LogService.FileOp($"KeyPhoto Save[Apple]: HEIC saved to '{targetHeicPath}'", LogLevel.Info);

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
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: ContentIdentifier written to HEIC: {contentIdentifier}",
                            LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: ContentIdentifier write to HEIC failed: {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                // ── 10. 静默复制 MOV 到同目录（同名覆盖，不做占位预留）──
                //     不用 PathHelper.GetUniqueFilePath——其 TryReservePath 会创建 0 字节
                //     占位文件，在 Windows Defender / Search Indexer 等系统组件竞争下，
                //     File.Copy 覆盖时可能因文件被外部短暂锁定而失败 → MOV 丢失。
                //     改用直接 Path.Combine + File.Copy(overwrite: true)，干净可靠。
                //     HEIC 文件名由用户通过 FileSavePicker 确认，MOV 同名伴随是最自然的行为。
                string targetDir = Path.GetDirectoryName(targetHeicPath)!;
                string movFileName = Path.GetFileNameWithoutExtension(targetHeicPath) + Path.GetExtension(pairedVideoPath);
                string targetMovPath = Path.Combine(targetDir, movFileName);

                try
                {
                    File.Copy(pairedVideoPath, targetMovPath, overwrite: true);
                    LogService.FileOp($"KeyPhoto Save[Apple]: MOV copied to '{targetMovPath}'", LogLevel.Info);
                    NotifyShellFileCreated(targetMovPath);
                }
                catch (Exception ex)
                {
                    // MOV 复制失败不阻断整体流程：HEIC 已成功保存，单独记录错误日志
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: MOV copy FAILED — {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Error, ex);
                }

                // ── 10b. 更新 MOV mebx 轨的封面时间 ──────────────────────
                // Apple 实况照片的封面时间存在 MOV 的 mebx 轨 edit list 中
                //（elst[0].trackDur ÷ mvhd.timescale = 封面在视频中的秒数）。
                // 复制了原始 MOV 后其值仍是旧的，用二进制 patch 直接改 elst 和 tkhd，
                // 不 remux、不重编码、毫秒级完成。
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
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: ContentIdentifier written to MOV: {contentIdentifier}",
                            LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp(
                            $"KeyPhoto Save[Apple]: ContentIdentifier write to MOV failed: {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                // ── 11. 验证 ─────────────────────────────────────────────
                try
                {
                    long? readBack = await ReadTimestampFromFileAsync(targetHeicPath);
                    LogService.FileOp(
                        $"KeyPhoto Save[Apple]: verify timestamp expect={presentationTimestampUs}, " +
                        $"readback={(readBack.HasValue ? readBack.Value.ToString() : "N/A")}",
                        readBack == presentationTimestampUs ? LogLevel.Info : LogLevel.Warning);
                }
                catch (Exception ex)
                {
                    LogService.FileOp($"KeyPhoto Save[Apple]: verify failed (non-fatal): {ex.Message}", LogLevel.Warning);
                }

                IsModified = false;

                LogService.FileOp(
                    $"KeyPhoto Save[Apple] SUCCESS: {Path.GetFileName(photoPath)} frame#{frame.FrameIndex + 1} " +
                    $"-> HEIC({heicSize}B) + MOV({new FileInfo(pairedVideoPath).Length}B)",
                    LogLevel.Info);

                                // 修改日期设为当前时间
                    try { File.SetLastWriteTime(targetHeicPath, DateTime.Now); } catch { }
                    try { File.SetLastWriteTime(targetMovPath, DateTime.Now); } catch { }

                // ── 12. 完成消息：对号 + "保存完成" → 4s 后消失 ─────────
                IsShowingSaveComplete = true;
                ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveComplete");
                _ = HoldSaveCompleteMessageAsync();
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhoto Save[Apple] FAILED: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);

                IsShowingSaveComplete = false;
                IsExporting = false;
                ExportProgressText = string.Empty;
                ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");

                // 提取目标目录（可能部分完成但报错，尽量提供目录位置）
                string? appleOutputDir = !string.IsNullOrEmpty(targetHeicPath)
                    ? Path.GetDirectoryName(targetHeicPath)
                    : null;
                _ = ShowSaveErrorDialogAsync(ex.Message, appleOutputDir);
            }
            finally
            {
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
                return;

            // 弹出另存为对话框保存照片
            var savedPath = await FilePickerService.SaveFileAsAsync(photoPath);
            if (savedPath == null) return; // 用户取消

            // 显示"正在保存…"状态
            IsShowingSaveComplete = false;
            ProgressPrefixText = string.Empty;
            ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveInProgress");
            IsExporting = true;

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

                // 完成状态
                IsShowingSaveComplete = true;
                ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveComplete");
                _ = HoldSaveCompleteMessageAsync();
            }
            catch (Exception ex)
            {
                LogService.FileOp($"SaveAs FAILED: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, ex);

                IsShowingSaveComplete = false;
                IsExporting = false;
                ExportProgressText = string.Empty;
                ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
                _ = ShowSaveErrorDialogAsync(ex.Message, Path.GetDirectoryName(savedPath));
            }
        }
        [RelayCommand] private void Export() { }
        /// <summary>
        /// 导出当前帧：先弹出系统"另存为"窗口让用户选择格式和位置，
        /// 选完后再按需转换（避免转换阻塞弹窗）。
        /// 视频帧已是 JPEG 直接复制；封面（⭐）若非 JPG 则提供原格式 + JPEG 选项，
        /// 选 JPEG 时以 quality=92 转换，文件大小合理。
        /// </summary>
        [RelayCommand]
        private async Task ExportCurrentFrame()
        {
            var frame = SelectedTimelineFrame;
            if (frame == null) return;

            // 1. 确定源文件路径和扩展名
            string sourcePath;
            string sourceExt;

            if (frame.IsStillPhoto)
            {
                var photoPath = SelectedFilePath;
                if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath)) return;
                sourcePath = photoPath;
                sourceExt = Path.GetExtension(photoPath);
            }
            else
            {
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    return;
                sourcePath = frame.FullFramePath;
                sourceExt = ".JPG"; // ffmpeg 提取的帧始终是 JPEG
            }

            // 2. 生成建议文件名
            var photoBaseName = Path.GetFileNameWithoutExtension(SelectedFilePath ?? "photo");
            var suggestedName = frame.IsStillPhoto
                ? photoBaseName
                : $"{photoBaseName}_帧{frame.FrameIndex + 1}";

            // 3. 弹出另存为窗口（先弹窗，不阻塞，用户可选手选格式）
            var targetFile = await FilePickerService.PickSaveFileForExportAsync(
                sourceExt, suggestedName);
            if (targetFile == null) return; // 用户取消

            // 4. 显示"正在保存…"状态
            string targetPath = targetFile.Path;
            IsShowingSaveComplete = false;
            ProgressPrefixText = string.Empty;
            ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveInProgress");
            IsExporting = true;

            // 5. 根据用户选择的格式执行导出
            bool targetIsJpeg = targetFile.FileType is ".jpg" or ".jpeg";
            string? tempConvertedPath = null;

            try
            {
                if (targetIsJpeg && HeicConverterService.IsHeicFile(sourcePath))
                {
                    // 用户选了 JPEG + 源是 HEIC → 转换后再复制（quality=92，文件大小合理）
                    tempConvertedPath = await HeicConverterService.ConvertToJpegAsync(
                        sourcePath, Path.GetTempPath(), quality: 92);
                    var jpegFile = await StorageFile.GetFileFromPathAsync(tempConvertedPath);
                    await jpegFile.CopyAndReplaceAsync(targetFile);
                }
                else
                {
                    // 原格式导出 / 源已是 JPEG → 直接复制
                    var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                    await sourceFile.CopyAndReplaceAsync(targetFile);
                }

                LogService.FileOp(
                    $"ExportCurrentFrame: {Path.GetFileName(sourcePath)} -> {targetFile.Path}",
                    LogLevel.Info);

                // 6. JPEG 导出：复制原图 EXIF（相机/日期/GPS等），但排除实况照片私有协议标签
                if (targetIsJpeg && !string.IsNullOrEmpty(SelectedFilePath)
                    && File.Exists(SelectedFilePath))
                {
                    await CopyExifForExportAsync(SelectedFilePath, targetFile.Path);
                }

                // 修改日期设为当前时间
                try { File.SetLastWriteTime(targetFile.Path, DateTime.Now); } catch { }

                // 完成状态
                IsShowingSaveComplete = true;
                ExportProgressText = ResourceService.GetString("KeyPhotoPage_SaveComplete");
                _ = HoldSaveCompleteMessageAsync();
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"ExportCurrentFrame failed: {ex.Message}", LogLevel.Error, ex);

                IsShowingSaveComplete = false;
                IsExporting = false;
                ExportProgressText = string.Empty;
                ProgressPrefixText = ResourceService.GetString("KeyPhotoPage_ExportProgressPrefixLabel");
                _ = ShowSaveErrorDialogAsync(ex.Message, Path.GetDirectoryName(targetPath));
            }
            finally
            {
                if (tempConvertedPath != null)
                {
                    try { File.Delete(tempConvertedPath); } catch { }
                }
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
            // 1. 防重入守卫
            if (IsExporting)
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

            IsExporting = true;
            ExportProgressText = $"0/{TimelineFrames.Count}";
            ExportProgressPercent = 0.0;

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
                        options.CopyExif, token, semaphore,
                        TimelineFrames.Count, counters));
                }

                await Task.WhenAll(tasks);

                // 8. 先清除进度显示，避免"完成了还在显示进度"的重复感
                ExportProgressText = string.Empty;
                ExportProgressPercent = 0.0;

                // 9. 汇总日志
                LogService.FileOp(
                    $"ExportAllFrames completed: {counters.Success} succeeded, {counters.Fail} failed -> '{exportDir}'",
                    counters.Fail > 0 ? LogLevel.Warning : LogLevel.Info);

                // 10. 显示结果对话框
                if (!token.IsCancellationRequested
                    && App.MainWindow?.Content?.XamlRoot is XamlRoot resultXamlRoot)
                {
                    var summaryText = ResourceService.Format(
                        "KeyPhotoPage_ExportComplete_Summary",
                        counters.Success, counters.Fail, TimelineFrames.Count);

                    var openFolder = await DialogService.ShowDualAsync(
                        resultXamlRoot,
                        ResourceService.GetString("KeyPhotoPage_ExportComplete_Title"),
                        summaryText,
                        primaryText: ResourceService.GetString("Msg_OpenOutputFolder"),
                        closeText: ResourceService.GetString("Msg_GotIt"));

                    // 用户点击"打开输出文件夹"→ 在资源管理器中打开
                    if (openFolder)
                    {
                        FilePickerService.OpenFolderInExplorer(exportDir);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp("ExportAllFrames cancelled mid-operation", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"ExportAllFrames fatal error: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                IsExporting = false;
                ExportProgressText = string.Empty;
                ExportProgressPercent = 0.0;
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
            var panel = new StackPanel { Spacing = 10 };

            // 描述文字：告诉用户会自动创建文件夹
            panel.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("KeyPhotoPage_ExportDialog_Description"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });

            // 导出位置：header + 路径文本框 + 文件夹图标按钮（Grid 保证文本框填满）
            panel.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("KeyPhotoPage_ExportDialog_FolderPathLabel"),
                FontSize = 13,
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
                ResourceService.GetString("KeyPhotoPage_ExportDialog_BrowseTip"));
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
                Text = ResourceService.GetString("KeyPhotoPage_ExportDialog_FolderNameLabel"),
                FontSize = 13,
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
                ResourceService.GetString("KeyPhotoPage_ExportDialog_ResetTip"));
            Grid.SetColumn(resetNameButton, 1);
            nameRow.Children.Add(resetNameButton);

            panel.Children.Add(nameRow);

            // EXIF 勾选框（默认勾选）
            var copyExifCheckBox = new CheckBox
            {
                Content = ResourceService.GetString("KeyPhotoPage_ExportDialog_CopyExifLabel"),
                IsChecked = true,
            };
            panel.Children.Add(copyExifCheckBox);

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("KeyPhotoPage_ExportDialog_Title"),
                Content = panel,
                PrimaryButtonText = ResourceService.GetString("KeyPhotoPage_ExportDialog_ExportBtn"),
                CloseButtonText = ResourceService.GetString("Msg_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme,
            };

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
                    errorText.Text = ResourceService.GetString("KeyPhotoPage_ExportDialog_PathInvalidError");
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
                        errorText.Text = ResourceService.GetString("KeyPhotoPage_ExportDialog_PathInvalidError");
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }
                    currentFolderPath = testPath;
                    errorText.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    errorText.Text = ResourceService.GetString("KeyPhotoPage_ExportDialog_PathInvalidError");
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
                return new ExportOptions(folderName, copyExif, currentFolderPath);
            }

            return null;
        }

        /// <summary>
        /// 在信号量约束下导出单帧到目标目录，并更新进度计数器。
        /// 可被多个任务并行调用，线程安全。
        /// </summary>
        private async Task ExportOneFrameAsync(
            TimelineFrame frame, string photoPath, string photoBaseName,
            string exportDir, bool copyExif, CancellationToken token,
            SemaphoreSlim semaphore, int totalFrames,
            ExportCounters counters)
        {
            string? tempConvertedPath = null;
            try
            {
                token.ThrowIfCancellationRequested();

                // 1. 确定源文件路径
                string sourcePath;

                if (frame.IsStillPhoto)
                {
                    sourcePath = photoPath; // 封面帧：原照片文件
                }
                else
                {
                    // 视频帧：ffmpeg 提取的全分辨率 JPEG
                    if (string.IsNullOrEmpty(frame.FullFramePath)
                        || !File.Exists(frame.FullFramePath))
                    {
                        Interlocked.Increment(ref counters.Fail);
                        LogService.FileOp(
                            $"ExportAllFrames: frame {frame.FrameIndex + 1} SKIP — source not found",
                            LogLevel.Warning);
                        return;
                    }
                    sourcePath = frame.FullFramePath;
                }

                // 2. 生成输出文件名
                var fileName = frame.IsStillPhoto
                    ? $"{photoBaseName}.jpg"
                    : $"{photoBaseName}_帧{frame.FrameIndex + 1}.jpg";

                // 3. 原子性预留不冲突的文件路径
                var targetPath = PathHelper.GetUniqueFilePath(exportDir, fileName);

                // 4. 执行复制/转换
                try
                {
                    if (frame.IsStillPhoto && HeicConverterService.IsHeicFile(sourcePath))
                    {
                        // HEIC 封面 → 转换为 JPEG
                        tempConvertedPath = await HeicConverterService.ConvertToJpegAsync(
                            sourcePath, Path.GetTempPath(), quality: 92, token);
                        File.Copy(tempConvertedPath, targetPath, overwrite: true);
                    }
                    else
                    {
                        // 直接复制：视频帧（已是 JPEG）或非 HEIC 封面
                        File.Copy(sourcePath, targetPath, overwrite: true);
                    }

                    // 5. 复制 EXIF（从原照片复制到导出文件）
                    if (copyExif && File.Exists(photoPath))
                    {
                        await CopyExifForExportAsync(photoPath, targetPath);
                    }

                    // 6. 修改日期设为当前时间（保留原始拍摄日期不变）
                    try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

                    Interlocked.Increment(ref counters.Success);
                    LogService.FileOp(
                        $"ExportAllFrames: frame {(frame.IsStillPhoto ? "⭐" : $"#{frame.FrameIndex + 1}")} -> {Path.GetFileName(targetPath)}",
                        LogLevel.Info);
                }
                finally
                {
                    // 清理 HEIC 转换临时文件
                    if (tempConvertedPath != null)
                    {
                        try { File.Delete(tempConvertedPath); } catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref counters.Fail);
                LogService.FileOp(
                    $"ExportAllFrames: frame {(frame.IsStillPhoto ? "⭐" : $"#{frame.FrameIndex + 1}")} FAILED: {ex.Message}",
                    LogLevel.Error, ex);
            }
            finally
            {
                int done = Interlocked.Increment(ref counters.Completed);

                // 通过 DispatcherQueue 更新 UI 进度（线程安全）
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    ExportProgressText = $"{done}/{totalFrames}";
                    ExportProgressPercent = (double)done / totalFrames * 100.0;
                });

                semaphore.Release();
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

        [RelayCommand] private void ConvertProtocol() { }

        /// <summary>
        /// 前往封面：滚动到星标帧（IsStillPhoto=true）。
        /// 复用首次加载实况照片时的程序化选中 + 滚动吸附管线。
        /// </summary>
        [RelayCommand]
        private void GotoCover()
        {
            var coverFrame = TimelineFrames.FirstOrDefault(f => f.IsStillPhoto);
            if (coverFrame != null)
            {
                // 复用 SelectTimelineFrameProgrammatically，
                // 确保即使已选中封面帧也会重新触发滚动
                SelectTimelineFrameProgrammatically(coverFrame);
            }
        }

        [RelayCommand] private void BrowseFolder() { }


        // ══════════════════════════════════════════════════════════════
        //  文件选中 → 加载属性
        // ══════════════════════════════════════════════════════════════

        /// <summary>属性加载取消令牌</summary>
        private CancellationTokenSource? _propLoadCts;

        /// <summary>反向地理编码速率限制：上次请求完成的时间戳</summary>
        private long _lastGeoRequestTicks;
        private const long GeoCooldownTicks = 12_000_000; // 1.2 秒 (Ticks)
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
            _exportCts?.Cancel();
            _previewLoadCts?.Cancel();
            CleanupFrameTempFiles();
            CleanupTempVideo();

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
                IsSelectedLivePhoto = item.HasConfirmedProtocol;
                PhotoFileName = EditFileItem.FormatDisplayFileName(
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

            // 时间轴：仅从实况切换到非实况时清空（实况之间保留旧帧防闪烁）
            if (wasLivePhoto && !IsSelectedLivePhoto)
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
                LogService.FileOp(
                    $"Timeline[SelectFile]: DualFile confirmed, videoPath='{videoPath}', exists={File.Exists(videoPath)}",
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
            LogService.Debug($"KeyPhoto SelectFile: type={item?.LivePhotoType}, videoPath={videoPath ?? "null"}, embeddedVideoLen={embeddedVideoLen}", LogSource.UI);
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

        /// <summary>属性查询用的 PersistentExifTool 单例（懒加载，一个足够）</summary>
        private PersistentExifTool? _propExifTool;

        private PersistentExifTool GetPropExifTool()
        {
            if (_propExifTool != null) return _propExifTool;

            string? exifToolPath = ExternalToolLocator.FindExifTool()
                ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
            if (!File.Exists(exifToolPath)) throw new InvalidOperationException("exiftool not found");

            _propExifTool = new PersistentExifTool(exifToolPath);
            _propExifTool.OnRestarted += (msg) =>
                LogService.FileOp($"[KeyPhoto exiftool] {msg}", LogLevel.Warning);
            return _propExifTool;
        }

        private void DisposeExifTool()
        {
            try { _propExifTool?.Dispose(); } catch { }
            _propExifTool = null;
        }

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
            LogService.Debug($"KeyPhoto LoadProperties: image='{Path.GetFileName(imagePath)}', video='{(videoPath != null ? Path.GetFileName(videoPath) : "none")}', embedded={embeddedVideoLen}", LogSource.UI);

            string? tempVideoPath = null;
            try
            {
                if (!IsImageOrVideo(imagePath))
                {
                    LogService.FileOp("Timeline[LoadProps] SKIP: not an image or video file", LogLevel.Warning);
                    return;
                }

                PersistentExifTool exifTool;
                try { exifTool = GetPropExifTool(); }
                catch (InvalidOperationException ex)
                {
                    LogService.FileOp($"Timeline[LoadProps] SKIP: exiftool not available: {ex.Message}", LogLevel.Error);
                    return;
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
                        $"Timeline[LoadProps] Embedded video extracted to temp: '{tempVideoPath}', " +
                        $"exists={File.Exists(tempVideoPath)}, size={new FileInfo(tempVideoPath).Length}",
                        LogLevel.Info);
                    LogService.Debug($"KeyPhoto embedded video extracted to temp: {tempVideoPath}", LogSource.UI);
                }

                // 构建照片查询
                var imgArgs = new string[PropTags.Length + 1];
                Array.Copy(PropTags, imgArgs, PropTags.Length);
                imgArgs[^1] = imagePath;

                // 并行查询照片 + 配对视频
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

                await Task.WhenAll(imgTask, vidTask);

                // 捕获 exiftool stderr（外部工具警告/错误）
                var stderr = exifTool.FlushStderr();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    LogService.FileOp(
                        $"Timeline[LoadProps] exiftool stderr: {stderr.Trim()}",
                        LogLevel.Warning);
                    LogService.Debug($"[KeyPhoto exiftool stderr] {stderr.Trim()}", LogSource.UI);
                }

                if (token.IsCancellationRequested)
                {
                    LogService.FileOp("Timeline[LoadProps] CANCELLED", LogLevel.Warning);
                    return;
                }

                var imgJson = imgTask.Result;
                var vidJson = vidTask.Result;

                LogService.Debug($"KeyPhoto exiftool image output: {TruncateJson(imgJson)}", LogSource.UI);
                if (!string.IsNullOrWhiteSpace(vidJson))
                    LogService.Debug($"KeyPhoto exiftool video output: {TruncateJson(vidJson)}", LogSource.UI);

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
                LogService.Debug($"KeyPhoto video props: W={vidProps.Width} H={vidProps.Height} Size={vidProps.FileSizeBytes} BR={vidProps.AvgBitrate} Codec={vidProps.CompressorID} Dur={vidProps.MediaDuration} FPS={vidProps.VideoFrameRate}", LogSource.UI);

                dispatcher.TryEnqueue(() =>
                {
                    // 代数检查：exiftool 查询耗时 1~3s，期间用户可能已切换到另一个文件。
                    // 若代数不匹配，说明此回调已过期，跳过所有 UI 更新和 ffmpeg 提取。
                    if (generation != Volatile.Read(ref _selectionGeneration))
                    {
                        LogService.FileOp(
                            $"Timeline[LoadProps] SKIP: generation mismatch (my={generation}, current={_selectionGeneration})",
                            LogLevel.Warning);
                        return;
                    }

                    ApplyProperties(imgProps);
                    ApplyVideoProperties(vidProps);

                    // 解析视频时长：取 MediaDuration 和 Duration 中较长的那个。
                    // • MediaDuration ("0.95 s") 有时被 exiftool 截断，而 Duration ("0.96 s") 更精确
                    // • 某些文件 MediaDuration = "0.00 s" 但 Duration 正确 → 取不为 0 的那个
                    double durSec = ParseExifDuration(vidProps.MediaDuration);
                    double durSec2 = ParseExifDuration(vidProps.Duration);
                    if (durSec2 > durSec)
                    {
                        durSec = durSec2;
                        LogService.FileOp(
                            $"Timeline[LoadProps] Using Duration={vidProps.Duration} (longer than MediaDuration={vidProps.MediaDuration})",
                            LogLevel.Info);
                    }

                    // 计算关键帧的时间偏移（各协议标签）
                    // 优先级：MotionPhotoPresentationTimestampUs(Google V2) >
                    //         MicroVideoPresentationTimestampUs(Google V1) >
                    //         PosterTime(Apple paired .MOV) >
                    //         默认 = 视频总时长 / 2
                    double keyPhotoTimeSeconds = 0;
                    if (imgProps.MotionPhotoPresentationTimestampUs > 0)
                    {
                        keyPhotoTimeSeconds = imgProps.MotionPhotoPresentationTimestampUs / 1_000_000.0;
                        LogService.FileOp(
                            $"Timeline[LoadProps] KeyPhoto from MotionPhotoPresentationTimestampUs: " +
                            $"{imgProps.MotionPhotoPresentationTimestampUs}μs → {keyPhotoTimeSeconds}s",
                            LogLevel.Info);
                    }
                    else if (imgProps.MicroVideoPresentationTimestampUs > 0)
                    {
                        keyPhotoTimeSeconds = imgProps.MicroVideoPresentationTimestampUs / 1_000_000.0;
                        LogService.FileOp(
                            $"Timeline[LoadProps] KeyPhoto from MicroVideoPresentationTimestampUs: " +
                            $"{imgProps.MicroVideoPresentationTimestampUs}μs → {keyPhotoTimeSeconds}s",
                            LogLevel.Info);
                    }
                    else if (!string.IsNullOrWhiteSpace(vidProps.PosterTime))
                    {
                        keyPhotoTimeSeconds = ParseExifDuration(vidProps.PosterTime);
                        LogService.FileOp(
                            $"Timeline[LoadProps] KeyPhoto from PosterTime: '{vidProps.PosterTime}' → {keyPhotoTimeSeconds}s",
                            LogLevel.Info);
                    }

                    // PosterTime 通常为 0，实际照片帧在视频中间位置
                    if (keyPhotoTimeSeconds <= 0 && durSec > 0)
                    {
                        var halfDur = durSec / 2.0;
                        LogService.FileOp(
                            $"Timeline[LoadProps] KeyPhoto fallback: keyPhotoTimeSeconds={keyPhotoTimeSeconds}, " +
                            $"using half duration={halfDur:F2}s",
                            LogLevel.Info);
                        keyPhotoTimeSeconds = halfDur;
                    }

                    // Apple MOV: PosterTime 永远为 0，真正封面/照片时间在 mebx 元数据轨
                    // ffprobe 找最后一个 nb_frames=1 且 start_time>0 的 mebx 轨
                    if (!string.IsNullOrEmpty(videoPath) && durSec > 0)
                    {
                        var appleTime = EditTimingService.ReadAppleStillImageTime(videoPath);
                        if (appleTime.HasValue && appleTime.Value > 0)
                        {
                            LogService.FileOp(
                                $"Timeline[LoadProps] KeyPhoto from Apple MOV metadata track: " +
                                $"{appleTime.Value:F4}s (was {keyPhotoTimeSeconds:F4}s)",
                                LogLevel.Info);
                            keyPhotoTimeSeconds = appleTime.Value;
                        }
                    }

                    // ── 协议专属 Cover 时机分离（OPPO 等）──
                    string? xmpText = null;
                    try { xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath); }
                    catch { /* 非 JPEG 或读取失败，跳过 */ }
                    var timing = EditTimingService.Resolve(keyPhotoTimeSeconds, xmpText);

                    // OPPO 改封面后原始高清图在 Original item 中，需要提取出来给 ⭐
                    byte[]? originalPhotoBytes = null;
                    if (timing.HasOriginalPhoto)
                    {
                        originalPhotoBytes = EditTimingService.ReadOriginalPhotoBytes(imagePath);
                    }

                    // 触发时间轴帧提取（需要视频路径 + 元数据）
                    if (durSec > 0)
                    {
                        double fps = double.TryParse(vidProps.VideoFrameRate,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var f)
                            ? f : 30.0;
                        _videoFps = fps;
                        FpsDisplayText = ResourceService.Format("KeyPhoto_TimelineFps", fps.ToString("F2"));

                        // 单文件实况照片：使用提取出的临时视频路径
                        // 双文件实况照片：使用配对视频路径
                        string? actualVideoPath = tempVideoPath ?? videoPath;
                        bool videoExists = !string.IsNullOrEmpty(actualVideoPath) && File.Exists(actualVideoPath);
                        LogService.FileOp(
                            $"Timeline[LoadProps] Checking trigger: durSec={durSec}, fps={fps}, " +
                            $"actualVideoPath='{actualVideoPath ?? "null"}', exists={videoExists}",
                            LogLevel.Info);
                        if (videoExists)
                        {
                            LogService.FileOp(
                                $"Timeline[LoadProps] → Triggering extraction for '{Path.GetFileName(actualVideoPath!)}'",
                                LogLevel.Info);
                            TriggerTimelineExtraction(actualVideoPath!, durSec, fps,
                                timing.PhotoTimeSeconds, timing.CoverTimeSeconds,
                                generation,
                                originalPhotoBytes);
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
                LogService.FileOp($"KeyPhoto property load cancelled for '{Path.GetFileName(imagePath)}'", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"Timeline[LoadProps] EXCEPTION: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);
                LogService.FileOp($"KeyPhoto property load failed for '{Path.GetFileName(imagePath)}': {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                // 不立即删除临时视频 — 时间轴帧提取还需要用它
                // 由 CleanupTempVideo() 在帧提取完成后清理
                if (tempVideoPath != null)
                {
                    _tempVideoPath = tempVideoPath;
                    LogService.FileOp(
                        $"Timeline[LoadProps] Temp video stored: '{tempVideoPath}', will cleanup after extraction",
                        LogLevel.Info);
                }
            }
        }

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
                keyPhotoTimeSeconds, keyPhotoTimeSeconds, generation);
        }

        private void TriggerTimelineExtraction(string videoPath, double durationSeconds, double fps,
            double photoTimeSeconds, double coverTimeSeconds,
            int generation = 0,
            byte[]? originalPhotoBytes = null)
        {
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

                    var result = await VideoFrameExtractionService.ExtractAllFramesAsync(
                        videoPath, ct);

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

                            // 2. 插入静态照片帧 ⭐（用 photoTimeSeconds 定位）
                            var photoTimestamp = TimeSpan.FromSeconds(photoTimeSeconds);
                            int insertPos = 0;
                            for (; insertPos < TimelineFrames.Count; insertPos++)
                            {
                                if (TimelineFrames[insertPos].Timestamp >= photoTimestamp)
                                    break;
                            }

                            // ⭐ 帧缩略图：先查内存缓存，避免重复解码 HEIC
                            Microsoft.UI.Xaml.Media.ImageSource? starThumbnail = SelectedFileThumbnail;
                            string starKey = $"{sourcePath}|star";
                            if (_thumbnailCache.TryGetValue(starKey, out var cachedStar))
                            {
                                starThumbnail = cachedStar;
                                LogService.FileOp("Timeline[Extract] ⭐ thumbnail from cache", LogLevel.Info);
                            }
                            else if (originalPhotoBytes != null && originalPhotoBytes.Length > 0)
                            {
                                try
                                {
                                    // SoftwareBitmap + SoftwareBitmapSource，杜绝 BitmapImage
                                    var ms = new MemoryStream(originalPhotoBytes);
                                    ms.Position = 0;
                                    var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
                                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                                    var source = new SoftwareBitmapSource();
                                    await source.SetBitmapAsync(softwareBitmap);
                                    starThumbnail = source;
                                    _thumbnailCache[starKey] = source;
                                    LogService.FileOp(
                                        $"Timeline[Extract] ⭐ using Original photo ({originalPhotoBytes.Length} bytes)",
                                        LogLevel.Info);
                                }
                                catch (Exception ex)
                                {
                                    LogService.FileOp(
                                        $"Timeline[Extract] Failed to decode Original photo: {ex.Message}",
                                        LogLevel.Warning);
                                }
                            }
                            else if (starThumbnail != null)
                            {
                                // SelectedFileThumbnail 已可用，加入缓存供后续复用
                                _thumbnailCache[starKey] = starThumbnail;
                            }

                            stillFrame = new TimelineFrame
                            {
                                FrameIndex = -1,     // 哨兵值，照片帧不是视频帧
                                Timestamp = photoTimestamp,
                                IsStillPhoto = true,
                                Thumbnail = starThumbnail
                            };
                            TimelineFrames.Insert(insertPos, stillFrame);

                            LogService.FileOp(
                                $"Timeline[Extract] Still photo ⭐ at pos={insertPos}/{TimelineFrames.Count}, " +
                                $"time={photoTimeSeconds}s, split={split}, " +
                                $"thumbnail={(starThumbnail != null ? "ok" : "null")}",
                                LogLevel.Info);

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
                            SelectTimelineFrameProgrammatically(frameToSelect);
                            LogService.Debug(
                                $"Timeline select: {(frameToSelect.IsStillPhoto ? "⭐" : $"vid #{frameToSelect.FrameIndex}")} " +
                                $"at {frameToSelect.Timestamp.TotalSeconds:F4}s " +
                                $"(cover={coverTimeSeconds:F4}s, photo={photoTimeSeconds:F4}s, split={split})",
                                LogSource.UI);

                            // 5. 逐帧加载 JPEG 缩略图 → SoftwareBitmap (Bgra8 Premultiplied) + SoftwareBitmapSource
                            //    后台线程解码 + UI 线程创建 Source。
                            //    排水泵：每提取 4 帧执行一次 Task.Delay(1)，
                            //    强制 WinUI Compositor 在单帧内将已就绪纹理刷入 GPU，
                            //    避免 ItemsRepeater 虚拟化回收与异步解码撞车导致白块。
                            int timelineIdx = 0;
                            for (int jpegIdx = 0; jpegIdx < result.JpegPaths.Count; jpegIdx++)
                            {
                                try
                                {
                                // 跳过照片帧
                                while (timelineIdx < TimelineFrames.Count
                                       && TimelineFrames[timelineIdx].IsStillPhoto)
                                    timelineIdx++;
                                if (timelineIdx >= TimelineFrames.Count) break;
                                    string frameKey = $"{sourcePath}|{jpegIdx}";
                                    if (!_thumbnailCache.TryGetValue(frameKey, out var cachedFrame))
                                    {
                                        var jpegPath = result.JpegPaths[jpegIdx];
                                        // 后台线程：BitmapDecoder 解码 JPEG → SoftwareBitmap (Bgra8, Premultiplied)
                                        var softwareBitmap = await Task.Run(() =>
                                        {
                                            using var fs = new FileStream(jpegPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                            var decoder = BitmapDecoder.CreateAsync(fs.AsRandomAccessStream()).GetAwaiter().GetResult();
                                            return decoder.GetSoftwareBitmapAsync(
                                                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).GetAwaiter().GetResult();
                                        });
                                        // UI 线程：SoftwareBitmap → SoftwareBitmapSource
                                        var source = new SoftwareBitmapSource();
                                        await source.SetBitmapAsync(softwareBitmap);
                                        _thumbnailCache[frameKey] = source;
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

                    // ⭐ 帧缩略图未就绪（SelectedFileThumbnail 可能异步加载中）→ 主动等待并回填
                    if (stillFrame != null && stillFrame.Thumbnail == null
                        && !string.IsNullOrEmpty(sourcePath))
                    {
                        string starKey = $"{sourcePath}|star";
                        try
                        {
                            var loaded = await ThumbnailService.LoadAsync(sourcePath, dispatcher);
                            if (loaded != null)
                            {
                                _thumbnailCache[starKey] = loaded;
                                dispatcher.TryEnqueue(() =>
                                {
                                    if (stillFrame != null) stillFrame.Thumbnail = loaded;
                                });
                                LogService.FileOp("Timeline[Extract] ⭐ thumbnail loaded post-fetch", LogLevel.Info);
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
        private async Task LoadPreviewImageAsync(string imagePath, int generation = 0)
        {
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
                disp?.TryEnqueue(() => SetPreviewSafe(cached));
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
        /// 用户手动点击时间轴帧 → 更新大图预览。
        /// 照片帧⭐ → 加载原始照片文件；
        /// 视频帧 → 加载 ffmpeg 提取的全分辨率 JPEG。
        /// </summary>
        private async Task UpdatePreviewForTimelineFrameAsync(TimelineFrame frame)
        {
            string? imagePath = null;

            if (frame.IsStillPhoto)
            {
                // 静态照片帧：使用原始照片文件
                imagePath = SelectedFilePath;
            }
            else if (!string.IsNullOrEmpty(frame.FullFramePath) && File.Exists(frame.FullFramePath))
            {
                // 视频帧：使用 ffmpeg 提取的全分辨率帧 JPEG
                imagePath = frame.FullFramePath;
            }

            if (string.IsNullOrEmpty(imagePath)) return;

            await LoadPreviewImageAsync(imagePath);
        }

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
            ProtocolLine = protocol ?? string.Empty;

            // ── 摄像头位置（后置/前置 + 类型），用于 Line 1 后缀 ──
            string cameraPosition = GetCameraPosition(p);

            // ── ExifCamera（Line 1 粗体）：拍摄设备 ──
            ExifCamera = !string.IsNullOrWhiteSpace(p.Camera)
                ? p.Camera
                : ResourceService.GetString("KeyPhoto_UnknownDevice");

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
            // 有 GPS 坐标才查询（坐标仅用于 API 调用，不显示）
            double? lat = DmsToDecimal(p.GpsLatitude, p.GpsLatitudeRef);
            double? lon = DmsToDecimal(p.GpsLongitude, p.GpsLongitudeRef);
            if (lat != null && lon != null && SelectedFilePath != null)
                _ = TriggerGeoLookupAsync(lat.Value, lon.Value, SelectedFilePath);
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
            _previewCache.Clear();
            _previewCacheOrder.Clear();

            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                LogService.FileOp($"KeyPhoto scan started: '{directoryPath}'");

                // 阶段 1：使用统一发现服务扫描（识别实况照片类型）
                var discoveryResult = await Task.Run(
                    () => LivePhotoDiscoveryService.ScanAsync(
                        directoryPath,
                        DiscoveryScanMode.JpegMarkers | DiscoveryScanMode.HeicTrack
                            | DiscoveryScanMode.CidMatch, token),
                    token);

                if (token.IsCancellationRequested) return;

                var files = discoveryResult.Items
                    .Where(d => !(d.LivePhotoType == LivePhotoType.DualFile && d.IsVideo))
                    .Select(d =>
                    {
                        // 有协议确认的：JPEG XMP 标记 / HEIC 视频轨。
                        // DualFile 先标 false，Phase 2 exiftool 查到 ContentIdentifier 后再确认。
                        bool confirmed = d.LivePhotoType is LivePhotoType.SingleFileJpeg
                            or LivePhotoType.SingleFileHeic;
                        return new EditFileItem
                        {
                            FileName = Path.GetFileName(d.FilePath),
                            FilePath = d.FilePath,
                            FileSize = FileSizeFormatter.Format(d.FileSizeBytes),
                            DateTaken = d.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                            Resolution = string.Empty,
                            LivePhotoType = d.LivePhotoType,
                            PairedVideoPath = d.PairedVideoPath,
                            AppendedVideoLength = d.AppendedVideoLength,
                            DetectionMethod = d.DetectionMethod,
                            HasConfirmedProtocol = confirmed
                        };
                    }).ToList();

                // 按分类统计（仅已确认协议的才算实况照片）
                int confirmedCount = files.Count(f => f.HasConfirmedProtocol);
                int dualCount = files.Count(f => f.LivePhotoType == LivePhotoType.DualFile);
                int singleJpegCount = files.Count(f => f.LivePhotoType == LivePhotoType.SingleFileJpeg);
                int singleHeicCount = files.Count(f => f.LivePhotoType == LivePhotoType.SingleFileHeic);
                LogService.FileOp($"KeyPhoto scan done: {files.Count} files total, " +
                    $"DualFile={dualCount}, SingleFileJpeg={singleJpegCount}, " +
                    $"SingleFileHeic={singleHeicCount}, Confirmed={confirmedCount}, " +
                    $"Regular={files.Count - dualCount - singleJpegCount - singleHeicCount}");

                // ── SingleFileHeic 补 CID 匹配：关联同目录的外部 MOV ──
                // Pass 2 把 HEIC 分类为 SingleFileHeic 后跳过了 Pass 4，
                // 导致同目录 MOV 永远没机会配对。此处补一次 CID 匹配，
                // 把配对视频路径写入 PairedVideoPath，供另存为等功能使用。
                if (singleHeicCount > 0)
                {
                    var unclassifiedMovs = discoveryResult.Items
                        .Where(d => d.LivePhotoType == LivePhotoType.None
                                    && d.FilePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                        .Select(d => d.FilePath)
                        .ToList();

                    if (unclassifiedMovs.Count > 0)
                    {
                        var heicPaths = files
                            .Where(f => f.LivePhotoType == LivePhotoType.SingleFileHeic)
                            .Select(f => f.FilePath)
                            .ToList();

                        string? exifToolPath = ExternalToolLocator.FindExifTool()
                            ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");

                        if (File.Exists(exifToolPath))
                        {
                            try
                            {
                                var matchResult = await Task.Run(
                                    () => LivePhotoMetadataMatcher.MatchAsync(
                                        heicPaths, unclassifiedMovs, exifToolPath, token,
                                        enableCombinedMatching: false, runContentIdentifier: true),
                                    token);

                                foreach (var pair in matchResult.Pairs)
                                {
                                    var item = files.FirstOrDefault(f =>
                                        string.Equals(f.FilePath, pair.ImagePath, StringComparison.OrdinalIgnoreCase));
                                    if (item != null)
                                    {
                                        item.PairedVideoPath = pair.VideoPath;
                                    }
                                }

                                if (matchResult.Pairs.Count > 0)
                                    LogService.FileOp($"KeyPhoto CID补配: {matchResult.Pairs.Count} 对 HEIC↔MOV");
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                LogService.FileOp($"KeyPhoto CID补配失败: {ex.Message}", LogLevel.Warning);
                            }
                        }
                    }
                }

                _allFileItems = files;
                LivePhotoCount = confirmedCount;
                OtherCount = files.Count - confirmedCount;

                ThumbnailService.ClearCache();
                ClearFileInfo();

                LogService.FileOp($"KeyPhoto scan phase 1: {files.Count} files ({LivePhotoCount} live photos) in '{directoryPath}'");

                // 阶段 2：exiftool 并行读取分辨率 + EXIF 日期
                if (files.Count > 0)
                {
                    await ReadResolutionsAsync(files, token);
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

        private async Task ReadResolutionsAsync(List<EditFileItem> files, CancellationToken token)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool()
                ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
            if (!File.Exists(exifToolPath))
            {
                LogService.FileOp("KeyPhoto: exiftool not found, skipping resolution reading", LogLevel.Warning);
                return;
            }

            int resSuccess = 0, resFail = 0;

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return;

            var pool = new List<PersistentExifTool>(ExifToolPoolSize);
            try
            {
                for (int i = 0; i < ExifToolPoolSize; i++)
                {
                    var tool = new PersistentExifTool(exifToolPath);
                    int toolIdx = i;
                    tool.OnRestarted += (msg) =>
                        dispatcher.TryEnqueue(() =>
                            LogService.FileOp($"[KeyPhoto exiftool#{toolIdx}] {msg}", LogLevel.Warning));
                    pool.Add(tool);
                }

                int batchSize = ExifToolPoolSize;
                for (int batchStart = 0; batchStart < files.Count; batchStart += batchSize)
                {
                    if (token.IsCancellationRequested) break;
                    int batchEnd = Math.Min(batchStart + batchSize, files.Count);
                    int batchCount = batchEnd - batchStart;

                    var batchTasks = new Task<(int index, int width, int height, string? dateTaken, string? cid)>[batchCount];
                    for (int bi = 0; bi < batchCount; bi++)
                    {
                        int fileIndex = batchStart + bi;
                        var file = files[fileIndex];
                        var tool = pool[bi % ExifToolPoolSize];
                        batchTasks[bi] = Task.Run(async () =>
                        {
                            try
                            {
                                string json = await tool.SendCommandAsync(
                                    token, "-j", "-ImageWidth", "-ImageHeight", "-DateTimeOriginal",
                                    "-ContentIdentifier", file.FilePath);
                                var (w, h, date, cid) = ParseExifInfo(json);
                                return (fileIndex, w, h, date, cid);
                            }
                            catch (OperationCanceledException) { return (fileIndex, 0, 0, null, null); }
                            catch (Exception ex)
                            {
                                LogService.FileOp($"exiftool resolution failed: {ex.Message}", LogLevel.Warning);
                                return (fileIndex, 0, 0, null, null);
                            }
                        }, token);
                    }

                    var results = await Task.WhenAll(batchTasks);
                    dispatcher.TryEnqueue(() =>
                    {
                        foreach (var (index, width, height, dateTaken, cid) in results)
                        {
                            if (width > 0 && height > 0)
                            {
                                files[index].Resolution = $"{width} × {height}";
                                resSuccess++;
                            }
                            else resFail++;
                            if (!string.IsNullOrWhiteSpace(dateTaken))
                                files[index].DateTaken = dateTaken;
                            // DualFile + ContentIdentifier → 确认为 Apple，显示 LIVE 徽标
                            if (!string.IsNullOrWhiteSpace(cid) &&
                                files[index].LivePhotoType == LivePhotoType.DualFile &&
                                !files[index].HasConfirmedProtocol)
                            {
                                files[index].HasConfirmedProtocol = true;
                                LivePhotoCount++;
                                OtherCount--;
                            }
                        }
                    });
                }
            }
            finally
            {
                foreach (var tool in pool) try { tool.Dispose(); } catch { }
            }

            LogService.FileOp($"KeyPhoto resolution reading done: {resSuccess} success, {resFail} failed (out of {files.Count})");
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
            SupportedImageExtensions.Contains(Path.GetExtension(path)) ||
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
            (new[] { "ultrawide", "ultra wide" }, "KeyPhoto_Lens_UltraWide"),
            (new[] { "wide" },                  "KeyPhoto_Lens_Wide"),
            (new[] { "telephoto" },             "KeyPhoto_Lens_Telephoto"),
            (new[] { "tele" },                  "KeyPhoto_Lens_Telephoto"),
            (new[] { "macro" },                 "KeyPhoto_Lens_Macro"),
            (new[] { "main" },                  "KeyPhoto_Lens_Main"),
            (new[] { "periscope" },             "KeyPhoto_Lens_Periscope"),
            (new[] { "depth", "portrait" },     "KeyPhoto_Lens_Depth"),
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
                ? ResourceService.GetString("KeyPhoto_Lens_Front")
                : ResourceService.GetString("KeyPhoto_Lens_Rear");

            string? type = lens != null ? MatchLensType(lens) : null;
            if (type == null && p.ZoomMultiple > 0)
            {
                type = p.ZoomMultiple switch
                {
                    <= 1 => ResourceService.GetString("KeyPhoto_Lens_Main"),
                    2 or 3 => ResourceService.GetString("KeyPhoto_Lens_Telephoto"),
                    _ => ResourceService.GetString("KeyPhoto_Lens_Periscope")
                };
            }

            return type != null ? $"{position}{type}" : $"{position}{ResourceService.GetString("KeyPhoto_Lens_Camera")}";
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
                return ResourceService.GetString("KeyPhoto_UnknownCamera");

            // ── 位置：LensModel 关键词 → 小米 SensorType → 默认后置 ──
            bool isFront = (!string.IsNullOrWhiteSpace(lens) && lens.Contains("front", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(p.SensorType) && p.SensorType.Equals("front", StringComparison.OrdinalIgnoreCase));

            string position = isFront
                ? ResourceService.GetString("KeyPhoto_Lens_Front")
                : ResourceService.GetString("KeyPhoto_Lens_Rear");

            // ── 类型：LensModel 关键词 → 小米 ZoomMultiple ──
            string? type = lens != null ? MatchLensType(lens) : null;

            if (type == null && p.ZoomMultiple > 0)
            {
                type = p.ZoomMultiple switch
                {
                    <= 1 => ResourceService.GetString("KeyPhoto_Lens_Main"),
                    2 or 3 => ResourceService.GetString("KeyPhoto_Lens_Telephoto"),
                    _ => ResourceService.GetString("KeyPhoto_Lens_Periscope")
                };
            }

            var parts = new List<string>
            {
                type != null ? $"{position}{type}" : $"{position}{ResourceService.GetString("KeyPhoto_Lens_Camera")}"
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

        /// <summary>JPEG 实况照片协议关键词 → 资源键（OPPO/小米优先于 Google：它们复用 Google XMP 结构）</summary>
        private static readonly (string Keyword, string ResourceKey)[] JpegProtocolMap =
        {
            ("OpCamera:VideoLength",            "KeyPhoto_Protocol_OPPO"),
            ("MiCamera:VideoLength",            "KeyPhoto_Protocol_Xiaomi"),
            ("GCamera:MicroVideo",              "KeyPhoto_Protocol_GoogleV1"),
            ("GCamera:MotionPhoto",             "KeyPhoto_Protocol_GoogleV2"),
            ("Container:Directory",             "KeyPhoto_Protocol_GoogleV2"),
        };

        /// <summary>根据 LivePhotoType + XMP 内容 + ContentIdentifier，确定协议显示名</summary>
        private static string? GetProtocolName(LivePhotoType type, string? filePath,
            string? contentIdentifier = null)
        {
            switch (type)
            {
                case LivePhotoType.DualFile:
                    if (!string.IsNullOrWhiteSpace(contentIdentifier))
                        return ResourceService.GetString("KeyPhoto_Protocol_Apple");
                    return ResourceService.GetString("KeyPhoto_Protocol_NonLive");

                case LivePhotoType.SingleFileJpeg:
                    if (filePath != null)
                    {
                        try
                        {
                            var text = LivePhotoSplitService.ReadMetadataTextSync(filePath);
                            foreach (var (keyword, resourceKey) in JpegProtocolMap)
                            {
                                if (text.Contains(keyword))
                                    return ResourceService.GetString(resourceKey);
                            }
                        }
                        catch { }
                    }
                    return ResourceService.GetString("KeyPhoto_Protocol_JpegGeneric");

                case LivePhotoType.SingleFileHeic:
                    return ResourceService.GetString("KeyPhoto_Protocol_HeicEmbedded");

                default:
                    return ResourceService.GetString("KeyPhoto_Protocol_NonLive");
            }
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

        /// <summary>反向地理编码：速率限制 1.2s，语言跟系统 CultureInfo</summary>
        private async Task TriggerGeoLookupAsync(double lat, double lon, string filePath)
        {
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

            try
            {
                // 语言跟系统当前 UI 文化：中文 → zh，其余 → en
                string lang = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh") ? "zh" : "en";
                string url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat:F6}&lon={lon:F6}&zoom=10&accept-language={lang}";
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "LivePhotoBox/2.0");
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetStringAsync(url, token);
                if (string.IsNullOrWhiteSpace(response)) return;

                using var doc = JsonDocument.Parse(response);
                string? name = doc.RootElement.TryGetProperty("display_name", out var dn)
                    ? dn.GetString() : null;

                if (!string.IsNullOrWhiteSpace(name) && !token.IsCancellationRequested)
                {
                    var dispatcher = App.MainWindow?.DispatcherQueue;
                    dispatcher?.TryEnqueue(() => ExifPlaceName = name);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                LogService.FileOp($"Geo lookup failed: {ex.Message}", LogLevel.Warning);
            }
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

            IsScanning = true;
            try
            {
                // 收集所有涉及的目录，按目录批量扫描（去重）
                var dirs = filePaths
                    .Select(p => Path.GetDirectoryName(p) ?? "")
                    .Where(d => Directory.Exists(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // ── 步骤 1：扫描所有涉及目录，建立路径→发现结果的映射 ──
            var discoveryMap = new Dictionary<string, LivePhotoDiscoveryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                try
                {
                    var result = await Task.Run(() =>
                        LivePhotoDiscoveryService.ScanAsync(dir,
                            DiscoveryScanMode.JpegMarkers | DiscoveryScanMode.HeicTrack
                                | DiscoveryScanMode.CidMatch));
                    foreach (var di in result.Items)
                    {
                        discoveryMap[di.FilePath] = di;
                    }
                }
                catch (Exception ex)
                {
                    LogService.FileOp(
                        $"Drop[Scan] Failed for '{dir}': {ex.Message}", LogLevel.Warning);
                }
            }

            // ── 步骤 2：构建 EditFileItem 列表，处理配对去重 ──
            var toAdd = new List<EditFileItem>();
            var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pairedVideoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawPath in filePaths)
            {
                if (!File.Exists(rawPath)) continue;
                if (addedPaths.Contains(rawPath)) continue;

                var filePath = rawPath;
                var fileName = Path.GetFileName(filePath);
                var fileSize = new FileInfo(filePath).Length;
                var lastWrite = File.GetLastWriteTime(filePath);

                LivePhotoType detectedType = LivePhotoType.None;
                LivePhotoDetectionMethod detectionMethod = LivePhotoDetectionMethod.FilenamePairing;
                string? pairedVideoPath = null;
                long appendedVideoLength = 0;

                if (discoveryMap.TryGetValue(filePath, out var match))
                {
                    detectedType = match.LivePhotoType;
                    detectionMethod = match.DetectionMethod;
                    pairedVideoPath = match.PairedVideoPath;
                    appendedVideoLength = match.AppendedVideoLength;
                }

                // ── DualFile：去重处理 ──
                if (detectedType == LivePhotoType.DualFile)
                {
                    // 如果这个文件是配对中的视频 → 看照片是否在本次拖入列表中
                    bool isVideo = match?.IsVideo ?? false;
                    if (isVideo && !string.IsNullOrEmpty(match?.PairedImagePath))
                    {
                        // 照片在本次拖入中 → 跳过视频，等照片加入时一起处理
                        if (filePaths.Contains(match.PairedImagePath, StringComparer.OrdinalIgnoreCase))
                        {
                            LogService.FileOp(
                                $"Drop[Pair] Skipping video '{fileName}' — paired photo also dropped",
                                LogLevel.Info);
                            continue;
                        }
                        // 照片不在本次拖入但存在于目录 → 改以照片为主文件
                        if (File.Exists(match.PairedImagePath))
                        {
                            var photoPath = match.PairedImagePath;
                            filePath = photoPath;
                            fileName = Path.GetFileName(photoPath);
                            fileSize = new FileInfo(photoPath).Length;
                            lastWrite = File.GetLastWriteTime(photoPath);
                            pairedVideoPath = match.FilePath; // 原视频路径是配对视频
                            if (discoveryMap.TryGetValue(photoPath, out var photoMatch))
                            {
                                detectedType = photoMatch.LivePhotoType;
                                detectionMethod = photoMatch.DetectionMethod;
                                appendedVideoLength = photoMatch.AppendedVideoLength;
                            }
                        }
                    }
                    // 标记配对视频路径（后续不再重复加入）
                    if (pairedVideoPath != null)
                        pairedVideoPaths.Add(pairedVideoPath);
                }

                bool confirmed = detectedType is LivePhotoType.SingleFileJpeg
                    or LivePhotoType.SingleFileHeic
                    or LivePhotoType.DualFile;

                // DualFile 需要配对视频路径才算已确认
                if (detectedType == LivePhotoType.DualFile && pairedVideoPath == null)
                    confirmed = false;

                var item = new EditFileItem
                {
                    FileName = fileName,
                    FilePath = filePath,
                    FileSize = FileSizeFormatter.Format(fileSize),
                    DateTaken = lastWrite.ToString("yyyy/MM/dd HH:mm"),
                    LivePhotoType = detectedType,
                    PairedVideoPath = pairedVideoPath,
                    AppendedVideoLength = appendedVideoLength,
                    DetectionMethod = detectionMethod,
                    HasConfirmedProtocol = confirmed,
                    Resolution = string.Empty
                };

                toAdd.Add(item);
                addedPaths.Add(filePath);
                if (pairedVideoPath != null)
                    addedPaths.Add(pairedVideoPath);
            }

            if (toAdd.Count == 0) return null;

            LogService.FileOp(
                $"Drop[Result] {toAdd.Count} items to add: " +
                string.Join(", ", toAdd.Select(i => $"{i.FileName}[{i.LivePhotoType}]")),
                LogLevel.Info);

            // ── 步骤 3：UI 线程加入列表，返回第一个新路径让 View 层触发选中 ──
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return null;

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
                            if (firstNewPath == null) firstNewPath = item.FilePath;
                            continue;
                        }

                        // 避免重复添加配对视频路径
                        if (pairedVideoPaths.Contains(item.FilePath)
                            && FileItems.Any(f => string.Equals(f.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        _allFileItems.Insert(0, item);
                        FileItems.Insert(0, item);
                        if (item.HasConfirmedProtocol)
                            LivePhotoCount++;
                        else
                            OtherCount++;

                        if (firstNewPath == null) firstNewPath = item.FilePath;
                    }

                    // 显式通知统计数绑定刷新，确保 LivePhotoCount/OtherCount 的 x:Bind 更新
                    OnPropertyChanged(nameof(LivePhotoCount));
                    OnPropertyChanged(nameof(OtherCount));
                    OnPropertyChanged(nameof(HasAnyFiles));

                    // 不在这里调用 SelectFile — 交给 View 层通过 ListView.SelectedItem 触发，
                    // 这样 SelectionChanged → SelectFile 只走一次，避免重复加载。
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
