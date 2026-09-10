using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ExtractorAuthorityTests
{
    private static string ResolveSample(string filename) => TestSampleResolver.ResolveSample(filename);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string P2WorkspaceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".ai-tmp")))
                return Path.Combine(dir, ".ai-tmp", "workspace", "p2-w1");

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException("The repository .ai-tmp workspace could not be located.");
    }

    private static string P2CacheRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".ai-tmp")))
                return Path.Combine(dir, ".ai-tmp", "cache", "samples");

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException("The repository .ai-tmp sample cache could not be located.");
    }

    private static string P2RepairWorkspaceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".ai-tmp")))
                return Path.Combine(dir, ".ai-tmp", "workspace", "p2-w1-repair");

            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException("The repository .ai-tmp repair workspace could not be located.");
    }

    private static string CacheSample(string sourceName, string cacheName)
    {
        string sourcePath = ResolveSample(sourceName);
        string cachePath = Path.Combine(P2CacheRoot(), cacheName);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.Copy(sourcePath, cachePath, overwrite: true);
        return cachePath;
    }

    private static (string Image, string Video, string AlternateImage) PrepareAppleDualCache() =>
        (
            CacheSample("苹果双文件.HEIC", "p2-apple-dual.HEIC"),
            CacheSample("苹果双文件.MOV", "p2-apple-dual.MOV"),
            CacheSample("苹果-双文件.JPG", "p2-apple-alternate.JPG"));

    private static void ResetEvidenceDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task InspectorIssuedPlan_ExtractsAppleDualFile_AndLeavesSourcesUnchanged()
    {
        string originalImagePath = ResolveSample("苹果双文件.HEIC");
        string originalVideoPath = ResolveSample("苹果双文件.MOV");
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        string imageShaBefore = Sha256(originalImagePath);
        string videoShaBefore = Sha256(originalVideoPath);

        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        Assert.Equal(SourceProtocol.AppleLivePhoto, inspected.Facts.Protocol);
        Assert.NotEmpty(inspected.Facts.PrimarySha256);
        Assert.False(string.IsNullOrEmpty(inspected.Facts.SecondarySha256));
        Assert.NotNull(inspected.Facts.MotionVideo);

        using var workspace = new MediaWorkspace();
        ExtractedMediaBundle bundle = await new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            workspace);

        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(imageShaBefore, Sha256(originalImagePath));
        Assert.Equal(videoShaBefore, Sha256(originalVideoPath));
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task InspectorIssuedPlan_DiagnosticsSerializeButCannotAuthorizeExtraction()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        var inspector = new SourceInspector();

        using var inspected = await inspector.InspectWithPlanAsync(imagePath, videoPath);
        string serialized = JsonSerializer.Serialize(inspected.Facts);
        SourceMediaFacts? deserialized = JsonSerializer.Deserialize<SourceMediaFacts>(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(inspected.Facts.Protocol, deserialized!.Protocol);
        Assert.Equal(inspected.Facts.PrimarySha256, deserialized.PrimarySha256);
        Assert.Equal(inspected.Facts.SecondarySha256, deserialized.SecondarySha256);
        Assert.Equal(inspected.Facts.PrimaryImage, deserialized.PrimaryImage);
        Assert.Equal(inspected.Facts.MotionVideo, deserialized.MotionVideo);
        Assert.Equal(inspected.Facts.ConfirmedResidues.Count, deserialized.ConfirmedResidues.Count);

        // There is intentionally no production overload accepting
        // SourceMediaFacts. Only the still-live Inspector-issued plan can be
        // passed to the production extractor, and it remains single-use.
        using var workspace = new MediaWorkspace();
        await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, imagePath, videoPath, workspace);
        ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(inspected.ExtractionPlan, imagePath, videoPath, null, null, null));
        Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void HarnessPlanToken_IsNotTheAuthoritativeRegistryRecordAddress()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var context = TestNativeContext.Create();
        (nint token, ulong generation, nuint recordAddress) = context.InspectPlanToken(imagePath, videoPath);

        Assert.NotEqual(nint.Zero, token);
        Assert.NotEqual(0UL, generation);
        Assert.NotEqual(0U, recordAddress);
        Assert.NotEqual((nuint)token, recordAddress);
        Assert.NotEqual((nuint)0x12345678, (nuint)token);
        context.ReleasePlan(token);
        context.ReleasePlan(token);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public void ProductionManagedAssembly_HasNoRawFactsExtractionImportOrSurface()
    {
        string assemblyPath = typeof(NativeMediaService).Assembly.Location;
        byte[] assemblyBytes = File.ReadAllBytes(assemblyPath);
        string ascii = Encoding.ASCII.GetString(assemblyBytes);
        string unicode = Encoding.Unicode.GetString(assemblyBytes);

        foreach (string forbidden in new[]
        {
            "lpb_test_extract_media_from_facts",
            "TestExtractMediaFromFacts",
            "ExtractMediaForTestsAsync"
        })
        {
            Assert.DoesNotContain(forbidden, ascii, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, unicode, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task DiagnosticMutation_DoesNotReplaceInspectorAuthority()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        SourceMediaFacts diagnostic = inspected.Facts;
        SourceMediaFacts tampered = diagnostic with
        {
            PrimaryImage = diagnostic.PrimaryImage with { ByteOffset = diagnostic.PrimaryImage.ByteOffset + 1 },
            MotionVideo = diagnostic.MotionVideo is null ? null : diagnostic.MotionVideo with
            {
                SourceIndex = diagnostic.MotionVideo.SourceIndex == 0 ? 1 : 0,
                ByteOffset = diagnostic.MotionVideo.ByteOffset + 1
            },
            GainMap = diagnostic.GainMap is null ? null : diagnostic.GainMap with
            {
                ByteOffset = diagnostic.GainMap.ByteOffset + 1,
                Relationship = diagnostic.GainMap.Relationship + "-tampered",
                Ownership = diagnostic.GainMap.Ownership == AuxiliaryOwnership.Primary
                    ? AuxiliaryOwnership.Auxiliary
                    : AuxiliaryOwnership.Primary
            },
            PairingIdentifier = "tampered-pairing",
            AuxiliaryItems = diagnostic.AuxiliaryItems.Count == 0
                ? [new AuxiliaryMediaFacts { IsPresent = true, ItemId = 0xDEAD, Relationship = "tampered" }]
                : new[] { diagnostic.AuxiliaryItems[0] }.Concat(diagnostic.AuxiliaryItems).ToArray()
        };

        Assert.NotEqual(diagnostic.PrimaryImage.ByteOffset, tampered.PrimaryImage.ByteOffset);
        Assert.NotEqual(diagnostic.PairingIdentifier, tampered.PairingIdentifier);
        if (diagnostic.AuxiliaryItems is System.Collections.Generic.IList<AuxiliaryMediaFacts> mutableAuxiliary)
        {
            mutableAuxiliary.Clear();
            mutableAuxiliary.Add(new AuxiliaryMediaFacts
            {
                IsPresent = true,
                ItemId = 0xBEEF,
                Relationship = "diagnostic-only-mutation"
            });
        }
        using var workspace = new MediaWorkspace();
        await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, imagePath, videoPath, workspace);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public void FabricatedFactsWithCurrentSha_CannotAuthorizeExtraction()
    {
        string imagePath = CacheSample("苹果双文件.HEIC", "p2-fabricated-primary.HEIC");
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Heic,
                ByteOffset = 0,
                ByteLength = new FileInfo(imagePath).Length
            },
            PrimarySha256 = Sha256(imagePath)
        };

        (NativeResult result, string? error) = InvokeRawFactsAbi(imagePath, null, facts);

        Assert.Equal(NativeResult.AuthorityViolation, result);
        Assert.StartsWith("[AuthorityViolation]", error);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task CallerFactsTampering_CannotUseTheProductionFactsAbi()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        (NativeResult result, string? error) = InvokeTamperedFactsAbi(imagePath, videoPath, inspected.Facts);

        Assert.Equal(NativeResult.AuthorityViolation, result);
        Assert.StartsWith("[AuthorityViolation]", error);
    }

    private static (NativeResult Result, string? Error) InvokeTamperedFactsAbi(
        string imagePath,
        string videoPath,
        SourceMediaFacts facts)
    {
        unsafe
        {
            NativeSourceMediaFacts tampered = NativeMediaService.MapToNativeFacts(facts);
            tampered.PrimaryImage.FileRange.Offset++;
            if (tampered.MotionVideo.IsPresent != 0)
                tampered.MotionVideo.SourceIndex = tampered.MotionVideo.SourceIndex == 0 ? 1 : 0;
            NativeGainMapItemFacts gainMap = tampered.GainMap;
            gainMap.Relationship[0] ^= 0x01;
            tampered.GainMap = gainMap;

            return InvokeRawFactsAbi(tampered, imagePath, videoPath);
        }
    }

    private static (NativeResult Result, string? Error) InvokeRawFactsAbi(
        string imagePath,
        string? videoPath,
        SourceMediaFacts facts)
    {
        unsafe
        {
            NativeSourceMediaFacts nativeFacts = NativeMediaService.MapToNativeFacts(facts);
            return InvokeRawFactsAbi(nativeFacts, imagePath, videoPath);
        }
    }

    private static unsafe (NativeResult Result, string? Error) InvokeRawFactsAbi(
        NativeSourceMediaFacts nativeFacts,
        string imagePath,
        string? videoPath)
    {
        using var context = NativeContext.Create();
        NativeResult result = TestNativeMethods.ExtractMediaLegacy(
                context.Handle,
                imagePath,
                videoPath,
                in nativeFacts,
                null,
                null,
                null);
        return (result, context.GetLastError());
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task FabricatedOrCrossContextPlanHandles_FailClosed()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        NativeContext owner = inspected.ExtractionPlan.Context!;

        using var other = NativeContext.Create();
        NativeResult crossContext = NativeMethods.ExtractMediaWithPlan(
            other.Handle,
            inspected.ExtractionPlan.NativeHandle,
            imagePath,
            videoPath,
            null,
            null,
            null);
        Assert.Equal(NativeResult.AuthorityViolation, crossContext);

        NativeResult fabricated = NativeMethods.ExtractMediaWithPlan(
            owner.Handle,
            (nint)0x12345678,
            imagePath,
            videoPath,
            null,
            null,
            null);
        Assert.Equal(NativeResult.AuthorityViolation, fabricated);

        nint guessed = new nint(inspected.ExtractionPlan.NativeHandle.ToInt64() ^ 0x5A5A5A5A);
        NativeResult guessedResult = NativeMethods.ExtractMediaWithPlan(
            owner.Handle,
            guessed,
            imagePath,
            videoPath,
            null,
            null,
            null);
        Assert.Equal(NativeResult.AuthorityViolation, guessedResult);

        NativeResult wrongGeneration = NativeMethods.ClaimExtractionPlan(
            owner.Handle,
            inspected.ExtractionPlan.NativeHandle,
            inspected.ExtractionPlan.Generation + 1);
        Assert.Equal(NativeResult.AuthorityViolation, wrongGeneration);

        NativeResult claim = NativeMethods.ClaimExtractionPlan(
            owner.Handle,
            inspected.ExtractionPlan.NativeHandle,
            inspected.ExtractionPlan.Generation);
        Assert.Equal(NativeResult.Ok, claim);
        Assert.Equal(NativeResult.Ok, NativeMethods.ReleaseExtractionPlan(owner.Handle, inspected.ExtractionPlan.NativeHandle));
        NativeResult releasedReplay = NativeMethods.ExtractMediaWithPlan(
            owner.Handle,
            inspected.ExtractionPlan.NativeHandle,
            imagePath,
            videoPath,
            null,
            null,
            null);
        Assert.Equal(NativeResult.PlanReplayed, releasedReplay);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task WrongSecondarySource_FailsWithTypedSourceChanged()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        string wrongVideoPath = CacheSample("vivo双文件.mp4", "p2-vivo-dual.mp4");
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(
                inspected.ExtractionPlan,
                imagePath,
                wrongVideoPath,
                null,
                null,
                null));

        Assert.Equal(ExtractionFailureCategory.SourceChanged, exception.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task ReplacedSecondaryAtSamePath_FailsSourceChanged_WithNoPublishedArtifacts()
    {
        string originalDesignPrimary = ResolveSample("苹果双文件.HEIC");
        string originalDesignSecondary = ResolveSample("苹果双文件.MOV");
        string primaryPath = CacheSample("苹果双文件.HEIC", "p2-secondary-replacement-primary.HEIC");
        string originalSecondary = CacheSample("苹果双文件.MOV", "p2-secondary-replacement-original.MOV");
        string alternateSecondary = CacheSample("苹果-双文件.MOV", "p2-secondary-replacement-alternate.MOV");
        string alternateImage = CacheSample("苹果-双文件.JPG", "p2-secondary-replacement-alternate.JPG");
        string evidenceRoot = Path.Combine(P2RepairWorkspaceRoot(), "secondary-replacement");
        string outputRoot = Path.Combine(evidenceRoot, "outputs");
        string evidencePath = Path.Combine(evidenceRoot, "evidence.txt");
        ResetEvidenceDirectory(evidenceRoot);
        Directory.CreateDirectory(outputRoot);

        // Prove that the replacement is another valid Apple dual-file media
        // sample before it is installed at the unchanged secondary path.
        using (InspectedSource alternateInspection = await new SourceInspector()
            .InspectWithPlanAsync(alternateImage, alternateSecondary))
        {
            Assert.Equal(SourceProtocol.AppleLivePhoto, alternateInspection.Facts.Protocol);
            Assert.NotNull(alternateInspection.Facts.MotionVideo);
        }

        string secondaryPath = Path.Combine(evidenceRoot, "same-path-secondary.MOV");
        File.Copy(originalSecondary, secondaryPath, overwrite: true);
        string primaryShaBefore = Sha256(primaryPath);
        string secondaryShaBeforeInspection = Sha256(secondaryPath);
        string originalDesignPrimaryShaBefore = Sha256(originalDesignPrimary);
        string originalDesignSecondaryShaBefore = Sha256(originalDesignSecondary);

        using var inspected = await new SourceInspector().InspectWithPlanAsync(primaryPath, secondaryPath);
        Assert.Equal(SourceProtocol.AppleLivePhoto, inspected.Facts.Protocol);
        Assert.Equal(secondaryShaBeforeInspection, inspected.Facts.SecondarySha256);
        string inspectorSecondarySha = inspected.Facts.SecondarySha256!;

        // Keep the exact path string unchanged while replacing its valid media
        // contents with a different valid Apple MOV.
        string pathBeforeReplacement = secondaryPath;
        File.Copy(alternateSecondary, secondaryPath, overwrite: true);
        string secondaryShaAfterReplacement = Sha256(secondaryPath);
        Assert.Equal(pathBeforeReplacement, secondaryPath);
        Assert.NotEqual(inspectorSecondarySha, secondaryShaAfterReplacement);

        string outputImagePath = Path.Combine(outputRoot, "primary.heic");
        string outputVideoPath = Path.Combine(outputRoot, "motion.mov");
        string outputBefore = DescribeDirectory(outputRoot);
        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(
                inspected.ExtractionPlan,
                primaryPath,
                secondaryPath,
                outputImagePath,
                outputVideoPath,
                null));
        string secondaryShaAfterExtraction = Sha256(secondaryPath);
        string outputAfter = DescribeDirectory(outputRoot);

        Assert.Equal(ExtractionFailureCategory.SourceChanged, exception.Category);
        Assert.False(File.Exists(outputImagePath));
        Assert.False(File.Exists(outputVideoPath));
        Assert.Equal(outputBefore, outputAfter);
        string primaryShaAfterExtraction = Sha256(primaryPath);
        Assert.Equal(primaryShaBefore, primaryShaAfterExtraction);
        Assert.Equal(secondaryShaAfterReplacement, secondaryShaAfterExtraction);
        Assert.Equal(originalDesignPrimaryShaBefore, Sha256(originalDesignPrimary));
        Assert.Equal(originalDesignSecondaryShaBefore, Sha256(originalDesignSecondary));

        File.WriteAllText(
            evidencePath,
            string.Join(
                Environment.NewLine,
                "case=ReplacedSecondaryAtSamePath_FailsSourceChanged_WithNoPublishedArtifacts",
                $"primary.path={primaryPath}",
                $"secondary.path={secondaryPath}",
                $"secondary.path.before={pathBeforeReplacement}",
                $"secondary.path.after={secondaryPath}",
                $"primary.sha.before={primaryShaBefore}",
                $"inspector.primary.sha={inspected.Facts.PrimarySha256}",
                $"primary.sha.after-extraction={primaryShaAfterExtraction}",
                $"inspector.secondary.sha={inspectorSecondarySha}",
                $"secondary.sha.after-replacement={secondaryShaAfterReplacement}",
                $"secondary.sha.after-extraction={secondaryShaAfterExtraction}",
                $"design.primary.sha.before={originalDesignPrimaryShaBefore}",
                $"design.primary.sha.after={Sha256(originalDesignPrimary)}",
                $"design.secondary.sha.before={originalDesignSecondaryShaBefore}",
                $"design.secondary.sha.after={Sha256(originalDesignSecondary)}",
                "extraction.result=SourceChanged",
                "output.before=" + outputBefore,
                "output.after=" + outputAfter));
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task ReplacedPrimarySourceAtSamePath_FailsWithTypedSourceChanged()
    {
        (string originalImagePath, string videoPath, string alternateImagePath) = PrepareAppleDualCache();
        string replacementPath = Path.Combine(P2WorkspaceRoot(), "authority-replaced-primary.heic");
        Directory.CreateDirectory(Path.GetDirectoryName(replacementPath)!);
        File.Copy(originalImagePath, replacementPath, overwrite: true);

        using var inspected = await new SourceInspector().InspectWithPlanAsync(replacementPath, videoPath);
        File.Copy(alternateImagePath, replacementPath, overwrite: true);

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(
                inspected.ExtractionPlan,
                replacementPath,
                videoPath,
                null,
                null,
                null));

        Assert.Equal(ExtractionFailureCategory.SourceChanged, exception.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task ConsumedPlan_CannotBeReplayedAfterARealExtraction()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        using var workspace = new MediaWorkspace();

        await new SourceExtractor().ExtractAsync(inspected.ExtractionPlan, imagePath, videoPath, workspace);

        ExtractionException exception = await Assert.ThrowsAsync<ExtractionException>(() =>
            NativeMediaService.ExtractMediaAsync(
                inspected.ExtractionPlan,
                imagePath,
                videoPath,
                null,
                null,
                null));

        Assert.Equal(ExtractionFailureCategory.PlanReplay, exception.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public async Task CancelAndRelease_AreIdempotentAndDoNotPermitReplay()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        NativeContext context = inspected.ExtractionPlan.Context!;
        nint handle = inspected.ExtractionPlan.NativeHandle;

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => NativeMediaService.ExtractMediaAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null,
            null,
            null,
            cancellation.Token));

        Assert.Equal(NativeResult.Ok, NativeMethods.ReleaseExtractionPlan(context.Handle, handle));
        Assert.Equal(NativeResult.Ok, NativeMethods.ReleaseExtractionPlan(context.Handle, handle));
        NativeResult replay = NativeMethods.ExtractMediaWithPlan(
            context.Handle,
            handle,
            imagePath,
            videoPath,
            null,
            null,
            null);
        Assert.Equal(NativeResult.PlanReplayed, replay);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task PreCancelledAttempt_ConsumesPlanBeforeCancellationAndCannotReplay()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => NativeMediaService.ExtractMediaAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null,
            null,
            null,
            cancellation.Token));

        ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() => NativeMediaService.ExtractMediaAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null,
            null,
            null));
        Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task ManagedPreflightFailure_ConsumesPlanBeforeWorkspaceAndCannotReplay()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() => new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null!));

        ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() => NativeMediaService.ExtractMediaAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null,
            null,
            null));
        Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task WorkspaceAllocationFailure_ConsumesPlanBeforeAllocationAndCannotReplay()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        using var workspace = new ThrowingAllocationWorkspace();

        await Assert.ThrowsAsync<IOException>(() => new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            workspace));

        ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() => NativeMediaService.ExtractMediaAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null,
            null,
            null));
        Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task NativeFailure_ConsumesPlanAndCannotReplay()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        using var workspace = new MediaWorkspace();

        ExtractionException failure = await Assert.ThrowsAsync<ExtractionException>(() => new SourceExtractor().ExtractAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            workspace,
            context => TestNativeHarness.SetExtractorFault(context, NativeExtractorFault.WriteFail, targetArtifact: 0)));
        Assert.Equal(ExtractionFailureCategory.OutputWriteFailed, failure.Category);

        ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() => NativeMediaService.ExtractMediaAsync(
            inspected.ExtractionPlan,
            imagePath,
            videoPath,
            null,
            null,
            null));
        Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task ConcurrentAttempts_OnlyOnePlanClaimSucceeds()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        using var firstWorkspace = new MediaWorkspace();
        using var secondWorkspace = new MediaWorkspace();
        using var start = new ManualResetEventSlim(false);

        static async Task<Exception?> Attempt(
            ManualResetEventSlim start,
            ExtractionPlan plan,
            string image,
            string video,
            IMediaWorkspace workspace)
        {
            start.Wait();
            try
            {
                await new SourceExtractor().ExtractAsync(plan, image, video, workspace);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        Task<Exception?> first = Task.Run(() => Attempt(start, inspected.ExtractionPlan, imagePath, videoPath, firstWorkspace));
        Task<Exception?> second = Task.Run(() => Attempt(start, inspected.ExtractionPlan, imagePath, videoPath, secondWorkspace));
        start.Set();
        Exception?[] results = await Task.WhenAll(first, second);

        Assert.Contains(results, exception => exception is null);
        ExtractionException replay = Assert.IsType<ExtractionException>(
            Assert.Single(results, exception => exception is not null));
        Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
    }

    private static ManualResetEventSlim? s_lifecycleEntered;
    private static ManualResetEventSlim? s_lifecycleContinue;
    private static CancellationTokenSource? s_productionCancellation;
    private static int s_productionCancellationCallbackInvoked;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BlockLifecycleStep(nint userData, int targetArtifact, ulong bytesProcessed)
    {
        if (bytesProcessed > 0)
        {
            s_lifecycleEntered?.Set();
            s_lifecycleContinue?.Wait();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CancelProductionExtractionStep(nint userData, int targetArtifact, ulong bytesProcessed)
    {
        if (bytesProcessed > 0)
        {
            Interlocked.Increment(ref s_productionCancellationCallbackInvoked);
            s_productionCancellation?.Cancel();
        }
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task InFlightCancellation_UsesBoundPlanTokenAndConsumesPlan()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        using var workspace = new MediaWorkspace();
        using var cancellation = new CancellationTokenSource();
        s_productionCancellation = cancellation;
        s_productionCancellationCallbackInvoked = 0;

        try
        {
            nint callback;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, int, ulong, void> callbackPointer = &CancelProductionExtractionStep;
                callback = (nint)callbackPointer;
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                imagePath,
                videoPath,
                workspace,
                context => TestNativeHarness.SetExtractorFault(context,
                    NativeExtractorFault.None,
                    targetArtifact: 0,
                    triggerAfterBytes: 0,
                    callback,
                    nint.Zero),
                cancellation.Token));

            Assert.True(Volatile.Read(ref s_productionCancellationCallbackInvoked) > 0,
                "The production plan extraction callback did not observe written bytes.");
            ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() => NativeMediaService.ExtractMediaAsync(
                inspected.ExtractionPlan,
                imagePath,
                videoPath,
                null,
                null,
                null));
            Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
        }
        finally
        {
            s_productionCancellation = null;
        }
    }

    [Fact]
    [Trait("Category", "Extractor")]
    public async Task DisposeDuringActiveExtraction_DefersContextDestroyUntilLeaseCompletes()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath, videoPath);
        NativeContext context = inspected.ExtractionPlan.Context!;
        using var workspace = new MediaWorkspace();
        using var entered = new ManualResetEventSlim(false);
        using var continueSignal = new ManualResetEventSlim(false);
        s_lifecycleEntered = entered;
        s_lifecycleContinue = continueSignal;

        try
        {
            nint callback;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, int, ulong, void> callbackPointer = &BlockLifecycleStep;
                callback = (nint)callbackPointer;
            }

            Task<ExtractedMediaBundle> extraction = new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan,
                imagePath,
                videoPath,
                workspace,
                context => TestNativeHarness.SetExtractorFault(context,
                    NativeExtractorFault.None,
                    targetArtifact: 0,
                    triggerAfterBytes: 0,
                    callback,
                    nint.Zero));

            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))), "Native extraction did not reach the deterministic lease barrier.");
            inspected.Dispose();
            Assert.Equal(nint.Zero, inspected.ExtractionPlan.NativeHandle);

            continueSignal.Set();
            await extraction;
            Assert.Equal(nint.Zero, context.Handle);
            Assert.Throws<ObjectDisposedException>(() => context.AcquireOperationLease());

            ExtractionException replay = await Assert.ThrowsAsync<ExtractionException>(() => NativeMediaService.ExtractMediaAsync(
                inspected.ExtractionPlan,
                imagePath,
                videoPath,
                null,
                null,
                null));
            Assert.Equal(ExtractionFailureCategory.PlanReplay, replay.Category);
            inspected.Dispose();
        }
        finally
        {
            continueSignal.Set();
            s_lifecycleEntered = null;
            s_lifecycleContinue = null;
        }
    }

    [Fact]
    [Trait("Category", "Extractor")]
    [Trait("Category", "RealSamples")]
    public void ContextDestruction_ReleasesMultipleOutstandingPlans_WithoutUsingFreedPointers()
    {
        (string imagePath, string videoPath, _) = PrepareAppleDualCache();
        using var context = TestNativeContext.Create();
        ulong contextId = TestNativeHarness.GetContextId(context.Handle);
        Assert.NotEqual(0UL, contextId);

        (nint Token, ulong Generation, nuint RecordAddress)[] plans = Enumerable.Range(0, 3)
            .Select(_ => context.InspectPlanToken(imagePath, videoPath))
            .ToArray();
        Assert.Equal(3, plans.Length);
        Assert.All(plans, plan =>
        {
            Assert.NotEqual(nint.Zero, plan.Token);
            Assert.NotEqual(0UL, plan.Generation);
            Assert.NotEqual(0U, plan.RecordAddress);
        });

        NativePlanAccounting issued = TestNativeHarness.GetLivePlanAccounting(context.Handle);
        Assert.Equal(contextId, issued.ContextId);
        Assert.Equal(3UL, issued.Issued);
        Assert.Equal(0UL, issued.Claimed);
        Assert.Equal(0UL, issued.Consumed);
        Assert.Equal(0UL, issued.Released);
        Assert.Equal(3UL, issued.LivePlanCount);
        Assert.Equal(3UL, issued.RegistryRecordCount);
        Assert.Equal(0U, issued.ContextDestroyed);

        // Dispose destroys the native context only after all active operation
        // leases complete. The safe post-destruction probe accepts only the
        // archived context id and integer token; it never receives a freed
        // lpb_context* or lpb_extraction_plan* pointer.
        context.Dispose();

        NativePlanAccounting destroyed = TestNativeHarness.GetDestroyedPlanAccounting(contextId);
        Assert.Equal(contextId, destroyed.ContextId);
        Assert.Equal(3UL, destroyed.Issued);
        Assert.Equal(0UL, destroyed.Claimed);
        Assert.Equal(0UL, destroyed.Consumed);
        Assert.Equal(3UL, destroyed.Released);
        Assert.Equal(3UL, destroyed.ReleasedOnContextDestroy);
        Assert.Equal(0UL, destroyed.LivePlanCount);
        Assert.Equal(0UL, destroyed.RegistryRecordCount);
        Assert.Equal(1U, destroyed.ContextDestroyed);

        NativeResult[] probeResults = new NativeResult[plans.Length];
        string[] planTokens = new string[plans.Length];
        for (int i = 0; i < plans.Length; i++)
        {
            planTokens[i] = $"0x{plans[i].Token.ToInt64():X16}";
            probeResults[i] = TestNativeHarness.ProbeDestroyedPlan(
                contextId,
                unchecked((ulong)plans[i].Token.ToInt64()));
            Assert.Equal(NativeResult.PlanReplayed, probeResults[i]);
        }

        string accountingEvidencePath = Path.Combine(P2RepairWorkspaceRoot(), "context-destruction-accounting.txt");
        Directory.CreateDirectory(P2RepairWorkspaceRoot());
        File.WriteAllText(
            accountingEvidencePath,
            string.Join(
                Environment.NewLine,
                "case=ContextDestruction_ReleasesMultipleOutstandingPlans_WithoutUsingFreedPointers",
                $"context.id={contextId}",
                $"issued.context-destroyed={issued.ContextDestroyed}",
                $"issued.issued={issued.Issued}",
                $"issued.claimed={issued.Claimed}",
                $"issued.consumed={issued.Consumed}",
                $"issued.released={issued.Released}",
                $"issued.live-plan-count={issued.LivePlanCount}",
                $"issued.registry-record-count={issued.RegistryRecordCount}",
                $"destroyed.context-destroyed={destroyed.ContextDestroyed}",
                $"destroyed.issued={destroyed.Issued}",
                $"destroyed.claimed={destroyed.Claimed}",
                $"destroyed.consumed={destroyed.Consumed}",
                $"destroyed.released={destroyed.Released}",
                $"destroyed.released-on-context-destroy={destroyed.ReleasedOnContextDestroy}",
                $"destroyed.live-plan-count={destroyed.LivePlanCount}",
                $"destroyed.registry-record-count={destroyed.RegistryRecordCount}",
                $"plan.tokens={string.Join(",", planTokens)}",
                $"post-destroy.probes={string.Join(",", probeResults)}",
                "post-destroy.pointer-inputs=none"));

    }

    private sealed class ThrowingAllocationWorkspace : IMediaWorkspace
    {
        private readonly MediaWorkspace _inner = new();

        public string RootDirectory => Path.GetTempPath();

        public string AllocateFilePath(string prefix, string extension) =>
            throw new IOException("Deterministic test workspace allocation failure.");

        public Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default) =>
            _inner.ComputeFileSha256Async(filePath, cancellationToken);

        public Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default) =>
            _inner.AssertSourceUnmodifiedAsync(sourcePath, expectedSha256, cancellationToken);

        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static string DescribeDirectory(string path)
    {
        string[] entries = Directory
            .EnumerateFileSystemEntries(path)
            .Select(entry => Path.GetRelativePath(path, entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();
        return entries.Length == 0 ? "<empty>" : string.Join("|", entries);
    }

    [Theory]
    [InlineData(6, ExtractionFailureCategory.AuthorityViolation)]
    [InlineData(7, ExtractionFailureCategory.PlanReplay)]
    [InlineData(8, ExtractionFailureCategory.SourceChanged)]
    [Trait("Category", "Extractor")]
    public void NativeTypedFailureCodes_MapToTypedManagedExceptions(
        int result,
        ExtractionFailureCategory expectedCategory)
    {
        using var context = NativeContext.Create();
        ExtractionException exception = Assert.Throws<ExtractionException>(() => context.ThrowIfFailed((NativeResult)result));
        Assert.Equal(expectedCategory, exception.Category);
    }
}
