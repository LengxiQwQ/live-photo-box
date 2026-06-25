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
        // 最大并行任务数。
        public int MaxDegreeOfParallelism { get; init; } = Math.Min(Environment.ProcessorCount, 5);
        // 每批任务的启动间隔。
        public TimeSpan TaskStartInterval { get; init; } = TimeSpan.FromMilliseconds(250);
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
            IReadOnlyCollection<MergeTask> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<MergeTask>? onTaskStarted,
            Action<MergeTask, bool, string, int>? onTaskCompleted)
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
                            task.ImagePath, task.VideoPath, task.BaseName, options, tempDir,
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
        private static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
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

                if (HeicConverterService.IsHeicFile(imagePath))
                {
                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(imagePath, tempDir, token);
                    tempFiles.Add(workingImagePath);
                    await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }

                bool forceMp4 = ComputeForceMp4(options.SelectedModeIndex);
                (workingVideoPath, bool vt) = await VideoTranscodeService.EnsureMp4Async(videoPath, tempDir, token, forceMp4);
                if (vt) tempFiles.Add(workingVideoPath);

                string prepared = await protocol.PrepareImageAsync(workingImagePath, tempDir, token);
                if (prepared != workingImagePath)
                {
                    workingImagePath = prepared;
                    tempFiles.Add(workingImagePath);
                }

                string outputName = LivePhotoMergeService.CreateOutputFileName(baseName, options.SelectedModeIndex);
                string finalOutputPath = PathHelper.GetUniqueFilePath(options.OutputDirectory, outputName);

                await WaitPauseAsync(pauseEvent, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await LivePhotoMergeService.WriteLivePhotoAsync(workingImagePath, workingVideoPath, finalOutputPath, options.SelectedModeIndex, token);

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
        // Google protocols (V1/V2) respect the user setting; OPPO always forces MP4.
        private static bool ComputeForceMp4(int selectedModeIndex)
        {
            // OPPO protocol (Id=2) always needs MP4
            if (selectedModeIndex == 2) return true;
            // Google V1 (Id=0) / V2 (Id=1) respect user's toggle (default false / off)
            return AppSettingsService.GetValue("IsGoogleProtocolForceMp4", false);
        }
    }
}
