/*
 * ThumbnailStripItem.cs
 *
 * 灯箱底部缩略图导航条的数据项。每位条目绑定一个 LightboxItem 的缩略图，
 * ListView 虚拟化按需加载，避免一次性解码所有缩略图。
 */

using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 缩略图导航条的单一条目。ImagePath 用于延迟加载缩略图，
    /// Thumbnail 为加载完成后的 ImageSource，Index 指向 LightboxItem 列表位置。
    /// </summary>
    public sealed class ThumbnailStripItem
    {
        /// <summary>对应的图片文件路径，用于延迟加载缩略图。</summary>
        public string ImagePath { get; set; } = "";

        /// <summary>在 LightboxItem 列表中的索引，用于点击跳转。</summary>
        public int Index { get; set; }

        /// <summary>已加载的缩略图，null 表示尚未加载。</summary>
        public ImageSource? Thumbnail { get; set; }
    }
}
