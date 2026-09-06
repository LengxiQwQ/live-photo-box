using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public class NativeGainMapReassemblyTests : IDisposable
{
    private readonly string _tempDir;

    public NativeGainMapReassemblyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lpb_gainmap_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in test teardown
        }
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_ValidJpegs_CreatesCombinedFile()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] primaryBytes = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x08, 0x45, 0x78, 0x69, 0x66, 0xFF, 0xD9];
        byte[] gainmapBytes = [0xFF, 0xD8, 0xFF, 0xE2, 0x00, 0x06, 0x4D, 0x50, 0xFF, 0xD9];

        await File.WriteAllBytesAsync(primaryPath, primaryBytes);
        await File.WriteAllBytesAsync(gainmapPath, gainmapBytes);

        await NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath);

        Assert.True(File.Exists(outputPath));
        byte[] outputBytes = await File.ReadAllBytesAsync(outputPath);
        Assert.Equal(primaryBytes.Length + gainmapBytes.Length, outputBytes.Length);

        byte[] expectedBytes = new byte[primaryBytes.Length + gainmapBytes.Length];
        Buffer.BlockCopy(primaryBytes, 0, expectedBytes, 0, primaryBytes.Length);
        Buffer.BlockCopy(gainmapBytes, 0, expectedBytes, primaryBytes.Length, gainmapBytes.Length);

        Assert.Equal(expectedBytes, outputBytes);
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_InvalidPrimaryMagic_ThrowsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "invalid_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] invalidPrimary = [0x00, 0x00, 0x01, 0x02];
        byte[] validGainmap = [0xFF, 0xD8, 0xFF, 0xD9];

        await File.WriteAllBytesAsync(primaryPath, invalidPrimary);
        await File.WriteAllBytesAsync(gainmapPath, validGainmap);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_InvalidGainmapMagic_ThrowsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "invalid_gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] validPrimary = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[] invalidGainmap = [0x89, 0x50, 0x4E, 0x47];

        await File.WriteAllBytesAsync(primaryPath, validPrimary);
        await File.WriteAllBytesAsync(gainmapPath, invalidGainmap);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_OutputAlreadyExists_FailsExclusiveCreate()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] validPrimary = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[] validGainmap = [0xFF, 0xD8, 0xFF, 0xD9];

        await File.WriteAllBytesAsync(primaryPath, validPrimary);
        await File.WriteAllBytesAsync(gainmapPath, validGainmap);
        await File.WriteAllBytesAsync(outputPath, [0x01, 0x02]);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath));
    }
}
