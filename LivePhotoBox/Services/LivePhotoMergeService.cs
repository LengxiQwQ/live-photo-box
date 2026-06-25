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
    // 实况照片合并（合成）服务。
    // 负责将图片与视频合成为符合各厂商协议的实况照片文件（JPEG + XMP + 视频尾插）。
    // 核心方法 <see cref="WriteLivePhotoAsync"/> 按协议构建 XMP 元数据并写入 APP1 段，
    // <see cref="WriteNativeAsync"/> 实现底层文件结构：SOI + APP1(XMP) + JPEG 其余部分 + 视频。
    // 同时在写入后通过 <see cref="LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync"/>
    // 追加 LivePhotoBox 操作标记。
    public static class LivePhotoMergeService
    {
        // 生成实况照片输出文件名（固定为 "{baseName}.jpg"）。
        // baseName: 文件名基础部分（不含扩展名）。
        // selectedModeIndex: 协议索引（当前不影响文件名）。
        public static string CreateOutputFileName(string baseName, int selectedModeIndex)
        {
            return $"{baseName}.jpg";
        }

        // 将图片和视频合成为实况照片文件。
        // 步骤：获取协议 → 构建 XMP 元数据（含视频大小） → 写入底层文件结构 → 追加操作标记。
        // sourceImg: 源图片路径（JPEG/HEIC）。
        // sourceVid: 源视频路径（MOV/MP4）。
        // targetPath: 目标文件路径。
        // selectedModeIndex: 选中的协议索引。
        // token: 取消令牌。
        public static async Task WriteLivePhotoAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            int selectedModeIndex,
            CancellationToken token)
        {
            var protocol = LivePhotoProtocol.FromIndex(selectedModeIndex);
            long videoSize = new FileInfo(sourceVid).Length;
            byte[] xmpBytes = protocol.BuildXmpMetadata(videoSize);
            await WriteNativeAsync(sourceImg, sourceVid, targetPath, xmpBytes, token);

            // 追加 dc:subject 操作记录（与 Split/Repair 统一格式）
            string details = !string.IsNullOrEmpty(protocol.Key) ? $"Protocol={protocol.Key}" : "";
            await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                targetPath, "Merge", details, token);
        }

        // Write the combined JPEG + XMP + video file.
        // Image pre-processing (e.g. OPPO EXIF injection) is expected to be handled
        // by the caller via <see cref="LivePhotoProtocol.PrepareImageAsync"/>.
        public static async Task WriteNativeAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            byte[] xmpBytes,
            CancellationToken token)
        {
            int segmentLength = 2 + XmpHeader.Length + xmpBytes.Length;
            if (segmentLength > ushort.MaxValue)
            {
                LogService.Merge($"XMP metadata too large: {segmentLength} bytes", LogLevel.Error);
                throw new InvalidOperationException(
                    ResourceService.Format("Error_XmpMetadataTooLarge", segmentLength));
            }

            using var imgFs = new FileStream(
                sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);

            if (imgFs.Length < 2 || imgFs.ReadByte() != 0xFF || imgFs.ReadByte() != 0xD8)
            {
                LogService.Merge($"Invalid JPEG file: {sourceImg}", LogLevel.Error);
                throw new InvalidDataException(ResourceService.GetString("Error_InvalidJpegFile"));
            }

            using var targetFs = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true);

            // Write SOI
            targetFs.WriteByte(0xFF);
            targetFs.WriteByte(0xD8);

            // Write APP1 XMP
            targetFs.WriteByte(0xFF);
            targetFs.WriteByte(0xE1);
            targetFs.WriteByte((byte)(segmentLength >> 8));
            targetFs.WriteByte((byte)(segmentLength & 0xFF));
            await targetFs.WriteAsync(XmpHeader, 0, XmpHeader.Length, token);
            await targetFs.WriteAsync(xmpBytes, 0, xmpBytes.Length, token);

            // Copy the rest of the JPEG (excluding SOI which we already wrote)
            await imgFs.CopyToAsync(targetFs, token);

            // Append video
            using var vidFs = new FileStream(
                sourceVid, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);
            await vidFs.CopyToAsync(targetFs, token);
        }

                // Adobe XMP APP1 segment header (29 bytes including NUL).
        private static readonly byte[] XmpHeader =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    }
}
