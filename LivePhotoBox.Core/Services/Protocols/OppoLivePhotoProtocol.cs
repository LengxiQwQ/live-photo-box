// <summary>
// OPPO/OnePlus O-Live Photo protocol parser.
// Extends Google Motion Photo V2 with OPPO-specific XMP attributes (OpCamera namespace)
// and an EXIF UserComment marker (oplus_10485792) required by OPPO Gallery recognition.
// Binary layout: [JPEG + GainMap] + [MP4 video] + [optional OnePlus trailer].
// Protocol version: O-Live Photo V2 (GCamera + Container + OpCamera namespaces).
// Used by: OPPO ColorOS and OnePlus OxygenOS devices.
// </summary>

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    // OPPO/OnePlus O-Live Photo (ColorOS / OxygenOS).
    // Extends Google Motion Photo V2 with:
    // 1. OpCamera XMP namespace — 4 proprietary fields
    // (VideoLength gives the PURE mp4 size, excluding the OnePlus trailer).
    // 2. EXIF UserComment marker <c>oplus_10485792</c> — required by OPPO Gallery
    // for recognition; the numeric suffix is the max video size (10 MB).
    // Binary layout (OPPO writes a trailer AFTER the mp4):
    // [JPEG including GainMap]  [MP4 video]  [OnePlus trailer ~846 KB]
    // GContainer.Item[2].Length covers video + trailer;
    // OpCamera.VideoLength covers the pure mp4 only.
    public sealed class OppoLivePhotoProtocol : LivePhotoProtocol
    {
        // Protocol index for UI ComboBox selection (2 = OPPO Live Photo).
        public override int Id => 3;

        // Short identifier for logging and protocol identification.
        public override string Key => "OppoLivePhoto";

        // Human-readable label in Chinese for UI display.
        public override string DisplayName => "OPPO/OnePlus Live Photo";

        // Human-readable label in English for UI display.
        public override string DisplayNameEn => "OPPO/OnePlus Live Photo";

        public override string? GetExifUserCommentMarker() => OppoExifMarker;

        // EXIF UserComment value that OPPO Gallery checks for live-photo recognition.
        // Format: oplus_&lt;max-video-bytes&gt;.  Observed values:
        // 8388608  (8 MB)  — older ColorOS
        // 10485792 (10 MB) — OnePlus Ace 6 / ColorOS 15
        private const string OppoExifMarker = "oplus_10485792";

        // Pure (trailer-free) video length for OPPO.  Since we are generating a file
        // from scratch we do NOT append a OnePlus trailer — our video is exactly the
        // mp4 bytes.  Therefore OpCamera:VideoLength equals GContainer:Item:Length,
        // and both represent the actual video payload.
        private static string RdfTemplate(long videoSize, long presentationTimestampUs) =>
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " xmlns:OpCamera=\"http://ns.oplus.com/photos/1.0/camera/\"" +
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            $" GCamera:MotionPhotoPresentationTimestampUs=\"{presentationTimestampUs}\"" +
            $" OpCamera:MotionPhotoPrimaryPresentationTimestampUs=\"{presentationTimestampUs}\"" +
            $" OpCamera:MotionPhotoOwner=\"oplus\"" +
            $" OpCamera:OLivePhotoVersion=\"2\"" +
            $" OpCamera:VideoLength=\"{videoSize}\">" +
            "<Container:Directory>" +
            "<rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            $"<Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{videoSize}\" Item:Padding=\"0\"/>" +
            "</rdf:li>" +
            "</rdf:Seq>" +
            "</Container:Directory>" +
            "</rdf:Description>";

        // Build the XMP metadata bytes for OPPO O-Live Photo.
        // Generates an rdf:Description with both GCamera/Container namespaces (Motion Photo V2)
        // and the OPPO-specific OpCamera namespace with OLivePhoto fields.
        // videoSize: Size of the appended MP4 video in bytes (trailer-free pure video size).
        // è¿å: UTF-8 encoded XMP bytes wrapped in xpacket markers.
        public override byte[] BuildXmpMetadata(long videoSize)
            => BuildXmpMetadata(videoSize, 0);

        // Build XMP metadata with presentation timestamp (microseconds).
        // OPPO uses two timestamp fields: MotionPhotoPresentationTimestampUs (cover position)
        // and OpCamera:MotionPhotoPrimaryPresentationTimestampUs (primary photo position).
        // We set both to the selected frame's timestamp since we're replacing the cover.
        // videoSize: Size of the appended MP4 video in bytes (trailer-free pure video size).
        // presentationTimestampUs: Timestamp in microseconds of the selected frame.
        // è¿å: UTF-8 encoded XMP bytes wrapped in xpacket markers.
        public override byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => WrapXmp(RdfTemplate(videoSize, presentationTimestampUs), Key);

        // Pre-process: inject <c>oplus_10485792</c> into the EXIF UserComment
        // so OPPO Gallery recognises the output as a valid O-Live Photo.
        // Works on a temp copy of the source image — the caller is responsible
        // for deleting it after use (the path differs from <paramref name="sourceImagePath"/>).
        // If exiftool is unavailable the original path is returned and a warning is logged;
        // the file will still be a structurally valid Motion Photo (Google-compatible)
        // but may not animate in OPPO Gallery.
        // sourceImagePath: Path to the source JPEG image.
        // workDir: Working directory for temporary file creation.
        // token: Cancellation token.
        // è¿å: Path to the processed image. If exiftool is available and succeeds, returns atemporary copy with the OPPO EXIF marker injected. Otherwise returns the original path.
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
                $"{Path.GetFileNameWithoutExtension(sourceImagePath)}_oppo_tmp_{Guid.NewGuid():N}.jpg");

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
