using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

/// <summary>Synthetic container/Win32 boundary checks; these are not real-sample preservation evidence.</summary>
public sealed class P6NativeObservationBoundaryTests
{
    [Theory]
    [InlineData(0, uint.MaxValue, 0)]
    [InlineData(1, uint.MaxValue, 0)]
    [InlineData(0, 2u, 0)]
    [InlineData(1, 2u, 0)]
    [InlineData(0, 1u, 2)]
    [InlineData(1, 1u, 2)]
    public void P6_IpmaCountBeyondAvailableBytes_FailsClosedAndReleasesFile(
        byte version, uint declaredEntries, byte declaredAssociations)
    {
        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath("ipma-count-budget", ".heic");
        File.WriteAllBytes(path, BuildHeif(version, declaredEntries, declaredAssociations));
        using var context = NativeContext.Create(default);

        // Direct ABI calls let us verify Unknown rather than just a managed exception.
        // Reopening exclusively on every iteration identifies a leaked handle for this
        // exact fixture without process-wide handle-count noise from parallel tests.
        for (int iteration = 0; iteration < 64; iteration++)
        {
            var orientation = new NativeImageOrientationObservationV1
            {
                StructSize = (uint)Marshal.SizeOf<NativeImageOrientationObservationV1>(),
                ApiVersion = 1,
                Status = NativeImageOrientationStatus.Verified,
                ClockwiseRotationDegrees = 270,
                RotationEvidence = NativeImageOrientationEvidence.Verified,
                ReflectionEvidence = NativeImageOrientationEvidence.Verified
            };
            Assert.Equal(NativeResult.Ok, NativeMethods.ObserveImageOrientationV1(
                context.Handle, path, (int)ImageContainer.Heic, ref orientation));
            Assert.Equal(NativeImageOrientationStatus.Unknown, orientation.Status);
            Assert.Equal(0, orientation.ClockwiseRotationDegrees);
            Assert.Equal(NativeImageOrientationEvidence.Unknown, orientation.RotationEvidence);
            Assert.Equal(NativeImageOrientationEvidence.Unknown, orientation.ReflectionEvidence);
            Assert.Equal(0u, orientation.Reserved0);

            var preservation = new NativePreservationObservation
            {
                StructSize = (uint)Marshal.SizeOf<NativePreservationObservation>()
            };
            Assert.Equal(NativeResult.Ok, NativeMethods.lpb_capture_preservation_observation(
                context.Handle, path, (int)SourceProtocol.Unknown, (int)ImageContainer.Heic, ref preservation));
            const uint iccParseError = 0x00020000u; // LPB_POBS_ICC_PARSE_ERROR
            Assert.NotEqual(0u, preservation.Flags & iccParseError);
            Assert.True(string.IsNullOrEmpty(preservation.IccSha256));

            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.Length > 0);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void P6_IpmaEntryAtExactByteBudget_RemainsSupported(byte version)
    {
        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath("ipma-exact-budget", ".heic");
        File.WriteAllBytes(path, BuildHeif(version, 1, 0));
        using var context = NativeContext.Create(default);
        var orientation = new NativeImageOrientationObservationV1
        {
            StructSize = (uint)Marshal.SizeOf<NativeImageOrientationObservationV1>(),
            ApiVersion = 1
        };
        Assert.Equal(NativeResult.Ok, NativeMethods.ObserveImageOrientationV1(
            context.Handle, path, (int)ImageContainer.Heic, ref orientation));
        Assert.Equal(NativeImageOrientationStatus.Verified, orientation.Status);
        Assert.Equal(0, orientation.ClockwiseRotationDegrees);
        Assert.Equal(NativeImageOrientationEvidence.Verified, orientation.RotationEvidence);
        Assert.Equal(NativeImageOrientationEvidence.Verified, orientation.ReflectionEvidence);
    }

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void P6_IpmaCompleteV1Graph_EnforcesObservationItemBudget(int entries, bool withinBudget)
    {
        // Every entry is physically present and has a distinct 32-bit id. The
        // negative case therefore reaches the resource budget, not the byte guard.
        byte[] ipma = new byte[8 + entries * 5];
        ipma[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(ipma.AsSpan(4), (uint)entries);
        for (int item = 0; item < entries; item++)
            BinaryPrimitives.WriteUInt32BigEndian(ipma.AsSpan(8 + item * 5), (uint)item);

        using var workspace = new TestFixtureWorkspace();
        string path = workspace.AllocateFilePath("ipma-complete-v1-budget", ".heic");
        File.WriteAllBytes(path, BuildHeifWithIpma(ipma));
        using var context = NativeContext.Create(default);
        var orientation = new NativeImageOrientationObservationV1
        {
            StructSize = (uint)Marshal.SizeOf<NativeImageOrientationObservationV1>(),
            ApiVersion = 1
        };
        Assert.Equal(NativeResult.Ok, NativeMethods.ObserveImageOrientationV1(
            context.Handle, path, (int)ImageContainer.Heic, ref orientation));
        Assert.Equal(withinBudget ? NativeImageOrientationStatus.Verified : NativeImageOrientationStatus.Unknown,
            orientation.Status);
        Assert.Equal(withinBudget ? NativeImageOrientationEvidence.Verified : NativeImageOrientationEvidence.Unknown,
            orientation.RotationEvidence);
        Assert.Equal(withinBudget ? NativeImageOrientationEvidence.Verified : NativeImageOrientationEvidence.Unknown,
            orientation.ReflectionEvidence);

        var preservation = new NativePreservationObservation
        {
            StructSize = (uint)Marshal.SizeOf<NativePreservationObservation>()
        };
        Assert.Equal(NativeResult.Ok, NativeMethods.lpb_capture_preservation_observation(
            context.Handle, path, (int)SourceProtocol.Unknown, (int)ImageContainer.Heic, ref preservation));
        Assert.Equal(withinBudget ? 0u : 0x00020000u, preservation.Flags & 0x00020000u);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(8 + entries * 5, ipma.Length);
    }

    private static byte[] BuildHeif(byte version, uint entries, byte associations)
    {
        byte[] entry = version == 0 ? [0, 1, associations] : [0, 0, 0, 1, associations];
        byte[] ipma = new byte[8 + entry.Length];
        ipma[0] = version;
        BinaryPrimitives.WriteUInt32BigEndian(ipma.AsSpan(4), entries);
        entry.CopyTo(ipma, 8); // Deliberately no bytes for the declared associations.
        return BuildHeifWithIpma(ipma);
    }

    private static byte[] BuildHeifWithIpma(byte[] ipma)
    {
        byte[] ipco = Box("ipco", Box("irot", [3]));
        byte[] iprp = Box("iprp", Join(ipco, Box("ipma", ipma)));
        byte[] pitm = Box("pitm", [0, 0, 0, 0, 0, 1]);
        return Join(Box("ftyp", [.. Encoding.ASCII.GetBytes("mif1"), 0, 0, 0, 0]),
            Box("meta", Join([0, 0, 0, 0], pitm, iprp)));
    }

    private static byte[] Box(string type, byte[] body)
    {
        byte[] result = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        body.CopyTo(result, 8);
        return result;
    }

    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(bytes => bytes).ToArray();
}

