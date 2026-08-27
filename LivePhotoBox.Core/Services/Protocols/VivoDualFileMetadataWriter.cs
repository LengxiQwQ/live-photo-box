namespace LivePhotoBox.Services.Protocols
{
    using LivePhotoBox.Models;
    using System;
    using System.Buffers.Binary;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>vivo 旧双文件 MP4 尾部中的封面信息。</summary>
    public sealed class VivoDualFileCoverInfo
    {
        /// <summary>原始拍摄封面在视频中的 0-based 帧序号。</summary>
        public int OriginalFrameIndex { get; init; }

        /// <summary>当前编辑封面时间（毫秒）；字段缺失表示未编辑。</summary>
        public long? CurrentCoverTimeMilliseconds { get; init; }

        /// <summary>JPEG/MP4 共享的配对 ID。</summary>
        public string? LivePhotoId { get; init; }
    }

    /// <summary>换算到视频时间轴后的 vivo 旧双文件封面信息。</summary>
    public sealed class VivoDualFileResolvedTiming
    {
        /// <summary>原始拍摄帧的 0-based 帧序号。</summary>
        public int OriginalFrameIndex { get; init; }

        /// <summary>原始拍摄帧在视频时间轴上的微秒位置。</summary>
        public long OriginalTimestampUs { get; init; }

        /// <summary>当前封面在视频时间轴上的微秒位置。</summary>
        public long CurrentTimestampUs { get; init; }

        /// <summary>是否存在 Gallery 写入的 newCoverTime 编辑字段。</summary>
        public bool HasEditedCover { get; init; }
    }

    /*
     * VivoDualFileMetadataWriter.cs
     *
     * vivo 旧格式双文件（≤X200 系列）配对元数据写入器。
     *
     *   - JPEG 尾部追加 vivo{JSON} 私有尾标
     *   - MP4 尾部追加 vivoMediaExtInfo uuid box
     *   - 两端共享同一个 com.android.camera.livephoto ID
     *   - 字节结构依据真机样本（designs/各个机型测试/双文件/）逆向得出
     */
    public static class VivoDualFileMetadataWriter
    {
        /// <summary>JPEG 私有尾标 JSON（vivo{...} 整体）。</summary>
        private const string ImageJsonTemplate =
            "vivo{{\"com.vivo.gallery.livephoto.source\":4," +
            "\"com.vivo.gallery.livePhoto.rotationOffset\":0," +
            "\"com.vivo.gallery.livePhoto.rotationCheck\":3," +
            "\"com.android.camera.livephoto\":\"{0}\"," +
            "\"version\":2104}}";

        /// <summary>未编辑状态的 MP4 uuid box JSON，仅有唯一基准封面帧。</summary>
        private const string UneditedVideoJsonTemplate =
            "vivo{{\"com.android.camera.imageTime\":{1}," +
            "\"com.android.camera.livephoto\":\"{0}\"," +
            "\"version\":2104}}";

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
            string sourcePath,
            string metadataText,
            string imagePath,
            string videoPath,
            long? keyTimestampUs,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            double currentSeconds = keyTimestampUs.HasValue
                ? keyTimestampUs.Value / 1_000_000.0
                : await AppleLivePhotoMetadata.ResolveCoverSecondsAsync(
                    sourcePath, metadataText, videoPath, token);

            int frameCount = await LivePhotoMergeService.DetectVideoFrameCountAsync(videoPath, token);
            double durationSeconds = await LivePhotoMergeService.DetectVideoDurationAsync(videoPath, token);
            if (durationSeconds <= 0)
                durationSeconds = currentSeconds;

            int coverFrameIndex = TimestampToFrameIndex(
                currentSeconds, frameCount, durationSeconds);

            await WritePairMetadataAsync(
                imagePath, videoPath, coverFrameIndex, token);
        }

        /// <summary>
        /// 写入一对未编辑状态的 vivo 旧双文件。转换时的当前封面直接成为
        /// 唯一基准 imageTime，不写 newCoverTime / bestTime 编辑字段。
        /// </summary>
        public static async Task WritePairMetadataAsync(
            string imagePath,
            string videoPath,
            int coverFrameIndex,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            string id = CreateLivePhotoId();

            await AppendJpegTailAsync(imagePath, id, token);
            await AppendVideoUuidBoxAsync(
                videoPath,
                Encoding.UTF8.GetBytes(string.Format(
                    CultureInfo.InvariantCulture,
                    UneditedVideoJsonTemplate,
                    id,
                    Math.Max(0, coverFrameIndex))),
                token);

            LogService.Split(
                $"vivo dual-file metadata written (unedited): ID={id}, imageTime={coverFrameIndex}, " +
                $"image={Path.GetFileName(imagePath)}, " +
                $"video={Path.GetFileName(videoPath)}",
                LogLevel.Debug);
        }

        /// <summary>
        /// 给已更换静态 JPG 的 vivo 双文件副本重建配对元数据，
        /// 保留 imageTime（原始拍摄帧）并更新 newCoverTime（当前封面毫秒）。
        /// </summary>
        public static async Task RewriteEditedPairMetadataAsync(
            string sourceImagePath,
            string sourceVideoPath,
            string outputImagePath,
            string outputVideoPath,
            long currentCoverTimeMilliseconds,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            JsonObject imageJson = TryReadVivoJson(sourceImagePath) ?? new JsonObject
            {
                ["com.vivo.gallery.livephoto.source"] = 4,
                ["com.vivo.gallery.livePhoto.rotationOffset"] = 0,
                ["com.vivo.gallery.livePhoto.rotationCheck"] = 3
            };
            JsonObject videoJson = TryReadVivoJson(sourceVideoPath) ?? new JsonObject();
            string id = CreateLivePhotoId();

            imageJson["com.android.camera.livephoto"] = id;
            imageJson["version"] = 2200;

            videoJson["com.android.camera.imageTime"] =
                ReadInt32(videoJson, "com.android.camera.imageTime") ?? 0;
            videoJson["com.vivo.gallery.livePhoto.bestTime"] = 0;
            videoJson["com.android.camera.livephoto"] = id;
            videoJson["version"] = 2200;
            videoJson["com.vivo.gallery.livePhoto.newCoverTime"] =
                Math.Max(0, currentCoverTimeMilliseconds);

            byte[] imageJsonBytes = Encoding.UTF8.GetBytes(
                "vivo" + imageJson.ToJsonString(JsonOptions));
            byte[] videoJsonBytes = Encoding.UTF8.GetBytes(
                "vivo" + videoJson.ToJsonString(JsonOptions));

            StripExistingJpegTail(outputImagePath);
            await AppendRawJpegTailAsync(outputImagePath, imageJsonBytes, token);
            await AppendVideoUuidBoxAsync(outputVideoPath, videoJsonBytes, token);

            LogService.Split(
                $"vivo edited-cover metadata rewritten: ID={id}, " +
                $"imageTime={ReadInt32(videoJson, "com.android.camera.imageTime") ?? 0}, " +
                $"newCoverTime={currentCoverTimeMilliseconds}ms",
                LogLevel.Debug);
        }

        /// <summary>读取 vivo 旧双文件 MP4 尾部的原始帧与当前封面字段。</summary>
        public static VivoDualFileCoverInfo? ReadCoverInfo(string videoPath)
        {
            JsonObject? json = TryReadVivoJson(videoPath);
            if (json == null)
                return null;

            int? originalFrameIndex = ReadInt32(json, "com.android.camera.imageTime");
            long? currentCoverMs = ReadInt64(
                json, "com.vivo.gallery.livePhoto.newCoverTime");
            if (!originalFrameIndex.HasValue && !currentCoverMs.HasValue)
                return null;

            return new VivoDualFileCoverInfo
            {
                OriginalFrameIndex = Math.Max(0, originalFrameIndex ?? 0),
                CurrentCoverTimeMilliseconds = currentCoverMs.HasValue
                    ? Math.Max(0, currentCoverMs.Value)
                    : null,
                LivePhotoId = json["com.android.camera.livephoto"]?.GetValue<string>()
            };
        }

        /// <summary>
        /// 将用户选中的帧换算为 vivo newCoverTime 毫秒值。
        /// 选中末帧时按真机行为写入视频时长边界。
        /// </summary>
        public static async Task<long> ResolveCurrentCoverTimeMillisecondsAsync(
            string videoPath,
            long timestampUs,
            int? frameIndex,
            CancellationToken token)
        {
            int frameCount = await LivePhotoMergeService.DetectVideoFrameCountAsync(videoPath, token);
            double durationSeconds = await LivePhotoMergeService.DetectVideoDurationAsync(videoPath, token);
            if (frameIndex.HasValue && frameCount > 0 &&
                frameIndex.Value >= frameCount - 1 && durationSeconds > 0)
            {
                return Math.Max(0, (long)Math.Floor(durationSeconds * 1000.0));
            }

            return Math.Max(0, timestampUs / 1000L);
        }

        /// <summary>将 vivo 帧号/毫秒字段换算成 Core 统一的微秒时间轴。</summary>
        public static async Task<VivoDualFileResolvedTiming?> ResolveCoverTimingAsync(
            string videoPath,
            CancellationToken token)
        {
            VivoDualFileCoverInfo? info = ReadCoverInfo(videoPath);
            if (info == null)
                return null;

            int frameCount = await LivePhotoMergeService.DetectVideoFrameCountAsync(videoPath, token);
            double durationSeconds = await LivePhotoMergeService.DetectVideoDurationAsync(videoPath, token);
            double fps = frameCount > 0 && durationSeconds > 0
                ? frameCount / durationSeconds
                : await LivePhotoMergeService.DetectVideoFpsAsync(videoPath, token);

            double originalSeconds = fps > 0
                ? info.OriginalFrameIndex / fps
                : 0;
            long originalTimestampUs = (long)Math.Round(originalSeconds * 1_000_000.0);
            long currentTimestampUs = info.CurrentCoverTimeMilliseconds.HasValue
                ? info.CurrentCoverTimeMilliseconds.Value * 1000
                : originalTimestampUs;

            return new VivoDualFileResolvedTiming
            {
                OriginalFrameIndex = info.OriginalFrameIndex,
                OriginalTimestampUs = Math.Max(0, originalTimestampUs),
                CurrentTimestampUs = Math.Max(0, currentTimestampUs),
                HasEditedCover = info.CurrentCoverTimeMilliseconds.HasValue
            };
        }

        /// <summary>在 JPEG 文件末尾追加 vivo{JSON} 尾标。</summary>
        private static async Task AppendJpegTailAsync(
            string imagePath,
            string id,
            CancellationToken token)
        {
            byte[] json = Encoding.UTF8.GetBytes(
                string.Format(ImageJsonTemplate, id));
            await AppendRawJpegTailAsync(imagePath, json, token);
        }

        private static async Task AppendRawJpegTailAsync(
            string imagePath,
            byte[] json,
            CancellationToken token)
        {
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
            byte[] json,
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

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        private static string CreateLivePhotoId()
            => Convert.ToHexString(RandomNumberGenerator.GetBytes(14)).ToLowerInvariant();

        private static int TimestampToFrameIndex(
            double seconds, int frameCount, double durationSeconds)
        {
            if (frameCount <= 0 || durationSeconds <= 0)
                return 0;
            int index = (int)Math.Round(seconds * frameCount / durationSeconds);
            return Math.Clamp(index, 0, frameCount - 1);
        }

        private static JsonObject? TryReadVivoJson(string filePath)
        {
            try
            {
                const int TailWindowBytes = 2 * 1024 * 1024;
                using var fs = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    81920, FileOptions.SequentialScan);
                int length = (int)Math.Min(fs.Length, TailWindowBytes);
                if (length < 8)
                    return null;

                byte[] data = new byte[length];
                fs.Seek(-length, SeekOrigin.End);
                fs.ReadExactly(data, 0, length);

                ReadOnlySpan<byte> marker = "vivo{"u8;
                int markerIndex = LastIndexOf(data, marker);
                if (markerIndex < 0)
                    return null;

                int jsonStart = markerIndex + 4;
                int jsonEnd = FindJsonObjectEnd(data, jsonStart);
                if (jsonEnd < jsonStart)
                    return null;

                string jsonText = Encoding.UTF8.GetString(
                    data, jsonStart, jsonEnd - jsonStart + 1);
                return JsonNode.Parse(jsonText) as JsonObject;
            }
            catch
            {
                return null;
            }
        }

        private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern)
        {
            for (int i = data.Length - pattern.Length; i >= 0; i--)
            {
                if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                    return i;
            }
            return -1;
        }

        private static int FindJsonObjectEnd(byte[] data, int start)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < data.Length; i++)
            {
                byte b = data[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        escaped = true;
                    }
                    else if (b == (byte)'"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (b == (byte)'"')
                {
                    inString = true;
                }
                else if (b == (byte)'{')
                {
                    depth++;
                }
                else if (b == (byte)'}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                    if (depth < 0)
                        return -1;
                }
            }
            return -1;
        }

        private static void StripExistingJpegTail(string imagePath)
        {
            const int TailWindowBytes = 2 * 1024 * 1024;
            using var fs = new FileStream(
                imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            int length = (int)Math.Min(fs.Length, TailWindowBytes);
            if (length < 8)
                return;

            byte[] data = new byte[length];
            long windowStart = fs.Length - length;
            fs.Seek(windowStart, SeekOrigin.Begin);
            fs.ReadExactly(data, 0, length);
            int markerIndex = LastIndexOf(data, "vivo{"u8);
            if (markerIndex < 0)
                return;

            fs.SetLength(windowStart + markerIndex);
        }

        private static int? ReadInt32(JsonObject json, string key)
        {
            long? value = ReadInt64(json, key);
            return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
        }

        private static long? ReadInt64(JsonObject json, string key)
        {
            JsonNode? node = json[key];
            if (node == null)
                return null;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<long>(out long number))
                    return number;
                if (value.TryGetValue<string>(out string? text) &&
                    long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    return number;
            }
            return null;
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
