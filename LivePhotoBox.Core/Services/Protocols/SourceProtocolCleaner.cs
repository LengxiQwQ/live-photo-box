using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services.Protocols
{
    /*
     * SourceProtocolCleaner.cs
     *
     * 鍚堟垚绔簮鍗忚鏍囪娓呮礂锛氬墺绂诲弻鏂囦欢婧愬浘鐗?瑙嗛鎼哄甫鐨勫疄鍐电収鐗囨爣璁般€?
     * 鍙竻鍙屾枃浠跺崗璁爣璁帮紱鍗曟枃浠跺崗璁爣璁扮敱鎷嗗垎绔竻鐞嗭紙鍙屾枃浠舵簮涓嶅彲鑳芥惡甯︼級銆?
     *
     *   - Apple锛氬浘鐗?ContentIdentifier锛圓pple MakerNote锛夈€佽棰戦厤瀵归敭
     *     锛坈ontent.identifier / live-photo / vitality锛夈€佸疄鍐垫椂搴忓厓鏁版嵁杞?
     *     锛圕ontentDescribes / 灏侀潰杞級
     *   - vivo 鈮200锛欽PEG 灏鹃儴 vivo{...}cameralbum!銆丮P4 vivoMediaExtInfo uuid box
     *   - 鍙湪涓存椂鍓湰涓婃搷浣滐紝缁濅笉淇敼鐢ㄦ埛婧愭枃浠讹紱杩斿洖鐨勪复鏃惰矾寰勭敱璋冪敤鏂归殢宸ヤ綔鍖烘竻鐞?
     */
    public static class SourceProtocolCleaner
    {
        /// <summary>
        /// 娓呮礂婧愬浘鐗囷細鍓ョ鑻规灉 ContentIdentifier 涓?vivo 鈮200 JPEG 灏鹃儴鏍囪銆?
        /// 杩斿洖娓呮礂鍚庣殑涓存椂鍓湰璺緞锛堣皟鐢ㄦ柟璐熻矗娓呯悊锛夛紱澶辫触鏃舵姏鍑哄紓甯搞€?
        /// </summary>
        public static async Task<string> CleanImageAsync(string imagePath, string workDir, CancellationToken token)
        {
            string ext = Path.GetExtension(imagePath).TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "jpg";
            string tempPath = TempFileService.AllocateTempPath(workDir, "src_img", ext);
            try
            {
                File.Copy(imagePath, tempPath, overwrite: true);
                CleanImageMarkersInPlace(tempPath, token);
                return tempPath;
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }
        }

        /// <summary>
        /// 娓呮礂婧愯棰戯細鍓ョ Apple 瀹炲喌閿笌 mebx 杞ㄣ€乿ivo 鈮200 uuid box銆?
        /// 鏃犲懡涓椂杩斿洖鍘熻矾寰勶紱鍛戒腑鏃惰繑鍥炴竻娲楀悗鐨勪复鏃跺壇鏈矾寰勩€?
        /// </summary>
        public static async Task<string> CleanVideoAsync(string videoPath, string workDir, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!FileContainsAny(videoPath,
                    "content.identifier", "live-photo", "vitality", "vivoMediaExtInfo"))
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
        /// 灏卞湴娓呮礂鍥剧墖涓殑鍙屾枃浠跺崗璁爣璁帮細vivo 鈮200 JPEG 灏鹃儴 + Apple ContentIdentifier銆?
        /// 渚涙媶鍒嗙瀵瑰凡鎻愬彇鐨勪复鏃跺浘鐗囪皟鐢紙闃茶剰婧愶細鏃ф枃浠?绗笁鏂瑰伐鍏峰彲鑳芥畫鐣欙級銆?
        /// </summary>
                public static void CleanImageMarkersInPlace(string path, CancellationToken token)
        {
            // vivo \u2265X200 JPEG 灏鹃儴锛歷ivo{...}cameralbum!锛堜簩杩涘埗鎴柇锛?
            StripVivoJpegTail(path);

            // Apple ContentIdentifier锛圡akerNote 閲岀殑閰嶅 UUID 鈫?娓呯┖锛?
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (Interop.NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out string? error))
                {
                    File.WriteAllBytes(path, data);
                }
                else
                {
                    LogService.Warn($"Apple MakerNote strip failed: {error}", source: LogSource.Split);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to read/write image for cleaning: {ex.Message}", source: LogSource.Split);
            }
        }

        /// <summary>
        /// 灏卞湴娓呮礂瑙嗛涓殑鍙屾枃浠跺崗璁爣璁帮細Apple 瀹炲喌閰嶅閿?+ 瀹炲喌鏃跺簭鍏冩暟鎹建 +
        /// vivo 鈮200 uuid box銆備緵鎷嗗垎绔宸叉彁鍙栫殑涓存椂瑙嗛璋冪敤銆?
        /// </summary>
        public static void CleanVideoMarkersInPlace(string path)
        {
            // Apple 瀹炲喌閰嶅閿紙content.identifier / live-photo / vitality锛?
            Mp4MdtaKeyStripper.TryStripMdtaKeys(path, 
                ["com.apple.quicktime.content.identifier"], 
                ["live-photo", "vitality"], 
                [], 
                ShouldStripAppleKey, out _);
            // Apple 瀹炲喌鏃跺簭鍏冩暟鎹建锛圕ontentDescribes / 灏侀潰杞級
            Mp4MdtaKeyStripper.TryStripMebxTracks(path, out _);
            // vivo 鈮200 uuid box
            Mp4MdtaKeyStripper.TryStripUuidBox(path, "vivoMediaExtInfo", out _);
        }

        private static bool ShouldStripAppleKey(string name, string value)
            => name.StartsWith("com.apple.quicktime.content.identifier", StringComparison.OrdinalIgnoreCase)
            || name.Contains("live-photo", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vitality", StringComparison.OrdinalIgnoreCase);

        // vivo 鈮200 JPEG 灏鹃儴锛氫粠鏈€鍚庝竴涓?vivo{ 鍒版枃浠舵湯灏炬暣浣撴埅鏂€?
        // 鏂扮増鏍锋湰鐨?cameralbum! 涔嬪悗杩樻湁 ID銆丗F FF FF FF 涓?11 瀛楄妭绛惧悕锛?
        // 涓嶈兘鍐嶇敤 "浠?cameralbum! 缁撳熬" 鍒ゆ柇銆?
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
                // 鎴柇澶辫触涓嶉樆鏂紙vivo 灏炬爣娓呯悊鏄?best-effort锛?
            }
        }

        // 鍏ㄦ枃浠?ASCII 鎵弿锛圶MP/EXIF/keys 鍙兘鍦ㄦ枃浠跺ご涔熷彲鑳藉湪鏂囦欢灏剧殑 moov 鍖猴紝蹇呴』鎵叏閲忥級銆?
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
                // 璇诲け璐ユ寜"鏈夋爣璁?澶勭悊锛岃涓婂眰璧板墺绂昏矾寰勯噸璇?
                return true;
            }
            return false;
        }
    }
}


