using LivePhotoBox.Models;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services.Protocols;
using LivePhotoBox.Interop;
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
    internal sealed record StructuredMergeOptions
    {
        internal PreservationPolicy PreservationPolicy { get; init; } = PreservationPolicy.BestEffort;
        internal bool KeepVideoStabilizationEdges { get; init; }
        internal HuaweiTargetVariant HuaweiVariant { get; init; } = HuaweiTargetVariant.HuaweiV6;
        internal SamsungTargetVariant SamsungVariant { get; init; } = SamsungTargetVariant.Mpv3;
        internal UnspecifiedImageOrientationHandling UnspecifiedImageOrientationHandling { get; init; }
    }

    // 合并任务的运行配置项。
    public sealed class LivePhotoMergeRunOptions
    {
        internal StructuredMergeOptions StructuredOptions { get; init; } = new();
        // 输出目录路径。
        public required string OutputDirectory { get; init; }
        public required OutputProfile OutputProfile { get; init; }
        public bool KeepVideoStabilizationEdges { get; init; }
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
        // 预定的输出文件路径，仅跳过内部命名；覆盖仍须由 OverwriteExisting 授权。
        public string? OutputFilePath { get; init; }
        // 用户指定的封面（key photo）在视频时间轴上的位置（微秒）。
        // null = 自动跟随源视频自带的时间轴（Apple MOV mebx / vivo uuid box）；
        // 0   = 封面就是静止图片本身（视频起始帧）。
        // GUI 编辑页后续“选帧设为封面”也走此选项，无需改协议字节格式。
        public long? KeyPhotoTimestampUs { get; init; }
        /// <summary>Explicit choice for this input only; batch invocation rejects it.</summary>
        public UnspecifiedImageOrientationHandling UnspecifiedImageOrientationHandling { get; init; }
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
        public static async Task RunAsync(
            IReadOnlyCollection<IMergeTaskInfo> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<IMergeTaskInfo>? onTaskStarted,
            Action<IMergeTaskInfo, bool, string, int>? onTaskCompleted)
        {
            await RunWithOutcomesAsync(tasks, options, pauseEvent, cancellationToken, onTaskStarted,
                (task, outcome, count) =>
                {
                    if (outcome.Status != ProductOperationStatus.Cancelled)
                        onTaskCompleted?.Invoke(task, outcome.IsSuccess, outcome.Details, count);
                }).ConfigureAwait(false);
            // Compatibility API only. GUI/CLI consume normal cancellation
            // results from RunWithOutcomesAsync instead.
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal static Task RunWithOutcomesAsync(
            IReadOnlyCollection<IMergeTaskInfo> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<IMergeTaskInfo>? onTaskStarted,
            Action<IMergeTaskInfo, ProductOperationOutcome, int>? onTaskCompleted)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(options.OutputProfile);
            if(options.StructuredOptions.PreservationPolicy == PreservationPolicy.Strict &&
               options.UnspecifiedImageOrientationHandling != UnspecifiedImageOrientationHandling.Reject)
                throw new ArgumentException("Unspecified image direction must be selected for one input, not applied to a batch.",nameof(options));
            if (options.TaskStartInterval < TimeSpan.Zero ||
                options.TaskStartInterval.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(options.TaskStartInterval),
                    "Batch start interval must be non-negative and within the supported delay range.");
            if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            return ProcessingPipelineRouter.RunRebuiltAsync("merge", () => RunRebuiltAsync(
                tasks, options, pauseEvent, cancellationToken, onTaskStarted, onTaskCompleted));
        }

        private static async Task RunRebuiltAsync(
            IReadOnlyCollection<IMergeTaskInfo> tasks,
            LivePhotoMergeRunOptions options,
            ManualResetEventSlim pauseEvent,
            CancellationToken cancellationToken,
            Action<IMergeTaskInfo>? onTaskStarted,
            Action<IMergeTaskInfo, ProductOperationOutcome, int>? onTaskCompleted)
        {
            if (cancellationToken.IsCancellationRequested) return;
            using var inputProtection = new SourceInputProtection(tasks.SelectMany(t => new[] { t.ImagePath, t.VideoPath }));
            int initiallyFailed = 0;
            bool Blocked(IMergeTaskInfo task) => inputProtection.FailedPaths.Contains(Path.GetFullPath(task.ImagePath)) ||
                (!string.IsNullOrEmpty(task.VideoPath) && inputProtection.FailedPaths.Contains(Path.GetFullPath(task.VideoPath)));
            if (!inputProtection.IsComplete)
            {
                foreach (var task in tasks.Where(t => t.Status is not (ProcessStatus.Success or ProcessStatus.Warning) && Blocked(t)))
                {
                    var outcome = ProductOutcomeCompletion.Record(new ProductOperationOutcome {
                        OperationId=Guid.NewGuid(),StartedUtc=DateTimeOffset.UtcNow,CompletedUtc=DateTimeOffset.UtcNow,
                        Operation=ProductOperationKind.Merge,Status=ProductOperationStatus.Failed,
                        CompletionKind=ProductCompletionKind.NoOutput,Inputs=new[]{task.ImagePath,task.VideoPath},
                        RequestedDestination=options.OutputDirectory,
                        Diagnostic=new(){Stage=ProductOperationStage.Inspection,Category=ProductFailureCategory.Io,
                            Message=inputProtection.Diagnostic ?? "来源保护未完成，未生成输出。"}});
                    onTaskCompleted?.Invoke(task,outcome,++initiallyFailed);
                }
            }
            var confirmed = new HashSet<(string Image, string Video)>();
            foreach (var scope in tasks.Where(t => t.SelectionIntent is SelectionIntent.AppleIdentity or SelectionIntent.VivoIdentity)
                .Select(t => t.SelectionScope).Where(scope => scope != null).Distinct())
            {
                var snapshot = await LivePhotoMergeScanService.ScanAsync(scope!, cancellationToken).ConfigureAwait(false);
                if (!snapshot.IsPartial && snapshot.Status == ScanOperationStatus.Completed)
                    foreach (var pair in snapshot.Pairs) confirmed.Add((Path.GetFullPath(pair.ImagePath).ToUpperInvariant(),Path.GetFullPath(pair.VideoPath).ToUpperInvariant()));
            }
            if (OutputDirectoryAttempt.Ensure(options.OutputDirectory) is { } outputDirectoryFailure)
            {
                int failed = initiallyFailed;
                foreach (var task in tasks.Where(t => t.Status is not (ProcessStatus.Success or ProcessStatus.Warning) && !Blocked(t)))
                    onTaskCompleted?.Invoke(task, ProductOutcomeCompletion.Record(new ProductOperationOutcome {
                        OperationId=Guid.NewGuid(),StartedUtc=DateTimeOffset.UtcNow,CompletedUtc=DateTimeOffset.UtcNow,
                        Operation=ProductOperationKind.Merge,Status=ProductOperationStatus.Failed,
                        CompletionKind=ProductCompletionKind.NoOutput,Inputs=new[]{task.ImagePath,task.VideoPath},
                        RequestedDestination=options.OutputDirectory,
                        Diagnostic=ProductOutcomeCompletion.Failure(outputDirectoryFailure,ProductOperationStage.Publication) }),++failed);
                return;
            }
            int completedCount = initiallyFailed;
            int batchSize = Math.Max(1, options.MaxDegreeOfParallelism);
            long? previousBatchStart = null;
            foreach (var batch in tasks.Where(task => task.Status is not (ProcessStatus.Success or ProcessStatus.Warning) && !Blocked(task)).Chunk(batchSize))
            {
                if (previousBatchStart is long previous)
                {
                    TimeSpan remaining = options.TaskStartInterval - Stopwatch.GetElapsedTime(previous);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return;
                previousBatchStart = Stopwatch.GetTimestamp();

                var runningTasks = batch.Select(async task =>
                {
                    await WaitPauseAsync(pauseEvent, cancellationToken).ConfigureAwait(false);
                    onTaskStarted?.Invoke(task);

                    var result = await ProcessSinglePairRebuiltAsync(
                        task.ImagePath, task.VideoPath, task.BaseName, task.Index, options, task.SelectionIntent,
                        task.SelectionIntent is not (SelectionIntent.AppleIdentity or SelectionIntent.VivoIdentity) ||
                            confirmed.Contains((Path.GetFullPath(task.ImagePath).ToUpperInvariant(),Path.GetFullPath(task.VideoPath).ToUpperInvariant())),
                        cancellationToken).ConfigureAwait(false);
                    if (result.Status == ProductOperationStatus.Cancelled)
                    {
                        onTaskCompleted?.Invoke(task, result, Volatile.Read(ref completedCount));
                        return;
                    }
                    int currentCompleted = Interlocked.Increment(ref completedCount);
                    onTaskCompleted?.Invoke(task, result, currentCompleted);
                });

                await Task.WhenAll(runningTasks).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return;
            }
        }

        private static ProductOperationOutcome Unrecorded(ProductOperationOutcome outcome) => outcome;

        private static async Task<ProductOperationOutcome> ProcessSinglePairRebuiltAsync(
            string imagePath, string videoPath, string baseName, int taskIndex, LivePhotoMergeRunOptions options,
            SelectionIntent selectionIntent, bool selectionConfirmed, CancellationToken token)
        {
            using var cleanupScope = WorkspaceCleanupScope.Begin();
            var outcome = await ProcessSinglePairCoreAsync(imagePath, videoPath, baseName, taskIndex, options,
                selectionIntent, selectionConfirmed, token).ConfigureAwait(false);
            outcome = await CleanupCompletion.ApplyAsync(outcome, cleanupScope.Failures,
                options.OutputFilePath is { } requested ? Path.GetDirectoryName(Path.GetFullPath(requested))! : options.OutputDirectory,
                token).ConfigureAwait(false);
            if (outcome.IsSuccess) outcome = outcome with { SourceActionGuard = CaptureSourceActionGuard(outcome.Inputs, outcome.CommittedOutputs) };
            return ProductOutcomeCompletion.Record(outcome with { CompletedUtc = DateTimeOffset.UtcNow });
        }

        private static async Task<ProductOperationOutcome> ProcessSinglePairCoreAsync(
            string imagePath,
            string videoPath,
            string baseName,
            int taskIndex,
            LivePhotoMergeRunOptions options,
            SelectionIntent selectionIntent,
            bool selectionConfirmed,
            CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            // An explicit path is known before preprocessing starts. Keep it
            // for diagnostics only; a failed merge must never delete it by path.
            string? finalOutputPath = options.OutputFilePath;
            string? candidateReservationKey = null;
            var baseline = new ProductOperationOutcome
            {
                OperationId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow,
                Operation = ProductOperationKind.Merge,
                RequestedSelection = new() { TargetProtocol = options.OutputProfile?.TargetProtocol,
                    OutputFormat = options.OutputProfile?.Format,
                    PreservationPolicy = options.StructuredOptions.PreservationPolicy,
                    KeepVideoStabilizationEdges = options.KeepVideoStabilizationEdges || options.StructuredOptions.KeepVideoStabilizationEdges,
                    UnspecifiedImageOrientationHandling=options.UnspecifiedImageOrientationHandling,
                    TargetVersion = options.OutputProfile?.TargetProtocol switch
                    {
                        TargetProtocol.SamsungMotionPhoto => (uint)options.StructuredOptions.SamsungVariant,
                        TargetProtocol.HuaweiMovingPhoto => (uint)options.StructuredOptions.HuaweiVariant,
                        _ => 1
                    } },
                Inputs = string.IsNullOrEmpty(videoPath) ? new[] { imagePath } : new[] { imagePath, videoPath },
                RequestedDestination = options.OutputFilePath ?? options.OutputDirectory
            };
            NeutralMediaBundle? prepared = null;
            ProductCommittedArtifact[] committedOutputs = [];
            var notCarried = new List<NotCarriedItem>();
            IReadOnlyList<string> warnings = [];
            string? isolationDirectory = null;
            ProductOperationStage stage = ProductOperationStage.Selection;
            ProductOperationOutcome terminal;
            try
            {
                if (token.IsCancellationRequested)
                    return Unrecorded(baseline with { CompletedUtc=DateTimeOffset.UtcNow,
                        Status=ProductOperationStatus.Cancelled,CompletionKind=ProductCompletionKind.Cancelled,
                        Diagnostic=new(){Category=ProductFailureCategory.Cancelled,Message="用户取消，未生成输出。"} });
                ArgumentNullException.ThrowIfNull(options.OutputProfile);
                if (!selectionConfirmed)
                    return Unrecorded(baseline with { CompletedUtc = DateTimeOffset.UtcNow,
                        Status = ProductOperationStatus.Failed, CompletionKind = ProductCompletionKind.NoOutput,
                        Diagnostic = new() { Stage = stage, Category = ProductFailureCategory.Inspection,
                            Message = "Identity selection scope changed or is incomplete; rescan before processing this pair." } });
                if (await ProductInputAssessment.RejectionAsync(imagePath,
                    string.IsNullOrEmpty(videoPath) ? null : videoPath, token, selectionIntent).ConfigureAwait(false) is { } refusal)
                {
                    var diagnostic = ProductOutcomeCompletion.Failure(refusal.ToException(), ProductOperationStage.Inspection);
                    return Unrecorded(baseline with
                    {
                        CompletedUtc = DateTimeOffset.UtcNow,
                        Status = refusal.Category == LivePhotoBox.Media.Inspection.SourceInspectionFailureCategory.Unsupported
                            ? ProductOperationStatus.Unsupported : ProductOperationStatus.Failed,
                        Diagnostic = diagnostic
                    });
                }
                var orientationHandling = options.UnspecifiedImageOrientationHandling;
                if (options.StructuredOptions.PreservationPolicy == PreservationPolicy.BestEffort &&
                    orientationHandling == UnspecifiedImageOrientationHandling.Reject)
                {
                    orientationHandling = UnspecifiedImageOrientationHandling.UseStoredPixels;
                }
                MediaFormatRequirement requirement = options.OutputProfile.MediaRequirement with
                    { UnspecifiedImageOrientationHandling = orientationHandling,
                      KeepVideoStabilizationEdges = options.KeepVideoStabilizationEdges || options.StructuredOptions.KeepVideoStabilizationEdges };

                // Keep original file objects read-only until publication completes.
                // This also blocks hard-link/reparse aliases and late replacements
                // at the actual mutation boundary, not just during path selection.
                using var imageLease = SourceReadAttempt.Open(imagePath);
                using var videoLease = string.IsNullOrEmpty(videoPath) ? null : SourceReadAttempt.Open(videoPath);
                if (!imageLease.Succeeded || videoLease is { Succeeded: false })
                {
                    int code = !imageLease.Succeeded ? imageLease.Error : videoLease!.Error;
                    return Unrecorded(baseline with
                    {
                        CompletedUtc = DateTimeOffset.UtcNow, Status = ProductOperationStatus.Failed,
                        CompletionKind = ProductCompletionKind.NoOutput,
                        Diagnostic = new() { Stage = ProductOperationStage.Inspection, Category = ProductFailureCategory.Io,
                            Message = $"无法读取或保护来源文件（系统错误 {code}）；未生成输出。" }
                    });
                }
                var workspaceAttempt = MediaWorkspace.TryCreate(token);
                if (workspaceAttempt.Failure is { } workspaceFailure)
                    return Unrecorded(baseline with { CompletedUtc = DateTimeOffset.UtcNow,
                        Status = token.IsCancellationRequested ? ProductOperationStatus.Cancelled : ProductOperationStatus.Failed,
                        CompletionKind = token.IsCancellationRequested ? ProductCompletionKind.Cancelled : ProductCompletionKind.NoOutput,
                        Diagnostic = ProductOutcomeCompletion.Failure(workspaceFailure, ProductOperationStage.Extraction) });
                using var workspace = workspaceAttempt.Workspace!;
                stage = ProductOperationStage.Inspection;
                NeutralPreparationAttempt preparation = selectionIntent == SelectionIntent.UserComposition && !string.IsNullOrEmpty(videoPath)
                    ? await new NeutralMediaService().TryCreateCompositionBundleAsync(imagePath, videoPath, workspace, requirement,
                        options.StructuredOptions.PreservationPolicy, token).ConfigureAwait(false)
                    : await new NeutralMediaService().TryCreateNeutralBundleAsync(
                    imagePath,
                    string.IsNullOrEmpty(videoPath) ? null : videoPath,
                    workspace,
                    requirement,
                    options.StructuredOptions.PreservationPolicy,
                    token).ConfigureAwait(false);
                if (!preparation.Succeeded)
                {
                    var diagnostic = ProductOutcomeCompletion.Failure(preparation.Failure!, preparation.Stage, preparation.Execution);
                    string recoveryDirectory = options.OutputFilePath is { } requested
                        ? Path.GetDirectoryName(Path.GetFullPath(requested))! : options.OutputDirectory;
                    var recovered = await ProductRecoveryService.SaveOriginalsAsync(
                        baseline with { PreparationExecution = preparation.Execution }, diagnostic, recoveryDirectory, token,
                        preparation.RecoveryMedia).ConfigureAwait(false);
                    return Unrecorded(recovered with { CompletedUtc = DateTimeOffset.UtcNow });
                }
                NeutralMediaBundle bundle = preparation.Bundle!;
                prepared = bundle;

                if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))
                    throw new InvalidDataException("Rebuilt merge requires a Native-inspected motion video pair.");

                var coverAttempt = TryResolveNeutralCoverTimestamp(
                    bundle.Semantics.Timing,
                    options.KeyPhotoTimestampUs,
                    options.StructuredOptions.PreservationPolicy,
                    notCarried);
                if (coverAttempt.Failure is { } coverFailure)
                {
                    var diagnostic = ProductOutcomeCompletion.Failure(coverFailure, ProductOperationStage.Selection);
                    var recovered = await ProductRecoveryService.SaveOriginalsAsync(baseline, diagnostic, options.OutputDirectory,
                        token, new[] { bundle.PrimaryImage, bundle.MotionVideo! }).ConfigureAwait(false);
                    return Unrecorded(recovered with { CompletedUtc = DateTimeOffset.UtcNow });
                }
                long presentationTimestampUs = coverAttempt.TimestampUs!.Value;
                bundle = bundle with { Semantics = bundle.Semantics with
                {
                    Timing = bundle.Semantics.Timing with { CoverTimestampUs = presentationTimestampUs }
                } };
                warnings = ProductExportAssessment.Warnings(bundle, notCarried);
                if (warnings.Count > 0)
                    isolationDirectory = ProductExportAssessment.IsolationDirectory(
                        options.OutputFilePath is { } explicitPath ? Path.GetDirectoryName(Path.GetFullPath(explicitPath))! : options.OutputDirectory,
                        baseline.OperationId);

                stage = ProductOperationStage.Selection;
                string outputName = ExistingTargetWriterAdapter.CreateOutputFileName(
                    baseName,
                    options.OutputProfile,
                    bundle.PrimaryImage.Path,
                    options.NamingRuleIndex,
                    customPattern: options.NamingRuleIndex == 2 ? options.CustomNamingPattern : null,
                    taskIndex: options.NamingRuleIndex == 2 ? taskIndex : null);

                if (isolationDirectory is not null)
                {
                    if (OutputDirectoryAttempt.Ensure(isolationDirectory) is { } directoryFailure)
                    return Unrecorded(baseline with { CompletedUtc=DateTimeOffset.UtcNow,
                        Status=ProductOperationStatus.Failed, CompletionKind=ProductCompletionKind.NoOutput,
                        PreparationExecution=prepared?.PreparationExecution,
                        Diagnostic=ProductOutcomeCompletion.Failure(directoryFailure,ProductOperationStage.Publication) });
                    if (options.OutputFilePath is { } requestedFile)
                        finalOutputPath = Path.Combine(isolationDirectory,Path.GetFileName(requestedFile));
                    else
                        (finalOutputPath,candidateReservationKey) = AllocateUniqueOutputPathCandidate(isolationDirectory,outputName);
                }
                else if (options.OutputFilePath != null)
                {
                    finalOutputPath = options.OutputFilePath;
                    if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(finalOutputPath)!) is { } directoryFailure)
                    return Unrecorded(baseline with { CompletedUtc=DateTimeOffset.UtcNow,
                        Status=ProductOperationStatus.Failed, CompletionKind=ProductCompletionKind.NoOutput,
                        PreparationExecution=prepared?.PreparationExecution,
                        Diagnostic=ProductOutcomeCompletion.Failure(directoryFailure,ProductOperationStage.Publication) });
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

                    if (OutputDirectoryAttempt.Ensure(outputDirectory) is { } directoryFailure)
                    return Unrecorded(baseline with { CompletedUtc=DateTimeOffset.UtcNow,
                        Status=ProductOperationStatus.Failed, CompletionKind=ProductCompletionKind.NoOutput,
                        PreparationExecution=prepared?.PreparationExecution,
                        Diagnostic=ProductOutcomeCompletion.Failure(directoryFailure,ProductOperationStage.Publication) });
                    finalOutputPath = Path.Combine(outputDirectory, outputName);
                    if (!options.OverwriteExisting)
                    {
                        (finalOutputPath, candidateReservationKey) = AllocateUniqueOutputPathCandidate(
                            outputDirectory,
                            outputName);
                    }
                }

                if (string.Equals(Path.GetFullPath(finalOutputPath), Path.GetFullPath(imagePath), StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(videoPath) && string.Equals(Path.GetFullPath(finalOutputPath), Path.GetFullPath(videoPath), StringComparison.OrdinalIgnoreCase)))
                    return Unrecorded(baseline with { CompletedUtc = DateTimeOffset.UtcNow,
                        Status = ProductOperationStatus.Failed, CompletionKind = ProductCompletionKind.NoOutput,
                        Diagnostic = new() { Stage = ProductOperationStage.Publication, Category = ProductFailureCategory.Io,
                            Message = "Target publication cannot overwrite an original input." } });

                stage = ProductOperationStage.Writer;
                var publication = await StructuredTargetWriterService.TryWriteAsync(
                    bundle, options.OutputProfile, finalOutputPath, workspace.RootDirectory,
                    isolationDirectory is null && options.OverwriteExisting, token,
                    preservationPolicy: options.StructuredOptions.PreservationPolicy,
                    huaweiVariant: options.StructuredOptions.HuaweiVariant,
                    samsungVariant: options.StructuredOptions.SamsungVariant,
                    oppoInitialCoverPolicy: options.OutputProfile.TargetProtocol == TargetProtocol.OppoLivePhoto
                        ? OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover
                        : OppoInitialCoverPolicy.RequireKnownOriginal,
                    publicationPathResolver: (targetNotes,path) =>
                    {
                        var assessed=ProductExportAssessment.Warnings(bundle,notCarried.Concat(targetNotes));
                        warnings=assessed;
                        if(assessed.Count==0||isolationDirectory is not null)return path;
                        isolationDirectory=ProductExportAssessment.IsolationDirectory(
                            options.OutputFilePath is { } requested ? Path.GetDirectoryName(Path.GetFullPath(requested))! : options.OutputDirectory,baseline.OperationId);
                        if(candidateReservationKey is { } oldKey)s_outputPathCandidates.TryRemove(oldKey,out _);
                        (string chosen,string? key)=AllocateUniqueOutputPathCandidate(isolationDirectory,Path.GetFileName(path));
                        candidateReservationKey=key;finalOutputPath=chosen;return chosen;
                    }).ConfigureAwait(false);

                if (publication.Failure is { } publicationFailure)
                {
                    var diagnostic = ProductOutcomeCompletion.Failure(publicationFailure, ProductOperationStage.Writer);
                    string recoveryDirectory = options.OutputFilePath is { } requested
                        ? Path.GetDirectoryName(Path.GetFullPath(requested))! : options.OutputDirectory;
                    var recovered = await ProductRecoveryService.SaveOriginalsAsync(baseline, diagnostic,
                        recoveryDirectory, token, new[] { bundle.PrimaryImage, bundle.MotionVideo! }).ConfigureAwait(false);
                    return Unrecorded(recovered with { CompletedUtc = DateTimeOffset.UtcNow });
                }
                var published = publication.Receipt!;
                committedOutputs = published.VideoPath is null
                    ? [new ProductCommittedArtifact { Role = "TargetLivePhoto", Path = published.FinalPath,
                        Sha256 = published.Sha256, ByteLength = published.ByteLength }]
                    : [new ProductCommittedArtifact { Role = "PrimaryImage", Path = published.FinalPath,
                        Sha256 = published.Sha256, ByteLength = published.ByteLength },
                       new ProductCommittedArtifact { Role = "MotionVideo", Path = published.VideoPath }];
                notCarried.AddRange(published.NotCarried);
                if (bundle.PreparationExecution is { } prepExec)
                {
                    if (prepExec.Image?.Truth.HdrGainMap == ConversionComponentOutcome.Degraded)
                    {
                        if (!notCarried.Any(x => x.Item == "GainMap"))
                            notCarried.Add(new NotCarriedItem { Item = "GainMap", Reason = "Image HDR GainMap degraded to SDR during neutral preparation", Outcome = PreservationOutcome.DegradedToSdr });
                    }
                }

                stopwatch.Stop();
                LogService.Merge($"Rebuilt merge completed for {baseName}: {finalOutputPath} ({stopwatch.Elapsed.TotalSeconds:F2}s)");
                terminal = baseline with
                {
                    Status = warnings.Count > 0 ? ProductOperationStatus.Warning : ProductOperationStatus.Success,
                    CompletionKind = warnings.Count > 0 ? ProductCompletionKind.CompatibleCompleted : ProductCompletionKind.TargetCompleted,
                    Warnings = warnings, IsolationDirectory = isolationDirectory,
                    PreparationSemantics = bundle.Semantics,
                    PreparationManifest = bundle.Manifest,
                    PreparationExecution = bundle.PreparationExecution,
                    CommittedOutputs = committedOutputs,
                    SourceActionGuard = null,
                    NotCarried = notCarried,
                    Diagnostic = new() { Stage = ProductOperationStage.Publication, Message = isolationDirectory is null && warnings.Count == 0
                        ? ResourceService.GetString("Task_Success") : ProductExportAssessment.Details(warnings, isolationDirectory) }
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                NeutralPreparationDiagnostics.TryRead(ex, out var failurePreparation);
                var execution = failurePreparation?.Execution ?? prepared?.PreparationExecution;
                var diagnostic = ProductOutcomeCompletion.Failure(ex, stage, execution);
                if (ex is not OperationCanceledException)
                    LogService.Merge($"Rebuilt merge failed for {baseName}: {ex.Message} ({stopwatch.Elapsed.TotalSeconds:F2}s)", LogLevel.Error, ex);
                terminal = baseline with
                {
                    Status = ProductOutcomeCompletion.Status(ex, diagnostic),
                    PreparationSemantics = prepared?.Semantics,
                    PreparationManifest = prepared?.Manifest ?? [],
                    PreparationExecution = execution,
                    CommittedOutputs = committedOutputs,
                    NotCarried = notCarried,
                    Diagnostic = diagnostic with { Message = ex is OperationCanceledException ? ex.Message : ResourceService.Format("Task_Error", ex.Message) }
                };
            }
            finally
            {
                if (candidateReservationKey != null)
                    s_outputPathCandidates.TryRemove(candidateReservationKey, out _);
            }
            // Includes workspace disposal and reservation release. No earlier
            // success record can escape if cleanup itself throws.
            return Unrecorded(terminal with { CompletedUtc = DateTimeOffset.UtcNow });
        }

        internal static ProductSourceActionGuard? CaptureSourceActionGuard(IEnumerable<string> inputs, IEnumerable<ProductCommittedArtifact> outputs)
        {
            try
            {
                var attempt = ProductSourceActionGuard.TryCapture(inputs, outputs);
                if (attempt.Failure is { } failure) LogService.Warn("Source post-processing unavailable; export retained. " + failure.Message);
                return attempt.Guard;
            }
            catch (Exception ex) { LogService.Warn("Source post-processing unavailable; export retained. " + ex.Message); return null; }
        }

        internal sealed record CoverTimestampAttempt(long? TimestampUs, Exception? Failure);

        internal static long ResolveNeutralCoverTimestamp(NeutralTimingSemantics timing, long? requestedTimestampUs,
            PreservationPolicy preservationPolicy = PreservationPolicy.Strict, List<NotCarriedItem>? notCarried = null)
        {
            var attempt = TryResolveNeutralCoverTimestamp(timing, requestedTimestampUs, preservationPolicy, notCarried);
            if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return attempt.TimestampUs!.Value;
        }

        internal static CoverTimestampAttempt TryResolveNeutralCoverTimestamp(
            NeutralTimingSemantics timing,
            long? requestedTimestampUs,
            PreservationPolicy preservationPolicy = PreservationPolicy.Strict,
            List<NotCarriedItem>? notCarried = null)
        {
            ArgumentNullException.ThrowIfNull(timing);

            CoverTimestampAttempt FirstFrameFallback()
            {
                long first = timing.FirstValidFrameTimestampUs ?? 0;
                if (!IsLegalCoverTimestamp(first,timing.DurationUs)) return new(null, new InvalidDataException("First valid video frame is unavailable."));
                notCarried?.Add(new NotCarriedItem { Item = "CoverTimestamp", Reason = $"Source cover unavailable; using first valid video frame at {first}us; selected static image retained.",
                    Outcome = PreservationOutcome.PartiallyPreserved });
                return new(first, null);
            }

            if (requestedTimestampUs is long requested)
            {
                if (!IsLegalCoverTimestamp(requested, timing.DurationUs))
                {
                    if (preservationPolicy == PreservationPolicy.BestEffort && timing.DurationUs is long duration && duration > 0)
                    {
                        long clamped = timing.FirstValidFrameTimestampUs ?? 0;
                        if (!IsLegalCoverTimestamp(clamped, timing.DurationUs))
                            return new(null, new InvalidDataException("First valid video frame is unavailable."));
                        notCarried?.Add(new NotCarriedItem
                        {
                            Item = "CoverTimestamp",
                            Reason = $"Requested key-photo timestamp {requested}us outside video; using first valid frame at {clamped}us",
                            Outcome = PreservationOutcome.PartiallyPreserved
                        });
                        return new(clamped, null);
                    }
                    return new(null, new InvalidDataException("The requested key-photo timestamp must be within the known final video duration."));
                }

                return new(requested, null);
            }

            switch (timing.EvidenceState)
            {
                case NeutralEvidenceState.Verified:
                case NeutralEvidenceState.VerifiedPreserved:
                case NeutralEvidenceState.VerifiedConverted:
                    if (timing.CoverTimestampUs is not long verifiedTimestamp ||
                        !IsLegalCoverTimestamp(verifiedTimestamp, timing.DurationUs))
                    {
                        if (preservationPolicy == PreservationPolicy.BestEffort && timing.DurationUs is long duration && duration > 0)
                        {
                            long raw = timing.CoverTimestampUs ?? 0;
                            long clamped = timing.FirstValidFrameTimestampUs ?? 0;
                            notCarried?.Add(new NotCarriedItem
                            {
                                Item = "CoverTimestamp",
                                Reason = $"Source cover timestamp {raw}us unavailable/outside video; using first valid frame at {clamped}us",
                                Outcome = PreservationOutcome.PartiallyPreserved
                            });
                            return new(clamped, null);
                        }
                        return new(null, new InvalidDataException("Verified neutral cover timing must be within the known final video duration."));
                    }

                    return new(verifiedTimestamp, null);

                case NeutralEvidenceState.NotApplicable when timing.CoverTimestampUs is null && timing.DurationUs is > 0:
                    return FirstFrameFallback();

                case NeutralEvidenceState.NotApplicable:
                    if (preservationPolicy == PreservationPolicy.BestEffort && timing.DurationUs is > 0)
                        return FirstFrameFallback();
                    return new(null, new InvalidDataException("NotApplicable neutral timing cannot include a source cover timestamp."));

                default:
                    if (preservationPolicy == PreservationPolicy.BestEffort && timing.DurationUs is > 0)
                        return FirstFrameFallback();
                    return new(null, new InvalidDataException("Neutral cover timing is unknown; an explicit in-range key-photo timestamp is required."));
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
            if (token.IsCancellationRequested) return;
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
                using var reg = token.Register(() => tcs.TrySetResult(false));
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
        public static async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(
            string imagePath,
            string videoPath,
            string baseName,
            int taskIndex,
            LivePhotoMergeRunOptions options,
            string tempDir,
            CancellationToken token)
        {
            _ = tempDir;
            ProductOperationOutcome outcome = await ProcessSinglePairWithOutcomeAsync(
                imagePath, videoPath, baseName, taskIndex, options, token).ConfigureAwait(false);
            if (outcome.Status == ProductOperationStatus.Cancelled) throw new OperationCanceledException(token);
            return (outcome.IsSuccess, outcome.Details);
        }

        internal static Task<ProductOperationOutcome> ProcessSinglePairWithOutcomeAsync(
            string imagePath, string videoPath, string baseName, int taskIndex,
            LivePhotoMergeRunOptions options, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(options);
            return ProcessingPipelineRouter.RunRebuiltAsync("merge", () => ProcessSinglePairRebuiltAsync(
                imagePath, videoPath, baseName, taskIndex, options, SelectionIntent.UserComposition, true, token));
        }

        internal static Task<ProductOperationOutcome> ProcessSinglePairWithOutcomeAsync(
            string imagePath, string videoPath, string baseName, int taskIndex,
            LivePhotoMergeRunOptions options, string tempDir, CancellationToken token)
        {
            _ = tempDir;
            return ProcessSinglePairWithOutcomeAsync(imagePath, videoPath, baseName, taskIndex, options, token);
        }
    }
}
