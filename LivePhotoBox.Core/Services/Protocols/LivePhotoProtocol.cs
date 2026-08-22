/*
 * LivePhotoProtocol.cs
 *
 * Live Photo 各打包协议的抽象基类，提供通用契约与共享工具。
 * 每个具体子类定义特定平台的 XMP 元数据格式与可选图片预处理。
 *
 *   - Motion Photo Fusion (Id=0)：V2 + OPPO + VIVO + Samsung
 *   - Google MicroVideo V1 (Id=1，已废弃)
 *   - Google Motion Photo V2 (Id=2)
 *   - OPPO/OnePlus O-Live Photo (Id=3)
 *   - VIVO Live Photo (Id=4)
 *   - Samsung Motion Photo (Id=5)
 *   - HUAWEI Moving Photo (Id=6)
 */

using System;
using System.IO;
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
        // Cached app version string (3-part, e.g. "2.2.1") read from the entry
        // assembly (or Core itself as fallback). Single source: XmpMarkerService.
        private static readonly string _appVersion = XmpMarkerService.AppVersion;

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
        // 返回: UTF-8 encoded XMP bytes including xpacket wrapper markers.
        public abstract byte[] BuildXmpMetadata(long videoSize);

        // Build XMP metadata with a specified presentation timestamp (microseconds).
        // This timestamp tells viewers (Google Photos, OPPO Gallery, etc.) where in the
        // video timeline the cover / still image was taken.
        // The default implementation ignores the timestamp for backward compatibility;
        // override in protocol subclasses that support presentation timestamps.
        // videoSize: Size of the appended video in bytes.
        // presentationTimestampUs: The timestamp in microseconds of the selected cover frame.
        // 返回: UTF-8 encoded XMP bytes including xpacket wrapper markers.
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

        // 协议预处理公共实现：在 workDir 中分配唯一临时文件（GUID 后缀），
        // 复制源图并写入 EXIF UserComment 标记。exiftool 写入失败时回退返回
        // 源图路径并尽力删除临时文件。OPPO / vivo / Fusion 的 PrepareImageAsync
        // 复用此方法，避免三处重复且命名各异的临时文件逻辑。
        // sourceImagePath: 源 JPEG 路径。
        // workDir: 临时工作目录。
        // hint: 临时文件名提示（如 "oppo"/"vivo"/"fusion"，仅用于排障）。
        // marker: 要写入的 EXIF UserComment 内容。
        // token: 取消令牌。
        protected static async Task<string> PrepareImageWithUserCommentAsync(
            string sourceImagePath,
            string workDir,
            string hint,
            string marker,
            CancellationToken token)
        {
            // 临时副本扩展名必须跟随源图片的实际容器（P1-3）：exiftool 按扩展名选择
            // 写入器模块，若 HEIC 源被复制成 .jpg，exiftool 会报 "Not a valid JPG" 并
            // 拒绝写入 UserComment（vivo 的 HEIC 源因此丢失 multi-frame 签名）。
            string sourceExt = HeicConverterService.IsHeicFile(sourceImagePath)
                ? "heic"
                : "jpg";
            string tempPath = TempFileService.AllocateTempPath(workDir, hint, sourceExt);
            try
            {
                File.Copy(sourceImagePath, tempPath, true);
                bool ok = await WriteExifUserCommentAsync(tempPath, marker, token);
                if (!ok)
                {
                    try { File.Delete(tempPath); } catch { /* best-effort */ }
                    return sourceImagePath;
                }
                return tempPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                return sourceImagePath;
            }
        }

        // EXIF UserComment marker that should be written to the final output
        // as a post-processing step. Returns null for protocols that don't need one.
        // OPPO and Fusion override this to return "oplus_10485792".
        public virtual string? GetExifUserCommentMarker() => null;

        // ── Protocol registry ──────────────────────────────────────────

        // All registered protocol instances, ordered by Id.
        private static readonly LivePhotoProtocol[] _all =
        [
            new MotionPhotoFusionProtocol(),     // 0 — fusion (V2 + OPPO + VIVO + Samsung)
            new MicroVideoV1Protocol(),          // 1
            new MotionPhotoV2Protocol(),         // 2 — default
            new OppoLivePhotoProtocol(),         // 3
            new VivoLivePhotoProtocol(),         // 4
            new SamsungMotionPhotoProtocol(),    // 5
            new HuaweiMovingPhotoProtocol(),     // 6 — HUAWEI Moving Photo
        ];

        // All registered protocols ordered by Id.
        public static LivePhotoProtocol[] All => _all;

        // Look up a protocol by its <see cref="Id"/>.
        // index: The protocol index (matches <see cref="Id"/>).
        // 返回: The matching <see cref="LivePhotoProtocol"/> instance, or MotionPhoto V2 as fallback if not found.
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
        // 返回: UTF-8 encoded XMP bytes including xpacket wrapper markers.
        protected static byte[] WrapXmp(string rdfDescription)
        {
            // Build the LivePhotoBox tracking marker: namespace + timestamp + version only.
            // Action/Protocol live in the dc:subject history entries (structured details).
            string marker = " xmlns:LivePhotoBox=\"https://github.com/LengxiQwQ/live-photo-box\"" +
                           $" LivePhotoBox:Timestamp=\"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\"";
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
        // to inject the <c>oplus_</c> gallery-recognition marker, and by vivo to
        // write its multi-line capture-state signature.
        // Returns true on success, false if exiftool is unavailable or fails.
        // filePath: Path to the image file to modify.
        // comment: The UserComment string value to write.
        // token: Cancellation token.
        // 返回: True if the EXIF write succeeded; false if exiftool is unavailable or the write failed.
        public static async Task<bool> WriteExifUserCommentAsync(
            string filePath, string comment, CancellationToken token)
        {
            if (string.IsNullOrEmpty(ExternalToolLocator.FindExifTool())) return false;

            string? tempValuePath = null;
            try
            {
                // The value is passed via a temp file (-UserComment<=file) rather
                // than inline (-UserComment=...). RunExifToolAsync feeds arguments
                // through exiftool's stdin pipe one per line, so a multi-line value
                // (vivo's UserComment uses '\n' between field groups) would be split
                // into separate arguments and truncated at the first newline.
                // Reading the value from a file keeps the entire string — newlines
                // and UTF-8 content — intact.
                tempValuePath = Path.Combine(
                    Path.GetTempPath(), $"_lpbx_uc_{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(
                    tempValuePath, comment, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), token);

                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-overwrite_original",
                    $"-UserComment<={tempValuePath}",
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
            finally
            {
                if (tempValuePath != null)
                {
                    try { File.Delete(tempValuePath); } catch { /* best-effort */ }
                }
            }
        }

    }
}
