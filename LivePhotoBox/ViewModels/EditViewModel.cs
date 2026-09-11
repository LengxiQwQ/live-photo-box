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
            return RebuiltSupportedImageExtensions.Contains(extension);
        }

        // ══════════════════════════════════════════════════════════════
        //  构造函数 & 生命周期
        // ══════════════════════════════════════════════════════════════

        public EditViewModel()
        {
            // 从设置恢复静音状态（默认不静音）
            _isMuted = AppSettingsService.GetValue("IsLivePhotoMuted", false);
            // 进度前缀默认：导出帧
            ProgressPrefixText = ResourceService.GetString("EditPage_ExportProgressPrefixLabel");

            // 挂接统一媒体打开管线事件（C# Control Plane 状态机同步）
            _openPipeline = new EditOpenPipeline();
            _openPipeline.DocumentChanged += doc =>
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    dispatcher.TryEnqueue(() => CurrentDocument = doc);
                }
                else
                {
                    CurrentDocument = doc;
                }
            };
            _openPipeline.SessionStateChanged += state =>
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    dispatcher.TryEnqueue(() => SessionState = state);
                }
                else
                {
                    SessionState = state;
                }
            };

            // 时间轴集合变化时同步 HasOriginalPhotoFrame
            TimelineFrames.CollectionChanged += (_, _) =>
                OnPropertyChanged(nameof(HasOriginalPhotoFrame));
        }

        public override string? PageStatusTag => null;

        /// <summary>页面卸载时清理资源与任务</summary>
        public void Cleanup()
        {
            InvalidateCurrentOpenSession();
            _propLoadCts?.Cancel();
            _geoCts?.Cancel();
            _timelineCts?.Cancel();
            _exportCts?.Cancel();
            _exportCts?.Dispose();
            _completionCts?.Cancel();
            _completionCts?.Dispose();
            CleanupFrameTempFiles();
            CleanupTempVideo();
            CleanupPlayableTempVideo();
            _previewService.Dispose();
            IsPreviewLoading = false;
            ThumbnailScheduler.Reset();
            CurrentDocument = null;
            SessionState = EditSessionState.Closed;
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
        private static bool IsVideoExtension(string path) =>
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        // ══════════════════════════════════════════════════════════════
        //  当前文档领域模型（EP1 引入：统一承载当前正在查看/编辑的媒体事实）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private EditDocument? _currentDocument;

        /// <summary>当前是否有有效编辑文档打开</summary>
        public bool HasDocument => CurrentDocument != null;

        /// <summary>当前编辑会话生命周期状态</summary>
        [ObservableProperty]
        private EditSessionState _sessionState = EditSessionState.Closed;

        partial void OnCurrentDocumentChanged(EditDocument? value)
        {
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(IsSelectedFileVideo));
            OnPropertyChanged(nameof(IsSelectedLivePhoto));
            OnPropertyChanged(nameof(IsSelectedPairIncomplete));
            OnPropertyChanged(nameof(IsPhotoRowVisible));
            OnPropertyChanged(nameof(IsVideoRowVisible));
            OnPropertyChanged(nameof(CanPlayLivePhoto));
            OnPropertyChanged(nameof(CanExportCurrentFrame));
            OnPropertyChanged(nameof(CanExportMultiFrame));
            OnPropertyChanged(nameof(ProtocolIconBrush));
            OnPropertyChanged(nameof(IsTimelineTabDisabled));
            OnPropertyChanged(nameof(PhotoFileName));
            OnPropertyChanged(nameof(FullPhotoFileName));
            OnPropertyChanged(nameof(PhotoInfoLine));
            OnPropertyChanged(nameof(VideoInfoLine));
            OnPropertyChanged(nameof(ProtocolLine));
            OnPropertyChanged(nameof(TimelineInfo));
            OnPropertyChanged(nameof(FpsDisplayText));
            OnPropertyChanged(nameof(ExifCamera));
            OnPropertyChanged(nameof(ExifCameraDateSuffix));
            OnPropertyChanged(nameof(ExifLensParams));
            OnPropertyChanged(nameof(ExifShootingParams));
            OnPropertyChanged(nameof(ExifPlaceName));
        }

        partial void OnSessionStateChanged(EditSessionState value)
        {
            OnPropertyChanged(nameof(PhotoInfoLine));
            OnPropertyChanged(nameof(VideoInfoLine));
            OnPropertyChanged(nameof(ProtocolLine));
            OnPropertyChanged(nameof(TimelineInfo));
            OnPropertyChanged(nameof(FpsDisplayText));
            OnPropertyChanged(nameof(ExifCamera));
            OnPropertyChanged(nameof(ExifCameraDateSuffix));
            OnPropertyChanged(nameof(ExifLensParams));
            OnPropertyChanged(nameof(ExifShootingParams));
            OnPropertyChanged(nameof(ExifPlaceName));
        }

        // ══════════════════════════════════════════════════════════════
        //  选中文件信息（右下角信息面板绑定，从 CurrentDocument 派生展示）
        // ══════════════════════════════════════════════════════════════

        /// <summary>格式化展示文件名（带省略号，双文件带双后缀）</summary>
        public string PhotoFileName
        {
            get
            {
                if (CurrentDocument == null || string.IsNullOrEmpty(CurrentDocument.PrimaryPath))
                    return string.Empty;
                string fileName = Path.GetFileName(CurrentDocument.PrimaryPath);
                bool isDual = CurrentDocument.LivePhotoType == LivePhotoType.DualFile;
                string? vidExt = !string.IsNullOrEmpty(CurrentDocument.MotionPath)
                    ? Path.GetExtension(CurrentDocument.MotionPath)
                    : null;
                return EditFileItem.FormatDisplayFileName(fileName, isDual, vidExt);
            }
        }

        /// <summary>完整展示文件名（不截断，供 AdaptiveFileName 自适应计算）</summary>
        public string FullPhotoFileName
        {
            get
            {
                if (CurrentDocument == null || string.IsNullOrEmpty(CurrentDocument.PrimaryPath))
                    return string.Empty;
                string fileName = Path.GetFileName(CurrentDocument.PrimaryPath);
                bool isDual = CurrentDocument.LivePhotoType == LivePhotoType.DualFile;
                string? vidExt = !string.IsNullOrEmpty(CurrentDocument.MotionPath)
                    ? Path.GetExtension(CurrentDocument.MotionPath)
                    : null;
                return EditFileItem.FormatFullDisplayFileName(fileName, isDual, vidExt);
            }
        }

        /// <summary>照片基础信息行（分辨率 │ 文件大小 │ 格式）</summary>
        public string PhotoInfoLine
        {
            get
            {
                if (CurrentDocument == null || SessionState != EditSessionState.Ready || IsSelectedFileVideo)
                    return string.Empty;

                string resolution = (CurrentDocument.Width > 0 && CurrentDocument.Height > 0)
                    ? $"{CurrentDocument.Width} × {CurrentDocument.Height}"
                    : string.Empty;

                string extension = Path.GetExtension(CurrentDocument.PrimaryPath).TrimStart('.').ToUpperInvariant();
                string sizeDisplay = GetPhotoSizeDisplay(CurrentDocument);

                return string.IsNullOrEmpty(resolution)
                    ? $"{sizeDisplay}  │  {extension}"
                    : $"{resolution}  │  {sizeDisplay}  │  {extension}";
            }
        }

        /// <summary>视频基础信息行（分辨率 │ 文件大小 │ 编码 │ 时长）</summary>
        public string VideoInfoLine
        {
            get
            {
                if (CurrentDocument == null || SessionState != EditSessionState.Ready)
                    return string.Empty;

                if (!IsVideoRowVisible)
                    return string.Empty;

                var parts = new List<string>();

                uint vWidth = CurrentDocument.VideoWidth > 0 ? CurrentDocument.VideoWidth : (IsSelectedFileVideo ? CurrentDocument.Width : 0);
                uint vHeight = CurrentDocument.VideoHeight > 0 ? CurrentDocument.VideoHeight : (IsSelectedFileVideo ? CurrentDocument.Height : 0);
                if (vWidth > 0 && vHeight > 0)
                    parts.Add($"{vWidth} × {vHeight}");

                long videoBytes = CurrentDocument.MotionVideoByteLength;
                if (videoBytes <= 0)
                {
                    string? videoPath = CurrentDocument.MotionPath ?? (IsSelectedFileVideo ? CurrentDocument.PrimaryPath : null);
                    if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                    {
                        try { videoBytes = new FileInfo(videoPath).Length; } catch { }
                    }
                }
                if (videoBytes > 0)
                    parts.Add(FileSizeFormatter.Format(videoBytes));

                if (CurrentDocument.VideoCodec != VideoCodec.Unknown)
                {
                    parts.Add(CurrentDocument.VideoCodec switch
                    {
                        VideoCodec.H264 => "H.264",
                        VideoCodec.Hevc => "H.265",
                        _ => CurrentDocument.VideoCodec.ToString()
                    });
                }

                if (CurrentDocument.DurationSeconds > 0)
                    parts.Add($"{CurrentDocument.DurationSeconds:F2}s");

                return string.Join("  │  ", parts);
            }
        }

        /// <summary>实况协议行文本</summary>
        public string ProtocolLine =>
            CurrentDocument != null && SessionState == EditSessionState.Ready
                ? GetRebuiltProtocolName(CurrentDocument.SourceProtocol)
                : string.Empty;

        /// <summary>EXIF 相机/设备型号</summary>
        public string ExifCamera =>
            CurrentDocument != null && SessionState == EditSessionState.Ready
                ? (!string.IsNullOrEmpty(CurrentDocument.Metadata.CameraModel)
                    ? CurrentDocument.Metadata.CameraModel
                    : ResourceService.GetString("EditPage_UnknownDevice"))
                : string.Empty;

        /// <summary>设备名后的日期后缀（如 " — 2025/12/12 16:44"），与 ExifCamera 同行显示</summary>
        public string ExifCameraDateSuffix =>
            CurrentDocument != null && SessionState == EditSessionState.Ready && CurrentDocument.Metadata.DateTaken.HasValue
                ? $" — {CurrentDocument.Metadata.DateTaken.Value:yyyy/MM/dd HH:mm}"
                : string.Empty;

        /// <summary>EXIF 镜头参数</summary>
        public string ExifLensParams =>
            CurrentDocument != null && SessionState == EditSessionState.Ready
                ? CurrentDocument.Metadata.LensModel
                : string.Empty;

        /// <summary>EXIF 曝光拍摄参数</summary>
        public string ExifShootingParams =>
            CurrentDocument != null && SessionState == EditSessionState.Ready
                ? CurrentDocument.Metadata.ShootingParams
                : string.Empty;

        /// <summary>EXIF 地理位置城市名</summary>
        public string ExifPlaceName =>
            CurrentDocument != null && SessionState == EditSessionState.Ready
                ? CurrentDocument.Metadata.PlaceName
                : string.Empty;

        /// <summary>时间轴时长信息文本（如 "2.98s"）</summary>
        public string TimelineInfo =>
            CurrentDocument != null && SessionState == EditSessionState.Ready && CurrentDocument.DurationSeconds > 0
                ? $"{CurrentDocument.DurationSeconds:F2}s"
                : string.Empty;

        /// <summary>FPS 显示文本，如 "30.00fps"</summary>
        public string FpsDisplayText =>
            CurrentDocument != null && SessionState == EditSessionState.Ready && CurrentDocument.FrameRate > 0
                ? ResourceService.Format("EditPage_TimelineFps", CurrentDocument.FrameRate.ToString("F2"))
                : string.Empty;

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

        /// <summary>初始加载自动滚动时抑制大图预览更新，避免滚过几十帧时大图疯狂切换</summary>
        private bool _isInitialTimelineScroll;

        /// <summary>时间轴正在自动滚动中（初始加载定位封面帧），View 层据此禁用用户滚轮输入</summary>
        public bool IsTimelineAutoScrolling => _isInitialTimelineScroll;


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

        /// <summary>大图预览服务，统一管理图片解码、LRU 缓存与临时预览资源生命周期</summary>
        private readonly EditPreviewService _previewService = new();

        /// <summary>统一媒体打开与加载管线（EP4 引入：统一处理输入校验、代数保护与状态机）</summary>
        private readonly EditOpenPipeline _openPipeline;

        /// <summary>当前播放的实况视频临时提取文件路径</summary>
        private string? _playableTempVideoPath;

        /// <summary>大图预览是否正在解码加载中</summary>
        [ObservableProperty]
        private bool _isPreviewLoading;

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

        /// <summary>当前选中的文件是否为缺失配对文件的实况照片（从 CurrentDocument 派生）</summary>
        public bool IsSelectedPairIncomplete =>
            CurrentDocument?.PairState == EditPairState.MissingVideo;

        /// <summary>不完整实况 → 禁用"组合查看"和"实况照片帧"标签页</summary>
        public bool IsTimelineTabDisabled => IsSelectedPairIncomplete;

        /// <summary>协议图标颜色：正常实况=主题色，非实况/残缺=红色警告</summary>
        public SolidColorBrush ProtocolIconBrush =>
            IsSelectedLivePhoto && !IsSelectedPairIncomplete
                ? (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));

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
                int totalFrameCount = TimelineFrames.Count(f => f.FrameIndex >= 0);

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

        /// <summary>
        /// 当前选中的关键帧（别名，对应需求中的 CurrentKeyFrame）。
        /// 与 SelectedTimelineFrame 指向同一对象，供外部以明确语义访问。
        /// </summary>
        public TimelineFrame? CurrentKeyFrame
        {
            get => SelectedTimelineFrame;
            set => SelectedTimelineFrame = value;
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

        [ObservableProperty]
        private string? _selectedFilePath;

        /// <summary>是否有文件被选中（用于控制信息面板图标和分隔线可见性）</summary>
        public bool HasSelectedFile => !string.IsNullOrEmpty(SelectedFilePath);

        /// <summary>当前目录是否加载了文件（控制折叠按钮可见性）</summary>
        public bool HasAnyFiles => FileItems.Count > 0;

        /// <summary>是否已加载过目录（_allFileItems 有数据），用于区分"未选择目录"和"筛选结果为空"</summary>
        public bool HasFilesLoaded => _allFileItems.Count > 0;

        /// <summary>选中文件是否为独立视频（从 CurrentDocument 派生）</summary>
        public bool IsSelectedFileVideo => CurrentDocument?.MediaKind == EditMediaKind.Video;

        /// <summary>照片信息行可见（非视频文件时显示）</summary>
        public bool IsPhotoRowVisible => !IsSelectedFileVideo;

        /// <summary>选中文件是否为已确认协议的实况照片（从 CurrentDocument 派生）</summary>
        public bool IsSelectedLivePhoto => CurrentDocument?.IsLivePhoto == true;

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
                // 优先从 CurrentDocument 获取配对视频事实，回退到 FileItems
                var pairedVideoPath = (CurrentDocument != null && string.Equals(CurrentDocument.PrimaryPath, photoPath, StringComparison.OrdinalIgnoreCase))
                    ? CurrentDocument.PairedVideoPath
                    : FileItems.FirstOrDefault(f => string.Equals(f.FilePath, photoPath, StringComparison.OrdinalIgnoreCase))?.PairedVideoPath;
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
                        var photoPath = CurrentDocument?.PrimaryPath ?? SelectedFilePath;
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
                    var photoPath = CurrentDocument?.PrimaryPath ?? SelectedFilePath;
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
            var photoBaseName = Path.GetFileNameWithoutExtension(CurrentDocument?.PrimaryPath ?? SelectedFilePath ?? "photo");
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
            var photoPath = CurrentDocument?.PrimaryPath ?? SelectedFilePath;
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

            var photoPath = CurrentDocument?.PrimaryPath ?? SelectedFilePath;
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
            formatComboBox.Items.Add(new ComboBoxItem { Content = "HEIC (.heic)", Tag = ".heic" });
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
            // Primary and MotionVideo ranges are Native Inspector facts. Do not
            // infer a still boundary from managed metadata or container scans.
            var facts = await new SourceInspector().InspectAsync(photoPath, null, token).ConfigureAwait(false);
            var video = facts.MotionVideo;
            if (facts.Protocol is SourceProtocol.NonLive or SourceProtocol.Unknown ||
                video is not { IsPresent: true } || video.SourceIndex != 0)
                return photoPath;

            var primary = facts.PrimaryImage;
            if (!primary.IsPresent || primary.ByteOffset < 0 || primary.ByteLength <= 0 ||
                video.ByteOffset < 0 || video.ByteLength <= 0)
                throw new InvalidDataException("Native source inspection returned incomplete primary/video ranges.");
            if (video.Container is not (VideoContainer.Mp4 or VideoContainer.Mov))
                throw new InvalidDataException("Native source inspection returned an unsupported embedded video container.");

            long sourceLength = new FileInfo(photoPath).Length;
            if (primary.ByteOffset > sourceLength || primary.ByteLength > sourceLength - primary.ByteOffset ||
                video.ByteOffset > sourceLength || video.ByteLength > sourceLength - video.ByteOffset)
                throw new InvalidDataException("Native source inspection returned an out-of-bounds media range.");

            long primaryLength = primary.ByteLength;
            long primaryEnd = checked(primary.ByteOffset + primaryLength);
            long videoEnd = checked(video.ByteOffset + video.ByteLength);
            if (primary.ByteOffset < videoEnd && video.ByteOffset < primaryEnd)
                throw new InvalidDataException("Native primary and motion-video ranges overlap; refusing to derive a still boundary.");

            // A single-file source has an embedded video range. The still export
            // must use the exact primary range, including any protocol-owned image
            // metadata that belongs to that primary artifact.
            return await SliceContainerRangeAsync(photoPath, primary.ByteOffset, primaryLength, token);
        }

        /// <summary>按 Native Inspector 给出的 primary range 切片成临时文件。</summary>
        private static async Task<string> SliceContainerRangeAsync(
            string sourcePath,
            long offset,
            long length,
            CancellationToken token)
        {
            string ext = Path.GetExtension(sourcePath);
            string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_still_{Guid.NewGuid():N}{ext}");
            using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                if (offset < 0 || length <= 0 || offset > src.Length || length > src.Length - offset)
                    throw new InvalidDataException("Requested primary image range is outside the source file.");
                src.Seek(offset, SeekOrigin.Begin);
                long remain = length;
                while (remain > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                    if (r == 0) throw new EndOfStreamException("Source changed while extracting the inspected primary range.");
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

            bool isLivePhoto;
            string primaryPath;
            LivePhotoType livePhotoType;
            string? pairedVideoPath;

            if (CurrentDocument != null && (string.IsNullOrEmpty(SelectedFilePath) || string.Equals(CurrentDocument.PrimaryPath, SelectedFilePath, StringComparison.OrdinalIgnoreCase)))
            {
                isLivePhoto = CurrentDocument.IsLivePhoto && (CurrentDocument.Protocol != LivePhotoProtocolType.Unknown || CurrentDocument.LivePhotoType != LivePhotoType.None);
                primaryPath = CurrentDocument.PrimaryPath;
                livePhotoType = CurrentDocument.LivePhotoType;
                pairedVideoPath = CurrentDocument.PairedVideoPath;
            }
            else
            {
                var item = FileItems.FirstOrDefault(f =>
                    string.Equals(f.FilePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase));
                isLivePhoto = item != null && item.HasConfirmedProtocol;
                primaryPath = item?.FilePath ?? SelectedFilePath ?? string.Empty;
                livePhotoType = item?.LivePhotoType ?? LivePhotoType.None;
                pairedVideoPath = item?.PairedVideoPath;
            }

            if (!isLivePhoto || string.IsNullOrEmpty(primaryPath))
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_GuardNotLivePhoto"));
                return;
            }

            string? videoPath = await ProcessingPipelineRouter.RunRebuiltAsync(
                "edit.video-export",
                () => ResolveVideoPathForRebuiltExportAsync(primaryPath, livePhotoType, pairedVideoPath));
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                ShowExportGuardError(ResourceService.GetString("EditPage_GuardNoVideoSource"));
                return;
            }

            // 保存对话框 — 两种格式在文件类型下拉中选
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(primaryPath),
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
                var (success, errorMessage) = await ProcessingPipelineRouter.RunRebuiltAsync(
                    "edit.video-export",
                    () => ExportVideoWithNativeAsync(videoPath, targetFile.Path, cts.Token));

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

        [RelayCommand]
        private Task ExportGif()
        {
            if (IsExporting && !IsShowingSaveComplete) return Task.CompletedTask;

            ShowExportGuardError(ResourceService.GetString("EditPage_RebuiltGifUnsupported"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Resolves the video for Rebuilt export through the Native-backed
        /// Inspect -> Extract -> Clean pipeline. It deliberately does not use
        /// the Legacy protocol parsers or FFmpeg extraction helpers.
        /// </summary>
        private async Task<string?> ResolveVideoPathForRebuiltExportAsync(
            string primaryPath,
            LivePhotoType livePhotoType,
            string? pairedVideoPath,
            CancellationToken cancellationToken = default)
        {
            CleanupExportMediaWorkspace();

            var workspace = new MediaWorkspace();
            try
            {
                string? secondaryPath = livePhotoType == LivePhotoType.DualFile
                    && !string.IsNullOrWhiteSpace(pairedVideoPath)
                    ? pairedVideoPath
                    : null;

                NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                    primaryPath,
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

        private Task<string?> ResolveVideoPathForRebuiltExportAsync(
            EditFileItem item,
            CancellationToken cancellationToken = default)
        {
            return ResolveVideoPathForRebuiltExportAsync(item.FilePath, item.LivePhotoType, item.PairedVideoPath, cancellationToken);
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


        // ══════════════════════════════════════════════════════════════
        //  文件选中 → 加载属性
        // ══════════════════════════════════════════════════════════════

        /// <summary>属性加载取消令牌</summary>
        private CancellationTokenSource? _propLoadCts;

        private CancellationTokenSource? _geoCts;
        /// <summary>View 层选中变更或打开文件时调用，统一接入 OpenMediaAsync 管线</summary>
        public void SelectFile(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                CloseMedia();
                return;
            }

            _ = OpenMediaAsync(new EditOpenRequest(filePath));
        }

        /// <summary>
        /// 使当前打开会话立即失效并取消后台异步管线（代数递增 + CTS 取消）。
        /// 必须在清理 UI / Session 之前调用，以确保后台异步完成时无法触碰 UI 或恢复状态。
        /// </summary>
        private void InvalidateCurrentOpenSession()
        {
            _openPipeline.Close();
        }

        /// <summary>
        /// 关闭当前打开的媒体并清理预览与临时资源。
        /// </summary>
        public void CloseMedia()
        {
            InvalidateCurrentOpenSession();
            ClearFileInfo();
        }

        /// <summary>
        /// 打开媒体文件的便捷入口。
        /// </summary>
        public Task<bool> OpenMediaAsync(string filePath, CancellationToken externalToken = default)
        {
            return OpenMediaAsync(new EditOpenRequest(filePath), externalToken);
        }

        /// <summary>
        /// 统一媒体打开管线入口（EP4 核心入口）。
        /// 无论拖拽、列表点击、文件选择器均进入此统一流程。
        /// </summary>
        public async Task<bool> OpenMediaAsync(EditOpenRequest request, CancellationToken externalToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            // 1. 取消旧大图加载与清理临时视频
            _previewService.CancelCurrent();
            CleanupPlayableTempVideo();
            CleanupFrameTempFiles();
            CleanupTempVideo();

            SelectedFilePath = request.PrimaryPath;

            // 2. 挂接选中项缩略图监听
            if (_thumbnailLoadListener != null)
            {
                _thumbnailLoadListener.PropertyChanged -= ThumbnailItem_PropertyChanged;
                _thumbnailLoadListener = null;
            }

            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, request.PrimaryPath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                SelectedFileThumbnail = item.Thumbnail;
                if (SelectedFileThumbnail == null)
                {
                    _thumbnailLoadListener = item;
                    item.PropertyChanged += ThumbnailItem_PropertyChanged;
                }
            }
            else
            {
                SelectedFileThumbnail = null;
            }

            // 3. 区分纯视频与图片预览
            string ext = Path.GetExtension(request.PrimaryPath);
            bool isVideo = SupportedVideoExtensions.Contains(ext);

            if (isVideo)
            {
                SetPreviewSafe(null);
                IsPreviewLoading = false;
                PreviewClearRequested?.Invoke();
            }
            else if (IsSupportedImageExtension(ext))
            {
                _ = LoadPreviewAsync(request.PrimaryPath);
            }

            // 4. 交由统一 OpenPipeline 执行代数保护、Native Inspection 与状态转换
            bool success = await _openPipeline.OpenMediaAsync(request, externalToken).ConfigureAwait(false);

            if (success && !isVideo && item != null && string.IsNullOrEmpty(item.Resolution))
            {
                var doc = _openPipeline.CurrentDocument;
                if (doc != null && doc.Width > 0 && doc.Height > 0)
                {
                    string res = $"{doc.Width} × {doc.Height}";
                    var dispatcher = App.MainWindow?.DispatcherQueue;
                    if (dispatcher != null && !dispatcher.HasThreadAccess)
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            if (string.IsNullOrEmpty(item.Resolution))
                                item.Resolution = res;
                        });
                    }
                    else
                    {
                        item.Resolution = res;
                    }
                }
            }

            return success;
        }

        /// <summary>
        /// 请求获取当前文档的可播放动态视频路径（C# Control Plane 视频源提供者）。
        /// - 纯视频：返回 PrimaryPath 原文件路径
        /// - 双文件实况：返回 PairedVideoPath 路径
        /// - 单文件实况：通过 Native 提取嵌入视频到临时文件并返回路径
        /// - 非实况/未打开：返回 null
        /// </summary>
        public async Task<string?> GetPlayableMotionVideoAsync(CancellationToken cancellationToken = default)
        {
            var doc = CurrentDocument;
            if (doc == null) return null;

            // 1. 独立视频：直接播放原文件，无需复制或转码
            if (doc.MediaKind == EditMediaKind.Video)
            {
                return File.Exists(doc.PrimaryPath) ? doc.PrimaryPath : null;
            }

            // 2. 双文件实况照片：直接播放配对视频原文件
            if (doc.LivePhotoType == LivePhotoType.DualFile)
            {
                if (!string.IsNullOrEmpty(doc.PairedVideoPath) && File.Exists(doc.PairedVideoPath))
                    return doc.PairedVideoPath;
                return null;
            }

            // 3. 单文件实况照片：先检查已提取的临时视频是否仍然有效可用
            if (!string.IsNullOrEmpty(_playableTempVideoPath) && File.Exists(_playableTempVideoPath))
            {
                return _playableTempVideoPath;
            }

            if (!string.IsNullOrEmpty(CachedTempVideoPath) && File.Exists(CachedTempVideoPath))
            {
                return CachedTempVideoPath;
            }

            // 4. 调用 Native 统一视频提取器提取临时视频
            if (doc.HasEmbeddedMotionVideo || doc.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic)
            {
                try
                {
                    var tempPath = await LivePhotoVideoExtractor.ExtractVideoAutoAsync(
                        doc.PrimaryPath,
                        doc.MotionVideoByteLength,
                        cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                    {
                        _playableTempVideoPath = tempPath;
                        return tempPath;
                    }
                }
                catch (Exception ex)
                {
                    LogService.FileOp($"GetPlayableMotionVideoAsync extract failed: {ex.Message}", LogLevel.Warning);
                }
            }

            return null;
        }

        /// <summary>
        /// 销毁并清理当前提取的实况播放临时视频文件。
        /// </summary>
        public void CleanupPlayableTempVideo()
        {
            var path = _playableTempVideoPath;
            _playableTempVideoPath = null;
            if (!string.IsNullOrEmpty(path))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        LogService.FileOp($"CleanupPlayableTempVideo failed: {ex.Message}", Models.LogLevel.Warning);
                    }
                });
            }
        }

        /// <summary>清空信息面板与重置会话状态</summary>
        private void ClearFileInfo()
        {
            // 优先作废 OpenPipeline 的 generation 并取消后台异步任务，防止迟到回调触碰 UI 或复活旧文档
            InvalidateCurrentOpenSession();

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
            CurrentFramePositionText = string.Empty;
            SelectedFileThumbnail = null;

            // 清空大图预览与活跃请求
            _previewService.Clear();
            SetPreviewSafe(null);
            IsPreviewLoading = false;
            PreviewClearRequested?.Invoke();

            // 清除时间轴帧 + 临时文件
            TimelineFrames.Clear();
            HasTimelineFrames = false;
            IsTimelineLoading = false;
            SelectedTimelineFrame = null;
            CleanupFrameTempFiles();
            CleanupTempVideo();
            CleanupPlayableTempVideo();

            CurrentDocument = null;
            SessionState = EditSessionState.Closed;
        }

        // ══════════════════════════════════════════════════════════════
        //  属性加载（选中文件时）
        // ══════════════════════════════════════════════════════════════

        private static bool IsImageOrVideo(string path) =>
            IsSupportedImageExtension(Path.GetExtension(path)) ||
            SupportedVideoExtensions.Contains(Path.GetExtension(path));

        /// <summary>获取照片部分的大小（优先从 EditDocument 事实计算）</summary>
        private static string GetPhotoSizeDisplay(EditDocument? doc)
        {
            if (doc == null || string.IsNullOrEmpty(doc.PrimaryPath) || !File.Exists(doc.PrimaryPath))
                return "—";

            if (doc.HasEmbeddedMotionVideo && doc.MotionVideoByteLength > 0)
            {
                try
                {
                    var totalBytes = new FileInfo(doc.PrimaryPath).Length;
                    var photoBytes = totalBytes - doc.MotionVideoByteLength;
                    if (photoBytes > 0)
                        return FileSizeFormatter.Format(photoBytes);
                }
                catch { }
            }

            try
            {
                return FileSizeFormatter.Format(new FileInfo(doc.PrimaryPath).Length);
            }
            catch
            {
                return "—";
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
        //  大图预览加载（委托 EditPreviewService 统一管理解码与缓存）
        // ══════════════════════════════════════════════════════════════

        private async Task LoadPreviewAsync(string imagePath)
        {
            IsPreviewLoading = true;
            try
            {
                var result = await _previewService.LoadImagePreviewAsync(imagePath).ConfigureAwait(false);
                if (result.Success && result.ImageSource != null)
                {
                    var dispatcher = App.MainWindow?.DispatcherQueue;
                    if (dispatcher != null && !dispatcher.HasThreadAccess)
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            if (string.Equals(_previewService.LatestRequestPath, imagePath, StringComparison.OrdinalIgnoreCase))
                            {
                                SetPreviewSafe(result.ImageSource);
                            }
                        });
                    }
                    else
                    {
                        if (string.Equals(_previewService.LatestRequestPath, imagePath, StringComparison.OrdinalIgnoreCase))
                        {
                            SetPreviewSafe(result.ImageSource);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Debug($"LoadPreviewAsync error for '{Path.GetFileName(imagePath)}': {ex.Message}", LogSource.UI);
            }
            finally
            {
                if (string.Equals(_previewService.LatestRequestPath, imagePath, StringComparison.OrdinalIgnoreCase))
                {
                    var dispatcher = App.MainWindow?.DispatcherQueue;
                    if (dispatcher != null && !dispatcher.HasThreadAccess)
                    {
                        bool queued = dispatcher.TryEnqueue(() => IsPreviewLoading = false);
                        if (!queued)
                        {
                            IsPreviewLoading = false;
                        }
                    }
                    else
                    {
                        IsPreviewLoading = false;
                    }
                }
            }
        }

        /// <summary>
        /// 将帧缩略图写入缓存，超过上限时淘汰最旧的条目。
        /// 与预览缓存结构一致。
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
                imagePath = CurrentDocument?.PrimaryPath ?? SelectedFilePath;
            }
            else if (frame.IsOriginalPhoto)
            {
                // 原始帧 🖼：优先使用 FullFramePath（已写入临时文件），回退到重新提取
                if (!string.IsNullOrEmpty(frame.FullFramePath) && File.Exists(frame.FullFramePath))
                    imagePath = frame.FullFramePath;
                else
                {
                    var primaryPath = CurrentDocument?.PrimaryPath ?? SelectedFilePath;
                    if (!string.IsNullOrEmpty(primaryPath) && File.Exists(primaryPath))
                    {
                        byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(primaryPath);
                        if (origBytes != null && origBytes.Length > 0)
                        {
                            string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_preview_orig_{Guid.NewGuid():N}.jpg");
                            await File.WriteAllBytesAsync(tempPath, origBytes);
                            imagePath = tempPath;
                        }
                    }
                }
            }
            else if (!string.IsNullOrEmpty(frame.FullFramePath) && File.Exists(frame.FullFramePath))
            {
                // 视频帧：使用 ffmpeg 提取的全分辨率帧 JPEG
                imagePath = frame.FullFramePath;
            }

            if (string.IsNullOrEmpty(imagePath)) return;

            await LoadPreviewAsync(imagePath);
        }

        /// <summary>
        /// Rebuilt property path. All media facts, including dimensions and
        /// motion-video identity, come from the Native source inspector.
        /// </summary>
        private async Task LoadRebuiltPropertiesAsync(
            string imagePath, string? videoPath, int generation, CancellationToken token)
        {
            try
            {
                var inspector = new SourceInspector();
                var facts = await inspector.InspectAsync(imagePath, videoPath, token).ConfigureAwait(false);
                VideoFacts? videoFacts = facts.MotionVideo;

                EditFileItem? selectedItem = FileItems.FirstOrDefault(f =>
                    string.Equals(f.FilePath, imagePath, StringComparison.OrdinalIgnoreCase));

                LivePhotoType livePhotoType = (CurrentDocument != null && string.Equals(CurrentDocument.PrimaryPath, imagePath, StringComparison.OrdinalIgnoreCase))
                    ? CurrentDocument.LivePhotoType
                    : (selectedItem?.LivePhotoType ?? (IsSelectedFileVideo ? LivePhotoType.None : LivePhotoType.None));

                EditPairState pairState = EditPairState.NotApplicable;
                if (livePhotoType == LivePhotoType.DualFile)
                {
                    pairState = (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                        ? EditPairState.Complete
                        : EditPairState.MissingVideo;
                }

                var doc = EditDocumentMapper.FromInspectedFacts(
                    imagePath,
                    videoPath,
                    livePhotoType,
                    pairState,
                    facts);

                var dispatcher = App.MainWindow?.DispatcherQueue;
                dispatcher?.TryEnqueue(() =>
                {
                    if (generation != _selectionGeneration) return;
                    if (!IsSelectedFileVideo && doc.Width > 0 && doc.Height > 0)
                    {
                        if (selectedItem != null && string.IsNullOrEmpty(selectedItem.Resolution))
                            selectedItem.Resolution = $"{doc.Width} × {doc.Height}";
                    }
                    CurrentDocument = doc;
                    SessionState = EditSessionState.Ready;
                });
            }
            catch (OperationCanceledException) { }
            catch (SourceInspectionException ex)
            {
                LogService.FileOp(
                    $"Timeline[LoadProps] Rebuilt Native inspection failed " +
                    $"({ex.Category}/{ex.Stage}, capability=0x{ex.Capability:X}): {ex.Message}",
                    LogLevel.Warning);
                throw;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Timeline[LoadProps] Rebuilt Native inspection failed: {ex.Message}", LogLevel.Warning);
                throw;
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
            InvalidateCurrentOpenSession();
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
            InvalidateCurrentOpenSession();
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            IsScanning = true;

            // 切换到新目录 → 清空旧文件帧缩略图缓存 + 大图预览缓存 + 清空旧文件信息
            _thumbnailCache.Clear();
            _thumbnailCacheOrder.Clear();
            _previewService.Clear();
            ClearFileInfo();

            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                LogService.FileOp($"KeyPhoto scan started: '{directoryPath}'");

                // 阶段 1：文件发现。仅消费 Native inspector 确认的单文件事实，
                // 双文件配对不放这里——靠文件名碰运气不严谨，统一在 Phase 2 用 ContentIdentifier 严格匹配。
                var discoveryResult = await Task.Run(
                    () => LivePhotoDiscoveryService.ScanAsync(
                        directoryPath,
                        DiscoveryScanMode.JpegMarkers | DiscoveryScanMode.HeicTrack |
                        DiscoveryScanMode.CidMatch | DiscoveryScanMode.VivoMatch, token),
                    token);

                if (token.IsCancellationRequested) return;

                // 分离图片和视频：列表只显示图片，视频路径单独收集供 Phase 2 CID 匹配
                // 预建视频大小查找表，双文件实况照片的 FileSize 需合并图片+视频
                var videoSizeLookup = discoveryResult.Items
                    .Where(d => SupportedVideoExtensions.Contains(Path.GetExtension(d.FilePath)))
                    .ToDictionary(d => d.FilePath, d => d.FileSizeBytes, StringComparer.OrdinalIgnoreCase);

                var files = (await Task.WhenAll(discoveryResult.Items
                    .Where(d => !SupportedVideoExtensions.Contains(Path.GetExtension(d.FilePath)))
                    .Select(async d =>
                    {
                        bool confirmed = d.LivePhotoType is LivePhotoType.SingleFileJpeg
                            or LivePhotoType.SingleFileHeic
                            or LivePhotoType.DualFile;
                        // 双文件实况照片：计算图片+视频的合并大小
                        long totalBytes = d.FileSizeBytes;
                        if (!string.IsNullOrEmpty(d.PairedVideoPath)
                            && videoSizeLookup.TryGetValue(d.PairedVideoPath, out long vidBytes))
                        {
                            totalBytes += vidBytes;
                        }

                        // 协议检测：结果必须来自 Native SourceInspector；UI 不再
                        // 不读取 managed XMP/尾标，也不把文件名当作协议证据。
                        var protocol = LivePhotoProtocolType.Unknown;
                        long motionVideoOffset = 0;
                        long motionVideoLength = 0;
                        VideoContainer motionVideoContainer = VideoContainer.Unknown;
                        if (confirmed)
                        {
                            var facts = await new SourceInspector().InspectAsync(
                                d.FilePath, d.PairedVideoPath, CancellationToken.None).ConfigureAwait(false);
                            if (facts.PrimaryImage is not { IsPresent: true } ||
                                facts.Protocol is SourceProtocol.NonLive or SourceProtocol.Unknown ||
                                facts.MotionVideo is not { IsPresent: true } motionVideo ||
                                (d.LivePhotoType == LivePhotoType.DualFile && motionVideo.SourceIndex != 1) ||
                                (d.LivePhotoType != LivePhotoType.DualFile && motionVideo.SourceIndex != 0))
                            {
                                throw new SourceInspectionException(
                                    SourceInspectionFailureCategory.Malformed,
                                    SourceInspectionStage.Pairing,
                                    0,
                                    $"Native facts did not confirm the discovered source '{d.FilePath}'.");
                            }
                            protocol = MapRebuiltProtocol(facts.Protocol);
                            motionVideoOffset = motionVideo.ByteOffset;
                            motionVideoLength = motionVideo.ByteLength;
                            motionVideoContainer = motionVideo.Container;
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
                            AppendedVideoLength = motionVideoLength,
                            DetectionMethod = d.DetectionMethod,
                            DetectedProtocol = protocol,
                            MotionVideoByteOffset = motionVideoOffset,
                            MotionVideoByteLength = motionVideoLength,
                            MotionVideoContainer = motionVideoContainer,
                        };
                    }))).ToList();

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
            catch (SourceInspectionException ex)
            {
                LastSourceInspectionFailure = ex;
                LogService.FileOp(
                    $"KeyPhoto Native inspection failed ({ex.Category}/{ex.Stage}, capability=0x{ex.Capability:X}): {ex.Message}",
                    LogLevel.Error,
                    ex);
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
        /// Read dimensions and paired-source facts from Native inspection.
        /// </summary>
        private async Task ReadResolutionsAsync(List<EditFileItem> files, List<string> videoPaths, CancellationToken token)
        {
            await ReadRebuiltResolutionsAsync(files, token).ConfigureAwait(false);
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
        private async Task<string?> LoadDroppedFilesRebuiltAsync(List<string> filePaths)
        {
            IsScanning = true;
            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) return null;

                var candidates = await _openPipeline.DiscoverCandidatesAsync(filePaths).ConfigureAwait(false);
                if (candidates.Count == 0) return null;

                var firstCandidate = candidates[0];
                string firstCandidateToOpen = firstCandidate.PrimaryPath;
                string? firstCandidateMotionPath = firstCandidate.MotionPath;

                var toAdd = new List<EditFileItem>();
                foreach (var c in candidates)
                {
                    string ext = Path.GetExtension(c.PrimaryPath);
                    LivePhotoDetectionMethod method = c.LivePhotoType switch
                    {
                        LivePhotoType.DualFile => c.Protocol == SourceProtocol.VivoLegacyDualFile
                            ? LivePhotoDetectionMethod.VivoLivePhoto
                            : LivePhotoDetectionMethod.ContentIdentifier,
                        LivePhotoType.SingleFileJpeg => LivePhotoDetectionMethod.JpegByteMarkers,
                        LivePhotoType.SingleFileHeic => LivePhotoDetectionMethod.HeicVideoTrack,
                        _ => LivePhotoDetectionMethod.FilenamePairing
                    };

                    toAdd.Add(new EditFileItem
                    {
                        FileName = Path.GetFileName(c.PrimaryPath),
                        FilePath = c.PrimaryPath,
                        FileSize = FileSizeFormatter.Format(c.TotalByteSize),
                        DateTaken = c.DateModified.ToString("yyyy/MM/dd HH:mm"),
                        LivePhotoType = c.LivePhotoType,
                        PairedVideoPath = c.MotionPath,
                        AppendedVideoLength = c.AppendedVideoLength,
                        DetectionMethod = method,
                        DetectedProtocol = MapRebuiltProtocol(c.Protocol),
                        Resolution = string.Empty
                    });
                }

                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool queued = dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        bool anyAdded = false;
                        string? firstNewItemAdded = null;
                        foreach (var item in toAdd)
                        {
                            if (FileItems.Any(f => string.Equals(f.FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase)))
                                continue;
                            FileItems.Add(item);
                            _allFileItems.Add(item);
                            anyAdded = true;
                            firstNewItemAdded ??= item.FilePath;
                        }

                        if (anyAdded)
                        {
                            RefreshCounts();
                            ApplySortAndFilter();
                        }

                        // 3B. 第一个有效 candidate 无论是否已存在于列表中，都作为本次 Drop 的打开目标
                        await OpenMediaAsync(new EditOpenRequest(firstCandidateToOpen, firstCandidateMotionPath));

                        tcs.SetResult(firstCandidateToOpen);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                });

                if (!queued)
                {
                    tcs.TrySetCanceled();
                    return null;
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (SourceInspectionException ex)
            {
                LogService.FileOp(
                    $"Drop[Rebuilt] Native inspection failed " +
                    $"({ex.Category}/{ex.Stage}, capability=0x{ex.Capability:X}): {ex.Message}",
                    LogLevel.Warning);
                throw;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Drop[Rebuilt] failed: {ex.Message}", LogLevel.Warning);
                throw;
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
            var inspector = new SourceInspector();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var facts = await inspector.InspectAsync(file.FilePath, file.PairedVideoPath, token).ConfigureAwait(false);
                if (facts.PrimaryImage is { IsPresent: true, Width: > 0, Height: > 0 } imageFacts)
                    file.Resolution = $"{imageFacts.Width} × {imageFacts.Height}";
            }
        }

    }
}
