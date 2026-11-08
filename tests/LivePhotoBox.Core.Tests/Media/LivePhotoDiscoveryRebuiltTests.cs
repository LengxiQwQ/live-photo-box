using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Core.Tests.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class LivePhotoDiscoveryRebuiltTests
{
    [Fact]
    public async Task Scan_Rebuilt_UsesNativeMarkersWithoutClaimingExtractionAuthority()
    {
        string root = Path.Combine(Path.GetTempPath(), $"lpb-rebuilt-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string livePath = Path.Combine(root, "motion.jpg");
            SyntheticProtocolFixtures.CreateGoogleV2Jpeg(livePath, withGainMap: true);

            using var session = new DiscoveryInspectionSession();
            LivePhotoDiscoveryResult result = await LivePhotoDiscoveryService.ScanFilesAsync(
                new MergeScanOptions { InputDirectory = root }, DiscoveryScanMode.JpegMarkers, session: session);
            LivePhotoDiscoveryItem item = Assert.Single(result.Items);

            Assert.Equal(LivePhotoType.SingleFileJpeg, item.LivePhotoType);
            Assert.Equal(LivePhotoDetectionMethod.JpegByteMarkers, item.DetectionMethod);
            Assert.Equal(0, session.SingleInspections);
            Assert.True(session.CandidateInspections > 0);
            Assert.Equal(0, item.AppendedVideoLength);
            Assert.Equal(0, item.MotionVideoByteOffset);
            Assert.Equal(0, item.MotionVideoByteLength);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
