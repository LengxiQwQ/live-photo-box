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
        // 生成实况照片输出文件名（固定为 "{baseName}.jpg"）。
        // baseName: 文件名基础部分（不含扩展名）。
        // selectedModeIndex: 协议索引（仅用于接口一致性，当前未影响文件名）。
        public static string CreateOutputFileName(string baseName, int selectedModeIndex)
        {
            return $"{baseName}.jpg";
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
            CancellationToken token)
        {
            // Delegate to the shared implementation — same logic, same protocol support.
            await LivePhotoMergeService.WriteLivePhotoAsync(
                sourceImg, sourceVid, targetPath, selectedModeIndex, token);
        }
    }
}
