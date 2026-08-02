using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    // 实况照片合成服务（兼容层）。
    // 提供输出文件名生成与实况照片写入的入口，
    // 内部实际委托给 <see cref="LivePhotoMergeService"/> 的共享实现。
    public static class LivePhotoCompositionService
    {
        // Generate the live photo output filename.
        // Delegates to <see cref="LivePhotoMergeService.CreateOutputFileName"/>.
        // baseName: Filename base (without extension).
        // selectedModeIndex: Protocol index.
        // sourceImagePath: Optional source image path for HEIC detection.
        public static string CreateOutputFileName(string baseName, int selectedModeIndex,
            string? sourceImagePath = null)
        {
            return LivePhotoMergeService.CreateOutputFileName(baseName, selectedModeIndex, sourceImagePath);
        }

        // 将图片和视频合成为实况照片文件。
        // 本例中委托给 <see cref="LivePhotoMergeService.WriteLivePhotoAsync"/> 的共享实现。
        // sourceImg: 源图片路径。
        // sourceVid: 源视频路径。
        // targetPath: 目标文件路径。
        // selectedModeIndex: 协议索引。
        // token: 取消令牌。
        public static async Task WriteLivePhotoAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            int selectedModeIndex,
            CancellationToken token,
            int outputFormatIndex = 0)
        {
            // Delegate to the shared implementation — same logic, same protocol support.
            await LivePhotoMergeService.WriteLivePhotoAsync(
                sourceImg, sourceVid, targetPath, selectedModeIndex, token,
                outputFormatIndex: outputFormatIndex);
        }
    }
}
