using LivePhotoBox.Models;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using NeutralMediaBundle = LivePhotoBox.Protocols.Cleaning.NeutralMediaBundle;
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
using System.Xml.Linq;

/*
 * LivePhotoSplitService.cs
 *
 * 实况照片拆分核心。
 *
 *   - 将合成的实况照片拆回独立的图片与视频
 *   - 图片端按 JPEG 段结构逐段重建：对 XMP 做 XML 结构化清洗，只删除实况照片字段，
 *     保留 HDR GainMap、版权、评分等普通 XMP；EXIF/ICC/图像数据原样保留
 *   - 不直接按字节截断的原因：截断后图片端仍保留"我是实况照片"的标记，
 *     再次扫描会被误判为实况照片，构成"假阳性循环"
 *   - 嗅探按结构匹配（APP 段 + Adobe XMP 29 字节固定头 + Google 命名空间），
 *     不按关键词，避免误伤 EXIF 段与含 Motion/MicroVideo 字样的普通 XMP 段
 */

namespace LivePhotoBox.Services
{
    public static class LivePhotoSplitService
    {
        private const int MetadataProbeBytes = 1024 * 1024; // 探测前 1MB 的元数据

        private static readonly byte[] XmpHeaderBytes = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

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

        public static Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory, int protocolIndex, int outputFormatIndex, CancellationToken token, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, long? keyTimestampUs = null)
        {
            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
            {
                return ProcessingPipelineRouter.RunRebuiltAsync("split", () => SplitRebuiltAsync(
                    sourcePath, outputDirectory, protocolIndex, outputFormatIndex, token,
                    inputDirectory, outputBaseName, overwriteExisting));
            }

            return ProcessingPipelineRouter.RunAsync("split", () => SplitLegacyAsync(
                sourcePath, outputDirectory, protocolIndex, outputFormatIndex, token,
                inputDirectory, outputBaseName, overwriteExisting, keyTimestampUs));
        }

        // First rebuilt split slice: materialize a source single-file live photo into
        // two independently usable media files. Target protocol metadata writers are
        // intentionally not entered here yet; protocolIndex 0 is the format-validation
        // path for the current stage.
        private static async Task<LivePhotoSplitResult> SplitRebuiltAsync(
            string sourcePath,
            string outputDirectory,
            int protocolIndex,
            int outputFormatIndex,
            CancellationToken token,
            string? inputDirectory,
            string? outputBaseName,
            bool overwriteExisting)
        {
            if (protocolIndex != ProtocolFormatMatrix.SplitProtocolNone)
            {
                throw new InvalidOperationException(
                    "Rebuilt split target protocol writers are not enabled yet; use --protocol none for format conversion validation.");
            }

            MediaFormatRequirement requirement = ProtocolMediaRequirements.GetSplitRequirement(
                protocolIndex, outputFormatIndex);
            using var workspace = new MediaWorkspace();
            NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                sourcePath,
                secondaryPath: null,
                workspace,
                requirement,
                PreservationPolicy.BestEffort,
                token).ConfigureAwait(false);

            if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))
                throw new InvalidDataException("Rebuilt split requires a Native-inspected motion video.");
            if (bundle.GainMap != null)
                throw new InvalidOperationException(
                    "Rebuilt split cannot yet reassemble an embedded GainMap into the standalone image without losing HDR data.");

            string imageExtension = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".HEIC" : ".JPG";
            string videoExtension = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".MOV" : ".MP4";
            (string imageOutputPath, string videoOutputPath) = BuildOutputPaths(
                sourcePath,
                outputDirectory,
                imageExtension,
                videoExtension,
                inputDirectory,
                outputBaseName,
                overwriteExisting);

            try
            {
                File.Copy(bundle.PrimaryImage.Path, imageOutputPath, overwrite: true);
                File.Copy(bundle.MotionVideo.Path, videoOutputPath, overwrite: true);
                return new LivePhotoSplitResult
                {
                    ImageOutputPath = imageOutputPath,
                    VideoOutputPath = videoOutputPath
                };
            }
            catch
            {
                try { if (File.Exists(imageOutputPath)) File.Delete(imageOutputPath); } catch { }
                try { if (File.Exists(videoOutputPath)) File.Delete(videoOutputPath); } catch { }
                throw;
            }
        }

        private static async Task<LivePhotoSplitResult> SplitLegacyAsync(string sourcePath, string outputDirectory, int protocolIndex, int outputFormatIndex, CancellationToken token, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, long? keyTimestampUs = null)
        {
            Directory.CreateDirectory(outputDirectory);

            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length <= 0)
            {
                throw new InvalidDataException("Source file is empty.");
            }

            string metadataText = await ReadMetadataTextAsync(sourceStream, token);

            // ── 1. 检测容器：JPEG（FF D8）还是 HEIC（ftyp）──────────────
            sourceStream.Position = 0;
            byte[] header = new byte[12];
            int headerRead = await sourceStream.ReadAsync(header, token);
            sourceStream.Position = 0;
            bool sourceImageIsJpeg = headerRead >= 2 && header[0] == 0xFF && header[1] == 0xD8;
            bool sourceImageIsHeic = !sourceImageIsJpeg && headerRead >= 8
                && header[4] == (byte)'f' && header[5] == (byte)'t'
                && header[6] == (byte)'y' && header[7] == (byte)'p';

            // ── 2. 检测协议（已知容器类型），复用 LivePhotoProtocolDetector ──
            LivePhotoType livePhotoType = sourceImageIsJpeg
                ? LivePhotoType.SingleFileJpeg
                : LivePhotoType.SingleFileHeic;
            LivePhotoProtocolType protocol = LivePhotoProtocolDetector.Detect(
                sourcePath, livePhotoType, contentIdentifier: null, xmpText: metadataText);

            // ── 3. 按容器 + 协议分流，计算「图片 + 视频」的分段 ──────────
            long imageLength;
            long videoStart;
            long videoLength;

            switch (protocol)
            {
                case LivePhotoProtocolType.Huawei:
                {
                    // 华为/荣耀：[静态图] + [中间嵌入 MP4] + [尾部]，用 moov/ftyp 定位。
                    var range = GetHuaweiEmbeddedVideoRange(sourcePath);
                    if (range == null)
                    {
                        throw new InvalidDataException("Unable to locate the embedded HUAWEI/Honor video.");
                    }
                    imageLength = range.Value.videoStart;
                    videoStart = range.Value.videoStart;
                    videoLength = range.Value.videoLength;
                    break;
                }

                case LivePhotoProtocolType.Samsung:
                case LivePhotoProtocolType.Fusion:
                {
                    if (sourceImageIsJpeg)
                    {
                        // 三星/融合 JPEG：图片 = JPEG 到 EOI，视频在 Samsung Trailer 的 MotionPhoto_Data 标签里。
                        long eoiEnd = await FindJpegEoiEndOffsetAsync(sourceStream, token);
                        if (eoiEnd <= 0)
                        {
                            throw new InvalidDataException("Unable to locate JPEG EOI for Samsung Motion Photo.");
                        }
                        var trailer = FindSamsungJpegVideoRange(sourcePath);
                        if (trailer == null)
                        {
                            throw new InvalidDataException("Unable to locate the Samsung MotionPhoto_Data video.");
                        }
                        imageLength = eoiEnd;
                        videoStart = trailer.Value.videoStart;
                        videoLength = trailer.Value.videoLength;
                    }
                    else
                    {
                        // 三星 HEIC：视频在 mpvd box 里（sefd box 之前）。
                        var mpvd = FindHeicMpvdRange(sourcePath);
                        if (mpvd == null)
                        {
                            throw new InvalidDataException("Unable to locate the mpvd box for Samsung HEIC.");
                        }
                        imageLength = mpvd.Value.imageLength;
                        videoStart = mpvd.Value.videoStart;
                        videoLength = mpvd.Value.videoLength;
                    }
                    break;
                }

                default:
                {
                    if (sourceImageIsHeic)
                    {
                        // Google V2 / 其它 HEIC：[HEIC][mpvd box: 8 字节头 + 视频]。
                        // XMP 的 Item:Length 只算视频、不含 8 字节 mpvd 头，直接按 XMP 偏移切片会把
                        // mpvd 头并入图片导致坏图 → 必须按 mpvd box 定位。
                        var mpvd = FindHeicMpvdRange(sourcePath);
                        if (mpvd == null)
                        {
                            throw new InvalidDataException("Unable to locate the mpvd box for HEIC Motion Photo.");
                        }
                        imageLength = mpvd.Value.imageLength;
                        videoStart = mpvd.Value.videoStart;
                        videoLength = mpvd.Value.videoLength;
                    }
                    else
                    {
                        // Google V1/V2 / 小米 / OPPO / vivo / 未知 JPEG：XMP 偏移 + 文件尾追加视频（现有路径）。
                        videoLength = GetAppendedVideoLength(metadataText);
                        imageLength = sourceStream.Length - videoLength;
                        videoStart = imageLength;
                    }
                    break;
                }
            }

            if (imageLength <= 0 || videoStart <= 0 || videoLength <= 0)
            {
                throw new InvalidDataException("Unable to determine the image/video region or file is corrupted.");
            }

            // ── 协议 → 输出格式 → 编码 契约（全局 outputFormatIndex，与 protocolIndex 无关）─────────
            //   0 = 默认：图片/视频均原样输出（不转图片、不转码，等价旧「图片默认」）
            //   1 = JPG + MOV（H.265/HEVC）
            //   2 = HEIC + MOV（H.265/HEVC）
            //   3 = JPG + MP4（H.264/AVC）
            //   protocolIndex（0=无协议 / 1=Apple / 2=vivo）。
            //   vivo 双文件配对标记在输出落盘后由 VivoDualFileMetadataWriter 写入。
            // ──────────────────────────────────────────────────────────────────────────────
            string targetImageExtension = outputFormatIndex switch
            {
                1 or 3 => ".JPG",
                2 => ".HEIC",
                _ => Path.GetExtension(sourcePath) // 0 = 默认：图片跟随源扩展名
            };

            string targetVideoExtension = outputFormatIndex switch
            {
                1 or 2 => ".MOV",
                3 => ".MP4",
                _ => await ResolveVideoExtensionAsync(sourceStream, videoStart, metadataText, 0, token)
            };

            (string imageOutputPath, string videoOutputPath) = BuildOutputPaths(sourcePath, outputDirectory, targetImageExtension, targetVideoExtension, inputDirectory, outputBaseName, overwriteExisting);

            string tempDir = Path.Combine(outputDirectory, "Temp");
            Directory.CreateDirectory(tempDir);
            string tempImagePath = TempFileService.AllocateTempPath(tempDir, "split_image", sourceImageIsJpeg ? "jpg" : "heic");
            string? convertedImagePath = null;
            string tempVideoPath = Path.Combine(tempDir, Path.GetFileName(videoOutputPath) + ".tmp");

            try
            {
                // 1. 提取图片部分到临时文件
                sourceStream.Position = 0;
                await using (var imageOutputStream = new FileStream(tempImagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (sourceImageIsJpeg)
                        await CopyJpegStrippingLivePhotoMetadataAsync(sourceStream, imageOutputStream, imageLength, token);
                    else
                        await CopyExactLengthAsync(sourceStream, imageOutputStream, imageLength, token);
                }

                // HEIC 源：meta box 里的 XMP（谷歌 V2 / 三星 / 融合）仍是「我是实况照片」签名，用 exiftool 整组剥离。
                // 华为 HEIC 无 XMP，此步为空操作（best-effort）。
                // 注意：Apple 目标走字节级无损注入，exiftool 整文件重写会破坏「原样保留」并多塞空 XMP，
                // 故 Apple 目标跳过此步（配对靠 MakerNote，不依赖此处 XMP 剥离）。
                if (sourceImageIsHeic && protocolIndex != 1)
                {
                    await StripHeicXmpAsync(tempImagePath, token);
                }

                // OPPO 协议在 EXIF UserComment 里写了 "oplus_10485792" 标记（供 OPPO 相册识别）。
                // XMP 段已在上面被剥离，但 EXIF 段原样保留了 → 需单独清理。
                // 只清以 "oplus_" 开头的值，不碰其他内容的 UserComment。HEIC 源无此 EXIF 段，跳过。
                if (sourceImageIsJpeg
                    && protocol is LivePhotoProtocolType.OPPO or LivePhotoProtocolType.Fusion)
                {
                    await ClearOppoExifMarkerAsync(tempImagePath, token);
                }

                // vivo X300 在 EXIF UserComment 里写了 multi-frame 签名（供 vivo 相册识别），同样需清理。
                if (sourceImageIsJpeg && protocol == LivePhotoProtocolType.Vivo)
                {
                    await ClearVivoExifMarkerAsync(tempImagePath, token);
                }

                // Apple 残留（双文件标记）：拆出的图片要单独使用，不能带 Apple ContentIdentifier。
                // 复用 SourceProtocolCleaner 的清洗（exiftool -ContentIdentifier= + vivo 尾标）；
                // Apple 目标输出会重建配对标记，不在此列（仅无协议大清洗时执行）。
                if ((protocolIndex == 0 || protocolIndex == 2) && sourceImageIsJpeg)
                {
                    await Protocols.SourceProtocolCleaner.CleanImageMarkersInPlaceAsync(tempImagePath, token);
                }

                // Apple 实况条目字节级剥离（0x0011 ContentIdentifier / 0x0017 LivePhotoVideoIndex /
                // 0x0025，见协议文档 Apple 章节）：exiftool 只能清空 CID 值、删不掉 type=16 条目
                // （-LivePhotoVideoIndex= 等四种写法实测无效），且 HEIC 源此前被 sourceImageIsJpeg
                // 门控整体跳过。此步骤对 JPEG 与 HEIC 源统一执行，保持 MN 长度不变不破坏结构。
                if (protocolIndex == 0 || protocolIndex == 2)
                {
                    Protocols.AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(
                        tempImagePath, out string? stripMnError);
                    if (stripMnError != null)
                    {
                        LogService.Split(
                            $"Apple MakerNote strip failed (non-fatal): {stripMnError}",
                            LogLevel.Warning);
                    }
                }

                // Apple 协议：图片端 Apple MakerNote 必须在格式转换前注入到源 JPG。
                // heif-enc（libheif）/ heif-dec（ExifTool 元数据复制）会原样保留 MakerNote。
                // 本软件合成/苹果衍生源的单文件保留了源苹果的 MakerNote（CID 被清空、条目仍在），
                // 就地重建为 70 字节最小 MN（起点/长度不变）；原生相机 JPEG（小米/OPPO 等无
                // 0x927C 条目）走 APP1 注入（新增条目）；原生 HEIC（华为等）解码为 JPEG 桥接
                // 注入，目标为 HEIC 时再编码回 HEIC（heif-enc 保留 EXIF MakerNote）。
                string? appleContentId = null;
                string? appleBridgeJpeg = null; // HEIC 源经 JPEG 桥接注入 MN 的中间文件
                if (protocolIndex == 1)
                {
                    appleContentId = Guid.NewGuid().ToString("D").ToUpperInvariant();
                    bool mnOk = Protocols.AppleMakerNoteWriter.TryWriteContentIdentifier(
                        tempImagePath, appleContentId, out string? mnError);
                    if (!mnOk)
                    {
                        if (sourceImageIsJpeg)
                        {
                            byte[] makerNote = Protocols.AppleMakerNoteWriter.BuildMakerNote(appleContentId);
                            mnOk = Protocols.AppleMakerNoteWriter.TryInjectIntoJpeg(
                                tempImagePath, makerNote, out mnError);
                        }
                        else
                        {
                            // 无损优先：直接在 HEIC 容器的 Exif item 里原位写入 Apple MakerNote，
                            // 不重编码像素，保留 10-bit 子图 / 增益图 / 辅助图 / 厂商私有数据。
                            // 结构不认识或容量不足时回退 JPEG 桥接（老套路）。
                            bool heicDirectOk = Protocols.AppleMakerNoteWriter.TryInjectAppleMakerNoteIntoHeic(
                                tempImagePath, appleContentId, out string? heicDirectError);
                            if (heicDirectOk)
                            {
                                mnOk = true;
                            }
                            else
                            {
                                LogService.Split(
                                    $"Apple[image] lossless HEIC injection failed ({heicDirectError}), falling back to JPEG bridge",
                                    LogLevel.Warning);
                                appleBridgeJpeg = await HeicConverterService.ConvertToJpegAsync(
                                    tempImagePath, tempDir, token);
                                byte[] makerNote = Protocols.AppleMakerNoteWriter.BuildMakerNote(appleContentId);
                                mnOk = Protocols.AppleMakerNoteWriter.TryInjectIntoJpeg(
                                    appleBridgeJpeg, makerNote, out mnError);
                            }
                        }
                    }
                    if (!mnOk)
                    {
                        LogService.Split(
                            $"Apple[image] pre-convert MakerNote injection failed: {mnError}",
                            LogLevel.Warning);
                    }
                }

                // 按目标图片格式转换（复用 HeicConverterService，不自行另写转换逻辑）
                bool targetImageIsHeic = targetImageExtension.Equals(".heic", StringComparison.OrdinalIgnoreCase);
                string workingImagePath = tempImagePath;
                if (appleBridgeJpeg != null)
                {
                    // HEIC 源桥接注入后：目标 HEIC → 编码回；目标 JPG → 直接用桥接 JPEG。
                    if (targetImageIsHeic)
                    {
                        if (sourceImageIsHeic)
                        {
                            // HDR 保留：HEIC 源像素走 16-bit PNG（heif-dec -> heif-enc -b 10 + nclx/CLLI），
                            // EXIF（含 Apple MakerNote）整体取自 JPEG 桥接（先清后拷，保证唯一 0x927C）。
                            try
                            {
                                convertedImagePath = await HeicConverterService.ConvertHeicToHeicPreservingAsync(
                                    tempImagePath, tempDir, token,
                                    exifSourcePath: appleBridgeJpeg, metadataSourcePath: sourcePath);
                                workingImagePath = convertedImagePath;
                            }
                            catch (Exception ex)
                            {
                                LogService.Split(
                                    $"Apple[image] HDR-preserving HEIC encode failed ({ex.Message}), falling back to JPEG bridge",
                                    LogLevel.Warning);
                                convertedImagePath = await HeicConverterService.ConvertToHeicAsync(
                                    appleBridgeJpeg, tempDir, token);
                                workingImagePath = convertedImagePath;
                            }
                        }
                        else
                        {
                            convertedImagePath = await HeicConverterService.ConvertToHeicAsync(
                                appleBridgeJpeg, tempDir, token);
                            workingImagePath = convertedImagePath;
                        }
                    }
                    else
                    {
                        workingImagePath = appleBridgeJpeg;
                    }
                }
                else if (targetImageIsHeic && sourceImageIsJpeg)
                {
                    convertedImagePath = await HeicConverterService.ConvertToHeicAsync(tempImagePath, tempDir, token);
                    workingImagePath = convertedImagePath;
                }
                else if (!targetImageIsHeic && !sourceImageIsJpeg)
                {
                    convertedImagePath = await HeicConverterService.ConvertToJpegAsync(tempImagePath, tempDir, token);
                    workingImagePath = convertedImagePath;
                }

                // 无损 HEIC→HEIC：workingImagePath 仍是 tempImagePath（未重编码）。
                // 后续不要再对图片跑 exiftool（会重写容器、加 XMP，破坏「原样保留」）。
                bool imageIsLosslessHeic = sourceImageIsHeic
                    && targetImageIsHeic
                    && string.Equals(workingImagePath, tempImagePath, StringComparison.Ordinal);

                // 图片落位到最终输出路径（BuildOutputPaths 已预留 0 字节占位文件，需先删除）
                if (File.Exists(imageOutputPath))
                    File.Delete(imageOutputPath);
                File.Move(workingImagePath, imageOutputPath);

                // 2. 提取视频部分到临时文件
                sourceStream.Position = videoStart;
                await using (var videoOutputStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyExactLengthAsync(sourceStream, videoOutputStream, videoLength, token);
                }

                // 3. 视频处理：默认(0)原样输出；1/2 → MOV+H.265；3 → MP4+H.264
                // 无协议拆分：先剥离视频里残留的实况协议元数据（单文件 + 双文件残留），再移动/转码。
                // 拆出的视频要单独使用，不能带任何厂商实况标记。
                if (protocolIndex == 0 || protocolIndex == 2)
                {
                    // 单文件协议键：HUAWEI com.openharmony.*
                    if (!Protocols.Mp4MdtaKeyStripper.TryStripHuaweiKeys(tempVideoPath, out string? stripError))
                    {
                        LogService.Split($"Video vendor metadata strip failed (non-fatal): {stripError}", LogLevel.Warning);
                    }
                    // Track 3（com.openharmony.timed_metadata.movingphoto），属单文件协议元数据轨
                    Protocols.Mp4MdtaKeyStripper.TryStripTracks(
                        tempVideoPath, ["com.openharmony.timed_metadata.movingphoto"], out _);
                    // 双文件残留：Apple mdta keys（content.identifier/live-photo/vitality）、
                    // mebx 实况时序轨、vivoMediaExtInfo uuid box（复用合成端清洗器）
                    Protocols.SourceProtocolCleaner.CleanVideoMarkersInPlace(tempVideoPath);
                }

                if (outputFormatIndex == 0)
                {
                    // 不需要转码，直接移动临时文件到目标位置
                    if (File.Exists(videoOutputPath))
                        File.Delete(videoOutputPath);
                    File.Move(tempVideoPath, videoOutputPath);
                }
                else
                {
                    if (File.Exists(videoOutputPath))
                        File.Delete(videoOutputPath);
                    var transcodeResult = outputFormatIndex switch
                    {
                        // Apple 协议（protocolIndex==1）：HEVC 转码成全 I 帧（-g 1）。
                        // iOS 实况照片编辑器的拖动预览按同步样本取帧；只有关键帧可选，
                        // 常规 GOP（-g 15）会导致 3s 视频只有 7 帧可选、拖动卡顿。
                        // 全 I 帧后 stss 覆盖每一帧，编辑器可逐帧拖动并任选封面。
                        1 or 2 => await VideoTranscodeService.TranscodeToMovAsync(
                            tempVideoPath, videoOutputPath, token, videoCodec: "hevc",
                            keyframeInterval: protocolIndex == 1 ? 1 : null),
                        3 => await VideoTranscodeService.TranscodeToMp4Async(tempVideoPath, videoOutputPath, token, videoCodec: "h264"),
                        _ => throw new InvalidOperationException($"Unsupported output format index: {outputFormatIndex}")
                    };
                    if (!transcodeResult.Success)
                        throw new InvalidOperationException($"Video transcode failed: {transcodeResult.ErrorMessage}");
                }

                // 4. 将源文件的关键元数据写回视频输出（供后续元数据匹配使用）
                // 读清洗后的图片（tempImagePath）而非原始单文件：保证视频元数据与
                // 输出图片一致，不会把已被清洗掉的厂商标记（Make=HUAWEI 等）再带回去。
                await CopyMetadataToVideoAsync(tempImagePath, videoOutputPath, token);

                // 5. 给图片和视频打上 LivePhotoBox 标记（标识经本软件拆分过）
                // 无损 HEIC 图片跳过：exiftool 会重写容器并加 XMP，破坏原样保留。
                if (!imageIsLosslessHeic)
                {
                    await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                        imageOutputPath, "Split", "", token);
                }
                await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                    videoOutputPath, "Split", "", token);

                // ── 按 protocolIndex 写入双文件配对元数据 ───────────────────────────────
                // protocolIndex == 1（Apple）：给图片与视频两端写入配对元数据，
                //   使 Apple Photos 将两者识别为一对实况照片。
                // protocolIndex == 2（vivo）：在 JPG 尾部追加 vivo JSON 尾标，
                //   并在 MP4 写入 vivoMediaExtInfo uuid box，两端使用同一配对 ID。
                if (protocolIndex == 1)
                {
                    await Protocols.AppleLivePhotoMetadata.WritePairMetadataAsync(
                        sourcePath, metadataText, imageOutputPath, videoOutputPath, appleContentId, keyTimestampUs, token);
                }
                else if (protocolIndex == 2)
                {
                    await Protocols.VivoDualFileMetadataWriter.WritePairMetadataAsync(
                        imageOutputPath, videoOutputPath, token);
                }
                // ─────────────────────────────────────────────────────────────────────────────

                return new LivePhotoSplitResult
                {
                    ImageOutputPath = imageOutputPath,
                    VideoOutputPath = videoOutputPath
                };
            }
            catch
            {
                // 失败/取消时清理可能已经写入的不完整输出文件（含 BuildOutputPaths 预留的占位文件）
                try { if (File.Exists(videoOutputPath)) File.Delete(videoOutputPath); } catch { }
                try { if (File.Exists(imageOutputPath)) File.Delete(imageOutputPath); } catch { }
                throw;
            }
            finally
            {
                // 无论成功/失败/取消，临时文件都要清理
                try { if (File.Exists(tempVideoPath)) File.Delete(tempVideoPath); } catch { }
                try { if (File.Exists(tempImagePath)) File.Delete(tempImagePath); } catch { }
                if (convertedImagePath != null)
                    try { if (File.Exists(convertedImagePath)) File.Delete(convertedImagePath); } catch { }
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
        //   MicroVideo V1 → MotionPhoto V2 → OPPO O-Live Photo → 小米
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

            string diag = $"hasXmpHeader={hasXmpHeader}, " +
                          $"m1(MicroVideoOffset)={m1}, " +
                          $"m2(MotionPhotoLength)={m2}, " +
                          $"m3(OpCamera:VideoLength)={m3}, " +
                          $"m4(MiCamera:VideoLength)={m4}";

            throw new InvalidDataException(
                "No motion video length metadata was found in the file.\n" +
                $"Diagnostics: {diag}\n" +
                $"XMP header found: {hasXmpHeader}");
        }

        /// <summary>
        /// 解析 OPPO 私有字段 OpCamera:VideoLength —— 纯视频字节长度（不含 OnePlus trailer）。
        /// OPPO 原厂文件是 [JPEG][MP4][OnePlus trailer ~846KB]，Container:Directory 的
        /// Item:Length 覆盖"视频+trailer"，而 OpCamera:VideoLength 只指纯视频。
        /// 重设封面/导出时需要纯视频长度。无该字段返回 0。
        /// </summary>
        public static long GetOppoPureVideoLength(string metadataText)
        {
            var m = OppoVideoLengthRegex.Match(metadataText);
            return m.Success && long.TryParse(m.Groups["value"].Value, out long v) ? v : 0;
        }

        // ══════════════════════════════════════════════════════════════
        //  华为/荣耀 嵌入视频定位
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 从华为/荣耀实况照片二进制格式中定位嵌入的 MP4 视频段。
        /// 华为/荣耀协议 = [静态图] + [嵌入MP4(ftyp..mdat..moov)] + [可变长尾(荣耀有 uuidextend_type_matrix + 60B尾)]。
        /// 使用 moov box 结构定位 MP4 终点（而非硬编码减去固定尾长），对华为和荣耀均正确。
        /// </summary>
        /// <returns>(videoStart, videoEnd, videoLength) 或 null（定位失败）</returns>
        public static (long videoStart, long videoEnd, long videoLength)? GetHuaweiEmbeddedVideoRange(
            string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return null;

                // ── Step 1: 从文件末 256KB 找到最后一个 moov box ──
                const int tailProbe = 256 * 1024;
                int readSize = (int)Math.Min(fileSize, tailProbe);
                byte[] tailBuf = new byte[readSize];
                fs.Seek(-readSize, SeekOrigin.End);
                fs.ReadExactly(tailBuf, 0, readSize);

                int moovRelIdx = LastIndexOf(tailBuf, "moov"u8);
                long moovPos;
                uint moovSize;

                if (moovRelIdx >= 4)
                {
                    // 标准华为布局：moov 在嵌入 MP4 末尾（接近文件尾部）
                    moovPos = fileSize - readSize + moovRelIdx;
                    moovSize = ReadBigEndianU32(tailBuf, moovRelIdx - 4);
                }
                else
                {
                    // ── 回退：moov 不在文件尾部（嵌入 MP4 采用 moov-before-mdat 布局）──
                    // 例如：Apple MOV（moov 在开头）被直接作为 MP4 嵌入时，
                    // moov 距离文件尾部可能超过 256KB，上述搜索会失败。
                    // 此时从文件头跳过 HEIC ftyp 后搜索第二个 ftyp（嵌入 MP4 的 ftyp），
                    // 再向该位置之后搜索 moov box。
                    long secondFtypPos = FindSecondFtyp(fs, fileSize);
                    if (secondFtypPos < 4) return null;

                    moovPos = FindFourCCForward(fs, secondFtypPos, "moov"u8, fileSize);
                    if (moovPos < 0) return null;

                    // 读取 moov box size
                    fs.Seek(moovPos - 4, SeekOrigin.Begin);
                    Span<byte> size4 = stackalloc byte[4];
                    fs.ReadExactly(size4);
                    moovSize = ReadBigEndianU32(size4);
                }

                if (moovSize < 8 || moovSize > fileSize) return null;

                // moovEnd: box 起始 = moovPos - 4（size 字段），终止 = 起始 + moovSize
                long moovEnd = moovPos - 4 + moovSize;
                if (moovEnd > fileSize) return null;

                // ── Step 2: 在 moov 之前找最后一个 ftyp box（即嵌入 MP4 起点）──
                long ftypPos = FindLastFtypBefore(fs, moovPos);
                if (ftypPos < 4) return null;

                long videoStart = ftypPos - 4; // ftyp box 的 size 字段

                // ── Step 3: 确定视频终点 ──
                // 若 moov 在文件尾部（标准布局 ftyp→mdat→moov，或荣耀的 ftyp→mdat→moov→[uuidextend uuid box]），
                // moovEnd 即 MP4 终点，其后的荣耀 uuid box / LIVE_ 尾标都不属于视频。
                // 若 moov 不在尾部（如 ftyp→moov→mdat 布局），MP4 终点为 LIVE_ 尾标之前。
                long videoEnd;
                if (moovRelIdx >= 4)
                {
                    // moov 在文件尾部 256KB 内 → 它是 MP4 的最后一个 box，moovEnd 即视频终点
                    videoEnd = moovEnd;
                }
                else
                {
                    // moov 在 mdat 之前 → MP4 延伸到文件末的 60 字节 LIVE_ 尾标之前
                    videoEnd = fileSize - 60;
                }

                if (videoStart <= 0 || videoStart >= videoEnd || videoEnd > fileSize)
                    return null;

                long videoLength = videoEnd - videoStart;
                return (videoStart, videoEnd, videoLength);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在字节数组中从后往前搜索子序列，返回最后一个匹配的偏移</summary>
        private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern)
        {
            for (int i = data.Length - pattern.Length; i >= 0; i--)
            {
                if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                    return i;
            }
            return -1;
        }

        /// <summary>在 FileStream 中从后往前搜索最后一个 ftyp box（在 limit 之前），返回其绝对位置</summary>
        private static long FindLastFtypBefore(FileStream fs, long limit)
        {
            const int chunkSize = 64 * 1024;
            byte[] buf = new byte[chunkSize + 4];
            byte[] ftypPattern = "ftyp"u8.ToArray();
            long searchEnd = limit;

            while (searchEnd > 0)
            {
                int toRead = (int)Math.Min(chunkSize, searchEnd);
                long readPos = searchEnd - toRead;
                fs.Seek(readPos, SeekOrigin.Begin);
                int actual = fs.Read(buf, 0, toRead);
                if (actual < 4) { searchEnd = readPos; continue; }

                // 从后往前找
                for (int i = actual - 4; i >= 0; i--)
                {
                    if (buf[i] == ftypPattern[0] && buf[i + 1] == ftypPattern[1]
                        && buf[i + 2] == ftypPattern[2] && buf[i + 3] == ftypPattern[3])
                    {
                        return readPos + i;
                    }
                }
                searchEnd = readPos + 3; // overlap 3 bytes for cross-chunk ftyp
            }

            return -1;
        }

        /// <summary>从字节数组中读取 big-endian uint32</summary>
        private static uint ReadBigEndianU32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24)
                 | ((uint)data[offset + 1] << 16)
                 | ((uint)data[offset + 2] << 8)
                 | data[offset + 3];
        }

        /// <summary>从 Span 读取 big-endian uint32</summary>
        private static uint ReadBigEndianU32(ReadOnlySpan<byte> data)
        {
            return ((uint)data[0] << 24)
                 | ((uint)data[1] << 16)
                 | ((uint)data[2] << 8)
                 | data[3];
        }

        /// <summary>
        /// 定位嵌入 MP4 的 ftyp box，返回 'f' 字符的绝对偏移。
        /// HEIC 文件：跳过文件头部的第一个 ftyp，返回第二个（即嵌入 MP4 的）ftyp。
        /// JPEG 文件：文件头不是 ISOBMFF box，直接搜索第一个 ftyp。
        /// 返回 -1 表示未找到。
        /// </summary>
        private static long FindSecondFtyp(FileStream fs, long fileSize)
        {
            // 读取文件头部 4 字节，判断是否为 ISOBMFF box size
            Span<byte> header = stackalloc byte[4];
            fs.Seek(0, SeekOrigin.Begin);
            int read = fs.Read(header);
            if (read < 4) return -1;

            uint firstFour = ReadBigEndianU32(header);
            bool isIsobmff = (firstFour >= 8 && firstFour <= fileSize);

            long searchFrom;
            if (isIsobmff)
            {
                // HEIC / MP4：第一个 ftyp 在 offset 0，跳过它找第二个
                searchFrom = firstFour;
            }
            else
            {
                // JPEG / 其他：文件头不是 ISOBMFF box（如 JPEG SOI 0xFFD8），
                // 从文件开头搜索第一个（也是唯一一个）ftyp
                searchFrom = 0;
            }

            return FindFourCCForward(fs, searchFrom, "ftyp"u8, fileSize);
        }

        /// <summary>
        /// 在 FileStream 中从 startPos 向后搜索指定的 fourcc 标记，返回其绝对偏移。
        /// 使用分块扫描避免大内存分配。
        /// </summary>
        private static long FindFourCCForward(FileStream fs, long startPos,
            ReadOnlySpan<byte> fourcc, long endLimit)
        {
            const int chunkSize = 64 * 1024;
            byte[] buf = new byte[chunkSize + 4];
            long searchPos = startPos;

            while (searchPos < endLimit)
            {
                int toRead = (int)Math.Min(chunkSize, endLimit - searchPos);
                fs.Seek(searchPos, SeekOrigin.Begin);
                int actual = fs.Read(buf, 0, toRead);
                if (actual < 4) break;

                for (int i = 0; i <= actual - 4; i++)
                {
                    if (buf[i] == fourcc[0] && buf[i + 1] == fourcc[1]
                        && buf[i + 2] == fourcc[2] && buf[i + 3] == fourcc[3])
                    {
                        return searchPos + i;
                    }
                }
                // 重叠 3 字节防止 fourcc 跨块
                searchPos += actual - 3;
            }

            return -1;
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
        // 返回: (图片输出路径, 视频输出路径)
        private static (string ImageOutputPath, string VideoOutputPath) BuildOutputPaths(string sourcePath, string outputDirectory, string imageExtension, string videoExtension, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false)
        {
            string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
            // 命名模板渲染后的基本名（GUI 端已算好并消毒）；缺省时回退为源文件名。
            string baseName = string.IsNullOrWhiteSpace(outputBaseName)
                ? sourceFileNameWithoutExtension
                : outputBaseName;

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

            string imageOutputPath;
            string videoOutputPath;

            if (overwriteExisting)
            {
                // 覆盖模式：使用确定性文件名（与源同名 baseName），后续写入前删除旧文件。
                string targetDir = subDir != null ? Path.Combine(outputDirectory, subDir) : outputDirectory;
                Directory.CreateDirectory(targetDir);
                imageOutputPath = Path.Combine(targetDir, $"{baseName}{imageExtension}");
                videoOutputPath = Path.Combine(targetDir, $"{baseName}{videoExtension}");
            }
            else
            {
                imageOutputPath = PathHelper.GetUniqueFilePath(outputDirectory, $"{baseName}{imageExtension}", subDir);
                videoOutputPath = PathHelper.GetUniqueFilePath(outputDirectory, $"{baseName}{videoExtension}", subDir);
            }

            string sourceFullPath = Path.GetFullPath(sourcePath);

            // 防止输出文件覆盖掉正在读取的源文件
            if (string.Equals(Path.GetFullPath(imageOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                imageOutputPath = Path.Combine(outputDirectory, $"{baseName}_image{imageExtension}");
            }

            if (string.Equals(Path.GetFullPath(videoOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                videoOutputPath = Path.Combine(outputDirectory, $"{baseName}_video{videoExtension}");
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

                        int remainingPayload = segmentPayloadLength - sniffLength;

                        // JPEG HDR gain-map（Google Ultra HDR / ISO 21496-1）也使用 XMP APP1：
                        // xmlns:hdrgm 和 Container/GainMap 与 MotionPhoto 可能出现在同一个 XMP 段里。
                        // 旧逻辑把整个 XMP 段丢弃，会连 gain map 元数据一起删掉。现在改成：
                        //   1. 只含 HDR：原样保留；
                        //   2. 只含实况照片：整段丢弃；
                        //   3. 同时含 HDR + 实况照片：重写 XMP，只删实况照片字段，保留 hdrgm/GainMap。
                        bool isXmpSegment = sniffLength > 0
                            && segmentBuffer.AsSpan(0, Math.Min(sniffLength, XmpHeaderBytes.Length))
                                .SequenceEqual(XmpHeaderBytes.AsSpan(0, Math.Min(sniffLength, XmpHeaderBytes.Length)));

                        if (isXmpSegment)
                        {
                            byte[] fullPayload = await ReadFullAppPayloadAsync(
                                sourceStream, segmentBuffer, sniffLength, segmentPayloadLength, token);
                            consumedInImage += remainingPayload;
                            remainingPayload = 0;

                            string xmpText = ExtractXmpText(fullPayload);
                            if (TryRewriteXmpRemovingLivePhotoMetadata(
                                    xmpText, out string? rewrittenXmp, out bool changed))
                            {
                                if (changed)
                                {
                                    byte[] rewrittenPayload = BuildXmpPayload(rewrittenXmp!);
                                    await WriteAppSegmentAsync(destinationStream, marker, rewrittenPayload, token);
                                }
                                else
                                {
                                    await WriteAppSegmentAsync(destinationStream, marker, fullPayload, token);
                                }
                            }
                            else
                            {
                                // 解析失败时宁可保留整段，也不能为剥离实况字段而误删 HDR、
                                // 版权、评分等无关 XMP。
                                await WriteAppSegmentAsync(destinationStream, marker, fullPayload, token);
                                LogService.Split(
                                    $"Could not precisely rewrite LivePhoto XMP (len={segmentLength}); preserved segment",
                                    LogLevel.Warning);
                            }
                        }
                        else
                        {
                            // 非 XMP 的 APP 段（EXIF、ICC、MPF 等）原样保留。
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

        private static async Task<byte[]> ReadFullAppPayloadAsync(
            Stream sourceStream, byte[] sniffBuffer, int sniffLength, int totalPayloadLength,
            CancellationToken token)
        {
            byte[] fullPayload = new byte[totalPayloadLength];
            if (sniffLength > 0)
            {
                Buffer.BlockCopy(sniffBuffer, 0, fullPayload, 0, Math.Min(sniffLength, totalPayloadLength));
            }

            int remaining = totalPayloadLength - sniffLength;
            if (remaining > 0)
            {
                byte[] rest = new byte[remaining];
                int read = await ReadExactAsync(sourceStream, rest, remaining, token);
                if (read != remaining)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading APP payload.");
                }

                Buffer.BlockCopy(rest, 0, fullPayload, sniffLength, remaining);
            }

            return fullPayload;
        }

        private static string ExtractXmpText(byte[] payload)
        {
            if (payload.Length < XmpHeaderBytes.Length
                || !payload.AsSpan(0, XmpHeaderBytes.Length).SequenceEqual(XmpHeaderBytes))
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(payload, XmpHeaderBytes.Length, payload.Length - XmpHeaderBytes.Length);
        }

        private static bool TryRewriteXmpRemovingLivePhotoMetadata(
            string xmpText, out string? rewritten, out bool changed)
        {
            rewritten = null;
            changed = false;
            try
            {
                string xml = xmpText.TrimEnd('\0', ' ', '\r', '\n', '\t');
                var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
                XNamespace container = "http://ns.google.com/photos/1.0/container/";
                XNamespace item = "http://ns.google.com/photos/1.0/container/item/";

                // 删除 Directory 中语义为 MotionPhoto 的条目，保留 Primary / GainMap。
                foreach (var li in doc.Descendants(rdf + "li").ToList())
                {
                    var itemElement = li.DescendantsAndSelf()
                        .FirstOrDefault(e => e.Name.Namespace == container && e.Name.LocalName == "Item");
                    string? semantic = itemElement?.Attributes()
                        .FirstOrDefault(a => a.Name.Namespace == item && a.Name.LocalName == "Semantic")?.Value;
                    if (string.Equals(semantic, "MotionPhoto", StringComparison.OrdinalIgnoreCase))
                    {
                        li.Remove();
                        changed = true;
                    }
                }

                // 普通 V2 在删除 MotionPhoto 后只剩 Primary，Directory 已无意义；整块删除。
                // Ultra HDR 仍有 GainMap（或未来未知辅助 Item），必须保留完整 Directory。
                foreach (var directory in doc.Descendants(container + "Directory").ToList())
                {
                    var semantics = directory.Descendants(rdf + "li")
                        .Select(li => li.DescendantsAndSelf()
                            .SelectMany(e => e.Attributes())
                            .FirstOrDefault(a => a.Name.Namespace == item
                                && a.Name.LocalName == "Semantic")?.Value)
                        .ToList();
                    if (semantics.Count == 0 || semantics.All(s =>
                            string.Equals(s, "Primary", StringComparison.OrdinalIgnoreCase)))
                    {
                        directory.Remove();
                        changed = true;
                    }
                }

                string[] livePhotoNamespaces =
                [
                    "http://ns.google.com/photos/1.0/camera/",
                    "http://ns.oplus.com/photos/1.0/camera/",
                    "http://ns.xiaomi.com/photos/1.0/camera/",
                    "http://ns.vivo.com/photos/1.0/camera/",
                    "https://github.com/LengxiQwQ/live-photo-box"
                ];

                foreach (var element in doc.Descendants().ToList())
                {
                    foreach (var attribute in element.Attributes()
                                 .Where(a => livePhotoNamespaces.Contains(a.Name.NamespaceName))
                                 .ToList())
                    {
                        attribute.Remove();
                        changed = true;
                    }

                    if (livePhotoNamespaces.Contains(element.Name.NamespaceName)
                        && element.Parent != null)
                    {
                        element.Remove();
                        changed = true;
                    }
                }

                // XDocument 不会自动删除已经不用的 xmlns 声明。残留的 GCamera / OpCamera /
                // VCamera 等命名空间仍会触发旧扫描器；Container/Item 只在 HDR 目录使用时保留。
                string[] removableNamespaceDeclarations =
                [
                    .. livePhotoNamespaces,
                    container.NamespaceName,
                    item.NamespaceName
                ];
                if (doc.Root != null)
                {
                    foreach (var attribute in doc.Root.DescendantsAndSelf()
                                 .SelectMany(e => e.Attributes())
                                 .Where(a => a.IsNamespaceDeclaration
                                     && removableNamespaceDeclarations.Contains(a.Value))
                                 .ToList())
                    {
                        bool stillUsed = doc.Descendants().Any(e =>
                            e.Name.NamespaceName == attribute.Value
                            || e.Attributes().Any(a => !a.IsNamespaceDeclaration
                                && a.Name.NamespaceName == attribute.Value));
                        if (!stillUsed)
                        {
                            attribute.Remove();
                            changed = true;
                        }
                    }
                }

                rewritten = doc.ToString(SaveOptions.DisableFormatting);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] BuildXmpPayload(string xmpText)
        {
            byte[] xmlBytes = Encoding.UTF8.GetBytes(xmpText);
            byte[] payload = new byte[XmpHeaderBytes.Length + xmlBytes.Length];
            Buffer.BlockCopy(XmpHeaderBytes, 0, payload, 0, XmpHeaderBytes.Length);
            Buffer.BlockCopy(xmlBytes, 0, payload, XmpHeaderBytes.Length, xmlBytes.Length);
            return payload;
        }

        private static async Task WriteAppSegmentAsync(
            Stream destinationStream, byte marker, byte[] payload, CancellationToken token)
        {
            int segmentLength = payload.Length + 2;
            if (segmentLength > ushort.MaxValue)
            {
                throw new InvalidDataException($"JPEG APP segment too large: {segmentLength}");
            }

            byte[] header =
            [
                0xFF,
                marker,
                (byte)(segmentLength >> 8),
                (byte)segmentLength
            ];

            await destinationStream.WriteAsync(header.AsMemory(0, header.Length), token);
            await destinationStream.WriteAsync(payload.AsMemory(0, payload.Length), token);
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

        // ── vivo X300 EXIF UserComment 清理 ──────────────────────────────
        // vivo X300 在 EXIF UserComment 里写 multi-frame 签名（供 vivo 相册识别）。
        // 与 OPPO 不同：vivo 的 UserComment 是一大段 \n 分隔的相机状态文本，不是固定前缀。
        // 只在检测到 "multi-frame" 签名时整段清空，不碰其他内容的 UserComment。
        private static async Task ClearVivoExifMarkerAsync(string imagePath, CancellationToken token)
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

                // Only clear if this is a vivo multi-frame signature
                if (string.IsNullOrEmpty(currentValue)
                    || !currentValue.Contains("multi-frame", StringComparison.OrdinalIgnoreCase))
                    return;

                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-overwrite_original",
                    "-UserComment=",
                    imagePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split(
                    $"Failed to clear vivo EXIF UserComment: {ex.Message}",
                    LogLevel.Warning);
            }
        }

        // ── HEIC meta box XMP 剥离 ───────────────────────────────────────
        // HEIC 源（Google V2 / Samsung / Fusion）在 meta box 里带实况 XMP。
        // 与 JPEG 使用同一套 XML 结构化清洗：只删除实况字段，保留 HDR 与普通 XMP。
        private static async Task StripHeicXmpAsync(string imagePath, CancellationToken token)
        {
            string? xmpTempPath = null;
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                psi.ArgumentList.Add("-XMP");
                psi.ArgumentList.Add("-b");
                psi.ArgumentList.Add(imagePath);

                using var process = Process.Start(psi);
                if (process == null) return;
                try
                {
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(token);
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync(token);
                    await process.WaitForExitAsync(token);
                    string xmpText = await stdoutTask;
                    string stderr = await stderrTask;
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException(stderr.Trim());
                    if (string.IsNullOrWhiteSpace(xmpText)) return;

                    if (!TryRewriteXmpRemovingLivePhotoMetadata(
                            xmpText, out string? rewrittenXmp, out bool changed))
                    {
                        LogService.Split("Could not precisely rewrite HEIC XMP; preserved block",
                            LogLevel.Warning);
                        return;
                    }
                    if (!changed) return;

                    string xmpDir = Path.GetDirectoryName(imagePath) ?? string.Empty;
                    xmpTempPath = TempFileService.AllocateTempPath(xmpDir, "xmp_clean", "xmp");
                    await File.WriteAllTextAsync(
                        xmpTempPath, rewrittenXmp!, new UTF8Encoding(false), token);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    throw;
                }

                await LivePhotoRepairService.RunExifToolAsync(token,
                    "-overwrite_original",
                    $"-XMP<={xmpTempPath}",
                    imagePath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split(
                    $"Failed to strip HEIC XMP: {ex.Message}",
                    LogLevel.Warning);
            }
            finally
            {
                try { if (xmpTempPath != null && File.Exists(xmpTempPath)) File.Delete(xmpTempPath); }
                catch { /* best-effort */ }
            }
        }

        // ── 三星/融合 JPEG Trailer 视频定位 ─────────────────────────────
        // 三星（及融合）JPEG = [JPEG .. EOI] + [MotionPhoto_Data 标签(视频)][MotionPhoto_Version 标签][SEFH..SEFT]。
        // 每个标签：`[00 00][marker LE u16][name_len LE u32][name UTF-8][data]`。
        // 视频即 MotionPhoto_Data 标签的 data 段：从 "MotionPhoto_Data" 名字之后，
        // 到下一个标签（"MotionPhoto_Version"）开头之前。
        // 注：不走 exiftool -b -EmbeddedVideoFile —— 实测 exiftool 对本 App 自产的
        // 2-tag 简化 Trailer 解析报错（"Error processing Samsung trailer"），
        // 直接按协议文档字节格式解析对原厂 7-tag 与自产 2-tag 均可靠。
        public static (long videoStart, long videoLength)? FindSamsungJpegVideoRange(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return null;

                // "MotionPhoto_Data" 名字（16 字节）之后即视频数据
                long dataNamePos = FindBytesForward(fs, 0, "MotionPhoto_Data"u8, fileSize);
                if (dataNamePos < 0) return null;
                long videoStart = dataNamePos + "MotionPhoto_Data".Length;

                // 下一个标签 "MotionPhoto_Version" 的名字（19 字节），其标签头 8 字节在名字之前
                long versionNamePos = FindBytesForward(fs, videoStart, "MotionPhoto_Version"u8, fileSize);
                long videoEnd;
                if (versionNamePos >= 0)
                {
                    videoEnd = versionNamePos - 8;
                }
                else
                {
                    // 兜底：无 MotionPhoto_Version 时以 SEFH 魔数收尾
                    long sefhPos = FindBytesForward(fs, videoStart, "SEFH"u8, fileSize);
                    videoEnd = sefhPos >= 0 ? sefhPos : fileSize;
                }

                if (videoStart <= 0 || videoStart >= videoEnd || videoEnd > fileSize)
                    return null;

                return (videoStart, videoEnd - videoStart);
            }
            catch
            {
                return null;
            }
        }

        // 在 FileStream 中从 startPos 向后搜索任意字节序列，返回其绝对偏移（分块扫描，避免大内存分配）。
        private static long FindBytesForward(FileStream fs, long startPos, ReadOnlySpan<byte> pattern, long endLimit)
        {
            if (pattern.Length == 0) return -1;

            const int chunkSize = 256 * 1024;
            byte[] buf = new byte[chunkSize + pattern.Length];
            long searchPos = startPos;

            while (searchPos < endLimit)
            {
                int toRead = (int)Math.Min(chunkSize, endLimit - searchPos);
                fs.Seek(searchPos, SeekOrigin.Begin);
                int actual = fs.Read(buf, 0, toRead);
                if (actual < pattern.Length) break;

                for (int i = 0; i <= actual - pattern.Length; i++)
                {
                    if (buf.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                        return searchPos + i;
                }
                searchPos += actual - (pattern.Length - 1); // 重叠 pattern-1 字节防跨块
            }

            return -1;
        }

        // ── HEIC mpvd box 定位（谷歌 V2 / 三星共用）──────────────────────
        // 谷歌 V2 HEIC = [HEIC 静态图] + [mpvd box: 8B header + 视频]（无 sefd）。
        // 三星 HEIC   = [HEIC 静态图] + [mpvd box: 8B header + 视频 + sefd box]。
        // 返回 (imageLength, videoStart, videoLength)：图片 = [0..mpvd box 起点)，
        // 视频 = mpvd 内部 sefd box（若存在）之前的视频字节；无 sefd 时取到文件尾。
        private static (long imageLength, long videoStart, long videoLength)? FindHeicMpvdRange(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return null;

                // 从文件头跳过第一个 ftyp 后搜索 "mpvd" 顶层 box
                Span<byte> first4 = stackalloc byte[4];
                fs.Seek(0, SeekOrigin.Begin);
                if (fs.Read(first4) < 4) return null;
                uint firstSize = ReadBigEndianU32(first4);
                long searchFrom = (firstSize >= 8 && firstSize <= fileSize) ? firstSize : 0;

                long mpvdPos = FindFourCCForward(fs, searchFrom, "mpvd"u8, fileSize);
                if (mpvdPos < 8) return null;

                // mpvd box 起点 = mpvdPos - 4（size 字段）
                long mpvdBoxStart = mpvdPos - 4;

                // 视频从 mpvd 头之后开始
                long videoStart = mpvdPos + 4;

                // 在 mpvd 内部搜索 sefd box，视频终点 = sefd box 的 size 字段之前
                long sefdPos = FindFourCCForward(fs, videoStart, "sefd"u8, fileSize);
                long videoEnd = sefdPos >= 4 ? sefdPos - 4 : fileSize;

                if (videoStart <= 0 || videoStart >= videoEnd || videoEnd > fileSize)
                    return null;

                return (mpvdBoxStart, videoStart, videoEnd - videoStart);
            }
            catch
            {
                return null;
            }
        }

        // ── JPEG 主图 EOI 定位（三星/融合 JPEG 图片边界）───────────────
        // 三星/融合 JPEG 在 EOI 之后追加 Samsung Trailer，视频不在文件尾。
        // 该方法沿 JPEG 段结构走到 SOS 后，扫描熵编码数据里的 EOI（0xFFD9），
        // 返回「EOI 之后」的字节偏移（即纯 JPEG 图片的字节数）。
        private static async Task<long> FindJpegEoiEndOffsetAsync(FileStream stream, CancellationToken token)
        {
            stream.Position = 0;

            byte[] temp2 = new byte[2];
            byte[] singleByte = new byte[1];

            if (await ReadExactAsync(stream, temp2, 2, token) != 2 || temp2[0] != 0xFF || temp2[1] != 0xD8)
            {
                throw new InvalidDataException("Split image region is not a valid JPEG (missing SOI).");
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (await ReadExactAsync(stream, temp2, 2, token) != 2)
                {
                    break; // EOF
                }

                while (temp2[0] == 0xFF && temp2[1] == 0xFF)
                {
                    temp2[0] = temp2[1];
                    if (await ReadExactAsync(stream, singleByte, 1, token) != 1) break;
                    temp2[1] = singleByte[0];
                }

                byte marker = temp2[1];

                // SOS：其后是熵编码数据，扫描其中的 EOI
                if (marker == 0xDA)
                {
                    long scanStart = stream.Position;
                    long eoiBytes = await ScanForEoiAsync(stream, token);
                    return eoiBytes < 0 ? -1 : scanStart + eoiBytes;
                }

                // 直接遇到 EOI（空熵编码数据）
                if (marker == 0xD9)
                {
                    return stream.Position;
                }

                // 无长度字段的独立标记
                if (marker == 0xD8 || marker == 0x01 || marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    continue;
                }

                // 其余段：读长度并跳过 payload
                if (await ReadExactAsync(stream, temp2, 2, token) != 2)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading segment length.");
                }
                int segmentLength = (temp2[0] << 8) | temp2[1];
                if (segmentLength < 2)
                {
                    throw new InvalidDataException($"Invalid JPEG segment length: {segmentLength}");
                }
                await SkipExactAsync(stream, segmentLength - 2, token);
            }

            return -1;
        }

        // 从当前流位置扫描熵编码数据，返回「从扫描起点到 EOI 末尾（含 FF D9 两字节）」的字节数。
        // JPEG 熵数据有字节填充（0xFF 后必为 0x00 或 restart 标记），因此 0xFFD9 只会是 EOI。
        private static async Task<long> ScanForEoiAsync(FileStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[81920];
            long consumed = 0;
            int prev = -1;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer, token);
                if (read <= 0) return -1;

                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (prev == 0xFF && b == 0xD9)
                    {
                        return consumed + i + 1;
                    }
                    prev = b;
                }
                consumed += read;
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
