/*
 * EditFileItem.cs
 *
 * 实况照片封面更换页面 — 左侧资源浏览列表中每一项的数据模型。
 * 每个条目显示：缩略图 + 文件名（Bold）+ 分辨率/大小 + 日期。
 *
 * 继承 ObservableObject，支持 MVVM 属性变更通知。
 * 缩略图采用 ThumbnailService.TryGetOrLoad 懒加载模式（抄 SplitTask），
 * 自动走 Shell/BitmapDecoder/FFmpeg 三通道，带缓存和并发控制。
 *
 * 数据来源：KeyPhotoViewModel.ScanDirectoryAsync() 扫描真实文件系统。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.IO;

namespace LivePhotoBox.Models
{
    public partial class EditFileItem : ObservableObject
    {
        /// <summary>文件名（含扩展名），如 "IMG_8842.HEIC"</summary>
        [ObservableProperty]
        private string _fileName = string.Empty;

        /// <summary>文件完整路径</summary>
        [ObservableProperty]
        private string _filePath = string.Empty;

        /// <summary>文件大小格式化文本，如 "6.3 MB"</summary>
        [ObservableProperty]
        private string _fileSize = string.Empty;

        /// <summary>分辨率文本，如 "4032 × 3024"（exiftool 阶段 2 填充）</summary>
        private string _resolution = string.Empty;
        public string Resolution
        {
            get => _resolution;
            set { if (SetProperty(ref _resolution, value)) { OnPropertyChanged(nameof(Resolution)); OnPropertyChanged(nameof(FileInfoSubLine)); } }
        }

        /// <summary>拍摄/修改日期，如 "2024/12/15 14:32"</summary>
        [ObservableProperty]
        private string _dateTaken = string.Empty;

        // ══════════════════════════════════════════════════════════════
        //  实况照片分类（扫描阶段由 LivePhotoDiscoveryService 填充）
        // ══════════════════════════════════════════════════════════════

        /// <summary>实况照片类型（None = 普通文件）</summary>
        public LivePhotoType LivePhotoType { get; set; } = LivePhotoType.None;

        /// <summary>双文件实况照片：配对的视频路径</summary>
        public string? PairedVideoPath { get; set; }

        /// <summary>单文件 JPEG 实况照片：内嵌视频段字节数</summary>
        public long AppendedVideoLength { get; set; }

        /// <summary>检测方法（区分 Apple CID 配对 vs 纯文件名配对等）</summary>
        public LivePhotoDetectionMethod DetectionMethod { get; set; }

        /// <summary>检测到的实况照片协议类型（Unknown = 非实况或未识别）</summary>
        private LivePhotoProtocolType _detectedProtocol;
        public LivePhotoProtocolType DetectedProtocol
        {
            get => _detectedProtocol;
            set
            {
                if (SetProperty(ref _detectedProtocol, value))
                {
                    // 协议变更 → 同步刷新相关属性
                    OnPropertyChanged(nameof(HasConfirmedProtocol));
                    OnPropertyChanged(nameof(LivePhotoBadgeVisibility));
                    OnPropertyChanged(nameof(LiveBadgeBackground));
                    OnPropertyChanged(nameof(DisplayFileName));
                    OnPropertyChanged(nameof(FullDisplayFileName));
                }
            }
        }

        /// <summary>是否有已确认的实况照片协议（有真实协议标记，非纯文件名碰运气）</summary>
        public bool HasConfirmedProtocol
        {
            get => DetectedProtocol != LivePhotoProtocolType.Unknown;
            set
            {
                // 兼容旧代码：HasConfirmedProtocol = true → 不覆盖已有协议类型
                // HasConfirmedProtocol = false → 重置为 Unknown
                if (!value)
                    DetectedProtocol = LivePhotoProtocolType.Unknown;
                // value=true 时保持现有协议类型不变（调用方应改用 SetConfirmedProtocol）
            }
        }

        /// <summary>设置已确认协议（兼容旧代码 — 有协议类型时直接设 DetectedProtocol）</summary>
        public void SetConfirmedProtocol(LivePhotoProtocolType protocol)
        {
            DetectedProtocol = protocol;
        }

        // ══════════════════════════════════════════════════════════════
        //  LIVE 徽标颜色（写死不跟随主题色）
        // ══════════════════════════════════════════════════════════════

        /// <summary>完整实况 → 蓝色 #0078D4（与左上角 LIVE 按钮图标同色）</summary>
        private static readonly SolidColorBrush LiveCompleteBrush =
            new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));

        /// <summary>缺失配对 → 暖黄色（比纯黄偏橙，白字可读）</summary>
        private static readonly SolidColorBrush LiveMissingBrush =
            new(Windows.UI.Color.FromArgb(0xFF, 0xD4, 0x88, 0x0A));

        /// <summary>配对是否不完整（有协议但缺对方文件）</summary>
        public bool IsPairIncomplete =>
            HasConfirmedProtocol
            && LivePhotoType == LivePhotoType.DualFile
            && (string.IsNullOrEmpty(PairedVideoPath) || !File.Exists(PairedVideoPath));

        /// <summary>LIVE 徽标背景色：完整=蓝，缺失配对=暖黄</summary>
        public SolidColorBrush LiveBadgeBackground =>
            IsPairIncomplete ? LiveMissingBrush : LiveCompleteBrush;

        /// <summary>
        /// LIVE 徽标可见性：有已确认协议即显示。
        /// 双文件实况即使配对文件暂时缺失，徽标仍然保留——属性面板会额外标注"(未找到配对视频)"。
        /// </summary>
        public Visibility LivePhotoBadgeVisibility =>
            HasConfirmedProtocol ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 格式化显示用的文件名：双文件实况照片用括号（.HEIC+MOV）；其余正常显示后缀。
        /// 文件名 > 19 字符时截断：双文件仅前面，普通/单文件 前19 + "…" + 后4。
        /// </summary>
        public static string FormatDisplayFileName(string fileName, bool isDualFileLivePhoto, string? videoExtension = null)
        {
            if (string.IsNullOrEmpty(fileName)) return string.Empty;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string imageExt = Path.GetExtension(fileName).ToUpperInvariant(); // 含点，如 ".JPG"

            if (isDualFileLivePhoto && !string.IsNullOrEmpty(videoExtension))
            {
                // 双文件实况照片：括号括起双后缀，仅前面截断
                string truncatedName = nameWithoutExt.Length > 19
                    ? nameWithoutExt[..19] + "…"
                    : nameWithoutExt;
                string imgNoDot = imageExt.TrimStart('.');
                string vidNoDot = videoExtension.TrimStart('.').ToUpperInvariant();
                return $"{truncatedName} ({imgNoDot}+{vidNoDot})";
            }
            else
            {
                // 普通照片 / 单文件实况：正常 .后缀，前后都保留
                if (nameWithoutExt.Length > 19)
                    return nameWithoutExt[..19] + "…" + nameWithoutExt[^4..] + imageExt;
                else
                    return nameWithoutExt + imageExt;
            }
        }

        /// <summary>
        /// 与 <see cref="FormatDisplayFileName"/> 相同但**不截断**，返回完整显示名；
        /// 供 <see cref="LivePhotoBox.Controls.AdaptiveFileName"/> 按实际宽度自适应省略。
        /// </summary>
        public static string FormatFullDisplayFileName(string fileName, bool isDualFileLivePhoto, string? videoExtension = null)
        {
            if (string.IsNullOrEmpty(fileName)) return string.Empty;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string imageExt = Path.GetExtension(fileName).ToUpperInvariant(); // 含点，如 ".JPG"

            if (isDualFileLivePhoto && !string.IsNullOrEmpty(videoExtension))
            {
                // 双文件实况照片：括号括起双后缀，不截断
                string imgNoDot = imageExt.TrimStart('.');
                string vidNoDot = videoExtension.TrimStart('.').ToUpperInvariant();
                return $"{nameWithoutExt} ({imgNoDot}+{vidNoDot})";
            }

            // 普通照片 / 单文件实况：正常 .后缀，不截断
            return nameWithoutExt + imageExt;
        }

        /// <summary>是否为双文件实况照片（DualFile 且已确认协议）</summary>
        public bool IsDualFileLivePhoto => HasConfirmedProtocol && LivePhotoType == LivePhotoType.DualFile;

        /// <summary>实况照片对应的视频扩展名（含点），如 ".MOV"；非实况返回 null</summary>
        public string? VideoExtension => HasConfirmedProtocol
            ? LivePhotoType switch
            {
                LivePhotoType.DualFile => Path.GetExtension(PairedVideoPath ?? ""),
                LivePhotoType.SingleFileJpeg => ".MP4",
                LivePhotoType.SingleFileHeic => ".MOV",
                _ => null
            }
            : null;

        /// <summary>列表显示用文件名，绑定到 ListView</summary>
        public string DisplayFileName => FormatDisplayFileName(FileName, IsDualFileLivePhoto, VideoExtension);

        /// <summary>未截断的完整显示名，供宽度自适应控件使用（够宽显示完整，不够时控件自己中间省略）</summary>
        public string FullDisplayFileName => FormatFullDisplayFileName(FileName, IsDualFileLivePhoto, VideoExtension);

        /// <summary>ListView 子行：分辨率 │ 大小（合成属性，避免 Run 元素 x:Bind 不支持 OneWay 的问题）</summary>
        public string FileInfoSubLine
        {
            get
            {
                if (string.IsNullOrEmpty(Resolution))
                    return FileSize;
                if (string.IsNullOrEmpty(FileSize))
                    return Resolution;
                return $"{Resolution}  │  {FileSize}";
            }
        }

        partial void OnFileSizeChanged(string value) => OnPropertyChanged(nameof(FileInfoSubLine));

        // ══════════════════════════════════════════════════════════════
        //  缩略图 — 被动模式，由 ThumbnailScheduler 集中调度加载
        //  NeedsThumbnail 直接查 Scheduler._inFlight，不用本地 bool
        //  避免 TrimQueue 踢掉 item 后本地标记不会恢复的问题
        // ══════════════════════════════════════════════════════════════

        private ImageSource? _thumbnail;

        /// <summary>缩略图解码目标尺寸：匹配 UI 框 56×56</summary>
        public const uint ThumbnailTargetSize = 112;

        /// <summary>
        /// 文件缩略图。getter 只返回已加载或缓存的 ImageSource，不主动触发加载。
        /// 加载由 EditPage → ThumbnailScheduler 集中调度。
        /// </summary>
        public ImageSource? Thumbnail
        {
            get => _thumbnail ?? ThumbnailService.GetCached(FilePath);
            set
            {
                if (SetProperty(ref _thumbnail, value))
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
        }

        /// <summary>是否需要加载缩略图（由 Scheduler 的 _inFlight 集合决定，TrimQueue 自动恢复可加载状态）</summary>
        public bool NeedsThumbnail =>
            _thumbnail == null
            && ThumbnailService.GetCached(FilePath) == null
            && !ThumbnailScheduler.IsInFlight(FilePath);

        /// <summary>缩略图占位符可见性：加载中/失败时显示占位图标，加载完成后隐藏</summary>
        public Visibility ThumbnailPlaceholderVisibility =>
            ThumbnailService.GetPlaceholderVisibility(_thumbnail);

        /// <summary>文件路径变更时重置缩略图状态</summary>
        partial void OnFilePathChanged(string value)
        {
            Thumbnail = null;
        }

        /// <summary>清除缩略图引用（扫描新目录时调用，避免旧缓存干扰）</summary>
        public void ClearThumbnail()
        {
            Thumbnail = null;
        }
    }
}
