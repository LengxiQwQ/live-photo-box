using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly IReadOnlyDictionary<string, Route> Routes = new Dictionary<string, Route>(StringComparer.Ordinal)
    {
        ["apple-copy-mov-to-mp4"] = new(VideoContainer.Mp4, VideoCodec.Copy, PreservationPolicy.BestEffort),
        ["apple-copy-mov-same-container"] = new(VideoContainer.Mov, VideoCodec.Copy, PreservationPolicy.BestEffort),
        ["vivo-copy-mp4-to-mov"] = new(VideoContainer.Mov, VideoCodec.Copy, PreservationPolicy.BestEffort),
        ["vivo-copy-mp4-same-container"] = new(VideoContainer.Mp4, VideoCodec.Copy, PreservationPolicy.BestEffort),
        ["apple-hevc-to-h264-sdr"] = new(VideoContainer.Mov, VideoCodec.H264, PreservationPolicy.BestEffort),
        ["apple-hevc-to-h264-sdr-mp4"] = new(VideoContainer.Mp4, VideoCodec.H264, PreservationPolicy.BestEffort),
        ["vivo-h264-to-hevc-sdr"] = new(VideoContainer.Mp4, VideoCodec.Hevc, PreservationPolicy.BestEffort),
        ["huawei-main10-hlg-to-hevc-preservation"] = new(VideoContainer.Mp4, VideoCodec.Hevc, PreservationPolicy.Strict),
        ["huawei-main10-hlg-to-h264-preservation-required"] = new(VideoContainer.Mp4, VideoCodec.H264, PreservationPolicy.Strict)
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 1)
            {
                throw new ArgumentException("Usage: P5R5.RouteDriver <extract-huawei|route|failure> ...");
            }

            object report = args[0] switch
            {
                "extract-huawei" => await ExtractHuaweiAsync(args).ConfigureAwait(false),
                "route" => await RunRouteAsync(args).ConfigureAwait(false),
                "failure" => await RunFailureAsync(args).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown route-driver command '{args[0]}'.")
            };

            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"P5R5_ROUTE_DRIVER_ERROR: {ex}");
            return 1;
        }
    }

    private static async Task<object> ExtractHuaweiAsync(string[] args)
    {
        RequireCount(args, 5, "extract-huawei <parent> <workspace> <expected-sha256> <expected-size>");
        string parent = Path.GetFullPath(args[1]);
        string workspacePath = Path.GetFullPath(args[2]);
        string expectedHash = args[3].ToUpperInvariant();
        if (!long.TryParse(args[4], out long expectedSize) || expectedSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Huawei parent size must be positive.");
        }

        EnsureEmptyDirectory(workspacePath);
        long parentBytes = new FileInfo(parent).Length;
        string parentHash = await HashFileAsync(parent).ConfigureAwait(false);
        if (parentBytes != expectedSize || !string.Equals(parentHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Hash-locked Huawei parent mismatch: bytes={parentBytes}, sha256={parentHash}.");
        }

        using InspectedSource inspected = await new SourceInspector()
            .InspectWithPlanAsync(parent, null, CancellationToken.None)
            .ConfigureAwait(false);
        if (!string.Equals(inspected.Facts.PrimarySha256, parentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native inspector did not bind extraction to the hash-locked parent.");
        }

        VideoFacts facts = inspected.Facts.MotionVideo is { IsPresent: true } motion
            ? motion
            : throw new InvalidDataException("Native inspection found no canonical Huawei motion video.");
        ValidateRange(facts.ByteOffset, facts.ByteLength, parentBytes);
        string rangeHash = await HashRangeAsync(parent, facts.ByteOffset, facts.ByteLength).ConfigureAwait(false);

        await using var outputWorkspace = new PersistentWorkspace(workspacePath);
        ExtractedMediaBundle bundle = await new SourceExtractor()
            .ExtractAsync(inspected.ExtractionPlan, parent, null, outputWorkspace, CancellationToken.None)
            .ConfigureAwait(false);
        MediaArtifact artifact = bundle.MotionVideo
            ?? throw new InvalidDataException("Canonical Huawei extraction returned no motion video.");
        string extractedHash = await outputWorkspace.ComputeFileSha256Async(artifact.Path).ConfigureAwait(false);
        long extractedBytes = new FileInfo(artifact.Path).Length;
        if (artifact.SourceOffset != facts.ByteOffset || artifact.ByteLength != facts.ByteLength ||
            extractedBytes != facts.ByteLength || !string.Equals(rangeHash, extractedHash, StringComparison.OrdinalIgnoreCase) ||
            (artifact.Sha256 is not null && !string.Equals(artifact.Sha256, extractedHash, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Extracted Huawei bytes do not exactly match the Native-inspected parent range.");
        }

        return new
        {
            command = "extract-huawei",
            parentPath = parent,
            parentBytes,
            parentSha256 = parentHash,
            protocol = inspected.Facts.Protocol,
            range = new { offset = facts.ByteOffset, length = facts.ByteLength, facts.SourceIndex, sha256 = rangeHash },
            artifact.Path,
            artifactBytes = extractedBytes,
            artifactSha256 = extractedHash,
            nativeFacts = new
            {
                facts.Container,
                facts.Codec,
                facts.Width,
                facts.Height,
                facts.RotationDegrees,
                facts.DurationSeconds,
                facts.Fps,
                facts.HasAudio
            },
            rangeAndExtractedBytesIdentical = true
        };
    }

    private static async Task<object> RunRouteAsync(string[] args)
    {
        RequireCount(args, 4, "route <frozen-route-id> <source> <empty-output-directory>");
        if (!Routes.TryGetValue(args[1], out Route? route))
        {
            throw new ArgumentException($"Route '{args[1]}' is not part of frozen P5-R5-v1.");
        }

        return await ConvertAsync(args[1], Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), route, 0, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<object> RunFailureAsync(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Usage: P5R5.RouteDriver failure <frozen-failure-id> ...");
        string caseId = args[1];
        switch (caseId)
        {
            case "target-fps-positive":
                RequireCount(args, 4, "failure target-fps-positive <source> <empty-output-directory>");
                return await ConvertAsync(
                    caseId, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]),
                    new Route(VideoContainer.Mp4, VideoCodec.Copy, PreservationPolicy.BestEffort),
                    targetFps: 30, CancellationToken.None, preDispatchDeclaration: new VideoConversionSourceFacts
                    {
                        Container = VideoContainer.Mov,
                        Codec = VideoCodec.Hevc
                    })
                    .ConfigureAwait(false);
            case "ambiguous-or-malformed-preservation-facts":
                RequireCount(args, 4, "failure ambiguous-or-malformed-preservation-facts <mutated-copy> <empty-output-directory>");
                return await ConvertAsync(
                    caseId, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]),
                    new Route(VideoContainer.Mp4, VideoCodec.Hevc, PreservationPolicy.Strict), 0, CancellationToken.None)
                    .ConfigureAwait(false);
            case "cancellation":
                RequireCount(args, 4, "failure cancellation <canonical-huawei-video> <empty-output-directory>");
                return await RunCancellationAsync(caseId, Path.GetFullPath(args[2]), Path.GetFullPath(args[3])).ConfigureAwait(false);
            case "output-validation-or-publish-failure":
                RequireCount(args, 4, "failure output-validation-or-publish-failure <copy-source> <empty-workspace>");
                return await RunPublishFailureAsync(caseId, Path.GetFullPath(args[2]), Path.GetFullPath(args[3])).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Failure case '{caseId}' is not part of frozen P5-R5-v1.");
        }
    }

    private static async Task<object> ConvertAsync(
        string id,
        string sourcePath,
        string outputDirectory,
        Route route,
        int targetFps,
        CancellationToken cancellationToken,
        VideoConversionSourceFacts? preDispatchDeclaration = null)
    {
        EnsureEmptyDirectory(outputDirectory);
        var converter = new VideoConverter();
        MediaArtifact artifact;
        VideoFacts? sourceFacts = null;
        if (preDispatchDeclaration is not null)
        {
            artifact = new MediaArtifact
            {
                Path = sourcePath,
                Kind = MediaArtifactKind.MotionVideo,
                MimeType = "video/quicktime",
                VideoContainer = preDispatchDeclaration.Container,
                VideoCodec = preDispatchDeclaration.Codec,
                ByteLength = new FileInfo(sourcePath).Length
            };
        }
        else
        {
            sourceFacts = await converter.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            artifact = CreateArtifact(sourcePath, sourceFacts);
        }

        VideoConversionResult result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = route.Container,
            TargetCodec = route.Codec,
            TargetDirectory = outputDirectory,
            PreservationPolicy = route.Policy,
            TargetFps = targetFps
        }, cancellationToken).ConfigureAwait(false);

        return new
        {
            command = "conversion",
            id,
            sourcePath,
            sourceBytes = new FileInfo(sourcePath).Length,
            sourceSha256 = await HashFileAsync(sourcePath).ConfigureAwait(false),
            sourceFacts,
            request = new { targetContainer = route.Container, targetCodec = route.Codec, route.Policy, targetFps },
            success = result.Success,
            error = result.ErrorMessage,
            outputArtifact = await ArtifactReportAsync(result.OutputArtifact).ConfigureAwait(false),
            executionRecord = result.ExecutionRecord,
            directoryEntries = Directory.GetFileSystemEntries(outputDirectory).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ownedResidues = FindOwnedResidues(outputDirectory)
        };
    }

    private static async Task<object> RunCancellationAsync(string id, string sourcePath, string outputDirectory)
    {
        EnsureEmptyDirectory(outputDirectory);
        var converter = new VideoConverter();
        VideoFacts facts = await converter.ProbeAsync(sourcePath).ConfigureAwait(false);
        MediaArtifact artifact = CreateArtifact(sourcePath, facts);
        using var cancellation = new CancellationTokenSource();
        Task<VideoConversionResult> conversion = converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Hevc,
            TargetDirectory = outputDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        }, cancellation.Token);

        string[] stagePatterns = ["lpb-video-sidecar-stage-a*.tmp", "lpb-video-sidecar-stage-b*.tmp"];
        bool cancelledAfterStagingStarted = false;
        string[] observedStaging = [];
        while (!conversion.IsCompleted)
        {
            observedStaging = stagePatterns
                .SelectMany(pattern => Directory.EnumerateFiles(outputDirectory, pattern, SearchOption.TopDirectoryOnly))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (observedStaging.Length > 0)
            {
                cancelledAfterStagingStarted = true;
                cancellation.Cancel();
                break;
            }
            await Task.Delay(5).ConfigureAwait(false);
        }

        bool operationCancelled = false;
        bool operationSucceeded = false;
        string? error = null;
        try
        {
            VideoConversionResult result = await conversion.ConfigureAwait(false);
            operationSucceeded = result.Success;
            error = result.ErrorMessage;
        }
        catch (OperationCanceledException ex)
        {
            operationCancelled = true;
            error = ex.GetType().Name;
        }

        string[] entries = Directory.GetFileSystemEntries(outputDirectory).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new
        {
            command = "cancellation",
            id,
            sourcePath,
            sourceSha256 = await HashFileAsync(sourcePath).ConfigureAwait(false),
            sourceFacts = facts,
            cancelledAfterStagingStarted,
            observedStaging,
            operationCancelled,
            operationSucceeded,
            error,
            directoryEntries = entries,
            ownedResidues = FindOwnedResidues(outputDirectory),
            noFinalArtifact = !entries.Any(path => Path.GetFileName(path).StartsWith("vid-conv-", StringComparison.Ordinal)),
            stagingRemoved = !entries.Any(path => Path.GetFileName(path).StartsWith("lpb-video-sidecar-stage-", StringComparison.Ordinal))
        };
    }

    private static async Task<object> RunPublishFailureAsync(string id, string sourcePath, string outputDirectory)
    {
        EnsureEmptyDirectory(outputDirectory);
        string converterDirectory = Path.Combine(outputDirectory, "canonical-conversion");
        string facadeDirectory = Path.Combine(outputDirectory, "caller-owned-destination");
        EnsureEmptyDirectory(converterDirectory);
        EnsureEmptyDirectory(facadeDirectory);
        var converter = new VideoConverter();
        VideoFacts sourceFacts = await converter.ProbeAsync(sourcePath).ConfigureAwait(false);
        MediaArtifact artifact = CreateArtifact(sourcePath, sourceFacts);
        VideoConversionResult conversion = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = converterDirectory,
            PreservationPolicy = PreservationPolicy.BestEffort
        }).ConfigureAwait(false);

        string callerPath = Path.Combine(facadeDirectory, "caller-owned.mp4");
        byte[] originalBytes = [0x43, 0x41, 0x4C, 0x4C, 0x45, 0x52, 0x01, 0x02];
        await File.WriteAllBytesAsync(callerPath, originalBytes).ConfigureAwait(false);
        string originalHash = await HashFileAsync(callerPath).ConfigureAwait(false);
        VideoTranscodeService.TranscodeResult serviceResult = await VideoTranscodeService.RemuxAsync(sourcePath, callerPath)
            .ConfigureAwait(false);
        string finalCallerHash = await HashFileAsync(callerPath).ConfigureAwait(false);
        string[] facadeEntries = Directory.GetFileSystemEntries(facadeDirectory).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        return new
        {
            command = "output-validation-or-publish-failure",
            id,
            sourcePath,
            sourceSha256 = await HashFileAsync(sourcePath).ConfigureAwait(false),
            sourceFacts,
            canonicalConversion = new
            {
                conversion.Success,
                conversion.ErrorMessage,
                outputArtifact = await ArtifactReportAsync(conversion.OutputArtifact).ConfigureAwait(false),
                executionRecord = conversion.ExecutionRecord,
                directoryEntries = Directory.GetFileSystemEntries(converterDirectory).Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                ownedResidues = FindOwnedResidues(converterDirectory)
            },
            publishFailure = new
            {
                serviceResult.Success,
                serviceResult.WasRemux,
                serviceResult.ErrorMessage,
                callerPath,
                originalBytes = Convert.ToHexString(originalBytes),
                originalSha256 = originalHash,
                finalCallerSha256 = finalCallerHash,
                callerBytesUnchanged = string.Equals(originalHash, finalCallerHash, StringComparison.Ordinal),
                directoryEntries = facadeEntries,
                ownedResidues = FindOwnedResidues(facadeDirectory),
                noConverterArtifact = !facadeEntries.Any(path => Path.GetFileName(path).StartsWith("vid-conv-", StringComparison.Ordinal))
            }
        };
    }

    private static MediaArtifact CreateArtifact(string sourcePath, VideoFacts facts) => new()
    {
        Path = sourcePath,
        Kind = MediaArtifactKind.MotionVideo,
        MimeType = facts.Container == VideoContainer.Mov ? "video/quicktime" : "video/mp4",
        VideoContainer = facts.Container,
        VideoCodec = facts.Codec,
        ByteLength = new FileInfo(sourcePath).Length
    };

    private static async Task<object?> ArtifactReportAsync(MediaArtifact? artifact)
    {
        if (artifact is null || !File.Exists(artifact.Path)) return null;
        return new
        {
            artifact.Path,
            artifact.Kind,
            artifact.MimeType,
            artifact.VideoContainer,
            artifact.VideoCodec,
            artifact.ByteLength,
            sha256 = await HashFileAsync(artifact.Path).ConfigureAwait(false)
        };
    }

    private static string[] FindOwnedResidues(string root)
    {
        string[] prefixes = ["lpb-video-sidecar-stage-", "lpb-video-sidecar-", "lpb-transcode-", "vid-conv-"];
        return Directory.GetFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => prefixes.Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)))
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnsureEmptyDirectory(string path)
    {
        if (File.Exists(path)) throw new IOException($"Expected a directory, found a file: {path}");
        if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length != 0)
        {
            throw new IOException($"Refusing to reuse non-empty create-only route workspace: {path}");
        }
        Directory.CreateDirectory(path);
    }

    private static void RequireCount(string[] args, int expected, string usage)
    {
        if (args.Length != expected) throw new ArgumentException($"Usage: P5R5.RouteDriver {usage}");
    }

    private static void ValidateRange(long offset, long length, long parentSize)
    {
        if (offset < 0 || length <= 0 || offset > parentSize || length > parentSize - offset)
        {
            throw new InvalidDataException("Native inspector returned a range outside the hash-locked parent.");
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static async Task<string> HashRangeAsync(string path, long offset, long length)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Parent ended before the inspected video range was read.");
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed record Route(VideoContainer Container, VideoCodec Codec, PreservationPolicy Policy);

    private sealed class PersistentWorkspace(string rootDirectory) : IMediaWorkspace
    {
        private readonly Dictionary<string, int> _allocations = new(StringComparer.OrdinalIgnoreCase);
        public string RootDirectory { get; } = rootDirectory;

        public string AllocateFilePath(string prefix, string extension)
        {
            string sanitized = string.Concat(prefix.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' ? '_' : ch));
            string suffixExtension = extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
            int ordinal = _allocations.TryGetValue(sanitized, out int previous) ? previous + 1 : 1;
            _allocations[sanitized] = ordinal;
            string suffix = ordinal == 1 ? string.Empty : $"-{ordinal:D2}";
            string path = Path.Combine(RootDirectory, sanitized + suffix + suffixExtension);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"Refusing to overwrite extraction artifact: {path}");
            return path;
        }

        public async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        public Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default) =>
            AssertSourceUnmodifiedCoreAsync(sourcePath, expectedSha256, cancellationToken);

        private static async Task AssertSourceUnmodifiedCoreAsync(string sourcePath, string expected, CancellationToken token)
        {
            string actual = await HashFileAsync(sourcePath).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Source immutability check failed during canonical extraction.");
            }
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
