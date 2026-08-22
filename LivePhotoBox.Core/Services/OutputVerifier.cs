using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>输出自检级别（设置项 OutputCheckLevel）。</summary>
    public enum OutputCheckLevel
    {
        /// <summary>不检查。</summary>
        None = 0,

        /// <summary>浅查：XMP 可读/标识属性/单份 XMP + 结构完整性 + 实况数据标记。</summary>
        Light = 1,

        /// <summary>深查：浅查 + 解码 + 内嵌视频播放验证。</summary>
        Full = 2,
    }

    /// <summary>单文件输出自检结果。</summary>
    public sealed class OutputCheckResult
    {
        /// <summary>是否全部通过（Problems 为空）。</summary>
        public bool Passed { get; set; } = true;

        /// <summary>发现的问题列表。</summary>
        public List<string> Problems { get; } = new();

        /// <summary>正常观察记录（如 ftyp 位置、解码 OK）。</summary>
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// 输出自检服务：每次合成/拆分/修复/封面操作产出文件后，检查
    /// （1）XMP 是否写入正确（可读、标识属性齐全、只有一份）、
    /// （2）文件结构是否完整（JPEG 标记 / HEIC box 链 / MP4 moov）、
    /// （3）实况照片数据是否写入（内嵌视频、各家协议标记）、
    /// 深查模式额外做解码与视频播放验证。
    /// 检查失败只记日志，不阻断操作（best-effort）。
    /// </summary>
    public static class OutputVerifier
    {
        private const string SettingKey = "OutputCheckLevel";
        private const int DefaultLevel = (int)OutputCheckLevel.Light;

        /// <summary>
        /// 自检失败标记：操作返回详情/消息时以此前缀携带问题列表，
        /// GUI 队列据此显示"自检失败"状态（橙色 + 可点击查看）。
        /// </summary>
        public const string SelfCheckMarker = "SELFCHECK_FAIL:";

        /// <summary>当前自检级别（读设置，默认浅查）。</summary>
        public static OutputCheckLevel CurrentLevel
            => (OutputCheckLevel)AppSettingsService.GetValue(SettingKey, DefaultLevel);

        /// <summary>
        /// 按设置级别对输出文件做自检，记录日志，并返回发现的问题列表（空 = 通过）。
        /// 不抛异常。
        /// </summary>
        public static async Task<List<string>> VerifyAndLogAsync(
            string filePath, CancellationToken token,
            LivePhotoProtocolType? expectedProtocol = null,
            bool expectEmbeddedVideo = true)
        {
            if (CurrentLevel == OutputCheckLevel.None) return new List<string>();
            try
            {
                var result = await VerifyAsync(
                    filePath, token, expectedProtocol, expectEmbeddedVideo);
                if (result.Problems.Count > 0)
                {
                    LogService.Warn(
                        $"输出自检未通过 [{Path.GetFileName(filePath)}]: " +
                        string.Join("; ", result.Problems),
                        source: LogSource.System);
                }
                else if (result.Notes.Count > 0)
                {
                    LogService.Debug(
                        $"输出自检通过 [{Path.GetFileName(filePath)}]: " +
                        string.Join("; ", result.Notes),
                        LogSource.System);
                }
                return result.Problems;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Warn(
                    $"输出自检执行异常 [{Path.GetFileName(filePath)}]: {ex.Message}",
                    source: LogSource.System);
                return new List<string>();
            }
        }

        /// <summary>从操作详情/消息中剥离自检失败标记；返回 true 表示是自检失败。</summary>
        public static bool TryStripSelfCheckMarker(string details, out string problems)
        {
            if (details.StartsWith(SelfCheckMarker, StringComparison.Ordinal))
            {
                problems = details[SelfCheckMarker.Length..];
                return true;
            }
            problems = details;
            return false;
        }

        /// <summary>用于终端/日志显示的清洗文本（剥离自检失败标记）。</summary>
        public static string CleanMessage(string details)
            => TryStripSelfCheckMarker(details, out var problems) ? problems : details;

        /// <summary>对输出文件做自检，返回详细结果（不抛异常）。</summary>
        public static async Task<OutputCheckResult> VerifyAsync(
            string filePath, CancellationToken token,
            LivePhotoProtocolType? expectedProtocol = null,
            bool expectEmbeddedVideo = true)
        {
            var result = new OutputCheckResult();
            if (CurrentLevel == OutputCheckLevel.None) return result;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".heic" or ".heif" or ".mp4" or ".mov" or ".m4v"))
            {
                result.Problems.Add($"不支持自检的文件类型: {ext}");
                result.Passed = false;
                return result;
            }

            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(filePath, token); }
            catch (Exception ex)
            {
                result.Problems.Add($"无法读取文件: {ex.Message}");
                result.Passed = false;
                return result;
            }

            // 1. XMP：可读、标识属性、单份。
            string? xmp = await CheckXmpAsync(filePath, bytes, ext, result, token);

            // 2. 结构完整性。
            CheckStructure(bytes, ext, result);

            // 3. 实况照片数据（内嵌视频 + 协议标记）。
            CheckLivePhotoData(bytes, ext, xmp, expectedProtocol, expectEmbeddedVideo, result);

            // 4. 深查：解码 + 内嵌视频播放。
            if (CurrentLevel >= OutputCheckLevel.Full)
            {
                await CheckDecodeAsync(filePath, ext, result, token);
                await CheckVideoPlaybackAsync(bytes, ext, result, token);
            }

            result.Passed = result.Problems.Count == 0;
            return result;
        }

        // ── 1. XMP 检查 ────────────────────────────────────────────────────

        private static async Task<string?> CheckXmpAsync(
            string filePath, byte[] bytes, string ext, OutputCheckResult result, CancellationToken token)
        {
            string? xmp = await XmpMarkerService.ReadXmpTextAsync(filePath, token);
            // 调试开关关闭时本软件不写私有命名空间：跳过标识属性与单份 XMP 检查
            // （华为等协议产物可能完全没有 XMP，这是预期行为），但 XMP 文本仍要
            // 返回给协议标记检查（如 GCamera:MotionPhoto）使用。
            if (!XmpMarkerService.IsLpbNamespaceWriteEnabled)
                return xmp;

            if (string.IsNullOrWhiteSpace(xmp))
            {
                result.Problems.Add("XMP 读不到（exiftool 与字节级均无内容）");
                return null;
            }
            if (!xmp.Contains("xmlns:LivePhotoBox=", StringComparison.Ordinal) ||
                !xmp.Contains("LivePhotoBox:Version=", StringComparison.Ordinal) ||
                !xmp.Contains("LivePhotoBox:Timestamp=", StringComparison.Ordinal))
            {
                result.Problems.Add("缺少 LivePhotoBox 标识属性（命名空间/版本/时间戳）");
            }

            // 单份 XMP 检测。
            if (ext is ".jpg" or ".jpeg")
            {
                int xpacket = CountAscii(bytes, "<?xpacket begin");
                if (xpacket != 1)
                    result.Problems.Add($"JPEG XMP 数量异常: {xpacket} 份（应为 1）");
            }
            else if (ext is ".mp4" or ".mov" or ".m4v")
            {
                int uuidCount = CountTopLevelAdobeUuid(bytes);
                if (uuidCount > 1)
                    result.Problems.Add($"视频顶层 XMP uuid 数量异常: {uuidCount} 份（应为 0 或 1）");
            }
            else if (ext is ".heic" or ".heif")
            {
                var (metaUuid, mimeXmp) = CountHeicXmpSources(bytes);
                if (metaUuid > 1)
                    result.Problems.Add($"HEIC meta 内 XMP uuid 数量异常: {metaUuid} 份（应为 0 或 1）");
                if (mimeXmp != 1)
                    result.Problems.Add($"HEIC XMP mime 条目数量异常: {mimeXmp} 份（应为 1）");
            }

            return xmp;
        }

        // ── 2. 结构检查 ────────────────────────────────────────────────────

        private static void CheckStructure(byte[] bytes, string ext, OutputCheckResult result)
        {
            if (ext is ".jpg" or ".jpeg")
            {
                if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                {
                    result.Problems.Add("JPEG 缺少 SOI (FFD8)");
                    return;
                }
                // 视频起点（第一个 ftyp），JPEG 段截止到视频之前。
                int ftyp = FindAscii(bytes, "ftyp", start: 0x1000);
                int limit = ftyp > 0 ? ftyp : bytes.Length;
                bool hasApp1 = false, hasEoi = false;
                for (int i = 2; i + 1 < limit; i++)
                {
                    if (bytes[i] != 0xFF) continue;
                    if (bytes[i + 1] == 0xE1) hasApp1 = true;
                    if (bytes[i + 1] == 0xD9) hasEoi = true;
                }
                if (!hasApp1) result.Problems.Add("JPEG 视频前缺少 XMP APP1 段 (FFE1)");
                if (!hasEoi) result.Problems.Add("JPEG 视频前缺少 EOI (FFD9)");
            }
            else if (ext is ".heic" or ".heif")
            {
                var boxes = WalkTopLevelBoxes(bytes);
                if (boxes == null || boxes.Count == 0)
                {
                    result.Problems.Add("HEIC 顶层 box 链无效");
                    return;
                }
                var types = boxes.Select(b => b.Type).ToList();
                if (!types.Contains("meta")) result.Problems.Add("HEIC 缺少 meta box");
                if (!types.Contains("mdat")) result.Problems.Add("HEIC 缺少 mdat box");

                var (metaOk, ilocCount, iinfCount) = CheckHeicMeta(bytes);
                if (!metaOk) result.Problems.Add("HEIC meta 子 box 链异常（截断或非法 size）");
                else if (ilocCount >= 0 && iinfCount >= 0 && ilocCount != iinfCount)
                    result.Problems.Add($"HEIC iloc({ilocCount}) != iinf({iinfCount}) 条目数");
            }
            else if (ext is ".mp4" or ".mov" or ".m4v")
            {
                var types = WalkTopLevelBoxes(bytes)?.Select(b => b.Type).ToList();
                if (types == null || !types.Contains("ftyp"))
                    result.Problems.Add("视频缺少 ftyp");
                if (types == null || !types.Contains("moov"))
                    result.Problems.Add("视频缺少 moov");
            }
        }

        // ── 3. 实况照片数据检查 ────────────────────────────────────────────

        private static void CheckLivePhotoData(
            byte[] bytes, string ext, string? xmp,
            LivePhotoProtocolType? expectedProtocol, bool expectEmbeddedVideo,
            OutputCheckResult result)
        {
            bool singleFile = ext is ".jpg" or ".jpeg" or ".heic" or ".heif";
            if (singleFile && expectEmbeddedVideo)
            {
                int ftyp = FindEmbeddedVideoFtyp(bytes, ext);
                if (ftyp < 0)
                    result.Problems.Add("未找到内嵌视频 (ftyp)");
                else
                    result.Notes.Add($"内嵌视频 ftyp @ {ftyp}");
            }

            // 协议标记（按调用方传入的目标/源协议）。
            switch (expectedProtocol)
            {
                case LivePhotoProtocolType.Huawei:
                    if (!ContainsAscii(bytes, "LIVE_"))
                        result.Problems.Add("华为产物缺少 LIVE_ 尾标");
                    break;
                case LivePhotoProtocolType.Samsung:
                case LivePhotoProtocolType.Fusion:
                    if (ext is ".heic" or ".heif")
                    {
                        if (!ContainsAscii(bytes, "mpvd"))
                            result.Problems.Add("三星 HEIC 产物缺少 mpvd box");
                    }
                    else if (!ContainsAscii(bytes, "SEFH") || !ContainsAscii(bytes, "SEFT"))
                    {
                        result.Problems.Add("三星产物缺少 SEFH/SEFT 标记");
                    }
                    break;
                case LivePhotoProtocolType.GoogleV1:
                    if (xmp == null || !xmp.Contains("MicroVideo", StringComparison.Ordinal))
                        result.Problems.Add("Google V1 产物缺少 GCamera:MicroVideo 标记");
                    break;
                case LivePhotoProtocolType.GoogleV2:
                    if (xmp == null || !xmp.Contains("MotionPhoto", StringComparison.Ordinal))
                        result.Problems.Add("Google V2 产物缺少 GCamera:MotionPhoto 标记");
                    break;
                case LivePhotoProtocolType.OPPO:
                    if (xmp == null || !xmp.Contains("OpCamera", StringComparison.Ordinal))
                        result.Problems.Add("OPPO 产物缺少 OpCamera 标记");
                    break;
                case LivePhotoProtocolType.Vivo:
                    if (xmp == null || !xmp.Contains("VCamera", StringComparison.Ordinal))
                        result.Problems.Add("vivo 产物缺少 VCamera 标记");
                    break;
                // Apple 双文件配对（ContentIdentifier）由拆分/封面流程校验，这里不做字节断言。
                default:
                    break;
            }
        }

        // ── 4. 深查：解码 + 播放 ───────────────────────────────────────────

        private static async Task CheckDecodeAsync(
            string filePath, string ext, OutputCheckResult result, CancellationToken token)
        {
            if (ext is not (".heic" or ".heif")) return;
            string? heifDec = ExternalToolLocator.FindHeifDec();
            if (string.IsNullOrEmpty(heifDec))
            {
                result.Problems.Add("heif-dec 不可用，无法做解码验证");
                return;
            }

            string tmpPng = Path.Combine(Path.GetTempPath(), $"lpb_verify_{Guid.NewGuid():N}.png");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = heifDec,
                    WorkingDirectory = Path.GetDirectoryName(heifDec) ?? AppContext.BaseDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(filePath);
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(tmpPng);

                using var process = Process.Start(psi);
                if (process == null) { result.Problems.Add("无法启动 heif-dec"); return; }
                string stderr = await process.StandardError.ReadToEndAsync(token);
                string stdout = await process.StandardOutput.ReadToEndAsync(token);
                try { await process.WaitForExitAsync(token); }
                catch (OperationCanceledException) { process.Kill(); throw; }

                if (process.ExitCode != 0 || !File.Exists(tmpPng) || new FileInfo(tmpPng).Length == 0)
                    result.Problems.Add($"heif-dec 解码失败: {(stderr + stdout).Trim()[..Math.Min(160, (stderr + stdout).Trim().Length)]}");
                else
                    result.Notes.Add("heif-dec 解码 OK");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Problems.Add($"解码验证异常: {ex.Message}"); }
            finally
            {
                try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch { }
            }
        }

        private static async Task CheckVideoPlaybackAsync(
            byte[] bytes, string ext, OutputCheckResult result, CancellationToken token)
        {
            if (ext is not (".jpg" or ".jpeg" or ".heic" or ".heif")) return;
            int ftyp = FindEmbeddedVideoFtyp(bytes, ext);
            if (ftyp < 0) return; // 缺视频已在轻查报告

            int live = FindAscii(bytes, "LIVE_");
            int end = live > ftyp ? live : bytes.Length;
            byte[] video = new byte[end - (ftyp - 4)];
            Array.Copy(bytes, ftyp - 4, video, 0, video.Length);

            string? ffprobe = ResolveFfprobe();
            if (string.IsNullOrEmpty(ffprobe))
            {
                result.Problems.Add("ffprobe 不可用，无法做视频播放验证");
                return;
            }

            string tmpMp4 = Path.Combine(Path.GetTempPath(), $"lpb_verify_{Guid.NewGuid():N}.mp4");
            try
            {
                await File.WriteAllBytesAsync(tmpMp4, video, token);
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-select_streams");
                psi.ArgumentList.Add("v:0");
                psi.ArgumentList.Add("-show_entries");
                psi.ArgumentList.Add("stream=codec_name,width,height");
                psi.ArgumentList.Add("-of");
                psi.ArgumentList.Add("csv=p=0");
                psi.ArgumentList.Add(tmpMp4);

                using var process = Process.Start(psi);
                if (process == null) { result.Problems.Add("无法启动 ffprobe"); return; }
                string stdout = await process.StandardOutput.ReadToEndAsync(token);
                string stderr = await process.StandardError.ReadToEndAsync(token);
                try { await process.WaitForExitAsync(token); }
                catch (OperationCanceledException) { process.Kill(); throw; }

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                    result.Problems.Add($"内嵌视频播放验证失败: {(stderr + stdout).Trim()[..Math.Min(160, (stderr + stdout).Trim().Length)]}");
                else
                    result.Notes.Add($"内嵌视频 OK ({stdout.Trim()})");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Problems.Add($"视频播放验证异常: {ex.Message}"); }
            finally
            {
                try { if (File.Exists(tmpMp4)) File.Delete(tmpMp4); } catch { }
            }
        }

        // ── 字节工具 ───────────────────────────────────────────────────────

        private static readonly byte[] AdobeUsertype =
        {
            0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
            0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC
        };

        private static int CountAscii(byte[] bytes, string text)
            => CountBytes(bytes, Encoding.ASCII.GetBytes(text));

        private static bool ContainsAscii(byte[] bytes, string text)
            => FindBytes(bytes, Encoding.ASCII.GetBytes(text)) >= 0;

        private static int FindAscii(byte[] bytes, string text, int start = 0)
            => FindBytes(bytes, Encoding.ASCII.GetBytes(text), start);

        private static int FindBytes(byte[] haystack, byte[] needle, int start = 0)
        {
            if (needle.Length == 0 || needle.Length > haystack.Length - start) return -1;
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static int CountBytes(byte[] haystack, byte[] needle, int start = 0)
        {
            if (needle.Length == 0 || needle.Length > haystack.Length - start) return 0;
            int count = 0;
            for (int i = start; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) count++;
            }
            return count;
        }

        private static int ReadU32(byte[] a, int off)
            => (a[off] << 24) | (a[off + 1] << 16) | (a[off + 2] << 8) | a[off + 3];

        private static int ReadU16(byte[] a, int off)
            => (a[off] << 8) | a[off + 1];

        /// <summary>走顶层 box 链，返回 (类型, 起始偏移)；遇到非法 size/截断返回 null。</summary>
        private static List<(string Type, int Pos, int Size)>? WalkTopLevelBoxes(byte[] bytes)
        {
            var boxes = new List<(string, int, int)>();
            int p = 0;
            while (p + 8 <= bytes.Length)
            {
                var (size, next) = ReadBoxHeader(bytes, p, bytes.Length);
                if (size < 8 || next > bytes.Length) break; // 可能是尾标等非 box 数据
                string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type.Any(c => c < 32 || c > 126)) return null;
                boxes.Add((type, p, size));
                p = next;
            }
            return boxes;
        }

        /// <summary>检查 HEIC meta：子 box 链是否完整 + iloc/iinf 条目数。</summary>
        private static (bool MetaOk, int IlocCount, int IinfCount) CheckHeicMeta(byte[] bytes)
        {
            var boxes = WalkTopLevelBoxes(bytes);
            if (boxes == null) return (false, -1, -1);
            var meta = boxes.FirstOrDefault(b => b.Type == "meta");
            if (meta == default) return (false, -1, -1);

            int metaEnd = meta.Pos + meta.Size;
            int q = meta.Pos + 12; // meta header 8 + FullBox 4
            int ilocCount = -1, iinfCount = -1;
            while (q + 8 <= metaEnd)
            {
                var (size, next) = ReadBoxHeader(bytes, q, metaEnd);
                if (size < 8 || next > metaEnd) return (false, ilocCount, iinfCount);
                string type = Encoding.ASCII.GetString(bytes, q + 4, 4);
                if (type.Any(c => c < 32 || c > 126)) return (false, ilocCount, iinfCount);
                if (type == "iloc") ilocCount = ReadU16(bytes, q + 14);
                else if (type == "iinf") iinfCount = ReadU16(bytes, q + 12);
                q = next;
            }
            return (true, ilocCount, iinfCount);
        }

        /// <summary>
        /// HEIC 的 XMP 来源统计：meta 内 Adobe uuid box 数量 + iinf 中
        /// content_type 为 application/rdf+xml 的 mime 条目数量。
        /// </summary>
        private static (int MetaUuid, int MimeXmp) CountHeicXmpSources(byte[] bytes)
        {
            var boxes = WalkTopLevelBoxes(bytes);
            if (boxes == null) return (0, 0);
            var meta = boxes.FirstOrDefault(b => b.Type == "meta");
            if (meta == default) return (0, 0);

            int metaEnd = meta.Pos + meta.Size;
            int q = meta.Pos + 12;
            int uuidCount = 0, mimeXmp = 0;
            int iinfPos = -1, iinfEnd = -1;
            while (q + 8 <= metaEnd)
            {
                var (size, next) = ReadBoxHeader(bytes, q, metaEnd);
                if (size < 8 || next > metaEnd) break;
                string type = Encoding.ASCII.GetString(bytes, q + 4, 4);
                if (type == "uuid" && size >= 24 &&
                    bytes.AsSpan(q + 8, 16).SequenceEqual(AdobeUsertype))
                {
                    uuidCount++;
                }
                else if (type == "iinf")
                {
                    iinfPos = q;
                    iinfEnd = q + size;
                }
                q = next;
            }

            if (iinfPos >= 0)
            {
                int count = ReadU16(bytes, iinfPos + 12);
                int ip = iinfPos + 14;
                for (int i = 0; i < count && ip + 24 <= iinfEnd; i++)
                {
                    int infeSize = ReadU32(bytes, ip);
                    if (infeSize < 24) { ip += Math.Max(infeSize, 8); continue; }
                    int version = bytes[ip + 8];
                    int itemTypePos = version == 2 ? 16 : version == 3 ? 14 : -1;
                    int namePos = version == 2 ? 20 : version == 3 ? 22 : -1;
                    if (itemTypePos > 0 && namePos > 0 &&
                        bytes.AsSpan(ip + itemTypePos, 4).SequenceEqual(Encoding.ASCII.GetBytes("mime")))
                    {
                        int nameEnd = Array.IndexOf(bytes, (byte)0, ip + namePos, infeSize - namePos);
                        if (nameEnd >= ip + namePos && nameEnd + 1 < ip + infeSize)
                        {
                            string contentType = Encoding.ASCII.GetString(
                                bytes, nameEnd + 1, ip + infeSize - (nameEnd + 1));
                            if (contentType.StartsWith("application/rdf+xml", StringComparison.OrdinalIgnoreCase))
                                mimeXmp++;
                        }
                    }
                    ip += infeSize;
                }
            }
            return (uuidCount, mimeXmp);
        }

        /// <summary>统计文件顶层 Adobe XMP uuid box 数量（MP4/MOV）。</summary>
        private static int CountTopLevelAdobeUuid(byte[] bytes)
        {
            int count = 0;
            int p = 0;
            while (p + 8 <= bytes.Length)
            {
                var (size, next) = ReadBoxHeader(bytes, p, bytes.Length);
                if (size < 8 || next > bytes.Length) break;
                if (size >= 24 &&
                    bytes[p + 4] == (byte)'u' && bytes[p + 5] == (byte)'u' &&
                    bytes[p + 6] == (byte)'i' && bytes[p + 7] == (byte)'d' &&
                    bytes.AsSpan(p + 8, 16).SequenceEqual(AdobeUsertype))
                {
                    count++;
                }
                p = next;
            }
            return count;
        }

        /// <summary>
        /// 读取 box 头：(size, 下一个 box 偏移)。size==1 表示 64 位扩展长度
        /// （ISO/IEC 14496-12），size==0 表示延伸到容器末尾。非法返回 (0, off)。
        /// </summary>
        private static (int Size, int Next) ReadBoxHeader(byte[] a, int off, int limit)
        {
            if (off + 8 > limit) return (0, off);
            int size = ReadU32(a, off);
            if (size == 1)
            {
                if (off + 16 > limit) return (0, off);
                long size64 = ((long)ReadU32(a, off + 8) << 32) | (uint)ReadU32(a, off + 12);
                if (size64 < 16 || size64 > int.MaxValue) return (0, off);
                return ((int)size64, off + (int)size64);
            }
            if (size == 0) return (0, limit);
            return (size, off + size);
        }

        /// <summary>
        /// 定位内嵌视频 ftyp：JPEG 用第一个 ≥0x1000 的 ftyp（跳过 XMP/EXIF 噪声）；
        /// HEIC 自身 ftyp 在文件头，用第二个 ftyp。
        /// </summary>
        private static int FindEmbeddedVideoFtyp(byte[] bytes, string ext)
        {
            if (ext is ".heic" or ".heif")
            {
                int first = FindAscii(bytes, "ftyp"); // HEIC 自身 ftyp
                if (first < 0) return -1;
                return FindAscii(bytes, "ftyp", first + 4); // 内嵌视频 ftyp
            }
            return FindAscii(bytes, "ftyp", start: 0x1000);
        }

        private static string? ResolveFfprobe()
        {
            string? ffmpeg = ExternalToolLocator.FindFFmpeg();
            if (!string.IsNullOrEmpty(ffmpeg))
            {
                string candidate = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return "ffprobe"; // 由系统 PATH 解析
        }

        /// <summary>协议索引 → 协议类型（与 CoverChangeService.ToProtocolIndex 反向）。</summary>
        public static LivePhotoProtocolType ProtocolTypeFromIndex(int index) => index switch
        {
            0 => LivePhotoProtocolType.Fusion,
            1 => LivePhotoProtocolType.GoogleV1,
            2 => LivePhotoProtocolType.GoogleV2,
            3 => LivePhotoProtocolType.OPPO,
            4 => LivePhotoProtocolType.Vivo,
            5 => LivePhotoProtocolType.Samsung,
            6 => LivePhotoProtocolType.Huawei,
            _ => LivePhotoProtocolType.Unknown,
        };

        /// <summary>协议 key（XmpMarkerService.ProtocolKey 的输出）→ 协议类型。</summary>
        public static LivePhotoProtocolType ProtocolTypeFromKey(string? key) => key switch
        {
            "MicroVideoV1" => LivePhotoProtocolType.GoogleV1,
            "MotionPhotoV2" => LivePhotoProtocolType.GoogleV2,
            "OppoLivePhoto" => LivePhotoProtocolType.OPPO,
            "VivoLivePhoto" => LivePhotoProtocolType.Vivo,
            "SamsungMotionPhoto" => LivePhotoProtocolType.Samsung,
            "HuaweiMovingPhoto" => LivePhotoProtocolType.Huawei,
            "MotionPhotoFusion" => LivePhotoProtocolType.Fusion,
            "Apple" => LivePhotoProtocolType.Apple,
            _ => LivePhotoProtocolType.Unknown,
        };
    }
}
