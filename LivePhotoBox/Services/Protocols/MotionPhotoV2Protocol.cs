using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    /// <summary>
    /// Google Motion Photo V2.
    /// Uses <c>Container:Directory</c> with <c>Item:Semantic="MotionPhoto"</c> to
    /// describe the appended video.  Standard on Google Pixel, Samsung, and
    /// Xiaomi HyperOS 3+.
    /// </summary>
    public sealed class MotionPhotoV2Protocol : LivePhotoProtocol
    {
        public override int Id => 1;
        public override string Key => "MotionPhotoV2";
        public override string DisplayName => "(推荐) Google Motion Photo (V2)";
        public override string DisplayNameEn => "(Old) Google Motion Photo (V2)";

        private const string RdfTemplate =
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            " GCamera:MotionPhotoPresentationTimestampUs=\"0\">" +
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

        public override byte[] BuildXmpMetadata(long videoSize)
            => WrapXmp(string.Format(RdfTemplate, videoSize));
    }
}
