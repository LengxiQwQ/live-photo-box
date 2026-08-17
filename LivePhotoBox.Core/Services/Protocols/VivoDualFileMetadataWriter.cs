namespace LivePhotoBox.Services.Protocols
{
    using LivePhotoBox.Models;
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// vivo 旧格式双文件（≤X200 系列）配对元数据写入器。
    /// JPEG 尾部追加 vivo{JSON} 私有尾标，MP4 尾部追加
    /// vivoMediaExtInfo uuid box；两端共享同一个 com.android.camera.livephoto ID。
    /// 字节结构依据 designs/各个机型测试/双文件/vivo双文件.jpg 与
    /// designs/各个机型测试/双文件/vivo双文件.mp4 逆向得出。
    /// </summary>
    public static class VivoDualFileMetadataWriter
    {
        /// <summary>JPEG 私有尾标 JSON（vivo{...} 整体）。</summary>
        private const string ImageJsonTemplate =
            "vivo{{\"com.vivo.gallery.livephoto.source\":4," +
            "\"com.vivo.gallery.livePhoto.rotationOffset\":0," +
            "\"com.vivo.gallery.livePhoto.rotationCheck\":3," +
            "\"com.android.camera.livephoto\":\"{0}\"," +
            "\"version\":2200}}";

        /// <summary>MP4 uuid box 内 JSON（vivo{...} 整体）。</summary>
        private const string VideoJsonTemplate =
            "vivo{{\"com.android.camera.livephoto\":\"{0}\"," +
            "\"version\":2016," +
            "\"com.vivo.gallery.livePhoto.newCoverTime\":0}}";

        /// <summary>uuid box 的用户类型（16 字节，ISOBMFF usertype 字段）。</summary>
        private static readonly byte[] UserTypeBytes =
            Encoding.ASCII.GetBytes("vivoMediaExtInfo");

        /// <summary>cameralbum! 之后的固定签名，来自真实样本。</summary>
        private static readonly byte[] TailSignature =
            [0x1B, 0x2A, 0x39, 0x48, 0x57, 0x66, 0x75, 0x84, 0x93, 0xA2, 0xB3];

        /// <summary>
        /// 给拆分输出的一对 JPG + MP4 写入 vivo 双文件配对标记。
        /// 生成新的 28 位小写十六进制配对 ID，写满图片尾标与视频 uuid box。
        /// </summary>
        /// <param name="imagePath">输出 JPEG 文件路径。</param>
        /// <param name="videoPath">输出 MP4 文件路径。</param>
        /// <param name="token">取消令牌。</param>
        public static async Task WritePairMetadataAsync(
            string imagePath,
            string videoPath,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // 14 字节随机数 → 28 个十六进制字符，与真实样本 ID 长度一致。
            string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(14))
                .ToLowerInvariant();

            await AppendJpegTailAsync(imagePath, id, token);
            await AppendVideoUuidBoxAsync(videoPath, id, token);

            LogService.Split(
                $"vivo dual-file metadata written: ID={id}, image={Path.GetFileName(imagePath)}, video={Path.GetFileName(videoPath)}",
                LogLevel.Debug);
        }

        /// <summary>在 JPEG 文件末尾追加 vivo{JSON} 尾标。</summary>
        private static async Task AppendJpegTailAsync(
            string imagePath,
            string id,
            CancellationToken token)
        {
            byte[] json = Encoding.UTF8.GetBytes(
                string.Format(ImageJsonTemplate, id));
            byte[] tail = BuildTail(json);

            await using var fs = new FileStream(
                imagePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await fs.WriteAsync(tail, token);
        }

        /// <summary>在 MP4 文件末尾追加 vivoMediaExtInfo uuid box。</summary>
        private static async Task AppendVideoUuidBoxAsync(
            string videoPath,
            string id,
            CancellationToken token)
        {
            // 防止输出视频本身残留旧 vivo 双文件 box（如 keep 原样输出路径）。
            if (!Mp4MdtaKeyStripper.TryStripUuidBox(
                    videoPath, "vivoMediaExtInfo", out string? stripError))
            {
                LogService.Split(
                    $"vivo[video] existing vivoMediaExtInfo strip failed (non-fatal): {stripError}",
                    LogLevel.Warning);
            }

            byte[] json = Encoding.UTF8.GetBytes(
                string.Format(VideoJsonTemplate, id));
            byte[] payload = BuildTail(json);

            int boxSize = 8 + UserTypeBytes.Length + payload.Length;
            byte[] box = new byte[boxSize];
            BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)boxSize);
            box[4] = (byte)'u';
            box[5] = (byte)'u';
            box[6] = (byte)'i';
            box[7] = (byte)'d';
            UserTypeBytes.CopyTo(box, 8);
            payload.CopyTo(box, 8 + UserTypeBytes.Length);

            await using var fs = new FileStream(
                videoPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await fs.WriteAsync(box, token);
        }

        /// <summary>
        /// 按真实样本结构构建 JSON 之后的尾巴：
        /// [4 字节长度 = JSON 去掉 "vivo" 后的字节数]
        /// cameralbum!
        /// [4 字节长度 = 19 + ID 字节数]
        /// ID
        /// FF FF FF FF
        /// [11 字节固定签名]
        /// </summary>
        private static byte[] BuildTail(byte[] json)
        {
            int idLen = GetLivePhotoIdLength(json);
            int len1 = json.Length - 4; // 去掉 "vivo" 前缀
            int len2 = 19 + idLen;      // cameralbum!(11) + 长度字段(4) + ID + FFFFFFFF(4)

            using var ms = new MemoryStream(
                json.Length + 4 + 11 + 4 + idLen + 4 + TailSignature.Length);
            ms.Write(json);

            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)len1);
            ms.Write(lenBuf);
            ms.Write("cameralbum!"u8);
            BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)len2);
            ms.Write(lenBuf);

            // ID 在 JSON 内，需从 JSON 中按原样提取，避免手工拼接与编码不一致。
            int idStart = IndexOfLivePhotoId(json);
            if (idStart < 0)
            {
                throw new InvalidDataException(
                    "vivo JSON does not contain com.android.camera.livephoto ID.");
            }
            ms.Write(json, idStart, idLen);

            ReadOnlySpan<byte> terminator = [0xFF, 0xFF, 0xFF, 0xFF];
            ms.Write(terminator);
            ms.Write(TailSignature);
            return ms.ToArray();
        }

        /// <summary>计算 JSON 内配对 ID 的字节长度。</summary>
        private static int GetLivePhotoIdLength(byte[] json)
        {
            int start = IndexOfLivePhotoId(json);
            if (start < 0) return 0;
            int end = start;
            while (end < json.Length && json[end] != (byte)'"') end++;
            return end - start;
        }

        /// <summary>定位 JSON 中配对 ID 值第一个字符的位置。</summary>
        private static int IndexOfLivePhotoId(byte[] json)
        {
            const string key = "\"com.android.camera.livephoto\":\"";
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);

            for (int i = 0; i <= json.Length - keyBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < keyBytes.Length; j++)
                {
                    if (json[i + j] != keyBytes[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i + keyBytes.Length;
                }
            }
            return -1;
        }
    }
}
