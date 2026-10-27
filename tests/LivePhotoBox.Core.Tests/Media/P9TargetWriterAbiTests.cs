using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using Xunit;
using NativeFileIdentity = LivePhotoBox.Interop.NativeCleanService.NativeFileIdentity;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9TargetWriterAbiTests
{
    [Fact]
    public void VersionedWriterLayoutsMatchNativeX64TransportWithoutPlatformHandles()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Marshal.SizeOf<NativeFileIdentity>());
        Assert.Equal(1112, Marshal.SizeOf<NativeTargetWriterArtifactV1>());
        Assert.Equal(24, Marshal.OffsetOf<NativeTargetWriterArtifactV1>(nameof(NativeTargetWriterArtifactV1.ExpectedIdentity)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<NativeTargetWriterArtifactV1>(nameof(NativeTargetWriterArtifactV1.Sha256)).ToInt32());
        Assert.Equal(88, Marshal.OffsetOf<NativeTargetWriterArtifactV1>(nameof(NativeTargetWriterArtifactV1.PathUtf8)).ToInt32());
        Assert.Equal(1080, Marshal.SizeOf<NativeTargetWriterRequestV1>());
        Assert.Equal(32, Marshal.OffsetOf<NativeTargetWriterRequestV1>(nameof(NativeTargetWriterRequestV1.Artifacts)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<NativeTargetWriterRequestV1>(nameof(NativeTargetWriterRequestV1.CoverTimestampUs)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<NativeTargetWriterRequestV1>(nameof(NativeTargetWriterRequestV1.StagingDirectoryUtf8)).ToInt32());
        Assert.Equal(144, Marshal.SizeOf<NativeTargetWriterOutputV1>());
        Assert.Equal(48, Marshal.OffsetOf<NativeTargetWriterOutputV1>(nameof(NativeTargetWriterOutputV1.Identity)).ToInt32());
        Assert.Equal(80, Marshal.OffsetOf<NativeTargetWriterOutputV1>(nameof(NativeTargetWriterOutputV1.Sha256)).ToInt32());
        Assert.Equal(112, Marshal.OffsetOf<NativeTargetWriterOutputV1>(nameof(NativeTargetWriterOutputV1.RequestFingerprint)).ToInt32());
        Assert.Equal(32, Marshal.SizeOf<NativeTargetWriterStatusV1>());
        Assert.Equal(24, Marshal.OffsetOf<NativeTargetWriterStatusV1>(nameof(NativeTargetWriterStatusV1.Generation)).ToInt32());
        Assert.Equal(1272, Marshal.SizeOf<NativeTargetWriterRequestV3>());
        Assert.Equal(1256, Marshal.OffsetOf<NativeTargetWriterRequestV3>(nameof(NativeTargetWriterRequestV3.CompoundPrimaryToken)).ToInt32());
        Assert.Equal(56, Marshal.SizeOf<NativeCompoundPrimaryObservation>());
    }

    [Fact]
    public unsafe void MalformedWriterRequestCannotIssueAuthorityOrProduceOutput()
    {
        using var context = NativeContext.Create(CancellationToken.None);
        using var operation = context.AcquireOperationLease();
        var request = new NativeTargetWriterRequestV1
        {
            StructSize = (uint)sizeof(NativeTargetWriterRequestV1), Version = 1,
            TargetProfile = 2, TargetVersion = 1, MediaFormat = 0, ArtifactCount = 0
        };
        NativeResult result = NativeMethods.TargetWriterPrepare(operation.Handle, &request, out nint plan);
        Assert.NotEqual(NativeResult.Ok, result);
        Assert.Equal(nint.Zero, plan);
    }
}
