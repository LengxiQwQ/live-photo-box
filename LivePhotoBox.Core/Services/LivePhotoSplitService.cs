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

            string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, string? secondaryPath = null, long? keyTimestampUs = null)

            => (await ExecuteSplitWithOutcomeAsync(sourcePath, outputDirectory, format, token,

                inputDirectory, outputBaseName, overwriteExisting, secondaryPath, keyTimestampUs).ConfigureAwait(false)).Outcome;



        private sealed record SplitAttempt(ProductOperationOutcome Outcome, LivePhotoSplitResult? Result, Exception? Error);



        private static async Task<SplitAttempt> ExecuteSplitWithOutcomeAsync(

            string sourcePath, string outputDirectory, SplitMediaFormat format, CancellationToken token,

            string? inputDirectory, string? outputBaseName, bool overwriteExisting, string? secondaryPath, long? keyTimestampUs)

        {

            var baseline = new ProductOperationOutcome

            {

                OperationId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow,

                Operation = ProductOperationKind.Split,

                RequestedSelection = new() { SplitFormat = Enum.IsDefined(format) ? format : null, TargetProtocol = TargetProfile(format)?.TargetProtocol },

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

                token.ThrowIfCancellationRequested();

                using var sourceProtection = new SourceInputProtection(new[] { sourcePath, secondaryPath });

                OutputProfile? targetProfile = TargetProfile(format);

                if (keyTimestampUs.HasValue && targetProfile is null)

                    throw new NotSupportedException("Protocol-free media has no live cover binding to override.");

                MediaFormatRequirement requirement = SplitMediaRequirements.Get(format);
                if (await ProductInputAssessment.RejectionAsync(sourcePath, secondaryPath, token).ConfigureAwait(false) is { } refusal)
                {
                    var error = refusal.ToException(); // Retained only for callers of the strict legacy API.
                    var diagnostic = ProductOutcomeCompletion.Failure(error, ProductOperationStage.Inspection);
                    var rejected = ProductOutcomeCompletion.Record(baseline with
                    {
                        CompletedUtc = DateTimeOffset.UtcNow,
                        Status = ProductOutcomeCompletion.Status(error, diagnostic), Diagnostic = diagnostic
                    });
                    return new SplitAttempt(rejected, null, error);
                }
                if (format != SplitMediaFormat.KeepSource) requirement = requirement with
                    { UnspecifiedImageOrientationHandling = UnspecifiedImageOrientationHandling.UseStoredPixels };

                stage = ProductOperationStage.Inspection;

                result = await ProcessingPipelineRouter.RunRebuiltAsync("split", () => SplitRebuiltAsync(

                    sourcePath, outputDirectory, requirement, token,

                    inputDirectory, outputBaseName, overwriteExisting,

                    secondaryPath, bundle => prepared = bundle, boundary => stage = boundary, targetProfile, keyTimestampUs, notes => targetNotes = notes,
                    baseline.OperationId, (items, directory) => { warnings = items; isolationDirectory = directory; })).ConfigureAwait(false);

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

                    SourceActionGuard = warnings.Count == 0 ? LivePhotoMergeRunnerService.CaptureSourceActionGuard(baseline.Inputs, new[]
                    {
                        new ProductCommittedArtifact { Role = "PrimaryImage", Path = result.ImageOutputPath },
                        new ProductCommittedArtifact { Role = "MotionVideo", Path = result.VideoOutputPath }
                    }) : null,
                    Diagnostic = new() { Stage = ProductOperationStage.Publication, Message = isolationDirectory is null
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

            terminal = ProductOutcomeCompletion.Record(terminal with { CompletedUtc = DateTimeOffset.UtcNow });

            return new SplitAttempt(terminal, result, failure);

        }



        // Rebuilt split: materialize an inspected single/dual-file source into two

        // independently usable, protocol-free media files. Target protocol

        // writers intentionally do not participate in this pipeline stage.

        private static async Task<LivePhotoSplitResult> SplitRebuiltAsync(

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
            Guid operationId = default, Action<IReadOnlyList<string>, string?>? onAssessment = null)

        {

            if (string.IsNullOrWhiteSpace(sourcePath))

            {

                throw new ArgumentException("Source path cannot be null or whitespace.", nameof(sourcePath));

            }



            _ = new FileInfo(sourcePath).Length;



            using var workspace = new MediaWorkspace();

            NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(

                sourcePath,

                secondaryPath,

                workspace,

                requirement,

                PreservationPolicy.BestEffort,

                token).ConfigureAwait(false);

            onPrepared?.Invoke(bundle);



            if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))

                throw new InvalidDataException("Rebuilt split requires a Native-inspected motion video.");



            onStage?.Invoke(ProductOperationStage.Publication);
            var coverNotes = new List<NotCarriedItem>();
            if (targetProfile is not null)
            {
                long cover = LivePhotoMergeRunnerService.ResolveNeutralCoverTimestamp(bundle.Semantics.Timing,
                    keyTimestampUs, PreservationPolicy.BestEffort, coverNotes);
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

            if (targetProfile is not null)

            {

                onStage?.Invoke(ProductOperationStage.Writer);

                var receipt = await StructuredTargetWriterService.WriteAsync(bundle, targetProfile,

                    imageOutputPath, workspace.RootDirectory, overwriteExisting, token).ConfigureAwait(false);

                onTargetNotes?.Invoke(coverNotes.Concat(receipt.NotCarried).ToArray());

                return new LivePhotoSplitResult { ImageOutputPath = receipt.FinalPath,

                    VideoOutputPath = receipt.VideoPath ?? throw new IOException("Target writer omitted its companion.") };

            }



            string sourceImagePath = bundle.PrimaryImage.Path;

            if (bundle.PrimaryImage.CompoundPrimary is not null)

            {

                var (_, obs) = bundle.PrimaryImage.CompoundPrimary.Inspect(bundle.PrimaryImage.Path, token);

                if (obs.KnownLength > 0 && obs.KnownLength < (ulong)new FileInfo(bundle.PrimaryImage.Path).Length)

                {

                    string cleanPrimaryPath = workspace.AllocateFilePath("split-clean-primary", imageExtension.ToLowerInvariant());

                    using (var src = File.OpenRead(bundle.PrimaryImage.Path))

                    using (var dst = File.Create(cleanPrimaryPath))

                    {

                        byte[] buf = new byte[81920];

                        long remaining = checked((long)obs.KnownLength);

                        while (remaining > 0)

                        {

                            int toRead = (int)Math.Min(remaining, buf.Length);

                            int r = src.Read(buf, 0, toRead);

                            if (r <= 0) throw new EndOfStreamException("Known primary ended before its inspected extent.");

                            dst.Write(buf, 0, r);

                            remaining -= r;

                        }

                    }

                    string sha = await workspace.ComputeFileSha256Async(cleanPrimaryPath, token).ConfigureAwait(false);

                    var fileIdentity = WindowsFileIdentity.Capture(cleanPrimaryPath);

                    LivePhotoBox.Media.Workspace.WorkspaceOwnershipRegistry.RegisterCreatedFile(cleanPrimaryPath, fileIdentity, checked((long)obs.KnownLength), sha, "split-clean-primary");

                    sourceImagePath = cleanPrimaryPath;

                }

            }



            if (secondaryPath is not null &&

                (string.Equals(Path.GetFullPath(imageOutputPath), Path.GetFullPath(secondaryPath), StringComparison.OrdinalIgnoreCase) ||

                 string.Equals(Path.GetFullPath(videoOutputPath), Path.GetFullPath(secondaryPath), StringComparison.OrdinalIgnoreCase)))

                throw new IOException("Target publication cannot overwrite an original companion.");



            await PublishSplitOutputsAsync(

                sourceImagePath,

                bundle.MotionVideo.Path,

                imageOutputPath,

                videoOutputPath,

                overwriteExisting,

                token).ConfigureAwait(false);



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



        private static Task PublishSplitOutputsAsync(

            string imageSourcePath,

            string videoSourcePath,

            string imageFinalPath,

            string videoFinalPath,

            bool overwriteExisting,

            CancellationToken token)

        {

            return WindowsSplitOutputPublisher.PublishAsync(

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

            Directory.CreateDirectory(targetDir);



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
