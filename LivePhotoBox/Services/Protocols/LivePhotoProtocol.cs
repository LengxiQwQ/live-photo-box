using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    /// <summary>
    /// Abstract base for Live Photo packaging protocols.
    /// Each concrete protocol defines how XMP metadata is generated and optionally
    /// how the source image is pre-processed before the JPEG+video concatenation.
    /// </summary>
    public abstract class LivePhotoProtocol
    {
        /// <summary>Stable numeric id matching the ComboBox SelectedIndex in the UI.</summary>
        public abstract int Id { get; }

        /// <summary>Short identifier for logging / debugging.</summary>
        public abstract string Key { get; }

        /// <summary>Human-readable label (Chinese).</summary>
        public abstract string DisplayName { get; }

        /// <summary>Human-readable label (English).</summary>
        public abstract string DisplayNameEn { get; }

        /// <summary>
        /// Build the complete XMP XML bytes for the Live Photo APP1 segment.
        /// The returned bytes include the xpacket wrapper and are UTF-8 encoded.
        /// </summary>
        /// <param name="videoSize">Size of the appended video in bytes.</param>
        public abstract byte[] BuildXmpMetadata(long videoSize);

        /// <summary>
        /// Optional pre-processing on the source JPEG before it is combined with the video.
        /// Returns the filesystem path to use as the image source (the original path, or
        /// a temporary copy that the caller is responsible for cleaning up).
        /// The default implementation is a no-op (returns <paramref name="sourceImagePath"/>).
        /// </summary>
        public virtual Task<string> PrepareImageAsync(
            string sourceImagePath,
            string workDir,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(sourceImagePath);
        }

        // ── Protocol registry ──────────────────────────────────────────

        private static readonly LivePhotoProtocol[] _all =
        [
            new MicroVideoV1Protocol(),
            new MotionPhotoV2Protocol(),
            new OppoLivePhotoProtocol(),
        ];

        /// <summary>All registered protocols ordered by Id.</summary>
        public static LivePhotoProtocol[] All => _all;

        /// <summary>Look up a protocol by its <see cref="Id"/>.</summary>
        public static LivePhotoProtocol FromIndex(int index)
        {
            foreach (var p in _all)
            {
                if (p.Id == index) return p;
            }
            return _all[1]; // fallback → V2 (MotionPhoto)
        }

        // ── shared helpers ─────────────────────────────────────────────

        protected static readonly byte[] XmpHeaderBytes =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        /// <summary>
        /// Build a standard xpacket-wrapped XMP document with the given RDF body.
        /// </summary>
        protected static byte[] WrapXmp(string rdfDescription)
        {
            string xml = $"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                         $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
                         $"  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
                         $"    {rdfDescription}\n" +
                         $"  </rdf:RDF>\n" +
                         $"</x:xmpmeta>\n" +
                         $"<?xpacket end=\"w\"?>";
            return Encoding.UTF8.GetBytes(xml);
        }

        /// <summary>Whether exiftool is available on this system.</summary>
        protected static bool IsExifToolAvailable =>
            !string.IsNullOrEmpty(ExternalToolLocator.FindExifTool());

        /// <summary>
        /// Run exiftool to write an EXIF UserComment tag. Used by OPPO protocol
        /// to inject the <c>oplus_</c> gallery-recognition marker.
        /// Returns true on success, false if exiftool is unavailable or fails.
        /// </summary>
        protected static async Task<bool> WriteExifUserCommentAsync(
            string filePath, string comment, CancellationToken token)
        {
            var exifToolPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifToolPath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exifToolPath,
                Arguments = $"-overwrite_original -UserComment=\"{comment}\" \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return false;

                await process.WaitForExitAsync(token);

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    LogService.Combo(
                        $"exiftool UserComment write failed (exit {process.ExitCode}): {stderr.Trim()}",
                        Models.LogLevel.Warning);
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Combo(
                    $"exiftool UserComment write error: {ex.Message}",
                    Models.LogLevel.Warning);
                return false;
            }
        }
    }
}
