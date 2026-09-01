using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Media.Workspace;

/// <summary>
/// Contract for an isolated transaction media workspace.
/// </summary>
public interface IMediaWorkspace : IDisposable, IAsyncDisposable
{
    string RootDirectory { get; }
    string AllocateFilePath(string prefix, string extension);
    Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default);
    Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default);
}
