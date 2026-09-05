using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed partial class ExtractorScaleTests
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    private static async Task<string> ComputeSliceSha256Async(string filePath, long offset, long length)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
            if (read == 0) break;
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    [Fact]
    public async Task Extract_SparseFileOver4Gb_CorrectlyExtracts64BitOffsetWithoutTruncation()
    {
        using var tempDir = new DisposableTempDirectory();
        string sparsePath = Path.Combine(tempDir.Path, "sparse_source.jpg");

        // Offset > 4GB (4,500,000,000 bytes > 4,294,967,296)
        long imgOffset = 4_500_000_000L;
        long imgLen = 1024;
        long vidOffset = 4_500_001_024L;
        long vidLen = 2048;
        long totalLogicalSize = 4_500_005_000L;

        byte[] imgData = new byte[imgLen];
        imgData[0] = 0xFF;
        imgData[1] = 0xD8;
        for (int i = 2; i < imgLen; i++) imgData[i] = 0x77;

        byte[] vidData = new byte[vidLen];
        vidData[4] = (byte)'f';
        vidData[5] = (byte)'t';
        vidData[6] = (byte)'y';
        vidData[7] = (byte)'p';
        for (int i = 8; i < vidLen; i++) vidData[i] = 0x88;

        using (var fs = new FileStream(sparsePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // Mark file as sparse so NTFS doesn't actually allocate 4.5 GB of disk blocks
            if (!DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero))
            {
                // If the volume doesn't support sparse files, skip
                return;
            }

            fs.SetLength(totalLogicalSize);

            fs.Seek(imgOffset, SeekOrigin.Begin);
            await fs.WriteAsync(imgData);

            fs.Seek(vidOffset, SeekOrigin.Begin);
            await fs.WriteAsync(vidData);

            await fs.FlushAsync();
        }

        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMicroVideoV1,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                ByteOffset = imgOffset,
                ByteLength = imgLen
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Container = VideoContainer.Mp4,
                ByteOffset = vidOffset,
                ByteLength = vidLen,
                SourceIndex = 0
            }
        };

        using var workspace = new MediaWorkspace();
        var extractor = new SourceExtractor();

        var bundle = await extractor.ExtractAsync(facts, sparsePath, null, workspace);

        // 1. Image assertions
        Assert.NotNull(bundle.PrimaryImage);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.Equal(imgLen, bundle.PrimaryImage.ByteLength);
        byte[] extractedImg = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path);
        Assert.Equal(imgData, extractedImg);

        // 2. Video assertions
        Assert.NotNull(bundle.MotionVideo);
        Assert.True(File.Exists(bundle.MotionVideo.Path));
        Assert.Equal(vidLen, bundle.MotionVideo.ByteLength);
        byte[] extractedVid = await File.ReadAllBytesAsync(bundle.MotionVideo.Path);
        Assert.Equal(vidData, extractedVid);

        // 3. Exact SHA match
        string expectedImgSha = await ComputeSliceSha256Async(sparsePath, imgOffset, imgLen);
        Assert.Equal(expectedImgSha, bundle.PrimaryImage.Sha256);
        string expectedVidSha = await ComputeSliceSha256Async(sparsePath, vidOffset, vidLen);
        Assert.Equal(expectedVidSha, bundle.MotionVideo.Sha256);
    }

    private sealed class DisposableTempDirectory : IDisposable
    {
        public string Path { get; }

        public DisposableTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lpb_scale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch { }
        }
    }
}
