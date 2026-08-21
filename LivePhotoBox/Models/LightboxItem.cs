/*
 * LightboxItem.cs
 *
 * 灯箱预览中的单条目数据模型。封装图片路径和 Live Photo 视频源信息，
 * 支持两种实况照片模式：
 *   - 配对文件模式：VideoPath 指向独立的视频文件
 *   - 单文件实况模式：AppendedVideoLength 标记 JPEG 尾部追加的视频段长度
 */

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 灯箱条目，承载图片路径和可选的 Live Photo 视频源。
    /// IsLivePhoto 用于灯箱判断是否显示 LIVE 播放按钮。
    /// </summary>
    public sealed class LightboxItem
    {
        /// <summary>要显示的图片文件路径。</summary>
        public required string ImagePath { get; init; }

        /// <summary>配对视频文件的路径（模式 A）。非 null 即表示有配对视频。</summary>
        public string? VideoPath { get; init; }

        /// <summary>JPEG 尾部追加的 MP4 视频段字节数（模式 B）。> 0 表示是单文件实况。
        /// 可在后台探测到单文件实况后由 setter 更新。</summary>
        public long AppendedVideoLength { get; set; }

        // 后台探测到实况后置 true 的补充标记（与 VideoPath/AppendedVideoLength 逻辑或）
        private bool _liveDetected;

        /// <summary>是否为 Live Photo，决定灯箱中是否显示 LIVE 按钮。
        /// 构造时由 VideoPath/AppendedVideoLength 决定；后台探测到单文件实况后置 true。</summary>
        public bool IsLivePhoto
        {
            get => _liveDetected || VideoPath != null || AppendedVideoLength > 0;
            set => _liveDetected = value;
        }
    }
}
