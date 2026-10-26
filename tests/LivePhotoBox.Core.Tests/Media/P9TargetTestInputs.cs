using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Core.Tests.Media;

internal static class P9TargetTestInputs
{
    internal static string Root
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R2_INPUTS");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            for (DirectoryInfo? path = new(AppContext.BaseDirectory); path is not null; path = path.Parent)
                if (File.Exists(Path.Combine(path.FullName, "CMakeLists.txt")))
                    return Path.Combine(path.FullName, ".ai-tmp", "workspace", "P9", "target-writers", "R2", "inputs");
            throw new DirectoryNotFoundException("Set LIVEPHOTOBOX_P9_R2_INPUTS to the qualified P9 R2 inputs.");
        }
    }
    internal static byte[] Image => File.ReadAllBytes(Path.Combine(Root, "neutral.jpg"));
    internal static byte[] Video => File.ReadAllBytes(Path.Combine(Root, "neutral.mp4"));
    // Independently constructed from protocol facts, not the production serializer.
    internal static byte[] V2(long? cover = 0) => File.ReadAllBytes(Path.Combine(Root,
        cover is null ? "v2-unknown.jpg" : cover == 599999 ? "v2-known-near-end.jpg" : "v2-known-zero.jpg"));
    internal static byte[] AddXmp(byte[] image, string xml)
    {
        byte[] payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xml);
        if (payload.Length > 65533 || image.Length < 4 || image[0] != 255 || image[1] != 216) throw new InvalidDataException();
        int length = payload.Length + 2;
        return new byte[] { 255, 216, 255, 225, (byte)(length >> 8), (byte)length }
            .Concat(payload).Concat(image.AsSpan(2).ToArray()).ToArray();
    }
    internal static NeutralMediaBundle Bundle(string directory, long? cover = 0, byte[]? image = null, bool mov = false, bool audio = false)
    {
        string imagePath = Path.Combine(directory, "neutral.jpg"), videoPath = Path.Combine(directory, "neutral.mp4");
        File.WriteAllBytes(imagePath, image ?? Image); File.WriteAllBytes(videoPath,
            mov || audio ? File.ReadAllBytes(Path.Combine(Root, mov ? "neutral.mov" : "neutral-audio.mp4")) : Video);
        MediaArtifact primary = Artifact(imagePath, MediaArtifactKind.PrimaryImage), motion = Artifact(videoPath, MediaArtifactKind.MotionVideo);
        if (mov) motion = motion with { VideoContainer = VideoContainer.Mov };
        return new NeutralMediaBundle
        {
            PrimaryImage = primary, MotionVideo = motion,
            SourceProvenance = new SourceMediaFacts { Protocol = SourceProtocol.NonLive, PrimaryImage = new ImageFacts() },
            RemovedProtocolFacts = [],
            Manifest = new[] { primary, motion }.Select(a => new NeutralArtifactManifest
                { Role = a.Kind.ToString(), Path = a.Path, Sha256 = a.Sha256!, ByteLength = a.ByteLength }).ToArray(),
            Semantics = new NeutralMediaSemantics
            {
                PrimaryImage = new NeutralArtifactSemantics { Role = NeutralArtifactRole.PrimaryImage },
                MotionVideo = new NeutralArtifactSemantics { Role = NeutralArtifactRole.MotionVideo },
                Timing = new NeutralTimingSemantics { CoverTimestampUs = cover, EvidenceState = NeutralEvidenceState.Verified }
            }
        };
    }
    private static MediaArtifact Artifact(string path, MediaArtifactKind kind) => new()
    {
        Path = path, Kind = kind, ByteLength = new FileInfo(path).Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), FileIdentity = WindowsFileIdentity.Capture(path),
        ImageContainer = kind == MediaArtifactKind.PrimaryImage ? ImageContainer.Jpeg : ImageContainer.Unknown,
        VideoContainer = kind == MediaArtifactKind.MotionVideo ? VideoContainer.Mp4 : VideoContainer.Unknown,
        VideoCodec = kind == MediaArtifactKind.MotionVideo ? VideoCodec.H264 : VideoCodec.Unknown
    };
    internal sealed class Folder : IDisposable
    {
        internal string Path { get; }
        internal Folder(string name)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LivePhotoBox", "P9-R2", name);
            if (Directory.Exists(Path)) throw new IOException("Inspect previous test residue before reusing " + Path);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0 || Directory.GetDirectories(Path).Length != 0) throw new IOException("Unexpected fixture directory shape.");
            foreach (string file in Directory.GetFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
