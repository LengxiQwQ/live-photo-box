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
    public static class LivePhotoMergeService
    {
        public static string CreateOutputFileName(string baseName, int selectedModeIndex)
        {
            return $"{baseName}.jpg";
        }

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

        /// <summary>
        /// Write the combined JPEG + XMP + video file.
        /// Image pre-processing (e.g. OPPO EXIF injection) is expected to be handled
        /// by the caller via <see cref="LivePhotoProtocol.PrepareImageAsync"/>.
        /// </summary>
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

        /// <summary>Adobe XMP APP1 segment header (29 bytes including NUL).</summary>
        private static readonly byte[] XmpHeader =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    }
}
