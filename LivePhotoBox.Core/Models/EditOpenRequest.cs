/*
 * EditOpenRequest.cs
 *
 * 统一媒体打开请求与拖拽候选领域模型。
 * 对应重构路线图 EP4 — Unified Open / Drag & Drop Pipeline。
 *
 * 架构边界：
 * 1. 纯领域模型，不包含 UI / Presentation 类型（无 BitmapImage, ImageSource 等）。
 * 2. 不依赖特定操作系统 UI 句柄或 DispatcherQueue。
 * 3. 统一规范单文件、双文件实况照片及独立视频的打开请求。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Models;

/// <summary>
/// 请求打开/加载媒体的统一不可变请求模型。
/// </summary>
public sealed record EditOpenRequest
{
    /// <summary>主媒体文件完整路径（照片或独立视频）</summary>
    public required string PrimaryPath { get; init; }

    /// <summary>动态视频完整路径（若外部已知，例如双文件实况照片；可选）</summary>
    public string? MotionPath { get; init; }

    public EditOpenRequest() { }

    [SetsRequiredMembers]
    public EditOpenRequest(string primaryPath, string? motionPath = null)
    {
        PrimaryPath = primaryPath;
        MotionPath = motionPath;
    }
}

/// <summary>
/// 拖拽或批量扫描发现的候选媒体项事实。
/// </summary>
public sealed record EditOpenCandidate
{
    /// <summary>主媒体文件完整路径</summary>
    public required string PrimaryPath { get; init; }

    /// <summary>配对动态视频完整路径（双文件实况照片时有效）</summary>
    public string? MotionPath { get; init; }

    /// <summary>实况照片物理形态</summary>
    public LivePhotoType LivePhotoType { get; init; } = LivePhotoType.None;

    /// <summary>Native Inspector 探测的细分协议</summary>
    public SourceProtocol Protocol { get; init; } = SourceProtocol.Unknown;

    /// <summary>单文件实况照片尾部追加视频字节长度</summary>
    public long AppendedVideoLength { get; init; }

    /// <summary>媒体总大小（包含配对视频）</summary>
    public long TotalByteSize { get; init; }

    /// <summary>文件修改时间</summary>
    public DateTime DateModified { get; init; }

    public EditOpenCandidate() { }

    [SetsRequiredMembers]
    public EditOpenCandidate(
        string primaryPath,
        string? motionPath = null,
        LivePhotoType livePhotoType = LivePhotoType.None,
        SourceProtocol protocol = SourceProtocol.Unknown,
        long appendedVideoLength = 0,
        long totalByteSize = 0,
        DateTime dateModified = default)
    {
        PrimaryPath = primaryPath;
        MotionPath = motionPath;
        LivePhotoType = livePhotoType;
        Protocol = protocol;
        AppendedVideoLength = appendedVideoLength;
        TotalByteSize = totalByteSize;
        DateModified = dateModified;
    }
}

/// <summary>
/// 拖拽批处理处理结果。
/// </summary>
public sealed record EditDropResult
{
    /// <summary>所有识别并归并后的候选媒体项</summary>
    public IReadOnlyList<EditOpenCandidate> Candidates { get; init; } = Array.Empty<EditOpenCandidate>();

    /// <summary>首个被选为主媒体并提交打开的请求（无有效媒体时为 null）</summary>
    public EditOpenRequest? OpenedRequest { get; init; }

    /// <summary>首个主媒体打开操作是否成功</summary>
    public bool Succeeded { get; init; }
}
