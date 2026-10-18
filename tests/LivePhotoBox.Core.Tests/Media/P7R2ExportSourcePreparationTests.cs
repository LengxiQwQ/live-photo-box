using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2ExportSourcePreparationTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrepareStillRange_RealEmbeddedHuaweiSample_MatchesProductionExtraction()
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        string? preparedPath = null;
        WindowsOwnedTemporaryArtifact? prepared = null;
        FileSnapshot? preparedSnapshot = null;
        Exception? failure = null;
        string? actualOutcome = null;

        try
        {
            sample = await PrepareEmbeddedSampleAsync("华为-Mate80.jpg");
            outputDirectory = CreatePreparationDirectory(sample, "real-still-range");

            prepared = await ExportSourcePreparation.PrepareStillRangeAsync(
                sample.SourcePath,
                sample.Inspected.Facts.PrimaryImage.ByteOffset,
                sample.Inspected.Facts.PrimaryImage.ByteLength,
                CancellationToken.None,
                sample.Inspected.Facts.PrimarySha256,
                outputDirectory);
            preparedPath = prepared.Path;
            preparedSnapshot = await CaptureFileSnapshotAsync(prepared.Path);
            actualOutcome = "ReturnedOwnedTemporaryArtifact";

            Assert.StartsWith("lpb_still_", Path.GetFileName(prepared.Path), StringComparison.Ordinal);
            Assert.Equal(Path.GetExtension(sample.SourcePath), Path.GetExtension(prepared.Path));
            Assert.Equal(sample.Extracted.PrimaryImage!.ByteLength, prepared.ByteLength);
            Assert.Equal(sample.Extracted.PrimaryImage.Sha256, prepared.Sha256, ignoreCase: true);
            Assert.Equal(sample.Extracted.PrimaryImage.Sha256, preparedSnapshot.Sha256, ignoreCase: true);
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
            Assert.Equal(prepared.Path, Assert.Single(Directory.GetFiles(outputDirectory)));

            prepared.Dispose();
            prepared = null;
            Assert.False(File.Exists(preparedPath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
        }
        catch (Exception ex)
        {
            failure = ex;
            actualOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            TryDispose(ref prepared);
            WriteEvidence("export-source-preparation-real-embedded-range", new
            {
                operation = nameof(ExportSourcePreparation.PrepareStillRangeAsync),
                sample = sample?.Evidence,
                outputDirectory,
                preparedPath,
                prepared = preparedSnapshot,
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath),
                outputFilesAfter = SnapshotFiles(outputDirectory),
                actualOutcome,
                failure = FailureEvidence(failure)
            });
            sample?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrepareOriginalBytes_RealExtractedOriginalPhoto_PreservesExactBytesAndSource()
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        string? preparedPath = null;
        WindowsOwnedTemporaryArtifact? prepared = null;
        string? originalBytesSha256 = null;
        long? originalBytesLength = null;
        FileSnapshot? preparedSnapshot = null;
        Exception? failure = null;
        string? actualOutcome = null;

        try
        {
            sample = await PrepareEmbeddedSampleAsync("一加-改了封面照片.jpg");
            byte[]? originalBytes = EditTimingService.ReadOriginalPhotoBytes(sample.SourcePath);
            Assert.NotNull(originalBytes);
            Assert.NotEmpty(originalBytes!);
            originalBytesSha256 = Convert.ToHexString(SHA256.HashData(originalBytes));
            originalBytesLength = originalBytes.LongLength;
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
            outputDirectory = CreatePreparationDirectory(sample, "real-original-bytes");

            prepared = await ExportSourcePreparation.PrepareOriginalBytesAsync(
                originalBytes,
                CancellationToken.None,
                outputDirectory);
            preparedPath = prepared.Path;
            preparedSnapshot = await CaptureFileSnapshotAsync(prepared.Path);
            actualOutcome = "ReturnedOwnedTemporaryArtifact";

            Assert.StartsWith("lpb_orig_export_", Path.GetFileName(prepared.Path), StringComparison.Ordinal);
            Assert.Equal(".jpg", Path.GetExtension(prepared.Path), ignoreCase: true);
            Assert.Equal(originalBytesLength, prepared.ByteLength);
            Assert.Equal(originalBytesSha256, prepared.Sha256, ignoreCase: true);
            Assert.Equal(originalBytesSha256, preparedSnapshot.Sha256, ignoreCase: true);
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));

            prepared.Dispose();
            prepared = null;
            Assert.False(File.Exists(preparedPath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
        }
        catch (Exception ex)
        {
            failure = ex;
            actualOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            TryDispose(ref prepared);
            WriteEvidence("export-source-preparation-real-original-bytes", new
            {
                operation = nameof(ExportSourcePreparation.PrepareOriginalBytesAsync),
                sample = sample?.Evidence,
                outputDirectory,
                preparedPath,
                originalBytesLength,
                originalBytesSha256,
                prepared = preparedSnapshot,
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath),
                outputFilesAfter = SnapshotFiles(outputDirectory),
                actualOutcome,
                failure = FailureEvidence(failure)
            });
            sample?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrepareStillRange_CancelAfterWrittenChunk_RollsBackAndPreservesSource()
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        using var cancellation = new CancellationTokenSource();
        AsyncLocal<Action<long>?> hook = ExportSourcePreparation.RangeChunkWrittenTestHook;
        Action<long>? previousHook = hook.Value;
        int hookCalls = 0;
        long bytesWrittenAtCancellation = 0;
        Exception? failure = null;
        string? actualOutcome = null;

        try
        {
            sample = await PrepareEmbeddedSampleAsync("华为-Mate80.jpg");
            outputDirectory = CreatePreparationDirectory(sample, "mid-range-cancel");
            hook.Value = written =>
            {
                if (Interlocked.CompareExchange(ref hookCalls, 1, 0) == 0)
                {
                    Interlocked.Exchange(ref bytesWrittenAtCancellation, written);
                    cancellation.Cancel();
                }
            };

            failure = await Record.ExceptionAsync(() => ExportSourcePreparation.PrepareStillRangeAsync(
                sample.SourcePath,
                sample.Inspected.Facts.PrimaryImage.ByteOffset,
                sample.Inspected.Facts.PrimaryImage.ByteLength,
                cancellation.Token,
                sample.Inspected.Facts.PrimarySha256,
                outputDirectory));
            actualOutcome = failure is OperationCanceledException
                ? "OperationCanceledException"
                : failure is null ? "ReturnedSuccess" : "UnexpectedException";

            Assert.Equal(1, Volatile.Read(ref hookCalls));
            Assert.True(Volatile.Read(ref bytesWrittenAtCancellation) > 0);
            Assert.True(
                Volatile.Read(ref bytesWrittenAtCancellation) < sample.Inspected.Facts.PrimaryImage.ByteLength,
                "Cancellation must arrive after a real chunk but before the primary range finishes.");
            Assert.True(cancellation.IsCancellationRequested);
            OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
            Assert.Empty(Directory.GetFiles(outputDirectory, "lpb_still_*", SearchOption.TopDirectoryOnly));
        }
        catch (Exception ex)
        {
            failure ??= ex;
            actualOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            hook.Value = previousHook;
            WriteEvidence("export-source-preparation-mid-range-cancel", new
            {
                operation = nameof(ExportSourcePreparation.PrepareStillRangeAsync),
                sample = sample?.Evidence,
                outputDirectory,
                cancellationBoundary = "after a nonempty real primary-range chunk was written",
                hookCalls = Volatile.Read(ref hookCalls),
                bytesWrittenAtCancellation = Volatile.Read(ref bytesWrittenAtCancellation),
                hookThrows = false,
                cancellationRequested = cancellation.IsCancellationRequested,
                actualOutcome,
                failure = FailureEvidence(failure),
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath),
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedResidueAfter = SnapshotFiles(outputDirectory, ".lpb-split-stage-*.tmp")
                    .Concat(SnapshotFiles(outputDirectory, "lpb_still_*"))
                    .ToArray()
            });
            sample?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrepareStillRange_RealSourceShareLockFailsAndCleansOwnedStage()
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        Exception? failure = null;
        string? actualOutcome = null;
        int? win32Error = null;

        try
        {
            sample = await PrepareEmbeddedSampleAsync("华为-Mate80.jpg");
            outputDirectory = CreatePreparationDirectory(sample, "locked-source-io");

            using (new FileStream(
                sample.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                failure = await Record.ExceptionAsync(() => ExportSourcePreparation.PrepareStillRangeAsync(
                    sample.SourcePath,
                    sample.Inspected.Facts.PrimaryImage.ByteOffset,
                    sample.Inspected.Facts.PrimaryImage.ByteLength,
                    CancellationToken.None,
                    sample.Inspected.Facts.PrimarySha256,
                    outputDirectory));
            }

            actualOutcome = failure is IOException ? "IOException" : failure is null ? "ReturnedSuccess" : "UnexpectedException";
            if (failure is IOException ioFailure)
                win32Error = ioFailure.HResult & 0xffff;

            IOException observed = Assert.IsAssignableFrom<IOException>(failure);
            Assert.Equal(32, observed.HResult & 0xffff);
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
        }
        catch (Exception ex)
        {
            failure ??= ex;
            actualOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            WriteEvidence("export-source-preparation-locked-source-io", new
            {
                operation = nameof(ExportSourcePreparation.PrepareStillRangeAsync),
                sample = sample?.Evidence,
                outputDirectory,
                failureSource = "RealWindowsShareLock(FileShare.None)",
                actualOutcome,
                win32Error,
                failure = FailureEvidence(failure),
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath),
                outputFilesAfter = SnapshotFiles(outputDirectory),
                ownedResidueAfter = SnapshotFiles(outputDirectory, ".lpb-split-stage-*.tmp")
            });
            sample?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "RealSamples")]
    public async Task PrepareOriginalBytes_DisposeWithRealShareLock_ReportsCleanupFailureAndPreservesOwner(bool alreadyFailing)
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        string? preparedPath = null;
        WindowsOwnedTemporaryArtifact? prepared = null;
        FileSnapshot? lockedArtifact = null;
        Exception? cleanupFailure = null;
        Exception? retryFailure = null;
        string? retryOutcome = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? originalFailure = alreadyFailing
            ? new OperationCanceledException("Original export cancellation.", cancellation.Token) : null;
        bool ownerRetainedAfterFailure = false;

        try
        {
            sample = await PrepareEmbeddedSampleAsync("一加-改了封面照片.jpg");
            byte[]? originalBytes = EditTimingService.ReadOriginalPhotoBytes(sample.SourcePath);
            Assert.NotNull(originalBytes);
            Assert.NotEmpty(originalBytes!);
            outputDirectory = CreatePreparationDirectory(sample, "share-lock-cleanup");
            prepared = await ExportSourcePreparation.PrepareOriginalBytesAsync(
                originalBytes,
                CancellationToken.None,
                outputDirectory);
            preparedPath = prepared.Path;
            WindowsFileIdentity ownedIdentity = prepared.Identity;
            string ownedSha256 = prepared.Sha256;
            WindowsOwnedTemporaryArtifact originalOwner = prepared;

            using (new FileStream(prepared.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cleanupFailure = Record.Exception(() => ExportSourcePreparation.DisposePreparedSource(ref prepared, originalFailure));
                Assert.Contains("[CleanupFailed]", cleanupFailure?.Message ?? string.Empty, StringComparison.Ordinal);
                Assert.Same(originalOwner, prepared);
                ownerRetainedAfterFailure = true;
                if (alreadyFailing)
                {
                    AggregateException aggregate = Assert.IsType<AggregateException>(cleanupFailure);
                    Assert.Equal(2, aggregate.InnerExceptions.Count);
                    Assert.Same(originalFailure, aggregate.InnerExceptions[0]);
                    Assert.IsType<IOException>(aggregate.InnerExceptions[1]);
                }
                else
                    Assert.IsType<IOException>(cleanupFailure);
                lockedArtifact = await CaptureFileSnapshotAsync(prepared.Path);
                Assert.Equal(ownedIdentity, lockedArtifact.Identity);
                Assert.Equal(ownedSha256, lockedArtifact.Sha256, ignoreCase: true);
                Assert.Single(Directory.GetFiles(outputDirectory));
            }

            retryFailure = Record.Exception(() => ExportSourcePreparation.DisposePreparedSource(ref prepared));
            retryOutcome = retryFailure is null ? "ExactOwnedArtifactRemovedAfterLockReleased" : "CleanupFailedAfterLockReleased";
            Assert.Null(retryFailure);
            Assert.Null(prepared);
            Assert.False(File.Exists(preparedPath));
            Assert.Empty(Directory.GetFiles(outputDirectory));
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
            prepared = null;
        }
        catch (Exception ex)
        {
            retryOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            TryDispose(ref prepared);
            WriteEvidence("export-source-preparation-share-lock-cleanup-" + alreadyFailing, new
            {
                operation = "WindowsOwnedTemporaryArtifact.Dispose",
                sample = sample?.Evidence,
                outputDirectory,
                preparedPath,
                cleanupFailure = FailureEvidence(cleanupFailure),
                originalFailure = FailureEvidence(originalFailure),
                ownerRetainedAfterFailure,
                artifactRetainedUnderLock = lockedArtifact,
                retryOutcome,
                retryFailure = FailureEvidence(retryFailure),
                outputFilesAfter = SnapshotFiles(outputDirectory),
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath)
            });
            sample?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrepareOriginalBytes_ForeignReplacementIsPreservedAndCleanupFailsClosed()
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        string? preparedPath = null;
        string? relocatedOwnerPath = null;
        WindowsOwnedTemporaryArtifact? prepared = null;
        FileSnapshot? ownerAfterRename = null;
        FileSnapshot? foreignCreated = null;
        FileSnapshot? foreignAfterDispose = null;
        Exception? cleanupFailure = null;
        string? actualOutcome = null;
        byte[] foreignBytes = [0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E, 0x2D, 0x45, 0x58, 0x50, 0x4F, 0x52, 0x54];

        try
        {
            sample = await PrepareEmbeddedSampleAsync("一加-改了封面照片.jpg");
            byte[]? originalBytes = EditTimingService.ReadOriginalPhotoBytes(sample.SourcePath);
            Assert.NotNull(originalBytes);
            Assert.NotEmpty(originalBytes!);
            outputDirectory = CreatePreparationDirectory(sample, "foreign-replacement");
            prepared = await ExportSourcePreparation.PrepareOriginalBytesAsync(
                originalBytes,
                CancellationToken.None,
                outputDirectory);
            preparedPath = prepared.Path;
            WindowsFileIdentity ownerIdentity = prepared.Identity;
            string ownerSha256 = prepared.Sha256;

            relocatedOwnerPath = Path.Combine(outputDirectory, "relocated-owned-object.jpg");
            File.Move(preparedPath, relocatedOwnerPath);
            ownerAfterRename = await CaptureFileSnapshotAsync(relocatedOwnerPath);
            WindowsFileIdentity foreignCreatingIdentity;
            using (var foreignOutput = new FileStream(preparedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                await foreignOutput.WriteAsync(foreignBytes);
                foreignOutput.Flush(flushToDisk: true);
                foreignCreatingIdentity = WindowsFileIdentity.Capture(foreignOutput.SafeFileHandle);
            }
            FileSnapshot foreignBeforeDispose = await CaptureFileSnapshotAsync(preparedPath);
            Assert.Equal(foreignCreatingIdentity, foreignBeforeDispose.Identity);
            foreignCreated = foreignBeforeDispose;

            WindowsOwnedTemporaryArtifact originalOwner = prepared;
            cleanupFailure = Record.Exception(() => ExportSourcePreparation.DisposePreparedSource(ref prepared));
            actualOutcome = cleanupFailure is IOException ? "CleanupFailed" : cleanupFailure is null ? "DisposedWithoutFailure" : "UnexpectedException";
            Assert.Contains("[CleanupFailed]", cleanupFailure?.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Same(originalOwner, prepared);
            foreignAfterDispose = await CaptureFileSnapshotAsync(preparedPath);
            Assert.Equal(ownerIdentity, ownerAfterRename.Identity);
            Assert.Equal(ownerSha256, ownerAfterRename.Sha256, ignoreCase: true);
            Assert.Equal(foreignBeforeDispose, foreignAfterDispose);
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
            Assert.Equal(
                new[] { Path.GetFileName(preparedPath), Path.GetFileName(relocatedOwnerPath) }.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                Directory.GetFiles(outputDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            prepared = null; // The expected cleanup failure must not be retried against the replacement path.
        }
        catch (Exception ex)
        {
            actualOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            TryDispose(ref prepared);
            WriteEvidence("export-source-preparation-foreign-replacement", new
            {
                operation = "WindowsOwnedTemporaryArtifact.Dispose",
                sample = sample?.Evidence,
                outputDirectory,
                preparedPath,
                relocatedOwnerPath,
                cleanupFailure = FailureEvidence(cleanupFailure),
                actualOutcome,
                ownedObjectAfterRename = ownerAfterRename,
                foreignObjectAfterDispose = foreignAfterDispose,
                outputFilesAfter = SnapshotFiles(outputDirectory),
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath)
            });
            if (foreignCreated is not null) DeleteExactFixtureFile(foreignCreated);
            if (ownerAfterRename is not null) DeleteExactFixtureFile(ownerAfterRename);
            sample?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PrepareStillRange_RealDiskFullDuringRangeWrite_RollsBackOwnedStage()
    {
        PreparedSample? sample = null;
        string? outputDirectory = null;
        string? mount = null;
        string? fillPath = null;
        Exception? failure = null;
        string? actualOutcome = null;
        int? diskFullErrorCode = null;
        long fillBytes = 0;
        string[]? filesAfterFailure = null;

        try
        {
            sample = await PrepareEmbeddedSampleAsync("华为-Mate80.jpg");
            mount = Environment.GetEnvironmentVariable("LPB_R2_DISK_ROOT")
                ?? throw new InvalidOperationException("LPB_R2_DISK_ROOT must point to the provisioned dedicated disk-full volume.");
            Assert.EndsWith(Path.Combine("disk-full", "mount"), mount.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            Assert.True((File.GetAttributes(mount) & FileAttributes.ReparsePoint) != 0, "The disk-full root must be an actual mounted volume.");

            outputDirectory = Path.Combine(mount, "r2-export-source-preparation");
            Assert.False(Directory.Exists(outputDirectory), "The dedicated test directory must start empty and unique to this test.");
            Directory.CreateDirectory(outputDirectory);
            Assert.True(
                sample.Inspected.Facts.PrimaryImage.ByteLength > 256 * 1024,
                "The real sample range must exceed the space deliberately left on the dedicated volume.");
            fillPath = Path.Combine(outputDirectory, "fill.bin");
            using (var fill = new FileStream(fillPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] block = new byte[1024 * 1024];
                IOException? fullFailure = null;
                try
                {
                    for (int i = 0; i < 256; i++)
                        fill.Write(block);
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 112 or 39)
                {
                    fullFailure = ex;
                    diskFullErrorCode = ex.HResult & 0xffff;
                }

                Assert.NotNull(fullFailure);
                fillBytes = fill.Length;
                fill.SetLength(Math.Max(0, fillBytes - 256 * 1024));
                fill.Flush(flushToDisk: true);
            }

            failure = await Record.ExceptionAsync(() => ExportSourcePreparation.PrepareStillRangeAsync(
                sample.SourcePath,
                sample.Inspected.Facts.PrimaryImage.ByteOffset,
                sample.Inspected.Facts.PrimaryImage.ByteLength,
                CancellationToken.None,
                sample.Inspected.Facts.PrimarySha256,
                outputDirectory));
            actualOutcome = failure is IOException ? "IOException" : failure is null ? "ReturnedSuccess" : "UnexpectedException";
            if (failure is IOException observedIo && (observedIo.HResult & 0xffff) is 112 or 39)
                diskFullErrorCode = observedIo.HResult & 0xffff;

            Assert.NotNull(failure);
            Assert.Contains(FindErrorCode(failure), new int?[] { 112, 39 });
            filesAfterFailure = Directory.GetFiles(outputDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()!;
            Assert.Equal(new[] { "fill.bin" }, filesAfterFailure);
            Assert.Empty(Directory.GetFiles(outputDirectory, ".lpb-split-stage-*.tmp", SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.GetFiles(outputDirectory, "lpb_still_*", SearchOption.TopDirectoryOnly));
            Assert.Equal(sample.SourceSnapshot, await CaptureFileSnapshotAsync(sample.SourcePath));
        }
        catch (Exception ex)
        {
            failure ??= ex;
            actualOutcome ??= ex.GetType().Name;
            throw;
        }
        finally
        {
            WriteEvidence("export-source-preparation-real-disk-full", new
            {
                operation = nameof(ExportSourcePreparation.PrepareStillRangeAsync),
                sample = sample?.Evidence,
                mount,
                outputDirectory,
                fillPath,
                fillBytes,
                diskFullErrorCode,
                actualOutcome,
                failure = FailureEvidence(failure),
                filesAfterFailure,
                filesAtEvidence = SnapshotFiles(outputDirectory),
                sourceAfter = sample is null ? null : await TryCaptureFileSnapshotAsync(sample.SourcePath)
            });
            if (fillPath is not null && File.Exists(fillPath))
                File.Delete(fillPath);
            if (outputDirectory is not null && Directory.Exists(outputDirectory) && !Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                Directory.Delete(outputDirectory);
            sample?.Dispose();
        }
    }

    private static async Task<PreparedSample> PrepareEmbeddedSampleAsync(string filename)
    {
        string sourcePath = TestSampleResolver.ResolveSample(filename);
        FileSnapshot sourceSnapshot = await CaptureFileSnapshotAsync(sourcePath);
        InspectedSource? inspected = null;
        var workspace = new MediaWorkspace();
        try
        {
            inspected = await new SourceInspector().InspectWithPlanAsync(sourcePath);
            Assert.NotEmpty(inspected.Facts.PrimarySha256);
            Assert.Equal(sourceSnapshot.Sha256, inspected.Facts.PrimarySha256, ignoreCase: true);
            Assert.True(inspected.Facts.PrimaryImage.IsPresent);
            Assert.NotNull(inspected.Facts.MotionVideo);
            Assert.True(inspected.Facts.MotionVideo!.IsPresent);
            Assert.Equal(0, inspected.Facts.MotionVideo.SourceIndex);

            ExtractedMediaBundle extracted = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                sourcePath,
                null,
                workspace);
            Assert.NotNull(extracted.PrimaryImage);
            Assert.NotNull(extracted.MotionVideo);
            Assert.Equal(sourceSnapshot, await CaptureFileSnapshotAsync(sourcePath));
            return new PreparedSample(sourcePath, sourceSnapshot, inspected, workspace, extracted);
        }
        catch
        {
            inspected?.Dispose();
            workspace.Dispose();
            throw;
        }
    }

    private static string CreatePreparationDirectory(PreparedSample sample, string name)
    {
        // Export preparation owns its own temporary directory in production;
        // it is not a child artifact of the extraction workspace.
        string directory = sample.Workspace.RootDirectory + ".export-" + name;
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        sample.PreparationDirectories.Add(directory);
        return directory;
    }

    private static async Task<FileSnapshot> CaptureFileSnapshotAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(stream.SafeFileHandle);
        string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        return new FileSnapshot(path, stream.Length, sha256, identity);
    }

    private static async Task<FileSnapshot?> TryCaptureFileSnapshotAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try { return await CaptureFileSnapshotAsync(path); }
        catch { return null; }
    }

    private static string[] SnapshotFiles(string? directory, string pattern = "*") =>
        string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            ? []
            : Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

    private static object? FailureEvidence(Exception? failure) => failure is null
        ? null
        : new
        {
            type = failure.GetType().FullName,
            failure.Message,
            hresult = failure.HResult,
            innerType = failure.InnerException?.GetType().FullName,
            innerMessage = failure.InnerException?.Message
        };

    private static int? FindErrorCode(Exception? failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            int code = current.HResult & 0xffff;
            if (code is 112 or 39)
                return code;
        }

        return null;
    }

    private static void TryDispose(ref WindowsOwnedTemporaryArtifact? artifact)
    {
        WindowsOwnedTemporaryArtifact? current = artifact;
        artifact = null;
        try { current?.Dispose(); }
        catch { }
    }

    private static void WriteEvidence(string filename, object evidence)
    {
        string directory = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE")
            ?? throw new InvalidOperationException("LPB_R2_EVIDENCE is required for P7-R2 export-source preparation evidence.");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, filename + ".json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, evidence, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(flushToDisk: true);
    }

    private sealed record FileSnapshot(string Path, long Length, string Sha256, WindowsFileIdentity Identity);

    private static void DeleteExactFixtureFile(FileSnapshot expected)
    {
        Assert.Equal(expected, CaptureFileSnapshotAsync(expected.Path).GetAwaiter().GetResult());
        using var handle = WindowsOwnedFilePublisher.OpenOwnedStagedFile(expected.Path, expected.Identity);
        OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
            handle, expected.Identity, Path.GetDirectoryName(expected.Path)!, out _);
        Assert.True(proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked);
    }

    private sealed class PreparedSample(
        string sourcePath,
        FileSnapshot sourceSnapshot,
        InspectedSource inspected,
        MediaWorkspace workspace,
        ExtractedMediaBundle extracted) : IDisposable
    {
        public string SourcePath { get; } = sourcePath;
        public FileSnapshot SourceSnapshot { get; } = sourceSnapshot;
        public InspectedSource Inspected { get; } = inspected;
        public MediaWorkspace Workspace { get; } = workspace;
        public System.Collections.Generic.List<string> PreparationDirectories { get; } = [];
        public ExtractedMediaBundle Extracted { get; } = extracted;
        public object Evidence => new
        {
            sourcePath = SourcePath,
            sourceSnapshot = SourceSnapshot,
            protocol = Inspected.Facts.Protocol.ToString(),
            primaryRange = new
            {
                Inspected.Facts.PrimaryImage.ByteOffset,
                Inspected.Facts.PrimaryImage.ByteLength,
                inspectedSourceSha256 = Inspected.Facts.PrimarySha256
            },
            extractedPrimary = new
            {
                Extracted.PrimaryImage!.Path,
                Extracted.PrimaryImage.ByteLength,
                Extracted.PrimaryImage.Sha256
            },
            extractedMotionVideo = new
            {
                Extracted.MotionVideo!.Path,
                Extracted.MotionVideo.ByteLength,
                Extracted.MotionVideo.Sha256
            }
        };

        public void Dispose()
        {
            Inspected.Dispose();
            Workspace.Dispose();
            foreach (string directory in PreparationDirectories)
                Directory.Delete(directory); // Empty-only: never recurse over unknown content.
        }
    }
}
