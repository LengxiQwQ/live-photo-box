using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    // 合并任务的运行配置项。
    public sealed class LivePhotoMergeRunOptions
    {
        // 输出目录路径。
        public required string OutputDirectory { get; init; }
        // 选中的合成协议索引。
        public required int SelectedModeIndex { get; init; }
        // 输出格式索引（0=JPG+MP4, 1=JPG+MOV, 2=HEIC+MP4, 3=HEIC+MOV）。
        public int OutputFormatIndex { get; init; } = 0;
        // 命名规则索引（0=保留原名, 1=添加协议后缀, 2=自定义模板）。
        public int NamingRuleIndex { get; init; } = 0;
        // 自定义命名模板字符串（NamingRuleIndex==2 时使用）。
        public string? CustomNamingPattern { get; init; }
        // 最大并行任务数。
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 5);
        // 每批任务的启动间隔。
        public TimeSpan TaskStartInterval { get; init; } = TimeSpan.FromMilliseconds(250);
        // 是否覆盖已存在的输出文件（GUI 的 OverwriteExisting 选项）。
        public bool OverwriteExisting { get; init; } = false;
        // 是否在输出目录下保留源文件的相对子目录结构。
        public bool PreserveSubfolders { get; init; } = false;
        // 输入目录（PreserveSubfolders 为 true 时用于计算相对路径）。
        public string? InputDirectory { get; init; }
        // 预定的输出文件路径。设置后跳过内部路径生成和 OverwriteExisting 逻辑。
        // GUI 用此选项传入自己计算的路径（含子目录保留/覆盖处理）。
        public string? OutputFilePath { get; init; }
    }

    // 实况照片合并运行器。
    // 将一组 MergeTask 分批并行执行合并操作，
    // 支持暂停/取消/进度回调，以及临时文件自动清理。
    public static class LivePhotoMergeRunnerService
    {
        // 批量运行合并任务。
        // 按 <see cref="LivePhotoMergeRunOptions.MaxDegreeOfParallelism"/> 分块并行处理，
        // 每个任务内部自动处理 HEIC 转换、视频转码、协议预处理与最终写入。
        // tasks: 待处理的任务集合。
        // options: 运行配置。
        // pauseEvent: 暂停信号量。
        // cancellationToken: 取消令牌。
        // onTaskStarted: 任务开始回调。
        // onTaskCompleted: 任务完成回调（参数：task, success, details, completedCount）。
        public static async Task RunAsync(
            IReadOnlyCollection<IMergeTaskInfo> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<IMergeTaskInfo>? onTaskStarted,
            Action<IMergeTaskInfo, bool, string, int>? onTaskCompleted)
        {
            Directory.CreateDirectory(options.OutputDirectory);
            string tempDir = Path.Combine(options.OutputDirectory, "Temp");
            Directory.CreateDirectory(tempDir);

            try
            {
                int completedCount = 0;
                DateTimeOffset nextAllowedBatchStartTime = DateTimeOffset.MinValue;
                int batchSize = Math.Max(1, options.MaxDegreeOfParallelism);

                foreach (var batch in tasks.Where(task => task.Status != ProcessStatus.Success).Chunk(batchSize))
                {
                    await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    var now = DateTimeOffset.UtcNow;
                    var delay = nextAllowedBatchStartTime - now;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }

                    nextAllowedBatchStartTime = DateTimeOffset.UtcNow + options.TaskStartInterval;

                    var runningTasks = batch.Select(async task =>
                    {
                        await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        onTaskStarted?.Invoke(task);

                        var result = await ProcessSinglePairAsync(
                            task.ImagePath, task.VideoPath, task.BaseName, task.Index, options, tempDir,
                            pauseEvent, cancellationToken)
                            .ConfigureAwait(false);
                        int currentCompleted = Interlocked.Increment(ref completedCount);
                        onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                    });

                    await Task.WhenAll(runningTasks).ConfigureAwait(false);
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { LogService.Merge($"Failed to clean temp dir: {ex.Message}", LogLevel.Warning); }
            }
        }

        // Async pause-wait that does NOT block a thread-pool thread.
        // The paused worker is represented as an uncompleted Task rather than
        // a parked OS thread, so cancellation and Set() both propagate cleanly.
        private static async Task WaitPauseAsync(ManualResetEventSlim pauseEvent, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reg = token.Register(() => tcs.TrySetCanceled(token));
            try
            {
                var waitTask = Task.Run(() => { pauseEvent.Wait(); tcs.TrySetResult(true); }, token);
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                reg.Dispose();
            }
        }

        // 处理单对图片+视频的合并操作。
        // 按序执行：HEIC 转换 → MP4 保证 → 协议预处理 → 写入目标。
        // 任何步骤失败会用 try-catch 捕获并返回错误详情（不会中断整个批次）。
        // imagePath: 源图片路径。
        // videoPath: 源视频路径。
        // baseName: 输出文件名基础部分。
        // options: 运行配置。
        // tempDir: 临时文件目录。
        // pauseEvent: 暂停信号量。
        // token: 取消令牌。
        // è¿å: (是否成功, 结果描述)
        public static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            int taskIndex,
            LivePhotoMergeRunOptions options,
            string tempDir,
            ManualResetEventSlim pauseEvent,
            CancellationToken token)
        {
            var protocol = LivePhotoProtocol.FromIndex(options.SelectedModeIndex);
            string workingImagePath = imagePath;
            string workingVideoPath = videoPath;
            var tempFiles = new List<string>();
            try
            {
                token.ThrowIfCancellationRequested();

                // HEIC → JPEG conversion: skip when using Motion Photo V2 protocol
                // (V2 supports native HEIC primary images per Google spec).
                // Also skip when the user selected HEIC output format (indices 2/3).
                bool keepHeic = (options.OutputFormatIndex == 2 || options.OutputFormatIndex == 3
                    || options.OutputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265)
                    && HeicConverterService.IsHeicFile(imagePath);
                if (!keepHeic && HeicConverterService.IsHeicFile(imagePath))
                {
                    if (protocol is MotionPhotoV2Protocol)
                    {
                        // Native HEIC path — no JPEG conversion needed.
                        // The HEIC will be written directly with XMP injected via exiftool
                        // into the ISOBMFF meta box, followed by an mpvd box with the video.
                    }
                    else
                    {
                        workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, tempDir, token);
                        tempFiles.Add(workingImagePath);
                        await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                    }
                }

                // JPG/PNG → HEIC conversion: when user selects HEIC output format
                // (indices 2/3) but the source image is not HEIC, convert it so the
                // output container matches the user's format selection.
                // This is the inverse of the HEIC→JPEG block above.
                if ((options.OutputFormatIndex == 2 || options.OutputFormatIndex == 3
                    || options.OutputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265)
                    && !HeicConverterService.IsHeicFile(workingImagePath))
                {
                    workingImagePath = await HeicConverterService.ConvertToHeicAsync(
                        workingImagePath, tempDir, token);
                    tempFiles.Add(workingImagePath);
                }

                // Read cover frame timestamp before ffmpeg transcode —
                // the Apple mebx track (StillImageTime) and vivo uuid box are
                // discarded by ffmpeg's -map 0:V:0 selector.
                long coverTimestampUs = LivePhotoMergeService.ReadSourceCoverTimestamp(videoPath);

                bool forceMp4 = ComputeForceMp4(options.SelectedModeIndex, options.OutputFormatIndex);
                // Huawei V6 (6): use brand mp42 + ©too via hwFaststart=false
                bool hwFaststart = options.SelectedModeIndex != 6;
                // H.265 format: use HEVC codec (native Huawei camera format)
                string videoCodec = options.OutputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265
                    ? "hevc" : "h264";
                (workingVideoPath, bool vt) = await VideoTranscodeService.EnsureMp4Async(
                    videoPath, tempDir, token, forceMp4, hwFaststart, videoCodec);
                if (vt) tempFiles.Add(workingVideoPath);

                string prepared = await protocol.PrepareImageAsync(workingImagePath, tempDir, token);
                if (prepared != workingImagePath)
                {
                    workingImagePath = prepared;
                    tempFiles.Add(workingImagePath);
                }

                string outputName = LivePhotoMergeService.CreateOutputFileName(
                    baseName, options.SelectedModeIndex, workingImagePath,
                    options.OutputFormatIndex, options.NamingRuleIndex,
                    customPattern: options.NamingRuleIndex == 2 ? options.CustomNamingPattern : null,
                    taskIndex: options.NamingRuleIndex == 2 ? taskIndex : null);

                // Output path: use caller-provided override, or generate from options
                string finalOutputPath;
                if (options.OutputFilePath != null)
                {
                    finalOutputPath = options.OutputFilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(finalOutputPath)!);
                }
                else
                {
                    string targetDir = options.OutputDirectory;
                    if (options.PreserveSubfolders && !string.IsNullOrEmpty(options.InputDirectory))
                    {
                        string? subDir = PathHelper.GetRelativeSubDirectory(options.InputDirectory, imagePath);
                        if (!string.IsNullOrEmpty(subDir))
                            targetDir = Path.Combine(targetDir, subDir);
                    }

                    if (options.OverwriteExisting)
                    {
                        Directory.CreateDirectory(targetDir);
                        finalOutputPath = Path.Combine(targetDir, outputName);
                        try { if (File.Exists(finalOutputPath)) File.Delete(finalOutputPath); } catch { }
                    }
                    else
                    {
                        Directory.CreateDirectory(targetDir);
                        finalOutputPath = PathHelper.GetUniqueFilePath(targetDir, outputName);
                    }
                }

                await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await LivePhotoMergeService.WriteLivePhotoAsync(workingImagePath, workingVideoPath, finalOutputPath, options.SelectedModeIndex, token, coverTimestampUs, options.OutputFormatIndex);

                // WriteNativeAsync may lose EXIF UserComment tags injected by
                // PrepareImageAsync (e.g. OPPO/Fusion "oplus_10485792").
                // Re-inject the marker directly on the final output as a safeguard.
                string? exifMarker = protocol.GetExifUserCommentMarker();
                if (exifMarker != null)
                {
                    await LivePhotoProtocol.WriteExifUserCommentAsync(
                        finalOutputPath, exifMarker, token);
                }

                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Merge($"Merge task failed for {baseName}: {ex.Message}", LogLevel.Error, ex);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                foreach (var f in tempFiles)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        // Determine whether to force MP4 conversion.
        // Selected MP4 output (indices 0/2) → always convert.
        // Selected MOV output (indices 1/3) → keep MOV unless Samsung/vivo/toggle override.
        // Samsung (4) / vivo (3) always force MP4 regardless of output format.
        private static bool ComputeForceMp4(int selectedModeIndex, int outputFormatIndex)
        {
            // Samsung / vivo always need MP4
            if (selectedModeIndex == 4 || selectedModeIndex == 3 || selectedModeIndex == 5) return true;
            // User selected MP4 output → always convert to MP4
            bool wantMp4 = outputFormatIndex == 0 || outputFormatIndex == 2
                || outputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265;
            if (wantMp4) return true;
            // User selected MOV output → respect the force-MP4 toggle
            return AppSettingsService.GetValue("IsGoogleProtocolForceMp4", false);
        }
    }
}
