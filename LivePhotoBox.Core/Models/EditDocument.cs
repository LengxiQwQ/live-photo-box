/*
 * EditDocument.cs
 *
 * Edit Page 当前正在查看/编辑的媒体文档领域模型。
 * 对应重构路线图 EP1 — EditDocument Domain Model / Edit Session Foundation。
 *
 * 架构规则：
 * 1. 纯领域模型，不是 ViewModel、不是 Service、不是 Native Inspector、不是解码器。
 * 2. 只保存事实，不负责自主发现事实（无 Inspect、Decode、ParseHeic 等行为）。
 * 3. 严格禁止包含 UI / Presentation 类型（如 BitmapImage, ImageSource, Control）。
 * 4. 严格禁止持有大型媒体二进制 buffer。
 */

using System;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Models;

/// <summary>
/// 编辑器中的媒体大类。
/// </summary>
public enum EditMediaKind
{
    /// <summary>未知类型</summary>
    Unknown = 0,
    /// <summary>静态照片</summary>
    Photo = 1,
    /// <summary>独立视频</summary>
    Video = 2,
    /// <summary>实况照片（单文件或双文件）</summary>
    LivePhoto = 3
}

/// <summary>
/// 双文件实况照片的配对完整性状态。
/// </summary>
public enum EditPairState
{
    /// <summary>不适用（非双文件实况照片：普通照片、独立视频或单文件实况无需配对）</summary>
    NotApplicable = 0,
    /// <summary>配对完整（图片与视频均有效存在）</summary>
    Complete = 1,
    /// <summary>缺失配对视频（双文件实况仅有图片）</summary>
    MissingVideo = 2,
    /// <summary>缺失配对图片（双文件实况仅有视频）</summary>
    MissingPhoto = 3
}

/// <summary>
/// 编辑会话生命周期状态。
/// </summary>
public enum EditSessionState
{
    /// <summary>无文档打开或已关闭</summary>
    Closed = 0,
    /// <summary>正在加载/探测文档事实</summary>
    Loading = 1,
    /// <summary>文档就绪可查看/编辑</summary>
    Ready = 2
}

/// <summary>
/// 实况照片封面帧的不可变轻量事实描述。
/// </summary>
public sealed record EditKeyPhotoInfo
{
    /// <summary>封面帧在时间轴或视频中的帧索引（未探测或非帧定位时为 -1）</summary>
    public int FrameIndex { get; init; } = -1;

    /// <summary>封面时间戳（秒）</summary>
    public double TimestampSeconds { get; init; }

    /// <summary>封面帧在视频中的微秒时间戳（与 Native TimingFacts.CoverTimestampUs 一致）</summary>
    public long TimestampUs { get; init; }

    /// <summary>是否为原始封面（如 OPPO 换过封面后保留的原始拍摄封面）</summary>
    public bool IsOriginal { get; init; }
}

/// <summary>
/// 已探测的只读元数据事实（只存事实，不含解析逻辑）。
/// </summary>
public sealed record EditMetadataFacts
{
    /// <summary>相机/设备型号，如 "Apple iPhone 15 Pro"</summary>
    public string CameraModel { get; init; } = string.Empty;

    /// <summary>镜头参数，如 "24mm f/1.78"</summary>
    public string LensModel { get; init; } = string.Empty;

    /// <summary>曝光拍摄参数，如 "1/120s ISO 80"</summary>
    public string ShootingParams { get; init; } = string.Empty;

    /// <summary>拍摄地理位置/城市名称</summary>
    public string PlaceName { get; init; } = string.Empty;

    /// <summary>拍摄时间</summary>
    public DateTime? DateTaken { get; init; }
}

/// <summary>
/// Edit Page 正在查看/编辑的一个媒体文档领域模型。
///
/// 架构边界：
/// - 纯领域事实模型，不是 ViewModel、不是 Service、不是 Native Inspector、不是解码器。
/// - 只保存事实，不负责自主发现事实（禁止 Inspect、Decode、ParseHeic 等操作）。
/// - 禁止包含任何 UI / Presentation 类型（BitmapImage, ImageSource, Control 等）。
/// - 禁止持有大型二进制数据 buffer。
/// </summary>
public sealed record EditDocument
{
    // ══════════════════════════════════════════════════════════════
    //  Identity
    // ══════════════════════════════════════════════════════════════

    /// <summary>主媒体文件路径（照片完整路径，或独立视频完整路径）</summary>
    public required string PrimaryPath { get; init; }

    /// <summary>动态视频路径（双文件实况照片的独立配对视频路径；单文件实况或非实况为 null）</summary>
    public string? MotionPath { get; init; }

    /// <summary>双文件实况照片配对视频路径（MotionPath 的语义别名）</summary>
    public string? PairedVideoPath => MotionPath;

    // ══════════════════════════════════════════════════════════════
    //  Media Identity
    // ══════════════════════════════════════════════════════════════

    /// <summary>媒体大类（照片、独立视频、实况照片）</summary>
    public EditMediaKind MediaKind { get; init; } = EditMediaKind.Unknown;

    /// <summary>实况照片物理形态分类（None, DualFile, SingleFileJpeg, SingleFileHeic）</summary>
    public LivePhotoType LivePhotoType { get; init; } = LivePhotoType.None;

    /// <summary>实况照片协议类型（UI 展示层规范分类）</summary>
    public LivePhotoProtocolType Protocol { get; init; } = LivePhotoProtocolType.Unknown;

    /// <summary>Native Inspector 探测的细分源协议事实</summary>
    public SourceProtocol SourceProtocol { get; init; } = SourceProtocol.Unknown;

    // ══════════════════════════════════════════════════════════════
    //  Structural facts
    // ══════════════════════════════════════════════════════════════

    /// <summary>是否为实况照片（形态不为 None 或 MediaKind 为 LivePhoto）</summary>
    public bool IsLivePhoto => MediaKind == EditMediaKind.LivePhoto || LivePhotoType != LivePhotoType.None;

    /// <summary>配对完整性状态</summary>
    public EditPairState PairState { get; init; } = EditPairState.NotApplicable;

    /// <summary>配对是否完整（非双文件实况或双文件双方齐备时为 true）</summary>
    public bool IsPairComplete => PairState is EditPairState.Complete or EditPairState.NotApplicable;

    /// <summary>内嵌视频字节偏移量（单文件实况照片时 > 0）</summary>
    public long MotionVideoByteOffset { get; init; }

    /// <summary>内嵌视频字节长度（单文件实况照片时 > 0）</summary>
    public long MotionVideoByteLength { get; init; }

    /// <summary>单文件内嵌视频长度别名（兼容旧代码）</summary>
    public long AppendedVideoLength => MotionVideoByteLength;

    /// <summary>是否具有内嵌视频段</summary>
    public bool HasEmbeddedMotionVideo => MotionVideoByteLength > 0;

    // ══════════════════════════════════════════════════════════════
    //  Media facts
    // ══════════════════════════════════════════════════════════════

    /// <summary>图像/主媒体宽度（像素）</summary>
    public uint Width { get; init; }

    /// <summary>图像/主媒体高度（像素）</summary>
    public uint Height { get; init; }

    /// <summary>动态视频宽度（像素，若为独立视频则与 Width 一致）</summary>
    public uint VideoWidth { get; init; }

    /// <summary>动态视频高度（像素，若为独立视频则与 Height 一致）</summary>
    public uint VideoHeight { get; init; }

    /// <summary>动态视频或独立视频的时长（秒）</summary>
    public double DurationSeconds { get; init; }

    /// <summary>动态视频帧率（FPS）</summary>
    public double FrameRate { get; init; }

    /// <summary>图像容器格式（Jpeg, Heic 等）</summary>
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;

    /// <summary>视频容器格式（Mp4, Mov 等）</summary>
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;

    /// <summary>视频编码格式（H264, Hevc 等）</summary>
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Unknown;

    /// <summary>视频是否包含音频轨</summary>
    public bool HasAudio { get; init; }

    /// <summary>视频旋转角度（0, 90, 180, 270）</summary>
    public int RotationDegrees { get; init; }

    // ══════════════════════════════════════════════════════════════
    //  Edit State
    // ══════════════════════════════════════════════════════════════

    /// <summary>初始/原始封面状态</summary>
    public EditKeyPhotoInfo? OriginalKeyPhoto { get; init; }

    /// <summary>当前选中/设定的封面状态</summary>
    public EditKeyPhotoInfo? CurrentKeyPhoto { get; init; }

    // ══════════════════════════════════════════════════════════════
    //  Metadata Facts
    // ══════════════════════════════════════════════════════════════

    /// <summary>只读元数据事实（只存事实，不负责解析）</summary>
    public EditMetadataFacts Metadata { get; init; } = new();

    /// <summary>文档创建时间（UTC）</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // ══════════════════════════════════════════════════════════════
    //  In-Memory Factory / C# Adapter Helpers (Pure facts mapping)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从静态照片事实创建文档。
    /// </summary>
    public static EditDocument FromPhoto(
        string primaryPath,
        uint width = 0,
        uint height = 0,
        ImageContainer container = ImageContainer.Unknown,
        EditMetadataFacts? metadata = null)
    {
        return new EditDocument
        {
            PrimaryPath = primaryPath,
            MotionPath = null,
            MediaKind = EditMediaKind.Photo,
            LivePhotoType = LivePhotoType.None,
            Protocol = LivePhotoProtocolType.Unknown,
            SourceProtocol = SourceProtocol.NonLive,
            PairState = EditPairState.NotApplicable,
            Width = width,
            Height = height,
            ImageContainer = container,
            Metadata = metadata ?? new()
        };
    }

    /// <summary>
    /// 从独立视频事实创建文档。
    /// </summary>
    public static EditDocument FromVideo(
        string primaryPath,
        uint width = 0,
        uint height = 0,
        double durationSeconds = 0.0,
        double frameRate = 0.0,
        VideoContainer container = VideoContainer.Unknown,
        VideoCodec codec = VideoCodec.Unknown,
        bool hasAudio = false,
        EditMetadataFacts? metadata = null)
    {
        return new EditDocument
        {
            PrimaryPath = primaryPath,
            MotionPath = null,
            MediaKind = EditMediaKind.Video,
            LivePhotoType = LivePhotoType.None,
            Protocol = LivePhotoProtocolType.Unknown,
            SourceProtocol = SourceProtocol.NonLive,
            PairState = EditPairState.NotApplicable,
            Width = width,
            Height = height,
            VideoWidth = width,
            VideoHeight = height,
            DurationSeconds = durationSeconds,
            FrameRate = frameRate,
            VideoContainer = container,
            VideoCodec = codec,
            HasAudio = hasAudio,
            Metadata = metadata ?? new()
        };
    }

    /// <summary>
    /// 从实况照片通用事实创建文档。
    /// </summary>
    public static EditDocument FromLivePhoto(
        string primaryPath,
        string? motionPath,
        LivePhotoType livePhotoType,
        LivePhotoProtocolType protocol,
        SourceProtocol sourceProtocol = SourceProtocol.Unknown,
        EditPairState pairState = EditPairState.NotApplicable,
        uint width = 0,
        uint height = 0,
        uint videoWidth = 0,
        uint videoHeight = 0,
        double durationSeconds = 0.0,
        double frameRate = 0.0,
        long motionVideoByteOffset = 0,
        long motionVideoByteLength = 0,
        ImageContainer imageContainer = ImageContainer.Unknown,
        VideoContainer videoContainer = VideoContainer.Unknown,
        VideoCodec videoCodec = VideoCodec.Unknown,
        bool hasAudio = false,
        EditKeyPhotoInfo? originalKeyPhoto = null,
        EditKeyPhotoInfo? currentKeyPhoto = null,
        EditMetadataFacts? metadata = null)
    {
        return new EditDocument
        {
            PrimaryPath = primaryPath,
            MotionPath = motionPath,
            MediaKind = EditMediaKind.LivePhoto,
            LivePhotoType = livePhotoType,
            Protocol = protocol,
            SourceProtocol = sourceProtocol,
            PairState = pairState,
            Width = width,
            Height = height,
            VideoWidth = videoWidth,
            VideoHeight = videoHeight,
            DurationSeconds = durationSeconds,
            FrameRate = frameRate,
            MotionVideoByteOffset = motionVideoByteOffset,
            MotionVideoByteLength = motionVideoByteLength,
            ImageContainer = imageContainer,
            VideoContainer = videoContainer,
            VideoCodec = videoCodec,
            HasAudio = hasAudio,
            OriginalKeyPhoto = originalKeyPhoto,
            CurrentKeyPhoto = currentKeyPhoto ?? originalKeyPhoto,
            Metadata = metadata ?? new()
        };
    }
}

