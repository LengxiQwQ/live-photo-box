using LivePhotoBox.Models;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// =====================================================================================
// LivePhotoSplitService —— 实况照片拆分核心
// =====================================================================================
//
// 拆分后"图片"端为什么要重新构造（不直接 CopyExactLength）：
// -------------------------------------------------------------------------------------
// 原实况照片的图片部分在文件结构上 = 完整 JPEG，其 APP1 段里存放着 Google 规范的 XMP
// 元数据（xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" + GCamera:MicroVideo
// 或 GCamera:MotionPhoto + GCamera:MicroVideoOffset 等字段）。
//
// 如果直接按字节截断复制出图片端（imageLength = totalSize - videoLength），输出的"图片"
// 仍是一张完整的 JPEG 字节流，但同时**仍保留着"我是实况照片"的自白书**。
// 下次扫描到这张图时，LivePhotoSplitScanService 会再次把它识别为实况照片，
// 进入"已拆分"列表，用户又被诱导重复点击拆分——构成"假阳性循环"。
//
// 解决思路：拆分时按 JPEG 段结构逐段复制图片字节流，对每个 APP 段做"实况照片特征"嗅探，
// 命中则整段丢弃。**EXIF 段、ICC 段、普通 XMP 段、量化表、哈夫曼表、压缩图像数据等
// 全部原样保留**，确保拍摄日期、GPS 经纬度、光圈快门 ISO、镜头型号、方向、缩略图等
// 一切用户元数据不丢失。
//
// 嗅探策略（按"结构匹配"，不按"关键词搜索"，兼容性最优）：
// -------------------------------------------------------------------------------------
//   1. 必须是 APP 段（marker 落在 0xFFE0 - 0xFFEF）
//   2. 段 payload 必须以 Adobe XMP 规范规定的 29 字节固定头
//      "http://ns.adobe.com/xap/1.0/\0" 开头（普通 EXIF 段以 "Exif\0\0" 开头，
//      二进制层面不会混淆）
//   3. XMP 段内必须声明 Google 实况照片规范强制要求的命名空间之一：
//        - xmlns:GCamera="http://ns.google.com/photos/1.0/camera/"
//        - xmlns:Container="http://ns.google.com/photos/1.0/container/"
//      三个条件同时满足才视为"实况照片元数据段"，整段丢弃。
//
// 为什么不靠 GCamera: / MicroVideo / MotionPhoto 这些"关键词"做搜索：
//   - EXIF 段是二进制 TIFF 结构，文本字段都在 IFD 里以 ASCII 存储，若 EXIF 的 Make
//     字段恰好等于 "Google Camera"（Pixel 拍的照片就是），按关键词搜索会误伤 EXIF。
//   - 普通 XMP 段（Lightroom / Photoshop / Apple Photos 等写入的元数据）可能含
//     "Motion"、"MicroVideo" 等字样作为编辑历史标签，按关键词搜索会误伤 XMP。
//   - 按 namespace 匹配是 Google 官方规范的强制字段，Android ExifInterface、所有
//     Google/Samsung/Xiaomi/Huawei/OPPO 等厂商、第三方工具均遵循，零误伤。
//
// 兼容性范围（结构匹配）：
//   - 本工具自己合成的实况照片 ✅（注入的 XMP 完全符合 Google 规范）
//   - Google Pixel ✅
//   - Samsung Galaxy（MicroVideo / MotionPhoto 两种变体）✅
//   - 小米/华为/OPPO（Android 9+ 均走 Android ExifInterface）✅
//   - iPhone Live Photo —— N/A（iOS 不用 JPEG 容器，不走 XMP APP1 段）
//   - 普通 JPEG（无 XMP）✅ 不受影响
//   - 含 EXIF 的普通 JPEG（拍摄参数/GPS）✅ EXIF 段原样保留
//   - Lightroom/PS 处理过的 JPEG（XMP 调色/编辑历史）✅ 普通 XMP 段原样保留
// =====================================================================================

namespace LivePhotoBox.Services
{
    public static class LivePhotoSplitService
    {
        private const int MetadataProbeBytes = 1024 * 1024; // 探测前 1MB 的元数据

        // 添加了 TimeSpan.FromSeconds(2) 作为超时保护，防止正则表达式遇到损坏文件陷入死循环
        private static readonly Regex MicroVideoOffsetRegex = new(
            "GCamera:MicroVideoOffset=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MotionPhotoLengthRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Length=\"(?<value>\\d+)\"|Item:Length=\"(?<value>\\d+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MotionPhotoMimeRegex = new(
            "Item:Semantic=\"MotionPhoto\"[^>]*Item:Mime=\"(?<value>[^\"]+)\"|Item:Mime=\"(?<value>[^\"]+)\"[^>]*Item:Semantic=\"MotionPhoto\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline,
            TimeSpan.FromSeconds(2));

        // 厂商私有偏移量正则（rdf:Description 属性级，非 Container:Directory 结构）。
        // 作为深度防御：即使 exiftool/修图软件剥离了 Container:Directory 段，
        // 只要 rdf:Description 的属性还在，就能解析出视频长度。
        private static readonly Regex OppoVideoLengthRegex = new(
            "OpCamera:VideoLength=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MiCameraVideoLengthRegex = new(
            "MiCamera:VideoLength=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        public static async Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory, int selectedSplitFormatIndex, CancellationToken token, string? inputDirectory = null)
        {
            Directory.CreateDirectory(outputDirectory);

            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length <= 0)
            {
                throw new InvalidDataException("Source file is empty.");
            }

            string metadataText = await ReadMetadataTextAsync(sourceStream, token);
            long videoLength = GetAppendedVideoLength(metadataText);
            long imageLength = sourceStream.Length - videoLength;

            LogService.Split($"File={Path.GetFileName(sourcePath)}, TotalSize={sourceStream.Length}, VideoLength={videoLength}, ImageLength={imageLength}", LogLevel.Debug);

            if (videoLength <= 0 || imageLength <= 0)
            {
                throw new InvalidDataException("Unable to determine the appended motion video length or file is corrupted.");
            }

            string targetExtension = await ResolveVideoExtensionAsync(sourceStream, imageLength, metadataText, selectedSplitFormatIndex, token);
            (string imageOutputPath, string videoOutputPath) = BuildOutputPaths(sourcePath, outputDirectory, targetExtension, inputDirectory);

            // 1. 提取图片部分（同时剥离实况照片相关的 XMP / APP 段，避免拆分出的"图片"仍被识别为实况照片）
            sourceStream.Position = 0;
            await using (var imageOutputStream = new FileStream(imageOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyJpegStrippingLivePhotoMetadataAsync(sourceStream, imageOutputStream, imageLength, token);
            }

            // OPPO 协议在 EXIF UserComment 里写了 "oplus_10485792" 标记（供 OPPO 相册识别）。
            // XMP 段已在上面被剥离，但 EXIF 段原样保留了 → 需单独清理。
            // 只清以 "oplus_" 开头的值，不碰其他内容的 UserComment（如相机自定义备注）。
            if (metadataText.Contains("xmlns:OpCamera", StringComparison.Ordinal))
            {
                await ClearOppoExifMarkerAsync(imageOutputPath, token);
            }

            // 2. 提取视频部分到临时文件，使用 try-finally 保证任何异常/取消都会清理
            string tempDir = Path.Combine(outputDirectory, "Temp");
            Directory.CreateDirectory(tempDir);
            string tempVideoPath = Path.Combine(tempDir, Path.GetFileName(videoOutputPath) + ".tmp");

            sourceStream.Position = imageLength;
            await using (var videoOutputStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyExactLengthAsync(sourceStream, videoOutputStream, videoLength, token);
            }

            try
            {
                // 3. 检查是否需要处理
                bool needsProcessing = selectedSplitFormatIndex switch
                {
                    1 => true,  // 用户明确选择 MP4
                    2 => true,  // 用户明确选择 MOV
                    _ => false  // 默认格式，直接使用原视频
                };

                if (needsProcessing)
                {
                    // 检测源视频格式
                    string sourceVideoExtension = await DetectDefaultVideoExtensionAsync(sourceStream, imageLength, metadataText, token);
                    bool formatMatches = (selectedSplitFormatIndex == 1 && sourceVideoExtension == ".mp4") ||
                                        (selectedSplitFormatIndex == 2 && sourceVideoExtension == ".mov");

                    LogService.Split($"needsProcessing={needsProcessing}, selectedIndex={selectedSplitFormatIndex}, sourceExt={sourceVideoExtension}, targetExt={targetExtension}, formatMatches={formatMatches}", LogLevel.Debug);

                    if (formatMatches)
                    {
                        LogService.Split($"Remuxing video (container only): {sourceVideoExtension} -> {targetExtension}", LogLevel.Debug);
                        var remuxResult = await VideoTranscodeService.RemuxAsync(tempVideoPath, videoOutputPath, token);
                        if (!remuxResult.Success)
                            throw new InvalidOperationException($"Video remux failed: {remuxResult.ErrorMessage}");
                    }
                    else
                    {
                        LogService.Split($"Transcoding video: {sourceVideoExtension} -> {targetExtension}", LogLevel.Debug);
                        var transcodeResult = selectedSplitFormatIndex == 1
                            ? await VideoTranscodeService.TranscodeToMp4Async(tempVideoPath, videoOutputPath, token)
                            : await VideoTranscodeService.TranscodeToMovAsync(tempVideoPath, videoOutputPath, token);
                        if (!transcodeResult.Success)
                            throw new InvalidOperationException($"Video transcode failed: {transcodeResult.ErrorMessage}");
                    }
                }
                else
                {
                    // 不需要转码，直接移动临时文件到目标位置
                    if (File.Exists(videoOutputPath))
                        File.Delete(videoOutputPath);
                    File.Move(tempVideoPath, videoOutputPath);
                }

                // 4. 将源文件的关键元数据写回视频输出（供后续元数据匹配使用）
                await CopyMetadataToVideoAsync(sourcePath, videoOutputPath, token);

                // 5. 给图片和视频打上 LivePhotoBox 标记（标识经本软件拆分过）
                await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                    imageOutputPath, "Split", "", token);
                await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                    videoOutputPath, "Split", "", token);

                return new LivePhotoSplitResult
                {
                    ImageOutputPath = imageOutputPath,
                    VideoOutputPath = videoOutputPath
                };
            }
            catch
            {
                // 转码/remux 失败时清理可能已经写入的不完整输出文件
                try { if (File.Exists(videoOutputPath)) File.Delete(videoOutputPath); } catch { }
                throw;
            }
            finally
            {
                // 无论成功/失败/取消，临时文件都要清理
                try { if (File.Exists(tempVideoPath)) File.Delete(tempVideoPath); } catch { }
                // 注意：不删除 Temp 目录本身，由 ViewModel 在全部任务完成后统一清理。
                // 并发拆分时多个任务共享同一个 Temp 目录，单个任务删除会导致其他进行中任务
                // 路径失效，"Could not find a part of the path"。
            }
        }

        // 从源文件流中读取前 <see cref="MetadataProbeBytes"/> 字节的文本内容，
        // 用于提取实况照片的 XMP 元数据（MicroVideoOffset 等）。
        private static async Task<string> ReadMetadataTextAsync(FileStream sourceStream, CancellationToken token)
        {
            sourceStream.Position = 0;
            int bufferLength = (int)Math.Min(sourceStream.Length, MetadataProbeBytes);
            byte[] buffer = new byte[bufferLength];
            int bytesRead = await sourceStream.ReadAsync(buffer, token);
            sourceStream.Position = 0;
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        /// <summary>
        /// 公开的重载：从文件路径读取 XMP 元数据文本（前 1MB），
        /// 供 LightboxItemSource 等外部调用方使用。
        /// </summary>
        public static async Task<string> ReadMetadataFromFileAsync(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await ReadMetadataTextAsync(fs, CancellationToken.None);
        }

        /// <summary>
        /// 同步版本：供扫描阶段在同步循环中直接调用，避免 async 开销。
        /// </summary>
        public static string ReadMetadataTextSync(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bufferLength = (int)Math.Min(fs.Length, MetadataProbeBytes);
            byte[] buffer = new byte[bufferLength];
            int bytesRead = fs.Read(buffer, 0, bufferLength);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        // 从 XMP 元数据文本中提取视频尾部长度。
        // 深度防御：依次尝试全部已知厂商的偏移量格式。
        //   MicroVideo V1 → MotionPhoto V2 → OPPO O-Live → 小米
        // 只要任一格式匹配成功即返回，多道 fallback 确保 XMP 被
        // 修图软件/exiftool 部分修改后仍能解析。
        public static long GetAppendedVideoLength(string metadataText)
        {
            if (TryGetLong(MicroVideoOffsetRegex.Match(metadataText), out long microVideoOffset))
                return microVideoOffset;

            if (TryGetLong(MotionPhotoLengthRegex.Match(metadataText), out long motionPhotoLength))
                return motionPhotoLength;

            if (TryGetLong(OppoVideoLengthRegex.Match(metadataText), out long oppoVideoLength))
                return oppoVideoLength;

            if (TryGetLong(MiCameraVideoLengthRegex.Match(metadataText), out long miVideoLength))
                return miVideoLength;

            // 全部失败 → 构造含诊断信息的异常消息，用户可直接在错误弹窗看到
            bool m1 = MicroVideoOffsetRegex.Match(metadataText).Success;
            bool m2 = MotionPhotoLengthRegex.Match(metadataText).Success;
            bool m3 = OppoVideoLengthRegex.Match(metadataText).Success;
            bool m4 = MiCameraVideoLengthRegex.Match(metadataText).Success;

            // 检查 XMP header 是否存在
            bool hasXmpHeader = metadataText.Contains("http://ns.adobe.com/xap/1.0/");

            // 截取前 2000 字符，把不可打印字符替换为 .
            string snippet = metadataText.Length > 2000
                ? metadataText[..2000]
                : metadataText;
            string readable = System.Text.RegularExpressions.Regex.Replace(
                snippet, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]", m => ".");

            string diag = $"hasXmpHeader={hasXmpHeader}, " +
                          $"m1(MicroVideoOffset)={m1}, " +
                          $"m2(MotionPhotoLength)={m2}, " +
                          $"m3(OpCamera:VideoLength)={m3}, " +
                          $"m4(MiCamera:VideoLength)={m4}";

            LogService.Split($"GetAppendedVideoLength failed: {diag}", LogLevel.Debug);
            LogService.Split($"Metadata preview:\n{readable}", LogLevel.Debug);

            throw new InvalidDataException(
                "No motion video length metadata was found in the file.\n" +
                $"Diagnostics: {diag}\n" +
                $"XMP header found: {hasXmpHeader}\n" +
                "See debug log for metadata preview.");
        }

        private static async Task<string> ResolveVideoExtensionAsync(FileStream sourceStream, long videoStartOffset, string metadataText, int selectedSplitFormatIndex, CancellationToken token)
        {
            return selectedSplitFormatIndex switch
            {
                1 => ".MP4",
                2 => ".MOV",
                _ => await DetectDefaultVideoExtensionAsync(sourceStream, videoStartOffset, metadataText, token)
            };
        }

        // 通过视频流头部魔数（ftyp box）检测默认视频格式。
        // 优先级：二进制魔数 > XMP MIME 类型 > 兜底 .mp4。
        private static async Task<string> DetectDefaultVideoExtensionAsync(FileStream sourceStream, long videoStartOffset, string metadataText, CancellationToken token)
        {
            // 1. 视频流头部魔数判断（权威最高优先级）
            byte[] header = new byte[32];
            sourceStream.Position = videoStartOffset;
            int bytesRead = await sourceStream.ReadAsync(header, token);
            sourceStream.Position = 0; // 复位流指针

            if (bytesRead >= 12)
            {
                string boxType = Encoding.ASCII.GetString(header, 4, 4);

                if (boxType == "ftyp")
                {
                    string majorBrand = Encoding.ASCII.GetString(header, 8, 4);

                    // 匹配 Apple QuickTime
                    if (majorBrand.StartsWith("qt", StringComparison.OrdinalIgnoreCase))
                        return ".MOV";

                    // 匹配 MP4 及其变种 (含 hvc1 等 HEVC 变种)
                    if (majorBrand.StartsWith("isom", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("mp4", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("hev1", StringComparison.OrdinalIgnoreCase))
                        return ".MP4";
                }
                else if (boxType == "moov")
                {
                    // 兼容极少数无 ftyp 直接 moov 开头的老版本格式
                    return ".MOV";
                }
            }

            // 2. 备用方案：如果二进制流因故未能识别，退回查阅 XMP 文本
            string? mimeType = MotionPhotoMimeRegex.Match(metadataText).Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(mimeType))
            {
                var mime = mimeType.Trim().ToLowerInvariant();
                if (mime == "video/quicktime") return ".MOV";
                if (mime == "video/mp4") return ".MP4";
            }

            // 3. 兜底方案
            LogService.Split("Failed to detect video format via Magic Number and XMP, fallback to .MP4", LogLevel.Warning);
            return ".MP4";
        }

        // 构建拆分后图片和视频的输出路径。
        // 自动处理同名冲突（追加后缀），并防止输出路径覆盖源文件。
        // sourcePath: 源文件路径。
        // outputDirectory: 输出目录。
        // videoExtension: 视频扩展名（.mp4 / .mov）。
        // è¿å: (图片输出路径, 视频输出路径)
        private static (string ImageOutputPath, string VideoOutputPath) BuildOutputPaths(string sourcePath, string outputDirectory, string videoExtension, string? inputDirectory = null)
        {
            string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
            string imageExtension = Path.GetExtension(sourcePath);

            if (string.IsNullOrWhiteSpace(imageExtension))
            {
                imageExtension = ".JPG";
            }

            string? subDir = null;
            if (!string.IsNullOrEmpty(inputDirectory)
                && AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false))
            {
                subDir = PathHelper.GetRelativeSubDirectory(inputDirectory, sourcePath);
            }

            string imageOutputPath = PathHelper.GetUniqueFilePath(outputDirectory, $"{sourceFileNameWithoutExtension}{imageExtension}", subDir);
            string videoOutputPath = PathHelper.GetUniqueFilePath(outputDirectory, $"{sourceFileNameWithoutExtension}{videoExtension}", subDir);
            string sourceFullPath = Path.GetFullPath(sourcePath);

            // 防止输出文件覆盖掉正在读取的源文件
            if (string.Equals(Path.GetFullPath(imageOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                imageOutputPath = Path.Combine(outputDirectory, $"{sourceFileNameWithoutExtension}_image{imageExtension}");
            }

            if (string.Equals(Path.GetFullPath(videoOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                videoOutputPath = Path.Combine(outputDirectory, $"{sourceFileNameWithoutExtension}_video{videoExtension}");
            }

            return (imageOutputPath, videoOutputPath);
        }

        // 从源流复制指定字节数到目标流。
        // 使用 81920 字节缓冲区（低于 LOH 阈值，最优 IO 大小）。
        // 若提前遇到流结尾则抛出 EndOfStreamException。
        // sourceStream: 源流。
        // destinationStream: 目标流。
        // length: 要复制的字节数。
        // token: 取消令牌。
        private static async Task CopyExactLengthAsync(Stream sourceStream, Stream destinationStream, long length, CancellationToken token)
        {
            // 81920 (80KB) 刚好低于 LOH (Large Object Heap) 的阈值，是最优的 IO 缓冲大小
            byte[] buffer = new byte[81920];
            long remaining = length;

            while (remaining > 0)
            {
                int bytesToRead = (int)Math.Min(buffer.Length, remaining);
                int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead), token);

                if (bytesRead <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of file while splitting the live photo. The file might be corrupted.");
                }

                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
                remaining -= bytesRead;
            }
        }

        // 复制 JPEG 字节流到目标，过程中跳过包含实况照片元数据的 APP 段（XMP/EXIF），
        // 避免拆分出的图片仍带有 GCamera:MicroVideo / MotionPhoto 等标记，
        // 防止下次扫描时再次被误识别为实况照片。
        private static async Task CopyJpegStrippingLivePhotoMetadataAsync(Stream sourceStream, Stream destinationStream, long imageLength, CancellationToken token)
        {
            // 1. 确保起始是 SOI (0xFF 0xD8)
            byte[] soi = new byte[2];
            if (await ReadExactAsync(sourceStream, soi, 2, token) != 2 || soi[0] != 0xFF || soi[1] != 0xD8)
            {
                throw new InvalidDataException("Split image region is not a valid JPEG (missing SOI).");
            }
            await destinationStream.WriteAsync(soi.AsMemory(0, 2), token);
            long consumedInImage = 2;

            byte[] header = new byte[4];     // [0][1] 存 Marker，[2][3] 存 Length
            byte[] temp2 = new byte[2];      // 专门用于读取的2字节小缓冲区，避免指针错位
            byte[] singleByte = new byte[1]; // 用于跳过多余填充字节的单字节缓冲区
            byte[] segmentBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                while (consumedInImage < imageLength)
                {
                    token.ThrowIfCancellationRequested();

                    // 1. 读取 Marker (0xFF ??) 到 temp2
                    if (await ReadExactAsync(sourceStream, temp2, 2, token) != 2)
                    {
                        break; // EOF
                    }
                    consumedInImage += 2;

                    // 兼容性保护：JPEG 规范允许段之间有多个连续的 0xFF 作为填充字节
                    while (temp2[0] == 0xFF && temp2[1] == 0xFF)
                    {
                        await destinationStream.WriteAsync(temp2.AsMemory(0, 1), token); // 将多余的 0xFF 原样写入
                        temp2[0] = temp2[1];
                        if (await ReadExactAsync(sourceStream, singleByte, 1, token) != 1) break;
                        temp2[1] = singleByte[0];
                        consumedInImage += 1;
                    }

                    // 记录真实 Marker
                    header[0] = temp2[0];
                    header[1] = temp2[1];
                    byte marker = header[1];

                    // 遇到 SOS (0xDA)：写入标记后，剩余全部为压缩图像核心像素数据，直接原样拷贝并跳出
                    if (marker == 0xDA)
                    {
                        await destinationStream.WriteAsync(header.AsMemory(0, 2), token);
                        long remainingInImage = imageLength - consumedInImage;
                        if (remainingInImage > 0)
                        {
                            await CopyExactLengthAsync(sourceStream, destinationStream, remainingInImage, token);
                            consumedInImage += remainingInImage;
                        }
                        break;
                    }

                    // 遇到无长度字段的独立标记（如 RSTn 0xD0-0xD7、SOI 0xD8、EOI 0xD9、0x00 填充）
                    if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01 || marker == 0x00)
                    {
                        await destinationStream.WriteAsync(header.AsMemory(0, 2), token);
                        if (marker == 0xD9) break; // 遇到 EOI 正常结束
                        continue;
                    }

                    // 2. 读取当前段的长度字段 (2 字节)
                    if (await ReadExactAsync(sourceStream, temp2, 2, token) != 2)
                    {
                        throw new EndOfStreamException("Unexpected EOF while reading segment length.");
                    }
                    consumedInImage += 2;
                    header[2] = temp2[0];
                    header[3] = temp2[1];

                    int segmentLength = (header[2] << 8) | header[3];
                    if (segmentLength < 2)
                    {
                        throw new InvalidDataException($"Invalid JPEG segment length: {segmentLength}");
                    }
                    int segmentPayloadLength = segmentLength - 2;

                    // 3. 仅对 APP 段 (0xE0 - 0xEF) 进行实况照片 XMP 嗅探
                    if (marker >= 0xE0 && marker <= 0xEF)
                    {
                        int sniffLength = Math.Min(segmentPayloadLength, segmentBuffer.Length);
                        if (sniffLength > 0)
                        {
                            if (await ReadExactAsync(sourceStream, segmentBuffer, sniffLength, token) != sniffLength)
                            {
                                throw new EndOfStreamException("Unexpected EOF while sniffing APP payload.");
                            }
                            consumedInImage += sniffLength;
                        }

                        bool isLivePhotoSegment = sniffLength > 0 && ContainsLivePhotoMarker(segmentBuffer, sniffLength);
                        int remainingPayload = segmentPayloadLength - sniffLength;

                        if (isLivePhotoSegment)
                        {
                            // 💡【核心剔除逻辑】：如果命中实况照片元数据
                            // 跳过剩余流内容，并且 **绝不写入** 这 4 字节的 Header 和已经嗅探的内容！
                            if (remainingPayload > 0)
                            {
                                await SkipExactAsync(sourceStream, remainingPayload, token);
                                consumedInImage += remainingPayload;
                            }
                            LogService.Split($"Stripped LivePhoto APP{marker - 0xE0} segment (len={segmentLength})", LogLevel.Debug);
                        }
                        else
                        {
                            // 正常元数据 (如 EXIF，ICC 色彩配置等)：原样保留
                            await destinationStream.WriteAsync(header.AsMemory(0, 4), token);
                            if (sniffLength > 0)
                            {
                                await destinationStream.WriteAsync(segmentBuffer.AsMemory(0, sniffLength), token);
                            }
                            if (remainingPayload > 0)
                            {
                                await CopyExactLengthAsync(sourceStream, destinationStream, remainingPayload, token);
                                consumedInImage += remainingPayload;
                            }
                        }
                    }
                    else
                    {
                        // 非 APP 图像必要段 (如 DQT, DHT, SOF)：原封不动完整写入
                        await destinationStream.WriteAsync(header.AsMemory(0, 4), token);
                        if (segmentPayloadLength > 0)
                        {
                            await CopyExactLengthAsync(sourceStream, destinationStream, segmentPayloadLength, token);
                            consumedInImage += segmentPayloadLength;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(segmentBuffer);
            }

            // 兜底：如果还有剩余字节未读取完（如文件尾部的其他附加数据），原样写出保证不出错
            if (consumedInImage < imageLength)
            {
                long remainder = imageLength - consumedInImage;
                await CopyExactLengthAsync(sourceStream, destinationStream, remainder, token);
            }
        }
        

        // 检测 APP1 段是否包含实况照片元数据，判断是否为需要剥离的元数据。
        // 检测顺序：
        // 1. 先看是否包含本应用的 LivePhotoBox 命名空间标记（最精确）
        // 2. 如果没有，再回退到通用实况照片命名空间检测（兼容早期没有标记的旧文件）
        private static bool ContainsLivePhotoMarker(byte[] buffer, int length)
        {
            ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(buffer, 0, length);
            ReadOnlySpan<byte> xmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8;
            if (data.Length < xmpHeader.Length) return false;
            if (!data[..xmpHeader.Length].SequenceEqual(xmpHeader)) return false;

            // 精确检测：本应用的 LivePhotoBox 标记（Merge / Split 合成时注入，WrapXmp 统一写入）
            if (data.IndexOf("xmlns:LivePhotoBox=\"https://github.com/LengxiQwQ/live-photo-box\""u8) >= 0) return true;

            // 回退检测：通用实况照片命名空间（兼容旧版本应用合成的文件）
            if (data.IndexOf("xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\""u8) >= 0) return true;
            if (data.IndexOf("xmlns:Container=\"http://ns.google.com/photos/1.0/container/\""u8) >= 0) return true;
            if (data.IndexOf("xmlns:OpCamera=\"http://ns.oplus.com/photos/1.0/camera/\""u8) >= 0) return true;
            if (data.IndexOf("xmlns:MiCamera=\"http://ns.xiaomi.com/photos/1.0/camera/\""u8) >= 0) return true;
            return false;
        }

        // Clear the OPPO <c>oplus_*</c> marker from EXIF UserComment —
        // but ONLY when the current value starts with "oplus_".
        // If UserComment contains any other content (camera notes, custom remarks, etc.),
        // it is left completely untouched.
        private static async Task ClearOppoExifMarkerAsync(string imagePath, CancellationToken token)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath)) return;

                // Read current UserComment value
                string? currentValue = null;
                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-UserComment -s -s -S \"{imagePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null) return;
                    currentValue = (process.StandardOutput.ReadToEnd()).Trim();
                    process.WaitForExit(5000);
                }

                // Only clear if the value is an oplus_ marker
                if (string.IsNullOrEmpty(currentValue)
                    || !currentValue.StartsWith("oplus_", StringComparison.OrdinalIgnoreCase))
                    return;

                LogService.Split(
                    $"Clearing OPPO EXIF UserComment: '{currentValue}' → (empty)",
                    LogLevel.Debug);

                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-overwrite_original",
                    "-UserComment=",
                    imagePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split(
                    $"Failed to clear OPPO EXIF UserComment: {ex.Message}",
                    LogLevel.Warning);
            }
        }

        private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), token);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static async Task SkipExactAsync(Stream stream, long count, CancellationToken token)
        {
            if (stream.CanSeek)
            {
                stream.Seek(count, SeekOrigin.Current);
                return;
            }
            byte[] buffer = new byte[81920];
            long remaining = count;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token);
                if (read <= 0) break;
                remaining -= read;
            }
        }

        // 将源 JPEG 的关键元数据（ContentIdentifier、拍摄日期）写回拆分出的视频文件，
        // 确保后续元数据匹配能识别拆分后的视频与照片属于同一实况照片。
        private static async Task CopyMetadataToVideoAsync(
            string sourceImagePath, string videoOutputPath, CancellationToken token)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(exifToolPath))
                return;

            try
            {
                // 1. 从源图片读取元数据
                string readOutput;
                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-j");
                psi.ArgumentList.Add("-ContentIdentifier");
                psi.ArgumentList.Add("-DateTimeOriginal");
                psi.ArgumentList.Add("-OffsetTimeOriginal");
                psi.ArgumentList.Add("-Make");
                psi.ArgumentList.Add("-Model");
                psi.ArgumentList.Add("-GPSLatitude");
                psi.ArgumentList.Add("-GPSLongitude");
                psi.ArgumentList.Add("-GPSAltitude");
                psi.ArgumentList.Add("-GPSLatitudeRef");
                psi.ArgumentList.Add("-GPSLongitudeRef");
                psi.ArgumentList.Add(sourceImagePath);

                using (var process = Process.Start(psi))
                {
                    if (process == null) return;
                    readOutput = await process.StandardOutput.ReadToEndAsync(token);
                    await process.WaitForExitAsync(token);
                }

                if (string.IsNullOrWhiteSpace(readOutput) || !readOutput.TrimStart().StartsWith("["))
                    return;

                using var doc = System.Text.Json.JsonDocument.Parse(readOutput);
                var root = doc.RootElement[0];

                string cid = TryGetJsonString(root, "ContentIdentifier");
                string dto = TryGetJsonString(root, "DateTimeOriginal");
                string offset = TryGetJsonString(root, "OffsetTimeOriginal");
                string make = TryGetJsonString(root, "Make");
                string model = TryGetJsonString(root, "Model");
                string gpsLat = TryGetJsonString(root, "GPSLatitude");
                string gpsLon = TryGetJsonString(root, "GPSLongitude");
                string gpsAlt = TryGetJsonString(root, "GPSAltitude");
                string gpsLatRef = TryGetJsonString(root, "GPSLatitudeRef");
                string gpsLonRef = TryGetJsonString(root, "GPSLongitudeRef");

                // 2. 写入视频文件
                var writeArgs = new List<string>();
                writeArgs.Add("-overwrite_original");

                if (!string.IsNullOrWhiteSpace(cid))
                    writeArgs.Add($"-ContentIdentifier={cid}");

                if (!string.IsNullOrWhiteSpace(dto))
                {
                    // 拼接时区偏移，确保视频写入的是正确的 UTC 时间
                    string dateWithOffset = string.IsNullOrWhiteSpace(offset) ? dto : dto + offset;
                    writeArgs.Add($"-CreateDate={dateWithOffset}");
                }

                if (!string.IsNullOrWhiteSpace(make))
                    writeArgs.Add($"-Make={make}");

                if (!string.IsNullOrWhiteSpace(model))
                    writeArgs.Add($"-Model={model}");

                // GPS：拼接纬度/经度和方向标识
                if (!string.IsNullOrWhiteSpace(gpsLat))
                    writeArgs.Add($"-GPSLatitude={gpsLat}");
                if (!string.IsNullOrWhiteSpace(gpsLatRef))
                    writeArgs.Add($"-GPSLatitudeRef={gpsLatRef}");
                if (!string.IsNullOrWhiteSpace(gpsLon))
                    writeArgs.Add($"-GPSLongitude={gpsLon}");
                if (!string.IsNullOrWhiteSpace(gpsLonRef))
                    writeArgs.Add($"-GPSLongitudeRef={gpsLonRef}");
                if (!string.IsNullOrWhiteSpace(gpsAlt))
                    writeArgs.Add($"-GPSAltitude={gpsAlt}");

                if (writeArgs.Count > 1) // 有除了 -overwrite_original 之外的参数
                {
                    writeArgs.Add(videoOutputPath);
                    LogService.Split($"Writing metadata to split video: CID={(string.IsNullOrWhiteSpace(cid) ? "none" : cid)}, Date={dto}, Make={make}, GPS={gpsLat},{gpsLon}", LogLevel.Debug);
                    await LivePhotoRepairService.RunExifToolAsync(token, writeArgs.ToArray());
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split($"Failed to copy metadata to split video: {ex.Message}", LogLevel.Warning);
            }
        }

        // 安全地从 JsonElement 读取字符串属性值，仅当 ValueKind 为 String 时返回。
        private static string TryGetJsonString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? "";
            return "";
        }

        // 从正则匹配的 "value" 命名组中安全解析 long 值。
        private static bool TryGetLong(Match match, out long value)
        {
            value = 0;
            string rawValue = match.Groups["value"].Value;
            return !string.IsNullOrWhiteSpace(rawValue) && long.TryParse(rawValue, out value);
        }
    }
}