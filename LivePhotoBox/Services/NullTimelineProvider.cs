/*
 * NullTimelineProvider.cs
 *
 * 默认空时间轴提供者。返回空帧集合，保持当前的生产行为（底层 Native 接入前不抽帧）。
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// 默认空时间轴提供者。
/// </summary>
public sealed class NullTimelineProvider : ITimelineProvider
{
    public TimelineState State => TimelineState.NotLoaded;

    public Task<IReadOnlyList<TimelineFrame>> LoadTimelineFramesAsync(EditDocument doc, CancellationToken token = default)
    {
        return Task.FromResult<IReadOnlyList<TimelineFrame>>(Array.Empty<TimelineFrame>());
    }
}
