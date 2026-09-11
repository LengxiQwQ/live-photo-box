using System;
using System.IO;
using System.Threading;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Editor;

[Trait("Category", "EditPreview")]
public sealed class EditPreviewCoordinatorTests
{
    private sealed class MockImage
    {
        public string Path { get; }
        public MockImage(string path) => Path = path;
    }

    [Fact]
    public void SequentialRequests_LateResultDoesNotOverwriteNewerRequest()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);

        long reqA = coordinator.BeginRequest(@"C:\Photos\A.jpg", CancellationToken.None, out var tokenA);
        Assert.Equal(1, reqA);
        Assert.False(tokenA.IsCancellationRequested);
        Assert.True(coordinator.IsCurrentRequest(reqA, @"C:\Photos\A.jpg"));

        // 发起新请求 B，应自动打断并失效 A
        long reqB = coordinator.BeginRequest(@"C:\Photos\B.jpg", CancellationToken.None, out var tokenB);
        Assert.Equal(2, reqB);
        Assert.True(tokenA.IsCancellationRequested, "Request A's token should have been canceled.");
        Assert.False(tokenB.IsCancellationRequested);

        // 验证代数与路径防护：A 不再是当前有效请求
        Assert.False(coordinator.IsCurrentRequest(reqA, @"C:\Photos\A.jpg"));
        Assert.True(coordinator.IsCurrentRequest(reqB, @"C:\Photos\B.jpg"));
    }

    [Fact]
    public void CancelCurrent_CancelsTokenAndInvalidatesRequest()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);

        long req = coordinator.BeginRequest(@"C:\Photos\photo.jpg", CancellationToken.None, out var token);
        Assert.False(token.IsCancellationRequested);
        Assert.True(coordinator.IsCurrentRequest(req, @"C:\Photos\photo.jpg"));

        coordinator.CancelCurrent();

        Assert.True(token.IsCancellationRequested);
        Assert.False(coordinator.IsCurrentRequest(req, @"C:\Photos\photo.jpg"));
        Assert.Null(coordinator.LatestRequestPath);
    }

    [Fact]
    public void Cache_ReturnsExistingEntryOnSecondaryRequest()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        var img = new MockImage(@"C:\Photos\test.jpg");

        coordinator.PutCache(@"C:\Photos\test.jpg", img);

        bool hit = coordinator.TryGetCached(@"C:\Photos\test.jpg", out var cached);
        Assert.True(hit);
        Assert.Same(img, cached);
        Assert.Equal(1, coordinator.CachedCount);
    }

    [Fact]
    public void ActiveRequestA_ThenCachedB_InvalidatesA()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        var cachedImgB = new MockImage(@"C:\Photos\B.jpg");
        coordinator.PutCache(@"C:\Photos\B.jpg", cachedImgB);

        // 1. Begin/load A
        long reqA = coordinator.BeginRequest(@"C:\Photos\A.jpg", CancellationToken.None, out var tokenA);
        Assert.Equal(1, reqA);
        Assert.False(tokenA.IsCancellationRequested);
        Assert.True(coordinator.IsCurrentRequest(reqA, @"C:\Photos\A.jpg"));

        // 2. 请求 B（命中缓存）
        bool hit = coordinator.TryBeginCachedRequest(
            @"C:\Photos\B.jpg",
            CancellationToken.None,
            out long reqB,
            out var tokenB,
            out var cachedResult);

        // Assertions:
        // B cache hit
        Assert.True(hit);
        Assert.Same(cachedImgB, cachedResult);

        // A 必须立刻变 stale/cancelled
        Assert.True(tokenA.IsCancellationRequested, "In-flight request A must be cancelled upon cached B request.");
        Assert.False(coordinator.IsCurrentRequest(reqA, @"C:\Photos\A.jpg"), "Request A must become stale.");

        // current request 必须是 B
        Assert.True(coordinator.IsCurrentRequest(reqB, @"C:\Photos\B.jpg"));
        Assert.Equal(@"C:\Photos\B.jpg", coordinator.LatestRequestPath);
        Assert.Equal(reqB, coordinator.CurrentRequestId);
        Assert.False(tokenB.IsCancellationRequested);
    }

    [Fact]
    public void CachedRequest_GetsRealRequestId()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        var cachedImg = new MockImage(@"C:\Photos\cached.jpg");
        coordinator.PutCache(@"C:\Photos\cached.jpg", cachedImg);

        long initialId = coordinator.CurrentRequestId;
        bool hit = coordinator.TryBeginCachedRequest(
            @"C:\Photos\cached.jpg",
            CancellationToken.None,
            out long reqId,
            out _,
            out var result);

        Assert.True(hit);
        Assert.NotNull(result);
        Assert.NotEqual(0, reqId);
        Assert.True(reqId > initialId);
        Assert.Equal(reqId, coordinator.CurrentRequestId);
    }

    [Fact]
    public void CachedRequest_UpdatesLatestRequestPath()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        var cachedImgB = new MockImage(@"C:\Photos\B.jpg");
        coordinator.PutCache(@"C:\Photos\B.jpg", cachedImgB);

        coordinator.BeginRequest(@"C:\Photos\A.jpg", CancellationToken.None, out _);
        Assert.Equal(@"C:\Photos\A.jpg", coordinator.LatestRequestPath);

        coordinator.TryBeginCachedRequest(
            @"C:\Photos\B.jpg",
            CancellationToken.None,
            out _,
            out _,
            out _);

        Assert.Equal(@"C:\Photos\B.jpg", coordinator.LatestRequestPath);
    }

    [Fact]
    public void RapidSwitching_A_B_A_FinalIsActiveAndPreviousCancelled()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);

        // A (1)
        long reqA1 = coordinator.BeginRequest(@"C:\Photos\A.jpg", CancellationToken.None, out var tokenA1);
        // B (2)
        long reqB = coordinator.BeginRequest(@"C:\Photos\B.jpg", CancellationToken.None, out var tokenB);
        // A (3)
        long reqA2 = coordinator.BeginRequest(@"C:\Photos\A.jpg", CancellationToken.None, out var tokenA2);

        Assert.True(tokenA1.IsCancellationRequested);
        Assert.True(tokenB.IsCancellationRequested);
        Assert.False(tokenA2.IsCancellationRequested);

        Assert.False(coordinator.IsCurrentRequest(reqA1, @"C:\Photos\A.jpg"));
        Assert.False(coordinator.IsCurrentRequest(reqB, @"C:\Photos\B.jpg"));
        Assert.True(coordinator.IsCurrentRequest(reqA2, @"C:\Photos\A.jpg"));
        Assert.Equal(@"C:\Photos\A.jpg", coordinator.LatestRequestPath);
        Assert.Equal(reqA2, coordinator.CurrentRequestId);
    }

    [Fact]
    public void CacheEviction_ExceedingCapacityEvictsLeastRecentlyUsed()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);

        var img1 = new MockImage(@"C:\Photos\1.jpg");
        var img2 = new MockImage(@"C:\Photos\2.jpg");
        var img3 = new MockImage(@"C:\Photos\3.jpg");
        var img4 = new MockImage(@"C:\Photos\4.jpg");

        coordinator.PutCache(img1.Path, img1);
        coordinator.PutCache(img2.Path, img2);
        coordinator.PutCache(img3.Path, img3);
        Assert.Equal(3, coordinator.CachedCount);

        // 访问 1，使其变为最新访问（当前顺序：2(最旧), 3, 1(最新)）
        Assert.True(coordinator.TryGetCached(img1.Path, out _));

        // 插入 4，应淘汰最旧的 2
        coordinator.PutCache(img4.Path, img4);
        Assert.Equal(3, coordinator.CachedCount);

        Assert.False(coordinator.TryGetCached(img2.Path, out _), "img2 should have been evicted as LRU.");
        Assert.True(coordinator.TryGetCached(img1.Path, out _));
        Assert.True(coordinator.TryGetCached(img3.Path, out _));
        Assert.True(coordinator.TryGetCached(img4.Path, out _));
    }

    [Fact]
    public void Clear_ResetsCacheRequestStateAndCleansTempFiles()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_clear_{Guid.NewGuid():N}.jpg");
        File.WriteAllText(tempFile, "dummy");
        coordinator.RegisterTempFile(tempFile);

        coordinator.PutCache(@"C:\Photos\1.jpg", new MockImage(@"C:\Photos\1.jpg"));
        coordinator.BeginRequest(@"C:\Photos\2.jpg", CancellationToken.None, out _);

        coordinator.Clear();

        Assert.Equal(0, coordinator.CachedCount);
        Assert.Null(coordinator.LatestRequestPath);
        Assert.Equal(0, coordinator.TrackedTempFileCount);
        Assert.False(File.Exists(tempFile), "Temp file should be deleted on Clear.");
    }

    [Fact]
    public void Dispose_PreventsSubsequentOperationsAndCleansResources()
    {
        var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_disp_{Guid.NewGuid():N}.jpg");
        File.WriteAllText(tempFile, "dummy");
        coordinator.RegisterTempFile(tempFile);

        coordinator.Dispose();

        Assert.True(coordinator.IsDisposed);
        Assert.False(File.Exists(tempFile), "Temp file should be deleted on Dispose.");
        Assert.Throws<ObjectDisposedException>(() => coordinator.BeginRequest(@"C:\Photos\test.jpg", CancellationToken.None, out _));
        Assert.Throws<ObjectDisposedException>(() => coordinator.PutCache(@"C:\Photos\test.jpg", new MockImage(@"C:\Photos\test.jpg")));
        Assert.Throws<ObjectDisposedException>(() => coordinator.TryGetCached(@"C:\Photos\test.jpg", out _));
    }

    [Fact]
    public void TempFileLifecycle_DeleteTempFileDeletesTrackedAndPhysicalFile()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_del_{Guid.NewGuid():N}.jpg");
        File.WriteAllText(tempFile, "dummy");

        coordinator.RegisterTempFile(tempFile);
        Assert.Equal(1, coordinator.TrackedTempFileCount);

        coordinator.DeleteTempFile(tempFile);
        Assert.Equal(0, coordinator.TrackedTempFileCount);
        Assert.False(File.Exists(tempFile), "Physical file must be deleted.");
    }

    [Fact]
    public void TempFileLifecycle_CleansUpOnExceptionOrCancel()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_cancel_{Guid.NewGuid():N}.jpg");
        File.WriteAllText(tempFile, "dummy");

        coordinator.RegisterTempFile(tempFile);

        // 模拟解码过程中被取消或异常
        coordinator.CancelCurrent();
        coordinator.CleanupAllTempFiles();

        Assert.Equal(0, coordinator.TrackedTempFileCount);
        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public void TempRegistered_ThenOperationFailsBeforeOwnershipTransfer()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_fail_{Guid.NewGuid():N}.jpg");

        Action actFail = () =>
        {
            using var scope = coordinator.CreateTempFileScope(tempFile);
            File.WriteAllText(tempFile, "dummy partial write");
            Assert.True(File.Exists(tempFile));
            Assert.Equal(1, coordinator.TrackedTempFileCount);

            // 模拟在 encoder 或 FlushAsync 期间抛出异常，未能进入 TransferOwnership
            throw new InvalidOperationException("Simulation of failure during encode");
        };
        Assert.Throws<InvalidOperationException>(actFail);

        // 断言：内部作用域退出时 fail-closed，必须立即删除物理文件并清空跟踪
        Assert.False(File.Exists(tempFile), "Physical temp file must be deleted upon failure before ownership transfer.");
        Assert.Equal(0, coordinator.TrackedTempFileCount);
    }

    [Fact]
    public void TempRegistered_ThenCancelledBeforeOwnershipTransfer()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_canc_{Guid.NewGuid():N}.jpg");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Action actCancel = () =>
        {
            using var scope = coordinator.CreateTempFileScope(tempFile);
            File.WriteAllText(tempFile, "dummy partial write");
            Assert.True(File.Exists(tempFile));
            Assert.Equal(1, coordinator.TrackedTempFileCount);

            cts.Token.ThrowIfCancellationRequested();
            scope.TransferOwnership();
        };
        Assert.Throws<OperationCanceledException>(actCancel);

        Assert.False(File.Exists(tempFile), "Physical temp file must be deleted upon cancellation before ownership transfer.");
        Assert.Equal(0, coordinator.TrackedTempFileCount);
    }

    [Fact]
    public void Dispose_RacingWithTempRegistration()
    {
        var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_race_{Guid.NewGuid():N}.jpg");
        File.WriteAllText(tempFile, "dummy data created by background task");

        // UI 线程先调用了 Dispose
        coordinator.Dispose();
        Assert.True(coordinator.IsDisposed);

        // 后台任务此时尝试注册/创建作用域
        bool registered = coordinator.TryRegisterTempFile(tempFile);
        Assert.False(registered, "TryRegisterTempFile must reject registration when coordinator is disposed.");

        using (var scope = coordinator.CreateTempFileScope(tempFile))
        {
            // 作用域退出
        }

        Assert.False(File.Exists(tempFile), "Physical temp file must be deleted when racing with disposed coordinator.");
        Assert.Equal(0, coordinator.TrackedTempFileCount);
    }

    [Fact]
    public void TempFileScope_TransferOwnership_PreservesFileUntilOuterFinally()
    {
        using var coordinator = new EditPreviewCoordinator<MockImage>(maxCacheSize: 3);
        string tempFile = Path.Combine(Path.GetTempPath(), $"lpb_test_xfer_{Guid.NewGuid():N}.jpg");
        string transferredPath;

        using (var scope = coordinator.CreateTempFileScope(tempFile))
        {
            File.WriteAllText(tempFile, "valid decoded image data");
            transferredPath = scope.TransferOwnership();
            Assert.True(scope.IsTransferred);
        }

        // 作用域正常退出，但因已转移所有权，物理文件应保留给外层
        Assert.True(File.Exists(transferredPath), "Transferred temp file must NOT be deleted by inner scope.");
        Assert.Equal(1, coordinator.TrackedTempFileCount);

        // 外层 finally 执行 DeleteTempFile
        coordinator.DeleteTempFile(transferredPath);
        Assert.False(File.Exists(transferredPath));
        Assert.Equal(0, coordinator.TrackedTempFileCount);
    }
}
