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
            // Primary local development folder
            string candidate1 = Path.Combine(dir, "designs", "各个机型测试", filename);
            if (File.Exists(candidate1)) return candidate1;

            // Reproducible fixture location (clean checkout / CI)
            string candidate2 = Path.Combine(dir, "tests", "fixtures", "realsamples", filename);
            if (File.Exists(candidate2)) return candidate2;

            // samples folder
            string candidate3 = Path.Combine(dir, "samples", filename);
            if (File.Exists(candidate3)) return candidate3;

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new FileNotFoundException(
            $"Real sample fixture '{filename}' not found. Searched designs/各个机型测试, tests/fixtures/realsamples, and LIVEPHOTOBOX_TEST_SAMPLES_DIR.",
            filename);
    }
}
