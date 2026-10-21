using LivePhotoBox.Models;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services.Protocols;
using NeutralMediaBundle = LivePhotoBox.Protocols.Cleaning.NeutralMediaBundle;
using System;
using System.Collections.Concurrent;
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
    // 支持暂停/取消/进度回调；每个任务通过 MediaWorkspace 管理自己的临时文件。
    public static class LivePhotoMergeRunnerService
    {
        private static readonly ConcurrentDictionary<string, byte> s_outputPathCandidates =
            new(StringComparer.OrdinalIgnoreCase);

        // 批量运行合并任务。
        // 按 <see cref="LivePhotoMergeRunOptions.MaxDegreeOfParallelism"/> 分块并行处理，
        // 每个任务内部自动处理 HEIC 转换、视频转码、协议预处理与最终写入。
        // tasks: 待处理的任务集合。
        // options: 运行配置。
        // pauseEvent: 暂停信号量。
        // cancellationToken: 取消令牌。
        // onTaskStarted: 任务开始回调。
        // onTaskCompleted: 任务完成回调（参数：task, success, details, completedCount）。
        public static Task RunAsync(
            IReadOnlyCollection<IMergeTaskInfo> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<IMergeTaskInfo>? onTaskStarted,
            Action<IMergeTaskInfo, bool, string, int>? onTaskCompleted)
        {
            return ProcessingPipelineRouter.RunRebuiltAsync("merge", () => RunRebuiltAsync(
                tasks, options, pauseEvent, cancellationToken, onTaskStarted, onTaskCompleted));
        }

        private static async Task RunRebuiltAsync(
            IReadOnlyCollection<IMergeTaskInfo> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<IMergeTaskInfo>? onTaskStarted,
            Action<IMergeTaskInfo, bool, string, int>? onTaskCompleted)
        {
            Directory.CreateDirectory(options.OutputDirectory);
            int completedCount = 0;
            int batchSize = Math.Max(1, options.MaxDegreeOfParallelism);
            foreach (var batch in tasks.Where(task => task.Status != ProcessStatus.Success).Chunk(batchSize))
            {
                await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var runningTasks = batch.Select(async task =>
                {
                    await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    onTaskStarted?.Invoke(task);

                    var result = await ProcessSinglePairRebuiltAsync(
                        task.ImagePath, task.VideoPath, task.BaseName, task.Index, options,
                        cancellationToken).ConfigureAwait(false);
                    int currentCompleted = Interlocked.Increment(ref completedCount);
                    onTaskCompleted?.Invoke(task, result.IsSuccess, result.Details, currentCompleted);
                });

                await Task.WhenAll(runningTasks).ConfigureAwait(false);
            }
        }

        private static async Task<(bool IsSuccess, string Details)> ProcessSinglePairRebuiltAsync(
            string imagePath,
            string videoPath,
            string baseName,
            int taskIndex,
            LivePhotoMergeRunOptions options,
            CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            // An explicit path is known before preprocessing starts. Keep it
            // for diagnostics only; a failed merge must never delete it by path.
            string? finalOutputPath = options.OutputFilePath;
            string? candidateReservationKey = null;
            try
            {
                token.ThrowIfCancellationRequested();
                MediaFormatRequirement requirement = ProtocolMediaRequirements.GetMergeRequirement(
                    options.SelectedModeIndex, options.OutputFormatIndex);

                using var workspace = new MediaWorkspace();
                NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                    imagePath,
                    videoPath,
                    workspace,
                    requirement,
                    PreservationPolicy.BestEffort,
                    token).ConfigureAwait(false);

                if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))
                    throw new InvalidDataException("Rebuilt merge requires a Native-inspected motion video pair.");

                long presentationTimestampUs = ResolveNeutralCoverTimestamp(
                    bundle.Semantics.Timing,
                    options.KeyPhotoTimestampUs);

                string outputName = LivePhotoMergeService.CreateOutputFileName(
                    baseName,
                    options.SelectedModeIndex,
                    bundle.PrimaryImage.Path,
                    options.OutputFormatIndex,
                    options.NamingRuleIndex,
                    customPattern: options.NamingRuleIndex == 2 ? options.CustomNamingPattern : null,
                    taskIndex: options.NamingRuleIndex == 2 ? taskIndex : null);

                if (options.OutputFilePath != null)
                {
                    finalOutputPath = options.OutputFilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(finalOutputPath)!);
                }
                else
                {
                    string outputDirectory = options.OutputDirectory;
                    if (options.PreserveSubfolders && !string.IsNullOrEmpty(options.InputDirectory))
                    {
                        string? subDir = PathHelper.GetRelativeSubDirectory(options.InputDirectory, imagePath);
                        if (!string.IsNullOrEmpty(subDir))
                            outputDirectory = Path.Combine(outputDirectory, subDir);
                    }

                    Directory.CreateDirectory(outputDirectory);
                    finalOutputPath = Path.Combine(outputDirectory, outputName);
                    if (!options.OverwriteExisting)
                    {
                        (finalOutputPath, candidateReservationKey) = AllocateUniqueOutputPathCandidate(
                            outputDirectory,
                            outputName);
                    }
                }

                await LivePhotoMergeService.WriteLivePhotoRebuiltAsync(
                    bundle.PrimaryImage.Path,
                    bundle.MotionVideo.Path,
                    finalOutputPath,
                    options.SelectedModeIndex,
                    token,
                    presentationTimestampUs,
                    options.OutputFormatIndex,
                    overwriteExisting: options.OutputFilePath != null || options.OverwriteExisting,
                    testHook: null).ConfigureAwait(false);

                stopwatch.Stop();
                LogService.Merge($"Rebuilt merge completed for {baseName}: {finalOutputPath} ({stopwatch.Elapsed.TotalSeconds:F2}s)");
                return (true, ResourceService.GetString("Task_Success"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogService.Merge($"Rebuilt merge failed for {baseName}: {ex.Message} ({stopwatch.Elapsed.TotalSeconds:F2}s)", LogLevel.Error, ex);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                if (candidateReservationKey != null)
                    s_outputPathCandidates.TryRemove(candidateReservationKey, out _);
            }
        }

        internal static long ResolveNeutralCoverTimestamp(
            NeutralTimingSemantics timing,
            long? requestedTimestampUs)
        {
            ArgumentNullException.ThrowIfNull(timing);

            if (requestedTimestampUs is long requested)
            {
                if (!IsLegalCoverTimestamp(requested, timing.DurationUs))
                {
                    throw new InvalidDataException(
                        "The requested key-photo timestamp must be within the known final video duration.");
                }

                return requested;
            }

            switch (timing.EvidenceState)
            {
                case NeutralEvidenceState.Verified:
                case NeutralEvidenceState.VerifiedPreserved:
                case NeutralEvidenceState.VerifiedConverted:
                    if (timing.CoverTimestampUs is not long verifiedTimestamp ||
                        !IsLegalCoverTimestamp(verifiedTimestamp, timing.DurationUs))
                    {
                        throw new InvalidDataException(
                            "Verified neutral cover timing must be within the known final video duration.");
                    }

                    return verifiedTimestamp;

                case NeutralEvidenceState.NotApplicable when timing.CoverTimestampUs is null && timing.DurationUs is > 0:
                    // No source cover applies; retain the synthetic opening-frame default.
                    return 0;

                case NeutralEvidenceState.NotApplicable:
                    throw new InvalidDataException(
                        "NotApplicable neutral timing cannot include a source cover timestamp.");

                default:
                    throw new InvalidDataException(
                        "Neutral cover timing is unknown; an explicit in-range key-photo timestamp is required.");
            }
        }

        private static bool IsLegalCoverTimestamp(long timestampUs, long? durationUs) =>
            timestampUs >= 0 && durationUs is long duration && duration > 0 && timestampUs < duration;

        // Choose a free-looking name without creating a placeholder file. The
        // publication transaction owns the final no-replace operation; if an
        // external writer wins after this candidate check, publication fails
        // closed and preserves that writer's object.
        private static (string CandidatePath, string ReservationKey) AllocateUniqueOutputPathCandidate(
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
                if (TryReserveCandidate(candidate, out string? reservationKey))
                    return (candidate, reservationKey!);
            }

            while (true)
            {
                string candidate = Path.Combine(
                    directory,
                    $"{nameWithoutExtension} ({Guid.NewGuid():N}){extension}");
                if (TryReserveCandidate(candidate, out string? reservationKey))
                    return (candidate, reservationKey!);
            }
        }

        private static bool TryReserveCandidate(string candidate, out string? reservationKey)
        {
            string fullPath = Path.GetFullPath(candidate);
            reservationKey = null;
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                return false;
            if (!s_outputPathCandidates.TryAdd(fullPath, 0))
                return false;

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                s_outputPathCandidates.TryRemove(fullPath, out _);
                return false;
            }

            reservationKey = fullPath;
            return true;
        }

        // Async pause-wait that does NOT block a thread-pool thread.
        // The paused worker is represented as an uncompleted Task rather than
        // a parked OS thread, so cancellation and Set() both propagate cleanly.
        private static async Task WaitPauseAsync(ManualResetEventSlim pauseEvent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (pauseEvent.IsSet) return;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Registered waits observe the signal without parking a worker.
            // Cancellation must retire the actual wait, not merely its proxy task.
            RegisteredWaitHandle wait = ThreadPool.RegisterWaitForSingleObject(
                pauseEvent.WaitHandle,
                static (state, _) => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                tcs, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                using var reg = token.Register(() => tcs.TrySetCanceled(token));
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                wait.Unregister(null);
            }
        }

        // 单个文件对合并处理。
        // imagePath: 源图片路径。
        // videoPath: 源视频路径。
        // baseName: 输出文件名基础部分。
        // options: 运行配置。
        // tempDir: 保留以兼容既有调用方；重建流程使用每次调用独占的 MediaWorkspace。
        // token: 取消令牌。
        // 返回: (是否成功, 结果描述)
        public static Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            int taskIndex,
            LivePhotoMergeRunOptions options,
            string tempDir,
            CancellationToken token)
        {
            _ = tempDir;
            return ProcessingPipelineRouter.RunRebuiltAsync("merge", () => ProcessSinglePairRebuiltAsync(
                imagePath, videoPath, baseName, taskIndex, options, token));
        }
    }
}
