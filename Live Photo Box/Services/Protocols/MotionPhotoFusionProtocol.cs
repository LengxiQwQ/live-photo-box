// <summary>
// Motion Photo Fusion protocol.
// Combines Google V2, OPPO, VIVO, and Samsung private identification fields
// into a single file so that all four platform galleries recognise it as a live photo.
//
// XMP namespaces (all coexist in one rdf:Description):
//   GCamera  — Google Photos / Xiaomi / Windows detection
//   OpCamera — OPPO ColorOS / OnePlus OxygenOS Gallery detection
//   VCamera  — VIVO Gallery detection (X300 series and later)
//
// Binary layout (inherits Samsung SEFH/SEFT trailer):
//   [JPEG + unified XMP] + [24B tag header] + [MP4/MOV video] + [version tag] + [SEFH/SEFT]
//
// Additional markers:
//   EXIF UserComment = "oplus_10485792" — required by OPPO Gallery
//
// Identified as LivePhotoBox:Protocol="MotionPhotoFusion" in XMP for tooling.
// Not fused: HUAWEI (proprietary binary tail, no XMP) and V1 (deprecated).
// </summary>

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    // Motion Photo Fusion — one file, four galleries.
    // Extends SamsungMotionPhotoProtocol for the SEFH/SEFT trailer (Samsung Gallery
    // recognition) while injecting OpCamera and VCamera XMP attributes into the V2
    // rdf:Description so OPPO and VIVO galleries also recognise the file.
    public sealed class MotionPhotoFusionProtocol : SamsungMotionPhotoProtocol
    {
        // Protocol index for UI ComboBox selection (6 = Motion Photo Fusion).
        public override int Id => 0;

        // Short identifier for logging and protocol identification.
        // Written into XMP as LivePhotoBox:Protocol="MotionPhotoFusion" via WrapXmp.
        public override string Key => "MotionPhotoFusion";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "融合 - Motion Photo";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "Fusion - Motion Photo";

        // ── OPPO EXIF marker ──────────────────────────────────────────────

        // EXIF UserComment value that OPPO Gallery checks for live-photo recognition.
        // Same marker as OppoLivePhotoProtocol.
        private const string OppoExifMarker = "oplus_10485792";

        // ── XMP template ──────────────────────────────────────────────────

        // Unified XMP RDF template combining GCamera (V2 standard),
        // OpCamera (OPPO/OnePlus), and VCamera (VIVO) namespaces.
        //
        // {0} = Item:Length for MotionPhoto item (XMP-adjusted, may include
        //       Samsung trailer minus tag header padding).
        // {1} = presentation timestamp in microseconds.
        // {2} = primary image MIME type (e.g. "image/jpeg").
        // {3} = primary image padding (e.g. "0" for standard, "24" for Samsung tag header).
        // {4} = video MIME type (e.g. "video/mp4", "video/quicktime").
        // {5} = OpCamera:VideoLength — pure video size in bytes (OPPO's
        //       definition: trailer-free, header-free MP4/MOV size).
        private const string RdfTemplate =
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " xmlns:OpCamera=\"http://ns.oplus.com/photos/1.0/camera/\"" +
            " xmlns:VCamera=\"http://ns.vivo.com/photos/1.0/camera/\"" +
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            " GCamera:MotionPhotoPresentationTimestampUs=\"{1}\"" +
            " OpCamera:MotionPhotoPrimaryPresentationTimestampUs=\"{1}\"" +
            " OpCamera:MotionPhotoOwner=\"oplus\"" +
            " OpCamera:OLivePhotoVersion=\"2\"" +
            " OpCamera:VideoLength=\"{5}\"" +
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

        // ── BuildXmpMetadata overrides ────────────────────────────────────

        // Standard override: {0} and {5} use the same value (no Samsung trailer offset).
        // Used for non-Samsung binary layouts or when the call site doesn't
        // distinguish between XMP length and pure video size.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => BuildXmpMetadata(videoSize, presentationTimestampUs, PrimaryMime, PrimaryPadding);

        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs,
            string primaryMime, string primaryPadding, string videoMime = "video/mp4")
            => BuildXmpMetadata(videoSize, presentationTimestampUs, primaryMime, primaryPadding, videoMime, videoSize);

        // Fusion-specific overload: separates XMP-adjusted length from pure video size.
        //
        // xmpVideoLength:   Item:Length value (may be larger than the actual MP4 when
        //                   a Samsung trailer is appended after the video).
        // pureVideoSize:    OpCamera:VideoLength value — the actual MP4/MOV byte count
        //                   that OPPO Gallery uses to locate the video boundary.
        public byte[] BuildXmpMetadata(long xmpVideoLength, long presentationTimestampUs,
            string primaryMime, string primaryPadding, string videoMime,
            long pureVideoSize)
            => WrapXmp(
                string.Format(RdfTemplate, xmpVideoLength, presentationTimestampUs,
                    primaryMime, primaryPadding, videoMime, pureVideoSize),
                Key);

        // ── Image pre-processing ──────────────────────────────────────────

        // Inject the OPPO EXIF UserComment marker (oplus_10485792) into a temp
        // copy of the source image, so OPPO Gallery recognises the output.
        // Falls back to the original path if exiftool is unavailable.
        public override async Task<string> PrepareImageAsync(
            string sourceImagePath, string workDir, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (!IsExifToolAvailable)
            {
                LogService.Merge(
                    "exiftool not found — OPPO oplus_ marker will not be injected. " +
                    "The output will be a valid Motion Photo but may not animate in OPPO Gallery.",
                    Models.LogLevel.Warning);
                return sourceImagePath;
            }

            string tempPath = Path.Combine(
                workDir,
                $"{Path.GetFileNameWithoutExtension(sourceImagePath)}_fusion_tmp_{Guid.NewGuid():N}.jpg");

            File.Copy(sourceImagePath, tempPath, true);

            bool ok = await WriteExifUserCommentAsync(tempPath, OppoExifMarker, token);
            if (!ok)
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                return sourceImagePath;
            }

            return tempPath;
        }
    }
}
