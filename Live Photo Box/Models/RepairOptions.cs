/*
 * RepairOptions.cs
 *
 * 修复选项配置模型。用户在启动修复前通过对话框勾选需要执行的操作，
 * 所有选项默认开启。选项会传递到 LivePhotoRepairService 控制实际修复行为。
 */

namespace LivePhotoBox.Models
{
    /// <summary>
    /// 修复选项配置，控制修复过程中执行哪些操作。
    /// 用户在修复前通过对话框勾选，所有选项默认开启。
    /// </summary>
    public class RepairOptions
    {
        /// <summary>图片 — 旋转修正：jpegtran 无损旋转 + exiftool 重置方向标签（仅 JPEG）。</summary>
        public bool FixImageRotation { get; set; } = true;

        /// <summary>图片 — 缩略图剥离：exiftool 剥离内嵌缩略图和预览图，减小文件体积（仅 JPEG）。</summary>
        public bool StripImageThumbnail { get; set; } = true;

        /// <summary>图片 — HEIC 方向修正：exiftool 修正 Orientation 标记（仅 HEIC/HEIF）。</summary>
        public bool FixHeicOrientation { get; set; } = true;

        /// <summary>视频 — 旋转烘焙：FFmpeg 重编码将旋转矩阵烘焙到像素中（MOV/MP4）。</summary>
        public bool FixVideoRotation { get; set; } = true;
    }
}
