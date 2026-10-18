using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class P7R2OwnedObjectProofTests
{
    [Fact]
    public void ObjectGoneProbe_WrongRecordedVolumeSerial_FailsClosed()
    {
        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath("r2-proof-volume-serial", ".bin");
        File.WriteAllBytes(path, [0x52, 0x32, 0x56]);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
        WindowsFileIdentity wrongVolume = identity with
        {
            VolumeSerialNumber = identity.VolumeSerialNumber ^ 0xFFFFFFFFu
        };
        Assert.Equal(ObjectAliveCheck.Unknown,
            WindowsOwnedFilePublisher.IsObjectAliveByFileId(wrongVolume, path));
        Assert.Equal(identity, WindowsFileIdentity.Capture(path));
        Assert.Equal(new byte[] { 0x52, 0x32, 0x56 }, File.ReadAllBytes(path));
    }

    [Fact]
    [Trait("Category", "R2")]
    [Trait("Category", "RealFilesystem")]
    public void ObjectGoneProbe_RealDifferentVolume_FailsClosedAndPreservesOwnedFile()
    {
        string volumeRoot = Environment.GetEnvironmentVariable("LPB_R2_DISK_ROOT")
            ?? throw new InvalidOperationException("The dedicated R2 VHDX must be mounted for this real-volume probe.");
        Assert.True(Directory.Exists(volumeRoot));
        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath("r2-proof-cross-volume", ".bin");
        File.WriteAllBytes(path, [0x52, 0x32, 0x56, 0x48, 0x44]);
        WindowsFileIdentity before = WindowsFileIdentity.Capture(path);
        string shaBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        WindowsFileIdentity otherVolume = CaptureDirectory(volumeRoot);
        Assert.NotEqual(before.VolumeSerialNumber, otherVolume.VolumeSerialNumber);
        ObjectAliveCheck result = WindowsOwnedFilePublisher.IsObjectAliveByFileId(before, volumeRoot);
        WindowsFileIdentity after = WindowsFileIdentity.Capture(path);
        string shaAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(ObjectAliveCheck.Unknown, result);
        Assert.Equal(before, after);
        Assert.Equal(shaBefore, shaAfter);
        string evidence = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE")
            ?? throw new InvalidOperationException("R2 evidence directory is required.");
        Directory.CreateDirectory(evidence);
        using FileStream output = new(Path.Combine(evidence, "object-gone-cross-volume-proof.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, new
        {
            operation = "IsObjectAliveByFileId", category = "RealFilesystem / different mounted volume",
            path, volumeRoot, before, after, otherVolume, shaBefore, shaAfter,
            result = result.ToString(), ownedObjectStillExists = File.Exists(path)
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static WindowsFileIdentity CaptureDirectory(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFileW(
            path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        return WindowsFileIdentity.Capture(handle);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
}
