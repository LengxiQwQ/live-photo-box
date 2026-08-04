// <summary>
// HUAWEI Moving Photo protocol (native format).
// HUAWEI uses a proprietary binary format with a 60-byte LIVE_ tail marker
// instead of XMP metadata. Two container variants:
//   - JPEG: [JPEG still image] + [MP4 video] + [60-byte tail]
//   - HEIC: [HEIC still image] + [MP4 video] + [60-byte tail]
//
// The LIVE_ tail is the ONLY live-photo detection marker. All other markers
// (tmap, com.openharmony.*, Track 3 timed_metadata, MPF structure, MakerNote,
// _cuva, ICC Profile) are non-essential and are NOT written by this protocol.
//
// Binary layout follows the SDK simplified format (verified working):
//   [still image] + [raw MP4] + [60B tail]
//
// 60B tail fields:
//   [+0..+5]  "v6_f{XX}"     Cover frame number (controls Gallery progress bar)
//   [+6..+19] spaces          Padding
//   [+20..+27] "{PPP}:{QQQQ}" Cover frame : Total frames (read-only history)
//   [+28..+39] spaces          Padding
//   [+40..+51] "LIVE_{NNNN}"  MP4 size + 20 (live-photo detection marker)
//   [+52..+59] spaces          Padding
//
// Reference: docs/实况照片协议完整分析报告.md, scripts/Python_Scripts/convert_apple_to_huawei.py
// Used by: HUAWEI and Honor devices.
// </summary>

using System;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // HUAWEI Moving Photo (native, single-file).
    // Does NOT use XMP. Uses proprietary LIVE_ binary tail marker
    // appended after the MP4 video payload.
    public sealed class HuaweiMovingPhotoProtocol : LivePhotoProtocol
    {
        // Protocol index for UI ComboBox selection (6 = HUAWEI Moving Photo).
        public override int Id => 6;

        // Short identifier for logging and protocol identification.
        public override string Key => "HuaweiMovingPhoto";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "HUAWEI Moving Photo";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "HUAWEI Moving Photo";

        // ── Tail marker ─────────────────────────────────────────────────

        /// <summary>
        /// Build the 60-byte HUAWEI LIVE_ tail marker.
        /// The tail is appended after the MP4 video at the very end of the file.
        /// </summary>
        /// <param name="coverFrame">Cover frame number (0-based).
        /// The still image itself is frame 0.</param>
        /// <param name="totalFrames">Total video frame count.</param>
        /// <param name="mp4Size">Size of the MP4 video payload in bytes.</param>
        /// <param name="tailPrefix">Tail version prefix. Default "v6_f".</param>
        /// <returns>Exactly 60 bytes, space-padded, ASCII-encoded.</returns>
        public static byte[] BuildTail(int coverFrame, int totalFrames, long mp4Size,
            string tailPrefix = "v6_f")
        {
            byte[] tail = new byte[60];
            // Fill entire buffer with spaces (0x20)
            for (int i = 0; i < 60; i++) tail[i] = 0x20;

            // [+0..+5]  "{prefix}{XX}" — cover frame number
            string vf = $"{tailPrefix}{coverFrame}";
            byte[] vfBytes = Encoding.ASCII.GetBytes(vf);
            int vfLen = Math.Min(vfBytes.Length, 6);
            Array.Copy(vfBytes, 0, tail, 0, vfLen);

            // [+20..+27]  "{PPP}:{QQQQ}" — cover frame : total frames
            string pq = $"{coverFrame}:{totalFrames}";
            byte[] pqBytes = Encoding.ASCII.GetBytes(pq);
            int pqLen = Math.Min(pqBytes.Length, 8);
            Array.Copy(pqBytes, 0, tail, 20, pqLen);

            // [+40..+51]  "LIVE_{NNNNNNN}" — MP4 size + 20
            // Field target is 12 bytes, but for larger MP4 the value can extend
            // into trailing space padding (bytes 52-59), matching Python behavior.
            long liveValue = mp4Size + 20;
            string live = $"LIVE_{liveValue}";
            byte[] liveBytes = Encoding.ASCII.GetBytes(live);
            int liveLen = Math.Min(liveBytes.Length, 14); // up to 14 bytes, extends into trailing padding
            Array.Copy(liveBytes, 0, tail, 40, liveLen);

            return tail;
        }

        /// <summary>
        /// Build the 60-byte tail with preserved original PPP:QQQQ values.
        /// Used when exporting a new cover — the original cover timestamp and
        /// video duration (PPP:QQQQ) should be kept as read-only history.
        /// </summary>
        public static byte[] BuildTail(int coverFrame, int totalFrames, long mp4Size,
            int originalCoverMs, int originalDurationMs, string tailPrefix = "v6_f")
        {
            byte[] tail = BuildTail(coverFrame, totalFrames, mp4Size, tailPrefix);

            // Overwrite [+20..+27] with original PPP:QQQQ instead of coverFrame:totalFrames
            string pq = $"{originalCoverMs}:{originalDurationMs}";
            byte[] pqBytes = Encoding.ASCII.GetBytes(pq);
            int pqLen = Math.Min(pqBytes.Length, 8);
            for (int i = 20; i < 28; i++) tail[i] = 0x20; // clear to spaces
            Array.Copy(pqBytes, 0, tail, 20, pqLen);

            return tail;
        }

        /// <summary>
        /// Read the 60-byte LIVE_ tail from a Huawei/Honor Moving Photo file.
        /// Returns (coverFrame, coverMs, durationMs) or null if the tail is not valid.
        /// coverMs and durationMs come from the PPP:QQQQ field (read-only history).
        /// </summary>
        public static (int coverFrame, int coverMs, int durationMs)? ReadTail(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 60) return null;

                fs.Seek(-60, SeekOrigin.End);
                byte[] tail = new byte[60];
                fs.ReadExactly(tail, 0, 60);

                // [+0..+5]  "v6_f{XX}" — validate format
                if (tail[0] != 'v' || tail[1] < '0' || tail[1] > '9'
                    || tail[2] != '_' || tail[3] != 'f')
                    return null;

                // Parse cover frame number
                int coverFrame = 0;
                for (int i = 4; i < 6; i++)
                {
                    if (tail[i] >= '0' && tail[i] <= '9')
                        coverFrame = coverFrame * 10 + (tail[i] - '0');
                }

                // [+20..+27]  "{PPP}:{QQQQ}" — parse original cover ms : duration ms
                int coverMs = 0, durationMs = 0;
                int colonIdx = -1;
                for (int i = 20; i < 28; i++)
                {
                    if (tail[i] == ':') { colonIdx = i; break; }
                }
                if (colonIdx > 20)
                {
                    // Parse PPP before colon
                    for (int i = 20; i < colonIdx; i++)
                    {
                        if (tail[i] >= '0' && tail[i] <= '9')
                            coverMs = coverMs * 10 + (tail[i] - '0');
                    }
                    // Parse QQQQ after colon
                    for (int i = colonIdx + 1; i < 28; i++)
                    {
                        if (tail[i] >= '0' && tail[i] <= '9')
                            durationMs = durationMs * 10 + (tail[i] - '0');
                    }
                }

                return (coverFrame, coverMs, durationMs);
            }
            catch
            {
                return null;
            }
        }

        // ── XMP (unused) ────────────────────────────────────────────────

        // HUAWEI does NOT use XMP metadata. This method is never called —
        // WriteLivePhotoAsync routes Huawei to a dedicated binary writer.
        public override byte[] BuildXmpMetadata(long videoSize)
            => throw new NotSupportedException("HUAWEI protocol does not use XMP metadata.");

        // HUAWEI does NOT use XMP metadata.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => throw new NotSupportedException("HUAWEI protocol does not use XMP metadata.");
    }
}
