using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

/// <summary>
/// W4 tests use real Windows filesystem operations in the fixed p2-w4
/// workspace.  Synthetic source bytes are used only where the attack is on
/// transaction ownership rather than media parsing; the final test exercises
/// the Inspector-issued production plan with a cached Apple sample.
/// </summary>
public sealed class ExtractorW4AdversarialTests
{
    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "W4")]
    public async Task TempReplacementAfterFlushBeforePublish_FailsAndPreservesThirdPartyFile()
    {
        using var workspace = new W4EvidenceWorkspace("temp-replacement-before-publish");
        (string sourcePath, SourceMediaFacts facts, byte[] sourceBytes) = await CreateSyntheticInputAsync(workspace);
        var state = new BarrierState(workspace.RootDirectory, [0xFA, 0xCE, 0x01, 0x02]);
        nint callback = TempPublishCallback();
        GCHandle stateHandle = GCHandle.Alloc(state);

        ExtractionException exception;
        try
        {
            exception = await Assert.ThrowsAsync<ExtractionException>(() =>
                TestFactsExtractor.ExtractAsync(
                    facts,
                    sourcePath,
                    null,
                    workspace,
                    context => TestNativeHarness.SetExtractorFault(context,
                        NativeExtractorFault.TempPublishBarrier,
                        targetArtifact: 0,
                        triggerAfterBytes: 0,
                        callback,
                        GCHandle.ToIntPtr(stateHandle))));
        }
        finally
        {
            stateHandle.Free();
        }

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, exception.Category);
        Assert.Null(state.CallbackError);
        Assert.True(state.CallbackInvoked > 0, "The post-flush deterministic barrier did not run.");
        Assert.NotNull(state.OriginalTempPath);
        Assert.NotNull(state.ReplacementPath);
        Assert.True(File.Exists(state.OriginalTempPath), "The third-party replacement at the old temp path was deleted.");
        Assert.Equal(state.Sentinel, await File.ReadAllBytesAsync(state.OriginalTempPath));
        Assert.False(File.Exists(state.ReplacementPath), "The transaction-owned moved temp file was not removed by handle disposition.");
        AssertNoPublishedArtifactsExcept(workspace.RootDirectory, state.OriginalTempPath);
        Assert.Equal(Sha(sourceBytes), Sha(await File.ReadAllBytesAsync(sourcePath)));

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(TempReplacementAfterFlushBeforePublish_FailsAndPreservesThirdPartyFile),
            classification = "real-filesystem deterministic barrier",
            result = exception.Category.ToString(),
            error = exception.Message,
            source = DescribeFile(sourcePath),
            originalTempPath = state.OriginalTempPath,
            replacementPath = state.ReplacementPath,
            outputFiles = ListFiles(workspace.RootDirectory),
            sentinelSha256 = Sha(state.Sentinel)
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "W4")]
    public async Task DestinationCreatedAfterPreflight_IsRejectedAndPreserved()
    {
        using var workspace = new W4EvidenceWorkspace("destination-race");
        (string sourcePath, SourceMediaFacts facts, _) = await CreateSyntheticInputAsync(workspace);
        string destinationPath = string.Empty;
        var state = new BarrierState(workspace.RootDirectory, [0xDE, 0xAD, 0xBE, 0xEF]);

        workspace.OnAllocated = (prefix, path) =>
        {
            if (prefix.Equals("motion", StringComparison.OrdinalIgnoreCase))
            {
                destinationPath = path;
            }
        };

        nint callback = DestinationRaceCallback();
        GCHandle stateHandle = GCHandle.Alloc(state);
        ExtractionException exception;
        try
        {
            exception = await Assert.ThrowsAsync<ExtractionException>(() =>
                TestFactsExtractor.ExtractAsync(
                    facts,
                    sourcePath,
                    null,
                    workspace,
                    context =>
                    {
                        state.DestinationPath = destinationPath;
                        TestNativeHarness.SetExtractorFault(context,
                            NativeExtractorFault.None,
                            targetArtifact: 0,
                            triggerAfterBytes: 0,
                            callback,
                            GCHandle.ToIntPtr(stateHandle));
                    }));
        }
        finally
        {
            stateHandle.Free();
        }

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, exception.Category);
        Assert.Null(state.CallbackError);
        Assert.True(state.CallbackInvoked > 0);
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(state.Sentinel, await File.ReadAllBytesAsync(destinationPath));
        Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "primary.jpg")));
        AssertNoTemporaryOrPublishedArtifacts(workspace.RootDirectory, destinationPath);

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(DestinationCreatedAfterPreflight_IsRejectedAndPreserved),
            classification = "real-filesystem deterministic destination race",
            result = exception.Category.ToString(),
            destination = DescribeFile(destinationPath),
            outputFiles = ListFiles(workspace.RootDirectory)
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "W4")]
    public async Task PostPublishReplacement_FailsIdentityAndHashValidationAndPreservesReplacement()
    {
        using var workspace = new W4EvidenceWorkspace("post-publish-replacement");
        (string sourcePath, SourceMediaFacts facts, _) = await CreateSyntheticInputAsync(workspace);
        var state = new BarrierState(workspace.RootDirectory, [0xBA, 0xAD, 0xF0, 0x0D]);
        nint callback = PostPublishCallback();
        GCHandle stateHandle = GCHandle.Alloc(state);

        ExtractionException exception;
        try
        {
            exception = await Assert.ThrowsAsync<ExtractionException>(() =>
                TestFactsExtractor.ExtractAsync(
                    facts,
                    sourcePath,
                    null,
                    workspace,
                    context =>
                    {
                        state.DestinationPath = workspace.AllocatedPaths["primary"];
                        TestNativeHarness.SetExtractorFault(context,
                            NativeExtractorFault.PostPublishBarrier,
                            targetArtifact: 0,
                            triggerAfterBytes: 0,
                            callback,
                            GCHandle.ToIntPtr(stateHandle));
                    }));
        }
        finally
        {
            stateHandle.Free();
        }

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, exception.Category);
        Assert.Null(state.CallbackError);
        Assert.True(state.CallbackInvoked > 0);
        Assert.NotNull(state.OriginalPublishedPath);
        Assert.NotNull(state.ReplacementPath);
        Assert.True(File.Exists(state.ReplacementPath));
        Assert.Equal(state.Sentinel, await File.ReadAllBytesAsync(state.ReplacementPath));
        Assert.False(File.Exists(state.OriginalPublishedPath), "The owned published object was not removed by its handle.");
        AssertNoPublishedArtifactsExcept(workspace.RootDirectory, state.ReplacementPath);

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(PostPublishReplacement_FailsIdentityAndHashValidationAndPreservesReplacement),
            classification = "real-filesystem post-publish identity/hash race",
            result = exception.Category.ToString(),
            error = exception.Message,
            movedOwnedPath = state.OriginalPublishedPath,
            replacement = DescribeFile(state.ReplacementPath),
            outputFiles = ListFiles(workspace.RootDirectory),
            replacementSha256 = Sha(state.Sentinel)
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "W4")]
    public async Task HardlinkSourceDestinationAlias_IsRejectedWithoutMutation()
    {
        using var workspace = new W4EvidenceWorkspace("hardlink-alias");
        (string sourcePath, SourceMediaFacts facts, byte[] sourceBytes) = await CreateSyntheticInputAsync(workspace);
        string hardlinkPath = Path.Combine(workspace.RootDirectory, "hardlink-output.jpg");
        bool created = NativeFileSystem.CreateHardLink(hardlinkPath, sourcePath, out int error);
        if (!created)
        {
            WriteEvidence(workspace, "result.json", new
            {
                test = nameof(HardlinkSourceDestinationAlias_IsRejectedWithoutMutation),
                classification = "real-filesystem hardlink alias",
                environmentFailure = $"CreateHardLinkW failed with Win32 error {error}.",
                source = DescribeFile(sourcePath)
            });
        }
        Assert.True(created, $"Hardlink construction failed with Win32 error {error}; this is an environment failure, not a skip.");

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            TestFactsExtractor.ExtractNativeAsync(
                sourcePath,
                null,
                facts,
                hardlinkPath,
                null,
                null));

        Assert.Equal(ExtractionFailureCategory.InvalidAlias, exception.Category);
        Assert.Equal(Sha(sourceBytes), Sha(await File.ReadAllBytesAsync(sourcePath)));
        Assert.Equal(Sha(sourceBytes), Sha(await File.ReadAllBytesAsync(hardlinkPath)));

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(HardlinkSourceDestinationAlias_IsRejectedWithoutMutation),
            classification = "real-filesystem hardlink alias",
            result = exception.Category.ToString(),
            source = DescribeFile(sourcePath),
            alias = DescribeFile(hardlinkPath)
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "W4")]
    public async Task JunctionDestination_IsRejectedAsReparsePoint()
    {
        using var workspace = new W4EvidenceWorkspace("junction-reparse");
        (string sourcePath, SourceMediaFacts facts, _) = await CreateSyntheticInputAsync(workspace);
        string targetDirectory = Path.Combine(workspace.RootDirectory, "junction-target");
        string junctionPath = Path.Combine(workspace.RootDirectory, "junction-output");
        Directory.CreateDirectory(targetDirectory);
        (bool created, int exitCode, string output) = CreateJunction(junctionPath, targetDirectory);
        if (!created)
        {
            WriteEvidence(workspace, "result.json", new
            {
                test = nameof(JunctionDestination_IsRejectedAsReparsePoint),
                classification = "real-filesystem junction/reparse",
                environmentFailure = $"mklink /J failed with exit code {exitCode}: {output}"
            });
        }
        Assert.True(created, $"Junction construction failed with exit code {exitCode}: {output}; this is an environment failure, not a skip.");

        string outputPath = Path.Combine(junctionPath, "primary.jpg");
        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            TestFactsExtractor.ExtractNativeAsync(sourcePath, null, facts, outputPath, null, null));

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, exception.Category);
        Assert.Empty(Directory.GetFiles(targetDirectory, "*", SearchOption.AllDirectories));

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(JunctionDestination_IsRejectedAsReparsePoint),
            classification = "real-filesystem junction/reparse",
            result = exception.Category.ToString(),
            junction = junctionPath,
            targetDirectory,
            outputFiles = ListFiles(workspace.RootDirectory)
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "W4")]
    public async Task SymlinkDestination_IsRejectedAndTargetIsPreserved()
    {
        using var workspace = new W4EvidenceWorkspace("symlink-reparse");
        (string sourcePath, SourceMediaFacts facts, _) = await CreateSyntheticInputAsync(workspace);
        string targetPath = Path.Combine(workspace.RootDirectory, "foreign-target.jpg");
        string symlinkPath = Path.Combine(workspace.RootDirectory, "symlink-output.jpg");
        byte[] sentinel = [0x11, 0x22, 0x33, 0x44];
        await File.WriteAllBytesAsync(targetPath, sentinel);
        (bool created, int exitCode, string output) = CreateSymbolicFileLink(symlinkPath, targetPath);
        if (!created)
        {
            WriteEvidence(workspace, "result.json", new
            {
                test = nameof(SymlinkDestination_IsRejectedAndTargetIsPreserved),
                classification = "real-filesystem symbolic-link/reparse",
                environmentFailure = $"mklink failed with exit code {exitCode}: {output}",
                target = DescribeFile(targetPath)
            });
        }
        Assert.True(created, $"Symbolic-link construction failed with exit code {exitCode}: {output}; this is an environment failure, not a skip.");
        Assert.True(
            (File.GetAttributes(symlinkPath) & FileAttributes.ReparsePoint) != 0,
            "The filesystem did not expose the constructed destination as a reparse point.");

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            TestFactsExtractor.ExtractNativeAsync(sourcePath, null, facts, symlinkPath, null, null));

        Assert.Equal(ExtractionFailureCategory.OutputPublishFailed, exception.Category);
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(targetPath));
        Assert.True(File.Exists(symlinkPath));

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(SymlinkDestination_IsRejectedAndTargetIsPreserved),
            classification = "real-filesystem symbolic-link/reparse",
            result = exception.Category.ToString(),
            target = DescribeFile(targetPath),
            link = symlinkPath
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "W4")]
    public async Task AppleDualFile_ProductionPlanRemainsByteExactWithHandlePublish()
    {
        W2SampleSnapshot heic = await W2SampleEvidence.CacheAsync("苹果双文件.HEIC");
        W2SampleSnapshot mov = await W2SampleEvidence.CacheAsync("苹果双文件.MOV");
        string beforeHeicCache = await W2SampleEvidence.ComputeSha256Async(heic.CachePath);
        string beforeMovCache = await W2SampleEvidence.ComputeSha256Async(mov.CachePath);
        string beforeHeicDesign = await W2SampleEvidence.ComputeSha256Async(heic.OriginalPath);
        string beforeMovDesign = await W2SampleEvidence.ComputeSha256Async(mov.OriginalPath);

        using var workspace = new W4EvidenceWorkspace("apple-dual-real-sample");
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(heic.CachePath, mov.CachePath);
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            heic.CachePath,
            mov.CachePath,
            workspace);

        Assert.Equal(
            await W2IndependentMediaEvidence.ComputeSliceSha256Async(
                heic.CachePath,
                bundle.PrimaryImage.SourceOffset,
                bundle.PrimaryImage.ByteLength),
            bundle.PrimaryImage.Sha256);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(
            await W2IndependentMediaEvidence.ComputeSliceSha256Async(
                mov.CachePath,
                bundle.MotionVideo!.SourceOffset,
                bundle.MotionVideo.ByteLength),
            bundle.MotionVideo.Sha256);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.True(File.Exists(bundle.MotionVideo.Path));

        string afterHeicCache = await W2SampleEvidence.ComputeSha256Async(heic.CachePath);
        string afterMovCache = await W2SampleEvidence.ComputeSha256Async(mov.CachePath);
        string afterHeicDesign = await W2SampleEvidence.ComputeSha256Async(heic.OriginalPath);
        string afterMovDesign = await W2SampleEvidence.ComputeSha256Async(mov.OriginalPath);
        Assert.Equal(beforeHeicCache, afterHeicCache);
        Assert.Equal(beforeMovCache, afterMovCache);
        Assert.Equal(beforeHeicDesign, afterHeicDesign);
        Assert.Equal(beforeMovDesign, afterMovDesign);

        WriteEvidence(workspace, "result.json", new
        {
            test = nameof(AppleDualFile_ProductionPlanRemainsByteExactWithHandlePublish),
            classification = "production Inspector plan + real sample + independent slice SHA",
            sources = new
            {
                heic = new { before = beforeHeicCache, after = afterHeicCache, path = heic.CachePath },
                mov = new { before = beforeMovCache, after = afterMovCache, path = mov.CachePath },
                originalDesignHeic = new { before = beforeHeicDesign, after = afterHeicDesign },
                originalDesignMov = new { before = beforeMovDesign, after = afterMovDesign }
            },
            outputs = new
            {
                primary = DescribeFile(bundle.PrimaryImage.Path),
                motion = DescribeFile(bundle.MotionVideo.Path)
            }
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "W4")]
    public async Task ManagedPostNativeReplacement_IsPreservedByOwnershipCleanup()
    {
        W2SampleSnapshot heic = await W2SampleEvidence.CacheAsync("苹果双文件.HEIC");
        W2SampleSnapshot mov = await W2SampleEvidence.CacheAsync("苹果双文件.MOV");
        using var workspace = new W4ManagedMutationWorkspace("managed-post-native-replacement", heic.CachePath);
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(heic.CachePath, mov.CachePath);

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                heic.CachePath,
                mov.CachePath,
                workspace));

        Assert.Equal(ExtractionFailureCategory.SourceChanged, exception.Category);
        Assert.True(workspace.ReplacementInjected);
        Assert.NotNull(workspace.PrimaryOutputPath);
        Assert.True(
            File.Exists(workspace.PrimaryOutputPath),
            "Managed cleanup deleted a destination that had been replaced after Native returned.");
        Assert.Equal(workspace.Sentinel, await File.ReadAllBytesAsync(workspace.PrimaryOutputPath));
        Assert.NotNull(workspace.MotionOutputPath);
        Assert.False(File.Exists(workspace.MotionOutputPath), "The still-owned motion artifact was not cleaned up.");

        WriteEvidence(workspace.Inner, "result.json", new
        {
            test = nameof(ManagedPostNativeReplacement_IsPreservedByOwnershipCleanup),
            classification = "production plan + managed post-native real-filesystem replacement",
            result = exception.Category.ToString(),
            replacement = DescribeFile(workspace.PrimaryOutputPath),
            outputFiles = ListFiles(workspace.RootDirectory)
        });
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    [Trait("Category", "W4")]
    public async Task ManagedPostNativeSameByteReplacement_IsPreservedByOwnershipCleanup()
    {
        W2SampleSnapshot heic = await W2SampleEvidence.CacheAsync("苹果双文件.HEIC");
        W2SampleSnapshot mov = await W2SampleEvidence.CacheAsync("苹果双文件.MOV");
        using var workspace = new W4ManagedMutationWorkspace("managed-post-native-same-byte-replacement", heic.CachePath, replaceWithSameBytes: true);
        using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(heic.CachePath, mov.CachePath);

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                heic.CachePath,
                mov.CachePath,
                workspace));

        Assert.Equal(ExtractionFailureCategory.SourceChanged, exception.Category);
        Assert.True(workspace.ReplacementInjected);
        Assert.True(workspace.ReplacedWithSameBytes);
        Assert.NotNull(workspace.PrimaryOutputPath);
        Assert.True(File.Exists(workspace.PrimaryOutputPath));
        Assert.Equal(workspace.OriginalPublishedSha256, await workspace.ComputeFileSha256Async(workspace.PrimaryOutputPath));
        Assert.NotNull(workspace.MotionOutputPath);
        Assert.False(File.Exists(workspace.MotionOutputPath), "The still-owned motion artifact was not cleaned up.");

        WriteEvidence(workspace.Inner, "result.json", new
        {
            test = nameof(ManagedPostNativeSameByteReplacement_IsPreservedByOwnershipCleanup),
            classification = "production plan + same-byte different-file-object replacement",
            result = exception.Category.ToString(),
            replacement = DescribeFile(workspace.PrimaryOutputPath),
            outputFiles = ListFiles(workspace.RootDirectory)
        });
    }

    private static async Task<(string SourcePath, SourceMediaFacts Facts, byte[] SourceBytes)> CreateSyntheticInputAsync(
        W4EvidenceWorkspace workspace)
    {
        byte[] bytes = new byte[8192];
        Random.Shared.NextBytes(bytes);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        string sourcePath = Path.Combine(workspace.RootDirectory, "inputs", "source.jpg");
        await File.WriteAllBytesAsync(sourcePath, bytes);
        return (
            sourcePath,
            new SourceMediaFacts
            {
                Protocol = SourceProtocol.GoogleMicroVideoV1,
                PrimaryImage = new ImageFacts
                {
                    IsPresent = true,
                    Container = ImageContainer.Jpeg,
                    ByteOffset = 0,
                    ByteLength = 4096
                },
                MotionVideo = new VideoFacts
                {
                    IsPresent = true,
                    Container = VideoContainer.Mp4,
                    ByteOffset = 4096,
                    ByteLength = 4096,
                    SourceIndex = 0
                },
                PrimarySha256 = Sha(bytes)
            },
            bytes);
    }

    private static void AssertNoPublishedArtifacts(string root)
    {
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, path =>
            Path.GetFileName(path).Equals("primary.jpg", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("motion.mp4", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, path => Path.GetFileName(path).StartsWith(".lpb-", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertNoPublishedArtifactsExcept(string root, string? allowedPath)
    {
        string? allowedFullPath = allowedPath == null ? null : Path.GetFullPath(allowedPath);
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, path =>
        {
            if (allowedFullPath != null &&
                string.Equals(Path.GetFullPath(path), allowedFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string name = Path.GetFileName(path);
            return name.Equals("primary.jpg", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("motion.mp4", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".lpb-", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void AssertNoTemporaryOrPublishedArtifacts(string root, string foreignPath)
    {
        string foreignFullPath = Path.GetFullPath(foreignPath);
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, path =>
            !string.Equals(Path.GetFullPath(path), foreignFullPath, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).StartsWith(".lpb-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, path =>
            Path.GetFileName(path).Equals("primary.jpg", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] ListFiles(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static object DescribeFile(string? path) => new
    {
        path,
        exists = path != null && File.Exists(path),
        length = path != null && File.Exists(path) ? new FileInfo(path).Length : 0,
        sha256 = path != null && File.Exists(path) ? Sha(File.ReadAllBytes(path!)) : null
    };

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteEvidence(W4EvidenceWorkspace workspace, string name, object value)
    {
        File.WriteAllText(
            Path.Combine(workspace.RootDirectory, name),
            JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static nint TempPublishCallback()
    {
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnTempPublishBarrier;
            return (nint)callback;
        }
    }

    private static nint DestinationRaceCallback()
    {
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnDestinationRace;
            return (nint)callback;
        }
    }

    private static nint PostPublishCallback()
    {
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, int, ulong, void> callback = &OnPostPublishBarrier;
            return (nint)callback;
        }
    }

    private static BarrierState GetState(nint userData) =>
        (BarrierState)GCHandle.FromIntPtr(userData).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTempPublishBarrier(nint userData, int targetArtifact, ulong bytesProcessed)
    {
        BarrierState state = GetState(userData);
        if (Interlocked.CompareExchange(ref state.CallbackInvoked, 1, 0) != 0) return;
        try
        {
            string temp = Directory.GetFiles(state.Root, ".lpb-*.tmp", SearchOption.TopDirectoryOnly).Single();
            string moved = Path.Combine(state.Root, "attacker-moved-owned.tmp");
            File.Move(temp, moved, overwrite: false);
            File.WriteAllBytes(temp, state.Sentinel);
            state.OriginalTempPath = temp;
            state.ReplacementPath = moved;
        }
        catch (Exception ex)
        {
            state.CallbackError = ex.ToString();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDestinationRace(nint userData, int targetArtifact, ulong bytesProcessed)
    {
        BarrierState state = GetState(userData);
        if (Interlocked.CompareExchange(ref state.CallbackInvoked, 1, 0) != 0) return;
        try
        {
            File.WriteAllBytes(state.DestinationPath!, state.Sentinel);
        }
        catch (Exception ex)
        {
            state.CallbackError = ex.ToString();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPostPublishBarrier(nint userData, int targetArtifact, ulong bytesProcessed)
    {
        BarrierState state = GetState(userData);
        if (Interlocked.CompareExchange(ref state.CallbackInvoked, 1, 0) != 0) return;
        try
        {
            string published = state.DestinationPath!;
            string moved = Path.Combine(state.Root, "attacker-moved-published.bin");
            File.Move(published, moved, overwrite: false);
            File.WriteAllBytes(published, state.Sentinel);
            state.OriginalPublishedPath = moved;
            state.ReplacementPath = published;
            state.DestinationPath = published;
        }
        catch (Exception ex)
        {
            state.CallbackError = ex.ToString();
        }
    }

    private static (bool Created, int ExitCode, string Output) CreateJunction(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0 && Directory.Exists(link), process.ExitCode, output.Trim());
    }

    private static (bool Created, int ExitCode, string Output) CreateSymbolicFileLink(string link, string target)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0 && File.Exists(link), process.ExitCode, output.Trim());
    }

    private sealed class BarrierState(string root, byte[] sentinel)
    {
        internal string Root { get; } = root;
        internal byte[] Sentinel { get; } = sentinel;
        internal string? DestinationPath { get; set; }
        internal string? OriginalTempPath { get; set; }
        internal string? OriginalPublishedPath { get; set; }
        internal string? ReplacementPath { get; set; }
        internal string? CallbackError { get; set; }
        internal int CallbackInvoked;
    }

    private sealed class W4ManagedMutationWorkspace : LivePhotoBox.Media.Workspace.IMediaWorkspace
    {
        private readonly W4EvidenceWorkspace _inner;
        private readonly string _primarySourcePath;
        private int _primarySourceShaReads;

        private readonly bool _replaceWithSameBytes;

        internal W4ManagedMutationWorkspace(string caseName, string primarySourcePath, bool replaceWithSameBytes = false)
        {
            _inner = new W4EvidenceWorkspace(caseName);
            _primarySourcePath = primarySourcePath;
            _replaceWithSameBytes = replaceWithSameBytes;
        }

        internal W4EvidenceWorkspace Inner => _inner;
        public string RootDirectory => _inner.RootDirectory;
        internal string? PrimaryOutputPath { get; private set; }
        internal string? MotionOutputPath { get; private set; }
        internal byte[] Sentinel { get; } = [0x91, 0x92, 0x93, 0x94];
        internal bool ReplacementInjected { get; private set; }
        internal bool ReplacedWithSameBytes { get; private set; }
        internal string? OriginalPublishedSha256 { get; private set; }

        public string AllocateFilePath(string prefix, string extension)
        {
            string path = _inner.AllocateFilePath(prefix, extension);
            if (prefix.Equals("primary", StringComparison.OrdinalIgnoreCase)) PrimaryOutputPath = path;
            if (prefix.Equals("motion", StringComparison.OrdinalIgnoreCase)) MotionOutputPath = path;
            return path;
        }

        public async Task<string> ComputeFileSha256Async(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(filePath, _primarySourcePath, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref _primarySourceShaReads) == 2 &&
                PrimaryOutputPath is { } primaryOutput &&
                File.Exists(primaryOutput))
            {
                // Native has returned but the transaction rollback handle still
                // holds write+delete access on the published artifact.  Read
                // with a compatible share mode, then delete the old object and
                // create a foreign replacement at the same path before managed
                // post-native validation finishes.
                byte[] replacement;
                if (_replaceWithSameBytes)
                {
                    using var readStream = new FileStream(
                        primaryOutput, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
                    using var ms = new MemoryStream();
                    await readStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                    replacement = ms.ToArray();
                }
                else
                {
                    replacement = Sentinel;
                }
                OriginalPublishedSha256 = await _inner.ComputeFileSha256Async(primaryOutput, cancellationToken);
                File.Delete(primaryOutput);
                await File.WriteAllBytesAsync(primaryOutput, replacement, cancellationToken);
                ReplacementInjected = true;
                ReplacedWithSameBytes = _replaceWithSameBytes;
                return new string('0', 64);
            }

            return await _inner.ComputeFileSha256Async(filePath, cancellationToken).ConfigureAwait(false);
        }

        public Task AssertSourceUnmodifiedAsync(
            string sourcePath,
            string expectedSha256,
            CancellationToken cancellationToken = default) =>
            _inner.AssertSourceUnmodifiedAsync(sourcePath, expectedSha256, cancellationToken);

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static class NativeFileSystem
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateSymbolicLinkW(string symlinkFileName, string targetFileName, uint flags);

        internal static bool CreateHardLink(string link, string target, out int error)
        {
            bool result = CreateHardLinkW(link, target, nint.Zero);
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }

        internal static bool CreateSymbolicLink(string link, string target, bool isDirectory, out int error)
        {
            bool result = CreateSymbolicLinkW(link, target, isDirectory ? 1u : 0u);
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }
    }
}
