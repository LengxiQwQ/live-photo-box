using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.CommandLine;
using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class P7R2ConvertPublicationTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_PublishesImageAndVideoAndClosesFailureTransactions()
    {
        string? evidenceDirectory = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE");
        string? evidencePath = string.IsNullOrWhiteSpace(evidenceDirectory)
            ? null : Path.Combine(evidenceDirectory, "cli-convert-publication.json");
        string outputRoot = ResolveOutputRoot(evidencePath);
        Directory.CreateDirectory(outputRoot);
        string testDirectory = Path.Combine(outputRoot, $"convert-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var cases = new List<ConvertCaseEvidence>();
        NativeRuntimeEvidence? runtimeEvidence = null;

        try
        {
            string imageSource = ResolveCachedSample("oppo.jpg");
            string imageSourceHash = await ComputeSha256Async(imageSource);
            WindowsFileIdentity imageSourceIdentity = WindowsFileIdentity.Capture(imageSource);
            string imageOutput = Path.Combine(testDirectory, "image-copy.jpg");
            CliResult imageResult = await RunConvertAsync(imageSource, imageOutput);
            Assert.True(imageResult.ExitCode == 0, $"Image conversion failed: {imageResult.StdOut}\n{imageResult.StdErr}");
            Assert.True(File.Exists(imageOutput));
            string imageOutputHash = await ComputeSha256Async(imageOutput);
            WindowsFileIdentity imageOutputIdentity = WindowsFileIdentity.Capture(imageOutput);
            Assert.Equal(imageSourceHash, await ComputeSha256Async(imageSource));
            Assert.Equal(imageSourceIdentity, WindowsFileIdentity.Capture(imageSource));
            AssertNoConverterArtifacts(testDirectory);
            cases.Add(new ConvertCaseEvidence(
                "image-success",
                "passed",
                imageSource,
                imageSourceIdentity,
                imageSourceHash,
                imageOutput,
                imageOutputHash,
                imageOutputIdentity,
                null,
                null,
                null,
                [],
                SourceUnchanged: true,
                ConverterArtifactPath: null,
                ConverterArtifactIdentity: null,
                ConverterArtifactSha256: null));

            string videoSource = ResolveCachedSample("苹果双文件.MOV");
            string videoSourceHash = await ComputeSha256Async(videoSource);
            WindowsFileIdentity videoSourceIdentity = WindowsFileIdentity.Capture(videoSource);
            string videoOutput = Path.Combine(testDirectory, "video-copy.mov");
            CliResult videoResult = await RunConvertAsync(videoSource, videoOutput, "--codec", "copy");
            Assert.True(videoResult.ExitCode == 0, $"Video conversion failed: {videoResult.StdOut}\n{videoResult.StdErr}");
            Assert.True(File.Exists(videoOutput));
            string videoOutputHash = await ComputeSha256Async(videoOutput);
            WindowsFileIdentity videoOutputIdentity = WindowsFileIdentity.Capture(videoOutput);
            Assert.Equal(videoSourceHash, await ComputeSha256Async(videoSource));
            Assert.Equal(videoSourceIdentity, WindowsFileIdentity.Capture(videoSource));
            AssertNoConverterArtifacts(testDirectory);
            cases.Add(new ConvertCaseEvidence(
                "video-success",
                "passed",
                videoSource,
                videoSourceIdentity,
                videoSourceHash,
                videoOutput,
                videoOutputHash,
                videoOutputIdentity,
                null,
                null,
                null,
                [],
                SourceUnchanged: true,
                ConverterArtifactPath: null,
                ConverterArtifactIdentity: null,
                ConverterArtifactSha256: null));

            runtimeEvidence = await CaptureNativeRuntimeEvidenceAsync();

            string lockedOutput = Path.Combine(testDirectory, "locked-output.jpg");
            byte[] lockedBytes = [0x41, 0x52, 0x32, 0x00, 0x7F];
            await File.WriteAllBytesAsync(lockedOutput, lockedBytes);
            using (var lockHandle = new FileStream(
                lockedOutput,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read))
            {
                WindowsFileIdentity lockedIdentity = WindowsFileIdentity.Capture(lockHandle.SafeFileHandle);
                lockHandle.Position = 0;
                string lockedHash = Convert.ToHexString(await SHA256.HashDataAsync(lockHandle));
                CliResult lockedResult = await RunConvertAsync(imageSource, lockedOutput, "--overwrite");
                Assert.NotEqual(0, lockedResult.ExitCode);
                Assert.Equal(lockedIdentity, WindowsFileIdentity.Capture(lockHandle.SafeFileHandle));
                lockHandle.Position = 0;
                Assert.Equal(lockedHash, Convert.ToHexString(await SHA256.HashDataAsync(lockHandle)));
                Assert.Equal(imageSourceHash, await ComputeSha256Async(imageSource));
                Assert.Equal(imageSourceIdentity, WindowsFileIdentity.Capture(imageSource));
                AssertNoConverterArtifacts(testDirectory);
                cases.Add(new ConvertCaseEvidence(
                    "locked-output-failure",
                    "passed: failed closed; locked output object and bytes unchanged",
                    imageSource,
                    imageSourceIdentity,
                    imageSourceHash,
                    lockedOutput,
                    lockedHash,
                    lockedIdentity,
                    null,
                    null,
                    null,
                    [],
                    SourceUnchanged: true,
                    ConverterArtifactPath: null,
                    ConverterArtifactIdentity: null,
                    ConverterArtifactSha256: null));
            }

            string conflictDirectory = Path.Combine(testDirectory, "late-conflict");
            Directory.CreateDirectory(conflictDirectory);
            var nativeImageResult = await new ImageConverter().ConvertAsync(new ImageConversionRequest
            {
                SourceArtifact = new MediaArtifact
                {
                    Path = imageSource,
                    Kind = MediaArtifactKind.PrimaryImage,
                    MimeType = "image/jpeg",
                    ImageContainer = ImageContainer.Jpeg,
                    ImageCodec = ImageCodec.Jpeg,
                    ByteLength = new FileInfo(imageSource).Length,
                    FileIdentity = imageSourceIdentity,
                    Sha256 = imageSourceHash
                },
                TargetContainer = ImageContainer.Jpeg,
                TargetDirectory = conflictDirectory
            });
            Assert.True(nativeImageResult.Success, nativeImageResult.ErrorMessage);
            MediaArtifact nativeArtifact = Assert.IsType<MediaArtifact>(nativeImageResult.OutputArtifact);
            string nativeArtifactHash = await ComputeSha256Async(nativeArtifact.Path);
            WindowsFileIdentity nativeArtifactIdentity = WindowsFileIdentity.Capture(nativeArtifact.Path);
            Assert.Equal(nativeArtifactIdentity, nativeArtifact.FileIdentity);
            Assert.Equal(nativeArtifactHash, nativeArtifact.Sha256);
            Assert.Equal(imageSourceHash, await ComputeSha256Async(imageSource));
            Assert.Equal(imageSourceIdentity, WindowsFileIdentity.Capture(imageSource));

            // Keep the genuine Native-created object, then substitute a new
            // object with identical bytes at its old name. Content equality
            // must never become permission to publish or delete that object.
            string retainedNativePath = Path.Combine(conflictDirectory, "retained-native-output.bin");
            byte[] nativeBytes = await File.ReadAllBytesAsync(nativeArtifact.Path);
            File.Move(nativeArtifact.Path, retainedNativePath);
            await File.WriteAllBytesAsync(nativeArtifact.Path, nativeBytes);
            WindowsFileIdentity replacementIdentity = WindowsFileIdentity.Capture(nativeArtifact.Path);
            Assert.False(nativeArtifactIdentity.Matches(replacementIdentity));
            Assert.Equal(nativeArtifactHash, await ComputeSha256Async(nativeArtifact.Path));
            string replacementFinalPath = Path.Combine(conflictDirectory, "must-not-publish-substitution.jpg");
            await Assert.ThrowsAsync<IOException>(() => ConvertCommand.PublishConvertedArtifactAsync(
                replacementFinalPath, false, nativeArtifact, null, CancellationToken.None));
            Assert.False(File.Exists(replacementFinalPath));
            Assert.Equal(replacementIdentity, WindowsFileIdentity.Capture(nativeArtifact.Path));
            Assert.Equal(nativeArtifactHash, await ComputeSha256Async(nativeArtifact.Path));
            Assert.Equal(nativeArtifactIdentity, WindowsFileIdentity.Capture(retainedNativePath));
            Assert.Equal(nativeArtifactHash, await ComputeSha256Async(retainedNativePath));
            Assert.Equal(imageSourceIdentity, WindowsFileIdentity.Capture(imageSource));
            Assert.Equal(imageSourceHash, await ComputeSha256Async(imageSource));
            cases.Add(new ConvertCaseEvidence(
                "same-byte-foreign-artifact-substitution",
                "passed: creating-handle identity mismatch rejected; identical foreign bytes not published or deleted; Native-owned original preserved",
                imageSource, imageSourceIdentity, imageSourceHash,
                replacementFinalPath, null, null,
                nativeArtifact.Path, replacementIdentity, nativeArtifactHash,
                Directory.GetFiles(conflictDirectory), SourceUnchanged: true,
                ConverterArtifactPath: nativeArtifact.Path,
                ConverterArtifactIdentity: nativeArtifactIdentity,
                ConverterArtifactSha256: nativeArtifactHash));

            // Restore this test fixture using the exact foreign object handle;
            // the subsequent race case consumes the real Native artifact.
            using (var replacementHandle = WindowsOwnedFilePublisher.OpenOwnedStagedFile(nativeArtifact.Path, replacementIdentity))
            {
                OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                    replacementHandle, replacementIdentity, nativeArtifact.Path, out _);
                Assert.True(proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked);
            }
            File.Move(retainedNativePath, nativeArtifact.Path);
            Assert.Equal(nativeArtifactIdentity, WindowsFileIdentity.Capture(nativeArtifact.Path));

            byte[] foreignBytes = [0x52, 0x32, 0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E];
            string conflictOutput = Path.Combine(conflictDirectory, "foreign.jpg");
            WindowsFileIdentity? foreignIdentity = null;
            string? foreignHash = null;
            async Task CreateLateForeignObjectAsync(
                SplitPublicationTraceEvent traceEvent,
                CancellationToken token)
            {
                if (traceEvent.Boundary != SplitPublicationBoundary.BeforeMissingTargetPublication)
                    return;

                await using (var foreignStream = new FileStream(
                    conflictOutput,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read))
                {
                    await foreignStream.WriteAsync(foreignBytes, token);
                    await foreignStream.FlushAsync(token);
                }
                foreignIdentity = WindowsFileIdentity.Capture(conflictOutput);
                foreignHash = await ComputeSha256Async(conflictOutput);
            }

            await Assert.ThrowsAnyAsync<Exception>(() => ConvertCommand.PublishConvertedArtifactAsync(
                conflictOutput,
                overwriteExisting: false,
                nativeArtifact,
                CreateLateForeignObjectAsync,
                CancellationToken.None));
            Assert.NotNull(foreignIdentity);
            Assert.NotNull(foreignHash);
            Assert.Equal(foreignIdentity, WindowsFileIdentity.Capture(conflictOutput));
            Assert.Equal(foreignHash, await ComputeSha256Async(conflictOutput));
            Assert.False(File.Exists(nativeArtifact.Path));
            Assert.Equal(imageSourceIdentity, WindowsFileIdentity.Capture(imageSource));
            Assert.Equal(imageSourceHash, await ComputeSha256Async(imageSource));
            string[] conflictResidue = Directory.GetFiles(conflictDirectory);
            Assert.Equal(new[] { conflictOutput }, conflictResidue);
            cases.Add(new ConvertCaseEvidence(
                "late-conflict-overwrite-false",
                "passed: Native-owned converter artifact evidence was bound; foreign target preserved; exact invocation artifact removed",
                imageSource,
                imageSourceIdentity,
                imageSourceHash,
                conflictOutput,
                foreignHash,
                foreignIdentity,
                conflictOutput,
                foreignIdentity,
                foreignHash,
                conflictResidue,
                SourceUnchanged: true,
                ConverterArtifactPath: nativeArtifact.Path,
                ConverterArtifactIdentity: nativeArtifactIdentity,
                ConverterArtifactSha256: nativeArtifactHash));

            string failedStageDirectory = Path.Combine(testDirectory, "failed-stage");
            Directory.CreateDirectory(failedStageDirectory);
            string failedArtifactPath = await CreateConverterArtifactAsync(failedStageDirectory);
            WindowsFileIdentity failedArtifactIdentity = WindowsFileIdentity.Capture(failedArtifactPath);
            string failedArtifactHash = await ComputeSha256Async(failedArtifactPath);
            var failedArtifact = new MediaArtifact
            {
                Path = failedArtifactPath,
                Kind = MediaArtifactKind.PrimaryImage,
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = new FileInfo(failedArtifactPath).Length,
                FileIdentity = failedArtifactIdentity,
                Sha256 = failedArtifactHash
            };
            string failedOutput = Path.Combine(failedStageDirectory, "failed-output.jpg");
            async Task FailBeforeCommitAsync(SplitPublicationTraceEvent traceEvent, CancellationToken _)
            {
                if (traceEvent.Boundary == SplitPublicationBoundary.BeforeCommitBarrier)
                    throw new IOException("Injected R2 Convert test barrier failure.");
                await Task.CompletedTask;
            }

            await Assert.ThrowsAnyAsync<Exception>(() => ConvertCommand.PublishConvertedArtifactAsync(
                failedOutput,
                overwriteExisting: false,
                failedArtifact,
                FailBeforeCommitAsync,
                CancellationToken.None));
            Assert.False(File.Exists(failedOutput));
            Assert.False(File.Exists(failedArtifactPath));
            string[] failedResidue = Directory.GetFiles(failedStageDirectory);
            Assert.Empty(failedResidue);
            cases.Add(new ConvertCaseEvidence(
                "failed-stage-cleanup",
                "passed: unpublished target and exact invocation artifact removed",
                failedArtifactPath,
                failedArtifactIdentity,
                failedArtifactHash,
                failedOutput,
                null,
                null,
                null,
                null,
                null,
                failedResidue,
                SourceUnchanged: null,
                ConverterArtifactPath: failedArtifactPath,
                ConverterArtifactIdentity: failedArtifactIdentity,
                ConverterArtifactSha256: failedArtifactHash));

            if (!string.IsNullOrWhiteSpace(evidencePath))
                await WriteEvidenceCreateOnlyAsync(
                    evidencePath,
                    testDirectory,
                    cases,
                    runtimeEvidence ?? throw new InvalidOperationException("Native runtime provenance was not captured."));
        }
        finally
        {
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static async Task<CliResult> RunConvertAsync(
        string source,
        string output,
        params string[] extraArguments)
    {
        var root = new RootCommand { ConvertCommand.Create() };
        var args = new List<string> { "convert", source, "--output", output };
        args.AddRange(extraArguments);
        return await CliTestHost.RunAsync(root, args.ToArray());
    }

    private static string ResolveCachedSample(string fileName)
    {
        string? configuredSamples = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        string candidate = Path.Combine(
            string.IsNullOrWhiteSpace(configuredSamples)
                ? Path.Combine(FindRepositoryRoot(), ".ai-tmp", "cache", "samples")
                : configuredSamples,
            fileName);
        if (!File.Exists(candidate))
            throw new FileNotFoundException($"Frozen cached RealSample '{fileName}' was not found.", candidate);
        return candidate;
    }

    private static string ResolveOutputRoot(string? evidencePath)
    {
        if (string.IsNullOrWhiteSpace(evidencePath))
            return Path.Combine(Path.GetTempPath(), "lpb-r2-convert");

        string? parent = Path.GetDirectoryName(Path.GetFullPath(evidencePath));
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("LPB_R2_EVIDENCE must be an absolute or rooted JSON file path.");
        return parent;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LivePhotoBox.CLI", "LivePhotoBox.CLI.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Live Photo Box repository root.");
    }

    private static async Task<string> CreateConverterArtifactAsync(string directory)
    {
        string path = Path.Combine(directory, $"img-conv-{Guid.NewGuid():N}.jpg");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(new byte[] { 0xFF, 0xD8, 0x52, 0x32, 0xFF, 0xD9 });
        await stream.FlushAsync();
        return path;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task<NativeRuntimeEvidence> CaptureNativeRuntimeEvidenceAsync()
    {
        NativeRuntimeInfo runtime = NativeRuntime.Probe();
        Assert.True(runtime.IsAvailable, $"Native runtime health probe failed: {runtime.Diagnostic}");
        Assert.Equal(NativeRuntime.SupportedAbiVersion, runtime.AbiVersion);

        string? modulePath = null;
        using (Process process = Process.GetCurrentProcess())
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (string.Equals(
                    module.ModuleName,
                    "LivePhotoBox.Native.dll",
                    StringComparison.OrdinalIgnoreCase))
                {
                    modulePath = module.FileName;
                    break;
                }
            }
        }
        Assert.False(string.IsNullOrWhiteSpace(modulePath), "The product Native DLL was not found in the current process module list.");

        WindowsFileIdentity nativeModuleIdentity = WindowsFileIdentity.Capture(modulePath!);
        string nativeModuleSha256 = await ComputeSha256Async(modulePath!);
        Assert.Equal(nativeModuleIdentity, WindowsFileIdentity.Capture(modulePath!));

        return new NativeRuntimeEvidence(
            modulePath!,
            nativeModuleIdentity,
            nativeModuleSha256,
            runtime.AbiVersion,
            runtime.Version,
            typeof(NativeRuntime).Assembly.Location,
            typeof(ConvertCommand).Assembly.Location);
    }

    private static void AssertNoConverterArtifacts(string directory)
    {
        Assert.DoesNotContain(Directory.EnumerateFiles(directory), path =>
            Path.GetFileName(path).StartsWith("img-conv-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("img-hdr-conv-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("vid-conv-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteEvidenceCreateOnlyAsync(
        string evidencePath,
        string testDirectory,
        IReadOnlyList<ConvertCaseEvidence> cases,
        NativeRuntimeEvidence nativeRuntime)
    {
        string fullPath = Path.GetFullPath(evidencePath);
        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("LPB_R2_EVIDENCE must name a file in an existing directory.");
        Directory.CreateDirectory(parent);

        var document = new ConvertEvidenceDocument(
            SchemaVersion: 1,
            Scope: "P7-R2 Convert publication transactions",
            Result: "passed",
            TestDirectory: testDirectory,
            NativeRuntime: nativeRuntime,
            Cases: cases);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
        await using var evidence = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await evidence.WriteAsync(json);
        await evidence.FlushAsync();
    }

    private sealed record ConvertCaseEvidence(
        string Name,
        string Result,
        string InputPath,
        WindowsFileIdentity? InputIdentity,
        string? InputSha256,
        string OutputPath,
        string? OutputSha256,
        WindowsFileIdentity? OutputIdentity,
        string? ForeignPath,
        WindowsFileIdentity? ForeignIdentity,
        string? ForeignSha256,
        string[] Residue,
        bool? SourceUnchanged,
        string? ConverterArtifactPath,
        WindowsFileIdentity? ConverterArtifactIdentity,
        string? ConverterArtifactSha256);

    private sealed record NativeRuntimeEvidence(
        string NativeModulePath,
        WindowsFileIdentity NativeModuleIdentity,
        string NativeModuleSha256,
        uint AbiVersion,
        string? NativeVersion,
        string CoreAssemblyLocation,
        string CliAssemblyLocation);

    private sealed record ConvertEvidenceDocument(
        int SchemaVersion,
        string Scope,
        string Result,
        string TestDirectory,
        NativeRuntimeEvidence NativeRuntime,
        IReadOnlyList<ConvertCaseEvidence> Cases);
}
