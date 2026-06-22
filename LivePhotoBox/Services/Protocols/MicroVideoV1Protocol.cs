using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    /// <summary>
    /// Google MicroVideo V1 (deprecated).
    /// Single-file container: JPEG + appended MP4, located by
    /// <c>GCamera:MicroVideoOffset</c> bytes from the end of the file.
    /// Used by older Xiaomi (MIUI) and some legacy Google Pixel firmware.
    /// </summary>
    public sealed class MicroVideoV1Protocol : LivePhotoProtocol
    {
        public override int Id => 0;
        public override string Key => "MicroVideoV1";
        public override string DisplayName => "(旧版) Google Micro Video (V1)";
        public override string DisplayNameEn => "(Pref) Google Micro Video (V1)";

        private const string RdfTemplate =
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " GCamera:MicroVideo=\"1\"" +
            " GCamera:MicroVideoVersion=\"1\"" +
            " GCamera:MicroVideoOffset=\"{0}\"" +
            " GCamera:MicroVideoPresentationTimestampUs=\"0\"/>";

        public override byte[] BuildXmpMetadata(long videoSize)
            => WrapXmp(string.Format(RdfTemplate, videoSize));
    }
}
