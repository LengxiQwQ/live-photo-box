using System;
using System.IO;

namespace LivePhotoBox.Core.Tests;

public static class TestSampleResolver
{
    public static string ResolveSample(string filename)
    {
        // 1. Environment variable override
        string? envDir = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
        {
            string candidate = Path.Combine(envDir, filename);
            if (File.Exists(candidate)) return candidate;
        }

        // 2. Directory hierarchy walk
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            // Reproducible fixture location (clean checkout / CI)
            string candidate = Path.Combine(dir, "tests", "fixtures", "realsamples", filename);
            if (File.Exists(candidate)) return candidate;

            // samples folder
            string candidate3 = Path.Combine(dir, "samples", filename);
            if (File.Exists(candidate3)) return candidate3;

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            $"Real sample fixture '{filename}' not found. Searched tests/fixtures/realsamples and LIVEPHOTOBOX_TEST_SAMPLES_DIR.",
            filename);
    }

    public static string ResolveSyntheticFixture(string filename)
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string fixtureCandidate = Path.Combine(dir, "tests", "fixtures", "synthetic", filename);
            if (File.Exists(fixtureCandidate)) return fixtureCandidate;

            string outputCandidate = Path.Combine(dir, "synthetic", filename);
            if (File.Exists(outputCandidate)) return outputCandidate;

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            $"Synthetic fixture '{filename}' not found. Searched tests/fixtures/synthetic and test output.",
            filename);
    }
}
