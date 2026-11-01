using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace LivePhotoBox.Core.Tests.Support;

// Independent, test-only inspection. Neither executable is a product dependency.
internal static class AudioOutputAssertions
{
    internal static async Task AssertPcmToAacAsync(string source, string output)
    {
        var before = await InspectAsync(source);
        var after = await InspectAsync(output);
        Assert.StartsWith("pcm_", before.Audio.GetProperty("codec_name").GetString());
        Assert.Equal("aac", after.Audio.GetProperty("codec_name").GetString());
        Assert.Equal(before.Audio.GetProperty("channels").GetInt32(), after.Audio.GetProperty("channels").GetInt32());
        Assert.Equal(before.Audio.GetProperty("sample_rate").GetString(), after.Audio.GetProperty("sample_rate").GetString());
        // Allow AAC frame padding, but reject a changed audio/video offset or tail.
        Assert.InRange(Math.Abs(Offset(before) - Offset(after)), 0, 0.05);
        Assert.InRange(Math.Abs(Tail(before) - Tail(after)), 0, 0.05);
        float[] a = await DecodeAsync(source);
        float[] b = await DecodeAsync(output);
        Assert.NotEmpty(a);
        Assert.NotEmpty(b);
        Assert.InRange(Math.Abs(a.Length - b.Length), 0, 400); // 50 ms at 8 kHz.
        int length = Math.Min(a.Length, b.Length);
        Assert.True(length >= 2400, "Audio fixture must provide three meaningful comparison windows.");
        // Compare on the actual timestamp-aligned timeline, without fitting away a delay.
        // Separate beginning/middle/tail checks also catch local drift and truncation.
        for (int part = 0; part < 3; part++)
        {
            int start = part * length / 3;
            int end = (part + 1) * length / 3;
            double aa = 0, bb = 0, ab = 0, ma = 0, mb = 0;
            for (int i = start; i < end; i++) { ma += a[i]; mb += b[i]; }
            ma /= end - start; mb /= end - start;
            for (int i = start; i < end; i++)
            {
                double x = a[i] - ma, y = b[i] - mb;
                aa += x * x; bb += y * y; ab += x * y;
            }
            Assert.True(aa / (end - start) > 1e-12, "The source fixture must contain non-silent audio in every comparison window.");
            Assert.True(bb / (end - start) > 1e-12, "Output audio unexpectedly became silent.");
            double correlation = ab / Math.Sqrt(aa * bb);
            Assert.True(double.IsFinite(correlation) && correlation >= 0.9,
                $"Decoded audio content/timing differs in window {part}: correlation={correlation:F5}.");
        }
    }

    private static double Time(JsonElement stream, string name) =>
        double.Parse(stream.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    private static double Offset((JsonElement Audio, JsonElement Video) media) =>
        Time(media.Audio, "start_time") - Time(media.Video, "start_time");
    private static double Tail((JsonElement Audio, JsonElement Video) media) =>
        Offset(media) + Time(media.Audio, "duration") - Time(media.Video, "duration");

    private static async Task<(JsonElement Audio, JsonElement Video)> InspectAsync(string path)
    {
        byte[] bytes = await RunAsync("ffprobe", "-v", "error", "-show_entries",
            "stream=codec_type,codec_name,channels,sample_rate,start_time,duration", "-of", "json", path);
        using var json = JsonDocument.Parse(bytes);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        return (Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "audio").Clone(),
            Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "video").Clone());
    }

    private static async Task<float[]> DecodeAsync(string path)
    {
        byte[] bytes = await RunAsync("ffmpeg", "-v", "error", "-xerror", "-copyts", "-i", path,
            "-map", "0:a:0", "-vn", "-af", "aresample=8000:async=1:first_pts=0",
            "-ac", "1", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1");
        Assert.Equal(0, bytes.Length % sizeof(float));
        var samples = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)));
            Assert.True(float.IsFinite(samples[i]), "Decoded audio contains a non-finite sample.");
        }
        return samples;
    }

    private static async Task<byte[]> RunAsync(string tool, params string[] arguments)
    {
        string? executable = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, tool + ".exe")).FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrEmpty(executable), $"{tool}.exe on PATH is required for independent audio regression validation.");
        var info = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var output = new MemoryStream();
        Task stdout = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            try { await Task.WhenAll(stdout, stderr); } catch { /* Preserve the original failure. */ }
            throw;
        }
        Assert.True(process.ExitCode == 0, $"{tool} rejected the media: {await stderr}");
        return output.ToArray();
    }
}
