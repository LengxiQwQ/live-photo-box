/*
 * EditPreviewCoordinator.cs
 *
 * 负责大图预览生命周期核心逻辑的协调器（无 WinUI 依赖，便于跨层复用与轻量单元测试）。
 *
 * 职责：
 * 1. LRU 内存缓存管理（容量上限淘汰、命中更新、按键移除、清空）
 * 2. 请求代数与唯一标识（Request ID），杜绝过期回调/慢加载覆盖当前预览（Stale Request Protection）
 * 3. 关联取消令牌管理（发起新请求时自动取消前序加载请求）
 * 4. 临时预览文件追踪与生命周期清理（成功、失败、取消、清空、释放时绝不泄漏临时文件）
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LivePhotoBox.Services;

/// <summary>
/// 协调预览请求的缓存、取消、过期判断和临时文件追踪。
/// 泛型参数 <typeparamref name="TImage"/> 可以是 WinUI ImageSource，也可以是测试用的引用类型。
/// </summary>
public class EditPreviewCoordinator<TImage> : IDisposable where TImage : class
{
    private readonly int _maxCacheSize;
    private readonly Dictionary<string, TImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _cacheOrder = new();
    private readonly Lock _lock = new();

    private readonly HashSet<string> _trackedTempFiles = new(StringComparer.OrdinalIgnoreCase);

    private long _currentRequestId;
    private string? _latestRequestPath;
    private CancellationTokenSource? _activeCts;
    private bool _isDisposed;

    public int MaxCacheSize => _maxCacheSize;
    public string? LatestRequestPath => _latestRequestPath;
    public long CurrentRequestId => Volatile.Read(ref _currentRequestId);
    public bool IsDisposed => _isDisposed;

    public int CachedCount
    {
        get
        {
            lock (_lock)
            {
                return _cache.Count;
            }
        }
    }

    public int TrackedTempFileCount
    {
        get
        {
            lock (_lock)
            {
                return _trackedTempFiles.Count;
            }
        }
    }

    public EditPreviewCoordinator(int maxCacheSize = 3)
    {
        if (maxCacheSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCacheSize), "Max cache size must be greater than zero.");
        _maxCacheSize = maxCacheSize;
    }

    /// <summary>
    /// 开始一个新的预览请求。
    /// 自动取消前一次未完成的加载请求，生成自增的 Request ID，并返回关联的 CancellationToken。
    /// </summary>
    public long BeginRequest(string path, CancellationToken externalToken, out CancellationToken linkedToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_lock)
        {
            // 取消前一个请求
            if (_activeCts != null)
            {
                try
                {
                    _activeCts.Cancel();
                    _activeCts.Dispose();
                }
                catch { }
            }

            long reqId = Interlocked.Increment(ref _currentRequestId);
            _latestRequestPath = path;

            _activeCts = externalToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                : new CancellationTokenSource();

            linkedToken = _activeCts.Token;
            return reqId;
        }
    }

    /// <summary>
    /// 检查指定 Request ID 和路径是否仍为当前最新请求（防止过期的异步回调覆盖新图）。
    /// </summary>
    public bool IsCurrentRequest(long requestId, string path)
    {
        if (_isDisposed) return false;
        if (Volatile.Read(ref _currentRequestId) != requestId) return false;

        lock (_lock)
        {
            return string.Equals(_latestRequestPath, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 手动取消当前正在进行的加载请求，并将 Request ID 递增以立即使现有排队回调失效。
    /// </summary>
    public void CancelCurrent()
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _currentRequestId);
            _latestRequestPath = null;
            if (_activeCts != null)
            {
                try
                {
                    _activeCts.Cancel();
                    _activeCts.Dispose();
                }
                catch { }
                _activeCts = null;
            }
        }
    }

    /// <summary>
    /// 尝试从 LRU 缓存中获取已解码的预览图。若命中则更新其在 LRU 中的热度位置。
    /// </summary>
    public bool TryGetCached(string path, out TImage? image)
    {
        ThrowIfDisposed();
        image = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                // 移到最新访问位置
                _cacheOrder.Remove(path);
                _cacheOrder.Add(path);
                image = cached;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 将解码结果存入 LRU 缓存，超过上限时自动淘汰最久未访问的条目。
    /// </summary>
    public void PutCache(string path, TImage image)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);

        lock (_lock)
        {
            _cacheOrder.Remove(path);
            _cacheOrder.Add(path);
            _cache[path] = image;

            while (_cacheOrder.Count > _maxCacheSize)
            {
                string oldest = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                _cache.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 注册由解码器创建的临时预览文件路径（如 HEIC 解码转出的临时 JPEG），纳入生命周期管理。
    /// </summary>
    public void RegisterTempFile(string tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath)) return;
        lock (_lock)
        {
            _trackedTempFiles.Add(tempPath);
        }
    }

    /// <summary>
    /// 注销并删除已不再需要的临时预览文件。
    /// </summary>
    public void DeleteTempFile(string? tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath)) return;
        lock (_lock)
        {
            _trackedTempFiles.Remove(tempPath);
        }
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch { }
    }

    /// <summary>
    /// 清理所有当前被追踪的临时预览文件。
    /// </summary>
    public void CleanupAllTempFiles()
    {
        List<string> toDelete;
        lock (_lock)
        {
            toDelete = new List<string>(_trackedTempFiles);
            _trackedTempFiles.Clear();
        }

        foreach (var path in toDelete)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    /// <summary>
    /// 清空缓存、重置请求状态并清理所有未回收的临时文件。
    /// </summary>
    public void Clear()
    {
        CancelCurrent();
        lock (_lock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
        CleanupAllTempFiles();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Clear();
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(GetType().FullName);
    }
}
