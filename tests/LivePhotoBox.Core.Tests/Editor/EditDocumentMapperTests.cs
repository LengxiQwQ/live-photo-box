using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Editor;

[Trait("Category", "EditDocument")]
public sealed class EditDocumentMapperTests
{
    [Theory]
    [InlineData(SourceProtocol.AppleLivePhoto, LivePhotoProtocolType.Apple)]
    [InlineData(SourceProtocol.GoogleMicroVideoV1, LivePhotoProtocolType.GoogleV1)]
    [InlineData(SourceProtocol.GoogleMotionPhotoV2, LivePhotoProtocolType.GoogleV2)]
    [InlineData(SourceProtocol.OppoLivePhoto, LivePhotoProtocolType.OPPO)]
    [InlineData(SourceProtocol.VivoLivePhoto, LivePhotoProtocolType.Vivo)]
    [InlineData(SourceProtocol.VivoLegacyDualFile, LivePhotoProtocolType.Vivo)]
    [InlineData(SourceProtocol.SamsungMotionPhotoJpeg, LivePhotoProtocolType.Samsung)]
    [InlineData(SourceProtocol.SamsungMotionPhotoHeic, LivePhotoProtocolType.Samsung)]
    [InlineData(SourceProtocol.HuaweiMovingPhoto, LivePhotoProtocolType.Huawei)]
    [InlineData(SourceProtocol.HonorMovingPhoto, LivePhotoProtocolType.Huawei)]
    [InlineData(SourceProtocol.NonLive, LivePhotoProtocolType.Unknown)]
    [InlineData(SourceProtocol.Unknown, LivePhotoProtocolType.Unknown)]
    public void MapSourceProtocol_MapsToExpectedUIProtocol(SourceProtocol source, LivePhotoProtocolType expected)
    {
        var mapped = EditDocumentMapper.MapSourceProtocol(source);
        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void FromInspectedFacts_DualFileComplete_RetainsPassedPairState()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Width = 4032,
                Height = 3024,
                Container = ImageContainer.Heic
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Width = 1920,
                Height = 1080,
                DurationSeconds = 2.85,
                Fps = 30.0,
                Container = VideoContainer.Mov,
                Codec = VideoCodec.Hevc,
                HasAudio = true
            }
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Photos\IMG_0001.HEIC",
            motionPath: @"C:\Photos\IMG_0001.MOV",
            livePhotoType: LivePhotoType.DualFile,
            pairState: EditPairState.Complete,
            facts: facts);

        Assert.Equal(@"C:\Photos\IMG_0001.HEIC", doc.PrimaryPath);
        Assert.Equal(@"C:\Photos\IMG_0001.MOV", doc.MotionPath);
        Assert.Equal(LivePhotoType.DualFile, doc.LivePhotoType);
        Assert.Equal(EditPairState.Complete, doc.PairState);
        Assert.True(doc.IsPairComplete);
        Assert.True(doc.IsLivePhoto);
        Assert.Equal(LivePhotoProtocolType.Apple, doc.Protocol);
        Assert.Equal(4032u, doc.Width);
        Assert.Equal(3024u, doc.Height);
        Assert.Equal(1920u, doc.VideoWidth);
        Assert.Equal(1080u, doc.VideoHeight);
        Assert.Equal(2.85, doc.DurationSeconds);
        Assert.Equal(30.0, doc.FrameRate);
        Assert.Equal(ImageContainer.Heic, doc.ImageContainer);
        Assert.Equal(VideoContainer.Mov, doc.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, doc.VideoCodec);
        Assert.True(doc.HasAudio);
    }

    [Fact]
    public void FromInspectedFacts_DualFileMissingVideo_RetainsPassedPairState()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Width = 4032,
                Height = 3024,
                Container = ImageContainer.Heic
            },
            MotionVideo = null
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Photos\IMG_0002.HEIC",
            motionPath: null,
            livePhotoType: LivePhotoType.DualFile,
            pairState: EditPairState.MissingVideo,
            facts: facts);

        Assert.Equal(LivePhotoType.DualFile, doc.LivePhotoType);
        Assert.Equal(EditPairState.MissingVideo, doc.PairState);
        Assert.False(doc.IsPairComplete);
        Assert.True(doc.IsLivePhoto);
        Assert.Null(doc.MotionPath);
    }

    [Fact]
    public void FromInspectedFacts_SingleFileLivePhoto_ExtractsEmbeddedVideoMetrics()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.GoogleMotionPhotoV2,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Width = 4080,
                Height = 3072,
                Container = ImageContainer.Jpeg
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Width = 1920,
                Height = 1080,
                ByteOffset = 4_500_000,
                ByteLength = 3_200_000,
                DurationSeconds = 1.5,
                Fps = 30.0,
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.H264,
                HasAudio = false
            }
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Photos\PXL_2025.jpg",
            motionPath: null,
            livePhotoType: LivePhotoType.SingleFileJpeg,
            pairState: EditPairState.NotApplicable,
            facts: facts);

        Assert.Equal(LivePhotoType.SingleFileJpeg, doc.LivePhotoType);
        Assert.Equal(EditPairState.NotApplicable, doc.PairState);
        Assert.True(doc.IsPairComplete);
        Assert.True(doc.IsLivePhoto);
        Assert.True(doc.HasEmbeddedMotionVideo);
        Assert.Equal(4_500_000, doc.MotionVideoByteOffset);
        Assert.Equal(3_200_000, doc.MotionVideoByteLength);
        Assert.Equal(3_200_000, doc.AppendedVideoLength);
        Assert.Equal(LivePhotoProtocolType.GoogleV2, doc.Protocol);
        Assert.False(doc.HasAudio);
    }

    [Fact]
    public void FromInspectedFacts_StandaloneVideo_IdentifiedAsVideoMediaKind()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimaryImage = new ImageFacts { IsPresent = false },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Width = 3840,
                Height = 2160,
                DurationSeconds = 10.0,
                Fps = 60.0,
                Container = VideoContainer.Mp4,
                Codec = VideoCodec.Hevc,
                HasAudio = true
            }
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Videos\clip.mp4",
            motionPath: null,
            livePhotoType: LivePhotoType.None,
            pairState: EditPairState.NotApplicable,
            facts: facts);

        Assert.Equal(EditMediaKind.Video, doc.MediaKind);
        Assert.False(doc.IsLivePhoto);
        Assert.Equal(3840u, doc.Width);
        Assert.Equal(2160u, doc.Height);
        Assert.Equal(3840u, doc.VideoWidth);
        Assert.Equal(2160u, doc.VideoHeight);
        Assert.Equal(10.0, doc.DurationSeconds);
        Assert.Equal(60.0, doc.FrameRate);
        Assert.Equal(VideoCodec.Hevc, doc.VideoCodec);
    }

    [Fact]
    public void FromInspectedFacts_PurePhoto_IdentifiedAsPhotoMediaKind()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Width = 6000,
                Height = 4000,
                Container = ImageContainer.Jpeg
            },
            MotionVideo = null
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Photos\DSC_0001.JPG",
            motionPath: null,
            livePhotoType: LivePhotoType.None,
            pairState: EditPairState.NotApplicable,
            facts: facts);

        Assert.Equal(EditMediaKind.Photo, doc.MediaKind);
        Assert.False(doc.IsLivePhoto);
        Assert.Equal(6000u, doc.Width);
        Assert.Equal(4000u, doc.Height);
        Assert.Equal(0u, doc.VideoWidth);
        Assert.Equal(0u, doc.VideoHeight);
        Assert.Equal(ImageContainer.Jpeg, doc.ImageContainer);
    }

    [Fact]
    public void EditDocument_HasNoDependencyOn_System_IO()
    {
        // 验证 EditDocument 及其所有公共属性与方法不使用 System.IO 类型
        var docType = typeof(EditDocument);

        foreach (var prop in docType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            Assert.False(
                prop.PropertyType.Namespace == "System.IO",
                $"EditDocument property {prop.Name} references System.IO type: {prop.PropertyType}");
        }

        foreach (var method in docType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Assert.False(
                method.ReturnType.Namespace == "System.IO",
                $"EditDocument method {method.Name} returns System.IO type: {method.ReturnType}");

            foreach (var param in method.GetParameters())
            {
                Assert.False(
                    param.ParameterType.Namespace == "System.IO",
                    $"EditDocument method {method.Name} parameter {param.Name} uses System.IO type: {param.ParameterType}");
            }
        }
    }
}
