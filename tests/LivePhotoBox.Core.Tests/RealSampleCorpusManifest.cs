using System.Text.Json;

namespace LivePhotoBox.Core.Tests;

public static class RealSampleCorpusManifest
{
    private static readonly Lazy<CorpusManifest> Loaded = new(Load);

    public static IReadOnlyList<R5HeicExpectation> R5HeicExpectations => Loaded.Value.R5HeicExpectations;

    public static CorpusSample GetRequiredSample(string filename) =>
        Loaded.Value.Samples.Single(sample => sample.Required &&
            string.Equals(sample.Filename, filename, StringComparison.Ordinal));

    public static R5HeicExpectation GetR5HeicExpectation(string filename) =>
        R5HeicExpectations.Single(sample =>
            string.Equals(sample.Filename, filename, StringComparison.Ordinal));

    private static CorpusManifest Load()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, "tests", "fixtures", "realsamples-manifest.json");
            if (File.Exists(candidate))
            {
                CorpusManifest? manifest = JsonSerializer.Deserialize<CorpusManifest>(
                    File.ReadAllText(candidate),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return manifest ?? throw new InvalidDataException("RealSample corpus manifest is empty.");
            }

            string? parent = Directory.GetParent(directory)?.FullName;
            if (parent == null || parent == directory) break;
            directory = parent;
        }

        throw new FileNotFoundException(
            "Canonical RealSample corpus manifest was not found.",
            "tests/fixtures/realsamples-manifest.json");
    }
}

public sealed class CorpusManifest
{
    public List<CorpusSample> Samples { get; init; } = [];
    public List<R5HeicExpectation> R5HeicExpectations { get; init; } = [];
}

public sealed class CorpusSample
{
    public string Filename { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long ByteSize { get; init; }
    public bool Required { get; init; }
}

public sealed class R5HeicExpectation
{
    public string Filename { get; init; } = string.Empty;
    public uint[] VisualDimensions { get; init; } = [];
    public uint PrimaryBitDepth { get; init; }
    public R5AuxiliaryExpectation? Auxiliary { get; init; }
}

public sealed class R5AuxiliaryExpectation
{
    public uint ItemId { get; init; }
    public uint[] VisualDimensions { get; init; } = [];
    public uint BitDepth { get; init; }
}
