/*
 * EditOpenPipeline.cs
 *
 * 统一媒体打开与拖拽加载管线服务（C# Control Plane）。
 * 对应重构路线图 EP4 — Unified Open / Drag & Drop Pipeline。
 *
 * 核心架构原则：
 * 1. 统一入口：不论拖拽、列表点击、文件选择器均通过 OpenMediaAsync 提交。
 * 2. 严格代数与取消保护：请求自动递增 Generation，并终止前序进行的异步 inspection。
 * 3. 严格状态机：Closed -> Loading -> Ready。异常与取消强制 fail-closed（回到 Closed）。
 * 4. 独立于 UI 线程：无 WinUI / DispatcherQueue 依赖，完全可独立单元测试。
 * 5. 多文件/文件夹拖拽安全：自动发现成对实况照片，且仅自动打开第一个主媒体，杜绝竞态。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// 统一媒体打开与拖拽加载管线。
/// </summary>
public sealed class EditOpenPipeline : IDisposable
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".jpg", ".jpeg"
    };

    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4"
    };

    public static bool IsSupportedImage(string? ext) =>
        !string.IsNullOrEmpty(ext) && SupportedImageExtensions.Contains(ext);

    public static bool IsSupportedVideo(string? ext) =>
        !string.IsNullOrEmpty(ext) && SupportedVideoExtensions.Contains(ext);

    public static bool IsSupportedMedia(string? ext) =>
        IsSupportedImage(ext) || IsSupportedVideo(ext);

    private readonly ISourceInspector _inspector;
    private int _generation;
    private CancellationTokenSource? _activeCts;

    /// <summary>当前正在查看/编辑的统一媒体文档领域模型（未就绪或已关闭为 null）</summary>
    public EditDocument? CurrentDocument { get; private set; }

    /// <summary>当前会话生命周期状态</summary>
    public EditSessionState SessionState { get; private set; } = EditSessionState.Closed;

    /// <summary>当前代数计数器</summary>
    public int CurrentGeneration => Volatile.Read(ref _generation);

    /// <summary>当前主媒体路径</summary>
    public string? CurrentPrimaryPath => CurrentDocument?.PrimaryPath;

    /// <summary>当前是否打开了有效文档</summary>
    public bool HasDocument => CurrentDocument != null;

    /// <summary>当前文档变更事件通知</summary>
    public event Action<EditDocument?>? DocumentChanged;

    /// <summary>会话状态变更事件通知</summary>
    public event Action<EditSessionState>? SessionStateChanged;

    public EditOpenPipeline(ISourceInspector? inspector = null)
    {
        _inspector = inspector ?? new SourceInspector();
    }

    /// <summary>
    /// 打开指定主媒体文件。
    /// </summary>
    public Task<bool> OpenMediaAsync(string primaryPath, CancellationToken cancellationToken = default)
    {
        return OpenMediaAsync(new EditOpenRequest(primaryPath), cancellationToken);
    }

    /// <summary>
    /// 执行统一媒体打开流程。
    /// 包含代数递增、前序取消、格式校验、状态机转换与 Native Inspection 接入。
    /// </summary>
    public async Task<bool> OpenMediaAsync(EditOpenRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PrimaryPath))
        {
            if (CurrentDocument == null)
            {
                SessionState = EditSessionState.Closed;
                SessionStateChanged?.Invoke(SessionState);
            }
            return false;
        }

        string primaryPath = request.PrimaryPath;
        string ext = Path.GetExtension(primaryPath);

        // 1. 输入校验：文件必须存在且格式受支持
        if (!File.Exists(primaryPath) || !IsSupportedMedia(ext))
        {
            if (CurrentDocument == null)
            {
                SessionState = EditSessionState.Closed;
                SessionStateChanged?.Invoke(SessionState);
            }
            return false;
        }

        // 2. 确立新的请求代数，并取消前序正在进行的异步任务
        int myGen = Interlocked.Increment(ref _generation);

        _activeCts?.Cancel();
        _activeCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;
        var token = cts.Token;

        // 3. 切换状态到 Loading 并创建初始占位文档
        SessionState = EditSessionState.Loading;
        SessionStateChanged?.Invoke(SessionState);

        bool isVideo = IsSupportedVideo(ext);
        EditDocument initialDoc;
        if (isVideo)
        {
            initialDoc = EditDocument.FromVideo(primaryPath);
        }
        else if (!string.IsNullOrEmpty(request.MotionPath))
        {
            bool pairedExists = File.Exists(request.MotionPath);
            initialDoc = EditDocument.FromLivePhoto(
                primaryPath,
                request.MotionPath,
                LivePhotoType.DualFile,
                LivePhotoProtocolType.Unknown,
                pairState: pairedExists ? EditPairState.Complete : EditPairState.MissingVideo);
        }
        else
        {
            initialDoc = EditDocument.FromPhoto(primaryPath);
        }

        CurrentDocument = initialDoc;
        DocumentChanged?.Invoke(CurrentDocument);

        // 4. 异步执行 Native Inspection 探测媒体事实
        try
        {
            token.ThrowIfCancellationRequested();
            if (myGen != Volatile.Read(ref _generation))
                return false;

            var facts = await _inspector.InspectAsync(primaryPath, request.MotionPath, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            if (myGen != Volatile.Read(ref _generation))
                return false;

            LivePhotoType livePhotoType = LivePhotoType.None;
            EditPairState pairState = EditPairState.NotApplicable;

            if (facts.Protocol != SourceProtocol.NonLive && facts.Protocol != SourceProtocol.Unknown)
            {
                if (!string.IsNullOrEmpty(request.MotionPath))
                {
                    livePhotoType = LivePhotoType.DualFile;
                    pairState = File.Exists(request.MotionPath) ? EditPairState.Complete : EditPairState.MissingVideo;
                }
                else if (facts.MotionVideo is { IsPresent: true, SourceIndex: 0 })
                {
                    livePhotoType = ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
                                    ext.Equals(".heif", StringComparison.OrdinalIgnoreCase)
                        ? LivePhotoType.SingleFileHeic
                        : LivePhotoType.SingleFileJpeg;
                }
            }

            var finalDoc = EditDocumentMapper.FromInspectedFacts(
                primaryPath,
                request.MotionPath,
                livePhotoType,
                pairState,
                facts);

            token.ThrowIfCancellationRequested();
            if (myGen != Volatile.Read(ref _generation))
                return false;

            // 5. 提交就绪状态
            CurrentDocument = finalDoc;
            SessionState = EditSessionState.Ready;
            DocumentChanged?.Invoke(CurrentDocument);
            SessionStateChanged?.Invoke(SessionState);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (myGen == Volatile.Read(ref _generation))
            {
                FailClosed();
            }
            return false;
        }
        catch (Exception)
        {
            if (myGen == Volatile.Read(ref _generation))
            {
                FailClosed();
            }
            return false;
        }
    }

    private void FailClosed()
    {
        CurrentDocument = null;
        SessionState = EditSessionState.Closed;
        DocumentChanged?.Invoke(null);
        SessionStateChanged?.Invoke(SessionState);
    }

    /// <summary>
    /// 关闭当前会话并取消所有进行中的任务。
    /// </summary>
    public void Close()
    {
        Interlocked.Increment(ref _generation);
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = null;

        FailClosed();
    }

    /// <summary>
    /// 发现、展开并配对一组输入文件/目录路径中的合法媒体项。
    /// </summary>
    public async Task<IReadOnlyList<EditOpenCandidate>> DiscoverCandidatesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        if (paths == null) return Array.Empty<EditOpenCandidate>();

        // 1. 展开目录并过滤受支持的媒体扩展名
        var allFiles = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (Directory.Exists(p))
            {
                try
                {
                    var files = Directory.EnumerateFiles(p, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => IsSupportedMedia(Path.GetExtension(f)));
                    allFiles.AddRange(files);
                }
                catch { }
            }
            else if (File.Exists(p) && IsSupportedMedia(Path.GetExtension(p)))
            {
                allFiles.Add(p);
            }
        }

        var existingPaths = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existingPaths.Count == 0) return Array.Empty<EditOpenCandidate>();

        var images = existingPaths.Where(p => IsSupportedImage(Path.GetExtension(p))).ToList();
        var videos = existingPaths.Where(p => IsSupportedVideo(Path.GetExtension(p))).ToList();

        var pairedVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairedImages = new Dictionary<string, (string VideoPath, SourceProtocol Protocol)>(StringComparer.OrdinalIgnoreCase);

        // 2. 双文件配对识别（基于 Native Inspector）
        foreach (var imagePath in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var videoPath in videos)
            {
                if (pairedVideos.Contains(videoPath)) continue;

                try
                {
                    var facts = await _inspector.InspectAsync(imagePath, videoPath, cancellationToken).ConfigureAwait(false);
                    if (facts.Protocol is SourceProtocol.AppleLivePhoto or SourceProtocol.VivoLegacyDualFile &&
                        facts.MotionVideo is { IsPresent: true, SourceIndex: 1 })
                    {
                        pairedImages[imagePath] = (videoPath, facts.Protocol);
                        pairedVideos.Add(videoPath);
                        break;
                    }
                }
                catch { }
            }
        }

        // 3. 构建候选事实列表
        var candidates = new List<EditOpenCandidate>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in existingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processed.Contains(path) || pairedVideos.Contains(path))
                continue;

            string ext = Path.GetExtension(path);
            bool isImage = IsSupportedImage(ext);
            LivePhotoType type = LivePhotoType.None;
            string? pairedVideo = null;
            SourceProtocol protocol = SourceProtocol.NonLive;
            long appendedLen = 0;

            if (isImage && pairedImages.TryGetValue(path, out var pair))
            {
                type = LivePhotoType.DualFile;
                pairedVideo = pair.VideoPath;
                protocol = pair.Protocol;
            }
            else if (isImage)
            {
                try
                {
                    var facts = await _inspector.InspectAsync(path, null, cancellationToken).ConfigureAwait(false);
                    protocol = facts.Protocol;
                    if (facts.MotionVideo is { IsPresent: true, SourceIndex: 0 } motionVideo &&
                        protocol is not (SourceProtocol.NonLive or SourceProtocol.Unknown))
                    {
                        type = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                            ? LivePhotoType.SingleFileJpeg
                            : LivePhotoType.SingleFileHeic;
                        appendedLen = motionVideo.ByteLength;
                    }
                }
                catch { }
            }

            long size = 0;
            DateTime date = DateTime.MinValue;
            try
            {
                var fi = new FileInfo(path);
                size = fi.Length;
                date = fi.LastWriteTime;
                if (pairedVideo != null && File.Exists(pairedVideo))
                {
                    size += new FileInfo(pairedVideo).Length;
                }
            }
            catch { }

            candidates.Add(new EditOpenCandidate
            {
                PrimaryPath = path,
                MotionPath = pairedVideo,
                LivePhotoType = type,
                Protocol = protocol,
                AppendedVideoLength = appendedLen,
                TotalByteSize = size,
                DateModified = date
            });

            processed.Add(path);
            if (pairedVideo != null) processed.Add(pairedVideo);
        }

        return candidates;
    }

    /// <summary>
    /// 处理拖拽文件集：自动识别所有候选媒体，但仅自动打开列表中的首个主媒体。
    /// 其余候选文件仅作为发现项返回，不进行并发抢占式打开。
    /// </summary>
    public async Task<EditDropResult> ProcessDroppedFilesAsync(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        var candidates = await DiscoverCandidatesAsync(paths, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new EditDropResult
            {
                Candidates = Array.Empty<EditOpenCandidate>(),
                OpenedRequest = null,
                Succeeded = false
            };
        }

        var first = candidates[0];
        var request = new EditOpenRequest(first.PrimaryPath, first.MotionPath);
        bool opened = await OpenMediaAsync(request, cancellationToken).ConfigureAwait(false);

        return new EditDropResult
        {
            Candidates = candidates,
            OpenedRequest = request,
            Succeeded = opened
        };
    }

    public void Dispose()
    {
        Close();
    }
}
