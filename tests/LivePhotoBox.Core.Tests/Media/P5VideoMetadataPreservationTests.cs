using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P5VideoMetadataPreservationTests
{
    [Theory]
    [Trait("Category", "DerivedRealSamples")]
    [InlineData("苹果双文件.MOV", VideoCodec.Hevc, VideoContainer.Mp4, PreservationPolicy.Strict)]
    [InlineData("苹果双文件.MOV", VideoCodec.Hevc, VideoContainer.Mov, PreservationPolicy.BestEffort)]
    [InlineData("vivo双文件.mp4", VideoCodec.H264, VideoContainer.Mp4, PreservationPolicy.Strict)]
    [InlineData("vivo双文件.mp4", VideoCodec.H264, VideoContainer.Mov, PreservationPolicy.BestEffort)]
    [InlineData("vivo双文件.mp4", VideoCodec.H264, VideoContainer.Mp4, PreservationPolicy.AllowDiscard)]
    [InlineData("苹果双文件.MOV", VideoCodec.Copy, VideoContainer.Mp4, PreservationPolicy.Strict)]
    public async Task ScopedMetadata_IsByteExactThroughActualBackend(string sample, VideoCodec codec,
        VideoContainer container, PreservationPolicy policy)
    {
        using var fixture = new TestFixtureWorkspace();
        using var output = new MediaWorkspace();
        string source = Derive(fixture.RootDirectory, sample);
        byte[] before = SHA256.HashData(File.ReadAllBytes(source));
        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo,
                ByteLength = new FileInfo(source).Length },
            TargetContainer = container, TargetCodec = codec, TargetDirectory = output.RootDirectory,
            PreservationPolicy = policy
        });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        var evidence = Assert.IsType<VideoMetadataPreservationEvidence>(result.ExecutionRecord.MetadataPreservation);
        Assert.Equal(policy, evidence.Policy);
        Assert.Equal(evidence.SourceBoxCount, evidence.VerifiedBoxCount);
        Assert.True(evidence.OpaqueBoxCount >= 5);
        Assert.True(evidence.AudioBoxCount >= 2);
        Assert.Equal(evidence.SourceSha256, evidence.OutputSha256);
        Assert.Equal(64, evidence.SourceSha256.Length);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.Metadata);
        Assert.Equal(codec == VideoCodec.Copy ? VideoBackend.ProjectIsoBmffRemux :
            codec == VideoCodec.Hevc ? VideoBackend.MinimalLibav : VideoBackend.WindowsMediaFoundation,
            result.ExecutionRecord.Backend);
        Assert.False(result.ExecutionRecord.HardwareFallbackOccurred);
        if (codec != VideoCodec.Copy)
            Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
        // This parser is test-owned and follows hierarchy/handler relationships;
        // it never treats a string inside mdat/free as an authoritative box.
        var expected = Assets(File.ReadAllBytes(source));
        var actual = Assets(File.ReadAllBytes(result.OutputArtifact.Path));
        foreach (var (key, bytes) in expected)
        {
            Assert.True(actual.TryGetValue(key, out byte[]? found), key);
            Assert.Equal(bytes, found);
        }
        Assert.Equal(HeaderMetadata(File.ReadAllBytes(source)).OrderBy(p => p.Key),
            HeaderMetadata(File.ReadAllBytes(result.OutputArtifact.Path)).OrderBy(p => p.Key));
        VideoFacts facts = await new VideoConverter().ProbeAsync(result.OutputArtifact.Path);
        Assert.True(facts.HasAudio);
        Assert.True(facts.DurationSeconds > 0);
        Assert.Single(Directory.GetFiles(output.RootDirectory));
        string? evidenceRoot = Environment.GetEnvironmentVariable("LPB_P5_VIDEO_METADATA_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            string route = Path.GetFileNameWithoutExtension(sample) + "-" + codec + "-" + container + "-" + policy;
            string target = Path.Combine(evidenceRoot, route);
            Directory.CreateDirectory(target);
            File.Copy(source, Path.Combine(target, "source" + Path.GetExtension(source)), overwrite: false);
            File.Copy(result.OutputArtifact.Path, Path.Combine(target, "output" + Path.GetExtension(result.OutputArtifact.Path)), overwrite: false);
            File.WriteAllText(Path.Combine(target, "comparison.json"), JsonSerializer.Serialize(new
            {
                evidence, result.ExecutionRecord,
                assets = expected.Select(pair => new { scopeKey = pair.Key,
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(pair.Value)),
                    outputSha256 = Convert.ToHexString(SHA256.HashData(actual[pair.Key])) })
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData("苹果双文件.MOV", VideoCodec.Hevc)]
    [InlineData("vivo双文件.mp4", VideoCodec.H264)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task WideHeaderMetadata_IsPreservedThroughActualBackend(string sample, VideoCodec codec)
    {
        using var fixture = new TestFixtureWorkspace(); using var output = new MediaWorkspace();
        string source = Derive(fixture.RootDirectory, sample, "wide-time");
        var expected = HeaderMetadata(File.ReadAllBytes(source));
        Assert.Contains(expected.Values, value => value.Contains(":4294967297:4294967298:", StringComparison.Ordinal));
        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo, ByteLength = new FileInfo(source).Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = codec, TargetDirectory = output.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        });
        Assert.True(result.Success, result.ErrorMessage); Assert.NotNull(result.OutputArtifact);
        Assert.Equal(expected.OrderBy(p => p.Key), HeaderMetadata(File.ReadAllBytes(result.OutputArtifact.Path)).OrderBy(p => p.Key));
        Assert.Equal(expected.OrderBy(p => p.Key), HeaderMetadata(File.ReadAllBytes(source)).OrderBy(p => p.Key));
        string? root = Environment.GetEnvironmentVariable("LPB_P5_VIDEO_METADATA_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string saved = Path.Combine(root, "wide-" + Path.GetFileNameWithoutExtension(sample)); Directory.CreateDirectory(saved);
            File.Copy(source, Path.Combine(saved, "source" + Path.GetExtension(source)));
            File.Copy(result.OutputArtifact.Path, Path.Combine(saved, "output.mp4"));
            File.WriteAllText(Path.Combine(saved, "comparison.json"), JsonSerializer.Serialize(new { evidence = result.ExecutionRecord.MetadataPreservation }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData("duplicate", PreservationPolicy.Strict)]
    [InlineData("duplicate", PreservationPolicy.BestEffort)]
    [InlineData("short", PreservationPolicy.Strict)]
    [InlineData("reference", PreservationPolicy.AllowDiscard)]
    [InlineData("nested-mdia", PreservationPolicy.Strict)]
    [InlineData("nested-minf", PreservationPolicy.BestEffort)]
    [InlineData("nested-stbl", PreservationPolicy.Strict)]
    [InlineData("nested-edts", PreservationPolicy.Strict)]
    [InlineData("group", PreservationPolicy.Strict)]
    [InlineData("reference-index", PreservationPolicy.Strict)]
    [InlineData("mebx-cdsc", PreservationPolicy.Strict)]
    [InlineData("mebx-offset", PreservationPolicy.Strict)]
    [InlineData("mebx-reference", PreservationPolicy.Strict)]
    [InlineData("mebx-external", PreservationPolicy.Strict)]
    [InlineData("mebx-zero-external", PreservationPolicy.Strict)]
    [InlineData("mebx-zero-offset", PreservationPolicy.Strict)]
    [InlineData("mebx-reserved", PreservationPolicy.Strict)]
    [InlineData("reference-zero", PreservationPolicy.Strict)]
    [InlineData("interlaced", PreservationPolicy.Strict)]
    [InlineData("audio-layout", PreservationPolicy.Strict)]
    [InlineData("transform", PreservationPolicy.Strict)]
    [InlineData("lpex-matrix", PreservationPolicy.Strict)]
    [InlineData("lpex-duplicate", PreservationPolicy.Strict)]
    [InlineData("cuvv-unknown", PreservationPolicy.Strict)]
    [InlineData("movie-time", PreservationPolicy.Strict)]
    [InlineData("handler-name", PreservationPolicy.Strict)]
    [InlineData("handler-minf", PreservationPolicy.Strict)]
    [InlineData("handler-zero", PreservationPolicy.Strict)]
    [InlineData("handler-iso", PreservationPolicy.Strict)]
    [InlineData("handler-long", PreservationPolicy.Strict)]
    [InlineData("mebx-name", PreservationPolicy.Strict)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task AmbiguousOrUnmappedMetadata_FailsWithoutOutput(string corruption, PreservationPolicy policy)
    {
        using var fixture = new TestFixtureWorkspace();
        using var output = new MediaWorkspace();
        string source = Derive(fixture.RootDirectory, corruption is "handler-iso" or "handler-long" ? "vivo双文件.mp4" : "苹果双文件.MOV",
            corruption.StartsWith("nested-", StringComparison.Ordinal) ? corruption[7..] :
                (corruption is "group" or "reference-index" or "reference-zero" or "interlaced" or "audio-layout" or "transform" ||
                    corruption.StartsWith("mebx-", StringComparison.Ordinal) ||
                    corruption.StartsWith("lpex-", StringComparison.Ordinal) || corruption.StartsWith("handler-", StringComparison.Ordinal) ||
                    corruption is "cuvv-unknown" or "movie-time") ? corruption : null);
        byte[] addition = corruption switch
        {
            "duplicate" => Uuid(1),
            "short" => Box("uuid", [1, 2, 3]),
            "reference" => Box("sidx", new byte[24]),
            _ => []
        };
        using (var file = new FileStream(source, FileMode.Append)) file.Write(addition);
        byte[] before = File.ReadAllBytes(source);
        var attempt = await NativeMediaService.TranscodeVideoAttemptAsync(source,
            Path.Combine(output.RootDirectory, "must-not-exist.mp4"), VideoContainer.Mp4,
            corruption.StartsWith("reference-", StringComparison.Ordinal) || corruption.StartsWith("mebx-", StringComparison.Ordinal) ? VideoCodec.Copy : VideoCodec.Hevc,
            23, preservationPolicy: policy);
        Assert.False(attempt.Succeeded);
        Assert.Contains("Unsupported", attempt.LastError, StringComparison.Ordinal);
        Assert.Null(attempt.MetadataPreservation);
        Assert.Null(attempt.OutputEvidence);
        Assert.Empty(Directory.GetFiles(output.RootDirectory));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public unsafe void VersionedAbi_RejectsInvalidRequestBeforeIo(int invalid)
    {
        using var workspace = new TestFixtureWorkspace();
        using var context = NativeContext.Create();
        var request = new NativeVideoPreservationRequestV1 { StructSize = 16, ApiVersion = 1 };
        // Reusing a previously successful receipt must not leak success when
        // request validation fails before any source/output I/O.
        var result = new NativeVideoPreservationResultV1 { StructSize = 96, Status = 1,
            SourceBoxCount = 3, VerifiedBoxCount = 3 };
        var diagnostics = new NativeVideoBackendDiagnostics { StructSize = 208 };
        switch (invalid)
        {
            case 0: request.StructSize = 12; break;
            case 1: request.ApiVersion = 2; break;
            case 2: request.Policy = 3; break;
            case 3: request.Reserved = 1; break;
        }
        string destination = Path.Combine(workspace.RootDirectory, "output.mp4");
        Assert.Equal(NativeResult.InvalidArgument, NativeMethods.TranscodeVideoV3(context.Handle,
            "missing-source", destination, (int)VideoContainer.Mp4, (int)VideoCodec.Copy, 23,
            in request, ref diagnostics, ref result));
        Assert.Equal(0u, result.Status);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
        Assert.Equal(16, Marshal.SizeOf<NativeVideoPreservationRequestV1>());
        Assert.Equal(96, Marshal.SizeOf<NativeVideoPreservationResultV1>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task Probe_SeparatesCodedDimensionsFromTrackDisplaySize(bool versionOne)
    {
        using var fixture = new TestFixtureWorkspace();
        string source = Derive(fixture.RootDirectory, "苹果双文件.MOV", versionOne ? "display-v1" : "display-v0");
        byte[] before = File.ReadAllBytes(source);
        VideoFacts facts = await NativeMediaService.ProbeVideoAsync(source);
        Assert.Equal(1920u, facts.Width);
        Assert.Equal(1440u, facts.Height);
        Assert.Equal(0, facts.RotationDegrees);
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(VideoCodec.Copy)]
    [InlineData(VideoCodec.Hevc)]
    [Trait("Category", "RealSamples")]
    public async Task HuaweiImplicitLocalMebxZero_PreservesTrackThroughActualConversion(VideoCodec codec)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".ai", "state.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string source = Path.Combine(directory.FullName, ".ai-tmp", "workspace", "P5", "R5", "huawei-extraction-pass-c", "motion.mp4");
        byte[] original = File.ReadAllBytes(source);
        Assert.Equal("664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6", Convert.ToHexString(SHA256.HashData(original)));
        using var output = new MediaWorkspace();
        var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo, ByteLength = original.Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = codec, TargetDirectory = output.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        byte[] actual = File.ReadAllBytes(result.OutputArtifact.Path);
        Atom Mebx(byte[] bytes)
        {
            Atom parent = Assert.Single(Children(bytes, 0, bytes.Length), a => a.Type == "moov");
            parent = Assert.Single(Children(bytes, parent.Start + parent.Header, parent.End), a => a.Type == "trak" && Handler(bytes, a) == "meta");
            foreach (string type in new[] { "mdia", "minf", "stbl", "stsd", "mebx" })
                parent = Assert.Single(Children(bytes, parent.Start + parent.Header + (parent.Type == "stsd" ? 8 : 0), parent.End), a => a.Type == type);
            return parent;
        }
        Atom beforeEntry = Mebx(original), afterEntry = Mebx(actual);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(beforeEntry.Start + beforeEntry.Header + 6)));
        Assert.Equal(Slice(original, beforeEntry), Slice(actual, afterEntry));
        Assert.Equal(original, File.ReadAllBytes(source));
        string? root = Environment.GetEnvironmentVariable("LPB_P5_VIDEO_METADATA_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string saved = Path.GetFullPath(Path.Combine(root, "..", "huawei-local-zero", codec.ToString()));
            Directory.CreateDirectory(saved);
            File.Copy(source, Path.Combine(saved, "source.mp4"));
            File.Copy(result.OutputArtifact.Path, Path.Combine(saved, "output.mp4"));
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task UnmappedAuxiliaryVideoTrack_CannotReturnFalsePreservationSuccess()
    {
        using var output = new MediaWorkspace();
        string source = TestSampleResolver.ResolveSample("苹果-双文件.MOV");
        byte[] before = SHA256.HashData(File.ReadAllBytes(source));
        var attempt = await NativeMediaService.TranscodeVideoAttemptAsync(source,
            Path.Combine(output.RootDirectory, "must-not-exist.mp4"), VideoContainer.Mp4,
            VideoCodec.Hevc, 23, preservationPolicy: PreservationPolicy.Strict);
        Assert.False(attempt.Succeeded);
        Assert.Contains("Unsupported", attempt.LastError, StringComparison.Ordinal);
        Assert.Null(attempt.MetadataPreservation);
        Assert.Empty(Directory.GetFiles(output.RootDirectory));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task LegacyAbi_CannotBypassScopedMetadataGate(bool duplicate)
    {
        using var fixture = new TestFixtureWorkspace();
        using var output = new TestFixtureWorkspace();
        string source = Derive(fixture.RootDirectory, "苹果双文件.MOV");
        if (duplicate)
        {
            using var file = new FileStream(source, FileMode.Append); file.Write(Uuid(1));
        }
        byte[] before = File.ReadAllBytes(source);
        string destination = Path.Combine(output.RootDirectory, "legacy.mp4");
        NativeResult Run()
        {
            using var context = NativeContext.Create();
            var diagnostics = new NativeVideoBackendDiagnostics { StructSize = 208 };
            return NativeMethods.TranscodeVideoV2(context.Handle, source, destination,
                (int)VideoContainer.Mp4, (int)VideoCodec.Copy, 23, ref diagnostics);
        }
        NativeResult result = await Task.Run(Run);
        Assert.Equal(before, File.ReadAllBytes(source));
        if (duplicate)
        {
            Assert.NotEqual(NativeResult.Ok, result);
            Assert.Empty(Directory.GetFiles(output.RootDirectory));
        }
        else
        {
            Assert.Equal(NativeResult.Ok, result);
            var expected = Assets(before); var actual = Assets(File.ReadAllBytes(destination));
            foreach (var (key, bytes) in expected) Assert.Equal(bytes, actual[key]);
            Assert.Single(Directory.GetFiles(output.RootDirectory));
        }
    }

    private readonly record struct Atom(int Start, int End, int Header, string Type);
    private static List<Atom> Children(byte[] bytes, int start, int end, bool padding = false)
    {
        var found = new List<Atom>();
        while (start < end)
        {
            if (padding && end - start == 4 && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start)) == 0) break;
            Assert.True(end - start >= 8);
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start));
            int header = 8;
            if (length == 1) { length = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(start + 8)); header = 16; }
            if (length == 0) length = (ulong)(end - start);
            Assert.InRange(length, (ulong)header, (ulong)(end - start));
            int next = checked(start + (int)length);
            found.Add(new(start, next, header, Encoding.Latin1.GetString(bytes, start + 4, 4)));
            start = next;
        }
        return found;
    }
    private static byte[] Slice(byte[] b, Atom a) => b[a.Start..a.End];
    private static byte[] Box(string type, byte[] payload)
    {
        byte[] b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(b, 4); payload.CopyTo(b, 8); return b;
    }
    private static byte[] Uuid(byte id)
    {
        byte[] payload = Enumerable.Repeat(id, 16).Concat(Encoding.UTF8.GetBytes("P5 opaque asset " + id)).ToArray();
        if (id != 2) return Box("uuid", payload);
        byte[] extended = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(extended, 1);
        Encoding.ASCII.GetBytes("uuid").CopyTo(extended, 4);
        BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), (ulong)extended.Length);
        payload.CopyTo(extended, 16); return extended;
    }
    private static byte[] Join(IEnumerable<byte[]> parts) => parts.SelectMany(p => p).ToArray();
    private static string Handler(byte[] data, Atom track)
    {
        Atom media = Assert.Single(Children(data, track.Start + track.Header, track.End), a => a.Type == "mdia");
        Atom handler = Assert.Single(Children(data, media.Start + media.Header, media.End), a => a.Type == "hdlr");
        return Encoding.Latin1.GetString(data, handler.Start + handler.Header + 8, 4);
    }
    private static byte[] AddNestedAsset(byte[] container, string[] path, int depth = 0)
    {
        var children = Children(container, 8, container.Length).Select(a => Slice(container, a)).ToList();
        if (depth == path.Length) children.Add(Uuid(7));
        else
        {
            int index = children.FindIndex(b => Encoding.Latin1.GetString(b, 4, 4) == path[depth]);
            Assert.True(index >= 0);
            children[index] = AddNestedAsset(children[index], path, depth + 1);
        }
        return Box(Encoding.Latin1.GetString(container, 4, 4), Join(children));
    }
    private static byte[] ModifyAtPath(byte[] container, string[] path, Func<byte[], byte[]> modify, int depth = 0)
    {
        if (depth == path.Length) return modify(container);
        int prefix = Encoding.Latin1.GetString(container, 4, 4) == "stsd" ? 16 : 8;
        var children = Children(container, prefix, container.Length).Select(a => Slice(container, a)).ToList();
        int index = children.FindIndex(b => Encoding.Latin1.GetString(b, 4, 4) == path[depth]);
        Assert.True(index >= 0);
        children[index] = ModifyAtPath(children[index], path, modify, depth + 1);
        return Box(Encoding.Latin1.GetString(container, 4, 4), container[8..prefix].Concat(Join(children)).ToArray());
    }
    private static string Derive(string directory, string sample, string? nested = null)
    {
        byte[] data = File.ReadAllBytes(TestSampleResolver.ResolveSample(sample));
        Atom moov = Assert.Single(Children(data, 0, data.Length), a => a.Type == "moov");
        var movie = new List<byte[]>();
        bool metadataMutated = false;
        foreach (Atom child in Children(data, moov.Start + moov.Header, moov.End))
        {
            if (child.Type != "trak")
            {
                byte[] bytes = nested == "wide-time" && child.Type == "mvhd" ? WideTimedHeader(Slice(data, child)) : Slice(data, child);
                if (nested == "movie-time" && child.Type == "mvhd") BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + 20 + 52), 1);
                movie.Add(bytes); continue;
            }
            string handler = Handler(data, child);
            if (handler == "meta" && !metadataMutated && nested?.StartsWith("mebx-", StringComparison.Ordinal) == true)
            {
                byte[] damaged = Slice(data, child);
                if (nested.StartsWith("mebx-zero", StringComparison.Ordinal)) damaged = ModifyAtPath(damaged, ["mdia", "minf", "stbl", "stsd", "mebx"], box =>
                {
                    box[14] = 0; box[15] = 0; return box;
                });
                if (nested == "mebx-cdsc") damaged = ModifyAtPath(damaged, ["tref", "cdsc"], box =>
                {
                    BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(8), 0x7FFF); return box;
                });
                else if (nested is "mebx-offset" or "mebx-zero-offset") damaged = ModifyAtPath(damaged, ["mdia", "minf", "stbl", "stco"], box =>
                {
                    BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(16), 0xFFFFFF00); return box;
                });
                else if (nested == "mebx-reference") damaged = ModifyAtPath(damaged, ["mdia", "minf", "stbl", "stsd", "mebx"], box =>
                {
                    box[14] = 0; box[15] = 2; return box;
                });
                else if (nested == "mebx-reserved") damaged = ModifyAtPath(damaged, ["mdia", "minf", "stbl", "stsd", "mebx"], box =>
                {
                    box[8] = 1; return box;
                });
                else if (nested == "mebx-name") damaged = ModifyAtPath(damaged, ["mdia", "hdlr"], box =>
                {
                    box[32]++; return box;
                });
                else damaged = ModifyAtPath(damaged, ["mdia", "minf", "dinf", "dref"], box =>
                {
                    byte[] external = Box("url ", new byte[4].Concat(Encoding.UTF8.GetBytes("file:///missing-external-media\0")).ToArray());
                    return Box("dref", box[8..16].Concat(external).ToArray());
                });
                movie.Add(damaged); metadataMutated = true; continue;
            }
            if (handler is not ("vide" or "soun")) { movie.Add(Slice(data, child)); continue; }
            var track = Children(data, child.Start + child.Header, child.End).Select(a => Slice(data, a)).ToList();
            track.Add(Uuid(handler == "vide" ? (byte)3 : (byte)4));
            if (handler == "soun")
            {
                int existing = track.FindIndex(b => Encoding.Latin1.GetString(b, 4, 4) == "udta");
                byte[] tag = Box("©p5t", Encoding.UTF8.GetBytes("audio track inline tag"));
                if (existing >= 0) track[existing] = Box("udta", track[existing][8..].Concat(tag).ToArray());
                else track.Add(Box("udta", tag));
            }
            byte[] rebuilt = Box("trak", Join(track));
            if (nested == "wide-time")
            {
                rebuilt = ModifyAtPath(rebuilt, ["tkhd"], WideTimedHeader);
                rebuilt = ModifyAtPath(rebuilt, ["mdia", "mdhd"], WideTimedHeader);
            }
            if (handler == "soun" && nested == "audio-layout")
                rebuilt = ModifyAtPath(rebuilt, ["mdia", "minf", "stbl", "stsd", "lpcm"], entry =>
                {
                    byte[] layout = new byte[16];
                    BinaryPrimitives.WriteUInt32BigEndian(layout.AsSpan(8), 4); // Unmapped channel bitmap.
                    return Box("lpcm", entry[8..].Concat(Box("chan", layout)).ToArray());
                });
            if (handler == "vide")
            {
                if (nested?.StartsWith("handler-", StringComparison.Ordinal) == true)
                    rebuilt = ModifyAtPath(rebuilt, nested == "handler-minf" ? ["mdia", "minf", "hdlr"] : ["mdia", "hdlr"], box =>
                    {
                        if (nested == "handler-long") return Box("hdlr", Join([box[8..32], Enumerable.Repeat((byte)'x', 256).ToArray(), new byte[1]]));
                        if (nested == "handler-iso") box[^1] = (byte)'!';
                        else if (nested == "handler-zero") box[33] = 0;
                        else box[32]++;
                        return box;
                    });
                if (nested is "display-v0" or "display-v1" or "transform")
                    rebuilt = ModifyAtPath(rebuilt, ["tkhd"], header =>
                    {
                        if (nested == "display-v1")
                        {
                            Assert.Equal(0, header[8]);
                            byte[] old = header[8..]; byte[] expanded = new byte[old.Length + 12];
                            old[..4].CopyTo(expanded, 0); expanded[0] = 1;
                            BinaryPrimitives.WriteUInt64BigEndian(expanded.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(old.AsSpan(4)));
                            BinaryPrimitives.WriteUInt64BigEndian(expanded.AsSpan(12), BinaryPrimitives.ReadUInt32BigEndian(old.AsSpan(8)));
                            old[12..20].CopyTo(expanded, 20);
                            BinaryPrimitives.WriteUInt64BigEndian(expanded.AsSpan(28), BinaryPrimitives.ReadUInt32BigEndian(old.AsSpan(20)));
                            old[24..].CopyTo(expanded, 36); header = Box("tkhd", expanded);
                        }
                        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(header.Length - 8), 2560u << 16);
                        if (nested == "transform") BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(header.Length - 40), 0x00004000);
                        return header;
                    });
                // Geometry is independent of codec payload; use a non-square
                // pixel aspect to catch silent default/relabel behavior.
                string codec = sample.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "avc1" : "hvc1";
                rebuilt = ModifyAtPath(rebuilt, ["mdia", "minf", "stbl", "stsd", codec], entry =>
                {
                    byte[] aspect = new byte[8];
                    BinaryPrimitives.WriteUInt32BigEndian(aspect, 4);
                    BinaryPrimitives.WriteUInt32BigEndian(aspect.AsSpan(4), 3);
                    var extensions = Children(entry, 86, entry.Length, padding: true)
                        .Where(a => a.Type is not ("pasp" or "fiel")).Select(a => Slice(entry, a));
                    byte[] unknownVivid = nested == "cuvv-unknown" ? Box("cuvv", new byte[22]) : [];
                    return Box(codec, Join([entry[8..86], Join(extensions), Box("pasp", aspect),
                        Box("fiel", nested == "interlaced" ? new byte[] { 2, 1 } : new byte[] { 1, 0 }), unknownVivid]));
                });
                if (nested == "group") rebuilt = ModifyAtPath(rebuilt, ["mdia", "minf", "stbl", "sgpd"], box =>
                {
                    Encoding.ASCII.GetBytes("priv").CopyTo(box, 12); return box;
                });
                else if (nested is "reference-index" or "reference-zero") rebuilt = ModifyAtPath(rebuilt, ["mdia", "minf", "stbl", "stsd", codec], box =>
                {
                    box[14] = 0; box[15] = nested == "reference-zero" ? (byte)0 : (byte)2; return box;
                });
                else if (nested is "mdia" or "minf" or "edts" or "stbl") rebuilt = AddNestedAsset(rebuilt, nested switch
                {
                    "mdia" => ["mdia"], "minf" => ["mdia", "minf"], "edts" => ["edts"],
                    _ => ["mdia", "minf", "stbl"]
                });
            }
            movie.Add(rebuilt);
        }
        movie.Add(Uuid(5));
        byte[] lpex = Box("lpex", Encoding.ASCII.GetBytes("LivePhotoExtension{\"coverFramePts\":1332432,\"desc\":\"OppoMotionVideoExt\",\"matrixCount\":" +
            (nested == "lpex-matrix" ? "1" : "0") + ",\"version\":0,\"videoOrientation\":0}"));
        movie.Add(lpex);
        if (nested == "lpex-duplicate") movie.Add(lpex);
        // Keep every original mdat/chunk address unchanged: replace old moov
        // with equal-sized padding and append the rebuilt movie at EOF.
        Encoding.ASCII.GetBytes("free").CopyTo(data, moov.Start + 4);
        byte[] derived = Join([data, Box("moov", Join(movie)), Uuid(1), Uuid(2)]);
        string path = Path.Combine(directory, "derived" + Path.GetExtension(sample));
        File.WriteAllBytes(path, derived); return path;
    }
    private static byte[] WideTimedHeader(byte[] box)
    {
        Assert.Equal(0, box[8]); string kind = Encoding.ASCII.GetString(box, 4, 4);
        bool track = kind == "tkhd"; int prefix = track ? 24 : 20;
        byte[] body = new byte[box.Length - 8 + 12]; box[8..12].CopyTo(body, 0); body[0] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4), 0x100000001);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(12), 0x100000002);
        box.AsSpan(20, track ? 8 : 4).CopyTo(body.AsSpan(20));
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(track ? 28 : 24), BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(8 + prefix - 4)));
        box.AsSpan(8 + prefix).CopyTo(body.AsSpan(prefix + 12)); return Box(kind, body);
    }

    private static Dictionary<string, byte[]> Assets(byte[] data)
    {
        var assets = new Dictionary<string, byte[]>();
        void Read(int start, int end, string scope)
        {
            foreach (Atom atom in Children(data, start, end))
            {
                if (atom.Type == "uuid") assets.Add(scope + "/uuid/" + Convert.ToHexString(
                    data.AsSpan(atom.Start + atom.Header, 16)), Slice(data, atom));
                if (scope == "audio" && atom.Type == "udta") assets.Add("audio/udta", Slice(data, atom));
                if (scope == "movie" && atom.Type == "lpex") assets.Add("movie/lpex", Slice(data, atom));
                if (atom.Type == "moov") Read(atom.Start + atom.Header, atom.End, "movie");
                if (atom.Type == "trak") Read(atom.Start + atom.Header, atom.End, Handler(data, atom) switch
                    { "vide" => "video", "soun" => "audio", _ => "metadata" + atom.Start });
                if (scope == "video" && atom.Type == "mdia")
                {
                    Atom Find(Atom parent, string type, int skip = 0) => Assert.Single(
                        Children(data, parent.Start + parent.Header + skip, parent.End), a => a.Type == type);
                    Atom stsd = Find(Find(Find(atom, "minf"), "stbl"), "stsd");
                    Atom entry = Assert.Single(Children(data, stsd.Start + stsd.Header + 8, stsd.End));
                    foreach (Atom geometry in Children(data, entry.Start + entry.Header + 78, entry.End, padding: true)
                        .Where(a => a.Type is "clap" or "pasp" or "fiel"))
                        assets.Add("video/geometry/" + geometry.Type, Slice(data, geometry));
                }
            }
        }
        Read(0, data.Length, "top"); return assets;
    }

    // Independent field projection: creation/modification, all non-timeline
    // header attributes, language and complete handler bytes. IDs/timebase/
    // durations are structural outputs; they are checked by timing validators.
    private static Dictionary<string, string> HeaderMetadata(byte[] data)
    {
        var result = new Dictionary<string, string>();
        Atom fileType = Assert.Single(Children(data, 0, data.Length), a => a.Type == "ftyp");
        bool quicktime = Encoding.ASCII.GetString(data, fileType.Start + fileType.Header, 4) == "qt  ";
        Atom Find(Atom parent, string type) => Assert.Single(Children(data, parent.Start + parent.Header, parent.End), a => a.Type == type);
        string HandlerValue(Atom atom)
        {
            byte[] bytes = Slice(data, atom); int start = atom.Header + 24;
            if (bytes.Length > start)
            {
                if (quicktime)
                {
                    int length = bytes[start]; Assert.Equal(bytes.Length - start - 1, length);
                    bytes.AsSpan(start + 1, length).CopyTo(bytes.AsSpan(start)); bytes[^1] = 0;
                }
                else Assert.Equal(0, bytes[^1]);
            }
            return Convert.ToHexString(bytes);
        }
        void Timed(string key, Atom atom)
        {
            int b = atom.Start + atom.Header; bool wide = data[b] == 1;
            Assert.InRange(data[b], (byte)0, (byte)1);
            ulong created = wide ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(b + 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(b + 4));
            ulong modified = wide ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(b + 12)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(b + 8));
            int prefix = atom.Type == "tkhd" ? (wide ? 36 : 24) : (wide ? 32 : 20);
            int tailBytes = atom.End - b - prefix - (atom.Type == "mvhd" ? 4 : 0);
            string reserved = atom.Type == "tkhd" ? Convert.ToHexString(data.AsSpan(b + (wide ? 24 : 16), 4)) : "";
            result.Add(key, $"{Convert.ToHexString(data.AsSpan(b + 1, 3))}:{created}:{modified}:{reserved}:{Convert.ToHexString(data.AsSpan(b + prefix, tailBytes))}");
        }
        Atom movie = Assert.Single(Children(data, 0, data.Length), a => a.Type == "moov");
        Timed("movie/mvhd", Find(movie, "mvhd"));
        foreach (Atom track in Children(data, movie.Start + movie.Header, movie.End).Where(a => a.Type == "trak"))
        {
            string handler = Handler(data, track); if (handler is not ("vide" or "soun")) continue;
            Timed(handler + "/tkhd", Find(track, "tkhd"));
            Atom media = Find(track, "mdia"), minf = Find(media, "minf");
            Timed(handler + "/mdhd", Find(media, "mdhd"));
            result.Add(handler + "/mdia/hdlr", HandlerValue(Find(media, "hdlr")));
            foreach (Atom atom in Children(data, minf.Start + minf.Header, minf.End).Where(a => a.Type == "hdlr"))
                result.Add(handler + "/minf/hdlr", HandlerValue(atom));
        }
        return result;
    }
}
