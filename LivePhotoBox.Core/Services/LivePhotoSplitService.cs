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
using System.Xml.Linq;

/*
 * LivePhotoSplitService.cs
 *
 * 瀹炲喌鐓х墖鎷嗗垎鏍稿績銆?
 *
 *   - 灏嗗悎鎴愮殑瀹炲喌鐓х墖鎷嗗洖鐙珛鐨勫浘鐗囦笌瑙嗛
 *   - 鍥剧墖绔寜 JPEG 娈电粨鏋勯€愭閲嶅缓锛氬 XMP 鍋?XML 缁撴瀯鍖栨竻娲楋紝鍙垹闄ゅ疄鍐电収鐗囧瓧娈碉紝
 *     淇濈暀 HDR GainMap銆佺増鏉冦€佽瘎鍒嗙瓑鏅€?XMP锛汦XIF/ICC/鍥惧儚鏁版嵁鍘熸牱淇濈暀
 *   - 涓嶇洿鎺ユ寜瀛楄妭鎴柇鐨勫師鍥狅細鎴柇鍚庡浘鐗囩浠嶄繚鐣?鎴戞槸瀹炲喌鐓х墖"鐨勬爣璁帮紝
 *     鍐嶆鎵弿浼氳璇垽涓哄疄鍐电収鐗囷紝鏋勬垚"鍋囬槼鎬у惊鐜?
 *   - 鍡呮帰鎸夌粨鏋勫尮閰嶏紙APP 娈?+ Adobe XMP 29 瀛楄妭鍥哄畾澶?+ Google 鍛藉悕绌洪棿锛夛紝
 *     涓嶆寜鍏抽敭璇嶏紝閬垮厤璇激 EXIF 娈典笌鍚?Motion/MicroVideo 瀛楁牱鐨勬櫘閫?XMP 娈?
 */

namespace LivePhotoBox.Services
{
    public static class LivePhotoSplitService
    {
        private const int MetadataProbeBytes = 1024 * 1024; // 鎺㈡祴鍓?1MB 鐨勫厓鏁版嵁

        private static readonly byte[] XmpHeaderBytes = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        // 娣诲姞浜?TimeSpan.FromSeconds(2) 浣滀负瓒呮椂淇濇姢锛岄槻姝㈡鍒欒〃杈惧紡閬囧埌鎹熷潖鏂囦欢闄峰叆姝诲惊鐜?
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

        // 鍘傚晢绉佹湁鍋忕Щ閲忔鍒欙紙rdf:Description 灞炴€х骇锛岄潪 Container:Directory 缁撴瀯锛夈€?
        // 浣滀负娣卞害闃插尽锛氬嵆浣?exiftool/淇浘杞欢鍓ョ浜?Container:Directory 娈碉紝
        // 鍙 rdf:Description 鐨勫睘鎬ц繕鍦紝灏辫兘瑙ｆ瀽鍑鸿棰戦暱搴︺€?
        private static readonly Regex OppoVideoLengthRegex = new(
            "OpCamera:VideoLength=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        private static readonly Regex MiCameraVideoLengthRegex = new(
            "MiCamera:VideoLength=\"(?<value>\\d+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        public static async Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory, int protocolIndex, int outputFormatIndex, CancellationToken token, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, long? keyTimestampUs = null)
        {
            Directory.CreateDirectory(outputDirectory);

            await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length <= 0)
            {
                throw new InvalidDataException("Source file is empty.");
            }

            string metadataText = await ReadMetadataTextAsync(sourceStream, token);

            // 鈹€鈹€ 1. 妫€娴嬪鍣細JPEG锛團F D8锛夎繕鏄?HEIC锛坒typ锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
            sourceStream.Position = 0;
            byte[] header = new byte[12];
            int headerRead = await sourceStream.ReadAsync(header, token);
            sourceStream.Position = 0;
            bool sourceImageIsJpeg = headerRead >= 2 && header[0] == 0xFF && header[1] == 0xD8;
            bool sourceImageIsHeic = !sourceImageIsJpeg && headerRead >= 8
                && header[4] == (byte)'f' && header[5] == (byte)'t'
                && header[6] == (byte)'y' && header[7] == (byte)'p';

            // 鈹€鈹€ 2. 妫€娴嬪崗璁紙宸茬煡瀹瑰櫒绫诲瀷锛夛紝澶嶇敤 LivePhotoProtocolDetector 鈹€鈹€
            LivePhotoType livePhotoType = sourceImageIsJpeg
                ? LivePhotoType.SingleFileJpeg
                : LivePhotoType.SingleFileHeic;
            LivePhotoProtocolType protocol = LivePhotoProtocolDetector.Detect(
                sourcePath, livePhotoType, contentIdentifier: null, xmpText: metadataText);

            // 鈹€鈹€ 3. 鎸夊鍣?+ 鍗忚鍒嗘祦锛岃绠椼€屽浘鐗?+ 瑙嗛銆嶇殑鍒嗘 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
            long imageLength;
            long videoStart;
            long videoLength;

            switch (protocol)
            {
                case LivePhotoProtocolType.Huawei:
                {
                    // 鍗庝负/鑽ｈ€€锛歔闈欐€佸浘] + [涓棿宓屽叆 MP4] + [灏鹃儴]锛岀敤 moov/ftyp 瀹氫綅銆?
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
                        // 涓夋槦/铻嶅悎 JPEG锛氬浘鐗?= JPEG 鍒?EOI锛岃棰戝湪 Samsung Trailer 鐨?MotionPhoto_Data 鏍囩閲屻€?
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
                        // 涓夋槦 HEIC锛氳棰戝湪 mpvd box 閲岋紙sefd box 涔嬪墠锛夈€?
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
                        // Google V2 / 鍏跺畠 HEIC锛歔HEIC][mpvd box: 8 瀛楄妭澶?+ 瑙嗛]銆?
                        // XMP 鐨?Item:Length 鍙畻瑙嗛銆佷笉鍚?8 瀛楄妭 mpvd 澶达紝鐩存帴鎸?XMP 鍋忕Щ鍒囩墖浼氭妸
                        // mpvd 澶村苟鍏ュ浘鐗囧鑷村潖鍥?鈫?蹇呴』鎸?mpvd box 瀹氫綅銆?
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
                        // Google V1/V2 / 灏忕背 / OPPO / vivo / 鏈煡 JPEG锛歑MP 鍋忕Щ + 鏂囦欢灏捐拷鍔犺棰戯紙鐜版湁璺緞锛夈€?
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

            // 鈹€鈹€ 鍗忚 鈫?杈撳嚭鏍煎紡 鈫?缂栫爜 濂戠害锛堝叏灞€ outputFormatIndex锛屼笌 protocolIndex 鏃犲叧锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
            //   0 = 榛樿锛氬浘鐗?瑙嗛鍧囧師鏍疯緭鍑猴紙涓嶈浆鍥剧墖銆佷笉杞爜锛岀瓑浠锋棫銆屽浘鐗囬粯璁ゃ€嶏級
            //   1 = JPG + MOV锛圚.265/HEVC锛?
            //   2 = HEIC + MOV锛圚.265/HEVC锛?
            //   3 = JPG + MP4锛圚.264/AVC锛?
            //   protocolIndex锛?=鏃犲崗璁?/ 1=Apple / 2=vivo锛夈€?
            //   vivo 鍙屾枃浠堕厤瀵规爣璁板湪杈撳嚭钀界洏鍚庣敱 VivoDualFileMetadataWriter 鍐欏叆銆?
            // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
            string targetImageExtension = outputFormatIndex switch
            {
                1 or 3 => ".JPG",
                2 => ".HEIC",
                _ => Path.GetExtension(sourcePath) // 0 = 榛樿锛氬浘鐗囪窡闅忔簮鎵╁睍鍚?
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
                // 1. 鎻愬彇鍥剧墖閮ㄥ垎鍒颁复鏃舵枃浠?
                sourceStream.Position = 0;
                await using (var imageOutputStream = new FileStream(tempImagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (sourceImageIsJpeg)
                        await CopyJpegStrippingLivePhotoMetadataAsync(sourceStream, imageOutputStream, imageLength, token);
                    else
                        await CopyExactLengthAsync(sourceStream, imageOutputStream, imageLength, token);
                }

                // HEIC 婧愶細meta box 閲岀殑 XMP锛堣胺姝?V2 / 涓夋槦 / 铻嶅悎锛変粛鏄€屾垜鏄疄鍐电収鐗囥€嶇鍚嶏紝鐢?exiftool 鏁寸粍鍓ョ銆?
                // 鍗庝负 HEIC 鏃?XMP锛屾姝ヤ负绌烘搷浣滐紙best-effort锛夈€?
                // 娉ㄦ剰锛欰pple 鐩爣璧板瓧鑺傜骇鏃犳崯娉ㄥ叆锛宔xiftool 鏁存枃浠堕噸鍐欎細鐮村潖銆屽師鏍蜂繚鐣欍€嶅苟澶氬绌?XMP锛?
                // 鏁?Apple 鐩爣璺宠繃姝ゆ锛堥厤瀵归潬 MakerNote锛屼笉渚濊禆姝ゅ XMP 鍓ョ锛夈€?
                if (sourceImageIsHeic && protocolIndex != 1)
                {
                    await StripHeicXmpAsync(tempImagePath, token);
                }

                // OPPO 鍗忚鍦?EXIF UserComment 閲屽啓浜?"oplus_10485792" 鏍囪锛堜緵 OPPO 鐩稿唽璇嗗埆锛夈€?
                // XMP 娈靛凡鍦ㄤ笂闈㈣鍓ョ锛屼絾 EXIF 娈靛師鏍蜂繚鐣欎簡 鈫?闇€鍗曠嫭娓呯悊銆?
                // 鍙竻浠?"oplus_" 寮€澶寸殑鍊硷紝涓嶇鍏朵粬鍐呭鐨?UserComment銆侶EIC 婧愭棤姝?EXIF 娈碉紝璺宠繃銆?
                if (sourceImageIsJpeg
                    && protocol is LivePhotoProtocolType.OPPO or LivePhotoProtocolType.Fusion)
                {
                    await ClearOppoExifMarkerAsync(tempImagePath, token);
                }

                // vivo X300 鍦?EXIF UserComment 閲屽啓浜?multi-frame 绛惧悕锛堜緵 vivo 鐩稿唽璇嗗埆锛夛紝鍚屾牱闇€娓呯悊銆?
                if (sourceImageIsJpeg && protocol == LivePhotoProtocolType.Vivo)
                {
                    await ClearVivoExifMarkerAsync(tempImagePath, token);
                }

                // Apple 娈嬬暀锛堝弻鏂囦欢鏍囪锛夛細鎷嗗嚭鐨勫浘鐗囪鍗曠嫭浣跨敤锛屼笉鑳藉甫 Apple ContentIdentifier銆?
                // 澶嶇敤 SourceProtocolCleaner 鐨勬竻娲楋紙exiftool -ContentIdentifier= + vivo 灏炬爣锛夛紱
                // Apple 鐩爣杈撳嚭浼氶噸寤洪厤瀵规爣璁帮紝涓嶅湪姝ゅ垪锛堜粎鏃犲崗璁ぇ娓呮礂鏃舵墽琛岋級銆?
                if ((protocolIndex == 0 || protocolIndex == 2) && sourceImageIsJpeg)
                {
                    Protocols.SourceProtocolCleaner.CleanImageMarkersInPlace(tempImagePath, token);
                }

                // Apple 瀹炲喌鏉＄洰瀛楄妭绾у墺绂伙紙0x0011 ContentIdentifier / 0x0017 LivePhotoVideoIndex /
                // 0x0025锛岃鍗忚鏂囨。 Apple 绔犺妭锛夛細exiftool 鍙兘娓呯┖ CID 鍊笺€佸垹涓嶆帀 type=16 鏉＄洰
                // 锛?LivePhotoVideoIndex= 绛夊洓绉嶅啓娉曞疄娴嬫棤鏁堬級锛屼笖 HEIC 婧愭鍓嶈 sourceImageIsJpeg
                // 闂ㄦ帶鏁翠綋璺宠繃銆傛姝ラ瀵?JPEG 涓?HEIC 婧愮粺涓€鎵ц锛屼繚鎸?MN 闀垮害涓嶅彉涓嶇牬鍧忕粨鏋勩€?
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

                // Apple 鍗忚锛氬浘鐗囩 Apple MakerNote 蹇呴』鍦ㄦ牸寮忚浆鎹㈠墠娉ㄥ叆鍒版簮 JPG銆?
                // heif-enc锛坙ibheif锛? heif-dec锛圗xifTool 鍏冩暟鎹鍒讹級浼氬師鏍蜂繚鐣?MakerNote銆?
                // 鏈蒋浠跺悎鎴?鑻规灉琛嶇敓婧愮殑鍗曟枃浠朵繚鐣欎簡婧愯嫻鏋滅殑 MakerNote锛圕ID 琚竻绌恒€佹潯鐩粛鍦級锛?
                // 灏卞湴閲嶅缓涓?70 瀛楄妭鏈€灏?MN锛堣捣鐐?闀垮害涓嶅彉锛夛紱鍘熺敓鐩告満 JPEG锛堝皬绫?OPPO 绛夋棤
                // 0x927C 鏉＄洰锛夎蛋 APP1 娉ㄥ叆锛堟柊澧炴潯鐩級锛涘師鐢?HEIC锛堝崕涓虹瓑锛夎В鐮佷负 JPEG 妗ユ帴
                // 娉ㄥ叆锛岀洰鏍囦负 HEIC 鏃跺啀缂栫爜鍥?HEIC锛坔eif-enc 淇濈暀 EXIF MakerNote锛夈€?
                string? appleContentId = null;
                string? appleBridgeJpeg = null; // HEIC 婧愮粡 JPEG 妗ユ帴娉ㄥ叆 MN 鐨勪腑闂存枃浠?
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
                            // 鏃犳崯浼樺厛锛氱洿鎺ュ湪 HEIC 瀹瑰櫒鐨?Exif item 閲屽師浣嶅啓鍏?Apple MakerNote锛?
                            // 涓嶉噸缂栫爜鍍忕礌锛屼繚鐣?10-bit 瀛愬浘 / 澧炵泭鍥?/ 杈呭姪鍥?/ 鍘傚晢绉佹湁鏁版嵁銆?
                            // 缁撴瀯涓嶈璇嗘垨瀹归噺涓嶈冻鏃跺洖閫€ JPEG 妗ユ帴锛堣€佸璺級銆?
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

                // 鎸夌洰鏍囧浘鐗囨牸寮忚浆鎹紙澶嶇敤 HeicConverterService锛屼笉鑷鍙﹀啓杞崲閫昏緫锛?
                bool targetImageIsHeic = targetImageExtension.Equals(".heic", StringComparison.OrdinalIgnoreCase);
                string workingImagePath = tempImagePath;
                if (appleBridgeJpeg != null)
                {
                    // HEIC 婧愭ˉ鎺ユ敞鍏ュ悗锛氱洰鏍?HEIC 鈫?缂栫爜鍥烇紱鐩爣 JPG 鈫?鐩存帴鐢ㄦˉ鎺?JPEG銆?
                    if (targetImageIsHeic)
                    {
                        if (sourceImageIsHeic)
                        {
                            // HDR 淇濈暀锛欻EIC 婧愬儚绱犺蛋 16-bit PNG锛坔eif-dec -> heif-enc -b 10 + nclx/CLLI锛夛紝
                            // EXIF锛堝惈 Apple MakerNote锛夋暣浣撳彇鑷?JPEG 妗ユ帴锛堝厛娓呭悗鎷凤紝淇濊瘉鍞竴 0x927C锛夈€?
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

                // 鏃犳崯 HEIC鈫扝EIC锛歸orkingImagePath 浠嶆槸 tempImagePath锛堟湭閲嶇紪鐮侊級銆?
                // 鍚庣画涓嶈鍐嶅鍥剧墖璺?exiftool锛堜細閲嶅啓瀹瑰櫒銆佸姞 XMP锛岀牬鍧忋€屽師鏍蜂繚鐣欍€嶏級銆?
                bool imageIsLosslessHeic = sourceImageIsHeic
                    && targetImageIsHeic
                    && string.Equals(workingImagePath, tempImagePath, StringComparison.Ordinal);

                // 鍥剧墖钀戒綅鍒版渶缁堣緭鍑鸿矾寰勶紙BuildOutputPaths 宸查鐣?0 瀛楄妭鍗犱綅鏂囦欢锛岄渶鍏堝垹闄わ級
                if (File.Exists(imageOutputPath))
                    File.Delete(imageOutputPath);
                File.Move(workingImagePath, imageOutputPath);

                // 2. 鎻愬彇瑙嗛閮ㄥ垎鍒颁复鏃舵枃浠?
                sourceStream.Position = videoStart;
                await using (var videoOutputStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyExactLengthAsync(sourceStream, videoOutputStream, videoLength, token);
                }

                // 3. 瑙嗛澶勭悊锛氶粯璁?0)鍘熸牱杈撳嚭锛?/2 鈫?MOV+H.265锛? 鈫?MP4+H.264
                // 鏃犲崗璁媶鍒嗭細鍏堝墺绂昏棰戦噷娈嬬暀鐨勫疄鍐靛崗璁厓鏁版嵁锛堝崟鏂囦欢 + 鍙屾枃浠舵畫鐣欙級锛屽啀绉诲姩/杞爜銆?
                // 鎷嗗嚭鐨勮棰戣鍗曠嫭浣跨敤锛屼笉鑳藉甫浠讳綍鍘傚晢瀹炲喌鏍囪銆?
                if (protocolIndex == 0 || protocolIndex == 2)
                {
                    // 鍗曟枃浠跺崗璁敭锛欻UAWEI com.openharmony.*
                    if (!Protocols.Mp4MdtaKeyStripper.TryStripHuaweiKeys(tempVideoPath, out string? stripError))
                    {
                        LogService.Split($"Video vendor metadata strip failed (non-fatal): {stripError}", LogLevel.Warning);
                    }
                    // Track 3锛坈om.openharmony.timed_metadata.movingphoto锛夛紝灞炲崟鏂囦欢鍗忚鍏冩暟鎹建
                    Protocols.Mp4MdtaKeyStripper.TryStripTracks(
                        tempVideoPath, ["com.openharmony.timed_metadata.movingphoto"], out _);
                    // 鍙屾枃浠舵畫鐣欙細Apple mdta keys锛坈ontent.identifier/live-photo/vitality锛夈€?
                    // mebx 瀹炲喌鏃跺簭杞ㄣ€乿ivoMediaExtInfo uuid box锛堝鐢ㄥ悎鎴愮娓呮礂鍣級
                    Protocols.SourceProtocolCleaner.CleanVideoMarkersInPlace(tempVideoPath);
                }

                if (outputFormatIndex == 0)
                {
                    // 涓嶉渶瑕佽浆鐮侊紝鐩存帴绉诲姩涓存椂鏂囦欢鍒扮洰鏍囦綅缃?
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
                        // Apple 鍗忚锛坧rotocolIndex==1锛夛細HEVC 杞爜鎴愬叏 I 甯э紙-g 1锛夈€?
                        // iOS 瀹炲喌鐓х墖缂栬緫鍣ㄧ殑鎷栧姩棰勮鎸夊悓姝ユ牱鏈彇甯э紱鍙湁鍏抽敭甯у彲閫夛紝
                        // 甯歌 GOP锛?g 15锛変細瀵艰嚧 3s 瑙嗛鍙湁 7 甯у彲閫夈€佹嫋鍔ㄥ崱椤裤€?
                        // 鍏?I 甯у悗 stss 瑕嗙洊姣忎竴甯э紝缂栬緫鍣ㄥ彲閫愬抚鎷栧姩骞朵换閫夊皝闈€?
                        1 or 2 => await VideoTranscodeService.TranscodeToMovAsync(
                            tempVideoPath, videoOutputPath, token, videoCodec: "hevc",
                            keyframeInterval: protocolIndex == 1 ? 1 : null),
                        3 => await VideoTranscodeService.TranscodeToMp4Async(tempVideoPath, videoOutputPath, token, videoCodec: "h264"),
                        _ => throw new InvalidOperationException($"Unsupported output format index: {outputFormatIndex}")
                    };
                    if (!transcodeResult.Success)
                        throw new InvalidOperationException($"Video transcode failed: {transcodeResult.ErrorMessage}");
                }

                // 4. 灏嗘簮鏂囦欢鐨勫叧閿厓鏁版嵁鍐欏洖瑙嗛杈撳嚭锛堜緵鍚庣画鍏冩暟鎹尮閰嶄娇鐢級
                // 璇绘竻娲楀悗鐨勫浘鐗囷紙tempImagePath锛夎€岄潪鍘熷鍗曟枃浠讹細淇濊瘉瑙嗛鍏冩暟鎹笌
                // 杈撳嚭鍥剧墖涓€鑷达紝涓嶄細鎶婂凡琚竻娲楁帀鐨勫巶鍟嗘爣璁帮紙Make=HUAWEI 绛夛級鍐嶅甫鍥炲幓銆?
                await CopyMetadataToVideoAsync(tempImagePath, videoOutputPath, token);

                // 5. 缁欏浘鐗囧拰瑙嗛鎵撲笂 LivePhotoBox 鏍囪锛堟爣璇嗙粡鏈蒋浠舵媶鍒嗚繃锛?
                // 鏃犳崯 HEIC 鍥剧墖璺宠繃锛歟xiftool 浼氶噸鍐欏鍣ㄥ苟鍔?XMP锛岀牬鍧忓師鏍蜂繚鐣欍€?
                if (!imageIsLosslessHeic)
                {
                    await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                        imageOutputPath, "Split", "", token);
                }
                await LivePhotoRepairService.TryWriteLivePhotoBoxMarkerAsync(
                    videoOutputPath, "Split", "", token);

                // 鈹€鈹€ 鎸?protocolIndex 鍐欏叆鍙屾枃浠堕厤瀵瑰厓鏁版嵁 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
                // protocolIndex == 1锛圓pple锛夛細缁欏浘鐗囦笌瑙嗛涓ょ鍐欏叆閰嶅鍏冩暟鎹紝
                //   浣?Apple Photos 灏嗕袱鑰呰瘑鍒负涓€瀵瑰疄鍐电収鐗囥€?
                // protocolIndex == 2锛坴ivo锛夛細鍦?JPG 灏鹃儴杩藉姞 vivo JSON 灏炬爣锛?
                //   骞跺湪 MP4 鍐欏叆 vivoMediaExtInfo uuid box锛屼袱绔娇鐢ㄥ悓涓€閰嶅 ID銆?
                if (protocolIndex == 1)
                {
                    await Protocols.AppleLivePhotoMetadata.WritePairMetadataAsync(
                        sourcePath, metadataText, imageOutputPath, videoOutputPath, appleContentId, keyTimestampUs, token);
                }
                else if (protocolIndex == 2)
                {
                    await Protocols.VivoDualFileMetadataWriter.WritePairMetadataAsync(
                        sourcePath, metadataText, imageOutputPath, videoOutputPath,
                        keyTimestampUs, token);
                }
                // 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

                return new LivePhotoSplitResult
                {
                    ImageOutputPath = imageOutputPath,
                    VideoOutputPath = videoOutputPath
                };
            }
            catch
            {
                // 澶辫触/鍙栨秷鏃舵竻鐞嗗彲鑳藉凡缁忓啓鍏ョ殑涓嶅畬鏁磋緭鍑烘枃浠讹紙鍚?BuildOutputPaths 棰勭暀鐨勫崰浣嶆枃浠讹級
                try { if (File.Exists(videoOutputPath)) File.Delete(videoOutputPath); } catch { }
                try { if (File.Exists(imageOutputPath)) File.Delete(imageOutputPath); } catch { }
                throw;
            }
            finally
            {
                // 鏃犺鎴愬姛/澶辫触/鍙栨秷锛屼复鏃舵枃浠堕兘瑕佹竻鐞?
                try { if (File.Exists(tempVideoPath)) File.Delete(tempVideoPath); } catch { }
                try { if (File.Exists(tempImagePath)) File.Delete(tempImagePath); } catch { }
                if (convertedImagePath != null)
                    try { if (File.Exists(convertedImagePath)) File.Delete(convertedImagePath); } catch { }
                // 娉ㄦ剰锛氫笉鍒犻櫎 Temp 鐩綍鏈韩锛岀敱 ViewModel 鍦ㄥ叏閮ㄤ换鍔″畬鎴愬悗缁熶竴娓呯悊銆?
                // 骞跺彂鎷嗗垎鏃跺涓换鍔″叡浜悓涓€涓?Temp 鐩綍锛屽崟涓换鍔″垹闄や細瀵艰嚧鍏朵粬杩涜涓换鍔?
                // 璺緞澶辨晥锛?Could not find a part of the path"銆?
            }
        }

        // 浠庢簮鏂囦欢娴佷腑璇诲彇鍓?<see cref="MetadataProbeBytes"/> 瀛楄妭鐨勬枃鏈唴瀹癸紝
        // 鐢ㄤ簬鎻愬彇瀹炲喌鐓х墖鐨?XMP 鍏冩暟鎹紙MicroVideoOffset 绛夛級銆?
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
        /// 鍏紑鐨勯噸杞斤細浠庢枃浠惰矾寰勮鍙?XMP 鍏冩暟鎹枃鏈紙鍓?1MB锛夛紝
        /// 渚?LightboxItemSource 绛夊閮ㄨ皟鐢ㄦ柟浣跨敤銆?
        /// </summary>
        public static async Task<string> ReadMetadataFromFileAsync(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await ReadMetadataTextAsync(fs, CancellationToken.None);
        }

        /// <summary>
        /// 鍚屾鐗堟湰锛氫緵鎵弿闃舵鍦ㄥ悓姝ュ惊鐜腑鐩存帴璋冪敤锛岄伩鍏?async 寮€閿€銆?
        /// </summary>
        public static string ReadMetadataTextSync(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bufferLength = (int)Math.Min(fs.Length, MetadataProbeBytes);
            byte[] buffer = new byte[bufferLength];
            int bytesRead = fs.Read(buffer, 0, bufferLength);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        // 浠?XMP 鍏冩暟鎹枃鏈腑鎻愬彇瑙嗛灏鹃儴闀垮害銆?
        // 娣卞害闃插尽锛氫緷娆″皾璇曞叏閮ㄥ凡鐭ュ巶鍟嗙殑鍋忕Щ閲忔牸寮忋€?
        //   MicroVideo V1 鈫?MotionPhoto V2 鈫?OPPO O-Live Photo 鈫?灏忕背
        // 鍙浠讳竴鏍煎紡鍖归厤鎴愬姛鍗宠繑鍥烇紝澶氶亾 fallback 纭繚 XMP 琚?
        // 淇浘杞欢/exiftool 閮ㄥ垎淇敼鍚庝粛鑳借В鏋愩€?
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

            // 鍏ㄩ儴澶辫触 鈫?鏋勯€犲惈璇婃柇淇℃伅鐨勫紓甯告秷鎭紝鐢ㄦ埛鍙洿鎺ュ湪閿欒寮圭獥鐪嬪埌
            bool m1 = MicroVideoOffsetRegex.Match(metadataText).Success;
            bool m2 = MotionPhotoLengthRegex.Match(metadataText).Success;
            bool m3 = OppoVideoLengthRegex.Match(metadataText).Success;
            bool m4 = MiCameraVideoLengthRegex.Match(metadataText).Success;

            // 妫€鏌?XMP header 鏄惁瀛樺湪
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
        /// 瑙ｆ瀽 OPPO 绉佹湁瀛楁 OpCamera:VideoLength 鈥斺€?绾棰戝瓧鑺傞暱搴︼紙涓嶅惈 OnePlus trailer锛夈€?
        /// OPPO 鍘熷巶鏂囦欢鏄?[JPEG][MP4][OnePlus trailer ~846KB]锛孋ontainer:Directory 鐨?
        /// Item:Length 瑕嗙洊"瑙嗛+trailer"锛岃€?OpCamera:VideoLength 鍙寚绾棰戙€?
        /// 閲嶈灏侀潰/瀵煎嚭鏃堕渶瑕佺函瑙嗛闀垮害銆傛棤璇ュ瓧娈佃繑鍥?0銆?
        /// </summary>
        public static long GetOppoPureVideoLength(string metadataText)
        {
            var m = OppoVideoLengthRegex.Match(metadataText);
            return m.Success && long.TryParse(m.Groups["value"].Value, out long v) ? v : 0;
        }

        // 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
        //  鍗庝负/鑽ｈ€€ 宓屽叆瑙嗛瀹氫綅
        // 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲

        /// <summary>
        /// 浠庡崕涓?鑽ｈ€€瀹炲喌鐓х墖浜岃繘鍒舵牸寮忎腑瀹氫綅宓屽叆鐨?MP4 瑙嗛娈点€?
        /// 鍗庝负/鑽ｈ€€鍗忚 = [闈欐€佸浘] + [宓屽叆MP4(ftyp..mdat..moov)] + [鍙彉闀垮熬(鑽ｈ€€鏈?uuidextend_type_matrix + 60B灏?]銆?
        /// 浣跨敤 moov box 缁撴瀯瀹氫綅 MP4 缁堢偣锛堣€岄潪纭紪鐮佸噺鍘诲浐瀹氬熬闀匡級锛屽鍗庝负鍜岃崳鑰€鍧囨纭€?
        /// </summary>
        /// <returns>(videoStart, videoEnd, videoLength) 鎴?null锛堝畾浣嶅け璐ワ級</returns>
        public static (long videoStart, long videoEnd, long videoLength)? GetHuaweiEmbeddedVideoRange(
            string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return null;

                // 鈹€鈹€ Step 1: 浠庢枃浠舵湯 256KB 鎵惧埌鏈€鍚庝竴涓?moov box 鈹€鈹€
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
                    // 鏍囧噯鍗庝负甯冨眬锛歮oov 鍦ㄥ祵鍏?MP4 鏈熬锛堟帴杩戞枃浠跺熬閮級
                    moovPos = fileSize - readSize + moovRelIdx;
                    moovSize = ReadBigEndianU32(tailBuf, moovRelIdx - 4);
                }
                else
                {
                    // 鈹€鈹€ 鍥為€€锛歮oov 涓嶅湪鏂囦欢灏鹃儴锛堝祵鍏?MP4 閲囩敤 moov-before-mdat 甯冨眬锛夆攢鈹€
                    // 渚嬪锛欰pple MOV锛坢oov 鍦ㄥ紑澶达級琚洿鎺ヤ綔涓?MP4 宓屽叆鏃讹紝
                    // moov 璺濈鏂囦欢灏鹃儴鍙兘瓒呰繃 256KB锛屼笂杩版悳绱細澶辫触銆?
                    // 姝ゆ椂浠庢枃浠跺ご璺宠繃 HEIC ftyp 鍚庢悳绱㈢浜屼釜 ftyp锛堝祵鍏?MP4 鐨?ftyp锛夛紝
                    // 鍐嶅悜璇ヤ綅缃箣鍚庢悳绱?moov box銆?
                    long secondFtypPos = FindSecondFtyp(fs, fileSize);
                    if (secondFtypPos < 4) return null;

                    moovPos = FindFourCCForward(fs, secondFtypPos, "moov"u8, fileSize);
                    if (moovPos < 0) return null;

                    // 璇诲彇 moov box size
                    fs.Seek(moovPos - 4, SeekOrigin.Begin);
                    Span<byte> size4 = stackalloc byte[4];
                    fs.ReadExactly(size4);
                    moovSize = ReadBigEndianU32(size4);
                }

                if (moovSize < 8 || moovSize > fileSize) return null;

                // moovEnd: box 璧峰 = moovPos - 4锛坰ize 瀛楁锛夛紝缁堟 = 璧峰 + moovSize
                long moovEnd = moovPos - 4 + moovSize;
                if (moovEnd > fileSize) return null;

                // 鈹€鈹€ Step 2: 鍦?moov 涔嬪墠鎵炬渶鍚庝竴涓?ftyp box锛堝嵆宓屽叆 MP4 璧风偣锛夆攢鈹€
                long ftypPos = FindLastFtypBefore(fs, moovPos);
                if (ftypPos < 4) return null;

                long videoStart = ftypPos - 4; // ftyp box 鐨?size 瀛楁

                // 鈹€鈹€ Step 3: 纭畾瑙嗛缁堢偣 鈹€鈹€
                // 鑻?moov 鍦ㄦ枃浠跺熬閮紙鏍囧噯甯冨眬 ftyp鈫抦dat鈫抦oov锛屾垨鑽ｈ€€鐨?ftyp鈫抦dat鈫抦oov鈫抂uuidextend uuid box]锛夛紝
                // moovEnd 鍗?MP4 缁堢偣锛屽叾鍚庣殑鑽ｈ€€ uuid box / LIVE_ 灏炬爣閮戒笉灞炰簬瑙嗛銆?
                // 鑻?moov 涓嶅湪灏鹃儴锛堝 ftyp鈫抦oov鈫抦dat 甯冨眬锛夛紝MP4 缁堢偣涓?LIVE_ 灏炬爣涔嬪墠銆?
                long videoEnd;
                if (moovRelIdx >= 4)
                {
                    // moov 鍦ㄦ枃浠跺熬閮?256KB 鍐?鈫?瀹冩槸 MP4 鐨勬渶鍚庝竴涓?box锛宮oovEnd 鍗宠棰戠粓鐐?
                    videoEnd = moovEnd;
                }
                else
                {
                    // moov 鍦?mdat 涔嬪墠 鈫?MP4 寤朵几鍒版枃浠舵湯鐨?60 瀛楄妭 LIVE_ 灏炬爣涔嬪墠
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

        /// <summary>鍦ㄥ瓧鑺傛暟缁勪腑浠庡悗寰€鍓嶆悳绱㈠瓙搴忓垪锛岃繑鍥炴渶鍚庝竴涓尮閰嶇殑鍋忕Щ</summary>
        private static int LastIndexOf(byte[] data, ReadOnlySpan<byte> pattern)
        {
            for (int i = data.Length - pattern.Length; i >= 0; i--)
            {
                if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                    return i;
            }
            return -1;
        }

        /// <summary>鍦?FileStream 涓粠鍚庡線鍓嶆悳绱㈡渶鍚庝竴涓?ftyp box锛堝湪 limit 涔嬪墠锛夛紝杩斿洖鍏剁粷瀵逛綅缃?/summary>
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

                // 浠庡悗寰€鍓嶆壘
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

        /// <summary>浠庡瓧鑺傛暟缁勪腑璇诲彇 big-endian uint32</summary>
        private static uint ReadBigEndianU32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24)
                 | ((uint)data[offset + 1] << 16)
                 | ((uint)data[offset + 2] << 8)
                 | data[offset + 3];
        }

        /// <summary>浠?Span 璇诲彇 big-endian uint32</summary>
        private static uint ReadBigEndianU32(ReadOnlySpan<byte> data)
        {
            return ((uint)data[0] << 24)
                 | ((uint)data[1] << 16)
                 | ((uint)data[2] << 8)
                 | data[3];
        }

        /// <summary>
        /// 瀹氫綅宓屽叆 MP4 鐨?ftyp box锛岃繑鍥?'f' 瀛楃鐨勭粷瀵瑰亸绉汇€?
        /// HEIC 鏂囦欢锛氳烦杩囨枃浠跺ご閮ㄧ殑绗竴涓?ftyp锛岃繑鍥炵浜屼釜锛堝嵆宓屽叆 MP4 鐨勶級ftyp銆?
        /// JPEG 鏂囦欢锛氭枃浠跺ご涓嶆槸 ISOBMFF box锛岀洿鎺ユ悳绱㈢涓€涓?ftyp銆?
        /// 杩斿洖 -1 琛ㄧず鏈壘鍒般€?
        /// </summary>
        private static long FindSecondFtyp(FileStream fs, long fileSize)
        {
            // 璇诲彇鏂囦欢澶撮儴 4 瀛楄妭锛屽垽鏂槸鍚︿负 ISOBMFF box size
            Span<byte> header = stackalloc byte[4];
            fs.Seek(0, SeekOrigin.Begin);
            int read = fs.Read(header);
            if (read < 4) return -1;

            uint firstFour = ReadBigEndianU32(header);
            bool isIsobmff = (firstFour >= 8 && firstFour <= fileSize);

            long searchFrom;
            if (isIsobmff)
            {
                // HEIC / MP4锛氱涓€涓?ftyp 鍦?offset 0锛岃烦杩囧畠鎵剧浜屼釜
                searchFrom = firstFour;
            }
            else
            {
                // JPEG / 鍏朵粬锛氭枃浠跺ご涓嶆槸 ISOBMFF box锛堝 JPEG SOI 0xFFD8锛夛紝
                // 浠庢枃浠跺紑澶存悳绱㈢涓€涓紙涔熸槸鍞竴涓€涓級ftyp
                searchFrom = 0;
            }

            return FindFourCCForward(fs, searchFrom, "ftyp"u8, fileSize);
        }

        /// <summary>
        /// 鍦?FileStream 涓粠 startPos 鍚戝悗鎼滅储鎸囧畾鐨?fourcc 鏍囪锛岃繑鍥炲叾缁濆鍋忕Щ銆?
        /// 浣跨敤鍒嗗潡鎵弿閬垮厤澶у唴瀛樺垎閰嶃€?
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
                // 閲嶅彔 3 瀛楄妭闃叉 fourcc 璺ㄥ潡
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

        // 閫氳繃瑙嗛娴佸ご閮ㄩ瓟鏁帮紙ftyp box锛夋娴嬮粯璁よ棰戞牸寮忋€?
        // 浼樺厛绾э細浜岃繘鍒堕瓟鏁?> XMP MIME 绫诲瀷 > 鍏滃簳 .mp4銆?
        private static async Task<string> DetectDefaultVideoExtensionAsync(FileStream sourceStream, long videoStartOffset, string metadataText, CancellationToken token)
        {
            // 1. 瑙嗛娴佸ご閮ㄩ瓟鏁板垽鏂紙鏉冨▉鏈€楂樹紭鍏堢骇锛?
            byte[] header = new byte[32];
            sourceStream.Position = videoStartOffset;
            int bytesRead = await sourceStream.ReadAsync(header, token);
            sourceStream.Position = 0; // 澶嶄綅娴佹寚閽?

            if (bytesRead >= 12)
            {
                string boxType = Encoding.ASCII.GetString(header, 4, 4);

                if (boxType == "ftyp")
                {
                    string majorBrand = Encoding.ASCII.GetString(header, 8, 4);

                    // 鍖归厤 Apple QuickTime
                    if (majorBrand.StartsWith("qt", StringComparison.OrdinalIgnoreCase))
                        return ".MOV";

                    // 鍖归厤 MP4 鍙婂叾鍙樼 (鍚?hvc1 绛?HEVC 鍙樼)
                    if (majorBrand.StartsWith("isom", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("mp4", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("avc1", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase) ||
                        majorBrand.StartsWith("hev1", StringComparison.OrdinalIgnoreCase))
                        return ".MP4";
                }
                else if (boxType == "moov")
                {
                    // 鍏煎鏋佸皯鏁版棤 ftyp 鐩存帴 moov 寮€澶寸殑鑰佺増鏈牸寮?
                    return ".MOV";
                }
            }

            // 2. 澶囩敤鏂规锛氬鏋滀簩杩涘埗娴佸洜鏁呮湭鑳借瘑鍒紝閫€鍥炴煡闃?XMP 鏂囨湰
            string? mimeType = MotionPhotoMimeRegex.Match(metadataText).Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(mimeType))
            {
                var mime = mimeType.Trim().ToLowerInvariant();
                if (mime == "video/quicktime") return ".MOV";
                if (mime == "video/mp4") return ".MP4";
            }

            // 3. 鍏滃簳鏂规
            LogService.Split("Failed to detect video format via Magic Number and XMP, fallback to .MP4", LogLevel.Warning);
            return ".MP4";
        }

        // 鏋勫缓鎷嗗垎鍚庡浘鐗囧拰瑙嗛鐨勮緭鍑鸿矾寰勩€?
        // 鑷姩澶勭悊鍚屽悕鍐茬獊锛堣拷鍔犲悗缂€锛夛紝骞堕槻姝㈣緭鍑鸿矾寰勮鐩栨簮鏂囦欢銆?
        // sourcePath: 婧愭枃浠惰矾寰勩€?
        // outputDirectory: 杈撳嚭鐩綍銆?
        // videoExtension: 瑙嗛鎵╁睍鍚嶏紙.mp4 / .mov锛夈€?
        // 杩斿洖: (鍥剧墖杈撳嚭璺緞, 瑙嗛杈撳嚭璺緞)
        private static (string ImageOutputPath, string VideoOutputPath) BuildOutputPaths(string sourcePath, string outputDirectory, string imageExtension, string videoExtension, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false)
        {
            string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
            // 鍛藉悕妯℃澘娓叉煋鍚庣殑鍩烘湰鍚嶏紙GUI 绔凡绠楀ソ骞舵秷姣掞級锛涚己鐪佹椂鍥為€€涓烘簮鏂囦欢鍚嶃€?
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
                // 瑕嗙洊妯″紡锛氫娇鐢ㄧ‘瀹氭€ф枃浠跺悕锛堜笌婧愬悓鍚?baseName锛夛紝鍚庣画鍐欏叆鍓嶅垹闄ゆ棫鏂囦欢銆?
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

            // 闃叉杈撳嚭鏂囦欢瑕嗙洊鎺夋鍦ㄨ鍙栫殑婧愭枃浠?
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

        // 浠庢簮娴佸鍒舵寚瀹氬瓧鑺傛暟鍒扮洰鏍囨祦銆?
        // 浣跨敤 81920 瀛楄妭缂撳啿鍖猴紙浣庝簬 LOH 闃堝€硷紝鏈€浼?IO 澶у皬锛夈€?
        // 鑻ユ彁鍓嶉亣鍒版祦缁撳熬鍒欐姏鍑?EndOfStreamException銆?
        // sourceStream: 婧愭祦銆?
        // destinationStream: 鐩爣娴併€?
        // length: 瑕佸鍒剁殑瀛楄妭鏁般€?
        // token: 鍙栨秷浠ょ墝銆?
        private static async Task CopyExactLengthAsync(Stream sourceStream, Stream destinationStream, long length, CancellationToken token)
        {
            // 81920 (80KB) 鍒氬ソ浣庝簬 LOH (Large Object Heap) 鐨勯槇鍊硷紝鏄渶浼樼殑 IO 缂撳啿澶у皬
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

        // 澶嶅埗 JPEG 瀛楄妭娴佸埌鐩爣锛岃繃绋嬩腑璺宠繃鍖呭惈瀹炲喌鐓х墖鍏冩暟鎹殑 APP 娈碉紙XMP/EXIF锛夛紝
        // 閬垮厤鎷嗗垎鍑虹殑鍥剧墖浠嶅甫鏈?GCamera:MicroVideo / MotionPhoto 绛夋爣璁帮紝
        // 闃叉涓嬫鎵弿鏃跺啀娆¤璇瘑鍒负瀹炲喌鐓х墖銆?
        private static async Task CopyJpegStrippingLivePhotoMetadataAsync(Stream sourceStream, Stream destinationStream, long imageLength, CancellationToken token)
        {
            // 1. 纭繚璧峰鏄?SOI (0xFF 0xD8)
            byte[] soi = new byte[2];
            if (await ReadExactAsync(sourceStream, soi, 2, token) != 2 || soi[0] != 0xFF || soi[1] != 0xD8)
            {
                throw new InvalidDataException("Split image region is not a valid JPEG (missing SOI).");
            }
            await destinationStream.WriteAsync(soi.AsMemory(0, 2), token);
            long consumedInImage = 2;

            byte[] header = new byte[4];     // [0][1] 瀛?Marker锛孾2][3] 瀛?Length
            byte[] temp2 = new byte[2];      // 涓撻棬鐢ㄤ簬璇诲彇鐨?瀛楄妭灏忕紦鍐插尯锛岄伩鍏嶆寚閽堥敊浣?
            byte[] singleByte = new byte[1]; // 鐢ㄤ簬璺宠繃澶氫綑濉厖瀛楄妭鐨勫崟瀛楄妭缂撳啿鍖?
            byte[] segmentBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                while (consumedInImage < imageLength)
                {
                    token.ThrowIfCancellationRequested();

                    // 1. 璇诲彇 Marker (0xFF ??) 鍒?temp2
                    if (await ReadExactAsync(sourceStream, temp2, 2, token) != 2)
                    {
                        break; // EOF
                    }
                    consumedInImage += 2;

                    // 鍏煎鎬т繚鎶わ細JPEG 瑙勮寖鍏佽娈典箣闂存湁澶氫釜杩炵画鐨?0xFF 浣滀负濉厖瀛楄妭
                    while (temp2[0] == 0xFF && temp2[1] == 0xFF)
                    {
                        await destinationStream.WriteAsync(temp2.AsMemory(0, 1), token); // 灏嗗浣欑殑 0xFF 鍘熸牱鍐欏叆
                        temp2[0] = temp2[1];
                        if (await ReadExactAsync(sourceStream, singleByte, 1, token) != 1) break;
                        temp2[1] = singleByte[0];
                        consumedInImage += 1;
                    }

                    // 璁板綍鐪熷疄 Marker
                    header[0] = temp2[0];
                    header[1] = temp2[1];
                    byte marker = header[1];

                    // 閬囧埌 SOS (0xDA)锛氬啓鍏ユ爣璁板悗锛屽墿浣欏叏閮ㄤ负鍘嬬缉鍥惧儚鏍稿績鍍忕礌鏁版嵁锛岀洿鎺ュ師鏍锋嫹璐濆苟璺冲嚭
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

                    // 閬囧埌鏃犻暱搴﹀瓧娈电殑鐙珛鏍囪锛堝 RSTn 0xD0-0xD7銆丼OI 0xD8銆丒OI 0xD9銆?x00 濉厖锛?
                    if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01 || marker == 0x00)
                    {
                        await destinationStream.WriteAsync(header.AsMemory(0, 2), token);
                        if (marker == 0xD9) break; // 閬囧埌 EOI 姝ｅ父缁撴潫
                        continue;
                    }

                    // 2. 璇诲彇褰撳墠娈电殑闀垮害瀛楁 (2 瀛楄妭)
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

                    // 3. 浠呭 APP 娈?(0xE0 - 0xEF) 杩涜瀹炲喌鐓х墖 XMP 鍡呮帰
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

                        // JPEG HDR gain-map锛圙oogle Ultra HDR / ISO 21496-1锛変篃浣跨敤 XMP APP1锛?
                        // xmlns:hdrgm 鍜?Container/GainMap 涓?MotionPhoto 鍙兘鍑虹幇鍦ㄥ悓涓€涓?XMP 娈甸噷銆?
                        // 鏃ч€昏緫鎶婃暣涓?XMP 娈典涪寮冿紝浼氳繛 gain map 鍏冩暟鎹竴璧峰垹鎺夈€傜幇鍦ㄦ敼鎴愶細
                        //   1. 鍙惈 HDR锛氬師鏍蜂繚鐣欙紱
                        //   2. 鍙惈瀹炲喌鐓х墖锛氭暣娈典涪寮冿紱
                        //   3. 鍚屾椂鍚?HDR + 瀹炲喌鐓х墖锛氶噸鍐?XMP锛屽彧鍒犲疄鍐电収鐗囧瓧娈碉紝淇濈暀 hdrgm/GainMap銆?
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
                                // 瑙ｆ瀽澶辫触鏃跺畞鍙繚鐣欐暣娈碉紝涔熶笉鑳戒负鍓ョ瀹炲喌瀛楁鑰岃鍒?HDR銆?
                                // 鐗堟潈銆佽瘎鍒嗙瓑鏃犲叧 XMP銆?
                                await WriteAppSegmentAsync(destinationStream, marker, fullPayload, token);
                                LogService.Split(
                                    $"Could not precisely rewrite LivePhoto XMP (len={segmentLength}); preserved segment",
                                    LogLevel.Warning);
                            }
                        }
                        else
                        {
                            // 闈?XMP 鐨?APP 娈碉紙EXIF銆両CC銆丮PF 绛夛級鍘熸牱淇濈暀銆?
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
                        // 闈?APP 鍥惧儚蹇呰娈?(濡?DQT, DHT, SOF)锛氬師灏佷笉鍔ㄥ畬鏁村啓鍏?
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

            // 鍏滃簳锛氬鏋滆繕鏈夊墿浣欏瓧鑺傛湭璇诲彇瀹岋紙濡傛枃浠跺熬閮ㄧ殑鍏朵粬闄勫姞鏁版嵁锛夛紝鍘熸牱鍐欏嚭淇濊瘉涓嶅嚭閿?
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

                // 鍒犻櫎 Directory 涓涔変负 MotionPhoto 鐨勬潯鐩紝淇濈暀 Primary / GainMap銆?
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

                // 鏅€?V2 鍦ㄥ垹闄?MotionPhoto 鍚庡彧鍓?Primary锛孌irectory 宸叉棤鎰忎箟锛涙暣鍧楀垹闄ゃ€?
                // Ultra HDR 浠嶆湁 GainMap锛堟垨鏈潵鏈煡杈呭姪 Item锛夛紝蹇呴』淇濈暀瀹屾暣 Directory銆?
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

                // XDocument 涓嶄細鑷姩鍒犻櫎宸茬粡涓嶇敤鐨?xmlns 澹版槑銆傛畫鐣欑殑 GCamera / OpCamera /
                // VCamera 绛夊懡鍚嶇┖闂翠粛浼氳Е鍙戞棫鎵弿鍣紱Container/Item 鍙湪 HDR 鐩綍浣跨敤鏃朵繚鐣欍€?
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

        // Clear the OPPO <c>oplus_*</c> marker from EXIF UserComment 鈥?
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

        // 鈹€鈹€ vivo X300 EXIF UserComment 娓呯悊 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // vivo X300 鍦?EXIF UserComment 閲屽啓 multi-frame 绛惧悕锛堜緵 vivo 鐩稿唽璇嗗埆锛夈€?
        // 涓?OPPO 涓嶅悓锛歷ivo 鐨?UserComment 鏄竴澶ф \n 鍒嗛殧鐨勭浉鏈虹姸鎬佹枃鏈紝涓嶆槸鍥哄畾鍓嶇紑銆?
        // 鍙湪妫€娴嬪埌 "multi-frame" 绛惧悕鏃舵暣娈垫竻绌猴紝涓嶇鍏朵粬鍐呭鐨?UserComment銆?
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

        // 鈹€鈹€ HEIC meta box XMP 鍓ョ 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // HEIC 婧愶紙Google V2 / Samsung / Fusion锛夊湪 meta box 閲屽甫瀹炲喌 XMP銆?
        // 涓?JPEG 浣跨敤鍚屼竴濂?XML 缁撴瀯鍖栨竻娲楋細鍙垹闄ゅ疄鍐靛瓧娈碉紝淇濈暀 HDR 涓庢櫘閫?XMP銆?
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

        // 鈹€鈹€ 涓夋槦/铻嶅悎 JPEG Trailer 瑙嗛瀹氫綅 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // 涓夋槦锛堝強铻嶅悎锛塉PEG = [JPEG .. EOI] + [MotionPhoto_Data 鏍囩(瑙嗛)][MotionPhoto_Version 鏍囩][SEFH..SEFT]銆?
        // 姣忎釜鏍囩锛歚[00 00][marker LE u16][name_len LE u32][name UTF-8][data]`銆?
        // 瑙嗛鍗?MotionPhoto_Data 鏍囩鐨?data 娈碉細浠?"MotionPhoto_Data" 鍚嶅瓧涔嬪悗锛?
        // 鍒颁笅涓€涓爣绛撅紙"MotionPhoto_Version"锛夊紑澶翠箣鍓嶃€?
        // 娉細涓嶈蛋 exiftool -b -EmbeddedVideoFile 鈥斺€?瀹炴祴 exiftool 瀵规湰 App 鑷骇鐨?
        // 2-tag 绠€鍖?Trailer 瑙ｆ瀽鎶ラ敊锛?Error processing Samsung trailer"锛夛紝
        // 鐩存帴鎸夊崗璁枃妗ｅ瓧鑺傛牸寮忚В鏋愬鍘熷巶 7-tag 涓庤嚜浜?2-tag 鍧囧彲闈犮€?
        public static (long videoStart, long videoLength)? FindSamsungJpegVideoRange(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return null;

                // "MotionPhoto_Data" 鍚嶅瓧锛?6 瀛楄妭锛変箣鍚庡嵆瑙嗛鏁版嵁
                long dataNamePos = FindBytesForward(fs, 0, "MotionPhoto_Data"u8, fileSize);
                if (dataNamePos < 0) return null;
                long videoStart = dataNamePos + "MotionPhoto_Data".Length;

                // 涓嬩竴涓爣绛?"MotionPhoto_Version" 鐨勫悕瀛楋紙19 瀛楄妭锛夛紝鍏舵爣绛惧ご 8 瀛楄妭鍦ㄥ悕瀛椾箣鍓?
                long versionNamePos = FindBytesForward(fs, videoStart, "MotionPhoto_Version"u8, fileSize);
                long videoEnd;
                if (versionNamePos >= 0)
                {
                    videoEnd = versionNamePos - 8;
                }
                else
                {
                    // 鍏滃簳锛氭棤 MotionPhoto_Version 鏃朵互 SEFH 榄旀暟鏀跺熬
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

        // 鍦?FileStream 涓粠 startPos 鍚戝悗鎼滅储浠绘剰瀛楄妭搴忓垪锛岃繑鍥炲叾缁濆鍋忕Щ锛堝垎鍧楁壂鎻忥紝閬垮厤澶у唴瀛樺垎閰嶏級銆?
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
                searchPos += actual - (pattern.Length - 1); // 閲嶅彔 pattern-1 瀛楄妭闃茶法鍧?
            }

            return -1;
        }

        // 鈹€鈹€ HEIC mpvd box 瀹氫綅锛堣胺姝?V2 / 涓夋槦鍏辩敤锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // 璋锋瓕 V2 HEIC = [HEIC 闈欐€佸浘] + [mpvd box: 8B header + 瑙嗛]锛堟棤 sefd锛夈€?
        // 涓夋槦 HEIC   = [HEIC 闈欐€佸浘] + [mpvd box: 8B header + 瑙嗛 + sefd box]銆?
        // 杩斿洖 (imageLength, videoStart, videoLength)锛氬浘鐗?= [0..mpvd box 璧风偣)锛?
        // 瑙嗛 = mpvd 鍐呴儴 sefd box锛堣嫢瀛樺湪锛変箣鍓嶇殑瑙嗛瀛楄妭锛涙棤 sefd 鏃跺彇鍒版枃浠跺熬銆?
        private static (long imageLength, long videoStart, long videoLength)? FindHeicMpvdRange(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return null;

                // 浠庢枃浠跺ご璺宠繃绗竴涓?ftyp 鍚庢悳绱?"mpvd" 椤跺眰 box
                Span<byte> first4 = stackalloc byte[4];
                fs.Seek(0, SeekOrigin.Begin);
                if (fs.Read(first4) < 4) return null;
                uint firstSize = ReadBigEndianU32(first4);
                long searchFrom = (firstSize >= 8 && firstSize <= fileSize) ? firstSize : 0;

                long mpvdPos = FindFourCCForward(fs, searchFrom, "mpvd"u8, fileSize);
                if (mpvdPos < 8) return null;

                // mpvd box 璧风偣 = mpvdPos - 4锛坰ize 瀛楁锛?
                long mpvdBoxStart = mpvdPos - 4;

                // 瑙嗛浠?mpvd 澶翠箣鍚庡紑濮?
                long videoStart = mpvdPos + 4;

                // 鍦?mpvd 鍐呴儴鎼滅储 sefd box锛岃棰戠粓鐐?= sefd box 鐨?size 瀛楁涔嬪墠
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

        // 鈹€鈹€ JPEG 涓诲浘 EOI 瀹氫綅锛堜笁鏄?铻嶅悎 JPEG 鍥剧墖杈圭晫锛夆攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
        // 涓夋槦/铻嶅悎 JPEG 鍦?EOI 涔嬪悗杩藉姞 Samsung Trailer锛岃棰戜笉鍦ㄦ枃浠跺熬銆?
        // 璇ユ柟娉曟部 JPEG 娈电粨鏋勮蛋鍒?SOS 鍚庯紝鎵弿鐔电紪鐮佹暟鎹噷鐨?EOI锛?xFFD9锛夛紝
        // 杩斿洖銆孍OI 涔嬪悗銆嶇殑瀛楄妭鍋忕Щ锛堝嵆绾?JPEG 鍥剧墖鐨勫瓧鑺傛暟锛夈€?
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

                // SOS锛氬叾鍚庢槸鐔电紪鐮佹暟鎹紝鎵弿鍏朵腑鐨?EOI
                if (marker == 0xDA)
                {
                    long scanStart = stream.Position;
                    long eoiBytes = await ScanForEoiAsync(stream, token);
                    return eoiBytes < 0 ? -1 : scanStart + eoiBytes;
                }

                // 鐩存帴閬囧埌 EOI锛堢┖鐔电紪鐮佹暟鎹級
                if (marker == 0xD9)
                {
                    return stream.Position;
                }

                // 鏃犻暱搴﹀瓧娈电殑鐙珛鏍囪
                if (marker == 0xD8 || marker == 0x01 || marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    continue;
                }

                // 鍏朵綑娈碉細璇婚暱搴﹀苟璺宠繃 payload
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

        // 浠庡綋鍓嶆祦浣嶇疆鎵弿鐔电紪鐮佹暟鎹紝杩斿洖銆屼粠鎵弿璧风偣鍒?EOI 鏈熬锛堝惈 FF D9 涓ゅ瓧鑺傦級銆嶇殑瀛楄妭鏁般€?
        // JPEG 鐔垫暟鎹湁瀛楄妭濉厖锛?xFF 鍚庡繀涓?0x00 鎴?restart 鏍囪锛夛紝鍥犳 0xFFD9 鍙細鏄?EOI銆?
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

        // 灏嗘簮 JPEG 鐨勫叧閿厓鏁版嵁锛圕ontentIdentifier銆佹媿鎽勬棩鏈燂級鍐欏洖鎷嗗垎鍑虹殑瑙嗛鏂囦欢锛?
        // 纭繚鍚庣画鍏冩暟鎹尮閰嶈兘璇嗗埆鎷嗗垎鍚庣殑瑙嗛涓庣収鐗囧睘浜庡悓涓€瀹炲喌鐓х墖銆?
        private static async Task CopyMetadataToVideoAsync(
            string sourceImagePath, string videoOutputPath, CancellationToken token)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(exifToolPath))
                return;

            try
            {
                // 1. 浠庢簮鍥剧墖璇诲彇鍏冩暟鎹?
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

                // 2. 鍐欏叆瑙嗛鏂囦欢
                var writeArgs = new List<string>();
                writeArgs.Add("-overwrite_original");

                if (!string.IsNullOrWhiteSpace(cid))
                    writeArgs.Add($"-ContentIdentifier={cid}");

                if (!string.IsNullOrWhiteSpace(dto))
                {
                    // 鎷兼帴鏃跺尯鍋忕Щ锛岀‘淇濊棰戝啓鍏ョ殑鏄纭殑 UTC 鏃堕棿
                    string dateWithOffset = string.IsNullOrWhiteSpace(offset) ? dto : dto + offset;
                    writeArgs.Add($"-CreateDate={dateWithOffset}");
                }

                if (!string.IsNullOrWhiteSpace(make))
                    writeArgs.Add($"-Make={make}");

                if (!string.IsNullOrWhiteSpace(model))
                    writeArgs.Add($"-Model={model}");

                // GPS锛氭嫾鎺ョ含搴?缁忓害鍜屾柟鍚戞爣璇?
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

                if (writeArgs.Count > 1) // 鏈夐櫎浜?-overwrite_original 涔嬪鐨勫弬鏁?
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

        // 瀹夊叏鍦颁粠 JsonElement 璇诲彇瀛楃涓插睘鎬у€硷紝浠呭綋 ValueKind 涓?String 鏃惰繑鍥炪€?
        private static string TryGetJsonString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? "";
            return "";
        }

        // 浠庢鍒欏尮閰嶇殑 "value" 鍛藉悕缁勪腑瀹夊叏瑙ｆ瀽 long 鍊笺€?
        private static bool TryGetLong(Match match, out long value)
        {
            value = 0;
            string rawValue = match.Groups["value"].Value;
            return !string.IsNullOrWhiteSpace(rawValue) && long.TryParse(rawValue, out value);
        }
    }
}

