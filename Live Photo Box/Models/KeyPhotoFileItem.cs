/*
 * KeyPhotoFileItem.cs
 *
 * 实况照片主图更换页面 — 左侧资源浏览列表中每一项的数据模型。
 * 每个条目三行显示：缩略图占位 + 文件名（Bold）+ 分辨率/大小 + 日期。
 *
 * 当前阶段：仅 UI 展示，假数据。
 * 后续可扩展为绑定真实文件系统的 ObservableObject。
 */

namespace LivePhotoBox.Models
{
    public class KeyPhotoFileItem
    {
        /// <summary>文件名（含扩展名），如 "IMG_8842.HEIC"</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件完整路径（内部使用，不在列表中显示）</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>文件大小格式化文本，如 "6.3 MB"</summary>
        public string FileSize { get; set; } = string.Empty;

        /// <summary>分辨率文本，如 "4032 × 3024"</summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>拍摄日期，如 "2024/12/15 14:32"</summary>
        public string DateTaken { get; set; } = string.Empty;
    }
}
