/*
 * EditExportService.cs
 *
 * Edit Page 的专用媒体导出服务（UI-only Export Service）。
 * 剥离并封装单帧导出、多帧批量导出、视频导出与 GIF 导出等所有导出业务逻辑。
 *
 * 核心架构原则：
 * 1. 彻底解耦 ViewModel：ViewModel 仅负责维持进度条/状态机属性，不直接接触导出细节、格式转换或外部工具。
 * 2. 严密的生命周期与临时文件管理：临时切片文件、转换输出、工作空间资源在完成、失败、取消或服务释放时绝不泄漏。
 * 3. 严格的并发与取消控制：内置 CancellationTokenSource 管理与原子计数，支持多任务优雅取消。
 * 4. 进度与状态解耦：通过 IEditExportProgressNotifier 向上层通知导出进度、结果与守卫状态。
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Helpers;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services.Protocols;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace LivePhotoBox.Services;

/// <summary>
/// 导出进度与状态通知接口（上层 ViewModel 实现，用于驱动 UI 状态机）。
/// </summary>
public interface IEditExportProgressNotifier
{
    /// <summary>开始导出：清空旧状态，显示进度面板，设置初始文字</summary>
    void NotifyBegin(string progressText, string? progressPrefix = null);

    /// <summary>更新导出进度百分比与文本（如多帧导出 12/80）</summary>
    void NotifyProgress(int completed, int total);

    /// <summary>导出完成：显示成功图标与完成文本，记录输出目录</summary>
    void NotifyComplete(string completionText, string? outputDir);

    /// <summary>导出失败：显示错误图标与失败文本，记录错误详情与输出目录</summary>
    void NotifyFailure(string failureText, string errorMessage, string? outputDir = null);

    /// <summary>导出守卫拦截：显示警告说明文本</summary>
    void NotifyGuardError(string errorText);

    /// <summary>导出流程结束（finally 清理）</summary>
    void NotifyFinalize();
}

/// <summary>
/// 编辑页媒体导出服务，封装单帧、多帧、视频与动图的导出管线。
/// </summary>
public sealed class EditExportService : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> s_batchExportCandidates =
        new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _activeExportCts;
    private string? _exportTempVideoPath;
    private IMediaWorkspace? _exportMediaWorkspace;
    private bool _disposed;

    // ══════════════════════════════════════════════════════════════
    //  单帧导出
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 导出当前帧：弹出多格式另存为窗口，用户选格式后按需转换。
    /// 支持 JPEG / WebP / BMP / TIFF / HEIC。
    /// </summary>
    public async Task ExportCurrentFrameAsync(
        TimelineFrame? frame,
        bool isSelectedLivePhoto,
        bool isSelectedFileVideo,
        string? primaryPhotoPath,
        IEditExportProgressNotifier progress,
        CancellationToken cancellationToken = default)
    {
        // 不完整实况（仅照片，无时间轴）：直接导出照片文件本身
        if (frame == null && isSelectedLivePhoto && !isSelectedFileVideo)
        {
            await ExportPhotoAsSingleFrameAsync(primaryPhotoPath, progress, cancellationToken);
            return;
        }

        if (frame == null) return;

        WindowsOwnedTemporaryArtifact? preparedSourceArtifact = null;
        Exception? originalFailure = null;
        string sourcePath;

        try
        {
            // 1. 确定源文件路径
            if (frame.IsStillPhoto || frame.IsOriginalPhoto)
            {
                if (frame.IsOriginalPhoto)
                {
                    if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    {
                        if (string.IsNullOrEmpty(primaryPhotoPath) || !File.Exists(primaryPhotoPath)) return;
                        byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(primaryPhotoPath);
                        if (origBytes == null || origBytes.Length == 0) return;
                        preparedSourceArtifact = await ExportSourcePreparation.PrepareOriginalBytesAsync(
                            origBytes,
                            cancellationToken);
                        sourcePath = preparedSourceArtifact.Path;
                    }
                    else
                    {
                        sourcePath = frame.FullFramePath;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(primaryPhotoPath) || !File.Exists(primaryPhotoPath)) return;
                    preparedSourceArtifact = await ResolveStillPhotoSourceAsync(primaryPhotoPath, cancellationToken);
                    sourcePath = preparedSourceArtifact?.Path ?? primaryPhotoPath;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    return;
                sourcePath = frame.FullFramePath;
            }

            // 2. 生成建议文件名
            var photoBaseName = Path.GetFileNameWithoutExtension(primaryPhotoPath ?? "photo");
            var suggestedName = frame.IsStillPhoto
                ? photoBaseName
                : frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始帧"
                    : $"{photoBaseName}_帧{frame.FrameIndex + 1}";

            // 3. 弹出多格式另存为窗口
            var targetFile = await FilePickerService.PickSaveFileForExportMultiFormatAsync(suggestedName);
            if (targetFile == null) return;

            // 4. 显示进度
            string targetPath = targetFile.Path;
            progress.NotifyBegin(ResourceService.GetString("EditPage_ExportCurrentFrameInProgress"));

            try
            {
                // 5. 根据用户选择的格式执行导出
                string targetExt = Path.GetExtension(targetPath);
                bool needsConversion = ImageFormatService.NeedsConversion(sourcePath, targetExt);

                if (needsConversion)
                {
                    await ImageFormatService.ConvertImageAsync(sourcePath, targetPath, quality: 80, token: cancellationToken);
                }
                else
                {
                    await PublishExportCopyAsync(sourcePath, targetPath, MediaArtifactKind.PrimaryImage, true, cancellationToken);
                }

                LogService.FileOp(
                    $"ExportCurrentFrame: {Path.GetFileName(sourcePath)} -> {targetPath}",
                    LogLevel.Info);

                // The transaction's final write already supplies the current write time.

                ExportSourcePreparation.DisposePreparedSource(ref preparedSourceArtifact);

                // 7. 完成状态
                progress.NotifyComplete(
                    ResourceService.GetString("EditPage_ExportCurrentFrameComplete"),
                    Path.GetDirectoryName(targetPath));
            }
            catch (Exception ex)
            {
                originalFailure = ex;
                LogService.FileOp(
                    $"ExportCurrentFrame failed: {ex.Message}", LogLevel.Error, ex);
                progress.NotifyFailure(
                    ResourceService.GetString("EditPage_ExportCurrentFrameFailed"),
                    ex.Message, Path.GetDirectoryName(targetPath));
            }
            finally
            {
                progress.NotifyFinalize();
            }
        }
        catch (Exception ex)
        {
            if (originalFailure == null)
            {
                originalFailure = ex;
            }
            else if (!ReferenceEquals(originalFailure, ex))
            {
                originalFailure = new AggregateException(
                    "ExportCurrentFrame failed during export and failure notification/finalization.",
                    originalFailure,
                    ex);
                throw originalFailure;
            }

            throw;
        }
        finally
        {
            try
            {
                ExportSourcePreparation.DisposePreparedSource(ref preparedSourceArtifact, originalFailure);
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    LogService.FileOp(
                        $"ExportCurrentFrame: prepared source cleanup failed: {cleanupFailure.Message}",
                        LogLevel.Error,
                        cleanupFailure);
                }
                catch
                {
                    // Preserve and propagate the cleanup failure even if logging is unavailable.
                }
                throw;
            }
        }
    }

    /// <summary>
    /// 不完整实况（仅照片，无时间轴）：直接将照片文件作为单帧导出。
    /// </summary>
    public async Task ExportPhotoAsSingleFrameAsync(
        string? photoPath,
        IEditExportProgressNotifier progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath)) return;

        var photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
        var targetFile = await FilePickerService.PickSaveFileForExportMultiFormatAsync(photoBaseName);
        if (targetFile == null) return;

        string targetPath = targetFile.Path;
        progress.NotifyBegin(ResourceService.GetString("EditPage_ExportCurrentFrameInProgress"));

        try
        {
            string targetExt = Path.GetExtension(targetPath);
            bool needsConversion = ImageFormatService.NeedsConversion(photoPath, targetExt);
            if (needsConversion)
            {
                await ImageFormatService.ConvertImageAsync(photoPath, targetPath, quality: 80, token: cancellationToken);
            }
            else
            {
                await PublishExportCopyAsync(photoPath, targetPath, MediaArtifactKind.PrimaryImage, true, cancellationToken);
            }
            LogService.FileOp($"ExportPhotoAsSingleFrame: {Path.GetFileName(photoPath)} -> {targetPath}", LogLevel.Info);
            progress.NotifyComplete(
                ResourceService.GetString("EditPage_ExportCurrentFrameComplete"),
                Path.GetDirectoryName(targetPath));
        }
        catch (Exception ex)
        {
            LogService.FileOp($"ExportPhotoAsSingleFrame failed: {ex.Message}", LogLevel.Error, ex);
            progress.NotifyFailure(
                ResourceService.GetString("EditPage_ExportCurrentFrameFailed"),
                ex.Message, Path.GetDirectoryName(targetPath));
        }
        finally
        {
            progress.NotifyFinalize();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  多帧批量导出
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 导出所有帧：先弹出选项对话框 → 文件夹选择器 → 多线程并行导出所有帧，
    /// 更新进度 UI，导出完成后显示汇总结果。
    /// </summary>
    public async Task ExportAllFramesAsync(
        IReadOnlyList<TimelineFrame> frames,
        string? photoPath,
        IEditExportProgressNotifier progress,
        CancellationToken cancellationToken = default)
    {
        if (frames == null || frames.Count == 0)
        {
            LogService.FileOp("ExportAllFrames: no frames to export", LogLevel.Warning);
            return;
        }

        if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
        {
            LogService.FileOp("ExportAllFrames: no file selected or file not found", LogLevel.Warning);
            return;
        }

        var photoBaseName = Path.GetFileNameWithoutExtension(photoPath);
        var defaultDir = Path.GetDirectoryName(photoPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        var options = await ShowExportOptionsDialogAsync(photoBaseName, defaultDir);
        if (options == null)
        {
            LogService.FileOp("ExportAllFrames cancelled by user (options dialog)", LogLevel.Info);
            return;
        }

        var exportDir = GetUniqueFolderPath(options.ExportPath, options.FolderName);
        Directory.CreateDirectory(exportDir);

        LogService.FileOp(
            $"ExportAllFrames started: {frames.Count} frames -> '{exportDir}'",
            LogLevel.Info);

        CancelActiveExport();
        _activeExportCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_activeExportCts.Token, cancellationToken);
        var token = linkedCts.Token;

        progress.NotifyBegin($"0/{frames.Count}",
            ResourceService.GetString("EditPage_ExportAllFramesInProgress"));

        using var semaphore = new SemaphoreSlim(8, 8);
        var tasks = new List<Task>();
        var counters = new ExportCounters();

        try
        {
            foreach (var frame in frames)
            {
                token.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(token);

                tasks.Add(ExportOneFrameAsync(
                    frame, photoPath, photoBaseName, exportDir,
                    options.CopyExif, options.FormatExtension, options.Quality,
                    token, semaphore, frames.Count, counters, progress));
            }

            await Task.WhenAll(tasks);

            LogService.FileOp(
                $"ExportAllFrames completed: {counters.Success} succeeded, {counters.Fail} failed -> '{exportDir}'",
                counters.Fail > 0 ? LogLevel.Warning : LogLevel.Info);

            if (!token.IsCancellationRequested)
            {
                progress.NotifyComplete(
                    ResourceService.GetString("EditPage_ExportAllFramesComplete"),
                    exportDir);
            }
        }
        catch (OperationCanceledException)
        {
            LogService.FileOp("ExportAllFrames cancelled mid-operation", LogLevel.Warning);
            progress.NotifyFailure(
                ResourceService.GetString("EditPage_ExportAllFramesFailed"),
                "Operation was cancelled",
                exportDir);
        }
        catch (Exception ex)
        {
            LogService.FileOp($"ExportAllFrames fatal error: {ex.Message}", LogLevel.Error, ex);
            progress.NotifyFailure(
                ResourceService.GetString("EditPage_ExportAllFramesFailed"),
                ex.Message, exportDir);
        }
        finally
        {
            progress.NotifyFinalize();
            CancelActiveExport();
        }
    }

    private async Task ExportOneFrameAsync(
        TimelineFrame frame, string photoPath, string photoBaseName,
        string exportDir, bool copyExif, string formatExtension, int quality,
        CancellationToken token, SemaphoreSlim semaphore, int totalFrames,
        ExportCounters counters, IEditExportProgressNotifier progress)
    {
        WindowsOwnedTemporaryArtifact? preparedSourceArtifact = null;
        string? candidateReservationKey = null;
        Exception? originalFailure = null;
        try
        {
            token.ThrowIfCancellationRequested();

            string sourcePath;
            if (frame.IsStillPhoto || frame.IsOriginalPhoto)
            {
                if (frame.IsOriginalPhoto)
                {
                    if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                    {
                        byte[]? origBytes = EditTimingService.ReadOriginalPhotoBytes(photoPath);
                        if (origBytes == null || origBytes.Length == 0)
                        {
                            Interlocked.Increment(ref counters.Fail);
                            LogService.FileOp(
                                "ExportAllFrames: 🖼 original photo bytes unavailable",
                                LogLevel.Warning);
                            return;
                        }
                        preparedSourceArtifact = await ExportSourcePreparation.PrepareOriginalBytesAsync(
                            origBytes,
                            token);
                        sourcePath = preparedSourceArtifact.Path;
                    }
                    else
                    {
                        sourcePath = frame.FullFramePath;
                    }
                }
                else
                {
                    preparedSourceArtifact = await ResolveStillPhotoSourceAsync(photoPath, token);
                    sourcePath = preparedSourceArtifact?.Path ?? photoPath;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(frame.FullFramePath) || !File.Exists(frame.FullFramePath))
                {
                    Interlocked.Increment(ref counters.Fail);
                    LogService.FileOp(
                        $"ExportAllFrames: frame path missing — isStillPhoto=false, path='{frame.FullFramePath ?? "null"}'",
                        LogLevel.Warning);
                    return;
                }
                sourcePath = frame.FullFramePath;
            }

            var fileName = frame.IsStillPhoto
                ? $"{photoBaseName}{formatExtension}"
                : frame.IsOriginalPhoto
                    ? $"{photoBaseName}_原始帧{formatExtension}"
                    : $"{photoBaseName}_帧{frame.FrameIndex + 1}{formatExtension}";

            var finalTargetCandidate = AllocateUniqueBatchExportCandidate(exportDir, fileName);
            string finalTargetPath = finalTargetCandidate.CandidatePath;
            candidateReservationKey = finalTargetCandidate.ReservationKey;
            using var preparedOutput = new MediaWorkspace();
            string targetPath = preparedOutput.AllocateFilePath("prepared-export", formatExtension);

            if (ImageFormatService.NeedsConversion(sourcePath, formatExtension))
            {
                await ImageFormatService.ConvertImageAsync(sourcePath, targetPath, quality, token);
            }
            else
            {
                await PublishExportCopyAsync(sourcePath, targetPath, MediaArtifactKind.PrimaryImage, false, token);
            }

            if (copyExif && File.Exists(photoPath))
            {
                await CopyExifForExportAsync(photoPath, targetPath, token);
            }

            try { File.SetLastWriteTime(targetPath, DateTime.Now); } catch { }

            await PublishExportCopyAsync(targetPath, finalTargetPath, MediaArtifactKind.PrimaryImage, false, token);

            ExportSourcePreparation.DisposePreparedSource(ref preparedSourceArtifact);

            Interlocked.Increment(ref counters.Success);
        }
        catch (OperationCanceledException ex)
        {
            originalFailure = ex;
            throw;
        }
        catch (Exception ex)
        {
            originalFailure = ex;
            Interlocked.Increment(ref counters.Fail);
            LogService.FileOp(
                $"ExportAllFrames: frame {(frame.IsOriginalPhoto ? "🖼" : frame.IsStillPhoto ? "⭐" : $"#{frame.FrameIndex + 1}")} FAILED: {ex.Message}",
                LogLevel.Error, ex);
        }
        finally
        {
            Exception? finalizationFailure = null;
            try
            {
                ExportSourcePreparation.DisposePreparedSource(ref preparedSourceArtifact, originalFailure);
            }
            catch (Exception cleanupFailure)
            {
                finalizationFailure = cleanupFailure;
                try
                {
                    LogService.FileOp(
                        $"ExportOneFrame: prepared source cleanup failed: {cleanupFailure.Message}",
                        LogLevel.Error,
                        cleanupFailure);
                }
                catch (Exception loggingFailure)
                {
                    finalizationFailure = new AggregateException(
                        "Prepared source cleanup and cleanup logging both failed.",
                        finalizationFailure,
                        loggingFailure);
                }
            }
            finally
            {
                try
                {
                    if (candidateReservationKey != null)
                        s_batchExportCandidates.TryRemove(candidateReservationKey, out _);
                }
                catch (Exception reservationFailure)
                {
                    finalizationFailure = CombineFinalizationFailures(
                        finalizationFailure, reservationFailure, originalFailure);
                }
                finally
                {
                    try
                    {
                        int done = Interlocked.Increment(ref counters.Completed);
                        progress.NotifyProgress(done, totalFrames);
                    }
                    catch (Exception progressFailure)
                    {
                        finalizationFailure = CombineFinalizationFailures(
                            finalizationFailure, progressFailure, originalFailure);
                    }
                    finally
                    {
                        try
                        {
                            semaphore.Release();
                        }
                        catch (Exception releaseFailure)
                        {
                            finalizationFailure = CombineFinalizationFailures(
                                finalizationFailure, releaseFailure, originalFailure);
                        }
                    }
                }
            }

            if (finalizationFailure != null)
                throw finalizationFailure;
        }
    }

    // Select a candidate without leaving an empty placeholder at the final path.
    // PublishExportCopyAsync owns the no-replace operation; a file created by an
    // external process after this check remains protected by that transaction.
    private static (string CandidatePath, string ReservationKey) AllocateUniqueBatchExportCandidate(
        string directory,
        string fileName)
    {
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int ordinal = 1; ordinal < 999; ordinal++)
        {
            string candidateName = ordinal == 1
                ? fileName
                : $"{nameWithoutExtension} ({ordinal}){extension}";
            string candidate = Path.Combine(directory, candidateName);
            if (TryReserveBatchExportCandidate(candidate, out string? reservationKey))
                return (candidate, reservationKey!);
        }

        while (true)
        {
            string candidate = Path.Combine(
                directory,
                $"{nameWithoutExtension} ({Guid.NewGuid():N}){extension}");
            if (TryReserveBatchExportCandidate(candidate, out string? reservationKey))
                return (candidate, reservationKey!);
        }
    }

    private static bool TryReserveBatchExportCandidate(string candidate, out string? reservationKey)
    {
        string fullPath = Path.GetFullPath(candidate);
        reservationKey = null;
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            return false;
        if (!s_batchExportCandidates.TryAdd(fullPath, 0))
            return false;

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            s_batchExportCandidates.TryRemove(fullPath, out _);
            return false;
        }

        reservationKey = fullPath;
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  视频导出
    // ══════════════════════════════════════════════════════════════

    /// <summary>导出为视频 — 打开保存对话框，在文件类型中选择 MP4 或 MOV</summary>
    public async Task ExportVideoAsync(
        bool isLivePhoto,
        string? primaryPath,
        LivePhotoType livePhotoType,
        string? pairedVideoPath,
        IEditExportProgressNotifier progress,
        CancellationToken cancellationToken = default)
    {
        if (!isLivePhoto || string.IsNullOrEmpty(primaryPath))
        {
            progress.NotifyGuardError(ResourceService.GetString("EditPage_GuardNotLivePhoto"));
            return;
        }

        string? videoPath = await ProcessingPipelineRouter.RunRebuiltAsync(
            "edit.video-export",
            () => ResolveVideoPathForRebuiltExportAsync(primaryPath, livePhotoType, pairedVideoPath, cancellationToken));
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
        {
            progress.NotifyGuardError(ResourceService.GetString("EditPage_GuardNoVideoSource"));
            return;
        }

        var savePicker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(primaryPath),
        };
        savePicker.FileTypeChoices.Add("MP4 (H.264 + AAC)", new List<string> { ".mp4" });
        savePicker.FileTypeChoices.Add("MOV (H.265 QuickTime + AAC)", new List<string> { ".mov" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
        var targetFile = await savePicker.PickSaveFileAsync();
        if (targetFile == null)
        {
            CleanupExportTempVideo();
            return;
        }

        progress.NotifyBegin(ResourceService.GetString("EditPage_ExportVideoInProgress"));

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            var (success, errorMessage) = await ProcessingPipelineRouter.RunRebuiltAsync(
                "edit.video-export",
                () => ExportVideoWithNativeAsync(videoPath, targetFile.Path, linkedCts.Token));

            if (success)
            {
                progress.NotifyComplete(
                    ResourceService.GetString("EditPage_ExportVideoComplete"),
                    Path.GetDirectoryName(targetFile.Path));
            }
            else
            {
                progress.NotifyFailure(
                    ResourceService.GetString("EditPage_ExportVideoFailed"),
                    errorMessage ?? ResourceService.GetString("EditPage_UnknownError"),
                    Path.GetDirectoryName(targetFile.Path));
            }
        }
        catch (Exception ex)
        {
            progress.NotifyFailure(
                ResourceService.GetString("EditPage_ExportVideoFailed"),
                ex.Message, Path.GetDirectoryName(targetFile.Path));
        }
        finally
        {
            CleanupExportTempVideo();
            progress.NotifyFinalize();
        }
    }

    private static async Task<(bool Success, string? ErrorMessage)> ExportVideoWithNativeAsync(
        string inputPath, string outputPath, CancellationToken token)
    {
        bool isMp4 = Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        VideoContainer targetContainer = isMp4 ? VideoContainer.Mp4 : VideoContainer.Mov;
        VideoCodec targetCodec = isMp4 ? VideoCodec.H264 : VideoCodec.Hevc;

        using var conversionWorkspace = new MediaWorkspace();
        var converter = new VideoConverter();
        VideoFacts facts = await converter.ProbeAsync(inputPath, token).ConfigureAwait(false);
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = inputPath,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = facts.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                VideoContainer = facts.Container,
                VideoCodec = facts.Codec,
                ByteLength = new FileInfo(inputPath).Length
            },
            TargetContainer = targetContainer,
            TargetCodec = targetCodec,
            TargetDirectory = conversionWorkspace.RootDirectory,
            Crf = 23
        }, token).ConfigureAwait(false);

        if (!result.Success || result.OutputArtifact == null)
            return (false, result.ErrorMessage ?? "Native video conversion failed.");

        await WindowsOutputArtifactPublisher.PublishCopyAsync(result.OutputArtifact, outputPath, true, token).ConfigureAwait(false);
        return (true, null);
    }

    private static Task PublishExportCopyAsync(
        string sourcePath, string targetPath, MediaArtifactKind kind, bool overwrite, CancellationToken token)
    {
        return WindowsOutputArtifactPublisher.PublishCopyAsync(new MediaArtifact
        {
            Path = sourcePath,
            Kind = kind,
            ByteLength = new FileInfo(sourcePath).Length
        }, targetPath, overwrite, token);
    }

    // ══════════════════════════════════════════════════════════════
    //  GIF 导出
    // ══════════════════════════════════════════════════════════════

    /// <summary>导出 GIF — 当前 Rebuilt 架构下动图导出暂未支持，显示友好说明</summary>
    public Task ExportGifAsync(IEditExportProgressNotifier progress)
    {
        progress.NotifyGuardError(ResourceService.GetString("EditPage_RebuiltGifUnsupported"));
        return Task.CompletedTask;
    }

    // ══════════════════════════════════════════════════════════════
    //  底层辅助与生命周期方法
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 解析 ⭐ 静止封面帧的干净图片源。
    /// 单文件实况（图+视频拼在同一个容器里）的 photoPath 是整个容器：
    ///   - HEIC 容器 → Magick 解码到内嵌视频时抛 "Unexpected end of file"（导出 0 字节）
    ///   - JPEG 容器 → 直接复制会把视频/尾标一起带出来
    /// 这里把容器开头的图片部分切片成干净临时文件返回。
    /// 双文件实况（Apple/vivo ≤X200 图、视频分离）photoPath 本身就是干净图片，原样返回。
    /// </summary>
    private static async Task<WindowsOwnedTemporaryArtifact?> ResolveStillPhotoSourceAsync(
        string photoPath,
        CancellationToken token)
    {
        var facts = await new SourceInspector().InspectAsync(photoPath, null, token).ConfigureAwait(false);
        var video = facts.MotionVideo;
        if (facts.Protocol is SourceProtocol.NonLive or SourceProtocol.Unknown ||
            video is not { IsPresent: true } || video.SourceIndex != 0)
            return null;

        var primary = facts.PrimaryImage;
        if (!primary.IsPresent || primary.ByteOffset < 0 || primary.ByteLength <= 0 ||
            video.ByteOffset < 0 || video.ByteLength <= 0)
            throw new InvalidDataException("Native source inspection returned incomplete primary/video ranges.");
        if (video.Container is not (VideoContainer.Mp4 or VideoContainer.Mov))
            throw new InvalidDataException("Native source inspection returned an unsupported embedded video container.");

        long sourceLength = new FileInfo(photoPath).Length;
        if (primary.ByteOffset > sourceLength || primary.ByteLength > sourceLength - primary.ByteOffset ||
            video.ByteOffset > sourceLength || video.ByteLength > sourceLength - video.ByteOffset)
            throw new InvalidDataException("Native source inspection returned an out-of-bounds media range.");

        long primaryLength = primary.ByteLength;
        long primaryEnd = checked(primary.ByteOffset + primaryLength);
        long videoEnd = checked(video.ByteOffset + video.ByteLength);
        if (primary.ByteOffset < videoEnd && video.ByteOffset < primaryEnd)
            throw new InvalidDataException("Native primary and motion-video ranges overlap; refusing to derive a still boundary.");

        return await ExportSourcePreparation.PrepareStillRangeAsync(
            photoPath,
            primary.ByteOffset,
            primaryLength,
            token,
            facts.PrimarySha256).ConfigureAwait(false);
    }

    private static Exception CombineFinalizationFailures(
        Exception? currentFailure,
        Exception additionalFailure,
        Exception? originalFailure)
    {
        if (currentFailure != null)
        {
            return new AggregateException(
                "Export cleanup/finalization encountered multiple failures.",
                currentFailure,
                additionalFailure);
        }

        if (originalFailure != null && !ReferenceEquals(originalFailure, additionalFailure))
        {
            return new AggregateException(
                "The export and its finalization both failed.",
                originalFailure,
                additionalFailure);
        }

        return additionalFailure;
    }

    /// <summary>
    /// 将原图的 EXIF 信息（相机、日期、GPS 等）复制到导出文件，
    /// 但排除各家实况照片私有协议标签（GCamera、OpCamera、Container 等），
    /// 确保导出的是干净的静态图片。
    /// </summary>
    private static async Task CopyExifForExportAsync(string sourcePath, string targetPath, CancellationToken token = default)
    {
        try
        {
            await LivePhotoRepairService.RunExifToolAsync(token,
                "-TagsFromFile", sourcePath,
                "-all:all",
                "-Orientation=",
                "-ExifImageWidth=",
                "-ExifImageHeight=",
                "-ThumbnailImage=",
                "-overwrite_original",
                "-quiet",
                targetPath);

            await LivePhotoRepairService.RunExifToolAsync(token,
                "-xmp-GCamera:all=",
                "-xmp-OpCamera:all=",
                "-xmp-Container:all=",
                "-ContentIdentifier=",
                "-overwrite_original",
                "-quiet",
                targetPath);

            LogService.FileOp(
                $"CopyExifForExport: {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)}",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogService.FileOp(
                $"CopyExifForExport failed: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>
    /// Resolves the video for Rebuilt export through the Native-backed
    /// Inspect -> Extract -> Clean pipeline.
    /// </summary>
    private async Task<string?> ResolveVideoPathForRebuiltExportAsync(
        string primaryPath,
        LivePhotoType livePhotoType,
        string? pairedVideoPath,
        CancellationToken cancellationToken = default)
    {
        CleanupExportMediaWorkspace();

        var workspace = new MediaWorkspace();
        try
        {
            string? secondaryPath = livePhotoType == LivePhotoType.DualFile
                && !string.IsNullOrWhiteSpace(pairedVideoPath)
                ? pairedVideoPath
                : null;

            NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                primaryPath,
                secondaryPath,
                workspace,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))
            {
                workspace.Dispose();
                return null;
            }

            _exportMediaWorkspace = workspace;
            return bundle.MotionVideo.Path;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 弹出导出选项设置对话框：包含文件夹名编辑框、导出位置+浏览按钮、EXIF 勾选框。
    /// </summary>
    private async Task<ExportOptions?> ShowExportOptionsDialogAsync(
        string defaultFolderName, string currentFolderPath)
    {
        if (App.MainWindow?.Content?.XamlRoot is not XamlRoot xamlRoot)
            return null;

        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = ResourceService.GetString("EditPage_ExportDialog_Description"),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = ResourceService.GetString("EditPage_ExportDialog_FolderPathLabel"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
        });

        var pathRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };

        var folderPathBox = new TextBox
        {
            Text = currentFolderPath,
            Header = null,
        };
        Grid.SetColumn(folderPathBox, 0);
        pathRow.Children.Add(folderPathBox);

        var browseButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Content = new FontIcon { Glyph = "\uE8B7", FontSize = 14 },
        };
        ToolTipService.SetToolTip(browseButton,
            ResourceService.GetString("EditPage_ExportDialog_BrowseTip"));
        Grid.SetColumn(browseButton, 1);
        pathRow.Children.Add(browseButton);

        panel.Children.Add(pathRow);

        var pathErrorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 78, 78)),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 0),
        };
        panel.Children.Add(pathErrorText);

        panel.Children.Add(new TextBlock
        {
            Text = ResourceService.GetString("EditPage_ExportDialog_FolderNameLabel"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 0),
        });

        var nameRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };

        var folderNameBox = new TextBox
        {
            Text = defaultFolderName,
            PlaceholderText = defaultFolderName,
        };
        Grid.SetColumn(folderNameBox, 0);
        nameRow.Children.Add(folderNameBox);

        var resetNameButton = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Content = new FontIcon { Glyph = "\uE72C", FontSize = 14 },
        };
        ToolTipService.SetToolTip(resetNameButton,
            ResourceService.GetString("EditPage_ExportDialog_ResetTip"));
        Grid.SetColumn(resetNameButton, 1);
        nameRow.Children.Add(resetNameButton);

        panel.Children.Add(nameRow);

        panel.Children.Add(new TextBlock
        {
            Text = ResourceService.GetString("EditPage_ExportDialog_FormatLabel"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 0),
        });

        var formatComboBox = new ComboBox
        {
            Items =
            {
                new ComboBoxItem { Content = "JPEG (.jpg)", Tag = ".jpg" },
            },
            SelectedIndex = 0,
        };
        formatComboBox.Items.Add(new ComboBoxItem { Content = "HEIC (.heic)", Tag = ".heic" });
        panel.Children.Add(formatComboBox);

        var copyExifCheckBox = new CheckBox
        {
            Content = ResourceService.GetString("EditPage_ExportDialog_CopyExifLabel"),
            IsChecked = true,
        };
        panel.Children.Add(copyExifCheckBox);

        var dialog = new ContentDialog
        {
            Title = ResourceService.GetString("EditPage_ExportDialog_Title"),
            Content = panel,
            PrimaryButtonText = ResourceService.GetString("EditPage_ExportDialog_ExportBtn"),
            CloseButtonText = ResourceService.GetString("Msg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
            RequestedTheme = App.CurrentTheme,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 440.0;
        dialog.Resources["ContentDialogMinWidth"] = 440.0;

        var capturedDefaultName = defaultFolderName;
        resetNameButton.Click += (_, _) =>
        {
            folderNameBox.Text = capturedDefaultName;
        };

        static bool IsPathValid(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var invalid = Path.GetInvalidPathChars();
                if (path.IndexOfAny(invalid) >= 0) return false;
                if (!Path.IsPathRooted(path)) return false;
                var full = Path.GetFullPath(path);
                if (full.Length >= 2 && full[1] == ':')
                {
                    var drive = char.ToUpperInvariant(full[0]);
                    if (drive < 'A' || drive > 'Z') return false;
                    if (!Directory.Exists($@"{drive}:\")) return false;
                }
                return true;
            }
            catch { return false; }
        }

        var errorText = pathErrorText;
        void UpdatePathState()
        {
            currentFolderPath = folderPathBox.Text.Trim();
            if (IsPathValid(currentFolderPath))
            {
                errorText.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
            }
            else
            {
                errorText.Text = ResourceService.GetString("EditPage_ExportDialog_PathInvalidError");
                errorText.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
            }
        }

        folderPathBox.Loaded += (_, _) => UpdatePathState();
        folderPathBox.TextChanged += (_, _) => UpdatePathState();

        browseButton.Click += async (_, _) =>
        {
            try
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null)
                {
                    currentFolderPath = folder.Path;
                    folderPathBox.Text = currentFolderPath;
                    UpdatePathState();
                }
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Browse folder in dialog failed: {ex.Message}", LogLevel.Warning);
            }
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var testPath = folderPathBox.Text.Trim();
                if (!IsPathValid(testPath))
                {
                    errorText.Text = ResourceService.GetString("EditPage_ExportDialog_PathInvalidError");
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }
                currentFolderPath = testPath;
                errorText.Visibility = Visibility.Collapsed;
            }
            catch
            {
                errorText.Text = ResourceService.GetString("EditPage_ExportDialog_PathInvalidError");
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            string folderName = folderNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = defaultFolderName;
            bool copyExif = copyExifCheckBox.IsChecked ?? true;
            string fmtExt = ((ComboBoxItem)formatComboBox.SelectedItem).Tag as string ?? ".jpg";
            return new ExportOptions(folderName, copyExif, currentFolderPath, fmtExt, 80);
        }

        return null;
    }

    /// <summary>
    /// 在指定父目录下生成不冲突的文件夹路径。
    /// </summary>
    private static string GetUniqueFolderPath(string parentDir, string baseName)
    {
        var candidate = Path.Combine(parentDir, baseName);
        if (!Directory.Exists(candidate))
            return candidate;

        for (int i = 2; i < 999; i++)
        {
            candidate = Path.Combine(parentDir, $"{baseName} ({i})");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(parentDir, $"{baseName} ({Guid.NewGuid():N})");
    }

    /// <summary>取消当前正在执行的导出任务并清理临时资源</summary>
    public void CancelCurrentExport()
    {
        CancelActiveExport();
        CleanupExportTempVideo();
    }

    private void CancelActiveExport()
    {
        try
        {
            _activeExportCts?.Cancel();
            _activeExportCts?.Dispose();
        }
        catch { }
        finally
        {
            _activeExportCts = null;
        }
    }

    private void CleanupExportTempVideo()
    {
        if (_exportTempVideoPath != null)
        {
            try { if (File.Exists(_exportTempVideoPath)) File.Delete(_exportTempVideoPath); } catch { }
            _exportTempVideoPath = null;
        }
        CleanupExportMediaWorkspace();
    }

    private void CleanupExportMediaWorkspace()
    {
        if (_exportMediaWorkspace != null)
        {
            try { _exportMediaWorkspace.Dispose(); } catch { }
            _exportMediaWorkspace = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentExport();
    }

    private sealed record ExportOptions(string FolderName, bool CopyExif, string ExportPath,
        string FormatExtension = ".jpg", int Quality = 80);

    private sealed class ExportCounters
    {
        public int Completed;
        public int Success;
        public int Fail;
    }
}
