using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Media.Inspection;

namespace LivePhotoBox.Core.Tests.Support;

internal sealed class R1RangeBoxHeifFixture
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string FixtureSha256 { get; init; }
    public required string? DonorPath { get; init; }
    public required string? DonorSha256 { get; init; }
    public required string? ExpectedStage { get; init; }
    public required string? ExpectedCategory { get; init; }
    public required JsonElement Semantic { get; init; }
}

internal static class R1RangeBoxHeifFixtureFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static R1RangeBoxHeifFixture Load(string id)
    {
        string fixtureRoot = RequireEnvironmentPath("LIVEPHOTOBOX_R1C_FIXTURES");
        string samplesRoot = RequireEnvironmentPath("LIVEPHOTOBOX_R1C_SAMPLES");
        string manifestPath = Path.Combine(fixtureRoot, "manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement item = manifest.RootElement.GetProperty("cases")
            .EnumerateArray()
            .Single(entry => StringComparer.Ordinal.Equals(entry.GetProperty("id").GetString(), id));

        string fixturePath = ResolveWithin(fixtureRoot, item.GetProperty("fixture").GetString()!);
        string fixtureSha256 = item.GetProperty("fixtureSha256").GetString()!;
        string actualFixtureSha256 = HashFile(fixturePath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(fixtureSha256, actualFixtureSha256))
            throw new InvalidDataException($"Fixture {id} hash mismatch: expected {fixtureSha256}, observed {actualFixtureSha256}.");

        string donorName = item.GetProperty("donor").GetString()!;
        string? donorPath = donorName.StartsWith("<generated:", StringComparison.Ordinal)
            ? null
            : ResolveWithin(samplesRoot, donorName);
        string? donorSha256 = donorPath is null ? null : item.GetProperty("donorSha256").GetString();
        if (donorPath is not null && !StringComparer.OrdinalIgnoreCase.Equals(donorSha256, HashFile(donorPath)))
            throw new InvalidDataException($"Donor sample {donorName} hash mismatch.");

        JsonElement expected = item.GetProperty("expected");
        return new R1RangeBoxHeifFixture
        {
            Id = id,
            Path = fixturePath,
            FixtureSha256 = fixtureSha256,
            DonorPath = donorPath,
            DonorSha256 = donorSha256,
            ExpectedStage = expected.GetProperty("stage").ValueKind == JsonValueKind.Null
                ? null : expected.GetProperty("stage").GetString(),
            ExpectedCategory = expected.GetProperty("category").ValueKind == JsonValueKind.Null
                ? null : expected.GetProperty("category").GetString(),
            Semantic = item.GetProperty("semantic").Clone()
        };
    }

    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static void AssertInputsUnchanged(R1RangeBoxHeifFixture fixture, string fixtureBefore, string? donorBefore)
    {
        string fixtureAfter = HashFile(fixture.Path);
        if (!StringComparer.OrdinalIgnoreCase.Equals(fixtureBefore, fixtureAfter))
            throw new InvalidDataException($"Fixture {fixture.Id} changed during inspection.");
        if (fixture.DonorPath is not null)
        {
            string donorAfter = HashFile(fixture.DonorPath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(donorBefore, donorAfter))
                throw new InvalidDataException($"Donor sample for {fixture.Id} changed during inspection.");
        }
    }

    public static string CaptureDonorHash(R1RangeBoxHeifFixture fixture) =>
        fixture.DonorPath is null ? string.Empty : HashFile(fixture.DonorPath);

    public static void WriteResult(R1RangeBoxHeifFixture fixture, object result)
    {
        string resultRoot = RequireEnvironmentPath("LIVEPHOTOBOX_R1C_RESULTS");
        string casesRoot = Path.Combine(resultRoot, "cases");
        Directory.CreateDirectory(casesRoot);
        string path = Path.Combine(casesRoot, fixture.Id + ".json");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    private static string RequireEnvironmentPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Required test evidence path {name} is not set.");
        return Path.GetFullPath(value);
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Fixture path escapes its bound root: {relativePath}.");
        return fullPath;
    }
}
