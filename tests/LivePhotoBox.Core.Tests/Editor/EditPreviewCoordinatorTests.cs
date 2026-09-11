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
}
