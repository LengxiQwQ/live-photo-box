using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class MediaWorkspaceTests
{
    [Fact]
    public async Task Workspace_AllocatesPaths_ComputesHash_AndCleansUp()
    {
        string rootDir;
        using (var workspace = new MediaWorkspace())
        {
            rootDir = workspace.RootDirectory;
            Assert.True(Directory.Exists(rootDir));

            string tempFile = workspace.AllocateTempFilePath("test", "txt");
            Assert.StartsWith(rootDir, tempFile);

            await File.WriteAllTextAsync(tempFile, "Hello Media Workspace");
            string hash = await workspace.ComputeFileSha256Async(tempFile);
            Assert.False(string.IsNullOrWhiteSpace(hash));

            // Hash match assertion passes
            await workspace.AssertSourceUnmodifiedAsync(tempFile, hash);

            // Hash mismatch assertion throws
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => workspace.AssertSourceUnmodifiedAsync(tempFile, "INVALID_HASH"));
        }

        // After dispose, root directory should be cleaned up
        Assert.False(Directory.Exists(rootDir));
    }

    [Fact]
    public void Workspace_CreateSubdirectory_CreatesValidDirectory()
    {
        using var workspace = new MediaWorkspace();
        string subDir = workspace.CreateSubdirectory("custom_sub");
        Assert.True(Directory.Exists(subDir));
        Assert.StartsWith(workspace.RootDirectory, subDir);
    }
}
