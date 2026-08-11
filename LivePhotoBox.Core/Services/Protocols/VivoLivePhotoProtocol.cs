// <summary>
// Vivo Live Photo protocol (X300 series and later).
// Real vivo X300 Ultra camera output (reverse-engineered 2026-08-08 from
// C:\Users\LengxiQwQ\Downloads\实况照片样本\vivo实况照片样本) is a live-photo
// container with GCamera + VCamera markers and a Container:Directory describing
// the appended video, plus (because the camera always shoots Ultra HDR) an
// embedded GainMap JPEG and the hdrgm namespace.
//
// DESIGN RULE (user directive 2026-08-08): the XMP metadata carries only
// live-photo-relevant fields; the ONE exception is HDR — if the source image
// already carries an Ultra HDR gain map, preserve it as-is (bytes are kept and
// the GainMap item + hdrgm are declared with the real byte length); if the
// source has no HDR, neither the gain map nor hdrgm is emitted — the container
// then has just Primary + MotionPhoto.
// The EXIF UserComment is a separate rule: it is reproduced in FULL (see below)
// because it functions as vivo's private recognition signature — Google V2
// files without it are NOT recognised by vivo Gallery.
//
// Binary layout (source WITH HDR):
//   [Primary JPEG] [GainMap JPEG] [MP4 video]
// Binary layout (source WITHOUT HDR):
//   [Primary JPEG] [MP4 video]
//
// XMP structure (real vivo.jpg, WITH HDR):
//   hdrgm namespace            http://ns.adobe.com/hdr-gain-map/1.0/
//   hdrgm:Version="1.0"
//   GCamera:MotionPhoto="1", MotionPhotoVersion="1",
//   GCamera:MotionPhotoPresentationTimestampUs="1450273"
//   VCamera:VMotionPhotoVersion="1", VMotionPhotoSource="1",
//   VCamera:VMediaKitVersion="1.0.0.9"
//   Container:Directory with THREE items:
//     Primary   — Item:Semantic="Primary"   Item:Mime="image/jpeg"  (NO Length/Padding)
//     GainMap   — Item:Semantic="GainMap"   Item:Mime="image/jpeg"  Item:Length=<gainMapBytes>
//     MotionPhoto — Item:Mime="video/mp4"   Item:Semantic="MotionPhoto"
//                  Item:Length=<videoBytes> Item:Padding="0"
// Used by: Vivo devices (X300 series and later).
// </summary>

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    // Vivo Live Photo (X300 series, single-file).
    // Extends Google Motion Photo V2 with VCamera identity fields, a non-empty
    // EXIF UserComment carrying the live-photo capture state, and — only when the
    // source image carries an Ultra HDR gain map — the hdrgm namespace plus a
    // GainMap Container item.
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

        // ── XMP templates ───────────────────────────────────────────────

        // Live-photo container body shared by both templates: Primary + MotionPhoto
        // items (Primary carries no Length/Padding, matching the real vivo file).
        private const string ContainerBody =
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"Primary\" Item:Mime=\"{2}\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Mime=\"{4}\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{0}\" Item:Padding=\"0\"/>" +
            "</rdf:li>";

        // XMP RDF template used when the source image carries an Ultra HDR gain map:
        // 3-item Container (Primary / GainMap / MotionPhoto) + hdrgm namespace.
        // The GainMap item declares the REAL embedded gain map byte length.
        // {0} = video payload size in bytes (Item:Length for MotionPhoto item).
        // {1} = presentation timestamp in microseconds (GCamera:MotionPhotoPresentationTimestampUs).
        // {2} = primary image MIME type (e.g. "image/jpeg").
        // {3} = gain map byte length (Item:Length for GainMap item).
        // {4} = video MIME type (e.g. "video/mp4", "video/quicktime").
        private const string RdfTemplateWithGainMap =
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " xmlns:VCamera=\"http://ns.vivo.com/photos/1.0/camera/\"" +
            " hdrgm:Version=\"1.0\"" +
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            " GCamera:MotionPhotoPresentationTimestampUs=\"{1}\"" +
            " VCamera:VMotionPhotoVersion=\"1\"" +
            " VCamera:VMotionPhotoSource=\"1\"" +
            " VCamera:VMediaKitVersion=\"1.0.0.9\">" +
            "<Container:Directory>" +
            "<rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"Primary\" Item:Mime=\"{2}\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{3}\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Mime=\"{4}\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{0}\" Item:Padding=\"0\"/>" +
            "</rdf:li>" +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "</rdf:Description>";

        // XMP RDF template used when the source image has NO Ultra HDR gain map:
        // 2-item Container (Primary / MotionPhoto), NO hdrgm namespace, NO GainMap.
        // Only live-photo metadata is emitted (the HDR exception does not apply).
        // {0} = video payload size in bytes. {1} = presentation timestamp.
        // {2} = primary MIME. {4} = video MIME. ({3} unused — kept for placeholder parity.)
        private const string RdfTemplateNoGainMap =
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
            ContainerBody +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "</rdf:Description>";

        // ── BuildXmpMetadata overrides ──────────────────────────────────

        // Build the XMP metadata bytes for Vivo Live Photo.
        // videoSize: Size of the appended video in bytes.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        // Build XMP metadata with presentation timestamp (microseconds).
        // Uses the inherited PrimaryMime property; no gain map is assumed (call the
        // overload below with the real length when the source carries an embedded
        // Ultra HDR gain map).
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => BuildXmpMetadata(videoSize, presentationTimestampUs, 0, PrimaryMime, "video/mp4");

        // Build XMP metadata with explicit format parameters (thread-safe).
        // The primaryPadding argument is accepted for interface compatibility but
        // is intentionally IGNORED — the real vivo file's Primary item carries no
        // Padding attribute, so we never emit one.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs,
            string primaryMime, string primaryPadding, string videoMime = "video/mp4")
            => BuildXmpMetadata(videoSize, presentationTimestampUs, 0, primaryMime, videoMime);

        // Build XMP metadata with the real Ultra HDR gain map byte length.
        // gainMapLength > 0  → 3-item XMP with hdrgm + GainMap item (HDR preserved as-is).
        // gainMapLength <= 0 → 2-item XMP (Primary + MotionPhoto), no hdrgm — the
        // source carries no HDR, so none is written.
        public byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs,
            long gainMapLength, string primaryMime, string videoMime = "video/mp4")
        {
            string template = gainMapLength > 0 ? RdfTemplateWithGainMap : RdfTemplateNoGainMap;
            return WrapXmp(
                string.Format(template, videoSize, presentationTimestampUs, primaryMime, gainMapLength, videoMime),
                Key);
        }

        // ── EXIF UserComment ────────────────────────────────────────────

        // Real vivo files carry a non-empty EXIF UserComment (≈590 B, raw UTF-8,
        // `\n`-separated field groups) recording the camera capture state. This
        // string functions as vivo's private recognition signature — a plain
        // Google V2 file (no UserComment) is NOT recognised by vivo Gallery, and
        // like OPPO's fixed oplus_ marker an incomplete value may not be read.
        // We therefore reproduce the FULL observed field structure from the real
        // X300 Ultra samples (verified 2026-08-08), not just the live-photo
        // subset. The per-capture state fields (weather, AEC, temperature, etc.)
        // are synthesised with neutral values — their exact content is not
        // recognition-relevant (every real capture differs); the live-photo
        // signature fields (multi-frame: 1 / ispap:1 / papproctime / module) are
        // reproduced exactly.
        //
        // {0} = capture processing time in "yyyy:MM:dd HH:mm:ss" format.
        private const string UserCommentTemplate =
            "filter: 2237; fileterIntensity: 0.000000; filterMask: 0; captureOrientation: 90;\n" +
            "niceRunStatus: 1002; hdrForward: 7; shaking: 0.000000; highlight: 1; motionR: 0; algolist: 0;\n" +
            "multi-frame: 1;\n" +
            "brp_mask: 0;\n" +
            "brp_del_th: 0.0000,0.0000;\n" +
            "brp_del_sen: 0.0000,0.0000;\n" +
            "delta:1;\n" +
            "bokeh:1;\n" +
            "ispap:1;\n" +
            "papproctime: {0};\n" +
            "module: photo;hw-remosaic: false;touch: (-1.0, -1.0);sceneMode: 13107200;cct_value: 0;AI_Scene: (-1, -1);aec_lux: 130.098;aec_lux_index: 0;albedo:  ;confidence:  ;motionLevel: -1;weatherinfo: weather: cloudy,icon:1,weatherInfo:100;temperature: 37;zeissColor: bright;";

        // Build the vivo live-photo UserComment string.
        private static string BuildUserComment()
            => string.Format(UserCommentTemplate, DateTime.Now.ToString("yyyy:MM:dd HH:mm:ss"));

        // UserComment marker re-injected on the final output (see
        // LivePhotoMergeRunnerService). Returned on every call so vivo Gallery
        // sees the live-photo signature matching a real capture.
        public override string? GetExifUserCommentMarker() => BuildUserComment();

        // Pre-process: inject the vivo live-photo UserComment into a temp copy of
        // the source image (used by the EditViewModel direct-save path which does not
        // re-inject via GetExifUserCommentMarker). Mirrors OppoLivePhotoProtocol.
        // If exiftool is unavailable the original path is returned and a warning is
        // logged; the file will still be a structurally valid V2-compatible Motion
        // Photo but may not be recognised by vivo Gallery.
        // sourceImagePath: Path to the source JPEG image.
        // workDir: Working directory for temporary file creation.
        // token: Cancellation token.
        public override async Task<string> PrepareImageAsync(
            string sourceImagePath, string workDir, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (!IsExifToolAvailable)
            {
                LogService.Merge(
                    "exiftool not found — vivo live-photo UserComment will not be injected. " +
                    "The output will be a valid Motion Photo but may not be recognised by vivo Gallery.",
                    Models.LogLevel.Warning);
                return sourceImagePath;
            }

            return await PrepareImageWithUserCommentAsync(
                sourceImagePath, workDir, "vivo", BuildUserComment(), token);
        }
    }
}
