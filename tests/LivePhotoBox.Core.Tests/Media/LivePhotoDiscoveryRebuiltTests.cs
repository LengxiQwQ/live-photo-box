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
    public async Task Scan_Rebuilt_UsesNativeSingleFileInspection()
    {
        string root = Path.Combine(Path.GetTempPath(), $"lpb-rebuilt-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string livePath = Path.Combine(root, "motion.jpg");
            SyntheticProtocolFixtures.CreateGoogleV2Jpeg(livePath, withGainMap: true);

            LivePhotoDiscoveryResult result = await LivePhotoDiscoveryService.ScanAsync(
                root, DiscoveryScanMode.JpegMarkers);
            LivePhotoDiscoveryItem item = Assert.Single(result.Items);

            Assert.Equal(LivePhotoType.SingleFileJpeg, item.LivePhotoType);
            Assert.Equal(LivePhotoDetectionMethod.JpegByteMarkers, item.DetectionMethod);
            Assert.True(item.AppendedVideoLength > 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
