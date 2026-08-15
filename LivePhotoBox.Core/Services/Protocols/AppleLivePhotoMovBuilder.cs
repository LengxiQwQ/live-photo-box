using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    /// <summary>
    /// AppleLivePhotoMovBuilder — 以「能导入 iPhone 的最小 4 轨样本」IMG_6675.MOV 为字节模板，
    /// 整体重建 Apple Live Photo MOV。与旧的「在 ffmpeg MOV 上打补丁」不同：
    /// 本类只复用 ffmpeg 输出的视频/音频编码数据（hvcC、esds、样本数据），
    /// 容器结构（ftyp + moov(video/audio/ContentDescribes/mebx + meta) + wide + 单 mdat）
    /// 逐字节对齐模板，使 iOS 按真实 iPhone 样本的结构解析。
    /// </summary>
    public static class AppleLivePhotoMovBuilder
    {
        private const long AppleEpochOffsetSeconds = 2082844800;
        private const int MovieTimeScale = 44100;
        private const int VideoTimeScale = 600;
        private const int ContentTimeScale = 60000;
        private const int ContentSampleDelta = 1000;
        private const int CoverTimeScale = 600;
        private const int CoverSampleDelta = 1;

        // 模板常量（全部来自 IMG_6675.MOV 的逐字节提取，十六进制）
        private const string FtypHex =
            "0000001466747970717420200000000071742020";


        private const string MvhdHex =
            "0000006c6d76686400000000e1a4c0dfe1a4c0df0000ac440000b40500010000" +
            "0100000000000000000000000001000000000000000000000000000000010000" +
            "0000000000000000000000004000000000000000000000000000000000000000" +
            "000000000000000000000005";


        private const string VTkhdHex =
            "0000005c746b68640000000fe1a4c0dfe1a4c0df00000001000000000000ac44" +
            "0000000000000000000000000000000000010000000000000000000000000000" +
            "00010000000000000000000000000000400000000438000007800000";


        private const string VTaptHex =
            "000000447461707400000014636c656600000000043800000780000000000014" +
            "70726f6600000000043800000780000000000014656e6f660000000004380000" +
            "07800000";


        private const string VElstHex =
            "0000001c656c737400000000000000010000ac440000000000010000";


        private const string VMdhdHex =
            "000000206d64686400000000e1a4c0dfe1a4c0df000002580000025855c40000";


        private const string VMdiaHdlrHex =
            "0000003168646c72000000006d686c72766964656170706c0000000000000000" +
            "10436f7265204d6564696120566964656f";


        private const string VmhdHex =
            "00000014766d6864000000010040800080008000";


        private const string VMinfHdlrHex =
            "0000003868646c720000000064686c72616c69736170706c0000000000000000" +
            "17436f7265204d6564696120446174612048616e646c6572";


        private const string DrefHex =
            "0000001c6472656600000000000000010000000c616c697300000001";


        private const string VSgpdHex =
            "0000001a736770640100000073796e6300000001000000021415";


        private const string VSbgpHex =
            "00000034736267700000000073796e630000000400000001000000010000001c" +
            "0000000000000001000000020000001e00000000";

        private const string VStsdFixedHex =
            "000000dd68766331000000000000000100000000000000000000020000000200" +
            "0438078000480000004800000000000000010448455643000000000000000000" +
            "0000000000000000000000000000000000000018ffff";


        private const string ColrHex =
            "00000012636f6c726e636c63000100010001";


        private const string ATkhdHex =
            "0000005c746b68640000000fe1a4c0dfe1a4c0df00000002000000000000b405" +
            "0000000000000000000000000100000000010000000000000000000000000000" +
            "00010000000000000000000000000000400000000000000000000000";


        private const string AElstHex =
            "0000001c656c737400000000000000010000b4050000084000010000";


        private const string AMdhdHex =
            "000000206d64686400000000e1a4c0dfe1a4c0df0000ac440000c00055c40000";


        private const string AMdiaHdlrHex =
            "0000003168646c72000000006d686c72736f756e6170706c0000000000000000" +
            "10436f7265204d6564696120417564696f";


        private const string SmhdHex =
            "00000010736d68640000000000000000";


        private const string ASgpdHex =
            "0000001a7367706401000000726f6c6c0000000200000001ffff";


        private const string ASbgpHex =
            "0000001c7362677000000000726f6c6c000000010000003000000001";

        private const string AStsdTemplateHex =
            "0000008f6d7034610000000000000001000100000000000000020010fffe0000" +
            "ac440000000004000000000100000002000000020000005b776176650000000c" +
            "66726d616d7034610000000c6d70346100000000000000336573647300000000" +
            "0380808022000000048080801440140018000001f4000001f400058080800212" +
            "100680808001020000000800000000";


        private const string T3Hex =
            "000004137472616b0000005c746b68640000000fe1a4c0dfe1a4c0df00000003" +
            "000000000000ac44000000000000000000000000000000000001000000000000" +
            "0000000000000000000100000000000000000000000000004000000000000000" +
            "00000000000000306564747300000028656c737400000000000000020000089d" +
            "ffffffff000100000000a3a700000000000100000000037f6d64696100000020" +
            "6d64686400000000e1a4c0dfe1a4c0df0000ea600000dea855c4000000000034" +
            "68646c72000000006d686c726d6574616170706c000000010000000013436f72" +
            "65204d65646961204d65746164617461000003236d696e6600000020676d6864" +
            "00000018676d696e000000000040800080008000000000000000003868646c72" +
            "0000000064686c72616c69736170706c000000000000000017436f7265204d65" +
            "64696120446174612048616e646c65720000002464696e660000001c64726566" +
            "00000000000000010000000c616c6973000000010000029f7374626c0000022b" +
            "7374736400000000000000010000021b6d65627800000000000000010000020b" +
            "6b65797300000203000000010000002f6b6579646d647461636f6d2e6170706c" +
            "652e717569636b74696d652e6c6976652d70686f746f2d696e666f0000004364" +
            "74797000000001636f6d2e6170706c652e717569636b74696d652e636f6d2e61" +
            "70706c652e717569636b74696d652e6c6976652d70686f746f2d696e666f0000" +
            "017173657475000001596366677662706c6973743030d301020304050c5f1021" +
            "4c69766550686f746f4d6574616461746153657475704461746156657273696f" +
            "6e5d53797374656d56657273696f6e5f10114672616d65776f726b5665727369" +
            "6f6e731001d3060708090a0b5f101350726f647563744275696c645665727369" +
            "6f6e5b50726f647563744e616d655e50726f6475637456657273696f6e583231" +
            "413532373768596950686f6e65204f535431372e30d40d0e0f10111213145a43" +
            "6f72654d6f74696f6e5d434d43617074757265436f72655e4831304953505365" +
            "72766963657359436f72654d6564696158323836382e302e32573434362e352e" +
            "335432302e325e333034352e36392e322e31312e340008000f00330041005500" +
            "57005e00740080008f009800a200a700b000bb00c900d800e200eb00f300f800" +
            "0000000000020100000000000000150000000000000000000000000000010700" +
            "00001064696d7300000780000005a00000001863747073000000106474797000" +
            "000000000000000000001873747473000000000000000100000039000003e800" +
            "000028737473630000000000000002000000010000001e000000010000000200" +
            "00001b00000001000000147374737a0000000000000090000000390000001873" +
            "74636f00000000000000020003d8ff000487f6";


        private const string T4Hex =
            "000002a07472616b0000005c746b68640000000fe1a4c0dfe1a4c0df00000004" +
            "000000000000566c000000000000000000000000000000000001000000000000" +
            "0000000000000000000100000000000000000000000000004000000000000000" +
            "00000000000000306564747300000028656c7374000000000000000200005622" +
            "ffffffff000100000000004a00000000000100000000020c6d64696100000020" +
            "6d64686400000000e1a4c0dfe1a4c0df000002580000000155c4000000000034" +
            "68646c72000000006d686c726d6574616170706c000000010000000013436f72" +
            "65204d65646961204d65746164617461000001b06d696e6600000020676d6864" +
            "00000018676d696e000000000040800080008000000000000000003868646c72" +
            "0000000064686c72616c69736170706c000000000000000017436f7265204d65" +
            "64696120446174612048616e646c65720000002464696e660000001c64726566" +
            "00000000000000010000000c616c6973000000010000012c7374626c000000c8" +
            "737473640000000000000001000000b86d6562780000000000000001000000a8" +
            "6b6579730000004800000001000000306b6579646d647461636f6d2e6170706c" +
            "652e717569636b74696d652e7374696c6c2d696d6167652d74696d6500000010" +
            "6474797000000000000000410000005800000002000000406b6579646d647461" +
            "636f6d2e6170706c652e717569636b74696d652e6c6976652d70686f746f2d73" +
            "74696c6c2d696d6167652d7472616e73666f726d000000106474797000000000" +
            "000000530000001873747473000000000000000100000001000000010000001c" +
            "737473630000000000000001000000010000000100000001000000147374737a" +
            "000000000000005900000001000000147374636f00000000000000010003e9df";


        private const string MetaHdlrHex =
            "0000002268646c7200000000000000006d647461000000000000000000000000" +
            "0000";


        private const string MetaKeysHex =
            "000000cb6b65797300000000000000050000002e6d647461636f6d2e6170706c" +
            "652e717569636b74696d652e636f6e74656e742e6964656e7469666965720000" +
            "00206d647461636f6d2e6170706c652e717569636b74696d652e6d616b650000" +
            "00216d647461636f6d2e6170706c652e717569636b74696d652e6d6f64656c00" +
            "0000246d647461636f6d2e6170706c652e717569636b74696d652e736f667477" +
            "617265000000286d647461636f6d2e6170706c652e717569636b74696d652e63" +
            "72656174696f6e64617465";


        private const string ContentSampleHex =
            "000000900000000103000000bdc36d3ce3b5eb6d800000007b80ad425a2d6441" +
            "0a08cb3e7feea6bd79e9f63f000080400400ff00000000000000000000000000" +
            "000000000000000007000000525e873ee66e52bf1b2a6ac4d37862bf761ed23d" +
            "de3f8ec313f52f39b2f04439ff309dbf1a17f1ed1b070000206796ed1b070000" +
            "00000000000000000000000000000000";


        private const string CoverSampleHex =
            "0000000900000001ff00000050000000023ff000000000000000000000000000" +
            "00000000000000000000000000000000003ff000000000000000000000000000" +
            "00000000000000000000000000000000003ff0000000000000";


        private static readonly byte[] Ftyp = FromHex(FtypHex);
        private static readonly byte[] MvhdTemplate = FromHex(MvhdHex);
        private static readonly byte[] VTkhdTemplate = FromHex(VTkhdHex);
        private static readonly byte[] VTaptTemplate = FromHex(VTaptHex);
        private static readonly byte[] VElstTemplate = FromHex(VElstHex);
        private static readonly byte[] VMdhdTemplate = FromHex(VMdhdHex);
        private static readonly byte[] VMdiaHdlr = FromHex(VMdiaHdlrHex);
        private static readonly byte[] Vmhd = FromHex(VmhdHex);
        private static readonly byte[] VMinfHdlr = FromHex(VMinfHdlrHex);
        private static readonly byte[] Dref = FromHex(DrefHex);
        private static readonly byte[] VSgpd = FromHex(VSgpdHex);
        private static readonly byte[] VSbgp = FromHex(VSbgpHex);
        private static readonly byte[] VStsdFixed = FromHex(VStsdFixedHex);
        private static readonly byte[] Colr = FromHex(ColrHex);
        private static readonly byte[] ATkhdTemplate = FromHex(ATkhdHex);
        private static readonly byte[] AElstTemplate = FromHex(AElstHex);
        private static readonly byte[] AMdhdTemplate = FromHex(AMdhdHex);
        private static readonly byte[] AMdiaHdlr = FromHex(AMdiaHdlrHex);
        private static readonly byte[] Smhd = FromHex(SmhdHex);
        private static readonly byte[] ASgpd = FromHex(ASgpdHex);
        private static readonly byte[] ASbgp = FromHex(ASbgpHex);
        private static readonly byte[] AStsdTemplate = FromHex(AStsdTemplateHex);
        private static readonly byte[] T3Template = FromHex(T3Hex);
        private static readonly byte[] T4Template = FromHex(T4Hex);
        private static readonly byte[] MetaHdlr = FromHex(MetaHdlrHex);
        private static readonly byte[] MetaKeys = FromHex(MetaKeysHex);
        private static readonly byte[] ContentSample = FromHex(ContentSampleHex);
        private static readonly byte[] CoverSample = FromHex(CoverSampleHex);

        private static readonly byte[] Wide = { 0, 0, 0, 8, (byte)'w', (byte)'i', (byte)'d', (byte)'e' };

        /// <summary>
        /// 用 Apple 模板重建 MOV 并原子替换原文件。成功返回 true。
        /// </summary>
        public static bool TryRebuild(
            string movPath, string contentId, string model, double coverSeconds, out string? error)
        {
            error = null;
            try
            {
                byte[] source = File.ReadAllBytes(movPath);
                byte[]? built = Build(source, contentId, model, coverSeconds, out error);
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
            byte[] source, string contentId, string model, double coverSeconds, out string? error)
        {
            error = null;
            try
            {
                int moov = FindBox(source, 0, source.Length, "moov");
                if (moov < 0) { error = "source moov not found"; return null; }
                int moovEnd = moov + ReadBE32(source, moov);

                VideoData? video = null;
                AudioData? audio = null;
                var children = ParseChildren(source, moov + 8, moovEnd);
                foreach (var (type, box) in children)
                {
                    if (type != "trak") continue;
                    string entryType = GetSampleEntryType(box);
                    if (entryType is "hvc1" or "avc1")
                    {
                        if (video == null)
                            video = ParseVideoTrak(source, box);
                    }
                    else if (entryType == "mp4a")
                    {
                        if (audio == null)
                            audio = ParseAudioTrak(source, box);
                    }
                }

                if (video == null) { error = "source has no video track"; return null; }
                if (video.SampleSizes.Count == 0) { error = "source video track has no samples"; return null; }
                if (video.StsdType != "hvc1") { error = $"Apple output requires hvc1, got {video.StsdType}"; return null; }

                // ── 计算 ─────────────────────────────────────────────
                int frameCount = video.SampleSizes.Count;
                double origFps = (double)video.Timescale / Math.Max(1, video.AvgDelta);
                int videoDelta = Math.Max(1, (int)Math.Round(VideoTimeScale / origFps));
                int videoMediaDur = frameCount * videoDelta;
                double videoSeconds = (double)videoMediaDur / VideoTimeScale;
                int movieDur = (int)Math.Round(videoSeconds * MovieTimeScale);
                if (movieDur < 1) movieDur = 1;
                int appleTime = unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + AppleEpochOffsetSeconds));
                DateTimeOffset now = DateTimeOffset.Now;
                string creationDate = now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                    + now.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");

                // ctts：以模板风格归一化（dts 从 0 起，允许负偏移）
                int[] cttsNew = ConvertCtts(video, frameCount, videoDelta);

                int[]? syncSamples = video.SyncSamples.Count > 0
                    ? video.SyncSamples.ToArray()
                    : new[] { 1 };

                int contentCount = Math.Max(1, (int)Math.Round((videoSeconds - 0.05) * 60.0));
                int contentMediaDur = contentCount * ContentSampleDelta;
                int contentMovieTicks = (int)Math.Round(contentMediaDur * (double)MovieTimeScale / ContentTimeScale);
                int contentLeadIn = (int)Math.Round(0.05 * MovieTimeScale);
                int contentTkhdDur = contentLeadIn + contentMovieTicks;

                int coverTicks = coverSeconds > 0 ? (int)Math.Round(coverSeconds * MovieTimeScale) : 0;
                int coverMovieDelta = (int)Math.Round(MovieTimeScale / (double)CoverTimeScale); // 74
                int coverTkhdDur = coverTicks + coverMovieDelta;

                int audioBytes = audio?.SampleDataTotal ?? 0;
                int videoBytes = video.SampleDataTotal;
                int contentBytes = contentCount * ContentSample.Length;

                // ── 组装（先以 stco=0 占位算 moov 尺寸）──────────────
                List<byte[]> moovPayload0 = BuildMoov(
                    video, audio, movieDur, appleTime, contentCount, contentTkhdDur, contentMovieTicks,
                    contentLeadIn, coverTicks, coverMovieDelta, coverTkhdDur,
                    cttsNew, syncSamples, contentId, model, creationDate,
                    videoOffset: 0, audioOffset: 0, contentOffset: 0, coverOffset: 0);
                byte[] moov0 = BuildContainer("moov", moovPayload0);
                int dataStart = Ftyp.Length + moov0.Length + Wide.Length + 8;

                int videoOffset = dataStart;
                int audioOffset = videoOffset + videoBytes;
                int contentOffset = audioOffset + audioBytes;
                int coverOffset = contentOffset + contentBytes;

                List<byte[]> moovPayload = BuildMoov(
                    video, audio, movieDur, appleTime, contentCount, contentTkhdDur, contentMovieTicks,
                    contentLeadIn, coverTicks, coverMovieDelta, coverTkhdDur,
                    cttsNew, syncSamples, contentId, model, creationDate,
                    videoOffset, audioOffset, contentOffset, coverOffset);
                byte[] moovBox = BuildContainer("moov", moovPayload);

                int mdatSize = 8 + videoBytes + audioBytes + contentBytes + CoverSample.Length;
                byte[] file = new byte[Ftyp.Length + moovBox.Length + Wide.Length + mdatSize];
                int p = 0;
                Array.Copy(Ftyp, 0, file, p, Ftyp.Length); p += Ftyp.Length;
                Array.Copy(moovBox, 0, file, p, moovBox.Length); p += moovBox.Length;
                Array.Copy(Wide, 0, file, p, Wide.Length); p += Wide.Length;

                WriteBE32(file, p, mdatSize);
                WriteType(file, p + 4, "mdat");
                p += 8;
                foreach (var sample in video.Samples)
                {
                    Array.Copy(sample, 0, file, p, sample.Length);
                    p += sample.Length;
                }
                if (audio != null)
                {
                    foreach (var sample in audio.Samples)
                    {
                        Array.Copy(sample, 0, file, p, sample.Length);
                        p += sample.Length;
                    }
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

        // ── moov 组装 ──────────────────────────────────────────────

        private static List<byte[]> BuildMoov(
            VideoData video, AudioData? audio, int movieDur, int appleTime,
            int contentCount, int contentTkhdDur, int contentMovieTicks, int contentLeadIn,
            int coverTicks, int coverMovieDelta, int coverTkhdDur,
            int[] cttsNew, int[] syncSamples, string contentId, string model, string creationDate,
            int videoOffset, int audioOffset, int contentOffset, int coverOffset)
        {
            byte[] mvhd = (byte[])MvhdTemplate.Clone();
            WriteBE32(mvhd, 12, appleTime);
            WriteBE32(mvhd, 16, appleTime);
            WriteBE32(mvhd, 20, MovieTimeScale);
            WriteBE32(mvhd, 24, movieDur);
            WriteBE32(mvhd, 104, 5); // next track id

            var list = new List<byte[]>
            {
                mvhd,
                BuildVideoTrak(video, movieDur, appleTime, cttsNew, syncSamples, videoOffset),
            };
            if (audio != null)
                list.Add(BuildAudioTrak(audio, movieDur, appleTime, audioOffset));
            list.Add(BuildContentTrak(contentCount, contentTkhdDur, contentMovieTicks, contentLeadIn, appleTime, contentOffset));
            list.Add(BuildCoverTrak(coverTicks, coverMovieDelta, coverTkhdDur, appleTime, coverOffset));
            list.Add(BuildMeta(contentId, model, creationDate));
            return list;
        }

        // ── Video trak ─────────────────────────────────────────────

        private static byte[] BuildVideoTrak(
            VideoData video, int movieDur, int appleTime, int[] cttsNew, int[] syncSamples, int videoOffset)
        {
            int frameCount = video.SampleSizes.Count;
            int mediaDur = frameCount * video.VideoDelta;
            int w = video.Width;
            int h = video.Height;

            byte[] tkhd = (byte[])VTkhdTemplate.Clone();
            WriteBE32(tkhd, 12, appleTime);
            WriteBE32(tkhd, 16, appleTime);
            WriteBE32(tkhd, 20, 1);                    // track id
            WriteBE32(tkhd, 28, movieDur);
            WriteBE32(tkhd, 84, w << 16);
            WriteBE32(tkhd, 88, h << 16);

            byte[] tapt = (byte[])VTaptTemplate.Clone();
            WriteBE32(tapt, 12 + 8, w << 16); // clef.w
            WriteBE32(tapt, 12 + 12, h << 16);
            WriteBE32(tapt, 32 + 8, w << 16); // prof.w
            WriteBE32(tapt, 32 + 12, h << 16);
            WriteBE32(tapt, 52 + 8, w << 16); // enof.w
            WriteBE32(tapt, 52 + 12, h << 16);

            byte[] elst = (byte[])VElstTemplate.Clone();
            WriteBE32(elst, 16, movieDur);
            WriteBE32(elst, 20, 0);

            byte[] mdhd = (byte[])VMdhdTemplate.Clone();
            WriteBE32(mdhd, 12, appleTime);
            WriteBE32(mdhd, 16, appleTime);
            WriteBE32(mdhd, 20, VideoTimeScale);
            WriteBE32(mdhd, 24, mediaDur);

            byte[] stsd = BuildVideoStsd(video);
            byte[] sgpd = (byte[])VSgpd.Clone();
            byte[] sbgp = BuildSyncSbgp(frameCount, syncSamples);
            byte[] stts = BuildStts(frameCount, video.VideoDelta);
            byte[] ctts = BuildCtts(cttsNew);
            byte[] cslg = BuildCslg(cttsNew, mediaDur);
            byte[] stss = BuildStss(syncSamples);
            byte[] sdtp = BuildSdtp(frameCount, syncSamples);
            byte[] stsc = BuildStsc(frameCount);
            byte[] stsz = BuildStsz(video.SampleSizes);
            byte[] stco = BuildStco(videoOffset);

            byte[] stbl = BuildContainer("stbl", new List<byte[]>
                { stsd, sgpd, sbgp, stts, ctts, cslg, stss, sdtp, stsc, stsz, stco });

            byte[] dinf = BuildContainer("dinf", new List<byte[]> { (byte[])Dref.Clone() });
            byte[] minf = BuildContainer("minf", new List<byte[]>
                { (byte[])Vmhd.Clone(), (byte[])VMinfHdlr.Clone(), dinf, stbl });
            byte[] mdia = BuildContainer("mdia", new List<byte[]>
                { mdhd, (byte[])VMdiaHdlr.Clone(), minf });
            byte[] edts = BuildContainer("edts", new List<byte[]> { elst });
            return BuildContainer("trak", new List<byte[]> { tkhd, tapt, edts, mdia });
        }

        private static byte[] BuildVideoStsd(VideoData video)
        {
            byte[] entry = new byte[VStsdFixed.Length + video.HvcC!.Length + Colr.Length + 4];
            Array.Copy(VStsdFixed, 0, entry, 0, VStsdFixed.Length);
            Array.Copy(video.HvcC, 0, entry, VStsdFixed.Length, video.HvcC.Length);
            Array.Copy(Colr, 0, entry, VStsdFixed.Length + video.HvcC.Length, Colr.Length);
            // 采样条目 width/height 必须写实际流的显示尺寸（模板固定部是 IMG_6675 的 1080x1920，
            // 直接沿用会导致 iOS 按 1920 高分配画布 → 视频上移、底部绿）。偏移：entry 内
            // 8B 头 + 8B reserved/dref + 16B pre_defined/reserved 之后（即 entry[32]/[34]）。
            WriteBE16(entry, 32, (ushort)Math.Max(1, video.Width));
            WriteBE16(entry, 34, (ushort)Math.Max(1, video.Height));
            // 尾部 4 字节 0（与模板一致）
            WriteBE32(entry, 0, entry.Length); // 修正 entry size（模板固定部含 221，实际随 hvcC 变化）
            byte[] payload = new byte[8 + entry.Length];
            WriteBE32(payload, 0, 0);
            WriteBE32(payload, 4, 1);
            Array.Copy(entry, 0, payload, 8, entry.Length);
            return BuildBox("stsd", payload);
        }

        // ── Audio trak ─────────────────────────────────────────────

        private static byte[] BuildAudioTrak(AudioData audio, int movieDur, int appleTime, int audioOffset)
        {
            int frameCount = audio.SampleSizes.Count;
            int mediaDur = frameCount * audio.AvgDelta;

            byte[] tkhd = (byte[])ATkhdTemplate.Clone();
            WriteBE32(tkhd, 12, appleTime);
            WriteBE32(tkhd, 16, appleTime);
            WriteBE32(tkhd, 20, 2);
            WriteBE32(tkhd, 28, mediaDur);

            byte[] elst = (byte[])AElstTemplate.Clone();
            WriteBE32(elst, 16, mediaDur);
            WriteBE32(elst, 20, audio.Priming); // AAC priming（模板 2112；源有声明则用源值，无则 0）

            byte[] mdhd = (byte[])AMdhdTemplate.Clone();
            WriteBE32(mdhd, 12, appleTime);
            WriteBE32(mdhd, 16, appleTime);
            WriteBE32(mdhd, 20, audio.SampleRate);
            WriteBE32(mdhd, 24, mediaDur);

            byte[] stsd = BuildAudioStsd(audio);
            byte[] sgpd = (byte[])ASgpd.Clone();
            byte[] sbgp = (byte[])ASbgp.Clone();
            WriteBE32(sbgp, 20, frameCount); // sample_count
            byte[] stts = BuildStts(frameCount, audio.AvgDelta);
            byte[] stsc = BuildStsc(frameCount);
            byte[] stsz = BuildStsz(audio.SampleSizes);
            byte[] stco = BuildStco(audioOffset);

            byte[] stbl = BuildContainer("stbl", new List<byte[]>
                { stsd, sgpd, sbgp, stts, stsc, stsz, stco });
            byte[] dinf = BuildContainer("dinf", new List<byte[]> { (byte[])Dref.Clone() });
            byte[] minf = BuildContainer("minf", new List<byte[]>
                { (byte[])Smhd.Clone(), (byte[])VMinfHdlr.Clone(), dinf, stbl });
            byte[] mdia = BuildContainer("mdia", new List<byte[]>
                { mdhd, (byte[])AMdiaHdlr.Clone(), minf });
            byte[] edts = BuildContainer("edts", new List<byte[]> { elst });
            return BuildContainer("trak", new List<byte[]> { tkhd, edts, mdia });
        }

        private static byte[] BuildAudioStsd(AudioData audio)
        {
            byte[] entry = (byte[])AStsdTemplate.Clone();
            // channels
            WriteBE16(entry, 24, (ushort)audio.Channels);
            // samplerate（16.16）
            WriteBE32(entry, 32, audio.SampleRate << 16);
            // esds 描述符内字段（entry+100 起）
            int desc = 100;
            entry[desc + 14] = 0x14; // streamType（Apple 风格，非 ffmpeg 的 0x15）
            WriteBE24(entry, desc + 15, audio.BufferSize);
            WriteBE32(entry, desc + 18, audio.MaxBitrate);
            WriteBE32(entry, desc + 22, audio.AvgBitrate);
            // AudioSpecificConfig：44100 固定 0x12，声道数
            entry[desc + 32] = 0x12;
            entry[desc + 33] = (byte)(Math.Clamp(audio.Channels, 1, 7) << 3);
            return BuildBox("stsd", BuildStsdPayload(entry));
        }

        // ── ContentDescribes / cover trak ──────────────────────────

        private static byte[] BuildContentTrak(
            int sampleCount, int tkhdDur, int movieTicks, int leadIn, int appleTime, int dataOffset)
        {
            byte[] trak = (byte[])T3Template.Clone();
            int tkhd = FindBox(trak, 0, trak.Length, "tkhd");
            int elst = FindBox(trak, 0, trak.Length, "elst");
            int mdhd = FindBox(trak, 0, trak.Length, "mdhd");
            WriteBE32(trak, tkhd + 12, appleTime);
            WriteBE32(trak, tkhd + 16, appleTime);
            WriteBE32(trak, tkhd + 20, 3);
            WriteBE32(trak, tkhd + 28, tkhdDur);
            WriteBE32(trak, elst + 16, leadIn);
            WriteBE32(trak, elst + 20, -1);
            WriteBE32(trak, elst + 28, movieTicks);
            WriteBE32(trak, elst + 32, 0);
            WriteBE32(trak, mdhd + 12, appleTime);
            WriteBE32(trak, mdhd + 16, appleTime);
            WriteBE32(trak, mdhd + 24, sampleCount * ContentSampleDelta);

            byte[] stts = BuildStts(sampleCount, ContentSampleDelta);
            byte[] stsc = BuildStsc(sampleCount);
            byte[] stsz = BuildStszUniform(ContentSample.Length, sampleCount);
            byte[] stco = BuildStco(dataOffset);
            byte[] stbl = ReplaceSampleTables(trak, stts, stsc, stsz, stco);
            return ReplaceChild(trak, "stbl", stbl);
        }

        private static byte[] BuildCoverTrak(
            int coverTicks, int coverMovieDelta, int tkhdDur, int appleTime, int dataOffset)
        {
            byte[] trak = (byte[])T4Template.Clone();
            int tkhd = FindBox(trak, 0, trak.Length, "tkhd");
            int elst = FindBox(trak, 0, trak.Length, "elst");
            int mdhd = FindBox(trak, 0, trak.Length, "mdhd");
            WriteBE32(trak, tkhd + 12, appleTime);
            WriteBE32(trak, tkhd + 16, appleTime);
            WriteBE32(trak, tkhd + 20, 4);
            WriteBE32(trak, tkhd + 28, tkhdDur);
            WriteBE32(trak, elst + 16, coverTicks);
            WriteBE32(trak, elst + 20, -1);
            WriteBE32(trak, elst + 28, coverMovieDelta);
            WriteBE32(trak, elst + 32, 0);
            WriteBE32(trak, mdhd + 12, appleTime);
            WriteBE32(trak, mdhd + 16, appleTime);
            WriteBE32(trak, mdhd + 20, CoverTimeScale);
            WriteBE32(trak, mdhd + 24, 1);

            byte[] stco = BuildStco(dataOffset);
            return ReplaceChild(trak, "stco", stco);
        }

        // ── meta ───────────────────────────────────────────────────

        private static byte[] BuildMeta(string contentId, string model, string creationDate)
        {
            var ilst = BuildContainer("ilst", new List<byte[]>
            {
                BuildMetaEntry(1, contentId),
                BuildMetaEntry(2, "Apple"),
                BuildMetaEntry(3, model),
                BuildMetaEntry(4, "17.0.2"),
                BuildMetaEntry(5, creationDate),
            });
            return BuildContainer("meta", new List<byte[]>
                { (byte[])MetaHdlr.Clone(), (byte[])MetaKeys.Clone(), ilst });
        }

        private static byte[] BuildMetaEntry(int index, string value)
        {
            byte[] valueBytes = Encoding.ASCII.GetBytes(value);
            byte[] data = new byte[8 + 8 + valueBytes.Length];
            WriteBE32(data, 0, data.Length);
            WriteType(data, 4, "data");
            WriteBE32(data, 8, 1); // version/flags（type=1 UTF-8）
            WriteBE32(data, 12, 0); // locale
            Array.Copy(valueBytes, 0, data, 16, valueBytes.Length);
            byte[] entry = new byte[8 + data.Length];
            WriteBE32(entry, 0, entry.Length);
            entry[4] = 0; entry[5] = 0; entry[6] = 0; entry[7] = (byte)index;
            Array.Copy(data, 0, entry, 8, data.Length);
            return entry;
        }

        // ── 采样表构建 ─────────────────────────────────────────────

        private static byte[] BuildStts(int sampleCount, int delta)
        {
            byte[] box = new byte[24];
            WriteBE32(box, 0, 24);
            WriteType(box, 4, "stts");
            WriteBE32(box, 12, 1);
            WriteBE32(box, 16, sampleCount);
            WriteBE32(box, 20, delta);
            return box;
        }

        private static byte[] BuildCtts(int[] offsets)
        {
            int n = offsets.Length;
            byte[] box = new byte[16 + n * 8];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, "ctts");
            WriteBE32(box, 12, n);
            for (int i = 0; i < n; i++)
            {
                WriteBE32(box, 16 + i * 8, 1);
                WriteBE32(box, 20 + i * 8, offsets[i]);
            }
            return box;
        }

        private static byte[] BuildCslg(int[] offsets, int mediaDur)
        {
            int min = offsets.Length > 0 ? offsets.Min() : 0;
            int max = offsets.Length > 0 ? offsets.Max() : 0;
            byte[] box = new byte[32];
            WriteBE32(box, 0, 32);
            WriteType(box, 4, "cslg");
            WriteBE32(box, 12, -min); // compositionToDTSShift（模板公式）
            WriteBE32(box, 16, min);  // leastDecodeToDisplayDelta
            WriteBE32(box, 20, max);  // greatestDecodeToDisplayDelta
            WriteBE32(box, 24, 0);    // compositionStartTime
            WriteBE32(box, 28, mediaDur); // compositionEndTime（模板=媒体时长）
            return box;
        }

        private static byte[] BuildStss(int[] syncSamples)
        {
            byte[] box = new byte[16 + syncSamples.Length * 4];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, "stss");
            WriteBE32(box, 12, syncSamples.Length);
            for (int i = 0; i < syncSamples.Length; i++)
                WriteBE32(box, 16 + i * 4, syncSamples[i]);
            return box;
        }

        private static byte[] BuildSdtp(int sampleCount, int[] syncSamples)
        {
            var syncSet = new HashSet<int>(syncSamples);
            // 模板风格：无 entry_count，version/flags 后直接是每样本字节
            byte[] box = new byte[12 + sampleCount];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, "sdtp");
            for (int i = 0; i < sampleCount; i++)
                box[12 + i] = syncSet.Contains(i + 1) ? (byte)0x20 : (byte)0x18;
            return box;
        }

        private static byte[] BuildSyncSbgp(int sampleCount, int[] syncSamples)
        {
            // 与模板相同的规律：sync 样本各占一组（首个=1，其余=2），其余为组 0
            var entries = new List<(int Count, int Group)>();
            if (syncSamples.Length == 0)
            {
                entries.Add((sampleCount, 0));
            }
            else
            {
                entries.Add((1, 1));
                for (int k = 1; k < syncSamples.Length; k++)
                {
                    int gap = syncSamples[k] - syncSamples[k - 1] - 1;
                    if (gap > 0) entries.Add((gap, 0));
                    entries.Add((1, 2));
                }
                int tail = sampleCount - syncSamples[^1];
                if (tail > 0) entries.Add((tail, 0));
            }

            byte[] box = new byte[20 + entries.Count * 8];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, "sbgp");
            WriteBE32(box, 8, 0);
            WriteType(box, 12, "sync");
            WriteBE32(box, 16, entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                WriteBE32(box, 20 + i * 8, entries[i].Count);
                WriteBE32(box, 24 + i * 8, entries[i].Group);
            }
            return box;
        }

        private static byte[] BuildStsc(int sampleCount)
        {
            byte[] box = new byte[28];
            WriteBE32(box, 0, 28);
            WriteType(box, 4, "stsc");
            WriteBE32(box, 12, 1);
            WriteBE32(box, 16, 1);
            WriteBE32(box, 20, sampleCount);
            WriteBE32(box, 24, 1);
            return box;
        }

        private static byte[] BuildStsz(List<int> sizes)
        {
            byte[] box = new byte[20 + sizes.Count * 4];
            WriteBE32(box, 0, box.Length);
            WriteType(box, 4, "stsz");
            WriteBE32(box, 12, 0); // sample_size = 0（逐样本）
            WriteBE32(box, 16, sizes.Count);
            for (int i = 0; i < sizes.Count; i++)
                WriteBE32(box, 20 + i * 4, sizes[i]);
            return box;
        }

        private static byte[] BuildStszUniform(int sampleSize, int count)
        {
            byte[] box = new byte[20];
            WriteBE32(box, 0, 20);
            WriteType(box, 4, "stsz");
            WriteBE32(box, 12, sampleSize);
            WriteBE32(box, 16, count);
            return box;
        }

        private static byte[] BuildStco(int offset)
        {
            byte[] box = new byte[20];
            WriteBE32(box, 0, 20);
            WriteType(box, 4, "stco");
            WriteBE32(box, 12, 1);
            WriteBE32(box, 16, offset);
            return box;
        }

        private static byte[] BuildStsdPayload(byte[] entry)
        {
            byte[] payload = new byte[8 + entry.Length];
            WriteBE32(payload, 0, 0);
            WriteBE32(payload, 4, 1);
            Array.Copy(entry, 0, payload, 8, entry.Length);
            return payload;
        }

        // ── 源 MOV 解析 ────────────────────────────────────────────

        private sealed class VideoData
        {
            public string StsdType = "";
            public byte[]? HvcC;
            public int Width;
            public int Height;
            public int Timescale = 1;
            public int AvgDelta = 1;
            public int VideoDelta = 1;
            public int Priming;
            public List<(int Count, int Delta)> Stts = new();
            public List<int> SampleSizes = new();
            public List<byte[]> Samples = new();
            public List<int> SyncSamples = new();
            public int SampleDataTotal;
            public List<int> CttsFile = new();
        }

        private sealed class AudioData
        {
            public int Channels = 2;
            public int SampleRate = 44100;
            public int AvgDelta = 1024;
            public int Priming;
            public int BufferSize;
            public int MaxBitrate;
            public int AvgBitrate;
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
            int elst = FindBox(trak, 0, trak.Length, "elst");
            if (tkhd >= 0)
            {
                v.Width = ReadBE32(trak, tkhd + 84) >> 16;
                v.Height = ReadBE32(trak, tkhd + 88) >> 16;
            }
            if (mdhd >= 0)
            {
                v.Timescale = ReadBE32(trak, mdhd + 20);
                if (v.Timescale < 1) v.Timescale = 1;
            }
            if (elst >= 0 && trak[elst + 8] == 0)
            {
                int count = ReadBE32(trak, elst + 12);
                if (count > 0) v.Priming = Math.Max(0, ReadBE32(trak, elst + 20));
            }

            int stsd = FindBox(trak, 0, trak.Length, "stsd");
            if (stsd >= 0)
            {
                int entry = stsd + 16;
                int entrySize = ReadBE32(trak, entry);
                v.Width = v.Width > 0 ? v.Width : ReadBE16(trak, entry + 32);
                v.Height = v.Height > 0 ? v.Height : ReadBE16(trak, entry + 34);
                // hvcC 子盒
                int q = entry + 86;
                while (q + 8 <= entry + entrySize)
                {
                    int sz = ReadBE32(trak, q);
                    if (sz < 8 || q + sz > entry + entrySize) break;
                    if (BoxType(trak, q) == "hvcC")
                    {
                        v.HvcC = new byte[sz];
                        Array.Copy(trak, q, v.HvcC, 0, sz);
                        break;
                    }
                    q += sz;
                }
            }

            (v.Stts, v.CttsFile, v.SyncSamples, v.SampleSizes) = ParseSampleTables(trak, out var stsc, out var stco, out _);
            v.AvgDelta = SttsAvgDelta(v.Stts);
            v.VideoDelta = Math.Max(1, (int)Math.Round(VideoTimeScale / (v.Timescale / (double)Math.Max(1, v.AvgDelta))));
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
                a.Channels = ReadBE16(trak, entry + 24);
                int sr = ReadBE32(trak, entry + 32) >> 16;
                if (sr > 0) a.SampleRate = sr;
                ParseEsds(trak, out int buf, out int max, out int avg);
                a.BufferSize = buf;
                a.MaxBitrate = max;
                a.AvgBitrate = avg;
            }
            (var stts, _, _, a.SampleSizes) = ParseSampleTables(trak, out var stsc, out var stco, out _);
            a.AvgDelta = SttsAvgDelta(stts);
            a.Samples = ExtractSamples(source, a.SampleSizes, stsc, stco);
            a.SampleDataTotal = a.SampleSizes.Sum();
            return a;
        }

        private static void ParseEsds(byte[] trak, out int bufferSize, out int maxBitrate, out int avgBitrate)
        {
            bufferSize = 0; maxBitrate = 0; avgBitrate = 0;
            int stsd = FindBox(trak, 0, trak.Length, "stsd");
            if (stsd < 0) return;
            int entry = stsd + 16;
            int entrySize = ReadBE32(trak, entry);
            int q = entry + 56; // 音频 entry 固定 56 字节
            while (q + 8 <= entry + entrySize)
            {
                int sz = ReadBE32(trak, q);
                if (sz < 8 || q + sz > entry + entrySize) break;
                if (BoxType(trak, q) == "wave")
                {
                    int w = q + 8;
                    while (w + 8 < q + sz)
                    {
                        int ws = ReadBE32(trak, w);
                        if (ws < 8 || w + ws > q + sz) break;
                        if (BoxType(trak, w) == "esds")
                        {
                            ReadEsdsValues(trak, w + 12, w + ws, out bufferSize, out maxBitrate, out avgBitrate);
                            return;
                        }
                        w += ws;
                    }
                }
                q += sz;
            }
        }

        private static void ReadEsdsValues(
            byte[] data, int start, int end, out int bufferSize, out int maxBitrate, out int avgBitrate)
        {
            bufferSize = 0; maxBitrate = 0; avgBitrate = 0;
            int p = start;
            while (p < end)
            {
                if (p >= end) break;
                int tag = data[p++];
                int length = 0;
                while (p < end)
                {
                    byte b = data[p++];
                    length = (length << 7) | (b & 0x7f);
                    if ((b & 0x80) == 0) break;
                }
                if (p + length > end) break;
                if (tag == 0x03)
                {
                    // ES_Descriptor：3 字节头后是子描述符
                    int q = p + 3;
                    int subEnd = p + length;
                    while (q < subEnd)
                    {
                        int st = data[q++];
                        int sl = 0;
                        while (q < subEnd)
                        {
                            byte b = data[q++];
                            sl = (sl << 7) | (b & 0x7f);
                            if ((b & 0x80) == 0) break;
                        }
                        if (q + sl > subEnd) break;
                        if (st == 0x04 && sl >= 13)
                        {
                            bufferSize = ReadBE24(data, q + 2);
                            maxBitrate = ReadBE32(data, q + 5);
                            avgBitrate = ReadBE32(data, q + 9);
                            return;
                        }
                        q += sl;
                    }
                }
                p += length;
            }
        }

        private static (List<(int Count, int Delta)> Stts, List<int> Ctts, List<int> Sync, List<int> Sizes)
            ParseSampleTables(byte[] trak, out List<(int First, int PerChunk, int Desc)> stsc, out List<long> stco, out bool hasCo64)
        {
            stsc = new List<(int, int, int)>();
            stco = new List<long>();
            hasCo64 = false;
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
                bool v1 = trak[cttsOff + 8] == 1;
                int entrySize = v1 ? 8 : 8;
                for (int i = 0; i < n; i++)
                {
                    int cnt = ReadBE32(trak, baseOff + i * entrySize);
                    int off = ReadBE32(trak, baseOff + i * entrySize + 4);
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
                hasCo64 = true;
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
            // 展开 chunk -> samples_per_chunk
            var perChunk = new Dictionary<int, int>();
            for (int i = 0; i < stsc.Count; i++)
            {
                int next = i + 1 < stsc.Count ? stsc[i + 1].First : stco.Count + 1;
                for (int c = stsc[i].First; c < next; c++)
                    perChunk[c] = stsc[i].PerChunk;
            }
            int idx = 0;
            for (int chunk = 1; chunk <= stco.Count && idx < sizes.Count; chunk++)
            {
                if (!perChunk.TryGetValue(chunk, out int count)) break;
                long pos = stco[chunk - 1];
                for (int s = 0; s < count && idx < sizes.Count; s++)
                {
                    int size = sizes[idx];
                    if (pos + size > source.Length || size < 0)
                    {
                        result.Add(Array.Empty<byte>());
                        idx++;
                        continue;
                    }
                    byte[] sample = new byte[size];
                    Array.Copy(source, pos, sample, 0, size);
                    result.Add(sample);
                    pos += size;
                    idx++;
                }
            }
            return result;
        }

        private static int[] ConvertCtts(VideoData video, int frameCount, int newDelta)
        {
            var result = new int[frameCount];
            double scale = (double)VideoTimeScale / video.Timescale;
            double dts1 = -video.Priming;
            for (int i = 0; i < frameCount; i++)
            {
                double ptsFile = dts1 + (double)i * video.AvgDelta + (i < video.CttsFile.Count ? video.CttsFile[i] : 0);
                double ptsNew = Math.Round(ptsFile * scale);
                result[i] = (int)ptsNew - i * newDelta;
            }
            return result;
        }

        private static int SttsAvgDelta(List<(int Count, int Delta)> stts)
        {
            long total = 0, count = 0;
            foreach (var (c, d) in stts)
            {
                total += (long)c * d;
                count += c;
            }
            return count > 0 ? (int)Math.Round(total / (double)count) : 1;
        }

        // ── box 工具 ───────────────────────────────────────────────

        private static byte[] ReplaceSampleTables(byte[] trak, byte[] stts, byte[] stsc, byte[] stsz, byte[] stco)
        {
            int stbl = FindBox(trak, 0, trak.Length, "stbl");
            if (stbl < 0) return BuildContainer("stbl", new List<byte[]> { stts, stsc, stsz, stco });
            var children = ParseChildren(trak, stbl + 8, stbl + ReadBE32(trak, stbl));
            var result = new List<byte[]>();
            foreach (var (type, box) in children)
            {
                if (type == "stsd") result.Add(box);
                else if (type == "stts") result.Add(stts);
                else if (type == "stsc") result.Add(stsc);
                else if (type == "stsz") result.Add(stsz);
                else if (type == "stco") result.Add(stco);
                else result.Add(box);
            }
            return BuildContainer("stbl", result);
        }

        private static byte[] ReplaceChild(byte[] container, string type, byte[] replacement)
        {
            var children = ParseChildren(container, 8, container.Length);
            var result = new List<byte[]>();
            bool done = false;
            foreach (var (t, box) in children)
            {
                if (!done && t == type)
                {
                    result.Add(replacement);
                    done = true;
                }
                else if (IsContainerType(t) && !done)
                {
                    result.Add(ReplaceChild(box, type, replacement));
                }
                else
                {
                    result.Add(box);
                }
            }
            return BuildContainer(BoxType(container, 0), result);
        }

        private static bool IsContainerType(string type)
            => type is "moov" or "trak" or "mdia" or "minf" or "stbl"
                or "edts" or "dinf" or "udta" or "meta" or "ilst" or "keys" or "tapt" or "gmhd" or "wave";

        private static List<(string Type, byte[] Box)> ParseChildren(byte[] data, int start, int end)
        {
            var list = new List<(string, byte[])>();
            int p = start;
            while (p + 8 <= end)
            {
                int size = ReadBE32(data, p);
                if (size < 8 || p + size > end) break;
                byte[] box = new byte[size];
                Array.Copy(data, p, box, 0, size);
                list.Add((BoxType(data, p), box));
                p += size;
            }
            return list;
        }

        private static int FindBox(byte[] data, int start, int end, string type)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                int size = ReadBE32(data, pos);
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
            => IsContainerType(BoxType(data, off));

        private static byte[] BuildContainer(string type, List<byte[]> children)
        {
            int total = 8;
            foreach (var c in children) total += c.Length;
            byte[] box = new byte[total];
            WriteBE32(box, 0, total);
            WriteType(box, 4, type);
            int p = 8;
            foreach (var c in children)
            {
                Array.Copy(c, 0, box, p, c.Length);
                p += c.Length;
            }
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

        private static byte[] FromHex(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return result;
        }

        private static string BoxType(byte[] data, int off)
            => Encoding.ASCII.GetString(data, off + 4, 4);

        private static void WriteType(byte[] d, int off, string type)
        {
            d[off] = (byte)type[0];
            d[off + 1] = (byte)type[1];
            d[off + 2] = (byte)type[2];
            d[off + 3] = (byte)type[3];
        }

        private static int ReadBE32(byte[] d, int off)
            => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(off));

        private static long ReadBE64(byte[] d, int off)
            => BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(off));

        private static ushort ReadBE16(byte[] d, int off)
            => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off));

        private static void WriteBE32(byte[] d, int off, int v)
            => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(off), v);

        private static void WriteBE16(byte[] d, int off, ushort v)
            => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(off), v);

        private static void WriteBE24(byte[] d, int off, int v)
        {
            d[off] = (byte)((v >> 16) & 0xFF);
            d[off + 1] = (byte)((v >> 8) & 0xFF);
            d[off + 2] = (byte)(v & 0xFF);
        }

        private static int ReadBE24(byte[] d, int off)
            => (d[off] << 16) | (d[off + 1] << 8) | d[off + 2];
    }
}
