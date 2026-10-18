using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
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

            byte[] contents = Encoding.UTF8.GetBytes("Hello LivePhotoBox");
            string creatingHandleSha256;
            long creatingHandleLength;
            WindowsFileIdentity creatingHandleIdentity;
            using (var output = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true))
            {
                await output.WriteAsync(contents);
                await output.FlushAsync();
                output.Flush(flushToDisk: true);

                creatingHandleIdentity = WindowsFileIdentity.Capture(output.SafeFileHandle);
                creatingHandleLength = output.Length;
                output.Position = 0;
                creatingHandleSha256 = Convert.ToHexString(await SHA256.HashDataAsync(output));

                Assert.Equal(contents.LongLength, creatingHandleLength);
                Assert.True(WorkspaceOwnershipRegistry.RegisterCreatedFile(
                    filePath,
                    creatingHandleIdentity,
                    creatingHandleLength,
                    creatingHandleSha256,
                    role: "MediaWorkspaceTests text fixture"));
            }

            string hash = await workspace.ComputeFileSha256Async(filePath);
            Assert.NotEmpty(hash);
            Assert.Equal(creatingHandleSha256, hash);

            await workspace.AssertSourceUnmodifiedAsync(filePath, hash);
        }

        Assert.False(Directory.Exists(rootDir));
    }
}
