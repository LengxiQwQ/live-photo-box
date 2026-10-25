using LivePhotoBox.Models;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using System;
using System.IO;
using System.Text;
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
        private const int MetadataProbeBytes = 1024 * 1024;

        private static readonly AsyncLocal<SplitPublicationTestHook?> s_splitPublicationTestHook = new();

        public static Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory, int protocolIndex, int outputFormatIndex, CancellationToken token, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false, long? keyTimestampUs = null)
        {
            return SplitAsync(sourcePath, outputDirectory,
                ProductSelectionAdapter.Split(protocolIndex, outputFormatIndex), token,
                inputDirectory, outputBaseName, overwriteExisting);
        }

        public static async Task<LivePhotoSplitResult> SplitAsync(string sourcePath, string outputDirectory,
            SplitMediaFormat format, CancellationToken token, string? inputDirectory = null,
            string? outputBaseName = null, bool overwriteExisting = false)
        {
            SplitAttempt attempt = await ExecuteSplitWithOutcomeAsync(sourcePath, outputDirectory, format, token,
                inputDirectory, outputBaseName, overwriteExisting).ConfigureAwait(false);
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
            string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false)
            => (await ExecuteSplitWithOutcomeAsync(sourcePath, outputDirectory, format, token,
                inputDirectory, outputBaseName, overwriteExisting).ConfigureAwait(false)).Outcome;

        private sealed record SplitAttempt(ProductOperationOutcome Outcome, LivePhotoSplitResult? Result, Exception? Error);

        private static async Task<SplitAttempt> ExecuteSplitWithOutcomeAsync(
            string sourcePath, string outputDirectory, SplitMediaFormat format, CancellationToken token,
            string? inputDirectory, string? outputBaseName, bool overwriteExisting)
        {
            var baseline = new ProductOperationOutcome
            {
                OperationId = Guid.NewGuid(), StartedUtc = DateTimeOffset.UtcNow,
                Operation = ProductOperationKind.Split,
                RequestedSelection = new() { SplitFormat = Enum.IsDefined(format) ? format : null },
                Inputs = new[] { sourcePath }, RequestedDestination = outputDirectory
            };
            ProductOperationStage stage = ProductOperationStage.Selection;
            NeutralMediaBundle? prepared = null;
            LivePhotoSplitResult? result = null;
            Exception? failure = null;
            ProductOperationOutcome terminal;
            try
            {
                MediaFormatRequirement requirement = SplitMediaRequirements.Get(format);
                stage = ProductOperationStage.Inspection;
                result = await ProcessingPipelineRouter.RunRebuiltAsync("split", () => SplitRebuiltAsync(
                    sourcePath, outputDirectory, requirement, token,
                    inputDirectory, outputBaseName, overwriteExisting,
                    bundle => prepared = bundle, boundary => stage = boundary)).ConfigureAwait(false);
                terminal = baseline with
                {
                    Status = ProductOperationStatus.Success,
                    PreparationSemantics = prepared?.Semantics,
                    PreparationExecution = prepared?.PreparationExecution,
                    CommittedOutputs = new[]
                    {
                        new ProductCommittedArtifact { Role = "PrimaryImage", Path = result.ImageOutputPath },
                        new ProductCommittedArtifact { Role = "MotionVideo", Path = result.VideoOutputPath }
                    },
                    Diagnostic = new() { Stage = ProductOperationStage.Publication, Message = ResourceService.GetString("SplitPage_Task_Success") }
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

        // Rebuilt split: materialize a source single-file live photo into two
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
            Action<NeutralMediaBundle>? onPrepared = null,
            Action<ProductOperationStage>? onStage = null)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path cannot be null or whitespace.", nameof(sourcePath));
            }

            _ = new FileInfo(sourcePath).Length;

            using var workspace = new MediaWorkspace();
            NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                sourcePath,
                secondaryPath: null,
                workspace,
                requirement,
                PreservationPolicy.BestEffort,
                token).ConfigureAwait(false);
            onPrepared?.Invoke(bundle);

            if (bundle.MotionVideo == null || !File.Exists(bundle.MotionVideo.Path))
                throw new InvalidDataException("Rebuilt split requires a Native-inspected motion video.");

            onStage?.Invoke(ProductOperationStage.Publication);
            string imageExtension = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".HEIC" : ".JPG";
            string videoExtension = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".MOV" : ".MP4";
            (string imageOutputPath, string videoOutputPath) = BuildOutputPaths(
                sourcePath,
                outputDirectory,
                imageExtension,
                videoExtension,
                inputDirectory,
                outputBaseName,
                overwriteExisting);

            await PublishSplitOutputsAsync(
                bundle.PrimaryImage.Path,
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

        /// <summary>
        /// 同步版本：遗留文本扫描方法，仅保留给测试使用。
        /// 生产运行中的媒体检查必须使用 SourceInspector。
        /// </summary>
        [Obsolete("Legacy text probe retained only for tests. Production inspection must use SourceInspector.")]
        public static string ReadMetadataTextSync(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bufferLength = (int)Math.Min(fs.Length, MetadataProbeBytes);
            byte[] buffer = new byte[bufferLength];
            int bytesRead = fs.Read(buffer, 0, bufferLength);
            return Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }

        // 构建拆分后图片和视频的输出路径。
        // 自动处理同名冲突（追加后缀），并防止输出路径覆盖源文件。
        // sourcePath: 源文件路径。
        // outputDirectory: 输出目录。
        // videoExtension: 视频扩展名（.mp4 / .mov）。
        // 返回: (图片输出路径, 视频输出路径)
        private static (string ImageOutputPath, string VideoOutputPath) BuildOutputPaths(string sourcePath, string outputDirectory, string imageExtension, string videoExtension, string? inputDirectory = null, string? outputBaseName = null, bool overwriteExisting = false)
        {
            string sourceFileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
            // 命名模板渲染后的基本名（GUI 端已算好并消毒）；缺省时回退为源文件名。
            string baseName = string.IsNullOrWhiteSpace(outputBaseName)
                ? sourceFileNameWithoutExtension
                : outputBaseName;

            if (string.IsNullOrWhiteSpace(imageExtension))
            {
                imageExtension = ".JPG";
            }

            string? subDir = null;
            // The caller's input root is the explicit preservation request.
            // Core must not consult a GUI setting that can override a CLI flag.
            if (!string.IsNullOrEmpty(inputDirectory))
            {
                subDir = PathHelper.GetRelativeSubDirectory(inputDirectory, sourcePath);
            }

            string targetDir = subDir != null ? Path.Combine(outputDirectory, subDir) : outputDirectory;
            Directory.CreateDirectory(targetDir);

            string imageOutputPath;
            string videoOutputPath;

            if (overwriteExisting)
            {
                // 覆盖模式：使用确定性文件名（与源同名 baseName），后续写入前删除旧文件。
                imageOutputPath = Path.Combine(targetDir, $"{baseName}{imageExtension}");
                videoOutputPath = Path.Combine(targetDir, $"{baseName}{videoExtension}");
            }
            else
            {
                imageOutputPath = GetSplitNoOverwriteCandidate(targetDir, $"{baseName}{imageExtension}");
                videoOutputPath = GetSplitNoOverwriteCandidate(targetDir, $"{baseName}{videoExtension}");
            }

            string sourceFullPath = Path.GetFullPath(sourcePath);

            // 防止输出文件覆盖掉正在读取的源文件
            if (string.Equals(Path.GetFullPath(imageOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                imageOutputPath = Path.Combine(outputDirectory, $"{baseName}_image{imageExtension}");
            }

            if (string.Equals(Path.GetFullPath(videoOutputPath), sourceFullPath, StringComparison.OrdinalIgnoreCase))
            {
                videoOutputPath = Path.Combine(outputDirectory, $"{baseName}_video{videoExtension}");
            }

            return (imageOutputPath, videoOutputPath);
        }

        private static string GetSplitNoOverwriteCandidate(string targetDirectory, string fileName)
        {
            string candidate = Path.Combine(targetDirectory, fileName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;

            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            for (int suffix = 2; suffix < 999; suffix++)
            {
                candidate = Path.Combine(targetDirectory, $"{nameWithoutExtension} ({suffix}){extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(
                targetDirectory,
                $"{nameWithoutExtension} ({Guid.NewGuid():N}){extension}");
        }

    }
}
