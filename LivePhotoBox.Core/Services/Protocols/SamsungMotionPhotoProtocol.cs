/*
 * SamsungMotionPhotoProtocol.cs
 *
 * Samsung Motion Photo 协议：双轨元数据系统。用于 Samsung Galaxy S23+、Galaxy Tab S9+ 及更新设备。
 *
 *   - V2 XMP（GCamera:MotionPhoto + Container:Directory）供 Google Photos 兼容
 *   - Samsung 私有尾挂（7 个 tag + SEFH/SEFT）是 Samsung 相册唯一读取的部分
 *   - JPEG 布局：[带 V2 XMP 的 JPEG] + [Tag data：MotionPhoto_Data(视频) + MotionPhoto_Version] + [SEFH...SEFT]
 *   - HEIC 布局：[带 V2 XMP 的 HEIC] + [mpvd box 内含 MP4 视频 + sefd box 内含 Tag data + SEFH/SEFT]；
 *     HEIC 中 MotionPhoto_Data 存 12 字节指针（"mpv2" + offset + size）而非视频本体
 *
 * 参考：PetrVys/MotionPhoto2、doodspav/motionphoto、docs/实况照片协议完整分析报告.md
 */

using System;
using System.Buffers.Binary;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // Samsung Motion Photo (dual-track metadata).
    // Inherits MotionPhotoV2Protocol for V2 XMP generation (Google Photos compat).
    // Adds Samsung Private Trailer generation (Samsung Gallery recognition).
    public class SamsungMotionPhotoProtocol : MotionPhotoV2Protocol
    {
        // Protocol index for UI ComboBox selection (4 = Samsung Motion Photo).
        public override int Id => 5;

        // Short identifier for logging and protocol identification.
        public override string Key => "SamsungMotionPhoto";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "Samsung Motion Photo";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "Samsung Motion Photo";

        // ── Samsung Trailer constants ──────────────────────────────────────

        // Samsung Tag marker IDs (little-endian uint16 with 0x0000 prefix).
        // Full 4-byte sequences: [0x00, 0x00, marker_lo, marker_hi].
        private static readonly byte[] MarkerMotionPhotoData =
            { 0x00, 0x00, 0x30, 0x0a }; // 0x0a30
        private static readonly byte[] MarkerMotionPhotoVersion =
            { 0x00, 0x00, 0x31, 0x0a }; // 0x0a31

        private const string TagNameMotionPhotoData = "MotionPhoto_Data";
        private const string TagNameMotionPhotoVersion = "MotionPhoto_Version";
        private const string MotionPhotoVersionValue = "mpv3";
        private const int SefhVersion = 107;
        private const int FieldCount = 2;

        // ISOBMFF box FourCCs for HEIC output.
        private static readonly byte[] MpvdFourCc = Encoding.ASCII.GetBytes("mpvd");
        private static readonly byte[] SefdFourCc = Encoding.ASCII.GetBytes("sefd");
        private const int BoxHeaderSize = 8; // 4B size + 4B FourCC
        private static readonly byte[] Mpv2Signature = Encoding.ASCII.GetBytes("mpv2");

        // ── Trailer builder ────────────────────────────────────────────────

        /// <summary>
        /// Build the complete Samsung Trailer for appending after the image data.
        /// </summary>
        /// <param name="videoData">Raw MP4 video bytes.</param>
        /// <param name="imageType">"jpg" or "heic".</param>
        /// <param name="imageSize">
        /// Size of the still image portion in bytes (before any trailer).
        /// Required for HEIC to compute the mpv2 video offset pointer.
        /// Ignored for JPEG.
        /// </param>
        /// <returns>
        /// JPEG: tag_data + SEFH/SEFT bytes.
        /// HEIC: complete mpvd box (mpvd + video + sefd + tags + SEFH/SEFT).
        /// </returns>
        public static byte[] BuildTrailer(byte[] videoData, string imageType, long imageSize = 0)
        {
            bool isHeic = string.Equals(imageType, "heic", StringComparison.OrdinalIgnoreCase);

            // Build the MotionPhoto_Data payload
            byte[] motionPhotoPayload;
            if (isHeic)
            {
                // HEIC: "mpv2" + video_offset(BE u32) + video_size(BE u32) = 12 bytes
                // video_offset = imageSize + 8 (mpvd box header)
                long videoOffset = imageSize + BoxHeaderSize;
                motionPhotoPayload = new byte[12];
                Array.Copy(Mpv2Signature, 0, motionPhotoPayload, 0, 4);
                BinaryPrimitives.WriteInt32BigEndian(motionPhotoPayload.AsSpan(4), (int)videoOffset);
                BinaryPrimitives.WriteInt32BigEndian(motionPhotoPayload.AsSpan(8), videoData.Length);
            }
            else
            {
                // JPEG: entire video bytes
                motionPhotoPayload = videoData;
            }

            // Build tag data for both tags
            int tagDataLen, tagVerLen;
            byte[] tagData = BuildTag(MarkerMotionPhotoData, TagNameMotionPhotoData,
                motionPhotoPayload, out tagDataLen);
            byte[] tagVer = BuildTag(MarkerMotionPhotoVersion, TagNameMotionPhotoVersion,
                Encoding.ASCII.GetBytes(MotionPhotoVersionValue), out tagVerLen);

            byte[] combinedTagData = new byte[tagDataLen + tagVerLen];
            Array.Copy(tagData, 0, combinedTagData, 0, tagDataLen);
            Array.Copy(tagVer, 0, combinedTagData, tagDataLen, tagVerLen);

            // SEF offsets: distance backwards from SEFH start to each tag's start
            // MotionPhoto_Data starts at -(total tag data), MotionPhoto_Version starts at -(tagVerLen)
            int offsetData = tagDataLen + tagVerLen;
            int offsetVer = tagVerLen;

            // Build SEFH/SEFT section
            byte[] sefSection = BuildSefSection(
                MarkerMotionPhotoData, offsetData, tagDataLen,
                MarkerMotionPhotoVersion, offsetVer, tagVerLen);

            if (isHeic)
            {
                // HEIC layout: mpvd [ video | sefd [ tag_data | SEFH/SEFT ] ]
                int sefdPayloadLen = combinedTagData.Length + sefSection.Length;
                int sefdBoxSize = BoxHeaderSize + sefdPayloadLen;
                byte[] sefdBox = new byte[sefdBoxSize];
                BinaryPrimitives.WriteInt32BigEndian(sefdBox.AsSpan(0), sefdBoxSize);
                Array.Copy(SefdFourCc, 0, sefdBox, 4, 4);
                Array.Copy(combinedTagData, 0, sefdBox, BoxHeaderSize, combinedTagData.Length);
                Array.Copy(sefSection, 0, sefdBox, BoxHeaderSize + combinedTagData.Length, sefSection.Length);

                int mpvdPayloadLen = videoData.Length + sefdBox.Length;
                int mpvdBoxSize = BoxHeaderSize + mpvdPayloadLen;
                byte[] result = new byte[mpvdBoxSize];
                BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0), mpvdBoxSize);
                Array.Copy(MpvdFourCc, 0, result, 4, 4);
                Array.Copy(videoData, 0, result, BoxHeaderSize, videoData.Length);
                Array.Copy(sefdBox, 0, result, BoxHeaderSize + videoData.Length, sefdBox.Length);
                return result;
            }
            else
            {
                // JPEG layout: tag_data | SEFH/SEFT
                byte[] result = new byte[combinedTagData.Length + sefSection.Length];
                Array.Copy(combinedTagData, 0, result, 0, combinedTagData.Length);
                Array.Copy(sefSection, 0, result, combinedTagData.Length, sefSection.Length);
                return result;
            }
        }

        // ── Private helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Build a single Samsung Trailer tag.
        /// Format: [0x00,0x00][marker LE u16][name_len LE u32][name UTF-8][data]
        /// </summary>
        private static byte[] BuildTag(byte[] marker, string name, byte[] data, out int totalLength)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int nameLenField = nameBytes.Length;

            totalLength = 2 + 2 + 4 + nameBytes.Length + data.Length;
            byte[] result = new byte[totalLength];
            int pos = 0;

            // [0x00, 0x00] prefix
            result[pos++] = 0x00;
            result[pos++] = 0x00;

            // marker (already in [0x00, 0x00, marker_lo, marker_hi] format)
            Array.Copy(marker, 2, result, pos, 2);
            pos += 2;

            // name length (LE u32)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), nameLenField);
            pos += 4;

            // name string (UTF-8)
            Array.Copy(nameBytes, 0, result, pos, nameBytes.Length);
            pos += nameBytes.Length;

            // data
            Array.Copy(data, 0, result, pos, data.Length);
            // pos += data.Length; — not needed, we're done

            return result;
        }

        /// <summary>
        /// Build the SEFH/SEFT validation section.
        /// Structure: "SEFH" + version + field_count + N×entry + total_size + "SEFT"
        /// Each entry: [0x00,0x00][marker LE u16][offset LE u32][size LE u32] = 12 bytes
        /// </summary>
        private static byte[] BuildSefSection(
            byte[] marker1, int offset1, int size1,
            byte[] marker2, int offset2, int size2)
        {
            const int entrySize = 12; // 4B marker + 4B offset + 4B size
            int headerSize = 4 + 4 + 4; // "SEFH" + version + field_count
            int entriesSize = FieldCount * entrySize;
            int footerSize = 4 + 4; // total_size + "SEFT"
            int totalSefSize = headerSize + entriesSize + footerSize;

            byte[] result = new byte[totalSefSize];
            int pos = 0;

            // "SEFH"
            byte[] sefhBytes = Encoding.ASCII.GetBytes("SEFH");
            Array.Copy(sefhBytes, 0, result, pos, 4);
            pos += 4;

            // version (LE u32, always 107)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), SefhVersion);
            pos += 4;

            // field count (LE u32, = 2)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), FieldCount);
            pos += 4;

            // Entry 1: MotionPhoto_Data
            Array.Copy(marker1, 0, result, pos, 4);
            pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), offset1);
            pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), size1);
            pos += 4;

            // Entry 2: MotionPhoto_Version
            Array.Copy(marker2, 0, result, pos, 4);
            pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), offset2);
            pos += 4;
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), size2);
            pos += 4;

            // total SEF size (LE u32, including this field and "SEFT")
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(pos), totalSefSize);
            pos += 4;

            // "SEFT"
            byte[] seftBytes = Encoding.ASCII.GetBytes("SEFT");
            Array.Copy(seftBytes, 0, result, pos, 4);

            return result;
        }
    }
}
