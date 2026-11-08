using LivePhotoBox.Models;

using LivePhotoBox.Interop;

using LivePhotoBox.Media;

using LivePhotoBox.Media.Models;

using LivePhotoBox.Media.Workspace;

using System;

using System.IO;

using System.Collections.Generic;

using System.Text;
using System.Linq;

using System.Threading;

using System.Threading.Tasks;

using System.Runtime.ExceptionServices;

using NeutralMediaBundle = LivePhotoBox.Protocols.Cleaning.NeutralMediaBundle;



namespace LivePhotoBox.Services

{

    // Rebuilt orchestration and transactional publication only. Protocol truth

    // belongs to SourceInspector / Native; raw metadata reads are diagnostics.

    public static class LivePhotoSplitService

    {





        private static readonly AsyncLocal<SplitPublicationTestHook?> s_splitPublicationTestHook = new();



        public static Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory, int protocolIndex, int outputFormatIndex, CancellationToken token, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, long? keyTimestampUs = null, string? secondaryPath = null)

        {

            return SplitAsync(sourcePath, outputDirectory,

                ProductSelectionAdapter.Split(protocolIndex, outputFormatIndex), token,

                inputDirectory, outputBaseName, overwriteExisting, secondaryPath, keyTimestampUs);

        }



        public static async Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory,

            SplitMediaFormat format, CancellationToken token, string? inputDirectory = null,

            string? outputBaseName = null, bool overwriteExisting = false, string? secondaryPath = null, long? keyTimestampUs = null)

        {

            SplitAttempt attempt = await ExecuteSplitWithOutcomeAsync(sourcePath, outputDirectory, format, token,

                inputDirectory, outputBaseName, overwriteExisting, secondaryPath, keyTimestampUs).ConfigureAwait(false);

            if (attempt.Error is not null) ExceptionDispatchInfo.Capture(attempt.Error).Throw();

            return new LivePhotoSplitResult

            {

                ImageOutputPath = attempt.Result!.ImageOutputPath,

                VideoOutputPath = attempt.Result.VideoOutputPath,

                Outcome = attempt.Outcome

            };

        }



        internal static async Task<ProductOperationOutcome> SplitWithOutcomeAsync(

            string sourcePath, string outputDirectory, SplitMediaFormat format, CancellationToken token,

            string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, string? secondaryPath = null, long? keyTimestampUs = null,
            bool keepVideoStabilizationEdges = false)

            => (await ExecuteSplitWithOutcomeAsync(sourcePath, outputDirectory, format, token,

                inputDirectory, outputBaseName, overwriteExisting, secondaryPath, keyTimestampUs, keepVideoStabilizationEdges).ConfigureAwait(false)).Outcome;



        private sealed record SplitAttempt(ProductOperationOutcome Outcome, LivePhotoSplitResult? Result, Exception? Error);



        private static ProductOperationOutcome Unrecorded(ProductOperationOutcome outcome) => outcome;

        private static async Task<SplitAttempt> ExecuteSplitWithOutcomeAsync(
            string sourcePath, string outputDirectory, SplitMediaFormat format, CancellationToken token,
            string? inputDirectory, string? outputBaseName, bool overwriteExisting, string? secondaryPath,
            long? keyTimestampUs, bool keepVideoStabilizationEdges = false)
        {
            using var cleanupScope = WorkspaceCleanupScope.Begin();
            var attempt = await ExecuteSplitCoreAsync(sourcePath, outputDirectory, format, token, inputDirectory,
                outputBaseName, overwriteExisting, secondaryPath, keyTimestampUs, keepVideoStabilizationEdges).ConfigureAwait(false);
            var outcome = await CleanupCompletion.ApplyAsync(attempt.Outcome, cleanupScope.Failures, outputDirectory, token).ConfigureAwait(false);
            if (outcome.IsSuccess) outcome = outcome with { SourceActionGuard = LivePhotoMergeRunnerService.CaptureSourceActionGuard(outcome.Inputs, outcome.CommittedOutputs) };
            var result = attempt.Result;
            if (result is not null && outcome.CommittedOutputs.Count >= 2)
                result = new LivePhotoSplitResult { ImageOutputPath = outcome.CommittedOutputs[0].Path,
                    VideoOutputPath = outcome.CommittedOutputs[1].Path };
            return new SplitAttempt(ProductOutcomeCompletion.Record(outcome with { CompletedUtc = DateTimeOffset.UtcNow }), result, attempt.Error);
        }

        private static async Task<SplitAttempt> ExecuteSplitCoreAsync(

            string sourcePath, string outputDirectory, SplitMediaFormat format, CancellationToken token,

            string? inputDirectory, string? outputBaseName, bool overwriteExisting, string? secondaryPath, long? keyTimestampUs,
            bool keepVideoStabilizationEdges = false)

        {

            var baseline = new ProductOperationOutcome

            {

                OperationId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow,

                Operation = ProductOperationKind.Split,

                RequestedSelection = new() { SplitFormat = Enum.IsDefined(format) ? format : null, TargetProtocol = TargetProfile(format)?.TargetProtocol,
                    KeepVideoStabilizationEdges = keepVideoStabilizationEdges },

                Inputs = secondaryPath is null ? new[] { sourcePath } : new[] { sourcePath, secondaryPath }, RequestedDestination = outputDirectory

            };

            ProductOperationStage stage = ProductOperationStage.Selection;

            NeutralMediaBundle? prepared = null;

            LivePhotoSplitResult? result = null;

            IReadOnlyList<NotCarriedItem> targetNotes = [];
            IReadOnlyList<string> warnings = [];
            string? isolationDirectory = null;

            Exception? failure = null;

            ProductOperationOutcome terminal;

            try

            {

                if (token.IsCancellationRequested)
                {
                    var cancelled = Unrecorded(baseline with { CompletedUtc = DateTimeOffset.UtcNow,
                        Status = ProductOperationStatus.Cancelled, CompletionKind = ProductCompletionKind.Cancelled,
                        Diagnostic = new() { Stage = stage, Category = ProductFailureCategory.Cancelled, Message = "用户取消，未生成输出。" } });
                    return new SplitAttempt(cancelled, null, new OperationCanceledException(token));
                }

                using var sourceProtection = new SourceInputProtection(new[] { sourcePath, secondaryPath });
                if (!sourceProtection.IsComplete)
                {
                    var blocked = Unrecorded(baseline with { CompletedUtc=DateTimeOffset.UtcNow,
                        Status=ProductOperationStatus.Failed,CompletionKind=ProductCompletionKind.NoOutput,
                        Diagnostic=new(){Stage=ProductOperationStage.Inspection,Category=ProductFailureCategory.Io,
                            Message=sourceProtection.Diagnostic ?? "来源保护未完成，未生成输出。"} });
                    return new SplitAttempt(blocked,null,new IOException(blocked.Details));
                }

                OutputProfile? targetProfile = TargetProfile(format);

                if (keyTimestampUs.HasValue && targetProfile is null)

                {
                    var error = new NotSupportedException("Protocol-free media has no live cover binding to override.");
                    var refused = Unrecorded(baseline with { CompletedUtc = DateTimeOffset.UtcNow,
                        Status = ProductOperationStatus.Unsupported, CompletionKind = ProductCompletionKind.NoOutput,
                        Diagnostic = ProductOutcomeCompletion.Failure(error, ProductOperationStage.Selection) });
                    return new SplitAttempt(refused, null, error);
                }

                MediaFormatRequirement requirement = SplitMediaRequirements.Get(format) with
                    { KeepVideoStabilizationEdges = keepVideoStabilizationEdges };
                if (await ProductInputAssessment.RejectionAsync(sourcePath, secondaryPath, token).ConfigureAwait(false) is { } refusal)
                {
                    var error = refusal.ToException(); // Retained only for callers of the strict legacy API.
                    var diagnostic = ProductOutcomeCompletion.Failure(error, ProductOperationStage.Inspection);
                    var rejected = LivePhotoScanSupport.IsImage(sourcePath) &&
                        (secondaryPath == null || LivePhotoScanSupport.IsVideo(secondaryPath))
                        ? Unrecorded((await ProductRecoveryService.SaveOriginalsAsync(
                            baseline, diagnostic, outputDirectory, token).ConfigureAwait(false)) with { CompletedUtc = DateTimeOffset.UtcNow })
                        : Unrecorded(baseline with
                        {
                            CompletedUtc = DateTimeOffset.UtcNow,
                            Status = ProductOutcomeCompletion.Status(error, diagnostic), Diagnostic = diagnostic
                        });
                    return new SplitAttempt(rejected, null, error);
                }
                if (format != SplitMediaFormat.KeepSource) requirement = requirement with
                    { UnspecifiedImageOrientationHandling = UnspecifiedImageOrientationHandling.UseStoredPixels };

                stage = ProductOperationStage.Inspection;

                var split = await ProcessingPipelineRouter.RunRebuiltAsync("split", () => SplitRebuiltAsync(

                    sourcePath, outputDirectory, requirement, token,

                    inputDirectory, outputBaseName, overwriteExisting,

                    secondaryPath, bundle => prepared = bundle, boundary => stage = boundary, targetProfile, keyTimestampUs, notes => targetNotes = notes,
                    baseline.OperationId, (items, directory) => { warnings = items; isolationDirectory = directory; }, baseline)).ConfigureAwait(false);
                if (split.Recovery is { } recovery)
                    return new SplitAttempt(Unrecorded(recovery with { CompletedUtc = DateTimeOffset.UtcNow }), null, split.Failure);
                if (split.Result is null && split.Failure is { } splitFailure)
                {
                    var diagnostic = ProductOutcomeCompletion.Failure(splitFailure, stage);
                    var stopped = Unrecorded(baseline with { CompletedUtc = DateTimeOffset.UtcNow,
                        Status = ProductOutcomeCompletion.Status(splitFailure, diagnostic), Diagnostic = diagnostic,
                        CompletionKind = token.IsCancellationRequested ? ProductCompletionKind.Cancelled : ProductCompletionKind.NoOutput });
                    return new SplitAttempt(stopped, null, splitFailure);
                }
                result = split.Result!;

                terminal = baseline with

                {

                    Status = warnings.Count > 0 ? ProductOperationStatus.Warning : ProductOperationStatus.Success,
                    Warnings = warnings, IsolationDirectory = isolationDirectory,

                    PreparationSemantics = prepared?.Semantics,

                    PreparationManifest = prepared?.Manifest ?? [],

                    NotCarried = targetProfile is not null ? targetNotes : new[] { new NotCarriedItem { Item = "LiveCoverBinding",

                        Reason = "Protocol-free export keeps the still image and video timeline; source live cover timing is recorded in PreparationSemantics, not written as a live binding.",

                        Outcome = PreservationOutcome.DiscardedNotApplicable } },

                    PreparationExecution = prepared?.PreparationExecution,

                    CommittedOutputs = new[]

                    {

                        new ProductCommittedArtifact { Role = "PrimaryImage", Path = result.ImageOutputPath },

                        new ProductCommittedArtifact { Role = "MotionVideo", Path = result.VideoOutputPath }

                    },

                    SourceActionGuard = null,

                    Diagnostic = new() { Stage = ProductOperationStage.Publication, Message = isolationDirectory is null && warnings.Count == 0
                        ? ResourceService.GetString("SplitPage_Task_Success") : ProductExportAssessment.Details(warnings, isolationDirectory) }

                };

            }

            catch (Exception exception)

            {

                failure = exception;

                NeutralPreparationDiagnostics.TryRead(exception, out var diagnosticPreparation);

                var execution = diagnosticPreparation?.Execution ?? prepared?.PreparationExecution;

                var diagnostic = ProductOutcomeCompletion.Failure(exception, stage, execution);

                terminal = baseline with

                {

                    Status = ProductOutcomeCompletion.Status(exception, diagnostic), Diagnostic = diagnostic,

                    PreparationSemantics = prepared?.Semantics, PreparationExecution = execution

                };

            }

            terminal = Unrecorded(terminal with { CompletedUtc = DateTimeOffset.UtcNow });

            return new SplitAttempt(terminal, result, failure);

        }



        // Rebuilt split: materialize an inspected single/dual-file source into two

        // independently usable, protocol-free media files. Target protocol

        // writers intentionally do not participate in this pipeline stage.

        private sealed record SplitMediaAttempt(LivePhotoSplitResult? Result, ProductOperationOutcome? Recovery, Exception? Failure)
        {
            public static implicit operator SplitMediaAttempt(LivePhotoSplitResult result) => new(result, null, null);
        }

        private static async Task<SplitMediaAttempt> SplitRebuiltAsync(

            string sourcePath,

            string outputDirectory,

            MediaFormatRequirement requirement,

            CancellationToken token,

            string? inputDirectory,

            string? outputBaseName,

            bool overwriteExisting,

            string? secondaryPath,

            Action<NeutralMediaBundle>? onPrepared = null,

            Action<ProductOperationStage>? onStage = null, OutputProfile? targetProfile = null, long? keyTimestampUs = null, Action<IReadOnlyList<NotCarriedItem>>? onTargetNotes = null,
            Guid operationId = default, Action<IReadOnlyList<string>, string?>? onAssessment = null,
            ProductOperationOutcome? baseline = null)

        {

            if (string.IsNullOrWhiteSpace(sourcePath))

            {

                throw new ArgumentException("Source path cannot be null or whitespace.", nameof(sourcePath));

            }



            using var sourceRead = SourceReadAttempt.Open(sourcePath);
            if (!sourceRead.Succeeded) return new(null, null, new IOException($"Cannot retain split source (system error {sourceRead.Error})."));



            var workspaceAttempt = MediaWorkspace.TryCreate(token);
            if (workspaceAttempt.Failure is { } workspaceFailure) return new(null, null, workspaceFailure);
            using var workspace = workspaceAttempt.Workspace!;

            var preparation = await new NeutralMediaService().TryCreateNeutralBundleAsync(

                sourcePath,

                secondaryPath,

                workspace,

                requirement,

                PreservationPolicy.BestEffort,

                token).ConfigureAwait(false);

            if (!preparation.Succeeded)
            {
                var diagnostic = ProductOutcomeCompletion.Failure(preparation.Failure!, preparation.Stage, preparation.Execution);
                var recovered = await ProductRecoveryService.SaveOriginalsAsync(baseline!, diagnostic, outputDirectory,
                    token, preparation.RecoveryMedia).ConfigureAwait(false);
                return new(null, recovered, preparation.Failure);
            }
            NeutralMediaBundle bundle = preparation.Bundle!;
            onPrepared?.Invoke(bundle);

            async Task<SplitMediaAttempt> RecoverAsync(Exception error, ProductOperationStage failureStage)
            {
                var media = bundle.MotionVideo is { } movie ? new[] { bundle.PrimaryImage, movie } : new[] { bundle.PrimaryImage };
                var diagnostic = ProductOutcomeCompletion.Failure(error, failureStage, bundle.PreparationExecution);
                var recovered = await ProductRecoveryService.SaveOriginalsAsync(baseline!, diagnostic, outputDirectory, token, media).ConfigureAwait(false);
                return new(null, recovered, error);
            }



            if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))

                return await RecoverAsync(new InvalidDataException("Rebuilt split requires a Native-inspected motion video."), ProductOperationStage.Inspection).ConfigureAwait(false);



            onStage?.Invoke(ProductOperationStage.Publication);
            var coverNotes = new List<NotCarriedItem>();
            if (targetProfile is not null)
            {
                var coverAttempt = LivePhotoMergeRunnerService.TryResolveNeutralCoverTimestamp(bundle.Semantics.Timing,
                    keyTimestampUs, PreservationPolicy.BestEffort, coverNotes);
                if (coverAttempt.Failure is { } coverFailure) return await RecoverAsync(coverFailure, ProductOperationStage.Selection).ConfigureAwait(false);
                long cover = coverAttempt.TimestampUs!.Value;
                bundle = bundle with { Semantics = bundle.Semantics with
                    { Timing = bundle.Semantics.Timing with { CoverTimestampUs = cover } } };
                onPrepared?.Invoke(bundle);
            }
            var warnings = ProductExportAssessment.Warnings(bundle, coverNotes, targetProfile is not null);
            string? isolationDirectory = null;
            if (warnings.Count > 0)
            {
                isolationDirectory = ProductExportAssessment.IsolationDirectory(outputDirectory, operationId);
                outputDirectory = isolationDirectory;
                overwriteExisting = false;
                inputDirectory = null;
            }
            onAssessment?.Invoke(warnings, isolationDirectory);

            string imageExtension = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".HEIC" : ".JPG";

            string videoExtension = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".MOV" : ".MP4";

            (string imageOutputPath, string videoOutputPath, var reservations) = BuildOutputPaths(

                sourcePath,

                outputDirectory,

                imageExtension,

                videoExtension,

                inputDirectory,

                outputBaseName,

                overwriteExisting);



            try

            {

            if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(imageOutputPath)!) is { } directoryFailure)
                return await RecoverAsync(directoryFailure, ProductOperationStage.Publication).ConfigureAwait(false);

            if (targetProfile is not null)

            {

                onStage?.Invoke(ProductOperationStage.Writer);

                var publication = await StructuredTargetWriterService.TryWriteAsync(bundle, targetProfile,

                    imageOutputPath, workspace.RootDirectory, overwriteExisting, token,
                    publicationPathResolver:(targetNotes,path)=>
                    {
                        warnings=ProductExportAssessment.Warnings(bundle,coverNotes.Concat(targetNotes));
                        if(warnings.Count==0||isolationDirectory is not null)
                        {
                            onAssessment?.Invoke(warnings,isolationDirectory);
                            return path;
                        }
                        isolationDirectory=ProductExportAssessment.IsolationDirectory(outputDirectory,operationId);
                        var isolatedPaths=BuildOutputPaths(sourcePath,isolationDirectory,imageExtension,videoExtension,
                            null,outputBaseName,false);
                        reservations.AddRange(isolatedPaths.ReservationKeys);
                        onAssessment?.Invoke(warnings,isolationDirectory);
                        return isolatedPaths.ImageOutputPath;
                    }).ConfigureAwait(false);

                if (publication.Failure is { } publicationFailure)
                {
                    var diagnostic = ProductOutcomeCompletion.Failure(publicationFailure, ProductOperationStage.Writer);
                    var recovered = await ProductRecoveryService.SaveOriginalsAsync(baseline!, diagnostic, outputDirectory,
                        token, new[] { bundle.PrimaryImage, bundle.MotionVideo! }).ConfigureAwait(false);
                    return new(null, recovered, publicationFailure);
                }
                var receipt = publication.Receipt!;
                onTargetNotes?.Invoke(coverNotes.Concat(receipt.NotCarried).ToArray());

                if (receipt.VideoPath is null)
                    return await RecoverAsync(new IOException("Target writer omitted its companion."), ProductOperationStage.Publication).ConfigureAwait(false);
                return new LivePhotoSplitResult { ImageOutputPath = receipt.FinalPath,

                    VideoOutputPath = receipt.VideoPath };

            }



            string sourceImagePath = bundle.PrimaryImage.Path;

            if (bundle.PrimaryImage.CompoundPrimary is not null)

            {

                var compoundObservation = bundle.PrimaryImage.CompoundPrimary.TryInspect(bundle.PrimaryImage.Path, token);
                if (compoundObservation.Failure is { } compoundFailure)
                    return await RecoverAsync(compoundFailure, ProductOperationStage.Inspection).ConfigureAwait(false);
                var obs = compoundObservation.Observation;

                if (obs.KnownLength > 0 && obs.KnownLength < (ulong)new FileInfo(bundle.PrimaryImage.Path).Length)

                {

                    string cleanPrimaryPath = workspace.AllocateFilePath("split-clean-primary", imageExtension.ToLowerInvariant());

                    using (var src = File.OpenRead(bundle.PrimaryImage.Path))

                    using (var dst = File.Create(cleanPrimaryPath))

                    {

                        byte[] buf = new byte[81920];

                        long remaining = checked((long)obs.KnownLength);
                        long written = 0;

                        while (remaining > 0)

                        {

                            int toRead = (int)Math.Min(remaining, buf.Length);

                            int r = src.Read(buf, 0, toRead);

                            if (r <= 0) return await RecoverAsync(new EndOfStreamException("Known primary ended before its inspected extent."), ProductOperationStage.Extraction).ConfigureAwait(false);

                            if (LivePhotoBox.Interop.TransactionStreamWriteAttempt.Write(dst, buf.AsSpan(0, r), token, written) is { } writeFailure)
                                return await RecoverAsync(writeFailure, ProductOperationStage.Extraction).ConfigureAwait(false);
                            written += r;

                            remaining -= r;

                        }

                    }

                    var hash = await LivePhotoBox.Interop.MediaFileHashAttempt.ComputeAsync(cleanPrimaryPath, token).ConfigureAwait(false);
                    if (hash.Failure is { } hashFailure) return await RecoverAsync(hashFailure, ProductOperationStage.Extraction).ConfigureAwait(false);
                    string sha = hash.Sha256!;

                    var fileIdentity = WindowsFileIdentity.Capture(cleanPrimaryPath);

                    LivePhotoBox.Media.Workspace.WorkspaceOwnershipRegistry.RegisterCreatedFile(cleanPrimaryPath, fileIdentity, checked((long)obs.KnownLength), sha, "split-clean-primary");

                    sourceImagePath = cleanPrimaryPath;

                }

            }



            if (secondaryPath is not null &&

                (string.Equals(Path.GetFullPath(imageOutputPath), Path.GetFullPath(secondaryPath), StringComparison.OrdinalIgnoreCase) ||

                 string.Equals(Path.GetFullPath(videoOutputPath), Path.GetFullPath(secondaryPath), StringComparison.OrdinalIgnoreCase)))

                return new(null, baseline! with { Status = ProductOperationStatus.Failed,
                    CompletionKind = ProductCompletionKind.NoOutput,
                    Diagnostic = new() { Stage = ProductOperationStage.Publication, Category = ProductFailureCategory.Io,
                        Message = "Target publication cannot overwrite an original companion." } },
                    new IOException("Target publication cannot overwrite an original companion."));



            var splitPublication=await PublishSplitOutputsAsync(

                sourceImagePath,

                bundle.MotionVideo.Path,

                imageOutputPath,

                videoOutputPath,

                overwriteExisting,

                token).ConfigureAwait(false);
            if(splitPublication.Failure is { } splitPublicationFailure)
            {
                var diagnostic=ProductOutcomeCompletion.Failure(splitPublicationFailure,ProductOperationStage.Publication,bundle.PreparationExecution);
                var recovered=await ProductRecoveryService.SaveOriginalsAsync(baseline!,diagnostic,outputDirectory,token,
                    new[]{bundle.PrimaryImage,bundle.MotionVideo}).ConfigureAwait(false);
                return new(null,recovered,splitPublicationFailure);
            }

            return new LivePhotoSplitResult

            {

                ImageOutputPath = imageOutputPath,

                VideoOutputPath = videoOutputPath

            };

            }

            finally

            {

                foreach (var key in reservations)

                    s_splitOutputCandidates.TryRemove(key, out _);

            }

        }



        internal static OutputProfile? TargetProfile(SplitMediaFormat format) => format switch

        {

            SplitMediaFormat.AppleJpegMov => OutputProfile.Create(TargetProtocol.AppleLivePhoto, OutputMediaFormat.JpegMov),

            SplitMediaFormat.AppleHeicMov => OutputProfile.Create(TargetProtocol.AppleLivePhoto, OutputMediaFormat.HeicMov),

            SplitMediaFormat.VivoJpegMp4 => OutputProfile.Create(TargetProtocol.VivoLegacyDualFile, OutputMediaFormat.JpegMp4),

            _ => null

        };



        internal static IDisposable SetBeforeSplitPublicationTestHook(

            string outputDirectory,

            Func<string, CancellationToken, Task> callback)

        {

            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

            ArgumentNullException.ThrowIfNull(callback);



            return SetSplitPublicationTimingTestHook(

                outputDirectory,

                async (trace, token) =>

                {

                    if (trace.Boundary == SplitPublicationBoundary.BeforeMissingTargetPublication)

                        await callback(trace.FinalPath, token).ConfigureAwait(false);

                });

        }



        internal static IDisposable SetSplitPublicationTimingTestHook(

            string outputDirectory,

            Func<SplitPublicationTraceEvent, CancellationToken, Task> callback)

        {

            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

            ArgumentNullException.ThrowIfNull(callback);



            SplitPublicationTestHook? previous = s_splitPublicationTestHook.Value;

            s_splitPublicationTestHook.Value = new SplitPublicationTestHook(

                Path.GetFullPath(outputDirectory), callback);

            return new SplitPublicationTestHookScope(previous);

        }



        private static async Task InvokeSplitPublicationTestHookAsync(

            SplitPublicationTraceEvent trace,

            CancellationToken token)

        {

            SplitPublicationTestHook? hook = s_splitPublicationTestHook.Value;

            if (hook == null)

                return;



            string? finalDirectory = Path.GetDirectoryName(Path.GetFullPath(trace.FinalPath));

            if (string.Equals(finalDirectory, hook.OutputDirectory, StringComparison.OrdinalIgnoreCase))

                await hook.Callback(trace, token).ConfigureAwait(false);

        }



        private sealed record SplitPublicationTestHook(

            string OutputDirectory,

            Func<SplitPublicationTraceEvent, CancellationToken, Task> Callback);



        private sealed class SplitPublicationTestHookScope(SplitPublicationTestHook? previous) : IDisposable

        {

            private bool _disposed;



            public void Dispose()

            {

                if (_disposed)

                    return;



                s_splitPublicationTestHook.Value = previous;

                _disposed = true;

            }

        }



        private static Task<WindowsSplitOutputPublisher.PublicationAttempt> PublishSplitOutputsAsync(

            string imageSourcePath,

            string videoSourcePath,

            string imageFinalPath,

            string videoFinalPath,

            bool overwriteExisting,

            CancellationToken token)

        {

            return WindowsSplitOutputPublisher.TryPublishAsync(

                imageSourcePath,

                videoSourcePath,

                imageFinalPath,

                videoFinalPath,

                overwriteExisting,

                InvokeSplitPublicationTestHookAsync,

                token);

        }



        // 构建拆分后图片和视频的输出路径。

        // 自动处理同名冲突（追加后缀），并防止输出路径覆盖源文件。

        // sourcePath: 源文件路径。

        // outputDirectory: 输出目录。

        // videoExtension: 视频扩展名（.mp4 / .mov）。

        // 返回: (图片输出路径, 视频输出路径)

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_splitOutputCandidates =

            new(StringComparer.OrdinalIgnoreCase);



        // 构建拆分后图片和视频的输出路径。

        // 自动处理同名冲突（同步追加 (2)、(3) 等后缀），支持保留子目录结构，并防止输出覆盖源文件。

        private static (string ImageOutputPath, string VideoOutputPath, List<string> ReservationKeys) BuildOutputPaths(

            string sourcePath, string outputDirectory, string imageExtension, string videoExtension,

            string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false)

        {

            string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);

            string baseName = string.IsNullOrWhiteSpace(outputBaseName)

                ? sourceFileNameWithoutExtension

                : outputBaseName;



            if (string.IsNullOrWhiteSpace(imageExtension))

            {

                imageExtension = ".JPG";

            }



            string? subDir = null;

            if (!string.IsNullOrEmpty(inputDirectory))

            {

                subDir = PathHelper.GetRelativeSubDirectory(inputDirectory, sourcePath);

            }



            string targetDir = subDir != null ? Path.Combine(outputDirectory, subDir) : outputDirectory;

            // The product pipeline creates this directory through its result-based
            // boundary. Name allocation itself performs no filesystem mutation.



            string imageOutputPath;

            string videoOutputPath;

            var reservations = new List<string>();



            if (overwriteExisting)

            {

                imageOutputPath = Path.Combine(targetDir, $"{baseName}{imageExtension}");

                videoOutputPath = Path.Combine(targetDir, $"{baseName}{videoExtension}");

            }

            else

            {

                (imageOutputPath, videoOutputPath, reservations) = AllocateUniqueSplitOutputPaths(

                    targetDir, baseName, imageExtension, videoExtension);

            }



            string sourceFullPath = Path.GetFullPath(sourcePath);



            // 防止输出文件覆盖掉正在读取的源文件

            if (string.Equals(Path.GetFullPath(imageOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))

            {

                imageOutputPath = Path.Combine(targetDir, $"{baseName}_image{imageExtension}");

            }



            if (string.Equals(Path.GetFullPath(videoOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))

            {

                videoOutputPath = Path.Combine(targetDir, $"{baseName}_video{videoExtension}");

            }



            return (imageOutputPath, videoOutputPath, reservations);

        }



        private static (string ImagePath, string VideoPath, List<string> ReservationKeys) AllocateUniqueSplitOutputPaths(

            string targetDir, string baseName, string imageExtension, string videoExtension)

        {

            for (int suffix = 1; suffix < 999; suffix++)

            {

                string currentBase = suffix == 1 ? baseName : $"{baseName} ({suffix})";

                string img = Path.Combine(targetDir, $"{currentBase}{imageExtension}");

                string vid = Path.Combine(targetDir, $"{currentBase}{videoExtension}");

                if (TryReserveSplitPair(img, vid, out var keys))

                    return (img, vid, keys);

            }



            while (true)

            {

                string currentBase = $"{baseName} ({Guid.NewGuid():N})";

                string img = Path.Combine(targetDir, $"{currentBase}{imageExtension}");

                string vid = Path.Combine(targetDir, $"{currentBase}{videoExtension}");

                if (TryReserveSplitPair(img, vid, out var keys))

                    return (img, vid, keys);

            }

        }



        private static bool TryReserveSplitPair(string imgPath, string vidPath, out List<string> keys)

        {

            keys = new List<string>();

            string fullImg = Path.GetFullPath(imgPath);

            string fullVid = Path.GetFullPath(vidPath);



            if (File.Exists(fullImg) || Directory.Exists(fullImg) || File.Exists(fullVid) || Directory.Exists(fullVid))

                return false;



            if (!s_splitOutputCandidates.TryAdd(fullImg, 0))

                return false;



            if (!s_splitOutputCandidates.TryAdd(fullVid, 0))

            {

                s_splitOutputCandidates.TryRemove(fullImg, out _);

                return false;

            }



            if (File.Exists(fullImg) || Directory.Exists(fullImg) || File.Exists(fullVid) || Directory.Exists(fullVid))

            {

                s_splitOutputCandidates.TryRemove(fullImg, out _);

                s_splitOutputCandidates.TryRemove(fullVid, out _);

                return false;

            }



            keys.Add(fullImg);

            keys.Add(fullVid);

            return true;

        }



    }

}
