using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class WorkspaceCleanupResultTests
{
    private static async Task<string> CreateRegisteredFileAsync(MediaWorkspace workspace, string name, byte[] contents)
    {
        string path = workspace.AllocateFilePath(name, ".bin");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        await stream.WriteAsync(contents);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
        var identity = WindowsFileIdentity.Capture(stream.SafeFileHandle);
        stream.Position = 0;
        string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        Assert.True(WorkspaceOwnershipRegistry.RegisterCreatedFile(path, identity, contents.LongLength, sha, "test-fixture"));
        return path;
    }

    [Fact]
    public async Task ProductionWorkspace_CleanDispose_SucceedsAndRemovesRoot_ZeroFirstChance()
    {
        var creation = MediaWorkspace.TryCreate();
        Assert.Null(creation.Failure);
        using var workspace = creation.Workspace!;
        string root = workspace.RootDirectory;
        await CreateRegisteredFileAsync(workspace, "clean-test", Encoding.UTF8.GetBytes("clean data"));

        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("LivePhotoBox") == true)
                events.Enqueue(e.Exception);
        };

        MediaWorkspace.WorkspaceCleanupAttempt cleanup;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            cleanup = workspace.TryCleanup();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observer;
        }

        Assert.Empty(events);
        Assert.True(cleanup.Succeeded);
        Assert.Null(cleanup.Failure);
        Assert.Null(cleanup.LimitationMessage);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ProductionWorkspace_LockedFile_ReturnsLimitation_PreservesRootAndFile_ZeroFirstChance()
    {
        var creation = MediaWorkspace.TryCreate();
        Assert.Null(creation.Failure);
        var workspace = creation.Workspace!;
        string root = workspace.RootDirectory;
        string filePath = await CreateRegisteredFileAsync(workspace, "locked-test", Encoding.UTF8.GetBytes("locked data"));

        FileStream? shareLock = null;
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("LivePhotoBox") == true)
                events.Enqueue(e.Exception);
        };

        MediaWorkspace.WorkspaceCleanupAttempt cleanup;
        try
        {
            shareLock = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try
            {
                cleanup = workspace.TryCleanup();
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= observer;
            }

            Assert.Empty(events);
            Assert.False(cleanup.Succeeded);
            Assert.NotNull(cleanup.Failure);
            Assert.NotNull(cleanup.LimitationMessage);
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            shareLock?.Dispose();
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ProductionWorkspace_ForeignFile_ReturnsLimitation_PreservesRootAndForeignContent_ZeroFirstChance()
    {
        var creation = MediaWorkspace.TryCreate();
        Assert.Null(creation.Failure);
        var workspace = creation.Workspace!;
        string root = workspace.RootDirectory;
        await CreateRegisteredFileAsync(workspace, "owned-test", Encoding.UTF8.GetBytes("owned data"));

        string foreignPath = Path.Combine(root, "unregistered-foreign.txt");
        File.WriteAllText(foreignPath, "unregistered content");

        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("LivePhotoBox") == true)
                events.Enqueue(e.Exception);
        };

        MediaWorkspace.WorkspaceCleanupAttempt cleanup;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try
            {
                cleanup = workspace.TryCleanup();
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= observer;
            }

            Assert.Empty(events);
            Assert.False(cleanup.Succeeded);
            Assert.NotNull(cleanup.Failure);
            Assert.NotNull(cleanup.LimitationMessage);
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(foreignPath));
            Assert.Equal("unregistered content", File.ReadAllText(foreignPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ProductionWorkspace_ReplacedFile_ReturnsLimitation_PreservesBoth_ZeroFirstChance()
    {
        var creation = MediaWorkspace.TryCreate();
        Assert.Null(creation.Failure);
        var workspace = creation.Workspace!;
        string root = workspace.RootDirectory;
        string ownedPath = await CreateRegisteredFileAsync(workspace, "replaced-test", Encoding.UTF8.GetBytes("original data"));
        string relocatedOwnedPath = ownedPath + ".relocated";

        File.Move(ownedPath, relocatedOwnedPath);
        File.WriteAllText(ownedPath, "foreign replacement");

        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("LivePhotoBox") == true)
                events.Enqueue(e.Exception);
        };

        MediaWorkspace.WorkspaceCleanupAttempt cleanup;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try
            {
                cleanup = workspace.TryCleanup();
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= observer;
            }

            Assert.Empty(events);
            Assert.False(cleanup.Succeeded);
            Assert.NotNull(cleanup.Failure);
            Assert.NotNull(cleanup.LimitationMessage);
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(ownedPath));
            Assert.True(File.Exists(relocatedOwnedPath));
            Assert.Equal("foreign replacement", File.ReadAllText(ownedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task StrictWorkspace_RetainsThrowsOnFailure()
    {
        var workspace = new MediaWorkspace();
        string root = workspace.RootDirectory;
        string filePath = await CreateRegisteredFileAsync(workspace, "strict-lock-test", Encoding.UTF8.GetBytes("strict data"));

        FileStream? shareLock = null;
        try
        {
            shareLock = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ex = Record.Exception(workspace.Dispose);
            Assert.NotNull(ex);
            Assert.IsType<IOException>(ex);
            Assert.StartsWith("[CleanupFailed]", ex.Message);
            Assert.True(Directory.Exists(root));

            shareLock.Dispose();
            shareLock = null;
            workspace.Dispose();
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            shareLock?.Dispose();
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task MergePipeline_WhenCleanupHasLimitation_ReturnsWarningWithCommittedOutputsAndExplainsLimitation_ZeroFirstChance()
    {
        string sampleImage = TestSampleResolver.ResolveSample("苹果双文件.HEIC");
        string sampleVideo = TestSampleResolver.ResolveSample("苹果双文件.MOV");
        string tempOut = Path.Combine(Path.GetTempPath(), "LivePhotoBox", "test-cleanup-limitation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempOut);
        string? workspaceRoot = null;
        FileStream? lockedStream = null;

        var previousHook = MediaWorkspace.TestCleanupHook.Value;
        bool hookTriggered = false;
        MediaWorkspace.TestCleanupHook.Value = (_, root) =>
        {
            workspaceRoot = root;
            hookTriggered = true;
            // Lock a staged file in the workspace root instead of throwing an artificial exception
            var anyFile = Directory.GetFiles(root).FirstOrDefault();
            if (anyFile != null)
            {
                lockedStream = new FileStream(anyFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        };

        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("LivePhotoBox") == true)
                events.Enqueue(e.Exception);
        };

        try
        {
            var options = new LivePhotoMergeRunOptions
            {
                OutputDirectory = tempOut,
                OutputProfile = OutputProfile.Create(TargetProtocol.AppleLivePhoto, OutputMediaFormat.HeicMov),
                StructuredOptions = new() { PreservationPolicy = PreservationPolicy.BestEffort }
            };

            ProductOperationOutcome result;
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try
            {
                result = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(
                    sampleImage, sampleVideo, "test-pair", 1, options, CancellationToken.None);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= observer;
            }

            Assert.True(hookTriggered);
            Assert.Empty(events); // Zero first-chance exceptions from LivePhotoBox code!
            Assert.Equal(ProductOperationStatus.Warning, result.Status); // Yellow!
            Assert.Equal(ProductCompletionKind.CompatibleCompleted, result.CompletionKind);
            Assert.NotEmpty(result.CommittedOutputs); // Target output is committed and preserved!
            Assert.Null(result.SourceActionGuard); // Yellow prohibits source post-processing!
            Assert.Contains(result.Warnings, w => w.Contains("被占用") || w.Contains("受限"));
            Assert.True(!string.IsNullOrEmpty(result.Diagnostic.Message));

            foreach (var output in result.CommittedOutputs)
            {
                Assert.True(File.Exists(output.Path));
            }
            if (workspaceRoot is not null)
            {
                Assert.True(Directory.Exists(workspaceRoot)); // Workspace was preserved because cleanup had a limitation!
            }
        }
        finally
        {
            MediaWorkspace.TestCleanupHook.Value = previousHook;
            lockedStream?.Dispose();
            if (workspaceRoot is not null && Directory.Exists(workspaceRoot))
            {
                try { Directory.Delete(workspaceRoot, recursive: true); } catch { }
            }
            if (Directory.Exists(tempOut))
            {
                try { Directory.Delete(tempOut, recursive: true); } catch { }
            }
        }
    }
}
