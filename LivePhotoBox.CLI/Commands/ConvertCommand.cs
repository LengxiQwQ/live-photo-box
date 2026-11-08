using LivePhotoBox.Cli.Infrastructure;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Services;
using System;
using System.CommandLine;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Cli.Commands;

/// <summary>
/// Rebuilt-only standalone media conversion.  This command deliberately calls
/// the Native image/video converters and never starts an external media CLI.
/// </summary>
internal static class ConvertCommand
{
    public static Command Create()
    {
        var source = new Argument<FileInfo>("source")
        {
            Description = "Source image or video (.jpg/.jpeg/.heic/.heif/.mp4/.mov)."
        };
        var output = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Destination path. The extension selects the target container."
        };
        var codec = new Option<string>("--codec")
        {
            DefaultValueFactory = _ => "copy",
            Description = "Video codec: copy (when display geometry permits), h264, or hevc. Ignored for images."
        };
        var overwrite = new Option<bool>("--overwrite", "-w")
        {
            Description = "Replace an existing destination file."
        };

        var command = new Command(
            "convert",
            "Convert a standalone image/video through the Rebuilt Native media pipeline.\n" +
            "Examples:\n" +
            "  lpb convert input.mov -o output.mp4 --codec h264\n" +
            "  lpb convert input.heic -o output.jpg\n" +
            "  lpb convert input.jpg -o output.heic")
        {
            source,
            output,
            codec,
            overwrite
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            FileInfo sourceFile = parseResult.GetValue(source)!;
            FileInfo? outputFile = parseResult.GetValue(output);
            string codecValue = parseResult.GetValue(codec)!;
            bool allowOverwrite = parseResult.GetValue(overwrite);

            if (outputFile == null)
            {
                CliConsole.WriteErrorLine("Error: Required option '--output' was not provided.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                int exitCode = await ProcessingPipelineRouter.RunRebuiltAsync(
                    "convert",
                    () => RunAsync(sourceFile, outputFile, codecValue, allowOverwrite, cancellationToken));
                Environment.ExitCode = exitCode;
            }
            catch (Exception ex)
            {
                CliConsole.WriteErrorLine($"Error: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private static async Task<int> RunAsync(
        FileInfo sourceFile,
        FileInfo outputFile,
        string codecValue,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return 130;
        string sourcePath = Path.GetFullPath(sourceFile.FullName);
        string outputPath = Path.GetFullPath(outputFile.FullName);
        if (!sourceFile.Exists)
        {
            CliConsole.WriteErrorLine($"Error: File not found: {sourcePath}");
            return 1;
        }

        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            CliConsole.WriteErrorLine("Error: Source and output paths must be different.");
            return 1;
        }

        if (File.Exists(outputPath) && !overwrite)
        {
            CliConsole.WriteErrorLine($"Error: Output already exists: {outputPath}. Use --overwrite to replace it.");
            return 1;
        }

        string outputExtension = Path.GetExtension(outputPath);
        string sourceExtension = Path.GetExtension(sourcePath);
        if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(outputPath)!) is { } directoryFailure)
        { CliConsole.WriteErrorLine(directoryFailure.Message); return 1; }
        using var sourceRead = SourceReadAttempt.Open(sourcePath);
        if (!sourceRead.Succeeded || !sourceRead.TryGetLength(out long sourceLength, out _))
        { CliConsole.WriteErrorLine($"Cannot read source (system error {sourceRead.Error})."); return 1; }

        if (TryGetImageContainer(outputExtension, out ImageContainer imageContainer))
        {
            if (!TryGetImageContainer(sourceExtension, out _))
            {
                CliConsole.WriteErrorLine("Error: Image conversion requires an image source.");
                return 1;
            }

            var inspection = await NativeMediaService.TryInspectMediaWithPlanAsync(sourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (inspection.Failure is { } inspectionFailure) return await SaveUnconvertedAsync(sourcePath, outputPath, inspectionFailure, cancellationToken).ConfigureAwait(false);
            using var inspected = inspection.Source!;
            SourceMediaFacts facts = inspected.Facts;
            if (!facts.PrimaryImage.IsPresent)
            {
                CliConsole.WriteErrorLine("Error: Native inspection did not identify a primary image.");
                return 1;
            }

            var result = await new ImageConverter().TryConvertAsync(new ImageConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = sourcePath,
                    Kind = MediaArtifactKind.PrimaryImage,
                    ImageContainer = facts.PrimaryImage.Container,
                    ImageCodec = facts.PrimaryImage.Container == ImageContainer.Heic ? ImageCodec.Hevc : ImageCodec.Jpeg,
                    ByteLength = sourceLength
                },
                TargetContainer = imageContainer,
                TargetDirectory = Path.GetDirectoryName(outputPath)!,
                Quality = 95,
                PreservationPolicy = PreservationPolicy.BestEffort,
                TrustedSourceFacts = ImageConversionEligibility.ToConversionFacts(facts)
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Success || result.OutputArtifact == null)
            {
                return await SaveUnconvertedAsync(sourcePath, outputPath, new InvalidDataException(result.ErrorMessage ?? "Native image conversion did not complete."), cancellationToken).ConfigureAwait(false);
            }

            bool isolated = result.ExecutionRecord.Truth.HdrGainMap is ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost ||
                result.ExecutionRecord.Truth.ColorIcc is ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost ||
                result.ExecutionRecord.Truth.Audio is ConversionComponentOutcome.Reencoded or ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost;
            string actualPath = isolated ? IsolatedPath(outputPath) : outputPath;
            if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(actualPath)!) is { } destinationFailure)
            { CliConsole.WriteErrorLine(destinationFailure.Message); return 1; }
            var publicationFailure = await TryPublishConvertedArtifactAsync(outputPath, actualPath, !isolated && overwrite,
                result.OutputArtifact, null, cancellationToken).ConfigureAwait(false);
            if (publicationFailure is { } failure) { CliConsole.WriteErrorLine(failure.Message); return failure is OperationCanceledException ? 130 : 1; }
            if (isolated)
            { CliConsole.WriteLine($"Compatibility output isolated: {actualPath}. HDR, color or audio changed; review conversion details.", CliConsole.Highlight); return 2; }
            CliConsole.WriteLine($"Converted image: {outputPath}", CliConsole.Success);
            return 0;
        }

        if (TryGetVideoContainer(outputExtension, out VideoContainer videoContainer))
        {
            if (!TryGetVideoContainer(sourceExtension, out _))
            {
                CliConsole.WriteErrorLine("Error: Video conversion requires a video source.");
                return 1;
            }

            VideoCodec targetCodec = codecValue.Trim().ToLowerInvariant() switch
            { "copy" => VideoCodec.Copy, "h264" or "avc" => VideoCodec.H264, "hevc" or "h265" => VideoCodec.Hevc, _ => VideoCodec.Unknown };
            if (targetCodec == VideoCodec.Unknown) { CliConsole.WriteErrorLine("Video codec must be copy, h264, or hevc."); return 1; }

            var converter = new VideoConverter();
            var probe = await NativeMediaService.TryProbeVideoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (!probe.Succeeded) return await SaveUnconvertedAsync(sourcePath, outputPath, new InvalidDataException(probe.Diagnostic ?? "Video inspection did not complete."), cancellationToken).ConfigureAwait(false);
            VideoFacts facts = probe.Facts!;
            var result = await converter.TryConvertAsync(new VideoConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = sourcePath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = facts.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
                    VideoContainer = facts.Container,
                    VideoCodec = facts.Codec,
                    ByteLength = sourceLength
                },
                TargetContainer = videoContainer,
                // A container copy cannot retire an aperture or normalize its
                // pixels. Use the existing encoder once, before publication,
                // just as the shared neutral preparation stage does.
                TargetCodec = targetCodec == VideoCodec.Copy &&
                    (facts.HasDisplayAperture || facts.RotationDegrees != 0 || facts.Reflection != 0)
                    ? facts.Codec : targetCodec,
                TargetDirectory = Path.GetDirectoryName(outputPath)!,
                Crf = 23,
                TrustedSourceFacts = new VideoConversionSourceFacts
                {
                    Container = facts.Container,
                    Codec = facts.Codec,
                    HasAudio = facts.HasAudio,
                    RotationDegrees = facts.RotationDegrees,
                    DurationSeconds = facts.DurationSeconds
                }
            }, cancellationToken).ConfigureAwait(false);

            if (!result.Success || result.OutputArtifact == null)
            {
                return await SaveUnconvertedAsync(sourcePath, outputPath, new InvalidDataException(result.ErrorMessage ?? "Native video conversion did not complete."), cancellationToken).ConfigureAwait(false);
            }

            bool isolated = result.ExecutionRecord.Truth.HdrGainMap is ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost ||
                result.ExecutionRecord.Truth.ColorIcc is ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost ||
                result.ExecutionRecord.Truth.Audio is ConversionComponentOutcome.Reencoded or ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost;
            string actualPath = isolated ? IsolatedPath(outputPath) : outputPath;
            if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(actualPath)!) is { } destinationFailure)
            { CliConsole.WriteErrorLine(destinationFailure.Message); return 1; }
            var publicationFailure = await TryPublishConvertedArtifactAsync(outputPath, actualPath, !isolated && overwrite,
                result.OutputArtifact, null, cancellationToken).ConfigureAwait(false);
            if (publicationFailure is { } failure) { CliConsole.WriteErrorLine(failure.Message); return failure is OperationCanceledException ? 130 : 1; }
            if (isolated)
            { CliConsole.WriteLine($"Compatibility output isolated: {actualPath}. HDR, color or audio changed; review conversion details.", CliConsole.Highlight); return 2; }
            CliConsole.WriteLine(
                $"Converted video: {outputPath} ({result.ExecutionRecord.InputCodec} -> {result.ExecutionRecord.OutputCodec}, " +
                $"{result.ExecutionRecord.Truth.ActualOperationKind})",
                CliConsole.Success);
            return 0;
        }

        CliConsole.WriteErrorLine("Error: Output extension must be .jpg, .jpeg, .heic, .heif, .mp4, or .mov.");
        return 1;
    }

    private static string IsolatedPath(string requested)
    {
        string directory = ProductExportAssessment.IsolationDirectory(Path.GetDirectoryName(Path.GetFullPath(requested))!, Guid.NewGuid());
        string name = Path.GetFileNameWithoutExtension(requested), extension = Path.GetExtension(requested);
        string path = Path.Combine(directory, Path.GetFileName(requested));
        for (int index = 1; File.Exists(path) || Directory.Exists(path); index++) path = Path.Combine(directory, $"{name}_{index}{extension}");
        return path;
    }

    private static async Task<int> SaveUnconvertedAsync(string source, string requested, Exception failure, CancellationToken token)
    {
        if (token.IsCancellationRequested || failure is OperationCanceledException) return 130;
        string desired = Path.Combine(Path.GetDirectoryName(requested)!, Path.GetFileName(source));
        string isolated = IsolatedPath(desired);
        if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(isolated)!) is { } directoryFailure)
        { CliConsole.WriteErrorLine(directoryFailure.Message); return 1; }
        var artifact = new MediaArtifact { Path = source, Kind = TryGetVideoContainer(Path.GetExtension(source), out _) ? MediaArtifactKind.MotionVideo : MediaArtifactKind.PrimaryImage };
        var saved = await WindowsOutputArtifactPublisher.TryPublishCopyAsync(artifact, isolated, false, token).ConfigureAwait(false);
        if (saved.Failure is { } saveFailure) { CliConsole.WriteErrorLine(saveFailure.Message); return saveFailure is OperationCanceledException ? 130 : 1; }
        CliConsole.WriteLine($"Saved unchanged, conversion not completed: {isolated}. Reason: {failure.Message}", CliConsole.Highlight);
        return 2;
    }

    // Strict compatibility wrapper retained for callers/tests requesting exceptions.
    internal static async Task PublishConvertedArtifactAsync(string finalPath, bool overwriteExisting, MediaArtifact artifact,
        Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook, CancellationToken cancellationToken)
    {
        var failure = await TryPublishConvertedArtifactAsync(finalPath, finalPath, overwriteExisting, artifact, testHook, cancellationToken).ConfigureAwait(false);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task<Exception?> TryPublishConvertedArtifactAsync(string ownershipDestination, string finalPath,
        bool overwriteExisting, MediaArtifact artifact, Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook, CancellationToken token)
    {
        if (artifact.FileIdentity == null || artifact.Sha256 is not { Length: 64 })
            return new InvalidDataException("Native converter artifact is missing creating-handle identity or SHA-256 evidence; refusing pathname-only ownership.");
        string expectedDirectory = Path.GetDirectoryName(Path.GetFullPath(ownershipDestination))!;
        string artifactPath = Path.GetFullPath(artifact.Path);
        if (string.Equals(Path.GetFullPath(finalPath), artifactPath, StringComparison.OrdinalIgnoreCase))
            return new IOException("Final output must differ from the temporary converter artifact.");
        if (!string.Equals(expectedDirectory, Path.GetDirectoryName(artifactPath), StringComparison.OrdinalIgnoreCase))
            return new IOException("Converter output artifact must be in the requested output directory.");
        string name = Path.GetFileNameWithoutExtension(artifactPath);
        string[] prefixes = artifact.Kind switch { MediaArtifactKind.PrimaryImage => ["img-conv-", "img-hdr-conv-"], MediaArtifactKind.MotionVideo => ["vid-conv-"], _ => [] };
        bool valid = false;
        foreach (var prefix in prefixes) if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name[prefix.Length..], "N", out _)) valid = true;
        if (!valid) return new IOException($"Converter artifact '{artifactPath}' does not match the unique output-name shape for {artifact.Kind}.");
        WindowsOutputArtifactCopySource? owned = null;
        var publication = await WindowsOutputArtifactPublisher.TryPublishCopyAsync(artifact, finalPath, overwriteExisting, token,
            testHook, captured => owned = captured).ConfigureAwait(false);
        // No capture means that publication did not establish ownership of
        // the current pathname (for example a same-byte foreign replacement).
        // Preserve that object and the strict API's original refusal category.
        if (owned == null && publication.Failure != null && !token.IsCancellationRequested) return publication.Failure;
        owned ??= new WindowsOutputArtifactCopySource(artifactPath, artifact.FileIdentity, artifact.ByteLength, artifact.Sha256);
        Exception? cleanupFailure = null;
        using (var source = SourceReadAttempt.Open(owned.Path, share: 1, access: 0x80010080))
        {
            if (!source.Succeeded) cleanupFailure = new IOException($"Cannot retain converter output cleanup object (system error {source.Error}).");
            else if (!WindowsFileIdentity.TryCapture(source.Handle, out var identity, out int error) || !identity!.Matches(owned.Identity) || identity.IsReparsePoint || identity.LinkCount != 1)
                cleanupFailure = new IOException($"Converter output cleanup identity changed (system error {error}).");
            else if (!source.TryGetLength(out long length, out int lengthError)) cleanupFailure = new IOException($"Cannot verify converter cleanup length (system error {lengthError}).");
            else
            {
                var hash = MediaFileHashAttempt.ComputeHeld(source.Handle, length, CancellationToken.None);
                cleanupFailure = hash.Failure;
                if (cleanupFailure == null && (length != owned.ByteLength || !string.Equals(hash.Sha256, owned.Sha256, StringComparison.OrdinalIgnoreCase))) cleanupFailure = new IOException("Converter artifact changed content; retained without modification.");
                if (cleanupFailure == null)
                {
                    var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(source.Handle, owned.Identity, owned.Path, out _);
                    if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked)) cleanupFailure = new IOException($"Exact-object converter cleanup unproven ({proof}).");
                }
            }
        }
        return publication.Failure != null && cleanupFailure != null ? new AggregateException("Convert publication and cleanup failed.", publication.Failure, cleanupFailure) : publication.Failure ?? cleanupFailure;
    }

    private static VideoCodec ParseCodec(string value) => value.Trim().ToLowerInvariant() switch
    {
        "copy" => VideoCodec.Copy,
        "h264" or "avc" => VideoCodec.H264,
        "hevc" or "h265" => VideoCodec.Hevc,
        _ => throw new ArgumentException("Video codec must be copy, h264, or hevc.")
    };

    private static bool TryGetImageContainer(string extension, out ImageContainer container)
    {
        container = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageContainer.Jpeg,
            ".heic" or ".heif" => ImageContainer.Heic,
            _ => ImageContainer.Unknown
        };
        return container != ImageContainer.Unknown;
    }

    private static bool TryGetVideoContainer(string extension, out VideoContainer container)
    {
        container = extension.ToLowerInvariant() switch
        {
            ".mp4" => VideoContainer.Mp4,
            ".mov" => VideoContainer.Mov,
            _ => VideoContainer.Unknown
        };
        return container != VideoContainer.Unknown;
    }
}
