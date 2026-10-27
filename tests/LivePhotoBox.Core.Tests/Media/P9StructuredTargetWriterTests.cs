using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9StructuredTargetWriterTests
{
    private static OutputProfile Profile => OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4);

    [Theory]
    [InlineData(0L, false)] [InlineData(599999L, false)] [InlineData(null, false)] [InlineData(0L, true)]
    public async Task NativeWriterFreshValidatorAndGuardedPublicationCompleteRealLoop(long? cover, bool ordinaryXmp)
    {
        string caseName = nameof(NativeWriterFreshValidatorAndGuardedPublicationCompleteRealLoop) + (cover?.ToString() ?? "unknown") + ordinaryXmp;
        using var folder = new P9TargetTestInputs.Folder(caseName);
        const string ordinary = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:t=\"urn:independent:ordinary\" t:Keep=\"preserve-exactly\"/></rdf:RDF></x:xmpmeta>";
        var bundle = P9TargetTestInputs.Bundle(folder.Path, cover,
            ordinaryXmp ? P9TargetTestInputs.AddXmp(P9TargetTestInputs.Image, ordinary) : null);
        string imageHash = bundle.PrimaryImage.Sha256!, videoHash = bundle.MotionVideo!.Sha256!;
        string final = Path.Combine(folder.Path, "target.jpg");
        bool reopened = false;
        using var hook = new P9TargetPublicationTests.ReaderHook((stage, reader) =>
        { Assert.False(File.Exists(final)); Assert.True(reader.Length > bundle.PrimaryImage.ByteLength); reopened = true; });
        var receipt = await StructuredTargetWriterService.WriteAsync(bundle, Profile, final, folder.Path, false, CancellationToken.None);
        Assert.True(reopened); Assert.Equal(receipt.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(final))));
        Assert.Equal(imageHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle.PrimaryImage.Path))));
        Assert.Equal(videoHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle.MotionVideo.Path))));
        using var output = File.OpenRead(final); var expected = new TargetValidationExpectations(2, 0, 2, cover);
        var report = NativeTargetValidator.Observe(output, expected, CancellationToken.None); NativeTargetValidator.RequirePass(report, expected);
        byte[] bytes = File.ReadAllBytes(final);
        Assert.Equal(P9TargetTestInputs.Video, bytes.AsSpan(checked((int)report.Observed.VideoOffset)).ToArray());
        if (ordinaryXmp) Assert.Contains("t:Keep=\"preserve-exactly\"", Encoding.UTF8.GetString(bytes));
        Assert.Equal(3, Directory.GetFiles(folder.Path).Length);
        string? evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R2_OUTPUTS");
        if (!string.IsNullOrWhiteSpace(evidence))
        { Directory.CreateDirectory(evidence); File.Copy(final, Path.Combine(evidence, caseName + ".jpg"), true); }
    }

    [Fact]
    public async Task NativeMovWriterAndIndependentFreshValidationPublishExactCopiedMov()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(NativeMovWriterAndIndependentFreshValidationPublishExactCopiedMov));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, mov: true);
        string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle,
            OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMov), final, folder.Path, false, CancellationToken.None);
        using var reader = File.OpenRead(final); var expected = new TargetValidationExpectations(2, 1, 2, 0);
        var observed = NativeTargetValidator.Observe(reader, expected, CancellationToken.None); NativeTargetValidator.RequirePass(observed, expected);
        Assert.Equal(File.ReadAllBytes(bundle.MotionVideo!.Path), File.ReadAllBytes(final).AsSpan(checked((int)observed.Observed.VideoOffset)).ToArray());
        Assert.Equal(3, Directory.GetFiles(folder.Path).Length);
        string? evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R2_OUTPUTS");
        if (!string.IsNullOrWhiteSpace(evidence)) { Directory.CreateDirectory(evidence); File.Copy(final, Path.Combine(evidence, "NativeMovWriter.jpg"), true); }
    }

    [Fact]
    public async Task NativeAudioWriterPublishesBothUnchangedVideoAndAudioPayload()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(NativeAudioWriterPublishesBothUnchangedVideoAndAudioPayload));
        var bundle = P9TargetTestInputs.Bundle(folder.Path, audio: true); string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, Profile, final, folder.Path, false, CancellationToken.None);
        using var reader = File.OpenRead(final); var expected = new TargetValidationExpectations(2, 0, 2, 0);
        var report = NativeTargetValidator.Observe(reader, expected, CancellationToken.None); NativeTargetValidator.RequirePass(report, expected);
        Assert.Equal(File.ReadAllBytes(bundle.MotionVideo!.Path), File.ReadAllBytes(final).AsSpan(checked((int)report.Observed.VideoOffset)).ToArray());
        string? evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R2_OUTPUTS");
        if (!string.IsNullOrWhiteSpace(evidence)) { Directory.CreateDirectory(evidence); File.Copy(final, Path.Combine(evidence, "NativeAudioWriter.jpg"), true); }
    }

    [Fact]
    public async Task StalePreparedHashCannotProduceOutputOrAlterInputs()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(StalePreparedHashCannotProduceOutputOrAlterInputs));
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        File.AppendAllText(bundle.PrimaryImage.Path, "changed"); byte[] changed = File.ReadAllBytes(bundle.PrimaryImage.Path);
        string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<IOException>(() => StructuredTargetWriterService.WriteAsync(bundle, Profile, final, folder.Path, false, CancellationToken.None));
        Assert.False(File.Exists(final)); Assert.Equal(changed, File.ReadAllBytes(bundle.PrimaryImage.Path)); Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task ExplicitAuxiliaryCannotBeSilentlyDiscardedByExperimentalPath()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ExplicitAuxiliaryCannotBeSilentlyDiscardedByExperimentalPath));
        var bundle = P9TargetTestInputs.Bundle(folder.Path); bundle = bundle with { GainMap = bundle.PrimaryImage };
        await Assert.ThrowsAsync<NotSupportedException>(() => StructuredTargetWriterService.WriteAsync(bundle, Profile,
            Path.Combine(folder.Path, "target.jpg"), folder.Path, false, CancellationToken.None)); Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task PartialKnownOrientationCannotDisappearWhenOtherChannelIsUnknown()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(PartialKnownOrientationCannotDisappearWhenOtherChannelIsUnknown));
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        bundle = bundle with { Semantics = bundle.Semantics with { Orientation = new NeutralOrientationSemantics
        { PrimaryImage = new NeutralOrientationTransform { ClockwiseRotationDegrees = 90, RotationEvidence = NeutralEvidenceState.Verified } } } };
        await Assert.ThrowsAsync<TargetValidationException>(() => StructuredTargetWriterService.WriteAsync(bundle, Profile,
            Path.Combine(folder.Path, "target.jpg"), folder.Path, false, CancellationToken.None));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Theory]
    [InlineData(2)] [InlineData(6)] [InlineData(5)]
    public async Task RealDirectoryAclDenialBlocksNativeStageWithoutPublicationAndRestoresAcl(int target)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(RealDirectoryAclDenialBlocksNativeStageWithoutPublicationAndRestoresAcl) + target);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, audio: target == 6);
        var selectedProfile = target == 6 ? OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.JpegMp4) :
            target == 5 ? OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.JpegMp4) : Profile;
        string denied = Path.Combine(folder.Path, "denied-stage"); Directory.CreateDirectory(denied);
        var directory = new DirectoryInfo(denied);
        DirectorySecurity original = directory.GetAccessControl(AccessControlSections.Access);
        string before = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        int probeError = 0; string? stageFailure = null;
        try
        {
            DirectorySecurity restricted = directory.GetAccessControl(AccessControlSections.Access);
            restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(restricted);
            var deniedProbe = Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllBytes(Path.Combine(denied, "probe.bin"), [1]));
            probeError = deniedProbe.HResult;
            string final = Path.Combine(folder.Path, "target.jpg");
            var failure = await Assert.ThrowsAsync<IOException>(() => StructuredTargetWriterService.WriteAsync(bundle, selectedProfile,
                final, denied, false, CancellationToken.None));
            stageFailure = failure.Message;
            Assert.False(File.Exists(final)); Assert.Empty(Directory.GetFiles(denied));
            Assert.Equal(bundle.PrimaryImage.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle.PrimaryImage.Path))));
            Assert.Equal(bundle.MotionVideo!.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle.MotionVideo.Path))));
        }
        finally
        {
            // Persist an explicitly marked descriptor. Reusing an unmodified
            // DirectorySecurity snapshot does not mark its Access section dirty.
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(before, AccessControlSections.Access);
            directory.SetAccessControl(restored);
            string after = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.Equal(before, after);
            string? evidence = Environment.GetEnvironmentVariable(target == 6 ? "LIVEPHOTOBOX_P9_R4_OUTPUTS" : target == 5 ? "LIVEPHOTOBOX_P9_R5_OUTPUTS" : "LIVEPHOTOBOX_P9_R2_OUTPUTS");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "real-acl-denial.json"), System.Text.Json.JsonSerializer.Serialize(new
                { classification = "Real Windows DACL CreateFiles denial; not injected", probeHResult = probeError, stageFailure,
                    exactAclRestored = before == after, beforeSddl = before, afterSddl = after }));
            }
            Directory.Delete(denied);
        }
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task TargetCannotOverwritePreparedInput()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(TargetCannotOverwritePreparedInput));
        var bundle = P9TargetTestInputs.Bundle(folder.Path);
        await Assert.ThrowsAsync<IOException>(() => StructuredTargetWriterService.WriteAsync(bundle, Profile,
            bundle.PrimaryImage.Path, folder.Path, true, CancellationToken.None));
        Assert.Equal(bundle.PrimaryImage.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle.PrimaryImage.Path))));
    }
}
