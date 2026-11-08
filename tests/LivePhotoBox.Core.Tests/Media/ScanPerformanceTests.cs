using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Interop;
using Xunit;
using Xunit.Abstractions;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ScanPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    [Trait("Category", "RealSamples")]
    public async Task RepeatedVivoIdentity_IsIndexedWithoutTryingEveryCombination(int copies)
    {
        using var ws = new TestFixtureWorkspace();
        for (int i = 0; i < copies; i++)
        {
            File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"), Path.Combine(ws.RootDirectory, $"image{i}.jpg"));
            File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"), Path.Combine(ws.RootDirectory, $"video{i}.mp4"));
        }
        using var session = new DiscoveryInspectionSession();
        var watch = Stopwatch.StartNew();
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
        {
            InputDirectory = ws.RootDirectory, Mode = PairingMode.Vivo
        }, CancellationToken.None, null, session);
        output.WriteLine($"files={2 * copies}; single={session.SingleInspections}; candidates={session.CandidateInspections}; hints={session.HintInspections}; pairs={session.PairInspections}; candidateBytes={session.CandidateBytesRead}; elapsedMs={watch.ElapsedMilliseconds}");
        Assert.Equal(ScanOperationStatus.Completed, result.Status);
        Assert.Empty(result.Pairs);
        Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "ConflictDuplicateIdentity");
        Assert.Equal(0, session.SingleInspections);
        Assert.Equal(2 * copies, session.CandidateInspections);
        Assert.Equal(0, session.HintInspections);
        Assert.Equal(0, session.PairInspections);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task SplitPasses_ShareEachSingleInspection_AndRetainRealPairsAndEmbeddedLive()
    {
        using var ws = new TestFixtureWorkspace();
        foreach (var (sample, name) in new[]
        {
            ("苹果-双文件.JPG", "apple.jpg"), ("苹果-双文件.MOV", "renamed.mov"),
            ("vivo双文件.jpg", "vivo.jpg"), ("vivo双文件.mp4", "other.mp4"), ("oppo.jpg", "embedded.jpg")
        }) File.Copy(TestSampleResolver.ResolveSample(sample), Path.Combine(ws.RootDirectory, name));
        using var session = new DiscoveryInspectionSession();
        var result = await LivePhotoDiscoveryService.ScanFilesAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory },
            DiscoveryScanMode.SplitOnly, session: session);
        Assert.Empty(result.DualFilePairs);
        Assert.Equal(3,result.Items.Count);
        Assert.Equal(LivePhotoType.SingleFileJpeg, result.Items.Single(i => i.FilePath.EndsWith("embedded.jpg")).LivePhotoType);
        Assert.Equal(0, session.SingleInspections);
        Assert.Equal(0, session.PairInspections);
        using var batch = new RepairBatchContext { DiscoverySession = session };
        var apple = await LivePhotoRepairService.AnalyzeFileInBatchAsync(Path.Combine(ws.RootDirectory, "apple.jpg"),
            CancellationToken.None, true, batch);
        Assert.NotEqual(RepairIssueType.Error, apple.IssueType);
        Assert.False(string.IsNullOrEmpty(apple.ContentIdentifier));
        Assert.Equal(1, session.SingleInspections); // only the requested image is deeply diagnosed
        output.WriteLine($"files=5; single={session.SingleInspections}; hints={session.HintInspections}; pairs={session.PairInspections}");
    }

    [Theory]
    [InlineData("苹果-双文件.JPG")]
    [InlineData("苹果-双文件.MOV")]
    [InlineData("vivo双文件.jpg")]
    [InlineData("vivo双文件.mp4")]
    [Trait("Category", "RealSamples")]
    public async Task FastCandidate_AgreesWithEstablishedPairingParser_WithoutClaimingAHash(string sample)
    {
        using var ws = new TestFixtureWorkspace();
        string path = Path.Combine(ws.RootDirectory, "renamed" + Path.GetExtension(sample));
        File.Copy(TestSampleResolver.ResolveSample(sample), path);
        var fast = await NativeMediaCandidate.ReadAsync(path, default, sample.StartsWith("vivo",StringComparison.Ordinal) ? DiscoveryCapability.VivoId : DiscoveryCapability.AppleId);
        var established = await NativePairingHint.ReadAsync(path, default);
        Assert.NotNull(fast?.Pairing);
        Assert.NotNull(established);
        Assert.Equal(established.Identifier, fast.Pairing.Identifier);
        Assert.Equal(established.Protocol, fast.Pairing.Protocol);
        Assert.Equal(established.Role, fast.Pairing.Role);
        Assert.InRange(fast.BytesRead, 1UL, 4UL * 1024 * 1024);
        output.WriteLine($"sample={sample}; bytesRead={fast.BytesRead}; fileBytes={fast.FileSize}");
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task DistinctVivoPairs_RequireOnlyOneCombinationCheckPerIdentity()
    {
        using var ws = new TestFixtureWorkspace();
        string imageSource = TestSampleResolver.ResolveSample("vivo双文件.jpg");
        string videoSource = TestSampleResolver.ResolveSample("vivo双文件.mp4");
        var id = (await NativePairingHint.ReadAsync(imageSource, default))!.Identifier;
        byte[] old = System.Text.Encoding.UTF8.GetBytes(id);
        for (int n = 1; n <= 3; n++)
        {
            byte[] replacement = System.Text.Encoding.UTF8.GetBytes(n.ToString().PadLeft(id.Length, '0'));
            Assert.Equal(old.Length, replacement.Length);
            foreach (var (source, name) in new[] { (imageSource, $"photo{n}.jpg"), (videoSource, $"different{n}.mp4") })
            {
                byte[] bytes = File.ReadAllBytes(source);
                int count = 0;
                for (int at = 0; at <= bytes.Length - old.Length; at++)
                    if (bytes.AsSpan(at, old.Length).SequenceEqual(old))
                    {
                        replacement.CopyTo(bytes, at); count++; at += old.Length - 1;
                    }
                Assert.True(count >= 2); // JSON value and length-owned trailing ID
                File.WriteAllBytes(Path.Combine(ws.RootDirectory, name), bytes);
            }
        }
        using var session = new DiscoveryInspectionSession();
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
        { InputDirectory = ws.RootDirectory, Mode = PairingMode.Vivo }, default, null, session);
        Assert.Equal(3, result.Pairs.Count);
        Assert.Equal(0, session.PairInspections);
        Assert.Equal(0, session.SingleInspections);
        Assert.All(result.Pairs, p => Assert.Equal(PairingKind.IdentityCandidate, p.PairingKind));
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task DiscoveryProgress_IsMonotonicAndCancellationDuringPairingDoesNotReturnCompleted()
    {
        using var ws = new TestFixtureWorkspace();
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.JPG"), Path.Combine(ws.RootDirectory, "apple.jpg"));
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.MOV"), Path.Combine(ws.RootDirectory, "renamed.mov"));
        var snapshots = new System.Collections.Generic.List<WorkProgressSnapshot>();
        var result = await LivePhotoDiscoveryService.ScanAsync(ws.RootDirectory, DiscoveryScanMode.CidMatch,
            progress: new CallbackProgress<WorkProgressSnapshot>(snapshots.Add));
        Assert.Single(result.DualFilePairs);
        Assert.All(snapshots,s=>Assert.InRange(s.Completed,0,s.Total));
        for(int i=1;i<snapshots.Count;i++)
            if(snapshots[i].Stage==snapshots[i-1].Stage)
                Assert.True(snapshots[i].Completed>=snapshots[i-1].Completed);
        using var cts = new CancellationTokenSource();
        var canceled = await LivePhotoDiscoveryService.ScanAsync(ws.RootDirectory, DiscoveryScanMode.SplitOnly, cts.Token,
            new CallbackProgress<WorkProgressSnapshot>(s => { if (s.Stage == ScanWorkStage.Identifying && s.Completed > 0) cts.Cancel(); }));
        Assert.Equal(ScanOperationStatus.Cancelled, canceled.Status);
        Assert.NotEmpty(canceled.Items);
        Assert.True(canceled.IsPartial);
        Assert.DoesNotContain(canceled.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CachedDiscovery_DoesNotPinSource_AndNewVersionIsObserved()
    {
        using var ws = new TestFixtureWorkspace();
        string path = Path.Combine(ws.RootDirectory, "photo.jpg");
        File.Copy(TestSampleResolver.ResolveSample("oppo.jpg"), path);
        using (var session = new DiscoveryInspectionSession())
        {
            var first = await session.ReadFastCandidateAsync(path, CancellationToken.None, DiscoveryCapability.SingleFileMarker);
            var second = await session.ReadFastCandidateAsync(path, CancellationToken.None, DiscoveryCapability.SingleFileMarker);
            Assert.Same(first, second);
            Assert.Equal(0, session.SingleInspections);
            using (var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read)) { }
            File.Move(path,path+".replaced");File.Move(path+".replaced",path);
        }
        File.WriteAllText(path, "broken file after previous session");
        using var next = new DiscoveryInspectionSession();
        Assert.NotNull((await next.InspectForDiscoveryAsync(path, null, CancellationToken.None)).Failure);
    }

    [Fact]
    public void ProgressMailbox_IsBoundedAndOldProducerCannotOverwriteNewSession()
    {
        var old = new LatestValueProgress<int>();
        Parallel.For(0, 10000, i => old.Report(i));
        old.Report(10000);
        Assert.True(old.TryTake(out int last));
        Assert.Equal(10000, last);
        Assert.False(old.TryTake(out _));
        var current = new LatestValueProgress<int>();
        current.Report(1);
        old.Report(20000);
        Assert.True(current.TryTake(out int fresh));
        Assert.Equal(1, fresh);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task FilenameMode_DoesNotDeepInspectUnmatchedCandidates_ButReportsUnreadableInput()
    {
        using var ws = new TestFixtureWorkspace();
        for (int n = 0; n < 8; n++)
            File.Copy(TestSampleResolver.ResolveSample("oppo.jpg"), Path.Combine(ws.RootDirectory, $"orphan{n}.jpg"));
        string broken = Path.Combine(ws.RootDirectory, "broken.jpg");
        File.WriteAllText(broken, "not an image");
        using var session = new DiscoveryInspectionSession();
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory }, default, null, session);
        Assert.Empty(result.Pairs);
        Assert.Equal(9, result.StandaloneImagesCount);
        Assert.Equal(0, session.SingleInspections);
        Assert.Equal(0,session.CandidateInspections);
        Assert.Contains(broken,result.StandaloneImagePaths);
        Assert.Equal(9, result.Diagnostics.Count(d => d.DiagnosticCode == "UnmatchedImage"));
    }
}
