using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9TargetValidatorAbiTests
{
    [Fact]
    public void ReaderRequestAndResultMatchPairedNativeLayouts()
    {
        Assert.Equal(48, Marshal.SizeOf<NativeTargetReaderV1>());
        Assert.Equal(24, Marshal.OffsetOf<NativeTargetReaderV1>(nameof(NativeTargetReaderV1.ReadAt)).ToInt32());
        Assert.Equal(64, Marshal.SizeOf<NativeTargetValidationRequestV1>());
        Assert.Equal(32, Marshal.OffsetOf<NativeTargetValidationRequestV1>(nameof(NativeTargetValidationRequestV1.CoverTimestampUs)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<NativeTargetValidationRequestV1>(nameof(NativeTargetValidationRequestV1.Reserved)).ToInt32());
        Assert.Equal(176, Marshal.SizeOf<NativeTargetValidationResultV1>());
        Assert.Equal(48, Marshal.OffsetOf<NativeTargetValidationResultV1>(nameof(NativeTargetValidationResultV1.ObservedByteLength)).ToInt32());
        Assert.Equal(136, Marshal.OffsetOf<NativeTargetValidationResultV1>(nameof(NativeTargetValidationResultV1.FailureByteOffset)).ToInt32());
        Assert.Equal(144, Marshal.OffsetOf<NativeTargetValidationResultV1>(nameof(NativeTargetValidationResultV1.ObservedSha256)).ToInt32());
    }

    [Fact]
    public unsafe void MissingReaderCannotReturnAConformancePass()
    {
        using var context = NativeContext.Create(); using var operation = context.AcquireOperationLease();
        var request = new NativeTargetValidationRequestV1 { StructSize = 64, Version = 1, TargetProfile = 2,
            TargetVersion = 1, MediaFormat = 0, MemberCount = 1, ExpectedVideoCodec = 2 };
        var result = new NativeTargetValidationResultV1 { StructSize = 176, Version = 1, StructuralStatus = 1, GenericMediaStatus = 1 };
        Assert.NotEqual(NativeResult.Ok, NativeMethods.TargetValidate(operation.Handle, &request, null, &result));
        Assert.NotEqual(1u, result.StructuralStatus); Assert.NotEqual(1u, result.GenericMediaStatus);
    }
}

public sealed class P9TargetValidatorTests
{
    [Theory]
    [InlineData("v2-known-zero.jpg", 0L)]
    [InlineData("v2-known-near-end.jpg", 599999L)]
    [InlineData("v2-unknown.jpg", null)]
    [InlineData("v2-stale-v1.jpg", 0L)]
    [InlineData("v2-independent-mov.jpg", 0L)]
    [InlineData("v2-independent-audio.jpg", 0L)]
    public void IndependentArtifactsProduceFreshFactsWithoutWriterReceipts(string name, long? cover)
    {
        using var stream = File.OpenRead(Path.Combine(P9TargetTestInputs.Root, name));
        uint format = name.Contains("mov", StringComparison.Ordinal) ? 1u : 0;
        var expected = new TargetValidationExpectations(2, format, 2, cover);
        var report = NativeTargetValidator.Observe(stream, expected, CancellationToken.None);
        NativeTargetValidator.RequirePass(report, expected);
        Assert.Equal((ulong)stream.Length, report.Observed.ObservedByteLength);
        string input = format == 1 ? "neutral.mov" : name.Contains("audio", StringComparison.Ordinal) ? "neutral-audio.mp4" : "neutral.mp4";
        Assert.Equal((ulong)new FileInfo(Path.Combine(P9TargetTestInputs.Root, input)).Length, report.Observed.VideoLength);
        Assert.Equal((ulong)stream.Length, report.Observed.VideoOffset + report.Observed.VideoLength);
        Assert.True(report.Observed.ImageWidth > 0); Assert.True(report.Observed.VideoWidth > 0);
        Assert.Equal(0u, report.Observed.PreservationStatus); Assert.Equal(0u, report.Observed.DeviceStatus);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(stream.Name))), report.Sha256);
    }

    [Theory]
    [InlineData("bad-length.jpg")] [InlineData("bad-namespace.jpg")]
    [InlineData("bad-cover.jpg")] [InlineData("bad-trailing.jpg")]
    [InlineData("bad-duplicate-packet.jpg")] [InlineData("bad-postscan-packet.jpg")]
    public async Task IndependentlyMutatedArtifactCannotPublishThroughLowerService(string name)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(IndependentlyMutatedArtifactCannotPublishThroughLowerService) + name);
        byte[] bytes = File.ReadAllBytes(Path.Combine(P9TargetTestInputs.Root, name)), sentinel = [41, 42, 43];
        string final = Path.Combine(folder.Path, "existing.jpg"); File.WriteAllBytes(final, sentinel);
        using var stream = File.OpenRead(Path.Combine(P9TargetTestInputs.Root, name));
        var expected = new TargetValidationExpectations(2, 0, 2, 0);
        var report = NativeTargetValidator.Observe(stream, expected, CancellationToken.None);
        Assert.False(report.CanPublish);
        await Assert.ThrowsAsync<TargetValidationException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, true,
            async (output, token) => await output.WriteAsync(bytes, token), expected, CancellationToken.None));
        Assert.Equal(sentinel, File.ReadAllBytes(final)); Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData(1u, 0u, 2u, 0L)] [InlineData(2u, 1u, 2u, 0L)]
    [InlineData(2u, 0u, 3u, 0L)] [InlineData(2u, 0u, 2u, 100000L)]
    public async Task CorrectBytesCannotSatisfyWrongTargetFormatCodecOrTiming(uint target, uint format, uint codec, long cover)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(CorrectBytesCannotSatisfyWrongTargetFormatCodecOrTiming) + target + format + codec + cover);
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<TargetValidationException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, false,
            async (output, token) => await output.WriteAsync(P9TargetTestInputs.V2(), token), new(target, format, codec, cover), CancellationToken.None));
        Assert.False(File.Exists(final)); Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void OrdinaryEmptyUdtaIsPreservedAndValidatedWithoutMetadataDiscard()
    {
        string path = Path.Combine(P9TargetTestInputs.Root, "v2-audio-with-empty-udta.jpg");
        byte[] before = File.ReadAllBytes(path);
        using var reader = File.OpenRead(path);
        var report = NativeTargetValidator.Observe(reader, new(2, 0, 2, 0), CancellationToken.None);
        Assert.Equal(NativeResult.Ok, report.InvocationResult);
        Assert.True(report.CanPublish, $"ordinary metadata rejected: category={report.Observed.FailureCategory}, stage={report.Observed.FailureStage}, rule={report.Observed.RuleId}, offset={report.Observed.FailureByteOffset}");
        Assert.Equal(1u, report.Observed.StructuralStatus); Assert.Equal(0u, report.Observed.FailureCategory);
        Assert.True(report.CanPublish); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ManagedReadCallbackExceptionIsContainedAndCannotPublish()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ManagedReadCallbackExceptionIsContainedAndCannotPublish));
        string final = Path.Combine(folder.Path, "target.jpg"); byte[] original = [31, 32]; File.WriteAllBytes(final, original);
        var previous = NativeTargetValidator.ReadCallbackForTests.Value;
        try
        {
            NativeTargetValidator.ReadCallbackForTests.Value = () => throw new InvalidOperationException("Injected callback exception; state-machine evidence only.");
            var failure = await Assert.ThrowsAsync<IOException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, true,
                async (output, token) => await output.WriteAsync(P9TargetTestInputs.V2(), token), new(2, 0, 2, 0), CancellationToken.None));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Equal(original, File.ReadAllBytes(final)); Assert.Single(Directory.GetFiles(folder.Path));
        }
        finally { NativeTargetValidator.ReadCallbackForTests.Value = previous; }
    }

    [Fact]
    public async Task KnownPreparedDimensionCannotBeIgnoredByMandatoryGate()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(KnownPreparedDimensionCannotBeIgnoredByMandatoryGate));
        string final = Path.Combine(folder.Path, "target.jpg");
        var expected = new TargetValidationExpectations(2, 0, 2, 0) { ImageWidth = uint.MaxValue };
        await Assert.ThrowsAsync<TargetValidationException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, false,
            async (output, token) => await output.WriteAsync(P9TargetTestInputs.V2(), token), expected, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(folder.Path));
    }
}
