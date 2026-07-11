/*
 * KeyPhotoViewModel.cs
 *
 * 实况照片主图更换页面的 ViewModel。
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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LivePhotoBox.ViewModels
{
    public partial class KeyPhotoViewModel : ViewModelBase
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

        public KeyPhotoViewModel()
        {
            // 默认路径为空，用户需要点击"浏览"选择文件夹或手动输入路径
        }

        public override string? PageStatusTag => null;

        /// <summary>页面卸载时清理 exiftool 进程</summary>
        public void Cleanup()
        {
            _propLoadCts?.Cancel();
            _geoCts?.Cancel();
            _timelineCts?.Cancel();
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

        /// <summary>文件统计摘要：共 M 个实况照片，K 个其他照片（多语言）</summary>
        public string FileCountSummary => ResourceService.Format("KeyPhoto_FileCountSummary", LivePhotoCount, OtherCount);

        partial void OnLivePhotoCountChanged(int value) => OnPropertyChanged(nameof(FileCountSummary));
        partial void OnOtherCountChanged(int value) => OnPropertyChanged(nameof(FileCountSummary));

        /// <summary>照片过滤：0=所有照片 / 1=实况照片 / 2=普通照片</summary>
        [ObservableProperty]
        private int _selectedFilterIndex;

        partial void OnSelectedFilterIndexChanged(int value) => ApplySortAndFilter();

        // ══════════════════════════════════════════════════════════════
        //  文件列表
        // ══════════════════════════════════════════════════════════════

        public ObservableCollection<KeyPhotoFileItem> FileItems { get; } = new();

        /// <summary>未过滤的完整文件列表（排序/搜索的后备存储）</summary>
        private List<KeyPhotoFileItem> _allFileItems = new();

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

        partial void OnIsTimelineLoadingChanged(bool value)
        {
            OnPropertyChanged(nameof(TimelineLoadingOpacity));
        }

        /// <summary>时间轴帧提取取消令牌</summary>
        private CancellationTokenSource? _timelineCts;

        /// <summary>单文件实况照片的内嵌视频临时文件路径（帧提取完成后清理）</summary>
        private string? _tempVideoPath;

        /// <summary>ffmpeg 提取的帧 JPEG 临时目录</summary>
        private string? _frameExtractDir;

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

        /// <summary>ViewModel 通知 View 层滚动到指定帧（ItemsRepeater 布局就绪后吸附定位）</summary>
        public event Action<TimelineFrame>? RequestScrollToFrame;

        /// <summary>标记：设置页切换模式后，OnNavigatedTo 需要修正滚动位置和初始化</summary>
        public bool NeedsModeSwitchFixup { get; set; }

        /// <summary>标记当前 SelectedTimelineFrame 是否为程序化设置（vs 用户手动点击）。
        /// 为 true 时允许触发滚动，为 false 时跳过滚动（用户手动点击不滚）。</summary>
        private bool _isProgrammaticTimelineSelection;

        partial void OnSelectedTimelineFrameChanged(TimelineFrame? value)
        {
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
        /// 更新 XAML Visibility 绑定，然后像重新点击左侧文件一样完整重新加载当前文件，
        /// 让时间轴通过 SelectFile → LoadPropertiesAsync → TriggerTimelineExtraction 全链路重建。
        /// </summary>
        public void NotifyTimelineModeChanged()
        {
            OnPropertyChanged(nameof(IsClassicTimelineMode));
            OnPropertyChanged(nameof(IsFilmstripTimelineMode));

            // 标记：下次导航回 KeyPhotoPage 时需要修正滚动位置 + 初始化
            NeedsModeSwitchFixup = true;

            // 重新加载当前文件（跑 ffmpeg 重建帧）
            var currentFile = SelectedFilePath;
            if (!string.IsNullOrEmpty(currentFile))
            {
                SelectFile(currentFile);
            }
        }

        /// <summary>
        /// 以程序化方式选中帧（触发滚动吸附 + 大图预览更新）。
        /// 区别于用户手动拖拽吸附：此方法会触发 RequestScrollToFrame 事件。
        /// </summary>
        public void SelectTimelineFrameProgrammatically(TimelineFrame frame)
        {
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
        [RelayCommand] private void Save() { IsModified = false; }
        [RelayCommand] private void SaveAs() { }
        [RelayCommand] private void Export() { }
        [RelayCommand] private void BrowseFolder() { }
        [RelayCommand] private void ViewFullProperties() { }


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
            _previewLoadCts?.Cancel();
            CleanupFrameTempFiles();
            CleanupTempVideo();

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
            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                IsSelectedLivePhoto = item.HasConfirmedProtocol;
                PhotoFileName = item.FileName;
                SelectedFileThumbnail = item.Thumbnail;
            }
            // 触发大图预览加载（异步，用令牌保护）
            _ = LoadPreviewImageAsync(filePath);

            // 清空信息面板字段，等异步 LoadPropertiesAsync 一次填充（避免旧数据闪烁）
            PhotoInfoLine = string.Empty;
            VideoInfoLine = string.Empty;
            ProtocolLine = string.Empty;
            ExifCamera = string.Empty;
            ExifCameraDateSuffix = string.Empty;
            ExifLensParams = string.Empty;
            ExifShootingParams = string.Empty;
            ExifPlaceName = string.Empty;

            // 时间轴：仅当从实况照片切换到非实况时清空（实况之间切换保留旧帧，避免闪烁）
            if (wasLivePhoto && !IsSelectedLivePhoto)
            {
                TimelineFrames.Clear();
                HasTimelineFrames = false;
                IsTimelineLoading = false;
                TimelineInfo = string.Empty;
                SelectedTimelineFrame = null;
            }

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
            _ = LoadPropertiesAsync(filePath, videoPath, embeddedVideoLen, token);
        }

        /// <summary>清空信息面板</summary>
        private void ClearFileInfo()
        {
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
            SelectedFileThumbnail = null;

            // 清除时间轴帧
            TimelineFrames.Clear();
            HasTimelineFrames = false;
            IsTimelineLoading = false;
            SelectedTimelineFrame = null;
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
        private async Task LoadPropertiesAsync(string imagePath, string? videoPath, long embeddedVideoLen, CancellationToken token)
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
                        var appleTime = KeyPhotoTimingService.ReadAppleStillImageTime(videoPath);
                        if (appleTime.HasValue && appleTime.Value > 0)
                        {
                            LogService.FileOp(
                                $"Timeline[LoadProps] KeyPhoto from Apple MOV metadata track: " +
                                $"{appleTime.Value:F4}s (was {keyPhotoTimeSeconds:F4}s)",
                                LogLevel.Info);
                            keyPhotoTimeSeconds = appleTime.Value;
                        }
                    }

                    // ── 协议专属 Key Photo 时机分离（OPPO 等）──
                    string? xmpText = null;
                    try { xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath); }
                    catch { /* 非 JPEG 或读取失败，跳过 */ }
                    var timing = KeyPhotoTimingService.Resolve(keyPhotoTimeSeconds, xmpText);

                    // OPPO 改封面后原始高清图在 Original item 中，需要提取出来给 ⭐
                    byte[]? originalPhotoBytes = null;
                    if (timing.HasOriginalPhoto)
                    {
                        originalPhotoBytes = KeyPhotoTimingService.ReadOriginalPhotoBytes(imagePath);
                    }

                    // 触发时间轴帧提取（需要视频路径 + 元数据）
                    if (durSec > 0)
                    {
                        double fps = double.TryParse(vidProps.VideoFrameRate,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var f)
                            ? f : 30.0;

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
        /// <param name="coverTimeSeconds">封面帧/Key Photo 时间偏移（秒，🔵 选中位置）</param>
        private void TriggerTimelineExtraction(string videoPath, double durationSeconds, double fps,
            double keyPhotoTimeSeconds = 0)
        {
            // 兼容旧调用（没传 photo/cover 时，两者都等于 keyPhotoTimeSeconds）
            TriggerTimelineExtraction(videoPath, durationSeconds, fps,
                keyPhotoTimeSeconds, keyPhotoTimeSeconds);
        }

        private void TriggerTimelineExtraction(string videoPath, double durationSeconds, double fps,
            double photoTimeSeconds, double coverTimeSeconds,
            byte[]? originalPhotoBytes = null)
        {
            bool split = Math.Abs(coverTimeSeconds - photoTimeSeconds) > 0.001;
            LogService.FileOp(
                $"Timeline[Extract] START: video='{Path.GetFileName(videoPath)}', " +
                $"dur={durationSeconds}s, fps={fps}, " +
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
                TimelineFrames.Clear();
                HasTimelineFrames = true;
                IsTimelineLoading = true;
            });

            // 后台：ffmpeg 解码全部帧（原始尺寸），完成后一次性创建 TimelineFrame
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await VideoFrameExtractionService.ExtractAllFramesAsync(
                        videoPath, ct);

                    if (ct.IsCancellationRequested)
                    {
                        LogService.FileOp("Timeline[Extract] ffmpeg CANCELLED", LogLevel.Warning);
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

                            // 3. TimelineInfo 使用真实帧数（不是 Ceil(dur×fps) 估算值）
                            string durDisplay = $"{durationSeconds:F2}s";
                            TimelineInfo = ResourceService.Format(
                                "KeyPhoto_TimelineInfo", durDisplay, actualFrameCount);

                            // 4. 逐帧加载 JPEG → SoftwareBitmap (Bgra8 Premultiplied) + SoftwareBitmapSource
                            //    后台线程解码 + UI 线程创建 Source。
                            //    排水泵：每提取 4 帧执行一次 Task.Delay(1)，
                            //    强制 WinUI Compositor 在单帧内将已就绪纹理刷入 GPU，
                            //    避免 ItemsRepeater 虚拟化回收与异步解码撞车导致白块。
                            int timelineIdx = 0;
                            for (int jpegIdx = 0; jpegIdx < result.JpegPaths.Count; jpegIdx++)
                            {
                                // 跳过照片帧
                                while (timelineIdx < TimelineFrames.Count
                                       && TimelineFrames[timelineIdx].IsStillPhoto)
                                    timelineIdx++;
                                if (timelineIdx >= TimelineFrames.Count) break;

                                try
                                {
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

                            IsTimelineLoading = false;
                            LogService.FileOp(
                                $"Timeline[Extract] Thumbnails loaded: {loadedCount} ok, {failedCount} failed (out of {actualFrameCount})",
                                failedCount > 0 ? LogLevel.Warning : LogLevel.Info);

                            // 5. 找到离 coverTimeSeconds 最近的帧并选中
                            //    未改封面时 = stillFrame(⭐)；改了封面时可能是某个视频帧
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

                            SelectTimelineFrameProgrammatically(frameToSelect);
                            LogService.Debug(
                                $"Timeline select: {(frameToSelect.IsStillPhoto ? "⭐" : $"vid #{frameToSelect.FrameIndex}")} " +
                                $"at {frameToSelect.Timestamp.TotalSeconds:F4}s " +
                                $"(cover={coverTimeSeconds:F4}s, photo={photoTimeSeconds:F4}s, split={split})",
                                LogSource.UI);

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
                        _ = LoadPreviewImageAsync(sourcePath);
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
        private async Task LoadPreviewImageAsync(string imagePath)
        {
            _previewLoadCts?.Cancel();
            _previewLoadCts?.Dispose();
            _previewLoadCts = new CancellationTokenSource();
            var token = _previewLoadCts.Token;

            // 缓存命中 → 直接显示，无需重新解码
            if (_previewCache.TryGetValue(imagePath, out var cached))
            {
                PreviewImageSource = cached;
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

                        // UI 线程：从临时 JPEG 创建 BitmapImage
                        var tcs = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        dispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                                var bmp = new BitmapImage { DecodePixelWidth = 2560 };
                                using var fs = new FileStream(tempJpegPath, FileMode.Open, FileAccess.Read);
                                bmp.SetSource(fs.AsRandomAccessStream());
                                PreviewImageSource = bmp;
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
                            if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                            var bmp = new BitmapImage { DecodePixelWidth = 2560 };
                            using (var stream = await file.OpenReadAsync().AsTask(token))
                            {
                                if (token.IsCancellationRequested) { tcs.TrySetResult(false); return; }
                                await bmp.SetSourceAsync(stream);
                            }
                            PreviewImageSource = bmp;
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
            var ext = Path.GetExtension(PhotoFileName).TrimStart('.').ToUpperInvariant();
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

            // ── ExifCamera（Line 1）：拍摄设备（粗体）──
            ExifCamera = !string.IsNullOrWhiteSpace(p.Camera)
                ? p.Camera
                : ResourceService.GetString("KeyPhoto_UnknownDevice");

            // ── ExifCameraDateSuffix：设备名后的日期后缀（细灰字体）──
            ExifCameraDateSuffix = string.IsNullOrEmpty(date) ? string.Empty : $"  —  {date}";

            // ── ExifLensParams（Line 2）：镜头描述（关键词映射 + 焦段 + 光圈）──
            ExifLensParams = BuildLensDisplayName(p);

            // ── ExifShootingParams（Line 3）：ISO │ EV │ 快门 │ HDR ──
            var shootParts = new List<string>();
            if (p.ISO > 0)
                shootParts.Add($"ISO {p.ISO}");
            if (!string.IsNullOrWhiteSpace(p.ExposureCompensation))
            {
                var ev = p.ExposureCompensation.Trim();
                if (double.TryParse(ev, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var evVal))
                    shootParts.Add($"EV{(evVal >= 0 ? "+" : "")}{evVal:F1}");
                else
                    shootParts.Add($"EV{ev}");
            }
            if (!string.IsNullOrWhiteSpace(p.ExposureTime))
                shootParts.Add(FormatExposureTime(p.ExposureTime));
            if (!string.IsNullOrWhiteSpace(p.HDRImageType))
                shootParts.Add("HDR");
            ExifShootingParams = string.Join("  │  ", shootParts);

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
                        return new KeyPhotoFileItem
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

        private async Task ReadResolutionsAsync(List<KeyPhotoFileItem> files, CancellationToken token)
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
        private static string GetPhotoSizeDisplay(KeyPhotoFileItem? item)
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
