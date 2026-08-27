using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        // 用户指定的封面（key photo）在视频时间轴上的位置（微秒）。
        // null = 自动跟随源视频自带的时间轴（Apple MOV mebx / vivo uuid box）；
        // 0   = 封面就是静止图片本身（视频起始帧）。
        // GUI 编辑页后续“选帧设为封面”也走此选项，无需改协议字节格式。
        public long? KeyPhotoTimestampUs { get; init; }
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
                int successCount = 0;
                int failedCount = 0;
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
                            cancellationToken)
                            .ConfigureAwait(false);
                        int currentCompleted = Interlocked.Increment(ref completedCount);
                        if (result.IsSuccess) Interlocked.Increment(ref successCount);
                        else Interlocked.Increment(ref failedCount);
                        onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                    });

                    await Task.WhenAll(runningTasks).ConfigureAwait(false);
                }

                LogService.Merge($"Batch merge finished: {successCount} succeeded, {failedCount} failed.");
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
        // token: 取消令牌。
        // 返回: (是否成功, 结果描述)
        public static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            int taskIndex,
            LivePhotoMergeRunOptions options,
            string tempDir,
            CancellationToken token)
        {
            var protocol = LivePhotoProtocol.FromIndex(options.SelectedModeIndex);
            string workingImagePath = imagePath;
            string workingVideoPath = videoPath;
            var tempFiles = new List<string>();
            // 单对耗时：写进成功/失败日志，帮助定位"哪一步慢、哪个文件卡住"。
            var stopwatch = Stopwatch.StartNew();
            // 每个任务使用独立临时工作区（GUID 子目录），并发任务互不干扰；
            // 所有中间文件（HEIC 转换 / 视频转码 / 协议预处理）都在工作区内分配，
            // 方法结束时随工作区整体清理。
            using var workspace = TempFileService.CreateWorkspace("merge_task", tempDir);
            string taskTempDir = workspace.RootPath;
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
                    // 输出容器严格以用户请求为准：请求 jpg+*（格式 0/1）时，任何 HEIC 源
                    // 都必须转成 JPEG，包括 V2/vivo/Samsung 等 V2 子类协议；V2 的 HEIC
                    // 原生路径仅用于用户明确选择 heic+* 时。
                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, taskTempDir, token);
                    tempFiles.Add(workingImagePath);
                    token.ThrowIfCancellationRequested();
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
                        workingImagePath, taskTempDir, token);
                    tempFiles.Add(workingImagePath);
                }

                // Read cover frame timestamp before ffmpeg transcode —
                // the Apple mebx track (StillImageTime) and vivo uuid box are
                // discarded by ffmpeg's -map 0:V:0 selector.
                long coverTimestampUs = options.KeyPhotoTimestampUs
                    ?? await LivePhotoMergeService.ReadSourceCoverTimestampAsync(videoPath, token);

                // ── 源协议标记清洗（Fusion 除外）──────────────────────────────
                // 双文件源 → 单文件前，剥离源协议（苹果/各品牌）的实况照片标记，
                // 保证目标单文件里只含目标协议自己的标记。只在临时副本上操作。
                if (protocol is not MotionPhotoFusionProtocol)
                {
                    workingImagePath = await SourceProtocolCleaner.CleanImageAsync(workingImagePath, taskTempDir, token);
                    if (!string.Equals(workingImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
                        tempFiles.Add(workingImagePath);

                    workingVideoPath = await SourceProtocolCleaner.CleanVideoAsync(videoPath, taskTempDir, token);
                    if (!string.Equals(workingVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
                        tempFiles.Add(workingVideoPath);
                }

                bool forceMp4 = ComputeForceMp4(options.SelectedModeIndex, options.OutputFormatIndex);
                // Huawei V6 (6): use brand mp42 + ©too via hwFaststart=false
                bool hwFaststart = options.SelectedModeIndex != 6;
                // HUAWEI HEIC+H.265 format: force HEVC (native camera codec)
                string videoCodec = options.OutputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265
                    ? "hevc" : "h264";
                (workingVideoPath, bool vt) = await VideoTranscodeService.EnsureMp4Async(
                    workingVideoPath, taskTempDir, token, forceMp4, hwFaststart, videoCodec);
                if (vt) tempFiles.Add(workingVideoPath);

                // ── MOV 输出清洗（P1-5）：剔除源 Apple 的 mebx/ContentDescribes 时序轨 ──
                // EnsureMp4Async 在 forceMp4=false 时对 MOV 源直接跳过转码，导致源 Apple MOV
                // （含 mebx 实况轨 + 多条 HEVC 流 + PCM）被原样嵌入单文件。这里用 ffmpeg
                // 无损重封装为仅含主视频轨 + 音频轨的干净 MOV（-map 0:V:0 -map 0:a:0?），
                // mebx/ContentDescribes/缩略图轨被丢弃，Apple mdta 实况键也不带入。
                // 注意：封面时间戳必须在转码前读取（上面 coverTimestampUs 已读），
                // 因为 mebx 轨的 StillImageTime 会被 -map 0:V:0 丢弃。
                bool wantMov = options.OutputFormatIndex is 1 or 3;
                if (wantMov)
                {
                    string cleanMov = TempFileService.AllocateTempPath(taskTempDir, "merge_mov_clean", "mov");
                    var remuxResult = await VideoTranscodeService.RemuxAsync(
                        workingVideoPath, cleanMov, token, useFaststart: false);
                    if (remuxResult.Success)
                    {
                        workingVideoPath = cleanMov;
                        tempFiles.Add(workingVideoPath);
                    }
                    else
                    {
                        LogService.Merge(
                            $"MOV cleanup remux failed, using original video: {remuxResult.ErrorMessage}",
                            LogLevel.Warning);
                    }
                }

                string prepared = await protocol.PrepareImageAsync(workingImagePath, taskTempDir, token);
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

                // ── 源 Apple MakerNote 实况条目剥离（P1-1，合成端）─────────────────
                // 所有图片输出（JPEG/HEIC）最后统一字节级剥离 Apple 实况 MakerNote 条目
                // （0x0011 ContentIdentifier / 0x0017 LivePhotoVideoIndex /
                //   0x0025 / 0x002b PhotoIdentifier）。
                // exiftool 只能清空 CID 值、删不掉 0x0017/0x0025 这类 type=16 条目，
                // 必须字节级处理（AppleMakerNoteWriter.TryStripAppleLivePhotoEntries，
                // 保持 MN 长度不变，不破坏 EXIF/HEIC 结构）。
                // 放在 UserComment 回写之后执行，保证最终产物是干净状态。
                if (!Protocols.AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(
                        finalOutputPath, out string? mnStripError))
                {
                    LogService.Merge(
                        $"Apple MakerNote strip failed (non-fatal): {mnStripError}",
                        LogLevel.Warning);
                }

                LogService.Merge($"Merge completed for {baseName}: {finalOutputPath} ({stopwatch.Elapsed.TotalSeconds:F2}s)");
                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Merge($"Merge task failed for {baseName}: {ex.Message} ({stopwatch.Elapsed.TotalSeconds:F2}s)", LogLevel.Error, ex);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                foreach (var f in tempFiles)
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        // Determine whether to force MP4 conversion.
        // Selected MP4 output (indices 0/2/FormatHeicMp4H265) → always convert.
        // Selected MOV output → keep MOV (output format selection is the single source of truth).
        // OPPO (3) / VIVO (4) / Samsung (5) → always force MP4 regardless of output format.
        private static bool ComputeForceMp4(int selectedModeIndex, int outputFormatIndex)
        {
            // OPPO / VIVO / Samsung always need MP4
            if (selectedModeIndex == 4 || selectedModeIndex == 3 || selectedModeIndex == 5) return true;
            // User selected MP4 output → always convert to MP4
            bool wantMp4 = outputFormatIndex == 0 || outputFormatIndex == 2
                || outputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265;
            return wantMp4;
        }
    }
}
