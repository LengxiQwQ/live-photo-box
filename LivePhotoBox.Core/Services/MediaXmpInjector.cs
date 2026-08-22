using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// MP4/MOV 视频的字节级 XMP 读写器。
    /// 华为相机写入的 moov/meta（covertime 等）结构 exiftool 解析不了
    /// （"Terminator found in Meta"），无法用 exiftool 追加 XMP；
    /// 按 MP4 规范把 XMP 存为文件顶层的 Adobe uuid box（与 exiftool 在
    /// MP4 中的读写位置一致，QuickTime.pm File 级 uuid 表），追加在文件末尾，
    /// 不触碰任何现有 box，不影响视频播放。
    /// </summary>
    public static class MediaXmpInjector
    {
        /// <summary>
        /// 在文件末尾追加 Adobe XMP uuid box（原子替换：先写临时文件，成功后覆盖）。
        /// 写后读回验证 XMP 存在，否则回滚。
        /// </summary>
        public static async Task<(bool Success, string? Error)> TryInjectXmpAsync(
            string filePath, byte[] xmpBytes, CancellationToken token)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath, token);
                byte[] box = BuildUuidBox(xmpBytes);
                byte[] result = new byte[bytes.Length + box.Length];
                Array.Copy(bytes, 0, result, 0, bytes.Length);
                Array.Copy(box, 0, result, bytes.Length, box.Length);

                string dir = Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory;
                string temp = Path.Combine(dir, $".lpb_mp4_xmp_{Guid.NewGuid():N}.mp4");
                try
                {
                    await File.WriteAllBytesAsync(temp, result, token);
                    File.Move(temp, filePath, overwrite: true);

                    string? readBack = await TryReadXmpTextAsync(filePath, token);
                    if (string.IsNullOrWhiteSpace(readBack) ||
                        !readBack.Contains("LivePhotoBox:", StringComparison.Ordinal))
                    {
                        await File.WriteAllBytesAsync(filePath, bytes, token); // 回滚
                        return (false, "XMP verification failed after injection; rolled back");
                    }
                    return (true, null);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 读取文件顶层 Adobe XMP uuid box 的 XMP 文本（原始 xpacket 包装）。
        /// 无 uuid XMP 或结构不认识时返回 null。
        /// </summary>
        public static async Task<string?> TryReadXmpTextAsync(
            string filePath, CancellationToken token)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath, token);
                byte[]? payload = ExtractXmpPayload(bytes);
                if (payload == null || payload.Length == 0) return null;

                int end = payload.Length;
                while (end > 0 && payload[end - 1] == 0) end--; // 去掉尾部 NUL 填充
                if (end == 0) return null;
                return Encoding.UTF8.GetString(payload, 0, end);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// 移除字节流中所有文件顶层的 Adobe XMP uuid box。
        /// 拆分产物视频自带顶层 uuid（供独立视频读取）；但当该视频被嵌入
        /// 华为合并产物时，这个 uuid 会变成合并文件顶层 box，exiftool 会把它
        /// 当作整个文件的 XMP 而盖过 meta 内 mime 条目的完整 XMP，导致读取歧义。
        /// 合并前先剥离，保证合并产物只有一份可读 XMP。
        /// </summary>
        public static byte[] StripTopLevelXmpUuid(byte[] bytes)
        {
            using var result = new MemoryStream(bytes.Length);
            int p = 0;
            while (p + 8 <= bytes.Length)
            {
                int size = ReadU32(bytes, p);
                if (size < 8 || p + size > bytes.Length) break;
                string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "uuid" && size >= 24 &&
                    bytes.AsSpan(p + 8, 16).SequenceEqual(HeicXmpInjector.AdobeXmpUsertype))
                {
                    p += size; // 跳过该 box
                    continue;
                }
                result.Write(bytes, p, size);
                p += size;
            }
            if (p < bytes.Length) result.Write(bytes, p, bytes.Length - p);
            return result.ToArray();
        }

        private static byte[] BuildUuidBox(byte[] xmpBytes)
        {
            byte[] box = new byte[8 + 16 + xmpBytes.Length];
            BinaryPrimitives.WriteInt32BigEndian(box.AsSpan(0, 4), box.Length);
            box[4] = (byte)'u'; box[5] = (byte)'u'; box[6] = (byte)'i'; box[7] = (byte)'d';
            Array.Copy(HeicXmpInjector.AdobeXmpUsertype, 0, box, 8, 16);
            Array.Copy(xmpBytes, 0, box, 24, xmpBytes.Length);
            return box;
        }

        /// <summary>
        /// 在顶层 box 链中找 usertype 为 Adobe XMP 的 uuid box，返回其 payload。
        /// </summary>
        private static byte[]? ExtractXmpPayload(byte[] bytes)
        {
            int p = 0;
            while (p + 8 <= bytes.Length)
            {
                int size = ReadU32(bytes, p);
                if (size < 8 || p + size > bytes.Length) break;
                string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "uuid" && size >= 24 &&
                    bytes.AsSpan(p + 8, 16).SequenceEqual(HeicXmpInjector.AdobeXmpUsertype))
                {
                    int payloadLen = size - 24;
                    var payload = new byte[payloadLen];
                    Array.Copy(bytes, p + 24, payload, 0, payloadLen);
                    return payload;
                }
                p += size;
            }
            return null;
        }

        private static int ReadU32(byte[] a, int off)
            => BinaryPrimitives.ReadInt32BigEndian(a.AsSpan(off, 4));
    }
}
