using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;

string source = Path.GetFullPath(args[0]), destination = Path.GetFullPath(args[1]);
Directory.CreateDirectory(destination);
if (Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidOperationException("Nonempty diagnostic output");
string before;
using (var input = File.OpenRead(source)) before = Convert.ToHexString(SHA256.HashData(input));
var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest {
    SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo,
        VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.Hevc, ByteLength = new FileInfo(source).Length },
    TargetContainer = VideoContainer.Mp4, TargetCodec = Enum.Parse<VideoCodec>(args[2], true),
    TargetDirectory = destination, PreservationPolicy = PreservationPolicy.Strict
});
string after;
using (var input = File.OpenRead(source)) after = Convert.ToHexString(SHA256.HashData(input));
if (before != after) throw new InvalidOperationException("Diagnostic source changed");
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
Console.WriteLine(JsonSerializer.Serialize(new { sourceSha256 = before, result,
    entries = Directory.GetFiles(destination, "*", SearchOption.AllDirectories) }, json));
