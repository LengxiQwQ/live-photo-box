// <summary>
// HUAWEI Moving Photo V3 protocol (HarmonyOS 4.0 native format).
//
// Differs from the newer V6 protocol (HarmonyOS 5+):
//   - Tail uses "v3_f" prefix instead of "v6_f"
//   - MP4 uses minimal ISOBMFF: ftyp(24B, 2 compat brands) + moov + free
//   - No udta ©too or openharmony keys
//   - JPEG only (HarmonyOS 4.0 does not produce HEIC live photos)
//   - MP4 brand "mp42", compat "isom" + "mp42" only
//
// Reference: real HarmonyOS 4.0 sample files (IMG_20260802_*.jpg)
// Used by: HUAWEI devices running HarmonyOS 4.x.
// </summary>

using System;

namespace LivePhotoBox.Services.Protocols
{
    // HUAWEI Moving Photo V3 (HarmonyOS 4.0, Mate 60).
    // Single-file JPEG + embedded MP4 + 60B v3_f tail.
    public sealed class HuaweiMovingPhotoV3Protocol : LivePhotoProtocol
    {
        // Protocol index for UI ComboBox selection (7 = HUAWEI Moving Photo V3).
        public override int Id => 7;

        // Short identifier for logging and protocol identification.
        public override string Key => "HuaweiMovingPhotoV3";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "HUAWEI Moving Photo (鸿蒙4.0)";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "HUAWEI Moving Photo (HarmonyOS 4.0)";

        // ── XMP (unused) ────────────────────────────────────────────────

        public override byte[] BuildXmpMetadata(long videoSize)
            => throw new NotSupportedException("HUAWEI V3 protocol does not use XMP metadata.");

        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => throw new NotSupportedException("HUAWEI V3 protocol does not use XMP metadata.");
    }
}
