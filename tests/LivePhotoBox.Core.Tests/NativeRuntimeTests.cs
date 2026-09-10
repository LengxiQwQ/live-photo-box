using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class NativeRuntimeTests
{
    [Fact]
    public void Probe_LoadsMatchingNativeRuntime()
    {
        NativeRuntimeInfo info = NativeRuntime.Probe();

        Assert.True(info.IsAvailable, info.Diagnostic);
        Assert.Equal(NativeRuntime.SupportedAbiVersion, info.AbiVersion);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.NotEqual(0UL, info.Capabilities & NativeRuntime.FoundationCapability);

        string managedVersion = typeof(NativeRuntime).Assembly.GetName().Version!.ToString(4);
        Assert.Equal(managedVersion, info.Version);
    }

    [Fact]
    public async Task Probe_IsStableAcrossConcurrentContexts()
    {
        Task<NativeRuntimeInfo>[] probes = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(NativeRuntime.Probe))
            .ToArray();

        NativeRuntimeInfo[] results = await Task.WhenAll(probes);

        Assert.All(results, result => Assert.True(result.IsAvailable, result.Diagnostic));
        Assert.Single(results.Select(result => result.Version).Distinct());
    }

    [Fact]
    public void SupportedAbiVersion_IsFive()
    {
        Assert.Equal(5u, NativeRuntime.SupportedAbiVersion);
    }

    [Fact]
    public unsafe void SourceFacts_AuxiliaryCapacityDoesNotTruncate()
    {
        var native = new NativeSourceMediaFacts
        {
            StructSize = (uint)sizeof(NativeSourceMediaFacts),
            AuxiliaryCount = 9
        };

        SourceInspectionException error = Assert.Throws<SourceInspectionException>(() =>
            NativeMediaService.MapFromNativeFacts(native));
        Assert.Equal(SourceInspectionFailureCategory.Unsupported, error.Category);
        Assert.Equal(SourceInspectionStage.Container, error.Stage);
        Assert.Equal(NativeRuntime.FoundationCapability, error.Capability);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(999u)]
    public unsafe void CreateContext_MismatchedAbiVersion_FailsClosed(uint wrongAbi)
    {
        var options = new NativeContextOptions
        {
            StructSize = (uint)sizeof(NativeContextOptions),
            AbiVersion = wrongAbi
        };

        NativeResult res = NativeMethods.CreateContext((nint)(&options), out nint handle);
        Assert.Equal(NativeResult.AbiMismatch, res);
        Assert.Equal(nint.Zero, handle);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(10u)]
    public unsafe void CreateContext_InvalidStructSize_FailsClosed(uint invalidSize)
    {
        var options = new NativeContextOptions
        {
            StructSize = invalidSize,
            AbiVersion = NativeRuntime.SupportedAbiVersion
        };

        NativeResult res = NativeMethods.CreateContext((nint)(&options), out nint handle);
        Assert.Equal(NativeResult.InvalidArgument, res);
        Assert.Equal(nint.Zero, handle);
    }

    [Fact]
    public unsafe void NativeStructs_LayoutAgreement_MatchesNativeDefinition()
    {
        Assert.Equal(16, sizeof(NativeMediaRange));
        Assert.Equal(40, sizeof(NativeImageItemFacts));
        Assert.Equal(80, sizeof(NativeVideoItemFacts));
        Assert.Equal(72, (int)Marshal.OffsetOf<NativeVideoItemFacts>("SourceIndex"));

        Assert.Equal(112, sizeof(NativeGainMapItemFacts));
        Assert.Equal(32, (int)Marshal.OffsetOf<NativeGainMapItemFacts>("FileRange"));
        Assert.Equal(48, (int)Marshal.OffsetOf<NativeGainMapItemFacts>("Relationship"));
        Assert.Equal(2208, sizeof(NativeAuxiliaryItemFacts));
        Assert.Equal(104, (int)Marshal.OffsetOf<NativeAuxiliaryItemFacts>("Codec"));
        Assert.Equal(112, (int)Marshal.OffsetOf<NativeAuxiliaryItemFacts>("Sha256"));
        Assert.Equal(400, (int)Marshal.OffsetOf<NativeAuxiliaryItemFacts>("ItemType"));
        Assert.Equal(408, (int)Marshal.OffsetOf<NativeAuxiliaryItemFacts>("GraphFlags"));
        Assert.Equal(412, (int)Marshal.OffsetOf<NativeAuxiliaryItemFacts>("DependencyCount"));
        Assert.Equal(464, sizeof(NativePreservationCarrierFacts));
        Assert.Equal(32, sizeof(NativeTimingFacts));

        Assert.Equal(21872, sizeof(NativeSourceMediaFacts));
        Assert.Equal(128, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("GainMap"));
        Assert.Equal(240, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("Timing"));
        Assert.Equal(416, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("PrimarySha256"));
        Assert.Equal(448, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("SecondarySha256"));
        Assert.Equal(480, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("HasSecondarySource"));
        Assert.Equal(484, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("AuxiliaryCount"));
        Assert.Equal(18152, (int)Marshal.OffsetOf<NativeSourceMediaFacts>("PreservationCarrierCount"));

        Assert.Equal(348, sizeof(NativeConfirmedResidue));
        Assert.Equal(336, (int)Marshal.OffsetOf<NativeConfirmedResidue>("CoordinateSpace"));

        Assert.Equal(348, sizeof(NativeCleanupAction));
        Assert.Equal(336, (int)Marshal.OffsetOf<NativeCleanupAction>("CoordinateSpace"));
        Assert.Equal(340, (int)Marshal.OffsetOf<NativeCleanupAction>("RemovalMode"));
        Assert.Equal(344, (int)Marshal.OffsetOf<NativeCleanupAction>("IsMandatory"));
        Assert.Equal(56, sizeof(NativeCleanupArtifactBinding));
        Assert.Equal(524, sizeof(NativeRemovedProtocolFact));
    }

    [Fact]
    public unsafe void Sha256_StandardVectors_MatchDotNetImplementation()
    {
        Span<byte> outHash = stackalloc byte[32];

        // 1. Empty string
        fixed (byte* pHash = outHash)
        {
            NativeResult res = NativeMethods.TestSha256Buffer(null, 0, pHash);
            Assert.Equal(NativeResult.Ok, res);
            byte[] expected = System.Security.Cryptography.SHA256.HashData([]);
            Assert.True(outHash.SequenceEqual(expected));
        }

        // 2. "abc"
        byte[] abcBytes = "abc"u8.ToArray();
        fixed (byte* pData = abcBytes, pHash = outHash)
        {
            NativeResult res = NativeMethods.TestSha256Buffer(pData, (nuint)abcBytes.Length, pHash);
            Assert.Equal(NativeResult.Ok, res);
            byte[] expected = System.Security.Cryptography.SHA256.HashData(abcBytes);
            Assert.True(outHash.SequenceEqual(expected));
        }

        // 3. Multi-block payload
        byte[] multiBlock = new byte[1000];
        Array.Fill(multiBlock, (byte)'a');
        fixed (byte* pData = multiBlock, pHash = outHash)
        {
            NativeResult res = NativeMethods.TestSha256Buffer(pData, (nuint)multiBlock.Length, pHash);
            Assert.Equal(NativeResult.Ok, res);
            byte[] expected = System.Security.Cryptography.SHA256.HashData(multiBlock);
            Assert.True(outHash.SequenceEqual(expected));
        }
    }

    [Fact]
    public unsafe void Sha256File_StreamingOverNon64kAlignedFile_MatchesDotNet()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "lpb_sha_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] data = new byte[100_000];
            new Random(42).NextBytes(data);
            File.WriteAllBytes(tempFile, data);

            byte[] expectedHash = System.Security.Cryptography.SHA256.HashData(data);

            Span<byte> outHash = stackalloc byte[32];
            using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long initialPos = fs.Position;
            fixed (byte* pHash = outHash)
            {
                NativeResult res = NativeMethods.TestSha256File(fs.SafeFileHandle.DangerousGetHandle(), pHash);
                Assert.Equal(NativeResult.Ok, res);
            }
            Assert.True(outHash.SequenceEqual(expectedHash));
            Assert.Equal(initialPos, fs.Position);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public unsafe void Sha256File_ReadFailure_ReturnsIoErrorAndPreservesWin32Error()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "lpb_sha_fail_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(tempFile, new byte[1024]);
            using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Span<byte> outHash = stackalloc byte[32];
            fixed (byte* pHash = outHash)
            {
                NativeResult res = NativeMethods.TestSha256File(fs.SafeFileHandle.DangerousGetHandle(), pHash);
                Assert.NotEqual(NativeResult.Ok, res);
                int win32Err = Marshal.GetLastPInvokeError();
                Assert.True(win32Err != 0, $"Expected non-zero Win32 error, got {win32Err}");
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
