// <summary>
// Google Motion Photo V2 protocol parser.
// Implements the standard Motion Photo format using the Container:Directory XMP
// structure with Item:Semantic="MotionPhoto" to describe the appended video payload.
// This is the modern cross-platform standard used by Google Pixel, Samsung Galaxy,
// and Xiaomi HyperOS 3+ devices.
// Protocol version: Motion Photo V2 (GCamera + Container namespaces).
// </summary>

using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // Google Motion Photo V2.
    // Uses <c>Container:Directory</c> with <c>Item:Semantic="MotionPhoto"</c> to
    // describe the appended video.  Standard on Google Pixel, Samsung, and
    // Xiaomi HyperOS 3+.
    public sealed class MotionPhotoV2Protocol : LivePhotoProtocol
    {
        // Protocol index for UI ComboBox selection (1 = Motion Photo V2).
        public override int Id => 1;

        // Short identifier for logging and protocol identification.
        public override string Key => "MotionPhotoV2";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "Google Motion Photo (V2)";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "Google Motion Photo (V2)";

        // XMP RDF template for Motion Photo V2 metadata.
        // Uses an expanded rdf:Description tag with GCamera, Container, and Item
        // namespaces, containing a Container:Directory with two items:
        // 1. Primary image (JPEG)
        // 2. MotionPhoto video (MP4)
        // {0} is replaced with the video payload size in bytes.
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
            "<Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{0}\" Item:Padding=\"0\"/>" +
            "</rdf:li>" +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "</rdf:Description>";

        // Build the XMP metadata bytes for Google Motion Photo V2.
        // Generates an rdf:Description with Container:Directory structure describing
        // the primary JPEG image and the appended MotionPhoto MP4 video.
        // videoSize: Size of the appended MP4 video in bytes.
        // è¿å: UTF-8 encoded XMP bytes wrapped in xpacket markers.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        // Build XMP metadata with presentation timestamp (microseconds).
        // This timestamp tells the viewer (Google Photos, Windows 11, etc.) where in the
        // video the key photo was taken, so playback starts from the correct position.
        // videoSize: Size of the appended MP4 video in bytes.
        // presentationTimestampUs: Timestamp in microseconds of the selected frame.
        // è¿å: UTF-8 encoded XMP bytes wrapped in xpacket markers.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => WrapXmp(string.Format(RdfTemplate, videoSize, presentationTimestampUs), Key);
    }
}
