/*
 * ITimelineProvider.cs
 *
 * 时间轴帧数据提供者契约。
 * 隔离 ViewModel 与底层抽帧引擎（Native / Mock / Null 等）。
 */

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

/// <summary>
/// 时间轴帧数据提供者接口。
/// </summary>
public interface ITimelineProvider
{
    /// <summary>当前提供者状态</summary>
    TimelineState State { get; }

    /// <summary>
    /// 为指定的编辑文档加载时间轴帧序列。
    /// </summary>
    /// <param name="doc">当前打开的编辑文档</param>
    /// <param name="token">取消令牌</param>
    /// <returns>时间轴帧序列集合</returns>
    Task<IReadOnlyList<TimelineFrame>> LoadTimelineFramesAsync(EditDocument doc, CancellationToken token = default);
}
