using System.Security.Cryptography;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "SyntheticFixture")]
public sealed class SyntheticHeicHdrFixtureTests
{
    private const string FixtureName = "谷歌自己合成的.heic";
    private const string FixtureSha256 = "B75BD5CAD6E8036929E50C9617982C857C9D2D072686D4C156FAA9BAC82F47F6";

    [Fact]
    public void MalformedSyntheticHeicFixture_IsVersionedAndExcludedFromDeviceEvidence()
    {
        string fixture = TestSampleResolver.ResolveSyntheticFixture(FixtureName);
        Assert.Equal(FixtureSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))));
    }

    [Fact]
    public async Task SplitHeic_KeepFormat_FailsClosedForMalformedSyntheticXmpOwnership()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
                LivePhotoSplitService.SplitAsync(
                    TestSampleResolver.ResolveSyntheticFixture(FixtureName), outputDir,
                    protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None));
            Assert.Contains("do not share one rdf:Description owner", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task SplitHeic_AppleTargetHeicOutput_RemainsUnsupportedForSyntheticFixture()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                LivePhotoSplitService.SplitAsync(
                    TestSampleResolver.ResolveSyntheticFixture(FixtureName), outputDir,
                    protocolIndex: 1, outputFormatIndex: 2, CancellationToken.None));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task MergeHeic_MotionPhotoV2_FailsClosedForMalformedSyntheticXmpOwnership()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
                LivePhotoSplitService.SplitAsync(
                    TestSampleResolver.ResolveSyntheticFixture(FixtureName), outputDir,
                    protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None));
            Assert.Contains("do not share one rdf:Description owner", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lpb_synthetic_heic_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; test runners may hold file handles briefly.
        }
    }
}
