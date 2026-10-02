/*
 * TimelineState.cs
 *
 * 时间轴加载与展示状态枚举。
 */

namespace LivePhotoBox.Models;

/// <summary>
/// 时间轴加载与展示状态机。
/// </summary>
public enum TimelineState
{
    /// <summary>未加载</summary>
    NotLoaded = 0,

    /// <summary>正在加载/抽帧中</summary>
    Loading = 1,

    /// <summary>时间轴就绪，已有帧可显示</summary>
    Ready = 2,

    /// <summary>加载失败或抽帧发生错误</summary>
    Failed = 3
}
