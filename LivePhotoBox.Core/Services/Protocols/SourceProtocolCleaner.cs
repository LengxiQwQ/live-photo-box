using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    /*
     * SourceProtocolCleaner.cs
     *
     * 合成端源协议标记清洗：剥离双文件源图片/视频携带的实况照片标记。
     * 只清双文件协议标记；单文件协议标记由拆分端清理（双文件源不可能携带）。
     *
     *   - Apple：图片 ContentIdentifier（Apple MakerNote）、视频配对键
     *     （content.identifier / live-photo / vitality）、实况时序元数据轨
     *     （ContentDescribes / 封面轨）
     *   - vivo ≤X200：JPEG 尾部 vivo{...}cameralbum!、MP4 vivoMediaExtInfo uuid box
     *   - 只在临时副本上操作，绝不修改用户源文件；返回的临时路径由调用方随工作区清理
     */
    public static class SourceProtocolCleaner
    {
        /// <summary>
        /// 清洗源图片：剥离苹果 ContentIdentifier 与 vivo ≤X200 JPEG 尾部标记。
        /// 返回清洗后的临时副本路径（调用方负责清理）；失败时抛出异常。
        /// </summary>
        public static async Task<string> CleanImageAsync(string imagePath, string workDir, CancellationToken token)
        {
            string ext = Path.GetExtension(imagePath).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "jpg";
            string tempPath = TempFileService.AllocateTempPath(workDir, "src_img", ext);
            try
            {
                File.Copy(imagePath, tempPath, overwrite: true);
                await CleanImageMarkersInPlaceAsync(tempPath, token);
                return tempPath;
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }
        }

        /// <summary>
        /// 清洗源视频：剥离 Apple 实况键与 mebx 轨、vivo ≤X200 uuid box。
        /// 无命中时返回原路径；命中时返回清洗后的临时副本路径。
        /// </summary>
        public static async Task<string> CleanVideoAsync(string videoPath, string workDir, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!FileContainsAny(videoPath,
                    "content.identifier", "live-photo", "vitality", "vivoMediaExtInfo",
                    "openharmony"))
                return videoPath;

            string ext = Path.GetExtension(videoPath).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "mp4";
            string tempPath = TempFileService.AllocateTempPath(workDir, "src_vid", ext);
            try
            {
                File.Copy(videoPath, tempPath, overwrite: true);
                CleanVideoMarkersInPlace(tempPath);
                return tempPath;
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }
        }

        /// <summary>
        /// 就地清洗图片中的双文件协议标记：vivo ≤X200 JPEG 尾部 + Apple ContentIdentifier。
        /// 供拆分端对已提取的临时图片调用（防脏源：旧文件/第三方工具可能残留）。
        /// </summary>
        public static async Task CleanImageMarkersInPlaceAsync(string path, CancellationToken token)
        {
            bool isHeic = path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
                          path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
            if (isHeic)
            {
                // HEIC: no vivo JPEG tail; exiftool rewrite would break Huawei merged structure
                // and prevent later XMP writes. Strip Apple live-photo entries byte-level instead
                // (container untouched); safe no-op for Huawei HEIC without Apple MakerNote.
                AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(path, out _);
                return;
            }

            // vivo X200 JPEG tail: vivo{...}cameralbum! (binary truncate)
            StripVivoJpegTail(path);

            // OPPO / vivo X300 EXIF UserComment signatures (oplus_ / multi-frame)
            await ClearOppoVivoUserCommentAsync(path, token);

            // Apple ContentIdentifier (pairing UUID in MakerNote -> clear)
            await LivePhotoRepairService.RunExifToolAsync(
                token, "-overwrite_original", "-ContentIdentifier=", path);
        }


        /// <summary>
        /// 就地清洗视频中的双文件协议标记：Apple 实况配对键 + 实况时序元数据轨 +
        /// vivo ≤X200 uuid box。供拆分端对已提取的临时视频调用。
        /// </summary>
        public static void CleanVideoMarkersInPlace(string path)
        {
            // Apple 实况配对键（content.identifier / live-photo / vitality）
            Mp4MdtaKeyStripper.TryStripMdtaKeys(path, ShouldStripAppleKey, out _);
            // Apple 实况时序元数据轨（ContentDescribes / 封面轨）
            Mp4MdtaKeyStripper.TryStripMebxTracks(path, out _);
            // vivo ≤X200 uuid box
            Mp4MdtaKeyStripper.TryStripUuidBox(path, "vivoMediaExtInfo", out _);
            // Huawei single-file keys (com.openharmony.*, incl. 漏too=openharmony6 patch)
            Mp4MdtaKeyStripper.TryStripHuaweiKeys(path, out _);
            // Huawei live-photo timed metadata track
            Mp4MdtaKeyStripper.TryStripTracks(path, ["com.openharmony.timed_metadata.movingphoto"], out _);
        }

        private static bool ShouldStripAppleKey(string name, string value)
            => name.StartsWith("com.apple.quicktime.content.identifier", StringComparison.OrdinalIgnoreCase)
            || name.Contains("live-photo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vitality", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 清空 OPPO（oplus_ 前缀）与 vivo X300+（multi-frame 签名）的 EXIF UserComment，
        /// 避免源实况照片的厂商识别签名残留到合成输出。先做字节扫描，命中才读/清。
        /// </summary>
        private static async Task ClearOppoVivoUserCommentAsync(string path, CancellationToken token)
        {
            try
            {
                if (!FileContainsAny(path, "oplus_", "multi-frame"))
                    return;

                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath)) return;

                string? currentValue = null;
                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-UserComment");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add("-S");
                psi.ArgumentList.Add(path);
                using (var process = Process.Start(psi))
                {
                    if (process == null) return;
                    currentValue = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                }

                if (string.IsNullOrEmpty(currentValue)) return;
                bool isOppo = currentValue.StartsWith("oplus_", StringComparison.OrdinalIgnoreCase);
                bool isVivo = currentValue.Contains("multi-frame", StringComparison.OrdinalIgnoreCase);
                if (!isOppo && !isVivo) return;

                await LivePhotoRepairService.RunExifToolAsync(
                    token, "-overwrite_original", "-UserComment=", path);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }

        // vivo ≤X200 JPEG 尾部：从最后一个 vivo{ 到文件末尾整体截断。
        // 新版样本的 cameralbum! 之后还有 ID、FF FF FF FF 与 11 字节签名，
        // 不能再用 "以 cameralbum! 结尾" 判断。
        private static void StripVivoJpegTail(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 16) return;
                string text = Encoding.ASCII.GetString(data);
                int idx = text.LastIndexOf("vivo{", StringComparison.Ordinal);
                int markerIdx = idx > 0
                    ? text.IndexOf("cameralbum!", idx, StringComparison.Ordinal)
                    : -1;
                if (idx > 0 && markerIdx >= 0)
                {
                    byte[] trimmed = new byte[idx];
                    Array.Copy(data, 0, trimmed, 0, idx);
                    File.WriteAllBytes(path, trimmed);
                }
            }
            catch
            {
                // 截断失败不阻断（vivo 尾标清理是 best-effort）
            }
        }

        // 全文件 ASCII 扫描（XMP/EXIF/keys 可能在文件头也可能在文件尾的 moov 区，必须扫全量）。
        private static bool FileContainsAny(string path, params string[] needles)
        {
            try
            {
                byte[] buf = File.ReadAllBytes(path);
                string text = Encoding.ASCII.GetString(buf);
                foreach (string needle in needles)
                {
                    if (text.Contains(needle, StringComparison.Ordinal))
                        return true;
                }
            }
            catch
            {
                // 读失败按"有标记"处理，让上层走剥离路径重试
                return true;
            }
            return false;
        }
    }
}
