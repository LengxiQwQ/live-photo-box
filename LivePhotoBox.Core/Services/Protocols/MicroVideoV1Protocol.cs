/*
 * MicroVideoV1Protocol.cs
 *
 * Google MicroVideo V1 协议解析器（已废弃）：检测与生成旧的 MicroVideo 格式。
 * MP4 视频载荷直接追加在 JPEG 图像之后，视频相对文件末尾的字节偏移记录在
 * XMP 的 GCamera:MicroVideoOffset 属性。
 * 用于旧版小米 (MIUI) 设备与部分 Google Pixel 固件。
 */

using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // Google MicroVideo V1 (deprecated).
    // Single-file container: JPEG + appended MP4, located by
    // <c>GCamera:MicroVideoOffset</c> bytes from the end of the file.
    // Used by older Xiaomi (MIUI) and some legacy Google Pixel firmware.
    public sealed class MicroVideoV1Protocol : LivePhotoProtocol
    {
        // Protocol index for UI ComboBox selection (0 = MicroVideo V1).
        public override int Id => 1;

        // Short identifier for logging and protocol identification.
        public override string Key => "MicroVideoV1";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "Google Micro Video (v1)";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "Google Micro Video (v1)";

        // XMP RDF template for MicroVideo V1 metadata.
        // Uses a self-closing rdf:Description tag with GCamera namespace attributes.
        // {0} is replaced with the byte offset of the appended MP4 video,
        // measured as the number of bytes from the end of the file.
        private const string RdfTemplate =
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " GCamera:MicroVideo=\"1\"" +
            " GCamera:MicroVideoVersion=\"1\"" +
            " GCamera:MicroVideoOffset=\"{0}\"" +
            " GCamera:MicroVideoPresentationTimestampUs=\"{1}\"/>";

        // Build the XMP metadata bytes for Google MicroVideo V1.
        // Generates a self-closing rdf:Description tag with GCamera:MicroVideoOffset
        // set to the video size. The video offset is measured from end of file,
        // so it equals the video payload size in bytes.
        // videoSize: Size of the appended MP4 video in bytes (equals the offset from end of file).
        // 返回: UTF-8 encoded XMP bytes wrapped in xpacket markers.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        // Build XMP metadata with presentation timestamp (microseconds).
        // This timestamp tells the viewer where in the video the cover was taken.
        // videoSize: Size of the appended MP4 video in bytes.
        // presentationTimestampUs: Timestamp in microseconds of the selected frame.
        // 返回: UTF-8 encoded XMP bytes wrapped in xpacket markers.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => WrapXmp(string.Format(RdfTemplate, videoSize, presentationTimestampUs), Key);
    }
}
