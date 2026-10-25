using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class P9LegacyMutationBoundaryTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public unsafe void ProductionRawEntry_RejectsWithoutAuthorityAndDoesNotTouchBuffers(int entry)
    {
        byte[] input = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0, 0, 0, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'2', (byte)'m', (byte)'p', (byte)'4', (byte)'2',
            0, 0, 0, 8, (byte)'m', (byte)'o', (byte)'o', (byte)'v',
            0, 0, 0, 8, (byte)'m', (byte)'d', (byte)'a', (byte)'t'];
        byte[] baseline = input.ToArray();
        byte[] json = "vivo{\"com.android.camera.livephoto\":\"00112233445566778899aabbccdd\"}"u8.ToArray();
        byte[] output = Enumerable.Repeat((byte)0xA7, 512).ToArray();
        byte[] outputBaseline = output.ToArray();
        byte[] userType = "vivoMediaExtInfo\0"u8.ToArray();
        using var context = NativeContext.Create();
        fixed (byte* pIn = input)
        fixed (byte* pJson = json)
        fixed (byte* pOut = output)
        fixed (byte* pUser = userType)
        {
            // The same gate must cover full buffers, undersized buffers and output-size queries.
            foreach (int mode in new[] { 0, 1, 2 })
            {
                nuint written = 99;
                byte* destination = mode == 2 ? null : pOut;
                nuint capacity = mode == 0 ? (nuint)output.Length : 0;
                NativeResult result = entry switch
                {
                    0 => NativeMethods.HuaweiPatchHeicFtyp(context.Handle, pIn, (nuint)input.Length),
                    1 => NativeMethods.HuaweiPatchMp4(context.Handle, pIn, (nuint)input.Length),
                    2 => NativeMethods.RewriteVivoImageMetadata(context.Handle, pIn, (nuint)input.Length,
                        pJson, (nuint)json.Length, 1, destination, capacity, out written),
                    3 => NativeMethods.RewriteVivoVideoMetadata(context.Handle, pIn, (nuint)input.Length,
                        pJson, (nuint)json.Length, destination, capacity, out written),
                    4 => StripUuid(context.Handle, pIn, (nuint)input.Length, pUser, destination, capacity, out written),
                    5 => StripTracks(context.Handle, pIn, (nuint)input.Length, null, 0, destination, capacity, out written),
                    _ => StripMdta(context.Handle, pIn, (nuint)input.Length,
                        null, 0, null, 0, null, 0, destination, capacity, out written)
                };
                Assert.Equal(NativeResult.AuthorityViolation, result);
                if (entry >= 2) Assert.Equal((nuint)0, written);
                Assert.Equal(baseline, input);
                Assert.Equal(outputBaseline, output);
                Assert.Contains("AuthorityViolation", context.GetLastError());
            }
            Assert.Equal(NativeResult.InvalidArgument,
                NativeMethods.HuaweiPatchMp4(0, pIn, (nuint)input.Length));
        }
    }

    [Fact]
    public async Task ManagedHistoricalEntries_RejectBeforeAnyPathOrPredicateSideEffect()
    {
        string root = Workspace();
        Directory.CreateDirectory(root);
        string image = Path.Combine(root, "input.jpg");
        string video = Path.Combine(root, "input.mp4");
        string absent = Path.Combine(root, "never-created.heic");
        byte[] baseline = [0xFF, 0xD8, 0xFF, 0xD9];
        File.WriteAllBytes(image, baseline);
        File.WriteAllBytes(video, baseline);
        // Existing fixed-name legacy temporary objects must remain foreign and untouched.
        foreach (string suffix in new[] { ".lpb_uuid_strip.tmp", ".lpb_track_strip.tmp", ".lpb_mdta_strip.tmp" })
            File.WriteAllBytes(video + suffix, baseline);
        Assert.False(File.Exists(absent));
        string[] before = Directory.GetFileSystemEntries(root).OrderBy(x => x).ToArray();
        byte[] maker = AppleMakerNoteWriter.BuildMakerNote("11111111-2222-3333-4444-555566667777");
        Assert.NotEmpty(maker);

        Reject(AppleMakerNoteWriter.TryInjectIntoJpeg(image, maker, out string? error), error);
        Reject(AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(image, out error), error);
        Reject(AppleMakerNoteWriter.TryWriteContentIdentifier(image, "cid", out error), error);
        Reject(AppleMakerNoteWriter.TryInjectAppleMakerNoteIntoHeic(absent, "cid", out error), error);
        Reject(AppleMakerNoteWriter.TryInjectMakerNoteIntoHeic(absent, maker, out error), error);
        Reject(AppleMakerNoteWriter.TryNormalizeExifItem(absent, out error), error);
        Reject(AppleLivePhotoMebxWriter.TryAppendStillImageTrack(video, 0, out error), error);
        Reject(AppleLivePhotoMovBuilderV2.TryRebuild(video, "cid", "model", 0, out error), error);
        Reject(Mp4MdtaKeyStripper.TryStripHuaweiKeys(video, out error), error);
        Reject(Mp4MdtaKeyStripper.TryStripUuidBox(video, "vivoMediaExtInfo", out error), error);
        Reject(Mp4MdtaKeyStripper.TryStripTracks(video, ["live"], out error), error);
        Reject(Mp4MdtaKeyStripper.TryStripMebxTracks(video, out error), error);
        Reject(Mp4MdtaKeyStripper.TryStripMdtaKeys(video,
            (_, _) => throw new Xunit.Sdk.XunitException("Unauthorized predicate executed."), out error), error);
        Reject(HeifAuxImageWriter.TryAddHdrGainMapAux(absent, out error), error);
        Reject(HeifAuxImageWriter.TryAddNclxColr(absent, out error), error);
        Reject(HeifAuxImageWriter.TryAddNclxColr(absent, 1, 13, 6, 1, out error), error);
        Assert.Throws<NotSupportedException>(() => UltraHdrJpegWriter.Write(image, image, image, null!));
        Assert.Throws<NotSupportedException>(() => UltraHdrJpegWriter.Write(image, image, absent, null!));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            VivoDualFileMetadataWriter.WritePairMetadataAsync(image, video, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            VivoDualFileMetadataWriter.WritePairMetadataAsync(image, absent, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VivoDualFileMetadataWriter.WritePairMetadataAsync(image, video, new CancellationToken(true)));
        Assert.Equal(before, Directory.GetFileSystemEntries(root).OrderBy(x => x).ToArray());
        foreach (string file in before) Assert.Equal(baseline, File.ReadAllBytes(file));
    }

    [Fact]
    public void RawHarnessWriterHelpers_AreAbsentFromProductionAssembly()
    {
        var assembly = typeof(AppleMakerNoteWriter).Assembly;
        foreach (string name in new[] { "NativeJpegEditor", "NativeSamsungSef", "NativeMp4MdtaKeyStripper" })
            Assert.Null(assembly.GetType("LivePhotoBox.Interop." + name));
        Assert.DoesNotContain(assembly.GetTypes(), t => t.Name.StartsWith("Legacy", StringComparison.Ordinal)
            && t.Namespace == "LivePhotoBox.Services.Protocols");
    }

    private static void Reject(bool success, string? error)
    {
        Assert.False(success);
        Assert.Contains("AuthorityViolation", error);
    }

    private static string Workspace()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Live Photo Box.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, ".ai-tmp", "workspace", "P9", "target-writers", "R1", "legacy-entry-tests", "boundary");
    }

    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_mp4_strip_uuid_box", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe NativeResult StripUuid(nint context, byte* input, nuint size,
        byte* userType, byte* output, nuint capacity, out nuint written);
    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_mp4_strip_stsd_tracks", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe NativeResult StripTracks(nint context, byte* input, nuint size,
        nint* keys, nuint keyCount, byte* output, nuint capacity, out nuint written);
    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_mp4_strip_mdta_keys", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe NativeResult StripMdta(nint context, byte* input, nuint size,
        nint* starts, nuint startsCount, nint* contains, nuint containsCount, nint* values, nuint valuesCount,
        byte* output, nuint capacity, out nuint written);
}
