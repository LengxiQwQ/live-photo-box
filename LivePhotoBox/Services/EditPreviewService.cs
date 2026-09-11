/*
 * EditPreviewService.cs
 *
 * Edit Page 的专用大图预览服务（UI-only Preview Owner）。
 * 对应重构路线图 EP3 — EditPreviewService / Preview Ownership Refactor。
 *
 * 核心架构原则：
 * 1. UI-only 能力：图片解码、Windows 原生格式支持、大图预览 LRU 缓存、临时文件生命周期均收归本 Service。
 * 2. 彻底解耦 ViewModel：ViewModel 仅表达"请求加载预览"，不直接接触解码器、缓存结构或临时文件。
 * 3. 严格的代数与取消保护：内置 Request ID 与 CancellationTokenSource，杜绝慢加载覆盖当前预览。
 * 4. 严密的临时资源管理：HEIC preview 解码转出的临时 JPEG 在成功、失败、取消及销毁时绝不泄漏。
 * 5. 纯净的返回结果：通过不可变 PreviewLoadResult 返回，不直接穿透修改 ViewModel 字段。
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace LivePhotoBox.Services;

/// <summary>
/// 预览加载结果模型。
/// </summary>
public sealed record PreviewLoadResult
{
    /// <summary>是否成功加载并解码</summary>
    public bool Success { get; init; }

    /// <summary>解码得到的 WinUI ImageSource（成功时非 null）</summary>
    public ImageSource? ImageSource { get; init; }

    /// <summary>请求的媒体路径</summary>
    public string? SourcePath { get; init; }

    /// <summary>失败原因（仅失败时非 null）</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>对应的预览请求代数 ID</summary>
    public long RequestId { get; init; }

    /// <summary>是否已被后续新请求打断失效或主动取消</summary>
    public bool IsStaleOrCancelled { get; init; }

    /// <summary>是否从内存 LRU 缓存直接命中返回</summary>
    public bool FromCache { get; init; }

    public static PreviewLoadResult Succeeded(string sourcePath, ImageSource imageSource, long requestId, bool fromCache = false) => new()
    {
        Success = true,
        ImageSource = imageSource,
        SourcePath = sourcePath,
        RequestId = requestId,
        FromCache = fromCache
    };

    public static PreviewLoadResult Failed(string sourcePath, string errorMessage, long requestId) => new()
    {
        Success = false,
        SourcePath = sourcePath,
        ErrorMessage = errorMessage,
        RequestId = requestId
    };

    public static PreviewLoadResult CancelledOrStale(string sourcePath, long requestId) => new()
    {
        Success = false,
        SourcePath = sourcePath,
        RequestId = requestId,
        IsStaleOrCancelled = true
    };
}

/// <summary>
/// Edit Page 大图预览服务实现。
/// </summary>
public sealed class EditPreviewService : IDisposable
{
    private const int DefaultMaxCacheSize = 3;
    private const int MaxDecodeDimension = 2560;

    private readonly EditPreviewCoordinator<ImageSource> _coordinator;
    private DispatcherQueue? _dispatcher;
    private int _activeLoadingCount;

    /// <summary>当前是否有预览正在解码加载中</summary>
    public bool IsLoading => Volatile.Read(ref _activeLoadingCount) > 0;

    /// <summary>最新请求的图片路径</summary>
    public string? LatestRequestPath => _coordinator.LatestRequestPath;

    /// <summary>当前缓存中的条目数</summary>
    public int CachedCount => _coordinator.CachedCount;

    public EditPreviewService(DispatcherQueue? dispatcher = null, int maxCacheSize = DefaultMaxCacheSize)
    {
        _coordinator = new EditPreviewCoordinator<ImageSource>(maxCacheSize);
        _dispatcher = dispatcher;
    }

    private DispatcherQueue? GetDispatcher()
    {
        if (_dispatcher == null)
        {
            try
            {
                _dispatcher = App.MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            }
            catch { }
        }
        return _dispatcher;
    }

    /// <summary>
    /// 异步加载指定图片的大图预览（默认最大尺寸 2560px）。
    /// 支持 JPEG / PNG 等原生格式以及 HEIC / HEIF（使用 BitmapDecoder 降采样解码）。
    /// </summary>
    public async Task<PreviewLoadResult> LoadImagePreviewAsync(string imagePath, CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return PreviewLoadResult.Failed(imagePath ?? string.Empty, "Image path cannot be empty.", 0);

        // 1. 开启新请求（无论 cache hit 还是 miss，都必须先确立新的 request identity 并自动取消前序请求）
        if (_coordinator.TryBeginCachedRequest(imagePath, externalToken, out long reqId, out var token, out var cached) && cached != null)
        {
            LogService.FileOp($"EditPreviewService: cache hit for '{Path.GetFileName(imagePath)}' (reqId={reqId})", LogLevel.Info);
            return PreviewLoadResult.Succeeded(imagePath, cached, reqId, fromCache: true);
        }

        if (!File.Exists(imagePath))
            return PreviewLoadResult.Failed(imagePath, $"File not found: {imagePath}", reqId);

        Interlocked.Increment(ref _activeLoadingCount);

        try
        {
            token.ThrowIfCancellationRequested();

            var dispatcher = GetDispatcher();
            if (dispatcher == null)
            {
                return PreviewLoadResult.Failed(imagePath, "UI DispatcherQueue is not available.", reqId);
            }

            bool isHeic = HeicConverterService.IsHeicFile(imagePath);
            ImageSource? decodedImage = null;

            if (isHeic)
            {
                decodedImage = await DecodeHeicPreviewAsync(imagePath, reqId, dispatcher, token).ConfigureAwait(false);
            }
            else
            {
                decodedImage = await DecodeStandardPreviewAsync(imagePath, reqId, dispatcher, token).ConfigureAwait(false);
            }

            // 2. 最终代数与取消检查
            if (token.IsCancellationRequested || !_coordinator.IsCurrentRequest(reqId, imagePath))
            {
                return PreviewLoadResult.CancelledOrStale(imagePath, reqId);
            }

            if (decodedImage == null)
            {
                return PreviewLoadResult.Failed(imagePath, "Decoder produced null image or dispatcher queue unavailable.", reqId);
            }

            // 3. 写入缓存并返回成功
            _coordinator.PutCache(imagePath, decodedImage);
            return PreviewLoadResult.Succeeded(imagePath, decodedImage, reqId);
        }
        catch (OperationCanceledException)
        {
            return PreviewLoadResult.CancelledOrStale(imagePath, reqId);
        }
        catch (Exception ex)
        {
            LogService.Debug($"EditPreviewService load failed for '{Path.GetFileName(imagePath)}': {ex.Message}", LogSource.UI);
            return PreviewLoadResult.Failed(imagePath, ex.Message, reqId);
        }
        finally
        {
            Interlocked.Decrement(ref _activeLoadingCount);
        }
    }

    /// <summary>
    /// 解码 HEIC / HEIF 图片：
    /// 后台线程使用 BitmapDecoder + BitmapTransform 降采样解码并写入临时 JPEG，
    /// 随后在 UI 线程加载为 BitmapImage，并在 finally 中彻底删除临时 JPEG。
    /// </summary>
    private async Task<ImageSource?> DecodeHeicPreviewAsync(
        string imagePath,
        long reqId,
        DispatcherQueue dispatcher,
        CancellationToken token)
    {
        string? tempJpegPath = null;
        try
        {
            // 后台线程：解码、缩放、编码为临时 JPEG
            tempJpegPath = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(token).ConfigureAwait(false);
                using var inputStream = await file.OpenAsync(FileAccessMode.Read).AsTask(token).ConfigureAwait(false);
                var decoder = await BitmapDecoder.CreateAsync(inputStream).AsTask(token).ConfigureAwait(false);

                uint origW = decoder.PixelWidth;
                uint origH = decoder.PixelHeight;
                double scale = origW > MaxDecodeDimension ? (double)MaxDecodeDimension / origW : 1.0;
                uint targetW = scale < 1.0 ? MaxDecodeDimension : origW;
                uint targetH = scale < 1.0 ? (uint)Math.Max(1, origH * scale) : origH;

                var transform = new BitmapTransform
                {
                    ScaledWidth = targetW,
                    ScaledHeight = targetH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb).AsTask(token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_prev_{Guid.NewGuid():N}.jpg");
                _coordinator.RegisterTempFile(tempPath);

                using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var encoder = await BitmapEncoder.CreateAsync(
                        BitmapEncoder.JpegEncoderId, fileStream.AsRandomAccessStream()).AsTask(token).ConfigureAwait(false);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync().AsTask(token).ConfigureAwait(false);
                }

                return tempPath;
            }, token).ConfigureAwait(false);

            if (token.IsCancellationRequested || !_coordinator.IsCurrentRequest(reqId, imagePath))
            {
                return null;
            }

            if (tempJpegPath == null || !File.Exists(tempJpegPath))
            {
                return null;
            }

            // UI 线程：从临时 JPEG 创建 BitmapImage
            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (token.IsCancellationRequested || !_coordinator.IsCurrentRequest(reqId, imagePath))
                    {
                        tcs.TrySetResult(null);
                        return;
                    }

                    var bmp = new BitmapImage { DecodePixelWidth = MaxDecodeDimension };
                    using var fs = new FileStream(tempJpegPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    bmp.SetSource(fs.AsRandomAccessStream());

                    if (!_coordinator.IsCurrentRequest(reqId, imagePath))
                    {
                        tcs.TrySetResult(null);
                        return;
                    }

                    tcs.TrySetResult(bmp);
                }
                catch (Exception ex)
                {
                    LogService.Debug($"EditPreviewService HEIC set source failed: {ex.Message}", LogSource.UI);
                    tcs.TrySetResult(null);
                }
            });

            if (!enqueued)
            {
                LogService.Debug("EditPreviewService: DispatcherQueue rejected HEIC enqueue (dispatcher shutdown).", LogSource.UI);
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // 无论成功、失败或取消，及时删除临时 JPEG
            if (tempJpegPath != null)
            {
                _coordinator.DeleteTempFile(tempJpegPath);
            }
        }
    }

    /// <summary>
    /// 解码普通图片（JPEG、PNG 等）：
    /// 后台读取 StorageFile，在 UI 线程通过 BitmapImage.SetSourceAsync 异步加载。
    /// </summary>
    private async Task<ImageSource?> DecodeStandardPreviewAsync(
        string imagePath,
        long reqId,
        DispatcherQueue dispatcher,
        CancellationToken token)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(token).ConfigureAwait(false);
        if (token.IsCancellationRequested || !_coordinator.IsCurrentRequest(reqId, imagePath))
        {
            return null;
        }

        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (token.IsCancellationRequested || !_coordinator.IsCurrentRequest(reqId, imagePath))
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var bmp = new BitmapImage { DecodePixelWidth = MaxDecodeDimension };
                using (var stream = await file.OpenReadAsync().AsTask(token))
                {
                    if (token.IsCancellationRequested || !_coordinator.IsCurrentRequest(reqId, imagePath))
                    {
                        tcs.TrySetResult(null);
                        return;
                    }

                    await bmp.SetSourceAsync(stream);
                }

                if (!_coordinator.IsCurrentRequest(reqId, imagePath))
                {
                    tcs.TrySetResult(null);
                    return;
                }

                tcs.TrySetResult(bmp);
            }
            catch (Exception ex)
            {
                LogService.Debug($"EditPreviewService standard decode failed: {ex.Message}", LogSource.UI);
                tcs.TrySetResult(null);
            }
        });

        if (!enqueued)
        {
            LogService.Debug("EditPreviewService: DispatcherQueue rejected standard decode enqueue (dispatcher shutdown).", LogSource.UI);
            return null;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 手动取消当前正在进行的加载操作。
    /// </summary>
    public void CancelCurrent()
    {
        _coordinator.CancelCurrent();
    }

    /// <summary>
    /// 清空预览缓存并清理未回收的临时文件。
    /// </summary>
    public void Clear()
    {
        _coordinator.Clear();
    }

    /// <summary>
    /// 释放服务资源并终止一切未完成操作。
    /// </summary>
    public void Dispose()
    {
        _coordinator.Dispose();
    }
}
