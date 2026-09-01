using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Media.Workspace;

/// <summary>
/// Transaction workspace managing temporary media artifacts and source immutability checks.
/// </summary>
public interface IMediaWorkspace : IDisposable, IAsyncDisposable
{
    string RootDirectory { get; }

    string CreateSubdirectory(string name);

    string AllocateTempFilePath(string prefix, string extension);

    Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default);

    Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken ct = default);
}
