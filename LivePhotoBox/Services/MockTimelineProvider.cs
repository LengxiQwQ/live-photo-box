/*
 * MockTimelineProvider.cs
 *
 * 模拟时间轴帧提供者，用于纯前端脱机开发与 UI 预览自测。
 * 生成指定帧数的模拟帧数据（带封面帧、原始帧与时间戳）。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// 模拟时间轴帧提供者（脱机 UI 调试用）。
/// </summary>
public sealed class MockTimelineProvider : ITimelineProvider
{
    private readonly int _frameCount;
    private readonly int _keyFrameIndex;
    private readonly bool _hasOriginalPhoto;

    public TimelineState State { get; private set; } = TimelineState.NotLoaded;

    public MockTimelineProvider(int frameCount = 36, int keyFrameIndex = 12, bool hasOriginalPhoto = false)
    {
        _frameCount = Math.Clamp(frameCount, 10, 120);
        _keyFrameIndex = Math.Clamp(keyFrameIndex, 0, _frameCount - 1);
        _hasOriginalPhoto = hasOriginalPhoto;
    }

    public async Task<IReadOnlyList<TimelineFrame>> LoadTimelineFramesAsync(EditDocument doc, CancellationToken token = default)
    {
        State = TimelineState.Loading;

        try
        {
            // 模拟短延迟（50ms），还原真实异步加载体验
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            State = TimelineState.NotLoaded;
            throw;
        }

        var frames = new List<TimelineFrame>(_frameCount + (_hasOriginalPhoto ? 1 : 0));
        double totalDurationMs = 1500.0;
        double frameIntervalMs = totalDurationMs / _frameCount;

        for (int i = 0; i < _frameCount; i++)
        {
            token.ThrowIfCancellationRequested();
            bool isCover = (i == _keyFrameIndex);

            frames.Add(new TimelineFrame
            {
                FrameIndex = i,
                Timestamp = TimeSpan.FromMilliseconds(i * frameIntervalMs),
                IsStillPhoto = isCover,
                IsOriginalPhoto = false,
                IsSelected = isCover
            });
        }

        if (_hasOriginalPhoto)
        {
            frames.Insert(0, new TimelineFrame
            {
                FrameIndex = -1,
                Timestamp = TimeSpan.Zero,
                IsStillPhoto = false,
                IsOriginalPhoto = true,
                IsSelected = false
            });
        }

        State = TimelineState.Ready;
        return frames;
    }
}
