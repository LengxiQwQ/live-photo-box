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
        /// <summary>图片 — 旋转修正：Native 优先无损转正；三星无法完整无损时高质量重编码。</summary>
        public bool FixImageRotation { get; set; } = true;

        /// <summary>图片 — 缩略图剥离：Native 精确清理已确认的嵌入缩略图和预览图。</summary>
        public bool StripImageThumbnail { get; set; } = true;

        /// <summary>图片 — HEIC 方向修正：Native 纠正 HEIC/HEIF 方向；三星同时物理转正。</summary>
        public bool FixHeicOrientation { get; set; } = true;

        /// <summary>视频 — 旋转烘焙：Native 媒体后端将方向应用到画面；三星包含主视频和自动播放视频。</summary>
        public bool FixVideoRotation { get; set; } = true;
    }
}
