using System;
using System.Buffers.Binary;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

/// <summary>Native final-timeline evidence. Derived malformed/default fixtures
/// are explicitly synthetic, not independent media preservation evidence.</summary>
public sealed class P6NeutralTimingObservationTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Observation
    {
        public uint Size, Flags;
        public int SourceEvidence, Applicability;
        public long Cover, Primary;
        public int CoverFrame, TotalFrames;
        public long Duration;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PrimaryHash;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] SecondaryHash;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] FinalHash;
    }

    [DllImport(NativeMethods.LibraryName, EntryPoint = "lpb_observe_neutral_timing_v1", CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeResult Observe(nint context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string primary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? secondary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string final, ref Observation observation);

    // Values are independently read protocol scalars / documented metadata-track
    // edit durations, not the old SourceFacts.Timing projection. -1 means the
    // protocol uses a frame ordinal and the exact PTS must come from stts/ctts.
    [Theory]
    [InlineData("红米老款-GV1.JPG", null, 868816L, false, -1)]
    [InlineData("小米.jpg", null, 865167L, false, -1)]
    [InlineData("oppo.jpg", null, 1332432L, true, -1)]
    [InlineData("一加.jpg", null, 1466388L, true, -1)]
    [InlineData("一加-改了封面照片.jpg", null, 2762000L, true, -1)]
    [InlineData("vivo.jpg", null, 1450273L, false, -1)]
    [InlineData("三星.jpg", null, 1506914L, false, -1)]
    [InlineData("三星.heic", null, 4251628L, false, -1)]
    [InlineData("华为-Mate80.jpg", null, 1333693L, false, -1)]
    [InlineData("华为Mate80.heic", null, 1300352L, false, -1)]
    [InlineData("荣耀.jpg", null, -1L, false, 43)]
    [InlineData("vivo双文件.jpg", "vivo双文件.mp4", -1L, true, 39)]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV", 1433334L, false, 43)]
    // Independent ffprobe integer packet PTS: visible ordinal 44 is 900/600 s.
    // The sample has a PTS gap, so multiplying seconds by nominal FPS is wrong.
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV", 1500000L, false, 44)]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_AllSourceRoutes_ProducePresenceAndActualTimeline(
        string primaryName, string? secondaryName, long expectedCover, bool expectedPrimary, int expectedFrame)
    {
        string primary = Cached(primaryName);
        string? secondary = secondaryName is null ? null : Cached(secondaryName);
        using var fixture = new TestFixtureWorkspace();
        string final = await ExtractVideo(primary, secondary, fixture);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(144, Marshal.SizeOf<Observation>());
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, secondary, final, ref result));
        // Frozen Samsung HEIC declares 4.251628 s, but its embedded video is
        // 72 frames / 2.689478 s (independent ffprobe observation). The scalar
        // is syntactically valid and semantically unusable, not a new epoch.
        if (primaryName == "三星.heic")
        {
            Assert.Equal(0, result.SourceEvidence);
            Assert.Equal(0, result.Applicability);
            Assert.Equal(0u, result.Flags & (1u | 2u | 4u));
            Assert.Equal(72, result.TotalFrames);
            Assert.InRange(result.Duration, 2_689_000L, 2_690_000L);
            return;
        }
        Assert.Equal(1, result.SourceEvidence);
        Assert.Equal(1, result.Applicability);
        Assert.Equal(1u | 4u | 8u | 16u, result.Flags & (1u | 4u | 8u | 16u));
        Assert.Equal(expectedPrimary, (result.Flags & 2u) != 0);
        if (!expectedPrimary) Assert.Equal(0, result.Primary); // value is absent, not verified zero
        if (expectedCover >= 0) Assert.Equal(expectedCover, result.Cover);
        if (expectedFrame >= 0) Assert.Equal(expectedFrame, result.CoverFrame);
        Assert.InRange(result.CoverFrame, 0, result.TotalFrames - 1);
        Assert.InRange(result.Cover, 0, result.Duration);
        Assert.True(result.Duration > 0);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(primary)), result.PrimaryHash);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(final)), result.FinalHash);
        Assert.Equal(secondary is not null, (result.Flags & 32u) != 0);
        if (secondary is not null) Assert.Equal(SHA256.HashData(File.ReadAllBytes(secondary)), result.SecondaryHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("0")]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedV2MissingMinusOneAndExplicitZero_AreDistinct(string? value)
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = fixture.AllocateFilePath("derived-v2", ".jpg");
        File.WriteAllBytes(primary, ReplaceCurrentTime(File.ReadAllBytes(Cached("oppo.jpg")), value));
        string final = await ExtractVideo(primary, null, fixture);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, null, final, ref result));
        Assert.Equal(value == "0" ? 1 : 2, result.SourceEvidence);
        Assert.Equal(1, result.Applicability);
        Assert.NotEqual(0u, result.Flags & 1u);
        if (value == "0") { Assert.Equal(0, result.Cover); Assert.Equal(0, result.CoverFrame); }
        else Assert.InRange(result.Cover, result.Duration / 2 - 100_000, result.Duration / 2);
        Assert.NotEqual(0u, result.Flags & 2u); // OPPO original position remains independently present
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedOverflowPosition_FailsClosedAndReleasesFiles()
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = fixture.AllocateFilePath("derived-invalid-time", ".jpg");
        File.WriteAllBytes(primary, ReplaceCurrentTime(File.ReadAllBytes(Cached("oppo.jpg")), long.MaxValue.ToString()));
        string final = await ExtractVideo(primary, null, fixture);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.InvalidArgument, Observe(context.Handle, primary, null, final, ref result));
        Assert.Equal(0u, result.Flags);
        Assert.Equal(0, result.Applicability);
        using var primaryLease = new FileStream(primary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var finalLease = new FileStream(final, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("trailing-object")]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedVivoAmbiguousJson_FailsClosed(string corruption)
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = Cached("vivo双文件.jpg");
        string secondary = fixture.AllocateFilePath("ambiguous-vivo", ".mp4");
        byte[] modified = RewriteVivoUuid(File.ReadAllBytes(Cached("vivo双文件.mp4")), json =>
            corruption == "duplicate" ? json[..^1] + ",\"com.android.camera.imageTime\":0}" : json + "{}");
        File.WriteAllBytes(secondary, modified);
        string final = await ExtractVideo(primary, secondary, fixture);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.InvalidArgument, Observe(context.Handle, primary, secondary, final, ref result));
        Assert.Equal(0u, result.Flags);
        Assert.Equal(0, result.Applicability);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedVivoEditedEndSentinel_SelectsLastFrameAndRetainsOriginal()
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = Cached("vivo双文件.jpg");
        string baselineSecondary = Cached("vivo双文件.mp4");
        string baselineFinal = await ExtractVideo(primary, baselineSecondary, fixture);
        using var context = NativeContext.Create(default);
        Observation baseline = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, baselineSecondary, baselineFinal, ref baseline));
        long endMilliseconds = baseline.Duration / 1000;
        string secondary = fixture.AllocateFilePath("edited-end-vivo", ".mp4");
        File.WriteAllBytes(secondary, RewriteVivoUuid(File.ReadAllBytes(baselineSecondary), json =>
            json[..^1] + ",\"com.vivo.gallery.livePhoto.newCoverTime\":" + endMilliseconds + "}"));
        string final = await ExtractVideo(primary, secondary, fixture);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, secondary, final, ref result));
        Assert.Equal(1, result.Applicability);
        Assert.Equal(result.TotalFrames - 1, result.CoverFrame);
        Assert.True(result.Cover < result.Duration);
        Assert.NotEqual(0u, result.Flags & 2u);
        Assert.Equal(baseline.Primary, result.Primary);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedVivoRoundedUpDuration_IsNotAnAuthorizedEndSentinel()
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = Cached("vivo双文件.jpg"), original = Cached("vivo双文件.mp4");
        string final = await ExtractVideo(primary, original, fixture);
        using var context = NativeContext.Create(default);
        Observation baseline = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, original, final, ref baseline));
        Assert.NotEqual(0, baseline.Duration % 1000);
        long roundedUp = (baseline.Duration + 999) / 1000;
        string secondary = fixture.AllocateFilePath("invalid-end-vivo", ".mp4");
        File.WriteAllBytes(secondary, RewriteVivoUuid(File.ReadAllBytes(original), json =>
            json[..^1] + ",\"com.vivo.gallery.livePhoto.newCoverTime\":" + roundedUp + "}"));
        Observation result = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, secondary, final, ref result));
        Assert.Equal(0, result.Applicability);
        Assert.Equal(0u, result.Flags & (1u | 2u | 4u));
        Assert.NotEqual(0u, result.Flags & 16u);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, false)]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedPresentationSchedules_MapWithinIntervalsOtherwiseUnknown(int tickDelta, bool maps)
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = Cached("vivo双文件.jpg"), secondary = Cached("vivo双文件.mp4");
        string final = await ExtractVideo(primary, secondary, fixture);
        byte[] bytes = File.ReadAllBytes(final);
        int moov = FindChild(bytes, 0, bytes.Length, "moov");
        int track = Children(bytes, moov + 8, moov + Size(bytes, moov)).First(p =>
        {
            if (!bytes.AsSpan(p + 4, 4).SequenceEqual("trak"u8)) return false;
            int mdia = FindChild(bytes, p + 8, p + Size(bytes, p), "mdia");
            int handler = FindChild(bytes, mdia + 8, mdia + Size(bytes, mdia), "hdlr");
            return bytes.AsSpan(handler + 16, 4).SequenceEqual("vide"u8);
        });
        Assert.Equal(-1, FindChild(bytes, track + 8, track + Size(bytes, track), "edts", optional: true));
        int mdia = FindChild(bytes, track + 8, track + Size(bytes, track), "mdia");
        int mdhd = FindChild(bytes, mdia + 8, mdia + Size(bytes, mdia), "mdhd");
        uint scale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdhd + 20));
        int minf = FindChild(bytes, mdia + 8, mdia + Size(bytes, mdia), "minf");
        int stbl = FindChild(bytes, minf + 8, minf + Size(bytes, minf), "stbl");
        Assert.Equal(-1, FindChild(bytes, stbl + 8, stbl + Size(bytes, stbl), "ctts", optional: true));
        int stts = FindChild(bytes, stbl + 8, stbl + Size(bytes, stbl), "stts");
        uint runs = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stts + 12));
        long coverTicks = 0;
        int ordinal = 0;
        for (int run = 0; run < runs; run++)
        {
            int entry = stts + 16 + 8 * run;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry));
            uint delta = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 4));
            uint changed = checked(delta + (uint)tickDelta);
            for (int frame = 0; frame < count; frame++, ordinal++) if (ordinal < 39) coverTicks += changed;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), changed);
        }
        File.WriteAllBytes(final, bytes);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, secondary, final, ref result));
        Assert.Equal(1, result.SourceEvidence);
        if (maps)
        {
            Assert.Equal(2, result.Applicability);
            Assert.Equal(39, result.CoverFrame);
            Assert.Equal((coverTicks * 1_000_000 + scale - 1) / scale, result.Cover);
            Assert.Equal(result.Cover, result.Primary);
            Assert.NotEqual(0u, result.Flags & 2u);
        }
        else
        {
            Assert.Equal(0, result.Applicability);
            Assert.Equal(0u, result.Flags & (1u | 2u | 4u));
            Assert.NotEqual(0u, result.Flags & 16u);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DifferentFinalFrameCount_KeepsFinalFactsAndRejectsSourcePositions()
    {
        using var fixture = new TestFixtureWorkspace();
        string final = await ExtractVideo(Cached("oppo.jpg"), null, fixture);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, Cached("vivo双文件.jpg"), Cached("vivo双文件.mp4"), final, ref result));
        Assert.Equal(0, result.Applicability);
        Assert.Equal(0u, result.Flags & (1u | 2u | 4u));
        Assert.True(result.TotalFrames > 0);
        Assert.NotEqual(61, result.TotalFrames);
        Assert.True(result.Duration > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_FiniteSourceTimeBeyondActualVideo_IsUnknownWithFinalFacts()
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = fixture.AllocateFilePath("outside-video", ".jpg");
        File.WriteAllBytes(primary, ReplaceCurrentTime(File.ReadAllBytes(Cached("oppo.jpg")), "9000000"));
        string final = await ExtractVideo(primary, null, fixture);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.Ok, Observe(context.Handle, primary, null, final, ref result));
        Assert.Equal(0, result.SourceEvidence);
        Assert.Equal(0, result.Applicability);
        Assert.Equal(0u, result.Flags & (1u | 2u | 4u));
        Assert.Equal(8u | 16u, result.Flags & (8u | 16u));
        Assert.True(result.Duration > 0);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("wrong-type")]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedHuaweiMissingOrWrongMetadataHandler_IsRejected(string corruption)
    {
        using var fixture = new TestFixtureWorkspace();
        string final = await ExtractVideo(Cached("华为-Mate80.jpg"), null, fixture);
        byte[] bytes = File.ReadAllBytes(Cached("华为-Mate80.jpg"));
        int key = bytes.AsSpan().IndexOf("com.openharmony.covertime"u8);
        Assert.True(key > 0);
        int handler = bytes.AsSpan(0, key).LastIndexOf("hdlr"u8);
        Assert.True(handler > 0);
        if (corruption == "absent") "free"u8.CopyTo(bytes.AsSpan(handler));
        else "mdir"u8.CopyTo(bytes.AsSpan(handler + 12));
        string primary = fixture.AllocateFilePath("bad-mdta-handler", ".jpg");
        File.WriteAllBytes(primary, bytes);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.InvalidArgument, Observe(context.Handle, primary, null, final, ref result));
        Assert.Equal(0u, result.Flags);
    }

    [Theory]
    [InlineData("key-id")]
    [InlineData("duplicate-key-id")]
    [InlineData("unused-description")]
    [InlineData("edit-skips-sample")]
    [InlineData("nonzero-composition-offset")]
    [Trait("Category", "RealSamples")]
    public async Task P6_Timing_DerivedAppleMetadataWithoutActualSampleBinding_IsRejected(string corruption)
    {
        using var fixture = new TestFixtureWorkspace();
        string primary = Cached("苹果双文件.HEIC");
        string final = await ExtractVideo(primary, Cached("苹果双文件.MOV"), fixture);
        byte[] bytes = File.ReadAllBytes(Cached("苹果双文件.MOV"));
        if (corruption == "key-id")
        {
            byte[] sample = [0, 0, 0, 9, 0, 0, 0, 1, 0xff];
            int position = bytes.AsSpan().IndexOf(sample);
            Assert.True(position >= 0);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(position + 4), 99);
        }
        else if (corruption == "duplicate-key-id")
        {
            int key = bytes.AsSpan().IndexOf("com.apple.quicktime.still-image-time"u8);
            int keys = bytes.AsSpan(0, key).LastIndexOf("keys"u8) - 4;
            Assert.True(keys >= 0);
            int first = keys + 8;
            int second = first + Size(bytes, first);
            Assert.True(second < keys + Size(bytes, keys));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(second + 4),
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(first + 4)));
        }
        else if (corruption == "unused-description") bytes = AddUnreferencedAppleDescription(bytes);
        else bytes = ChangeAppleMetadataPresentation(bytes, corruption);
        string secondary = fixture.AllocateFilePath("unbound-apple-cover", ".mov");
        File.WriteAllBytes(secondary, bytes);
        using var context = NativeContext.Create(default);
        Observation result = NewObservation();
        Assert.Equal(NativeResult.InvalidArgument, Observe(context.Handle, primary, secondary, final, ref result));
        Assert.Equal(0u, result.Flags);
    }

    [Fact]
    public void P6_Timing_CancelledNativeContext_ReturnsCancelledWithoutPublishingEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        using var context = NativeContext.Create(cancellation.Token);
        cancellation.Cancel();
        Observation result = NewObservation();
        result.Flags = uint.MaxValue;
        result.Applicability = 1;
        Assert.Equal(NativeResult.Cancelled, Observe(context.Handle, "unused-primary.jpg", null, "unused-final.mp4", ref result));
        Assert.Equal(0u, result.Flags);
        Assert.Equal(0, result.Applicability);
        Assert.All(result.FinalHash, value => Assert.Equal(0, value));
    }

    private static Observation NewObservation() => new()
    {
        Size = 144, PrimaryHash = new byte[32], SecondaryHash = new byte[32], FinalHash = new byte[32]
    };

    private static string Cached(string name)
    {
        string path = TestSampleResolver.ResolveSample(name);
        Assert.DoesNotContain(Path.Combine("designs", "各个机型测试"), path, StringComparison.OrdinalIgnoreCase);
        return path;
    }

    private static async Task<string> ExtractVideo(string primary, string? secondary, TestFixtureWorkspace fixture)
    {
        var facts = await new SourceInspector().InspectAsync(primary, secondary);
        var motion = Assert.IsType<LivePhotoBox.Media.Models.VideoFacts>(facts.MotionVideo);
        byte[] source = File.ReadAllBytes(motion.SourceIndex == 1 ? secondary! : primary);
        string final = fixture.AllocateFilePath("same-timeline", ".mp4");
        File.WriteAllBytes(final, source.AsSpan(checked((int)motion.ByteOffset), checked((int)motion.ByteLength)).ToArray());
        return final;
    }

    private static byte[] ReplaceCurrentTime(byte[] bytes, string? value)
    {
        const string name = "GCamera:MotionPhotoPresentationTimestampUs=\"";
        for (int p = 2; p + 4 < bytes.Length;)
        {
            Assert.Equal(0xff, bytes[p++]);
            byte marker = bytes[p++];
            if (marker is 0xda or 0xd9) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(p));
            if (marker == 0xe1)
            {
                string body = Encoding.UTF8.GetString(bytes, p + 2, length - 2);
                int start = body.IndexOf(name, StringComparison.Ordinal);
                if (start >= 0)
                {
                    int end = body.IndexOf('"', start + name.Length);
                    string replacement = value is null ? "" : name + value + "\"";
                    byte[] changed = Encoding.UTF8.GetBytes(body[..start] + replacement + body[(end + 1)..]);
                    byte[] result = new byte[bytes.Length + changed.Length - (length - 2)];
                    bytes.AsSpan(0, p).CopyTo(result);
                    BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(p), checked((ushort)(changed.Length + 2)));
                    changed.CopyTo(result, p + 2);
                    bytes.AsSpan(p + length).CopyTo(result.AsSpan(p + 2 + changed.Length));
                    return result;
                }
            }
            p += length;
        }
        throw new InvalidDataException("The fixture must carry an owned current-cover XMP field.");
    }

    private static byte[] RewriteVivoUuid(byte[] bytes, Func<string, string> rewrite)
    {
        for (int p = 0; p + 24 <= bytes.Length;)
        {
            int size = Size(bytes, p);
            Assert.InRange(size, 8, bytes.Length - p);
            if (bytes.AsSpan(p + 4, 4).SequenceEqual("uuid"u8) &&
                bytes.AsSpan(p + 8, 16).SequenceEqual("vivoMediaExtInfo"u8))
            {
                Assert.True(bytes.AsSpan(p + 24, 4).SequenceEqual("vivo"u8));
                var reader = new Utf8JsonReader(bytes.AsSpan(p + 28, size - 28));
                using var document = JsonDocument.ParseValue(ref reader);
                byte[] payload = Encoding.UTF8.GetBytes("vivo" + rewrite(document.RootElement.GetRawText()));
                byte[] result = new byte[bytes.Length - size + 24 + payload.Length];
                bytes.AsSpan(0, p + 24).CopyTo(result);
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(p), checked((uint)(24 + payload.Length)));
                payload.CopyTo(result, p + 24);
                bytes.AsSpan(p + size).CopyTo(result.AsSpan(p + 24 + payload.Length));
                return result;
            }
            p += size;
        }
        throw new InvalidDataException("Expected the sample's structured vivo UUID.");
    }

    private static int Size(byte[] bytes, int offset)
    {
        uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
        if (size == 1)
        {
            Assert.True(bytes.Length - offset >= 16);
            return checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8)));
        }
        return size == 0 ? bytes.Length - offset : checked((int)size);
    }

    private static byte[] AddUnreferencedAppleDescription(byte[] bytes)
    {
        int moov = FindChild(bytes, 0, bytes.Length, "moov");
        foreach (int track in Children(bytes, moov + 8, moov + Size(bytes, moov)))
        {
            if (!bytes.AsSpan(track + 4, 4).SequenceEqual("trak"u8)) continue;
            int mdia = FindChild(bytes, track + 8, track + Size(bytes, track), "mdia");
            int minf = FindChild(bytes, mdia + 8, mdia + Size(bytes, mdia), "minf");
            int stbl = FindChild(bytes, minf + 8, minf + Size(bytes, minf), "stbl");
            int stsd = FindChild(bytes, stbl + 8, stbl + Size(bytes, stbl), "stsd");
            int key = bytes.AsSpan(stsd, Size(bytes, stsd)).IndexOf("com.apple.quicktime.still-image-time"u8);
            if (key < 0) continue;
            int entry = stsd + 16;
            int entrySize = Size(bytes, entry);
            Assert.Equal(entry + entrySize, stsd + Size(bytes, stsd));
            byte[] result = new byte[bytes.Length + entrySize];
            bytes.AsSpan(0, entry + entrySize).CopyTo(result);
            bytes.AsSpan(entry, entrySize).CopyTo(result.AsSpan(entry + entrySize));
            bytes.AsSpan(entry + entrySize).CopyTo(result.AsSpan(entry + 2 * entrySize));
            // Existing stsc still selects description 1, which now has no
            // still-image-time key. The unreferenced second entry owns it.
            "com.apple.quicktime.still-image-xxxx"u8.CopyTo(result.AsSpan(stsd + key));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(stsd + 12), 2);
            foreach (int ancestor in new[] { moov, track, mdia, minf, stbl, stsd })
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(ancestor), checked((uint)(Size(bytes, ancestor) + entrySize)));
            return result;
        }
        throw new InvalidDataException("Expected a structurally owned Apple cover metadata description.");
    }

    private static byte[] ChangeAppleMetadataPresentation(byte[] bytes, string corruption)
    {
        int moov = FindChild(bytes, 0, bytes.Length, "moov");
        foreach (int track in Children(bytes, moov + 8, moov + Size(bytes, moov)))
        {
            if (!bytes.AsSpan(track + 4, 4).SequenceEqual("trak"u8)) continue;
            int mdia = FindChild(bytes, track + 8, track + Size(bytes, track), "mdia");
            int minf = FindChild(bytes, mdia + 8, mdia + Size(bytes, mdia), "minf");
            int stbl = FindChild(bytes, minf + 8, minf + Size(bytes, minf), "stbl");
            int stsd = FindChild(bytes, stbl + 8, stbl + Size(bytes, stbl), "stsd");
            if (bytes.AsSpan(stsd, Size(bytes, stsd)).IndexOf("com.apple.quicktime.still-image-time"u8) < 0) continue;
            if (corruption == "edit-skips-sample")
            {
                int edts = FindChild(bytes, track + 8, track + Size(bytes, track), "edts");
                int elst = FindChild(bytes, edts + 8, edts + Size(bytes, edts), "elst");
                Assert.Equal(0, bytes[elst + 8]);
                Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(elst + 12)));
                // The sole sample has DTS/PTS zero. Starting its media edit at
                // int.MaxValue excludes it while retaining the empty edit.
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(elst + 32), int.MaxValue);
                return bytes;
            }
            Assert.Equal("nonzero-composition-offset", corruption);
            Assert.Equal(-1, FindChild(bytes, stbl + 8, stbl + Size(bytes, stbl), "ctts", optional: true));
            // This fixture stores mdat before moov, so inserting a table in
            // moov preserves all real sample offsets and payload bytes.
            int mdat = FindChild(bytes, 0, bytes.Length, "mdat");
            Assert.True(mdat + Size(bytes, mdat) <= moov);
            byte[] ctts = [0, 0, 0, 24, (byte)'c', (byte)'t', (byte)'t', (byte)'s',
                0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0x7f, 0xff, 0xff, 0xff];
            int insertion = stbl + Size(bytes, stbl);
            byte[] result = new byte[bytes.Length + ctts.Length];
            bytes.AsSpan(0, insertion).CopyTo(result);
            ctts.CopyTo(result, insertion);
            bytes.AsSpan(insertion).CopyTo(result.AsSpan(insertion + ctts.Length));
            foreach (int ancestor in new[] { moov, track, mdia, minf, stbl })
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(ancestor), checked((uint)(Size(bytes, ancestor) + ctts.Length)));
            return result;
        }
        throw new InvalidDataException("Expected a structurally owned Apple cover metadata description.");
    }
    private static IEnumerable<int> Children(byte[] bytes, int start, int end)
    {
        for (int p = start; p < end; p += Size(bytes, p))
        {
            Assert.InRange(Size(bytes, p), 8, end - p);
            yield return p;
        }
    }
    private static int FindChild(byte[] bytes, int start, int end, string type, bool optional = false)
    {
        int[] matches = Children(bytes, start, end).Where(p => Encoding.ASCII.GetString(bytes, p + 4, 4) == type).ToArray();
        if (optional && matches.Length == 0) return -1;
        return Assert.Single(matches);
    }
}
