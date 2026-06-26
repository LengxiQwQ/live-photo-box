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
    // XMP 中已包含 LivePhotoBox 命名空间标记（WrapXmp 注入），无需事后用 exiftool 补写。
    // （事后 exiftool 重写 XMP 会剥离协议命名空间属性，破坏拆分服务的偏移量解析。）
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
        // 步骤：获取协议 → 构建 XMP 元数据（含视频大小） → 写入底层文件结构。
        // XMP 中已包含 LivePhotoBox 命名空间标记，无需事后补写 dc:subject。
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

            // 注意：不在此处调用 TryWriteLivePhotoBoxMarkerAsync 写 dc:subject。
            // WrapXmp 已在 XMP 中注入了 LivePhotoBox 命名空间标记（Action/Protocol/Version），
            // 足够标识本应用生成的文件。若再用 exiftool 追加 dc:subject，exiftool 重写 XMP
            // 时会剥离 GCamera / Container / OpCamera 等协议命名空间属性，
            // 导致拆分服务无法解析视频偏移量（"No motion video length metadata found"）。
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

            // Validate source image (async-safe — no sync ReadByte on async stream)
            byte[] soiCheck = new byte[2];
            {
                using var imgCheckFs = new FileStream(
                    sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
                if (imgCheckFs.Length < 2)
                {
                    LogService.Merge($"Empty or invalid JPEG file: {sourceImg}", LogLevel.Error);
                    throw new InvalidDataException(ResourceService.GetString("Error_InvalidJpegFile"));
                }
                await imgCheckFs.ReadExactlyAsync(soiCheck, 0, 2, token);
                if (soiCheck[0] != 0xFF || soiCheck[1] != 0xD8)
                {
                    LogService.Merge($"Invalid JPEG file (no SOI): {sourceImg}", LogLevel.Error);
                    throw new InvalidDataException(ResourceService.GetString("Error_InvalidJpegFile"));
                }
            }

            // Build the complete JPEG prefix: SOI + APP1 marker + segment length + XMP header
            // as a SINGLE byte array written with one WriteAsync call.
            // Do NOT mix sync WriteByte with async WriteAsync on the same FileStream —
            // they use different I/O code paths and the OS may reorder the writes,
            // causing the XMP segment to land AFTER the source image data instead of before it.
            byte[] prefix = new byte[4 + 2 + XmpHeader.Length];
            prefix[0] = 0xFF; prefix[1] = 0xD8;               // SOI
            prefix[2] = 0xFF; prefix[3] = 0xE1;               // APP1 marker
            prefix[4] = (byte)(segmentLength >> 8);           // segment length hi
            prefix[5] = (byte)(segmentLength & 0xFF);         // segment length lo
            Array.Copy(XmpHeader, 0, prefix, 6, XmpHeader.Length);   // XMP header

            using var targetFs = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true);

            await targetFs.WriteAsync(prefix, 0, prefix.Length, token);
            await targetFs.WriteAsync(xmpBytes, 0, xmpBytes.Length, token);

            // Copy the rest of the source JPEG (skipping its SOI which we already wrote)
            using var imgFs = new FileStream(
                sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);
            imgFs.Position = 2;  // skip source JPEG's SOI
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
