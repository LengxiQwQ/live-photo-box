/*
 * LightboxItem.cs
 *
 * 灯箱预览中的单条目数据模型。封装图片路径和 Live Photo 视频源信息，
 * 覆盖所有实况照片协议：
 *   - 配对文件模式：VideoPath 指向独立的视频文件（Apple / vivo 旧格式等双文件协议）
 *   - 单文件实况模式：AppendedVideoLength 标记 JPEG 尾部追加的视频段长度（Google/OPPO/vivo 等）
 *   - 华为/荣耀/三星/融合：视频嵌在文件中段或 mpvd box，靠 DetectedProtocol 分流提取
 */

namespace LivePhotoBox.Models
{
    using LivePhotoBox.Services;
    /// <summary>
    /// 灯箱条目，承载图片路径和可选的 Live Photo 视频源。
    /// IsLivePhoto 用于灯箱判断是否显示 LIVE 播放按钮。
    /// </summary>
    public sealed class LightboxItem
    {
        /// <summary>要显示的图片文件路径。</summary>
        public required string ImagePath { get; init; }

        /// <summary>配对视频文件的路径（双文件实况）。非 null 即表示有配对视频。</summary>
        public string? VideoPath { get; init; }

        /// <summary>JPEG 尾部追加的 MP4 视频段字节数（尾部追加协议）。> 0 表示是单文件实况。</summary>
        public long AppendedVideoLength { get; init; }

        /// <summary>实况照片类型（None = 非实况 / DualFile / SingleFileJpeg / SingleFileHeic）。</summary>
        public LivePhotoType LivePhotoType { get; init; } = LivePhotoType.None;

        /// <summary>检测到的协议类型（Unknown = 非实况或未识别）。华为/三星/融合等无 XMP 长度的协议靠此识别。</summary>
        public LivePhotoProtocolType DetectedProtocol { get; init; } = LivePhotoProtocolType.Unknown;

        /// <summary>是否为 Live Photo，决定灯箱中是否显示 LIVE 按钮。
        /// 协议已识别或存在配对视频/尾部视频长度即视为实况。</summary>
        public bool IsLivePhoto =>
            DetectedProtocol != LivePhotoProtocolType.Unknown
            || VideoPath != null
            || AppendedVideoLength > 0;
    }
}
