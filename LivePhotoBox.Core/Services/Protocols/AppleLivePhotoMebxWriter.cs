using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    /*
     * AppleLivePhotoMebxWriter.cs
     *
     * 给拆分输出的 MOV 补全 Apple Live Photo 结构，使其可被 iPhone / Apple Photos 导入。
     *
     *   - 归一化 ffmpeg 生成的 Video/Audio 轨（tapt/tkhd/mdhd/mvhd/hdlr/vmhd/stsd/dref 等）
     *   - 追加 ContentDescribes 运动元数据轨与 mebx 静态封面轨
     *   - 布局对齐能导入 iPhone 的最小 4 轨样本 IMG_6675.MOV
     */
    public static class AppleLivePhotoMebxWriter
    {
        // Track3（ContentDescribes / NRT Metadata，1043 字节）—— 最小样本逐字节复刻。
        private static readonly byte[] ContentDescribesTrackTemplate = Convert.FromBase64String(
            "AAAEE3RyYWsAAABcdGtoZAAAAA/hpMDf4aTA3wAAAAMAAAAAAACsRAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAADBlZHRzAAAAKGVsc3QAAAAAAAAAAgAACJ3/////AAEAAAAAo6cAAAAAAAEAAAAAA39tZGlhAAAAIG1kaGQAAAAA4aTA3+GkwN8AAOpgAADeqFXEAAAAAAA0aGRscgAAAABtaGxybWV0YWFwcGwAAAABAAAAABNDb3JlIE1lZGlhIE1ldGFkYXRhAAADI21pbmYAAAAgZ21oZAAAABhnbWluAAAAAABAgACAAIAAAAAAAAAAADhoZGxyAAAAAGRobHJhbGlzYXBwbAAAAAAAAAAAF0NvcmUgTWVkaWEgRGF0YSBIYW5kbGVyAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADGFsaXMAAAABAAACn3N0YmwAAAIrc3RzZAAAAAAAAAABAAACG21lYngAAAAAAAAAAQAAAgtrZXlzAAACAwAAAAEAAAAva2V5ZG1kdGFjb20uYXBwbGUucXVpY2t0aW1lLmxpdmUtcGhvdG8taW5mbwAAAENkdHlwAAAAAWNvbS5hcHBsZS5xdWlja3RpbWUuY29tLmFwcGxlLnF1aWNrdGltZS5saXZlLXBob3RvLWluZm8AAAFxc2V0dQAAAVljZmd2YnBsaXN0MDDTAQIDBAUMXxAhTGl2ZVBob3RvTWV0YWRhdGFTZXR1cERhdGFWZXJzaW9uXVN5c3RlbVZlcnNpb25fEBFGcmFtZXdvcmtWZXJzaW9ucxAB0wYHCAkKC18QE1Byb2R1Y3RCdWlsZFZlcnNpb25bUHJvZHVjdE5hbWVeUHJvZHVjdFZlcnNpb25YMjFBNTI3N2hZaVBob25lIE9TVDE3LjDUDQ4PEBESExRaQ29yZU1vdGlvbl1DTUNhcHR1cmVDb3JlXkgxMElTUFNlcnZpY2VzWUNvcmVNZWRpYVgyODY4LjAuMlc0NDYuNS4zVDIwLjJeMzA0NS42OS4yLjExLjQACAAPADMAQQBVAFcAXgB0AIAAjwCYAKIApwCwALsAyQDYAOIA6wDzAPgAAAAAAAACAQAAAAAAAAAVAAAAAAAAAAAAAAAAAAABBwAAABBkaW1zAAAHgAAABaAAAAAYY3RwcwAAABBkdHlwAAAAAAAAAAAAAAAYc3R0cwAAAAAAAAABAAAAOQAAA+gAAAAoc3RzYwAAAAAAAAACAAAAAQAAAB4AAAABAAAAAgAAABsAAAABAAAAFHN0c3oAAAAAAAAAkAAAADkAAAAYc3RjbwAAAAAAAAACAAPY/wAEh/Y=");

        // Track4（mebx 静态封面轨，672 字节）—— 最小样本逐字节复刻。
        private static readonly byte[] MebxCoverTrackTemplate = Convert.FromBase64String(
            "AAACoHRyYWsAAABcdGtoZAAAAA/hpMDf4aTA3wAAAAQAAAAAAABWbAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAADBlZHRzAAAAKGVsc3QAAAAAAAAAAgAAViL/////AAEAAAAAAEoAAAAAAAEAAAAAAgxtZGlhAAAAIG1kaGQAAAAA4aTA3+GkwN8AAAJYAAAAAVXEAAAAAAA0aGRscgAAAABtaGxybWV0YWFwcGwAAAABAAAAABNDb3JlIE1lZGlhIE1ldGFkYXRhAAABsG1pbmYAAAAgZ21oZAAAABhnbWluAAAAAABAgACAAIAAAAAAAAAAADhoZGxyAAAAAGRobHJhbGlzYXBwbAAAAAAAAAAAF0NvcmUgTWVkaWEgRGF0YSBIYW5kbGVyAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADGFsaXMAAAABAAABLHN0YmwAAADIc3RzZAAAAAAAAAABAAAAuG1lYngAAAAAAAAAAQAAAKhrZXlzAAAASAAAAAEAAAAwa2V5ZG1kdGFjb20uYXBwbGUucXVpY2t0aW1lLnN0aWxsLWltYWdlLXRpbWUAAAAQZHR5cAAAAAAAAABBAAAAWAAAAAIAAABAa2V5ZG1kdGFjb20uYXBwbGUucXVpY2t0aW1lLmxpdmUtcGhvdG8tc3RpbGwtaW1hZ2UtdHJhbnNmb3JtAAAAEGR0eXAAAAAAAAAAUwAAABhzdHRzAAAAAAAAAAEAAAABAAAAAQAAABxzdHNjAAAAAAAAAAEAAAABAAAAAQAAAAEAAAAUc3RzegAAAAAAAABZAAAAAQAAABRzdGNvAAAAAAAAAAEAA+nf");

        // mebx 封面轨 sample（89 字节：still-image-time=-1 + still-image-transform 单位阵）。
        private static readonly byte[] MebxCoverSample = Convert.FromBase64String(
            "AAAACQAAAAH/AAAAUAAAAAI/8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD/wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAP/AAAAAAAAA=");

        // ContentDescribes 轨 LivePhotoInfo sample（144 字节，静态无运动 blob，逐字节复刻）。
        private static readonly byte[] LivePhotoInfoSample = Convert.FromBase64String(
            "AAAAkAAAAAEDAAAAvcNtPOO1622AAAAAe4CtQlotZEEKCMs+f+6mvXnp9j8AAIBABAD/AAAAAAAAAAAAAAAAAAAAAAAAAAAABwAAAFJehz7mblK/GypqxNN4Yr92HtI93j+OwxP1Lzmy8EQ5/zCdvxoX8e0bBwAAIGeW7RsHAAAAAAAAAAAAAAAAAAAAAAAA");

        private const int LivePhotoInfoTimeScale = 60000;   // Track3 mdhd.timescale
        private const int LivePhotoInfoSampleDelta = 1000;  // 每 sample 1000 ticks = 1/60 s
        private const int MebxCoverTimeScale = 600;         // Track4 mdhd.timescale

        // QuickTime 时间戳 epoch 差（1904-01-01 vs 1970-01-01，秒）。
        private const long AppleEpochOffsetSeconds = 2082844800;

        // 给 MOV 归一化 Video/Audio 轨 + 追加 ContentDescribes + mebx 封面轨。成功返回 true。
        public static bool TryAppendStillImageTrack(string movPath, double coverSeconds, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(movPath);
                byte[]? grown = AppendStillImageTrack(data, coverSeconds, out error);
                if (grown == null)
                    return false;
                File.WriteAllBytes(movPath, grown);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static byte[]? AppendStillImageTrack(byte[] data, double coverSeconds, out string? error)
        {
            error = null;

            // 1. 定位 moov。
            int moovOff = -1, moovSize = 0;
            for (int pos = 0; pos + 8 <= data.Length; )
            {
                int sz = ReadBE32(data, pos);
                if (sz < 8) break;
                if (IsType(data, pos, "moov")) { moovOff = pos; moovSize = sz; break; }
                pos += sz;
            }
            if (moovOff < 0) { error = "moov atom not found"; return null; }
            int oldMoovEnd = moovOff + moovSize;

            // 2. mvhd：timescale + 视频时长（version 0）。
            int mvhdOff = FindAtom(data, moovOff + 8, oldMoovEnd, "mvhd");
            if (mvhdOff < 0) { error = "mvhd atom not found"; return null; }
            if (data[mvhdOff + 8] != 0) { error = "mvhd version != 0 unsupported"; return null; }
            int timescale = ReadBE32(data, mvhdOff + 20);
            int movieDuration = ReadBE32(data, mvhdOff + 24);
            if (timescale <= 0) timescale = 1;

            // 3. Apple epoch 时间戳（bitexact 已清零 tkhd/mdhd/mvhd，这里写回真实时间）。
            int appleTime = unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + AppleEpochOffsetSeconds));

            // 4. 遍历 moov 子 box，归一化 Video/Audio 轨。
            var children = ParseChildren(data, moovOff + 8, oldMoovEnd);
            byte[]? normVideo = null, normAudio = null;
            int videoOldLen = 0, audioOldLen = 0;
            int maxTrackId = 0;

            foreach (var (type, box) in children)
            {
                if (type != "trak") continue;
                bool isVideo = FindAtom(box, 8, box.Length, "vmhd") >= 0;
                bool isAudio = FindAtom(box, 8, box.Length, "smhd") >= 0;
                int tkhd = FindAtom(box, 8, box.Length, "tkhd");
                int tid = tkhd >= 0 ? ReadBE32(box, tkhd + 20) : 0;
                if (tid > maxTrackId) maxTrackId = tid;

                if (isVideo)
                {
                    int vw = tkhd >= 0 ? ReadBE32(box, tkhd + 84) >> 16 : 0;
                    int vh = tkhd >= 0 ? ReadBE32(box, tkhd + 88) >> 16 : 0;
                    videoOldLen = box.Length;
                    normVideo = NormalizeTrak(box, isVideo: true, vw, vh, appleTime);
                }
                else if (isAudio)
                {
                    audioOldLen = box.Length;
                    normAudio = NormalizeTrak(box, isVideo: false, 0, 0, appleTime);
                }
            }

            int normalizationDelta =
                ((normVideo?.Length ?? videoOldLen) - videoOldLen) +
                ((normAudio?.Length ?? audioOldLen) - audioOldLen);
            const int MetadataTrakSize = 1043 + 672; // ContentDescribes + mebx 封面（模板固定）
            int moovDelta = normalizationDelta + MetadataTrakSize;

            // 5. 构造两条元数据轨（stco 指向新追加的 sample）。
            double videoSeconds = (double)movieDuration / timescale;
            int sampleCount = (int)Math.Clamp(Math.Round(videoSeconds * 60.0), 2, 600);
            int chunk1 = (sampleCount + 1) / 2;
            int sampleDataOff = data.Length + moovDelta + 8; // 新 mdat 头 8 字节之后

            int contentTrackId = maxTrackId + 1;
            int coverTrackId = maxTrackId + 2;
            byte[] contentTrak = BuildContentDescribesTrack(contentTrackId, timescale, videoSeconds, sampleCount, chunk1, sampleDataOff);
            byte[] coverTrak = BuildMebxCoverTrack(coverTrackId, timescale, coverSeconds, sampleDataOff + sampleCount * LivePhotoInfoSample.Length);

            // 元数据轨时间戳与 Video/Audio 对齐到同一时刻。
            PatchTrackTimestamps(contentTrak, appleTime);
            PatchTrackTimestamps(coverTrak, appleTime);

            // 6. 归一化后的 Video/Audio 轨：stco 落在旧 moov 之后的（mdat）顶移 moovDelta。
            if (normVideo != null) ShiftTrakStco(normVideo, oldMoovEnd, moovDelta);
            if (normAudio != null) ShiftTrakStco(normAudio, oldMoovEnd, moovDelta);

            // 7. 重组 moov：mvhd' + Video + Audio + ContentDescribes + mebx + 其余（udta/meta 最后）。
            var moovPayload = new List<byte[]>();
            var pending = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "mvhd")
                {
                    byte[] m = (byte[])box.Clone();
                    WriteBE32(m, 12, appleTime);
                    WriteBE32(m, 16, appleTime);
                    WriteBE32(m, 104, coverTrackId + 1); // NextTrackID
                    moovPayload.Add(m);
                }
                else if (type == "trak")
                {
                    bool isVideo = FindAtom(box, 8, box.Length, "vmhd") >= 0;
                    bool isAudio = FindAtom(box, 8, box.Length, "smhd") >= 0;
                    if (isVideo && normVideo != null) moovPayload.Add(normVideo);
                    else if (isAudio && normAudio != null) moovPayload.Add(normAudio);
                    else moovPayload.Add(box);
                }
                else
                {
                    pending.Add(box);
                }
            }
            moovPayload.Add(contentTrak);
            moovPayload.Add(coverTrak);
            moovPayload.AddRange(pending);

            byte[] newMoov = BuildContainer("moov", moovPayload);

            // 8. 拼装新文件：[0..moovOff) + 新 moov + 旧 mdat（顶移 moovDelta）+ 新 mdat。
            int samplesSize = sampleCount * LivePhotoInfoSample.Length + MebxCoverSample.Length;
            int sampleMdatSize = 8 + samplesSize;
            byte[] grown = new byte[data.Length + moovDelta + sampleMdatSize];
            Array.Copy(data, 0, grown, 0, moovOff);
            Array.Copy(newMoov, 0, grown, moovOff, newMoov.Length);
            Array.Copy(data, oldMoovEnd, grown, moovOff + newMoov.Length, data.Length - oldMoovEnd);

            int sampleMdatOff = data.Length + moovDelta;
            WriteBE32(grown, sampleMdatOff, sampleMdatSize);
            WriteType(grown, sampleMdatOff + 4, "mdat");
            for (int i = 0; i < sampleCount; i++)
                Array.Copy(LivePhotoInfoSample, 0, grown,
                    sampleMdatOff + 8 + i * LivePhotoInfoSample.Length, LivePhotoInfoSample.Length);
            Array.Copy(MebxCoverSample, 0, grown,
                sampleMdatOff + 8 + sampleCount * LivePhotoInfoSample.Length, MebxCoverSample.Length);

            return grown;
        }

        // ── Video/Audio 轨归一化 ────────────────────────────────────────────────

        private static byte[] NormalizeTrak(byte[] trak, bool isVideo, int videoWidth, int videoHeight, int appleTime)
        {
            var children = ParseChildren(trak, 8, trak.Length);
            var result = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "tkhd")
                {
                    result.Add(RebuildTkhd(box, appleTime));
                    if (isVideo) result.Add(BuildTapt(videoWidth, videoHeight));
                }
                else if (type == "mdia")
                {
                    result.Add(RebuildMdia(box, isVideo, appleTime));
                }
                else
                {
                    result.Add(box);
                }
            }
            return BuildContainer("trak", result);
        }

        private static byte[] RebuildMdia(byte[] mdia, bool isVideo, int appleTime)
        {
            var children = ParseChildren(mdia, 8, mdia.Length);
            var result = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "mdhd")
                    result.Add(RebuildMdhd(box, appleTime));
                else if (type == "hdlr")
                    result.Add(BuildHdlr("mhlr", isVideo ? "vide" : "soun", isVideo ? "Core Media Video" : "Core Media Audio"));
                else if (type == "minf")
                    result.Add(RebuildMinf(box, isVideo));
                else
                    result.Add(box);
            }
            return BuildContainer("mdia", result);
        }

        private static byte[] RebuildMinf(byte[] minf, bool isVideo)
        {
            var children = ParseChildren(minf, 8, minf.Length);
            var result = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "vmhd" && isVideo)
                    result.Add(BuildVmhd());
                else if (type == "hdlr")
                    result.Add(BuildHdlr("dhlr", "alis", "Core Media Data Handler"));
                else if (type == "dinf")
                    result.Add(RebuildDinf(box));
                else if (type == "stbl")
                    result.Add(RebuildStbl(box, isVideo));
                else
                    result.Add(box);
            }
            return BuildContainer("minf", result);
        }

        private static byte[] RebuildStbl(byte[] stbl, bool isVideo)
        {
            if (!isVideo) return stbl; // 音频 stbl 不改（保留 sgpd/sbgp 等）
            var children = ParseChildren(stbl, 8, stbl.Length);
            var result = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "stsd") result.Add(RebuildStsd(box));
                else result.Add(box);
            }
            return BuildContainer("stbl", result);
        }

        // 去掉 hvc1 里 ffmpeg 多写的 fiel / pasp。
        private static byte[] RebuildStsd(byte[] stsd)
        {
            int entryCount = ReadBE32(stsd, 12);
            int entryOff = 16;
            var entries = new List<byte[]>();
            for (int i = 0; i < entryCount; i++)
            {
                int esz = ReadBE32(stsd, entryOff);
                if (esz < 8 || entryOff + esz > stsd.Length) break;
                string etype = BoxType(stsd, entryOff + 4);
                byte[] entry = new byte[esz];
                Array.Copy(stsd, entryOff, entry, 0, esz);
                if (etype == "hvc1") entry = RebuildHvc1Entry(entry);
                entries.Add(entry);
                entryOff += esz;
            }

            int total = 8;
            foreach (var e in entries) total += e.Length;
            byte[] payload = new byte[total];
            Array.Copy(stsd, 8, payload, 0, 4); // version/flags
            WriteBE32(payload, 4, entryCount);
            int p = 8;
            foreach (var e in entries) { Array.Copy(e, 0, payload, p, e.Length); p += e.Length; }
            return BuildBox("stsd", payload);
        }

        private static byte[] RebuildHvc1Entry(byte[] entry)
        {
            const int fixedLen = 86; // hvc1 固定头之后，子 box 起始偏移
            var children = ParseChildren(entry, fixedLen, entry.Length);
            var kept = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type != "fiel" && type != "pasp") kept.Add(box);
            }
            int total = fixedLen - 8;
            foreach (var b in kept) total += b.Length;
            byte[] payload = new byte[total];
            Array.Copy(entry, 8, payload, 0, fixedLen - 8);
            int p = fixedLen - 8;
            foreach (var b in kept) { Array.Copy(b, 0, payload, p, b.Length); p += b.Length; }
            return BuildBox("hvc1", payload);
        }

        private static byte[] RebuildDinf(byte[] dinf)
        {
            var children = ParseChildren(dinf, 8, dinf.Length);
            var result = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "dref") result.Add(RebuildDref(box));
                else result.Add(box);
            }
            return BuildContainer("dinf", result);
        }

        private static byte[] RebuildDref(byte[] dref)
        {
            byte[] b = (byte[])dref.Clone();
            int count = ReadBE32(b, 12);
            int e = 16;
            for (int i = 0; i < count && e + 8 <= b.Length; i++)
            {
                int esz = ReadBE32(b, e);
                if (esz < 8 || e + esz > b.Length) break;
                if (BoxType(b, e + 4) == "url ") WriteType(b, e + 4, "alis");
                e += esz;
            }
            return b;
        }

        private static byte[] RebuildTkhd(byte[] tkhd, int appleTime)
        {
            byte[] b = (byte[])tkhd.Clone();
            b[9] = 0; b[10] = 0; b[11] = 0x0f; // flags = enabled+in_movie+in_preview+in_poster
            WriteBE32(b, 12, appleTime);
            WriteBE32(b, 16, appleTime);
            return b;
        }

        private static byte[] RebuildMdhd(byte[] mdhd, int appleTime)
        {
            byte[] b = (byte[])mdhd.Clone();
            WriteBE32(b, 12, appleTime);
            WriteBE32(b, 16, appleTime);
            return b;
        }

        private static byte[] BuildVmhd()
        {
            byte[] box = new byte[20];
            WriteBE32(box, 0, 20);
            WriteType(box, 4, "vmhd");
            WriteBE16(box, 12, 0x40);   // graphicsmode = ditherCopy
            WriteBE16(box, 14, 0x8000); // opcolor R
            WriteBE16(box, 16, 0x8000); // G
            WriteBE16(box, 18, 0x8000); // B
            return box;
        }

        private static byte[] BuildHdlr(string preDefined, string handlerType, string name)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            byte[] box = new byte[33 + nameBytes.Length];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, "hdlr");
            // version/flags @ 8 = 0（数组零初始化）
            WriteType(box, 12, preDefined);
            WriteType(box, 16, handlerType);
            WriteType(box, 20, "appl");
            // flags @ 24、mask @ 28 = 0
            box[32] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, box, 33, nameBytes.Length);
            return box;
        }

        private static byte[] BuildTapt(int width, int height)
        {
            byte[] box = new byte[68];
            WriteBE32(box, 0, 68);
            WriteType(box, 4, "tapt");
            WriteAperture(box, 8, "clef", width, height);
            WriteAperture(box, 28, "prof", width, height);
            WriteAperture(box, 48, "enof", width, height);
            return box;
        }

        private static void WriteAperture(byte[] box, int off, string type, int width, int height)
        {
            WriteBE32(box, off, 20);
            WriteType(box, off + 4, type);
            WriteBE32(box, off + 8, 0);              // version/flags
            WriteBE32(box, off + 12, width << 16);   // 16.16 定点
            WriteBE32(box, off + 16, height << 16);
        }

        private static void PatchTrackTimestamps(byte[] trak, int appleTime)
        {
            int tkhd = FindAtom(trak, 8, trak.Length, "tkhd");
            if (tkhd >= 0) { WriteBE32(trak, tkhd + 12, appleTime); WriteBE32(trak, tkhd + 16, appleTime); }
            int mdhd = FindAtom(trak, 8, trak.Length, "mdhd");
            if (mdhd >= 0) { WriteBE32(trak, mdhd + 12, appleTime); WriteBE32(trak, mdhd + 16, appleTime); }
        }

        private static void ShiftTrakStco(byte[] trak, int oldMoovEnd, int delta)
        {
            int stco = FindAtom(trak, 8, trak.Length, "stco");
            if (stco < 0) return;
            int count = ReadBE32(trak, stco + 12);
            for (int i = 0; i < count; i++)
            {
                int offField = stco + 16 + i * 4;
                int v = ReadBE32(trak, offField);
                if (v >= oldMoovEnd) WriteBE32(trak, offField, v + delta);
            }
        }

        // ── 元数据轨构造 ────────────────────────────────────────────────────────

        private static byte[] BuildContentDescribesTrack(
            int trackId, int timescale, double videoSeconds, int sampleCount, int chunk1, int dataOff)
        {
            byte[] trak = (byte[])ContentDescribesTrackTemplate.Clone();

            int tkhd = FindAtom(trak, 8, trak.Length, "tkhd");
            int elst = FindAtom(trak, 8, trak.Length, "elst");
            int mdhd = FindAtom(trak, 8, trak.Length, "mdhd");
            int stts = FindAtom(trak, 8, trak.Length, "stts");
            int stsc = FindAtom(trak, 8, trak.Length, "stsc");
            int stsz = FindAtom(trak, 8, trak.Length, "stsz");
            int stco = FindAtom(trak, 8, trak.Length, "stco");

            int mediaDur = sampleCount * LivePhotoInfoSampleDelta;
            int leadIn = (int)Math.Round(0.05 * timescale);
            int mediaMovie = (int)Math.Round(mediaDur * (double)timescale / LivePhotoInfoTimeScale);
            if (mediaMovie < 1) mediaMovie = 1;

            WriteBE32(trak, mdhd + 24, mediaDur);
            WriteBE32(trak, elst + 16, leadIn);
            WriteBE32(trak, elst + 28, mediaMovie);
            WriteBE32(trak, tkhd + 20, trackId);
            WriteBE32(trak, tkhd + 28, leadIn + mediaMovie);

            WriteBE32(trak, stts + 16, sampleCount);
            WriteBE32(trak, stsc + 20, chunk1);
            WriteBE32(trak, stsc + 32, sampleCount - chunk1);
            WriteBE32(trak, stsz + 16, sampleCount);
            WriteBE32(trak, stco + 16, dataOff);
            WriteBE32(trak, stco + 20, dataOff + chunk1 * LivePhotoInfoSample.Length);

            return trak;
        }

        private static byte[] BuildMebxCoverTrack(int trackId, int timescale, double coverSeconds, int dataOff)
        {
            byte[] trak = (byte[])MebxCoverTrackTemplate.Clone();

            int tkhd = FindAtom(trak, 8, trak.Length, "tkhd");
            int elst = FindAtom(trak, 8, trak.Length, "elst");
            int stco = FindAtom(trak, 8, trak.Length, "stco");

            int coverDur = (int)Math.Round(coverSeconds * timescale);
            if (coverDur < 0) coverDur = 0;
            int oneFrame = (int)Math.Round((double)timescale / MebxCoverTimeScale);
            if (oneFrame < 1) oneFrame = 1;

            WriteBE32(trak, elst + 16, coverDur);
            WriteBE32(trak, elst + 28, oneFrame);
            WriteBE32(trak, tkhd + 20, trackId);
            WriteBE32(trak, tkhd + 28, coverDur + oneFrame);
            WriteBE32(trak, stco + 16, dataOff);

            return trak;
        }

        // ── 通用 box 工具 ──────────────────────────────────────────────────────

        private static List<(string Type, byte[] Box)> ParseChildren(byte[] data, int start, int end)
        {
            var list = new List<(string, byte[])>();
            int p = start;
            while (p + 8 <= end)
            {
                int sz = ReadBE32(data, p);
                if (sz < 8 || p + sz > end) break;
                string type = BoxType(data, p + 4);
                byte[] box = new byte[sz];
                Array.Copy(data, p, box, 0, sz);
                list.Add((type, box));
                p += sz;
            }
            return list;
        }

        private static byte[] BuildContainer(string type, List<byte[]> children)
        {
            int total = 8;
            foreach (var c in children) total += c.Length;
            byte[] box = new byte[total];
            WriteBE32(box, 0, total);
            WriteType(box, 4, type);
            int p = 8;
            foreach (var c in children) { Array.Copy(c, 0, box, p, c.Length); p += c.Length; }
            return box;
        }

        private static byte[] BuildBox(string type, byte[] payload)
        {
            byte[] box = new byte[8 + payload.Length];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, type);
            Array.Copy(payload, 0, box, 8, payload.Length);
            return box;
        }

        private static string BoxType(byte[] data, int off)
            => Encoding.ASCII.GetString(data, off, 4);

        private static int FindAtom(byte[] data, int start, int end, string type)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                int size = ReadBE32(data, pos);
                if (size < 8 || pos + size > end) break;
                if (IsType(data, pos, type)) return pos;
                if (IsContainer(data, pos))
                {
                    int found = FindAtom(data, pos + 8, pos + size, type);
                    if (found >= 0) return found;
                }
                pos += size;
            }
            return -1;
        }

        private static bool IsContainer(byte[] data, int off)
        {
            byte a = data[off + 4], b = data[off + 5], c = data[off + 6], d = data[off + 7];
            return (a == (byte)'m' && b == (byte)'o' && c == (byte)'o' && d == (byte)'v')
                || (a == (byte)'t' && b == (byte)'r' && c == (byte)'a' && d == (byte)'k')
                || (a == (byte)'m' && b == (byte)'d' && c == (byte)'i' && d == (byte)'a')
                || (a == (byte)'m' && b == (byte)'i' && c == (byte)'n' && d == (byte)'f')
                || (a == (byte)'s' && b == (byte)'t' && c == (byte)'b' && d == (byte)'l')
                || (a == (byte)'e' && b == (byte)'d' && c == (byte)'t' && d == (byte)'s')
                || (a == (byte)'d' && b == (byte)'i' && c == (byte)'n' && d == (byte)'f')
                || (a == (byte)'u' && b == (byte)'d' && c == (byte)'t' && d == (byte)'a')
                || (a == (byte)'m' && b == (byte)'e' && c == (byte)'t' && d == (byte)'a');
        }

        private static bool IsType(byte[] data, int off, string type)
            => data[off + 4] == type[0] && data[off + 5] == type[1]
            && data[off + 6] == type[2] && data[off + 7] == type[3];

        private static int ReadBE32(byte[] d, int off)
            => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(off));

        private static void WriteBE32(byte[] d, int off, int v)
            => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(off), v);

        private static void WriteBE16(byte[] d, int off, int v)
            => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(off), (ushort)v);

        private static void WriteType(byte[] d, int off, string type)
        {
            d[off] = (byte)type[0];
            d[off + 1] = (byte)type[1];
            d[off + 2] = (byte)type[2];
            d[off + 3] = (byte)type[3];
        }
    }
}
