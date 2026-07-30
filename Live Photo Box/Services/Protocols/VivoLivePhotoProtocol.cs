// <summary>
// Vivo Live Photo protocol (X300 series and later).
// Vivo X300+ uses the Google Motion Photo V2 standard with additional
// VCamera namespace fields for vivo-specific identification.
// Binary layout: [JPEG] + [MP4 video] — identical to Motion Photo V2.
//
// VCamera fields (all non-essential for live-photo detection):
//   VCamera:VMotionPhotoVersion — vivo protocol version (currently "1")
//   VCamera:VMotionPhotoSource   — source indicator ("1" = camera capture)
//   VCamera:VMediaKitVersion     — media engine version string
//
// Video location is entirely determined by V2's Item:Length; VCamera
// fields are auxiliary identity markers only.
// Used by: Vivo devices (X300 series and later).
// </summary>

using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // Vivo Live Photo (X300 series, single-file).
    // Extends Google Motion Photo V2 with VCamera namespace attributes
    // for vivo Gallery recognition. Binary structure is identical to V2.
    public sealed class VivoLivePhotoProtocol : MotionPhotoV2Protocol
    {
        // Protocol index for UI ComboBox selection (3 = VIVO Live Photo).
        public override int Id => 4;

        // Short identifier for logging and protocol identification.
        public override string Key => "VivoLivePhoto";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "vivo Live Photo";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "vivo Live Photo";

        // ── XMP template ────────────────────────────────────────────────

        // XMP RDF template for Vivo Live Photo metadata.
        // Identical to Motion Photo V2 but with the VCamera namespace
        // declaration and three vivo-specific attributes on rdf:Description.
        //
        // {0} = video payload size in bytes (Item:Length for MotionPhoto item).
        // {1} = presentation timestamp in microseconds (GCamera:MotionPhotoPresentationTimestampUs).
        // {2} = primary image MIME type (e.g. "image/jpeg", "image/heic").
        // {3} = primary image padding (e.g. "0" for JPEG, "8" for HEIC/AVIF).
        // {4} = video MIME type (e.g. "video/mp4", "video/quicktime").
        private const string RdfTemplate =
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " xmlns:VCamera=\"http://ns.vivo.com/photos/1.0/camera/\"" +
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            " GCamera:MotionPhotoPresentationTimestampUs=\"{1}\"" +
            " VCamera:VMotionPhotoVersion=\"1\"" +
            " VCamera:VMotionPhotoSource=\"1\"" +
            " VCamera:VMediaKitVersion=\"1.0.0.9\">" +
            "<Container:Directory>" +
            "<rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Mime=\"{2}\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"{3}\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Mime=\"{4}\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{0}\" Item:Padding=\"0\"/>" +
            "</rdf:li>" +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "</rdf:Description>";

        // ── BuildXmpMetadata overrides ──────────────────────────────────

        // Build the XMP metadata bytes for Vivo Live Photo.
        // videoSize: Size of the appended video in bytes.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        // Build XMP metadata with presentation timestamp (microseconds).
        // Uses the inherited PrimaryMime / PrimaryPadding properties.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => BuildXmpMetadata(videoSize, presentationTimestampUs, PrimaryMime, PrimaryPadding);

        // Build XMP metadata with explicit format parameters (thread-safe).
        // videoSize: Size of the appended video in bytes.
        // presentationTimestampUs: Timestamp in microseconds of the selected frame.
        // primaryMime: MIME type for the primary image (e.g. "image/jpeg", "image/heic").
        // primaryPadding: Padding value (e.g. "0" for JPEG, "8" for HEIC/AVIF).
        // videoMime: MIME type for the video (e.g. "video/mp4", "video/quicktime").
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs,
            string primaryMime, string primaryPadding, string videoMime = "video/mp4")
            => WrapXmp(
                string.Format(RdfTemplate, videoSize, presentationTimestampUs, primaryMime, primaryPadding, videoMime),
                Key);
    }
}
