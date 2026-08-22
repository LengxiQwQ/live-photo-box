/*
 * MotionPhotoV2Protocol.cs
 *
 * Google Motion Photo V2 协议解析器：用 Container:Directory XMP 结构
 * （Item:Semantic="MotionPhoto"）描述追加的视频载荷。
 * 现代跨平台标准，用于 Google Pixel、Samsung Galaxy、小米 HyperOS 3+。
 *
 *   - image/jpeg（默认，Padding=0）：视频以原始字节追加
 *   - image/heic（Padding=8）：视频包成 mpvd ISOBMFF box
 *   - image/avif（Padding=8）：与 HEIC 相同的 ISOBMFF 结构
 */

using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // Google Motion Photo V2.
    // Uses <c>Container:Directory</c> with <c>Item:Semantic="MotionPhoto"</c> to
    // describe the appended video.  Standard on Google Pixel, Samsung, and
    // Xiaomi HyperOS 3+.
    public class MotionPhotoV2Protocol : LivePhotoProtocol
    {
        // Protocol index for UI ComboBox selection (1 = Motion Photo V2).
        public override int Id => 2;

        // Short identifier for logging and protocol identification.
        public override string Key => "MotionPhotoV2";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "Google Motion Photo (v2)";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "Google Motion Photo (v2)";

        // ── Primary image format ────────────────────────────────────────

        // MIME type for the primary image item (default: JPEG).
        // Set to "image/heic" or "image/avif" for HEIC/AVIF-based Motion Photos.
        // See: https://developer.android.com/media/platform/motion-photo-format
        public string PrimaryMime { get; set; } = "image/jpeg";

        // Padding value for the primary image item (default: 0 for JPEG).
        // Must be "8" for HEIC/AVIF — the 8-byte mpvd box header
        // (4-byte big-endian size + 4-byte FourCC 'mpvd') that wraps the video.
        public string PrimaryPadding { get; set; } = "0";

        // ── XMP template ────────────────────────────────────────────────

        // XMP RDF template for Motion Photo V2 metadata.
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
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            " GCamera:MotionPhotoPresentationTimestampUs=\"{1}\">" +
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

        // Build the XMP metadata bytes for Google Motion Photo V2.
        // Generates an rdf:Description with Container:Directory structure describing
        // the primary image and the appended MotionPhoto video.
        // videoSize: Size of the appended video in bytes.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        // Build XMP metadata with presentation timestamp (microseconds).
        // Uses the current PrimaryMime / PrimaryPadding properties for the
        // Container:Item attributes — set these before calling for non-JPEG output.
        // videoSize: Size of the appended video in bytes.
        // presentationTimestampUs: Timestamp in microseconds of the selected frame.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => BuildXmpMetadata(videoSize, presentationTimestampUs, PrimaryMime, PrimaryPadding);

        // Build XMP metadata with explicit primary image and video format parameters.
        // This overload is thread-safe — it does not rely on the mutable
        // PrimaryMime / PrimaryPadding properties of the singleton protocol instance.
        // videoSize: Size of the appended video in bytes.
        // presentationTimestampUs: Timestamp in microseconds of the selected frame.
        // primaryMime: MIME type for the primary image (e.g. "image/jpeg", "image/heic").
        // primaryPadding: Padding value (e.g. "0" for JPEG, "8" for HEIC/AVIF).
        // videoMime: MIME type for the video (e.g. "video/mp4", "video/quicktime").
        public virtual byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs,
            string primaryMime, string primaryPadding, string videoMime = "video/mp4")
            => WrapXmp(
                string.Format(RdfTemplate, videoSize, presentationTimestampUs, primaryMime, primaryPadding, videoMime));
    }
}
