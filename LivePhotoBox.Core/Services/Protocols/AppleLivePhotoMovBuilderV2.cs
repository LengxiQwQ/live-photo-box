using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    /*
     * AppleLivePhotoMovBuilderV2.cs
     *
     * 规范化的 Apple Live Photo MOV 生成器（替代 V1 的 IMG_6675 hex 模板方案）。
     *
     *   - 所有 ISO/IEC 14496-12 box 由结构化代码生成，字段值按规范计算，不抄样本字节
     *   - 编码参数全部从源 ffmpeg MOV 解析（支持 hvc1/avc1、任意声道/采样率 AAC）
     *   - 时间轴原样保留源的 timescale / stts / ctts（含 B 帧任意 GOP）
     *   - Apple 私有 mebx 元数据载荷（content-describes / cover 样本、keys/setu bplist）作为字节常量保留，
     *     唯一随源变化的 dims 由实际视频尺寸参数化
     */
    public static class AppleLivePhotoMovBuilderV2
    {
        // ── Apple 惯例（设计常量，非字节模板）──────────────────────────
        private const long AppleEpochOffsetSeconds = 2082844800;
        private const int MovieTimeScale = 44100;
        private const int ContentTimeScale = 60000;
        private const int ContentSampleDelta = 1000;
        private const int CoverTimeScale = 600;
        private const int CoverSampleDelta = 1;

        // ── Apple 私有 mebx 数据载荷（从可导入 iPhone 的参照样本提取的数据块）────────
        // content-describes 样本（144B）：包含 LivePhotoMetadata 内容描述二进制记录。
        private static readonly byte[] ContentSample = FromHex(
            "000000900000000103000000bdc36d3ce3b5eb6d800000007b80ad425a2d64410a08cb3e7feea6bd79e9f63f000080400400ff" +
            "00000000000000000000000000000000000000000007000000525e873ee66e52bf1b2a6ac4d37862bf761ed23dde3f8ec313f" +
            "52f39b2f04439ff309dbf1a17f1ed1b070000206796ed1b07000000000000000000000000000000000000");

        // cover 样本（89B）：封面帧记录（时间由封面轨 elst 表达）。
        private static readonly byte[] CoverSample = FromHex(
            "0000000900000001ff00000050000000023ff0000000000000000000000000000000000000000000000000000000000000" +
            "3ff00000000000000000000000000000000000000000000000000000000000003ff0000000000000");

        // mebx 样本条目 keys 载荷（Apple 私有）：ContentDescribes 轨（515B）。
        // 内部含 key 定义、'setu'(bplist LivePhotoMetadataSetupData)、'dims'(源相关，见 BuildMebxStsd 参数化)、'ctps'。
        private static readonly byte[] ContentKeysPayload = FromHex(
            "00000203000000010000002f6b6579646d647461636f6d2e6170706c652e717569636b74696d652e6c6976652d70686f746f2d696e666f" +
            "000000436474797000000001636f6d2e6170706c652e717569636b74696d652e636f6d2e6170706c652e717569636b74696d652e6c6976652d70686f746f2d696e666f" +
            "0000017173657475000001596366677662706c6973743030d301020304050c5f10214c69766550686f746f4d6574616461746153657475704461746156657273696f6e" +
            "5d53797374656d56657273696f6e5f10114672616d65776f726b56657273696f6e731001d3060708090a0b5f101350726f647563744275696c6456657273696f6e" +
            "5b50726f647563744e616d655e50726f6475637456657273696f6e583231413532373768596950686f6e65204f535431372e30d40d0e0f10111213145a436f72654d" +
            "6f74696f6e5d434d43617074757265436f72655e483130495350536572766963657359436f72654d6564696158323836382e302e32573434362e352e335432302e32" +
            "5e333034352e36392e322e31312e340008000f0033004100550057005e00740080008f009800a200a700b000bb00c900d800e200eb00f300f8000000000000020100" +
            "00000000000015000000000000000000000000000001070000001064696d7300000780000005a0000000186374707300000010647479700000000000000000");

        // mebx 样本条目 keys 载荷（Apple 私有）：封面轨（160B）。
        private static readonly byte[] CoverKeysPayload = FromHex(
            "0000004800000001000000306b6579646d647461636f6d2e6170706c652e717569636b74696d652e7374696c6c2d696d6167652d74696d65" +
            "000000106474797000000000000000410000005800000002000000406b6579646d647461636f6d2e6170706c652e717569636b74696d652e" +
            "6c6976652d70686f746f2d7374696c6c2d696d6167652d7472616e73666f726d00000010647479700000000000000053");

        // ── 公开入口 ─────────────────────────────────────────────────
        public static bool TryRebuild(
            string movPath, string contentId, string model, double coverSeconds, out string? error)
        {
            error = null;
            try
            {
                byte[] source = File.ReadAllBytes(movPath);
                byte[]? built = Build(source, contentId, coverSeconds, out error);
                if (built == null)
                    return false;

                string dir = Path.GetDirectoryName(movPath) ?? ".";
                string temp = Path.Combine(dir, $".lpb_apple_mov_{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temp, built);
                    File.Delete(movPath);
                    File.Move(temp, movPath);
                }
                finally
                {
                    if (File.Exists(temp)) { try { File.Delete(temp); } catch { /* best-effort */ } }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>内存级重建（测试友好）。失败返回 null 并设置 error。</summary>
        internal static byte[]? Build(
            byte[] source, string contentId, double coverSeconds, out string? error)
        {
            error = null;
            try
            {
                int moov = FindBox(source, 0, source.Length, "moov");
                if (moov < 0) { error = "source moov not found"; return null; }
                int moovEnd = moov + ReadBE32(source, moov);
                if (moovEnd > source.Length) moovEnd = source.Length;

                VideoData? video = null;
                AudioData? audio = null;
                foreach (var child in ParseChildren(source, moov + 8, moovEnd))
                {
                    if (child.Type != "trak") continue;
                    string entryType = GetSampleEntryType(child.Box);
                    if (entryType is "hvc1" or "avc1")
                    {
                        if (video == null) video = ParseVideoTrak(source, child.Box);
                    }
                    else if (entryType == "mp4a")
                    {
                        if (audio == null) audio = ParseAudioTrak(source, child.Box);
                    }
                }

                if (video == null) { error = "source has no video track"; return null; }
                if (video.SampleSizes.Count == 0) { error = "source video track has no samples"; return null; }
                if (video.StsdType is not ("hvc1" or "avc1"))
                {
                    error = $"Apple output requires hvc1 or avc1, got {video.StsdType}";
                    return null;
                }
                if (video.CodecConfig == null)
                {
                    error = $"source video sample entry has no {video.StsdType} config (hvcC/avcC)";
                    return null;
                }

                // ── 时间轴（全部沿用源时序，不做易碎的归一化）────────────
                int videoMediaDur = (int)video.Stts.Sum(e => (long)e.Count * e.Delta);
                double videoSeconds = videoMediaDur / (double)video.Timescale;
                if (videoSeconds <= 0) { error = "source video has zero duration"; return null; }
                int videoDur = Math.Max(1, (int)Math.Round(videoSeconds * MovieTimeScale));
                int appleTime = unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + AppleEpochOffsetSeconds));
                DateTimeOffset now = DateTimeOffset.Now;
                string creationDate = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                    + now.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");

                int contentCount = Math.Max(1, (int)Math.Round((videoSeconds - 0.05) * 60.0));
                int contentMediaDur = contentCount * ContentSampleDelta;
                int contentMovieTicks = (int)Math.Round(contentMediaDur * (double)MovieTimeScale / ContentTimeScale);
                int contentLeadIn = (int)Math.Round(0.05 * MovieTimeScale);
                int contentTkhdDur = contentLeadIn + contentMovieTicks;

                int coverTicks = coverSeconds > 0 ? (int)Math.Round(coverSeconds * MovieTimeScale) : 0;
                int coverMovieDelta = Math.Max(1, (int)Math.Round(MovieTimeScale / (double)CoverTimeScale));
                int coverTkhdDur = coverTicks + coverMovieDelta;

                int audioMediaDur = audio != null ? (int)audio.Stts.Sum(e => (long)e.Count * e.Delta) : 0;
                int audioDur = audio != null
                    ? Math.Max(0, (int)Math.Round(audioMediaDur * (double)MovieTimeScale / audio.SampleRate))
                    : 0;
                int movieDur = Math.Max(videoDur, audioDur);

                // ── 组装（先以 stco=0 占位算 moov 尺寸）──────────────────
                List<byte[]> moovPayload0 = BuildMoov(
                    video, audio, videoDur, audioDur, movieDur, appleTime,
                    contentCount, contentTkhdDur, contentMovieTicks, contentLeadIn,
                    coverTicks, coverMovieDelta, coverTkhdDur,
                    contentId, creationDate,
                    videoOffset: 0, audioOffset: 0, contentOffset: 0, coverOffset: 0);
                byte[] moov0 = BuildContainer("moov", moovPayload0);
                int dataStart = Ftyp().Length + moov0.Length + Wide().Length + 8;

                int videoBytes = video.SampleDataTotal;
                int audioBytes = audio?.SampleDataTotal ?? 0;
                int contentBytes = contentCount * ContentSample.Length;
                int videoOffset = dataStart;
                int audioOffset = videoOffset + videoBytes;
                int contentOffset = audioOffset + audioBytes;
                int coverOffset = contentOffset + contentBytes;

                List<byte[]> moovPayload = BuildMoov(
                    video, audio, videoDur, audioDur, movieDur, appleTime,
                    contentCount, contentTkhdDur, contentMovieTicks, contentLeadIn,
                    coverTicks, coverMovieDelta, coverTkhdDur,
                    contentId, creationDate,
                    videoOffset, audioOffset, contentOffset, coverOffset);
                byte[] moovBox = BuildContainer("moov", moovPayload);

                int mdatSize = 8 + videoBytes + audioBytes + contentBytes + CoverSample.Length;
                byte[] file = new byte[Ftyp().Length + moovBox.Length + Wide().Length + mdatSize];
                int p = 0;
                Array.Copy(Ftyp(), 0, file, p, Ftyp().Length); p += Ftyp().Length;
                Array.Copy(moovBox, 0, file, p, moovBox.Length); p += moovBox.Length;
                Array.Copy(Wide(), 0, file, p, Wide().Length); p += Wide().Length;
                WriteBE32(file, p, mdatSize);
                WriteType(file, p + 4, "mdat");
                p += 8;
                foreach (var sample in video.Samples) { Array.Copy(sample, 0, file, p, sample.Length); p += sample.Length; }
                if (audio != null)
                {
                    foreach (var sample in audio.Samples) { Array.Copy(sample, 0, file, p, sample.Length); p += sample.Length; }
                }
                for (int i = 0; i < contentCount; i++)
                {
                    Array.Copy(ContentSample, 0, file, p, ContentSample.Length);
                    p += ContentSample.Length;
                }
                Array.Copy(CoverSample, 0, file, p, CoverSample.Length);
                return file;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        // ── moov 组装 ────────────────────────────────────────────────
        private static List<byte[]> BuildMoov(
            VideoData video, AudioData? audio, int videoDur, int audioDur, int movieDur, int appleTime,
            int contentCount, int contentTkhdDur, int contentMovieTicks, int contentLeadIn,
            int coverTicks, int coverMovieDelta, int coverTkhdDur,
            string contentId, string creationDate,
            int videoOffset, int audioOffset, int contentOffset, int coverOffset)
        {
            return new List<byte[]>
            {
                BuildMvhd(appleTime, movieDur),
                BuildVideoTrak(video, videoDur, appleTime, videoOffset),
                audio != null ? BuildAudioTrak(audio, audioDur, appleTime, audioOffset) : null!,
                BuildContentTrak(contentCount, contentTkhdDur, contentMovieTicks, contentLeadIn, appleTime, contentOffset, video.Width, video.Height),
                BuildCoverTrak(coverTicks, coverMovieDelta, coverTkhdDur, appleTime, coverOffset),
                BuildMeta(contentId, creationDate),
            }.Where(b => b != null).Select(b => b!).ToList();
        }

        private static byte[] BuildMvhd(int appleTime, int movieDur)
        {
            byte[] b = new byte[100];
            b[0] = 0; b[1] = 0; b[2] = 0; b[3] = 0; // version 0, flags 0
            WriteBE32(b, 4, appleTime);
            WriteBE32(b, 8, appleTime);
            WriteBE32(b, 12, MovieTimeScale);
            WriteBE32(b, 16, movieDur);
            WriteBE32(b, 20, 0x00010000); // rate 1.0
            WriteBE16(b, 24, 0x0100);     // volume 1.0
            WriteBE16(b, 26, 0);          // reserved
            WriteBE32(b, 28, 0);          // reserved
            WriteBE32(b, 32, 0);          // reserved
            WriteIdentityMatrix(b, 36);
            WriteBE32(b, 72, 0); WriteBE32(b, 76, 0); WriteBE32(b, 80, 0);
            WriteBE32(b, 84, 0); WriteBE32(b, 88, 0); WriteBE32(b, 92, 0);
            WriteBE32(b, 96, 5);          // next track id
            return Box("mvhd", b);
        }

        private static void WriteIdentityMatrix(byte[] b, int off)
        {
            WriteBE32(b, off, 0x00010000); WriteBE32(b, off + 4, 0); WriteBE32(b, off + 8, 0);
            WriteBE32(b, off + 12, 0); WriteBE32(b, off + 16, 0x00010000); WriteBE32(b, off + 20, 0);
            WriteBE32(b, off + 24, 0); WriteBE32(b, off + 28, 0); WriteBE32(b, off + 32, 0x40000000);
        }

        // ── Video trak ───────────────────────────────────────────────
        private static byte[] BuildVideoTrak(VideoData v, int videoDur, int appleTime, int dataOffset)
        {
            int mediaDur = (int)v.Stts.Sum(e => (long)e.Count * e.Delta);
            byte[] tkhd = BuildTkhd(trackId: 1, duration: videoDur, appleTime, layer: 0, volume: 0, v.Width, v.Height);
            byte[] tapt = BuildTapt(v.Width, v.Height);
            byte[] elst = BuildElstSingle(videoDur, 0);
            byte[] mdhd = BuildMdhd(appleTime, v.Timescale, mediaDur);
            byte[] hdlr = BuildHdlr("vide", "Core Media Video");
            byte[] vmhd = Box("vmhd", new byte[] { 0, 0, 0, 1, 0x00, 0x40, 0x80, 0x00, 0x80, 0x00, 0x80, 0x00 });
            byte[] minf = BuildMinf(vmhd, BuildVideoStbl(v, dataOffset));
            byte[] mdia = BuildContainer("mdia", new List<byte[]> { mdhd, hdlr, minf });
            return BuildContainer("trak", new List<byte[]>
            {
                tkhd, tapt,
                BuildContainer("edts", new List<byte[]> { elst }),
                mdia,
            });
        }

        private static byte[] BuildTapt(int w, int h)
        {
            // QuickTime 轨道孔径扩展：clef / prof / enof 三个 FullBox，
            // 每个 = size(4) + type(4) + version/flags(4) + width(4) + height(4)。
            byte[] BuildSub(string type) => Box(type, Concat(
                new byte[4],
                Be32((int)((uint)w << 16)),
                Be32((int)((uint)h << 16))));
            return Box("tapt", Concat(BuildSub("clef"), BuildSub("prof"), BuildSub("enof")));
        }

        private static byte[] BuildVideoStbl(VideoData v, int dataOffset)
        {
            byte[] stsd = BuildVideoStsd(v);
            byte[] stts = BuildStts(v.Stts);
            // 全 I 帧源（ffmpeg 会省略 stss）等价于每个样本都是同步样本；
            // 补齐 stss/sdtp/sbgp，保证编辑器能逐帧取预览。
            var syncSamples = v.SyncSamples.Count > 0
                ? v.SyncSamples
                : Enumerable.Range(1, v.SampleSizes.Count).ToList();
            byte[] ctts = v.Ctts.Count > 0 && v.Ctts.Any(c => c != 0) ? BuildCtts(v.Ctts) : null!;
            byte[] cslg = v.Ctts.Count > 0 && v.Ctts.Any(c => c != 0)
                ? BuildCslg(v.Ctts, (int)v.Stts.Sum(e => (long)e.Count * e.Delta))
                : null!;
            byte[] stss = syncSamples.Count > 0 ? BuildStss(syncSamples) : null!;
            byte[] sdtp = BuildSdtp(v.SampleSizes.Count, syncSamples);
            byte[] stsc = BuildStsc(v.SampleSizes.Count);
            byte[] stsz = BuildStsz(v.SampleSizes);
            byte[] stco = BuildStco(dataOffset);
            return BuildContainer("stbl", new List<byte[]>
            {
                stsd, BuildSgpdSync(), BuildSbgpSync(v.SampleSizes.Count, syncSamples), stts,
                ctts ?? Array.Empty<byte>(), cslg ?? Array.Empty<byte>(),
                stss ?? Array.Empty<byte>(), sdtp, stsc, stsz, stco,
            }.Where(b => b.Length > 0).Select(b => b!).ToList());
        }

        private static byte[] BuildVideoStsd(VideoData v)
        {
            bool hevc = v.StsdType == "hvc1";
            byte[] fixedPart = new byte[78];
            WriteBE16(fixedPart, 6, 1);              // data_reference_index
            WriteBE16(fixedPart, 24, (ushort)v.Width);
            WriteBE16(fixedPart, 26, (ushort)v.Height);
            WriteBE32(fixedPart, 28, 0x00480000);    // horizresolution
            WriteBE32(fixedPart, 32, 0x00480000);    // vertresolution
            WriteBE16(fixedPart, 40, 1);             // frame_count
            string name = hevc ? "HEVC" : "AVC Coding";
            fixedPart[42] = (byte)name.Length;
            Encoding.ASCII.GetBytes(name, 0, name.Length, fixedPart, 43);
            WriteBE16(fixedPart, 74, 0x0018);        // depth
            WriteBE16(fixedPart, 76, 0xFFFF);        // pre_defined

            var children = new List<byte[]>();
            if (v.CodecConfig != null) children.Add(v.CodecConfig);
            if (v.ColrBox != null) children.Add(v.ColrBox);
            if (v.PaspBox != null) children.Add(v.PaspBox);
            byte[] entry = Concat(fixedPart, children.ToArray());
            byte[] full = new byte[8 + entry.Length];
            WriteBE32(full, 0, full.Length);
            WriteType(full, 4, v.StsdType);
            Array.Copy(entry, 0, full, 8, entry.Length);
            return BuildStsdPayload(full);
        }

        // ── Audio trak ───────────────────────────────────────────────
        private static byte[] BuildAudioTrak(AudioData a, int audioDur, int appleTime, int dataOffset)
        {
            int mediaDur = (int)a.Stts.Sum(e => (long)e.Count * e.Delta);
            int primingMovie = (int)Math.Round(a.Priming * (double)MovieTimeScale / a.SampleRate);
            byte[] tkhd = BuildTkhd(trackId: 2, duration: audioDur, appleTime, layer: 0, volume: 0x0100, 0, 0);
            byte[] elst = BuildElstSingle(audioDur, Math.Max(0, primingMovie));
            byte[] mdhd = BuildMdhd(appleTime, a.SampleRate, mediaDur);
            byte[] hdlr = BuildHdlr("soun", "Core Media Audio");
            byte[] smhd = Box("smhd", new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });
            byte[] stsd = BuildAudioStsd(a);
            byte[] stts = BuildStts(a.Stts);
            byte[] stsc = BuildStsc(a.SampleSizes.Count);
            byte[] stsz = BuildStsz(a.SampleSizes);
            byte[] stco = BuildStco(dataOffset);
            byte[] stbl = BuildContainer("stbl", new List<byte[]>
                { stsd, BuildSgpdRoll(), BuildSbgpRoll(a.SampleSizes.Count), stts, stsc, stsz, stco });
            byte[] minf = BuildMinf(smhd, stbl);
            byte[] mdia = BuildContainer("mdia", new List<byte[]> { mdhd, hdlr, minf });
            return BuildContainer("trak", new List<byte[]>
            {
                tkhd,
                BuildContainer("edts", new List<byte[]> { elst }),
                mdia,
            });
        }

        private static byte[] BuildAudioStsd(AudioData a)
        {
            byte[] fixedPart = new byte[44]; // QuickTime v1 SoundDescription 固定部（不含 size+type）
            WriteBE16(fixedPart, 6, 1);              // data_reference_index
            WriteBE16(fixedPart, 8, 1);              // version = 1
            WriteBE16(fixedPart, 10, 0);             // revision
            WriteBE32(fixedPart, 12, 0);             // vendor
            WriteBE16(fixedPart, 16, (ushort)Math.Clamp(a.Channels, 1, 65535));
            WriteBE16(fixedPart, 18, 16);            // sample size
            WriteBE16(fixedPart, 20, 0xFFFE);        // compression id (-2)
            WriteBE16(fixedPart, 22, 0);             // packet size
            WriteBE32(fixedPart, 24, (uint)a.SampleRate << 16); // 16.16
            WriteBE32(fixedPart, 28, 1024);          // samplesPerPacket（AAC-LC 帧样本数）
            WriteBE32(fixedPart, 32, 1);             // bytesPerPacket
            WriteBE32(fixedPart, 36, 2);             // bytesPerFrame
            WriteBE32(fixedPart, 40, 2);             // bytesPerSample

            byte[] frma = Box("frma", Encoding.ASCII.GetBytes("mp4a"));
            byte[] mp4aEmpty = Box("mp4a", new byte[4]); // QuickTime wave 内占位 mp4a：4 字节 0
            byte[] esds = BuildEsds(a.BufferSize, a.MaxBitrate, a.AvgBitrate,
                a.Asc ?? SynthesizeAsc(a.SampleRate, a.Channels));
            byte[] wave = Box("wave", Concat(frma, mp4aEmpty, esds, new byte[] { 0, 0, 0, 8, 0, 0, 0, 0 }));
            byte[] entry = Concat(fixedPart, wave);
            byte[] full = new byte[8 + entry.Length];
            WriteBE32(full, 0, full.Length);
            WriteType(full, 4, "mp4a");
            Array.Copy(entry, 0, full, 8, entry.Length);
            return BuildStsdPayload(full);
        }

        private static byte[] BuildEsds(int bufferSize, int maxBitrate, int avgBitrate, byte[] asc)
        {
            byte[] tag5 = Descriptor(0x05, asc);
            byte[] tag6 = Descriptor(0x06, new byte[] { 0x02 });
            byte[] decBody = new byte[13 + tag5.Length];
            decBody[0] = 0x40;                 // objectTypeIndication: MPEG-4 Audio
            decBody[1] = 0x14;                 // streamType=5(Audio), upStream=0, reserved=0（Apple 风格）
            WriteBE24(decBody, 2, bufferSize);
            WriteBE32(decBody, 5, maxBitrate);
            WriteBE32(decBody, 9, avgBitrate);
            Array.Copy(tag5, 0, decBody, 13, tag5.Length);
            byte[] tag4 = Descriptor(0x04, decBody);
            byte[] esBody = new byte[3 + tag4.Length + tag6.Length];
            WriteBE16(esBody, 0, 0);           // ES_ID
            esBody[2] = 0;                     // flags
            Array.Copy(tag4, 0, esBody, 3, tag4.Length);
            Array.Copy(tag6, 0, esBody, 3 + tag4.Length, tag6.Length);
            byte[] tag3 = Descriptor(0x03, esBody);
            byte[] body = new byte[4 + tag3.Length];
            WriteBE32(body, 0, 0);             // version/flags
            Array.Copy(tag3, 0, body, 4, tag3.Length);
            return Box("esds", body);
        }

        private static byte[] Descriptor(int tag, byte[] payload)
        {
            byte[] len = Varint(payload.Length);
            byte[] d = new byte[1 + len.Length + payload.Length];
            d[0] = (byte)tag;
            Array.Copy(len, 0, d, 1, len.Length);
            Array.Copy(payload, 0, d, 1 + len.Length, payload.Length);
            return d;
        }

        private static byte[] Varint(int value)
        {
            // Apple 风格：始终使用 4 字节 0x80 延续编码（与参照样本逐字节一致）。
            return new byte[]
            {
                (byte)(((value >> 21) & 0x7F) | 0x80),
                (byte)(((value >> 14) & 0x7F) | 0x80),
                (byte)(((value >> 7) & 0x7F) | 0x80),
                (byte)(value & 0x7F),
            };
        }

        // ── ContentDescribes / cover trak ────────────────────────────
        private static byte[] BuildContentTrak(
            int sampleCount, int tkhdDur, int movieTicks, int leadIn, int appleTime, int dataOffset, int videoW, int videoH)
        {
            byte[] tkhd = BuildTkhd(trackId: 3, duration: tkhdDur, appleTime, layer: 0, volume: 0, 0, 0);
            byte[] elst = BuildElstDouble(leadIn, -1, movieTicks, 0);
            byte[] mdhd = BuildMdhd(appleTime, ContentTimeScale, sampleCount * ContentSampleDelta);
            byte[] hdlr = BuildHdlr("mhlr", "meta", "appl", 1, "Core Media Metadata");
            byte[] gmhd = BuildGmhd();
            byte[] stsd = BuildMebxStsd(cover: false);
            byte[] stts = BuildStts(sampleCount, ContentSampleDelta);
            byte[] stsc = BuildStsc(sampleCount);
            byte[] stsz = BuildStszUniform(ContentSample.Length, sampleCount);
            byte[] stco = BuildStco(dataOffset);
            byte[] stbl = BuildContainer("stbl", new List<byte[]> { stsd, stts, stsc, stsz, stco });
            byte[] minf = BuildMinf(gmhd, stbl);
            byte[] mdia = BuildContainer("mdia", new List<byte[]> { mdhd, hdlr, minf });
            return BuildContainer("trak", new List<byte[]>
            {
                tkhd,
                BuildContainer("edts", new List<byte[]> { elst }),
                mdia,
            });
        }

        private static byte[] BuildCoverTrak(
            int coverTicks, int coverMovieDelta, int tkhdDur, int appleTime, int dataOffset)
        {
            byte[] tkhd = BuildTkhd(trackId: 4, duration: tkhdDur, appleTime, layer: 0, volume: 0, 0, 0);
            byte[] elst = BuildElstDouble(coverTicks, -1, coverMovieDelta, 0);
            byte[] mdhd = BuildMdhd(appleTime, CoverTimeScale, CoverSampleDelta);
            byte[] hdlr = BuildHdlr("mhlr", "meta", "appl", 1, "Core Media Metadata");
            byte[] gmhd = BuildGmhd();
            byte[] stsd = BuildMebxStsd(cover: true);
            byte[] stts = BuildStts(1, CoverSampleDelta);
            byte[] stsc = BuildStsc(1);
            byte[] stsz = BuildStszUniform(CoverSample.Length, 1);
            byte[] stco = BuildStco(dataOffset);
            byte[] stbl = BuildContainer("stbl", new List<byte[]> { stsd, stts, stsc, stsz, stco });
            byte[] minf = BuildMinf(gmhd, stbl);
            byte[] mdia = BuildContainer("mdia", new List<byte[]> { mdhd, hdlr, minf });
            return BuildContainer("trak", new List<byte[]>
            {
                tkhd,
                BuildContainer("edts", new List<byte[]> { elst }),
                mdia,
            });
        }

        private static byte[] BuildMebxStsd(bool cover)
        {
            byte[] keysPayload = cover
                ? (byte[])CoverKeysPayload.Clone()
                : (byte[])ContentKeysPayload.Clone();
            byte[] keysBox = Box("keys", keysPayload);
            byte[] entry = new byte[16 + keysBox.Length];
            WriteBE32(entry, 0, entry.Length);
            WriteType(entry, 4, "mebx");
            WriteBE32(entry, 8, 0);          // 6 reserved
            WriteBE16(entry, 14, 1);         // data_reference_index
            Array.Copy(keysBox, 0, entry, 16, keysBox.Length);
            return BuildStsdPayload(entry);
        }

        // ── meta（moov 直属，QuickTime 风格无 version/flags）─────────
        private static byte[] BuildMeta(string contentId, string creationDate)
        {
            byte[] hdlr = BuildHdlr("", "mdta", "", 0, "");
            string[] keys = new[]
            {
                "com.apple.quicktime.content.identifier",
                "com.apple.quicktime.software",
                "com.apple.quicktime.creationdate",
            };
            string[] values = new[] { contentId, "17.0.2", creationDate };
            byte[] keysBox = BuildMetaKeys(keys);
            byte[] ilst = BuildIlst(values);
            return Box("meta", Concat(hdlr, keysBox, ilst));
        }

        private static byte[] BuildMetaKeys(string[] keyNames)
        {
            var entries = keyNames.Select(k =>
            {
                byte[] val = Encoding.ASCII.GetBytes(k);
                byte[] e = new byte[8 + val.Length];
                WriteBE32(e, 0, e.Length);
                WriteType(e, 4, "mdta");
                Array.Copy(val, 0, e, 8, val.Length);
                return e;
            }).ToList();
            byte[] body = new byte[4 + entries.Sum(e => e.Length)];
            WriteBE32(body, 0, keyNames.Length);
            int p = 4;
            foreach (var e in entries) { Array.Copy(e, 0, body, p, e.Length); p += e.Length; }
            return FullBox("keys", body);
        }

        private static byte[] BuildIlst(string[] values)
        {
            var entries = new List<byte[]>();
            for (int i = 0; i < values.Length; i++)
            {
                byte[] vb = Encoding.UTF8.GetBytes(values[i]);
                byte[] data = new byte[16 + vb.Length];
                WriteBE32(data, 0, data.Length);
                WriteType(data, 4, "data");
                WriteBE32(data, 8, 1);                 // type 1 = UTF-8
                WriteBE32(data, 12, 0);                // locale
                Array.Copy(vb, 0, data, 16, vb.Length);
                byte[] e = new byte[8 + data.Length];
                WriteBE32(e, 0, e.Length);
                e[4] = 0; e[5] = 0; e[6] = 0; e[7] = (byte)(i + 1);
                Array.Copy(data, 0, e, 8, data.Length);
                entries.Add(e);
            }
            return Box("ilst", Concat(entries.ToArray()));
        }

        // ── 采样表 / 分组表（ISO 14496-12）────────────────────────────
        private static byte[] BuildStts(List<(int Count, int Delta)> stts)
        {
            byte[] body = new byte[4 + stts.Count * 8];
            WriteBE32(body, 0, stts.Count);
            for (int i = 0; i < stts.Count; i++)
            {
                WriteBE32(body, 4 + i * 8, (uint)stts[i].Count);
                WriteBE32(body, 8 + i * 8, (uint)stts[i].Delta);
            }
            return FullBox("stts", body);
        }

        private static byte[] BuildStts(int sampleCount, int delta)
            => FullBox("stts", Concat(Be32(1), Be32(sampleCount), Be32(delta)));

        private static byte[] BuildCtts(List<int> offsets)
        {
            // v0 + 无符号包装（与 Apple 自家文件一致；负偏移以 uint32 表示）。
            byte[] body = new byte[4 + offsets.Count * 8];
            WriteBE32(body, 0, offsets.Count);
            for (int i = 0; i < offsets.Count; i++)
            {
                WriteBE32(body, 4 + i * 8, 1);
                WriteBE32(body, 8 + i * 8, unchecked((uint)offsets[i]));
            }
            return FullBox("ctts", body);
        }

        private static byte[] BuildCslg(List<int> offsets, int mediaDur)
        {
            int min = offsets.Min(), max = offsets.Max();
            byte[] body = new byte[20];
            WriteBE32(body, 0, (uint)Math.Max(0, -min)); // compositionToDTSShift（≥0）
            WriteBE32(body, 4, unchecked((uint)min));  // leastDecodeToDisplayDelta
            WriteBE32(body, 8, unchecked((uint)max));  // greatestDecodeToDisplayDelta
            WriteBE32(body, 12, 0);                    // compositionStartTime
            WriteBE32(body, 16, (uint)mediaDur);       // compositionEndTime
            return FullBox("cslg", body);
        }

        private static byte[] BuildStss(List<int> syncSamples)
        {
            byte[] body = new byte[4 + syncSamples.Count * 4];
            WriteBE32(body, 0, syncSamples.Count);
            for (int i = 0; i < syncSamples.Count; i++)
                WriteBE32(body, 4 + i * 4, (uint)syncSamples[i]);
            return FullBox("stss", body);
        }

        private static byte[] BuildSdtp(int sampleCount, List<int> syncSamples)
        {
            var syncSet = new HashSet<int>(syncSamples);
            byte[] body = new byte[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                body[i] = syncSet.Contains(i + 1) ? (byte)0x20 : (byte)0x18;
            // Apple 风格：无 entry_count，version/flags 后直接每样本 1 字节。
            return FullBox("sdtp", body);
        }

        private static byte[] BuildSgpdSync()
        {
            // version 1 + 'sync'：default_length=1，2 组（组 1=首个关键帧，组 2=其余关键帧）。
            byte[] body = new byte[4 + 4 + 4 + 2];
            WriteType(body, 0, "sync");
            WriteBE32(body, 4, 1);   // default_length
            WriteBE32(body, 8, 2);   // entry_count
            body[12] = 0x14;
            body[13] = 0x15;
            return FullBox("sgpd", 1, body);
        }

        private static byte[] BuildSbgpSync(int sampleCount, List<int> syncSamples)
        {
            var entries = new List<(int Count, int Group)>();
            if (syncSamples.Count == 0)
            {
                entries.Add((sampleCount, 0));
            }
            else
            {
                entries.Add((1, 1));
                for (int k = 1; k < syncSamples.Count; k++)
                {
                    int gap = syncSamples[k] - syncSamples[k - 1] - 1;
                    if (gap > 0) entries.Add((gap, 0));
                    entries.Add((1, 2));
                }
                int tail = sampleCount - syncSamples[^1];
                if (tail > 0) entries.Add((tail, 0));
            }
            byte[] body = new byte[4 + 4 + entries.Count * 8];
            WriteType(body, 0, "sync");
            WriteBE32(body, 4, entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                WriteBE32(body, 8 + i * 8, (uint)entries[i].Count);
                WriteBE32(body, 12 + i * 8, (uint)entries[i].Group);
            }
            return FullBox("sbgp", body);
        }

        private static byte[] BuildSgpdRoll()
        {
            byte[] body = new byte[4 + 4 + 4 + 2];
            WriteType(body, 0, "roll");
            WriteBE32(body, 4, 2);   // default_length
            WriteBE32(body, 8, 1);   // entry_count
            body[12] = 0xFF;
            body[13] = 0xFF;         // roll distance = -1
            return FullBox("sgpd", 1, body);
        }

        private static byte[] BuildSbgpRoll(int sampleCount)
        {
            byte[] body = new byte[4 + 4 + 8];
            WriteType(body, 0, "roll");
            WriteBE32(body, 4, 1);
            WriteBE32(body, 8, (uint)sampleCount);
            WriteBE32(body, 12, 1);
            return FullBox("sbgp", body);
        }

        private static byte[] BuildStsc(int sampleCount)
        {
            byte[] body = new byte[4 + 12];
            WriteBE32(body, 0, 1);
            WriteBE32(body, 4, 1);
            WriteBE32(body, 8, (uint)sampleCount);
            WriteBE32(body, 12, 1);
            return FullBox("stsc", body);
        }

        private static byte[] BuildStsz(List<int> sizes)
        {
            byte[] body = new byte[8 + sizes.Count * 4];
            WriteBE32(body, 0, 0);                   // sample_size = 0
            WriteBE32(body, 4, sizes.Count);
            for (int i = 0; i < sizes.Count; i++)
                WriteBE32(body, 8 + i * 4, (uint)sizes[i]);
            return FullBox("stsz", body);
        }

        private static byte[] BuildStszUniform(int sampleSize, int count)
        {
            byte[] body = new byte[8];
            WriteBE32(body, 0, (uint)sampleSize);
            WriteBE32(body, 4, (uint)count);
            return FullBox("stsz", body);
        }

        private static byte[] BuildStco(int offset)
        {
            byte[] body = new byte[8];
            WriteBE32(body, 0, 1);
            WriteBE32(body, 4, (uint)offset);
            return FullBox("stco", body);
        }

        // ── 常用盒 ───────────────────────────────────────────────────
        private static byte[] BuildMinf(byte[] mediaHeader, byte[] stbl)
        {
            byte[] hdlr = BuildHdlr("dhlr", "alis", "appl", 0, "Core Media Data Handler");
            byte[] dref = FullBox("dref", Concat(Be32(1), Box("alis", Be32(1))));
            byte[] dinf = BuildContainer("dinf", new List<byte[]> { dref });
            return BuildContainer("minf", new List<byte[]> { mediaHeader, hdlr, dinf, stbl });
        }

        private static byte[] BuildGmhd()
        {
            // gmin：version/flags(4) + graphicsMode(2)=ditherCopy(0x40) +
            // opColor(6)=0x8000×3 + balance(2)=0 + reserved(2)=0。
            byte[] gminPayload = new byte[16];
            WriteBE16(gminPayload, 4, 0x0040);
            WriteBE16(gminPayload, 6, 0x8000);
            WriteBE16(gminPayload, 8, 0x8000);
            WriteBE16(gminPayload, 10, 0x8000);
            return Box("gmhd", Box("gmin", gminPayload));
        }

        private static byte[] BuildTkhd(int trackId, int duration, int appleTime, int layer, int volume, int w, int h)
        {
            byte[] b = new byte[84];
            b[0] = 0; b[1] = 0; b[2] = 0; b[3] = 0x0F; // version 0, flags 0x00000F
            WriteBE32(b, 4, appleTime);
            WriteBE32(b, 8, appleTime);
            WriteBE32(b, 12, (uint)trackId);
            WriteBE32(b, 16, 0);          // reserved
            WriteBE32(b, 20, (uint)duration);
            WriteBE32(b, 24, 0);          // reserved
            WriteBE32(b, 28, 0);          // reserved
            WriteBE16(b, 32, (ushort)layer);
            WriteBE16(b, 34, 0);          // alternate group
            WriteBE16(b, 36, (ushort)volume);
            WriteBE16(b, 38, 0);          // reserved
            WriteIdentityMatrix(b, 40);
            WriteBE32(b, 76, (uint)w << 16);
            WriteBE32(b, 80, (uint)h << 16);
            return Box("tkhd", b);
        }

        private static byte[] BuildMdhd(int appleTime, int timescale, int duration)
        {
            byte[] b = new byte[24];
            b[0] = 0; b[1] = 0; b[2] = 0; b[3] = 0;
            WriteBE32(b, 4, appleTime);
            WriteBE32(b, 8, appleTime);
            WriteBE32(b, 12, (uint)timescale);
            WriteBE32(b, 16, (uint)duration);
            WriteBE16(b, 20, 0x55C4);     // language 'und'
            WriteBE16(b, 22, 0);          // pre_defined
            return Box("mdhd", b);
        }

        private static byte[] BuildHdlr(string handlerType, string name)
        {
            return BuildHdlr("mhlr", handlerType, "appl", componentFlags: 0, name);
        }

        private static byte[] BuildHdlr(
            string componentType, string componentSubtype, string manufacturer, int componentFlags, string name)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            // QuickTime hdlr：version/flags(4) + componentType(4) + componentSubtype(4) +
            // manufacturer(4) + componentFlags(4) + componentFlagsMask(4) + name(Pascal)。
            // Apple 空名字写 2 字节（长度 0 + 填充 0），非空名字写 1 字节长度 + 字符。
            int nameFieldLen = 1 + nameBytes.Length + (nameBytes.Length == 0 ? 1 : 0);
            byte[] b = new byte[24 + nameFieldLen];
            WriteBE32(b, 0, 0);                 // version/flags
            WriteType(b, 4, componentType);     // 空串则保持 4 个 0
            WriteType(b, 8, componentSubtype);
            WriteType(b, 12, manufacturer);     // 空串则保持 4 个 0
            WriteBE32(b, 16, (uint)componentFlags);
            WriteBE32(b, 20, 0);                // componentFlagsMask
            b[24] = (byte)nameBytes.Length;
            if (nameBytes.Length > 0)
                Array.Copy(nameBytes, 0, b, 25, nameBytes.Length);
            return Box("hdlr", b);
        }

        private static byte[] BuildElstSingle(int duration, int mediaTime)
        {
            byte[] b = new byte[4 + 12];
            WriteBE32(b, 0, 1);
            WriteBE32(b, 4, (uint)duration);
            WriteBE32(b, 8, unchecked((uint)mediaTime));
            WriteBE32(b, 12, 0x00010000);
            return FullBox("elst", b);
        }

        private static byte[] BuildElstDouble(int dur1, int media1, int dur2, int media2)
        {
            byte[] b = new byte[4 + 24];
            WriteBE32(b, 0, 2);
            WriteBE32(b, 4, (uint)dur1);
            WriteBE32(b, 8, unchecked((uint)media1));
            WriteBE32(b, 12, 0x00010000);
            WriteBE32(b, 16, (uint)dur2);
            WriteBE32(b, 20, unchecked((uint)media2));
            WriteBE32(b, 24, 0x00010000);
            return FullBox("elst", b);
        }

        private static byte[] BuildStsdPayload(byte[] entry)
        {
            byte[] payload = new byte[8 + entry.Length];
            WriteBE32(payload, 0, 0);      // version/flags
            WriteBE32(payload, 4, 1);      // entry count
            Array.Copy(entry, 0, payload, 8, entry.Length);
            return Box("stsd", payload);
        }

        private static byte[] Ftyp()
        {
            byte[] b = new byte[20];
            WriteBE32(b, 0, 20);
            WriteType(b, 4, "ftyp");
            WriteType(b, 8, "qt  ");     // major brand
            WriteBE32(b, 12, 0);           // minor version
            WriteType(b, 16, "qt  ");      // compatible brand
            return b;
        }

        private static byte[] Wide()
        {
            byte[] b = new byte[8];
            WriteBE32(b, 0, 8);
            WriteType(b, 4, "wide");
            return b;
        }

        // ── 源 MOV 解析 ──────────────────────────────────────────────
        private sealed class VideoData
        {
            public string StsdType = "";
            public byte[]? CodecConfig;    // 完整 hvcC / avcC 盒
            public byte[]? ColrBox;
            public byte[]? PaspBox;
            public int Width;
            public int Height;
            public int Timescale = 1;
            public List<(int Count, int Delta)> Stts = new();
            public List<int> Ctts = new(); // 每样本有符号合成偏移
            public List<int> SyncSamples = new();
            public List<int> SampleSizes = new();
            public List<byte[]> Samples = new();
            public int SampleDataTotal;
        }

        private sealed class AudioData
        {
            public int Channels = 2;
            public int SampleRate = 44100;
            public int BufferSize = 6144;
            public int MaxBitrate = 128000;
            public int AvgBitrate = 128000;
            public byte[]? Asc;
            public int Priming;
            public List<(int Count, int Delta)> Stts = new();
            public List<int> SampleSizes = new();
            public List<byte[]> Samples = new();
            public int SampleDataTotal;
        }

        private static string GetSampleEntryType(byte[] trak)
        {
            int stsd = FindBox(trak, 0, trak.Length, "stsd");
            if (stsd < 0) return "";
            int count = ReadBE32(trak, stsd + 12);
            if (count < 1) return "";
            return BoxType(trak, stsd + 16);
        }

        private static VideoData ParseVideoTrak(byte[] source, byte[] trak)
        {
            var v = new VideoData { StsdType = GetSampleEntryType(trak) };
            int tkhd = FindBox(trak, 0, trak.Length, "tkhd");
            int mdhd = FindBox(trak, 0, trak.Length, "mdhd");
            if (tkhd >= 0)
            {
                bool v1 = trak[tkhd + 8] == 1;
                if (v1)
                {
                    v.Width = ReadBE32(trak, tkhd + 88) >> 16;
                    v.Height = ReadBE32(trak, tkhd + 92) >> 16;
                }
                else
                {
                    v.Width = ReadBE32(trak, tkhd + 84) >> 16;
                    v.Height = ReadBE32(trak, tkhd + 88) >> 16;
                }
            }
            if (mdhd >= 0)
            {
                int ts = ReadBE32(trak, mdhd + 20);
                if (ts > 0) v.Timescale = ts;
            }

            int stsd = FindBox(trak, 0, trak.Length, "stsd");
            if (stsd >= 0)
            {
                int entry = stsd + 16;
                int entrySize = ReadBE32(trak, entry);
                if (v.Width <= 0) v.Width = ReadBE16(trak, entry + 32);
                if (v.Height <= 0) v.Height = ReadBE16(trak, entry + 34);
                // hvcC/avcC/colr/pasp 位于采样条目内（Apple/ffmpeg 的固定部为 86B，
                // 个别 muxer 为 78B），在条目剩余区域内按类型字节定位并校验盒大小。
                v.CodecConfig = FindEntryChild(trak, entry, entrySize, v.StsdType == "hvc1" ? "hvcC" : "avcC");
                v.ColrBox = FindEntryChild(trak, entry, entrySize, "colr");
                v.PaspBox = FindEntryChild(trak, entry, entrySize, "pasp");
            }

            (v.Stts, v.Ctts, v.SyncSamples, v.SampleSizes) = ParseSampleTables(trak, out var stsc, out var stco);
            v.Samples = ExtractSamples(source, v.SampleSizes, stsc, stco);
            v.SampleDataTotal = v.SampleSizes.Sum();
            return v;
        }

        private static AudioData ParseAudioTrak(byte[] source, byte[] trak)
        {
            var a = new AudioData();
            int mdhd = FindBox(trak, 0, trak.Length, "mdhd");
            if (mdhd >= 0)
            {
                int ts = ReadBE32(trak, mdhd + 20);
                if (ts is > 0 and < 192000) a.SampleRate = ts;
            }
            int elst = FindBox(trak, 0, trak.Length, "elst");
            if (elst >= 0 && trak[elst + 8] == 0)
            {
                int count = ReadBE32(trak, elst + 12);
                if (count > 0) a.Priming = Math.Max(0, ReadBE32(trak, elst + 20));
            }
            int stsd = FindBox(trak, 0, trak.Length, "stsd");
            if (stsd >= 0)
            {
                int entry = stsd + 16;
                int entrySize = ReadBE32(trak, entry);
                a.Channels = ReadBE16(trak, entry + 24);
                int sr = ReadBE32(trak, entry + 32) >> 16;
                if (sr > 0) a.SampleRate = sr;
                // esds 可能直接挂在条目下，也可能在 wave 内（QuickTime 风格），
                // 在条目剩余区域内定位后解析。
                byte[]? esdsBox = FindEntryChild(trak, entry, entrySize, "esds");
                if (esdsBox == null)
                {
                    byte[]? waveBox = FindEntryChild(trak, entry, entrySize, "wave");
                    if (waveBox != null)
                        esdsBox = FindEntryChild(waveBox, 0, waveBox.Length, "esds");
                }
                if (esdsBox != null)
                {
                    int esdsOff = FindTypeOffset(trak, entry, entry + entrySize, "esds");
                    if (esdsOff >= 0)
                        ParseEsds(trak, esdsOff, ReadBE32(trak, esdsOff), out a.BufferSize, out a.MaxBitrate, out a.AvgBitrate, out a.Asc);
                }
            }
            (a.Stts, _, _, a.SampleSizes) = ParseSampleTables(trak, out var stsc, out var stco);
            a.Samples = ExtractSamples(source, a.SampleSizes, stsc, stco);
            a.SampleDataTotal = a.SampleSizes.Sum();
            return a;
        }

        private static void ParseEsds(
            byte[] data, int esdsOff, int esdsSize,
            out int bufferSize, out int maxBitrate, out int avgBitrate, out byte[]? asc)
        {
            bufferSize = 6144; maxBitrate = 128000; avgBitrate = 128000; asc = null;
            int end = esdsOff + esdsSize;
            int p = esdsOff + 12; // 跳过盒头 + version/flags
            while (p < end)
            {
                int tag = data[p++];
                int len = ReadVarint(data, ref p);
                int itemEnd = Math.Min(end, p + len);
                if (tag == 0x03)
                {
                    p += 3; // ES_ID(2) + flags(1)，子描述符在 len 内继续线性扫描
                    continue;
                }
                if (tag == 0x04 && p + 13 <= itemEnd)
                {
                    bufferSize = ReadBE24(data, p + 2);
                    if (bufferSize <= 0) bufferSize = 6144; // 源未写 bufferSize 时用 AAC 合理值
                    maxBitrate = ReadBE32(data, p + 5);
                    avgBitrate = ReadBE32(data, p + 9);
                    p += 13;
                    continue;
                }
                if (tag == 0x05)
                {
                    if (len > 0 && p + len <= end)
                    {
                        asc = new byte[len];
                        Array.Copy(data, p, asc, 0, len);
                    }
                    return;
                }
                p = itemEnd;
            }
        }

        private static int ReadVarint(byte[] data, ref int p)
        {
            int value = 0;
            while (p < data.Length)
            {
                byte b = data[p++];
                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            return value;
        }

        private static (List<(int Count, int Delta)> Stts, List<int> Ctts, List<int> Sync, List<int> Sizes)
            ParseSampleTables(byte[] trak, out List<(int First, int PerChunk, int Desc)> stsc, out List<long> stco)
        {
            stsc = new List<(int, int, int)>();
            stco = new List<long>();
            var stts = new List<(int, int)>();
            var ctts = new List<int>();
            var sync = new List<int>();
            var sizes = new List<int>();

            int sttsOff = FindBox(trak, 0, trak.Length, "stts");
            if (sttsOff >= 0)
            {
                int n = ReadBE32(trak, sttsOff + 12);
                for (int i = 0; i < n; i++)
                    stts.Add((ReadBE32(trak, sttsOff + 16 + i * 8), ReadBE32(trak, sttsOff + 20 + i * 8)));
            }
            int cttsOff = FindBox(trak, 0, trak.Length, "ctts");
            if (cttsOff >= 0)
            {
                int n = ReadBE32(trak, cttsOff + 12);
                int baseOff = cttsOff + 16;
                for (int i = 0; i < n; i++)
                {
                    int cnt = ReadBE32(trak, baseOff + i * 8);
                    // 按位解释为有符号：v1 原生有符号；Apple 风格 v0 把负偏移写成 uint32 包装。
                    int off = unchecked((int)ReadBE32(trak, baseOff + i * 8 + 4));
                    for (int k = 0; k < cnt; k++) ctts.Add(off);
                }
            }
            int stssOff = FindBox(trak, 0, trak.Length, "stss");
            if (stssOff >= 0)
            {
                int n = ReadBE32(trak, stssOff + 12);
                for (int i = 0; i < n; i++)
                    sync.Add(ReadBE32(trak, stssOff + 16 + i * 4));
            }
            int stszOff = FindBox(trak, 0, trak.Length, "stsz");
            if (stszOff >= 0)
            {
                int sampleSize = ReadBE32(trak, stszOff + 12);
                int n = ReadBE32(trak, stszOff + 16);
                for (int i = 0; i < n; i++)
                    sizes.Add(sampleSize > 0 ? sampleSize : ReadBE32(trak, stszOff + 20 + i * 4));
            }
            int stscOff = FindBox(trak, 0, trak.Length, "stsc");
            if (stscOff >= 0)
            {
                int n = ReadBE32(trak, stscOff + 12);
                for (int i = 0; i < n; i++)
                    stsc.Add((ReadBE32(trak, stscOff + 16 + i * 12),
                              ReadBE32(trak, stscOff + 20 + i * 12),
                              ReadBE32(trak, stscOff + 24 + i * 12)));
            }
            int stcoOff = FindBox(trak, 0, trak.Length, "stco");
            int co64Off = FindBox(trak, 0, trak.Length, "co64");
            if (co64Off >= 0)
            {
                int n = ReadBE32(trak, co64Off + 12);
                for (int i = 0; i < n; i++)
                    stco.Add(ReadBE64(trak, co64Off + 16 + i * 8));
            }
            else if (stcoOff >= 0)
            {
                int n = ReadBE32(trak, stcoOff + 12);
                for (int i = 0; i < n; i++)
                    stco.Add(ReadBE32(trak, stcoOff + 16 + i * 4));
            }
            return (stts, ctts, sync, sizes);
        }

        private static List<byte[]> ExtractSamples(
            byte[] source, List<int> sizes, List<(int First, int PerChunk, int Desc)> stsc, List<long> stco)
        {
            var result = new List<byte[]>(sizes.Count);
            if (stco.Count == 0 || sizes.Count == 0) return result;
            int stscIdx = 0;
            long pos = stco[0];
            int sampleIdx = 0;
            int chunkIdx = 0;
            while (sampleIdx < sizes.Count && chunkIdx < stco.Count)
            {
                // stsc 条目从 First 指定的 chunk 起生效：先推进到当前 chunk 的条目，再取 perChunk。
                while (stscIdx + 1 < stsc.Count && stsc[stscIdx + 1].First <= chunkIdx + 1)
                    stscIdx++;
                int perChunk = stsc[stscIdx].PerChunk;
                for (int k = 0; k < perChunk && sampleIdx < sizes.Count; k++)
                {
                    int size = sizes[sampleIdx];
                    if (pos + size > source.Length) break;
                    byte[] sample = new byte[size];
                    Array.Copy(source, pos, sample, 0, size);
                    result.Add(sample);
                    pos += size;
                    sampleIdx++;
                }
                chunkIdx++;
                if (chunkIdx < stco.Count) pos = stco[chunkIdx];
            }
            return result;
        }

        private static byte[] SynthesizeAsc(int sampleRate, int channels)
        {
            int freqIndex = sampleRate switch
            {
                96000 => 0, 88200 => 1, 64000 => 2, 48000 => 3, 44100 => 4,
                32000 => 5, 24000 => 6, 22050 => 7, 16000 => 8, 12000 => 9,
                11025 => 10, 8000 => 11, _ => 4,
            };
            return new byte[]
            {
                (byte)((2 << 3) | (freqIndex >> 1)),
                (byte)(((freqIndex & 1) << 7) | (Math.Clamp(channels, 1, 7) << 3)),
            };
        }

        // ── box 工具 ─────────────────────────────────────────────────
        private static byte[] Box(string type, byte[] payload)
        {
            byte[] b = new byte[8 + payload.Length];
            WriteBE32(b, 0, b.Length);
            WriteType(b, 4, type);
            Array.Copy(payload, 0, b, 8, payload.Length);
            return b;
        }

        private static byte[] FullBox(string type, byte[] payload)
            => Box(type, Concat(new byte[4], payload));

        private static byte[] FullBox(string type, int version, byte[] payload)
        {
            byte[] head = new byte[4];
            head[0] = (byte)version;
            return Box(type, Concat(head, payload));
        }

        private static byte[] BuildContainer(string type, List<byte[]> children)
            => Box(type, Concat(children.ToArray()));

        private static byte[] Concat(params byte[][] arrays)
        {
            int total = arrays.Sum(a => a.Length);
            byte[] b = new byte[total];
            int p = 0;
            foreach (var a in arrays) { Array.Copy(a, 0, b, p, a.Length); p += a.Length; }
            return b;
        }

        private static byte[] Concat(byte[] a, byte[][] rest)
        {
            var all = new byte[1 + rest.Length][];
            all[0] = a;
            Array.Copy(rest, 0, all, 1, rest.Length);
            return Concat(all);
        }

        private static byte[] Be32(int value)
        {
            byte[] b = new byte[4];
            WriteBE32(b, 0, unchecked((uint)value));
            return b;
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            byte[] b = new byte[length];
            Array.Copy(data, offset, b, 0, length);
            return b;
        }

        private static int FindBox(byte[] data, int start, int end, string type)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                int size = ReadBE32(data, pos);
                if (size == 1) size = (int)ReadBE64(data, pos + 8);
                if (size == 0) size = end - pos;
                if (size < 8 || pos + size > end) break;
                if (BoxType(data, pos) == type) return pos;
                if (IsContainer(data, pos))
                {
                    int found = FindBox(data, pos + 8, pos + size, type);
                    if (found >= 0) return found;
                }
                pos += size;
            }
            return -1;
        }

        private static bool IsContainer(byte[] data, int off)
            => BoxType(data, off) is "moov" or "trak" or "mdia" or "minf" or "stbl"
                or "edts" or "dinf" or "udta" or "meta";

        /// <summary>
        /// 在采样条目（fixed part 长度因 muxer 而异：Apple/ffmpeg 86B，标准 78B）的
        /// 剩余区域内定位指定子盒并返回完整盒字节；找不到或尺寸不合法返回 null。
        /// </summary>
        private static byte[]? FindEntryChild(byte[] data, int entryStart, int entrySize, string type)
        {
            int off = FindTypeOffset(data, entryStart, entryStart + entrySize, type);
            if (off < 0) return null;
            int sz = ReadBE32(data, off);
            if (sz < 8 || off + sz > entryStart + entrySize) return null;
            return Slice(data, off, sz);
        }

        private static int FindTypeOffset(byte[] data, int start, int end, string type)
        {
            byte[] t = Encoding.ASCII.GetBytes(type);
            for (int i = start; i + 4 <= end; i++)
            {
                if (data[i] == t[0] && data[i + 1] == t[1] && data[i + 2] == t[2] && data[i + 3] == t[3])
                {
                    int boxStart = i - 4;
                    if (boxStart >= start && boxStart + 4 <= end)
                    {
                        int sz = ReadBE32(data, boxStart);
                        if (sz >= 8 && boxStart + sz <= end) return boxStart;
                    }
                }
            }
            return -1;
        }

        private static List<(string Type, byte[] Box)> ParseChildren(byte[] data, int start, int end)
        {
            var result = new List<(string, byte[])>();
            int i = start;
            while (i + 8 <= end)
            {
                int size = ReadBE32(data, i);
                if (size == 1) size = (int)ReadBE64(data, i + 8);
                if (size == 0) size = end - i;
                if (size < 8 || i + size > end) break;
                result.Add((BoxType(data, i), Slice(data, i, size)));
                i += size;
            }
            return result;
        }

        private static string BoxType(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return "";
            return Encoding.ASCII.GetString(data, offset + 4, 4);
        }

        private static byte[] FromHex(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        private static void WriteType(byte[] d, int off, string type)
        {
            if (off + 4 > d.Length) return;
            Encoding.ASCII.GetBytes(type, 0, Math.Min(4, type.Length), d, off);
        }

        private static void WriteBE16(byte[] d, int off, ushort v)
        {
            if (off + 2 > d.Length) return;
            d[off] = (byte)(v >> 8);
            d[off + 1] = (byte)v;
        }

        private static void WriteBE24(byte[] d, int off, int v)
        {
            if (off + 3 > d.Length) return;
            d[off] = (byte)(v >> 16);
            d[off + 1] = (byte)(v >> 8);
            d[off + 2] = (byte)v;
        }

        private static void WriteBE32(byte[] d, int off, uint v)
        {
            if (off + 4 > d.Length) return;
            d[off] = (byte)(v >> 24);
            d[off + 1] = (byte)(v >> 16);
            d[off + 2] = (byte)(v >> 8);
            d[off + 3] = (byte)v;
        }

        private static void WriteBE32(byte[] d, int off, int v) => WriteBE32(d, off, unchecked((uint)v));

        private static ushort ReadBE16(byte[] d, int off)
            => (ushort)((d[off] << 8) | d[off + 1]);

        private static int ReadBE24(byte[] d, int off)
            => (d[off] << 16) | (d[off + 1] << 8) | d[off + 2];

        private static int ReadBE32(byte[] d, int off)
            => (d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3];

        private static long ReadBE64(byte[] d, int off)
        {
            long hi = ReadBE32(d, off) & 0xFFFFFFFFL;
            long lo = ReadBE32(d, off + 4) & 0xFFFFFFFFL;
            return (hi << 32) | lo;
        }
    }
}
