using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;

// Offline acceptance probe. Each invocation is a fresh process: no DLL removal
// from a live product process, no runtime policy/fault-injection switches.
var options = new JsonSerializerOptions { WriteIndented = true };
options.Converters.Add(new JsonStringEnumConverter());
string mode = args[0];
string source = Path.GetFullPath(args[1]);
string destination = Path.GetFullPath(args[2]);
if (Directory.Exists(destination)) throw new IOException("Destination must be fresh.");
Directory.CreateDirectory(destination);
string before = Hash(source);
var runtime = NativeRuntime.Probe();
object result;
bool success;
string workspacePath;
using (var workspace = new MediaWorkspace())
{
    workspacePath = workspace.RootDirectory;
    if (mode.StartsWith("image-", StringComparison.Ordinal))
    {
        var r = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg", ImageContainer = ImageContainer.Jpeg,
                ImageCodec = ImageCodec.Jpeg, ByteLength = new FileInfo(source).Length },
            TargetContainer = mode == "image-heic" ? ImageContainer.Heic : ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory,
            TrustedSourceFacts = mode == "image-trusted" ? new ImageConversionSourceFacts
                { Container = ImageContainer.Jpeg, Codec = ImageCodec.Jpeg } : null
        });
        result = r; success = r.Success;
        if (r.OutputArtifact is { } output) File.Copy(output.Path, Path.Combine(destination, "result" + Path.GetExtension(output.Path)));
    }
    else
    {
        VideoCodec codec = mode switch { "remux" => VideoCodec.Copy, "sdr-hevc" => VideoCodec.Hevc, "hdr" => VideoCodec.Hevc, _ => VideoCodec.H264 };
        var r = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo,
                MimeType = Path.GetExtension(source).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime" : "video/mp4",
                VideoContainer = Path.GetExtension(source).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? VideoContainer.Mov : VideoContainer.Mp4,
                VideoCodec = VideoCodec.Unknown, ByteLength = new FileInfo(source).Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = codec,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = mode == "hdr" ? PreservationPolicy.Strict : PreservationPolicy.BestEffort
        });
        result = r; success = r.Success;
        if (r.OutputArtifact is { } output) File.Copy(output.Path, Path.Combine(destination, "result.mp4"));
    }
}
var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
    .Where(m => m.ModuleName.StartsWith("LivePhotoBox.", StringComparison.OrdinalIgnoreCase))
    .Select(m => new { m.ModuleName, m.FileName, Sha256 = Hash(m.FileName) }).ToArray();
var report = new { Mode = mode, Source = source, SourceBefore = before, SourceAfter = Hash(source),
    Runtime = runtime, Result = result, LoadedModules = modules,
    WorkspacePath = workspacePath, WorkspaceRemoved = !Directory.Exists(workspacePath),
    Outputs = Directory.GetFiles(destination, "result.*").Select(p => new { Path = p, Bytes = new FileInfo(p).Length, Sha256 = Hash(p) }) };
using (var stream = new FileStream(Path.Combine(destination, "probe.json"), FileMode.CreateNew)) JsonSerializer.Serialize(stream, report, options);
if (before != Hash(source)) throw new InvalidDataException("Input changed.");
Console.WriteLine(JsonSerializer.Serialize(new { mode, success }));
return 0; // Parent validates expected success/failure and exact execution truth.

static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
