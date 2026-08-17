/*
 * LivePhotoProtocolDetector.cs
 *
 * 实况照片协议检测器。
 *
 *   - 核心原则：文件内容标记（XMP / 二进制尾标）优先于配对方式（ContentIdentifier），
 *     因 XMP 与尾标是协议作者有意写入的身份标识，而 ContentIdentifier 只是配对 UUID
 *   - 只识别拥有厂商相册必需实况标识的协议：华为 LIVE_ 尾标、三星 SEFH/SEFT、
 *     OPPO OpCamera、vivo VCamera、Fusion LivePhotoBox、Apple ContentIdentifier、
 *     Google V1/V2
 *   - 按优先级从高到低检测，先命中即返回
 *
 * 参考：docs/实况照片协议完整分析报告.md §检测与识别
 */

using LivePhotoBox.Models;
using System;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services
{
    public static class LivePhotoProtocolDetector
    {
        /// <summary>文件尾扫描缓冲区大小</summary>
        private const int TailProbeBytes = 4096;

        // ── 尾部二进制标记 ────────────────────────────────────────────
        private static readonly byte[] LiveUnderscoreMarker = "LIVE_"u8.ToArray();
        private static readonly byte[] SefhMarker = "SEFH"u8.ToArray();
        private static readonly byte[] SeftMarker = "SEFT"u8.ToArray();
        private static readonly byte[] MotionPhotoDataTagMarker =
            { 0x00, 0x00, 0x30, 0x0a }; // Samsung marker 0x0a30

        /// <summary>
        /// 检测文件的实况照片协议类型。
        /// 先检查文件内容标记（XMP + 尾标），内容标记不命中才走双文件配对检测。
        /// 这确保 OPPO/vivo/华为 等有明确内容标记的协议不会被 ContentIdentifier 配对"抢走"。
        /// </summary>
        public static LivePhotoProtocolType Detect(
            string filePath,
            LivePhotoType livePhotoType,
            string? contentIdentifier = null,
            string? xmpText = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return LivePhotoProtocolType.Unknown;
            if (livePhotoType == LivePhotoType.None)
                return LivePhotoProtocolType.Unknown;

            try
            {
                // ── 阶段 1: 文件内容检测（XMP + 尾标）────────────────
                // 无论 LivePhotoType 是什么，先检查文件自身的内容标记。
                // 这样即使文件被 CID 匹配误标为 DualFile，
                // 内容中的 XMP/尾标仍然能正确识别协议。
                // Dual-file VIVO tail marker takes priority over XMP.
                // A dual-file JPG may still carry an HDR gain-map XMP whose
                // Container:Directory (Primary/GainMap) would otherwise be
                // mistaken for Google MotionPhoto V2.
                if (livePhotoType == LivePhotoType.DualFile)
                {
                    byte[]? tail = ReadFileTail(filePath, TailProbeBytes);
                    if (tail != null)
                    {
                        string tailText = Encoding.UTF8.GetString(tail);
                        if (tailText.Contains(
                                "com.android.camera.livephoto", StringComparison.Ordinal))
                        {
                            return LivePhotoProtocolType.Vivo;
                        }
                    }
                }

                var contentProtocol = DetectFromFileContent(filePath, xmpText);
                if (contentProtocol != LivePhotoProtocolType.Unknown)
                    return contentProtocol;

                // ── 阶段 2: 双文件配对检测 ──────────────────────────
                // 只有文件内容没有明确协议标记时，才用配对方式判断
                // Apple Live Photo 一定是双文件（图片+视频通过 ContentIdentifier 配对），
                // 单文件绝不可能是 Apple。
                if (livePhotoType == LivePhotoType.DualFile)
                {
                    // ContentIdentifier UUID 配对 → Apple
                    if (!string.IsNullOrWhiteSpace(contentIdentifier))
                        return LivePhotoProtocolType.Apple;

                    // vivo 旧格式双文件 → JPEG 尾 vivo{JSON} / MP4 uuid box
                    // VIVO dual-file tail was already checked in the phase 0
                    // block above; reaching here means the file is Apple-style
                    // dual-file (or an unmarked pair).
                }

                return LivePhotoProtocolType.Unknown;
            }
            catch (Exception ex)
            {
                LogService.Scan(
                    $"Protocol detection error for '{Path.GetFileName(filePath)}': {ex.Message}",
                    Models.LogLevel.Warning);
                return LivePhotoProtocolType.Unknown;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  文件内容检测：尾标 → XMP（按优先级）
        // ══════════════════════════════════════════════════════════════

        private static LivePhotoProtocolType DetectFromFileContent(
            string filePath, string? xmpText)
        {
            // ── 优先级 1: 华为 LIVE_ 尾标 ────────────────────────────
            byte[]? tail = ReadFileTail(filePath, TailProbeBytes);
            if (tail != null && ContainsBytes(tail, LiveUnderscoreMarker))
                return LivePhotoProtocolType.Huawei;

            // ── 优先级 2-3: Samsung Trailer vs Fusion ──────────────────
            // 两者都有 SEFH+SEFT+MotionPhoto_Data 尾标，通过 XMP 区分
            bool hasSamsungTail = tail != null
                && ContainsBytes(tail, SefhMarker)
                && ContainsBytes(tail, SeftMarker)
                && ContainsBytes(tail, MotionPhotoDataTagMarker);

            xmpText ??= ReadXmpText(filePath);

            if (hasSamsungTail)
            {
                // Samsung Trailer 存在 → 可能是 Samsung 或 Fusion
                // Fusion 的特征：LivePhotoBox:Protocol="MotionPhotoFusion"
                // 注意：不能只看 "LivePhotoBox" 字符串——所有我们的文件都有它
                if (xmpText.Contains("LivePhotoBox:Protocol=\"MotionPhotoFusion\"", StringComparison.Ordinal)
                    || xmpText.Contains("OpCamera:VideoLength", StringComparison.Ordinal)
                    || xmpText.Contains("OpCamera:MotionPhotoOwner", StringComparison.Ordinal)
                    || xmpText.Contains("oplus_", StringComparison.Ordinal))
                    return LivePhotoProtocolType.Fusion;
                // 纯 Samsung（仅有 V2 XMP + Trailer）
                return LivePhotoProtocolType.Samsung;
            }

            // ── 优先级 4-8: XMP 文本扫描 ──────────────────────────
            return DetectFromXmp(xmpText);
        }

        /// <summary>从 XMP 文本按优先级检测协议</summary>
        internal static LivePhotoProtocolType DetectFromXmp(string xmpText)
        {
            // ── 优先：LivePhotoBox:Protocol 字段（我们 app 生成的，最可靠）──
            // WrapXmp 在每个文件里写了 LivePhotoBox:Protocol="{Key}"，
            // 直接读这个值就能知道当初是用哪个协议生成的。
            if (xmpText.Contains("LivePhotoBox:Protocol=\"MotionPhotoFusion\"", StringComparison.Ordinal))
                return LivePhotoProtocolType.Fusion;
            if (xmpText.Contains("LivePhotoBox:Protocol=\"OppoLivePhoto\"", StringComparison.Ordinal))
                return LivePhotoProtocolType.OPPO;
            if (xmpText.Contains("LivePhotoBox:Protocol=\"VivoLivePhoto\"", StringComparison.Ordinal))
                return LivePhotoProtocolType.Vivo;
            if (xmpText.Contains("LivePhotoBox:Protocol=\"SamsungMotionPhoto\"", StringComparison.Ordinal))
                return LivePhotoProtocolType.Samsung;
            if (xmpText.Contains("LivePhotoBox:Protocol=\"MicroVideoV1\"", StringComparison.Ordinal))
                return LivePhotoProtocolType.GoogleV1;
            if (xmpText.Contains("LivePhotoBox:Protocol=\"MotionPhotoV2\"", StringComparison.Ordinal))
                return LivePhotoProtocolType.GoogleV2;

            // ── 兜底：厂商 XMP 标记（原厂文件 / exiftool 处理过的文件）──
            // OPPO — OpCamera 命名空间 或 oplus_ EXIF 兜底
            if (xmpText.Contains("OpCamera:VideoLength", StringComparison.Ordinal)
                || xmpText.Contains("OpCamera:MotionPhotoOwner", StringComparison.Ordinal)
                || xmpText.Contains("xmlns:OpCamera", StringComparison.Ordinal)
                || xmpText.Contains("oplus_", StringComparison.Ordinal))
                return LivePhotoProtocolType.OPPO;

            // vivo — VCamera 命名空间
            if (xmpText.Contains("VCamera:VMotionPhotoVersion", StringComparison.Ordinal)
                || xmpText.Contains("xmlns:VCamera", StringComparison.Ordinal))
                return LivePhotoProtocolType.Vivo;

            // Google V1 — MicroVideo 但无 Container:Directory（V1 独有特征）
            bool hasMicroVideo = xmpText.Contains("GCamera:MicroVideo", StringComparison.Ordinal);
            bool hasDirectory = xmpText.Contains("Container:Directory", StringComparison.Ordinal);
            bool hasMotionPhoto = xmpText.Contains("GCamera:MotionPhoto", StringComparison.Ordinal);

            if (hasMicroVideo && !hasDirectory)
                return LivePhotoProtocolType.GoogleV1;

            // Google V2 — Container:Directory 或 MotionPhoto（最通用，兜底）
            if (hasDirectory || hasMotionPhoto)
                return LivePhotoProtocolType.GoogleV2;

            return LivePhotoProtocolType.Unknown;
        }

        // ══════════════════════════════════════════════════════════════
        //  辅助方法
        // ══════════════════════════════════════════════════════════════

        /// <summary>读取文件头部 XMP 文本（复用已验证的 ReadMetadataTextSync）</summary>
        private static string ReadXmpText(string filePath)
        {
            return LivePhotoSplitService.ReadMetadataTextSync(filePath);
        }

        /// <summary>读取文件尾部指定字节数</summary>
        private static byte[]? ReadFileTail(string filePath, int maxBytes)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                int readSize = (int)Math.Min(fileSize, maxBytes);
                if (readSize <= 0) return null;

                byte[] buffer = new byte[readSize];
                fs.Seek(-readSize, SeekOrigin.End);
                int totalRead = 0;
                while (totalRead < readSize)
                {
                    int n = fs.Read(buffer, totalRead, readSize - totalRead);
                    if (n == 0) break;
                    totalRead += n;
                }
                return buffer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在字节数组中搜索子序列</summary>
        private static bool ContainsBytes(byte[] data, byte[] pattern)
        {
            if (pattern.Length == 0) return false;
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                int j;
                for (j = 0; j < pattern.Length; j++)
                    if (data[i + j] != pattern[j]) break;
                if (j == pattern.Length) return true;
            }
            return false;
        }
    }
}
