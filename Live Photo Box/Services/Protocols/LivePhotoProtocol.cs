// <summary>
// Live Photo Box — Live Photo Protocol base class.
// Provides the abstract contract and shared utilities for all live photo packaging
// protocols. Each concrete subclass defines the XMP metadata format and optional
// image pre-processing for a specific platform.
// Supported protocol implementations:
//   - Motion Photo Fusion (Id=0) — V2 + OPPO + VIVO + Samsung
//   - Google MicroVideo V1 (deprecated, Id=1)
//   - Google Motion Photo V2 (Id=2)
//   - OPPO/OnePlus O-Live Photo (Id=3)
//   - VIVO Live Photo (Id=4)
//   - Samsung Motion Photo (Id=5)
//   - HUAWEI Moving Photo (Id=6)
// </summary>

using System;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    // Abstract base for Live Photo packaging protocols.
    // Each concrete protocol defines how XMP metadata is generated and optionally
    // how the source image is pre-processed before the JPEG+video concatenation.
    public abstract class LivePhotoProtocol
    {
        // Cached app version string read from the entry assembly.
        private static readonly string _appVersion = App.AppVersion;

        // Stable numeric id matching the ComboBox SelectedIndex in the UI.
        public abstract int Id { get; }

        // Short identifier for logging / debugging.
        public abstract string Key { get; }

        // Human-readable label (Chinese).
        public abstract string DisplayName { get; }

        // Human-readable label (English).
        public abstract string DisplayNameEn { get; }

        // Build the complete XMP XML bytes for the Live Photo APP1 segment.
        // The returned bytes include the xpacket wrapper and are UTF-8 encoded.
        // videoSize: Size of the appended video in bytes.
        // è¿å: UTF-8 encoded XMP bytes including xpacket wrapper markers.
        public abstract byte[] BuildXmpMetadata(long videoSize);

        // Build XMP metadata with a specified presentation timestamp (microseconds).
        // This timestamp tells viewers (Google Photos, OPPO Gallery, etc.) where in the
        // video timeline the cover / still image was taken.
        // The default implementation ignores the timestamp for backward compatibility;
        // override in protocol subclasses that support presentation timestamps.
        // videoSize: Size of the appended video in bytes.
        // presentationTimestampUs: The timestamp in microseconds of the selected cover frame.
        // è¿å: UTF-8 encoded XMP bytes including xpacket wrapper markers.
        public virtual byte[] BuildXmpMetadata(long videoSize, long presentationTimestampUs)
            => BuildXmpMetadata(videoSize);

        // Optional pre-processing on the source JPEG before it is combined with the video.
        // Returns the filesystem path to use as the image source (the original path, or
        // a temporary copy that the caller is responsible for cleaning up).
        // The default implementation is a no-op (returns <paramref name="sourceImagePath"/>).
        public virtual Task<string> PrepareImageAsync(
            string sourceImagePath,
            string workDir,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(sourceImagePath);
        }

        // ── Protocol registry ──────────────────────────────────────────

        // All registered protocol instances, ordered by Id.
        private static readonly LivePhotoProtocol[] _all =
        [
            new MotionPhotoFusionProtocol(),  // 0 — fusion (V2 + OPPO + VIVO + Samsung)
            new MicroVideoV1Protocol(),       // 1
            new MotionPhotoV2Protocol(),      // 2 — default
            new OppoLivePhotoProtocol(),      // 3
            new VivoLivePhotoProtocol(),     // 4
            new SamsungMotionPhotoProtocol(), // 5
            new HuaweiMovingPhotoProtocol(),  // 6
        ];

        // All registered protocols ordered by Id.
        public static LivePhotoProtocol[] All => _all;

        // Look up a protocol by its <see cref="Id"/>.
        // index: The protocol index (matches <see cref="Id"/>).
        // è¿å: The matching <see cref="LivePhotoProtocol"/> instance, or MotionPhoto V2 as fallback if not found.
        public static LivePhotoProtocol FromIndex(int index)
        {
            foreach (var p in _all)
            {
                if (p.Id == index) return p;
            }
            // Fallback: find V2 by scanning (robust against reordering).
            foreach (var p in _all)
            {
                if (p.Id == 2) return p; // V2 (MotionPhoto)
            }
            return _all[2]; // last resort
        }

        // ── shared helpers ─────────────────────────────────────────────

        // ASCII-encoded byte sequence for the XMP APP1 identifier header.
        // This is the standard XMP namespace identifier written immediately after
        // the JPEG APP1 marker (0xFFE1) and length field in the EXIF segment.
        protected static readonly byte[] XmpHeaderBytes =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        // Build a standard xpacket-wrapped XMP document with the given RDF body.
        // Injects a LivePhotoBox tracking namespace so the app can later identify
        // files it generated (via <see cref="LivePhotoSplitService.ContainsLivePhotoMarker"/>).
        // The extra XMP attributes do NOT affect live-photo detection on any platform
        // (Windows 11, Google Photos, Xiaomi, OPPO, Samsung, Apple) — XMP parsers
        // silently ignore namespaces they don't recognise.
        // rdfDescription: The rdf:Description XML element.
        // protocolKey: Identifier of the protocol that generated this XMP (e.g. "MotionPhotoV2").If null or empty, the Protocol attribute is omitted.
        // è¿å: UTF-8 encoded XMP bytes including xpacket wrapper markers.
        protected static byte[] WrapXmp(string rdfDescription, string? protocolKey = null)
        {
            // Build the LivePhotoBox tracking marker with app version and optional protocol key
            string marker = " xmlns:LivePhotoBox=\"https://github.com/LengxiQwQ/live-photo-box\"" +
                           $" LivePhotoBox:Action=\"Merge\"";
            if (!string.IsNullOrEmpty(protocolKey))
                marker += $" LivePhotoBox:Protocol=\"{protocolKey}\"";
            // Only add version on the first call to avoid the reflection cost each time
            marker += $" LivePhotoBox:Version=\"{_appVersion}\"";

            // Determine where to inject the marker:
            //   - Self-closing tag (V1):  <rdf:Description ... attr="val"/>  → insert before />
            //   - Regular tag (V2/OPPO):  <rdf:Description ... attr="val">   → insert before >
            //
            // NOTE: Must search BEFORE the first '>', NOT globally for "/>".
            // The rdfDescription may contain child elements (e.g. <Container:Item.../>)
            // whose "/>" would be matched by IndexOf("/>") first, causing the marker
            // to be injected on the wrong element.
            int tagEnd;
            int firstCloseBracket = rdfDescription.IndexOf('>');
            if (firstCloseBracket >= 1 && rdfDescription[firstCloseBracket - 1] == '/')
            {
                // Self-closing Description tag (MicroVideo V1) — inject before />
                tagEnd = firstCloseBracket - 1;
            }
            else
            {
                // Regular Description opening tag (V2/OPPO) — inject before >
                tagEnd = firstCloseBracket;
            }
            string marked = tagEnd >= 0
                ? rdfDescription.Insert(tagEnd, marker)
                : rdfDescription;

            // Build the complete XMP document with xpacket wrapper markers
            string xml = $"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                         $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
                         $"  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
                         $"    {marked}\n" +
                         $"  </rdf:RDF>\n" +
                         $"</x:xmpmeta>\n" +
                         $"<?xpacket end=\"w\"?>";
            return Encoding.UTF8.GetBytes(xml);
        }

        // Whether exiftool is available on this system.
        protected static bool IsExifToolAvailable =>
            !string.IsNullOrEmpty(ExternalToolLocator.FindExifTool());

        // Run exiftool to write an EXIF UserComment tag. Used by OPPO protocol
        // to inject the <c>oplus_</c> gallery-recognition marker.
        // Returns true on success, false if exiftool is unavailable or fails.
        // filePath: Path to the image file to modify.
        // comment: The UserComment string value to write.
        // token: Cancellation token.
        // è¿å: True if the EXIF write succeeded; false if exiftool is unavailable or the write failed.
        protected static async Task<bool> WriteExifUserCommentAsync(
            string filePath, string comment, CancellationToken token)
        {
            // Check if exiftool is available on the system path
            if (string.IsNullOrEmpty(ExternalToolLocator.FindExifTool())) return false;

            try
            {
                // 使用 RunExifToolAsync（stdin 管道，UTF-8 编码），兼容所有语言文件名
                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-overwrite_original",
                    $"-UserComment={comment}",
                    filePath);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Merge(
                    $"exiftool UserComment write error: {ex.Message}",
                    Models.LogLevel.Warning);
                return false;
            }
        }

    }
}
