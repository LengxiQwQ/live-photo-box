using System.Collections.Generic;
using System.Linq;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed class PreservationVerdictPrecedenceTests
{
    private const uint NativeUnableToVerify = 2;
    private const uint ExifError = 0x00010000u;
    private const uint IccError = 0x00020000u;
    private const uint MakerNoteMalformed = 0x00040000u;
    private const uint XmpMalformed = 0x00080000u;
    private const uint CodestreamError = 0x00200000u;

    private static PreservationObservation Observation(uint flags = 0) => new()
    {
        Flags = flags,
        ImageCodestreamSha256 = new string('A', 64)
    };

    private static NativePreservationVerdict Verify(
        PreservationObservation pre,
        PreservationObservation post,
        uint category,
        out bool allPassed)
    {
        IReadOnlyList<NativePreservationVerdict> verdicts = NativeMediaService.VerifyPreservation(
            pre,
            post,
            preVideo: null,
            postVideo: null,
            SourceProtocol.GoogleMicroVideoV1,
            DetachedGainMapVerificationState.NotExpected,
            out allPassed);

        return verdicts.Single(v => v.Category == category);
    }

    [Theory]
    [InlineData(2, ExifError)] // Exif / TIFF
    [InlineData(3, ExifError)] // Orientation depends on Exif
    [InlineData(4, ExifError)] // GPS depends on TIFF/Exif
    [InlineData(5, IccError)] // ICC
    [InlineData(6, MakerNoteMalformed)] // MakerNote
    [InlineData(7, XmpMalformed)] // XMP non-target
    [InlineData(8, XmpMalformed)] // Extended XMP
    [InlineData(9, XmpMalformed)] // HDR/GainMap semantic proof depends on XMP
    [InlineData(11, CodestreamError)] // Video
    [InlineData(12, CodestreamError)] // Audio
    [InlineData(13, ExifError)] // Capture timestamp depends on Exif
    public void ErrorStateWithAbsentPresence_IsUnableToVerify(uint category, uint errorFlag)
    {
        foreach (bool errorOnPre in new[] { true, false })
        {
            var pre = Observation(errorOnPre ? errorFlag : 0);
            var post = Observation(errorOnPre ? 0 : errorFlag);

            NativePreservationVerdict verdict = Verify(pre, post, category, out bool allPassed);

            Assert.Equal(NativeUnableToVerify, verdict.Status);
            Assert.False(allPassed);
            Assert.NotEqual((uint)PreservationCheckStatus.NotApplicable, verdict.Status);
        }
    }

    [Fact]
    public void ValidObservationWithTrulyAbsentMetadata_RemainsNotApplicable()
    {
        var pre = Observation();
        var post = Observation();

        var verdicts = NativeMediaService.VerifyPreservation(
            pre,
            post,
            preVideo: null,
            postVideo: null,
            SourceProtocol.GoogleMicroVideoV1,
            DetachedGainMapVerificationState.NotExpected,
            out bool allPassed);

        Assert.True(allPassed);

        uint[] absentCategories =
        [
            2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13
        ];
        foreach (uint category in absentCategories)
        {
            NativePreservationVerdict verdict = verdicts.Single(v => v.Category == category);
            Assert.Equal((uint)PreservationCheckStatus.NotApplicable, verdict.Status);
        }
    }
}
