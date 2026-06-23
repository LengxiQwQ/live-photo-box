using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    /// <summary>
    /// OPPO/OnePlus O-Live Photo (ColorOS / OxygenOS).
    ///
    /// Extends Google Motion Photo V2 with:
    ///   1. OpCamera XMP namespace — 4 proprietary fields
    ///      (VideoLength gives the PURE mp4 size, excluding the OnePlus trailer).
    ///   2. EXIF UserComment marker <c>oplus_10485792</c> — required by OPPO Gallery
    ///      for recognition; the numeric suffix is the max video size (10 MB).
    ///
    /// Binary layout (OPPO writes a trailer AFTER the mp4):
    ///   [JPEG including GainMap]  [MP4 video]  [OnePlus trailer ~846 KB]
    /// GContainer.Item[2].Length covers video + trailer;
    /// OpCamera.VideoLength covers the pure mp4 only.
    /// </summary>
    public sealed class OppoLivePhotoProtocol : LivePhotoProtocol
    {
        public override int Id => 2;
        public override string Key => "OppoLivePhoto";
        public override string DisplayName => "OPPO/OnePlus Live Photo";
        public override string DisplayNameEn => "OPPO/OnePlus Live Photo";

        /// <summary>
        /// EXIF UserComment value that OPPO Gallery checks for live-photo recognition.
        /// Format: oplus_&lt;max-video-bytes&gt;.  Observed values:
        ///   8388608  (8 MB)  — older ColorOS
        ///   10485792 (10 MB) — OnePlus Ace 6 / ColorOS 15
        /// </summary>
        private const string OppoExifMarker = "oplus_10485792";

        /// <summary>
        /// Pure (trailer-free) video length for OPPO.  Since we are generating a file
        /// from scratch we do NOT append a OnePlus trailer — our video is exactly the
        /// mp4 bytes.  Therefore OpCamera:VideoLength equals GContainer:Item:Length,
        /// and both represent the actual video payload.
        /// </summary>
        private static string RdfTemplate(long videoSize) =>
            "<rdf:Description rdf:about=\"\"" +
            " xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"" +
            " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
            " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
            " xmlns:OpCamera=\"http://ns.oplus.com/photos/1.0/camera/\"" +
            " GCamera:MotionPhoto=\"1\"" +
            " GCamera:MotionPhotoVersion=\"1\"" +
            $" GCamera:MotionPhotoPresentationTimestampUs=\"0\"" +
            $" OpCamera:MotionPhotoPrimaryPresentationTimestampUs=\"0\"" +
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

        public override byte[] BuildXmpMetadata(long videoSize)
            => WrapXmp(RdfTemplate(videoSize));

        /// <summary>
        /// Pre-process: inject <c>oplus_10485792</c> into the EXIF UserComment
        /// so OPPO Gallery recognises the output as a valid O-Live Photo.
        ///
        /// Works on a temp copy of the source image — the caller is responsible
        /// for deleting it after use (the path differs from <paramref name="sourceImagePath"/>).
        /// If exiftool is unavailable the original path is returned and a warning is logged;
        /// the file will still be a structurally valid Motion Photo (Google-compatible)
        /// but may not animate in OPPO Gallery.
        /// </summary>
        public override async Task<string> PrepareImageAsync(
            string sourceImagePath, string workDir, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (!IsExifToolAvailable)
            {
                LogService.Combo(
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
                // Don't leave a stale temp file on failure; fall back to original
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                return sourceImagePath;
            }

            return tempPath;
        }
    }
}
