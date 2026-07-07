/*
 * KeyPhotoFileItem.cs
 *
 * 实况照片主图更换页面 — 左侧资源浏览列表中每一项的数据模型。
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

namespace LivePhotoBox.Models
{
    public partial class KeyPhotoFileItem : ObservableObject
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

        /// <summary>是否有已确认的实况照片协议（有真实协议标记，非纯文件名碰运气）</summary>
        private bool _hasConfirmedProtocol;
        public bool HasConfirmedProtocol
        {
            get => _hasConfirmedProtocol;
            set
            {
                if (SetProperty(ref _hasConfirmedProtocol, value))
                    OnPropertyChanged(nameof(LivePhotoBadgeVisibility));
            }
        }

        /// <summary>LIVE 徽标可见性：仅有已确认协议的文件才显示</summary>
        public Visibility LivePhotoBadgeVisibility =>
            HasConfirmedProtocol ? Visibility.Visible : Visibility.Collapsed;

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
        //  缩略图（抄 SplitTask 的 TryGetOrLoad 懒加载模式）
        // ══════════════════════════════════════════════════════════════

        private bool _isLoadingThumbnail;
        private ImageSource? _thumbnail;

        /// <summary>缩略图解码目标尺寸：匹配 UI 框 56×56</summary>
        private const uint ThumbnailTargetSize = 112;

        /// <summary>
        /// 文件缩略图（懒加载，按 56px 解码匹配 56×56 显示框）。
        /// 首次访问时通过 ThumbnailService.TryGetOrLoad 触发后台加载，
        /// 加载完成后自动回写此属性并触发 PropertyChanged → UI 刷新。
        /// </summary>
        public ImageSource? Thumbnail
        {
            get => ThumbnailService.TryGetOrLoad(
                ref _thumbnail, ref _isLoadingThumbnail, FilePath,
                value => Thumbnail = value,
                ThumbnailTargetSize);
            set
            {
                if (SetProperty(ref _thumbnail, value))
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        /// <summary>缩略图占位符可见性：加载中/失败时显示占位图标，加载完成后隐藏</summary>
        public Visibility ThumbnailPlaceholderVisibility =>
            ThumbnailService.GetPlaceholderVisibility(_thumbnail);

        /// <summary>文件路径变更时重置缩略图状态</summary>
        partial void OnFilePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = null;
        }

        /// <summary>清除缩略图引用（扫描新目录时调用，避免旧缓存干扰）</summary>
        public void ClearThumbnail()
        {
            _isLoadingThumbnail = false;
            Thumbnail = null;
        }
    }
}
