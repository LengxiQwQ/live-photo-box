using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

[Trait("Category", "NativeDifferential")]
public sealed class NativeStrictMediaValidationTests
{
    [Fact]
    public async Task VivoX300_CorruptedPrimaryWithValidTail_FailsClosed()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("vivo-corrupt-primary", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300CorruptedPrimaryJpeg(path);

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => new SourceInspector().InspectAsync(path));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
        Assert.Contains("Primary", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("stts")]
    [InlineData("stsz")]
    [InlineData("stsc")]
    [InlineData("stco")]
    public async Task AppleMov_CrossTableMismatch_FailsClosed(string table)
    {
        using var workspace = new MediaWorkspace();
        string imagePath = workspace.AllocateFilePath("apple-strict", ".jpg");
        string videoPath = workspace.AllocateFilePath("apple-strict", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imagePath);
        SyntheticProtocolFixtures.CreateAppleMov(videoPath);

        byte[] bytes = await File.ReadAllBytesAsync(videoPath);
        int type = bytes.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(table));
        Assert.True(type >= 4, $"Synthetic MOV did not contain {table}.");
        int box = type - 4;
        switch (table)
        {
            case "stts":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(box + 16, 4), 2);
                break;
            case "stsz":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(box + 16, 4), 2);
                break;
            case "stsc":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(box + 20, 4), 2);
                break;
            case "stco":
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(box + 16, 4), 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(table));
        }
        await File.WriteAllBytesAsync(videoPath, bytes);

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => new SourceInspector().InspectAsync(imagePath, videoPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
    }
}
