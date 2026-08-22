/*
 * VivoLivePhotoProtocol.cs
 *
 * vivo Live Photo 协议（X300 系列及以后，真机逆向，用于 vivo 设备）。
 * 容器含 GCamera + VCamera 标记、Container:Directory 描述追加的视频；
 * 因相机总是拍 Ultra HDR，源图通常内嵌 GainMap JPEG 与 hdrgm 命名空间。
 *
 *   - 设计规则（用户 2026-08-08 指令）：XMP 只写实况相关字段；唯一例外是 HDR——
 *     源图已有 Ultra HDR gain map 则原样保留（GainMap item + hdrgm 按真实字节长度声明），
 *     否则既不输出 gain map 也不输出 hdrgm，容器只有 Primary + MotionPhoto
 *   - EXIF UserComment 整段复刻：它是 vivo 私有识别签名，缺它 vivo 相册不识别 Google V2 文件
 *   - 二进制布局：有 HDR 时 [Primary JPEG] [GainMap JPEG] [MP4 视频]；无 HDR 时 [Primary JPEG] [MP4 视频]
 */

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
                string.Format(template, videoSize, presentationTimestampUs, primaryMime, gainMapLength, videoMime));
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
