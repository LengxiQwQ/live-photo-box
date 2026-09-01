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
        string filePath;

        using (var workspace = new MediaWorkspace())
        {
            rootDir = workspace.RootDirectory;
            Assert.True(Directory.Exists(rootDir));

            filePath = workspace.AllocateFilePath("test", ".txt");
            Assert.StartsWith(rootDir, filePath, StringComparison.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(filePath, "Hello LivePhotoBox");
            string hash = await workspace.ComputeFileSha256Async(filePath);
            Assert.NotEmpty(hash);

            await workspace.AssertSourceUnmodifiedAsync(filePath, hash);
        }

        Assert.False(Directory.Exists(rootDir));
    }
}
