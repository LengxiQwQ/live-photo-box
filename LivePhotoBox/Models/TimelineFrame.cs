/*
 * TimelineFrame.cs
 *
 * KeyPhoto 页面时间轴中每一帧的数据模型。
 * 每个条目代表时间轴中的一帧：视频帧显示数字序号，照片帧显示星标。
 * 帧序号、时间戳、缩略图 + 照片帧标记，
 * 为后续"选择某帧作为新封面"编辑功能做准备。
 *
 * 继承 ObservableObject，支持 MVVM 属性变更通知。
 * 缩略图懒加载：先创建占位 FrameInfo（显示序号/星标），
 * 后台 ffmpeg 提取完成后回填 Thumbnail。
 *
 * IsSelected 属性由 KeyPhotoViewModel 在选中帧变更时批量同步，
 * 供 XAML DataTemplate 内绑定或代码后置视觉刷新使用。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;

namespace LivePhotoBox.Models
{
    public partial class TimelineFrame : ObservableObject
    {
        /// <summary>0-based 帧序号（对应 ffmpeg 输出文件 frame_000001.jpg）</summary>
        public int FrameIndex { get; init; }

        /// <summary>视频内时间位置</summary>
        public TimeSpan Timestamp { get; init; }

        /// <summary>时间戳显示文本，如 "0:00.033"</summary>
        public string TimestampDisplay => Timestamp.ToString(@"s\.fff");

        /// <summary>是否为静态照片帧（非视频帧），显示星标而非数字</summary>
        public bool IsStillPhoto { get; set; }

        /// <summary>是否为原始封面帧（换封面之前的旧高清大图位置），显示 🖼 标记。OPPO/VIVO 均支持</summary>
        public bool IsOriginalPhoto { get; set; }

        /// <summary>角标显示文本：原始封面=🖼，照片帧=⭐，视频帧=数字（从1开始）</summary>
        public string FrameBadgeText => IsOriginalPhoto ? "🖼" : (IsStillPhoto ? "⭐" : $"{FrameIndex + 1}");

        /// <summary>角标背景色：原始封面=暗紫，照片帧=暗金，视频帧=半透明黑</summary>
        public SolidColorBrush FrameBadgeBackground => IsOriginalPhoto
            ? _originalPhotoBadgeBg
            : (IsStillPhoto
                ? _stillPhotoBadgeBg
                : _videoFrameBadgeBg);

        private static readonly SolidColorBrush _stillPhotoBadgeBg = new(
            Windows.UI.Color.FromArgb(0xDD, 0xD4, 0x8B, 0x00)); // 暗金
        private static readonly SolidColorBrush _originalPhotoBadgeBg = new(
            Windows.UI.Color.FromArgb(0xEE, 0xA7, 0x6C, 0xF5)); // 亮紫，与暗金对比明显
        private static readonly SolidColorBrush _videoFrameBadgeBg = new(
            Windows.UI.Color.FromArgb(0x80, 0x00, 0x00, 0x00)); // 半透明黑

        /// <summary>全分辨率帧 JPEG 文件路径，供 PhotoViewer 加载大图预览。照片帧为 null</summary>
        public string? FullFramePath { get; set; }

        /// <summary>帧缩略图（后台 ffmpeg 提取后回填，照片帧复用 SelectedFileThumbnail）</summary>
        [ObservableProperty]
        private ImageSource? _thumbnail;

        /// <summary>缩略图加载中显示 FontIcon 占位，加载完成后隐藏</summary>
        public Visibility ThumbnailPlaceholderVisibility =>
            Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 是否为当前选中帧。
        /// 经典模式：同步 ListView.SelectedItem → 驱动卡片 Border 选中态视觉。
        /// 胶片模式：由吸附逻辑同步，配合固定选中框覆盖确认数据一致。
        /// </summary>
        [ObservableProperty]
        private bool _isSelected;
    }
}
