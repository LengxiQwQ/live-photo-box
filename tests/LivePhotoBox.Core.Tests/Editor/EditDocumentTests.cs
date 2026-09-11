using System;
using System.Reflection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Editor;

[Trait("Category", "EditDocument")]
public sealed class EditDocumentTests
{
    [Fact]
    public void StillPhoto_CanBeExpressedCorrectly()
    {
        var doc = EditDocument.FromPhoto(
            primaryPath: @"C:\Photos\IMG_1234.JPG",
            width: 4032,
            height: 3024,
            container: ImageContainer.Jpeg,
            metadata: new EditMetadataFacts
            {
                CameraModel = "Sony A7M4",
                LensModel = "FE 24-70mm F2.8 GM II",
                ShootingParams = "1/160s f/2.8 ISO 100",
                PlaceName = "Shanghai",
                DateTaken = new DateTime(2025, 5, 20, 14, 30, 0, DateTimeKind.Utc)
            });

        Assert.Equal(@"C:\Photos\IMG_1234.JPG", doc.PrimaryPath);
        Assert.Null(doc.MotionPath);
        Assert.Null(doc.PairedVideoPath);
        Assert.Equal(EditMediaKind.Photo, doc.MediaKind);
        Assert.Equal(LivePhotoType.None, doc.LivePhotoType);
        Assert.False(doc.IsLivePhoto);
        Assert.Equal(LivePhotoProtocolType.Unknown, doc.Protocol);
        Assert.Equal(SourceProtocol.NonLive, doc.SourceProtocol);
        Assert.Equal(EditPairState.NotApplicable, doc.PairState);
        Assert.True(doc.IsPairComplete);
        Assert.Equal(4032u, doc.Width);
        Assert.Equal(3024u, doc.Height);
        Assert.Equal(ImageContainer.Jpeg, doc.ImageContainer);
        Assert.False(doc.HasEmbeddedMotionVideo);
        Assert.Equal(0, doc.MotionVideoByteOffset);
        Assert.Equal(0, doc.MotionVideoByteLength);
        Assert.Equal("Sony A7M4", doc.Metadata.CameraModel);
        Assert.Equal("Shanghai", doc.Metadata.PlaceName);
    }

    [Fact]
    public void StandaloneVideo_CanBeExpressedCorrectly()
    {
        var doc = EditDocument.FromVideo(
            primaryPath: @"C:\Videos\clip.mp4",
            width: 1920,
            height: 1080,
            durationSeconds: 12.5,
            frameRate: 60.0,
            container: VideoContainer.Mp4,
            codec: VideoCodec.H264,
            hasAudio: true);

        Assert.Equal(@"C:\Videos\clip.mp4", doc.PrimaryPath);
        Assert.Null(doc.MotionPath);
        Assert.Equal(EditMediaKind.Video, doc.MediaKind);
        Assert.Equal(LivePhotoType.None, doc.LivePhotoType);
        Assert.False(doc.IsLivePhoto);
        Assert.Equal(LivePhotoProtocolType.Unknown, doc.Protocol);
        Assert.Equal(SourceProtocol.NonLive, doc.SourceProtocol);
        Assert.Equal(EditPairState.NotApplicable, doc.PairState);
        Assert.True(doc.IsPairComplete);
        Assert.Equal(1920u, doc.Width);
        Assert.Equal(1080u, doc.Height);
        Assert.Equal(1920u, doc.VideoWidth);
        Assert.Equal(1080u, doc.VideoHeight);
        Assert.Equal(12.5, doc.DurationSeconds);
        Assert.Equal(60.0, doc.FrameRate);
        Assert.Equal(VideoContainer.Mp4, doc.VideoContainer);
        Assert.Equal(VideoCodec.H264, doc.VideoCodec);
        Assert.True(doc.HasAudio);
    }

    [Fact]
    public void DualFileLivePhoto_CanBeExpressedCorrectly()
    {
        var doc = EditDocument.FromLivePhoto(
            primaryPath: @"C:\LivePhotos\IMG_0001.HEIC",
            motionPath: @"C:\LivePhotos\IMG_0001.MOV",
            livePhotoType: LivePhotoType.DualFile,
            protocol: LivePhotoProtocolType.Apple,
            sourceProtocol: SourceProtocol.AppleLivePhoto,
            pairState: EditPairState.Complete,
            width: 4032,
            height: 3024,
            durationSeconds: 2.98,
            frameRate: 30.0,
            imageContainer: ImageContainer.Heic,
            videoContainer: VideoContainer.Mov,
            videoCodec: VideoCodec.Hevc,
            hasAudio: true,
            originalKeyPhoto: new EditKeyPhotoInfo
            {
                FrameIndex = 42,
                TimestampSeconds = 1.4,
                TimestampUs = 1400000,
                IsOriginal = true
            });

        Assert.Equal(@"C:\LivePhotos\IMG_0001.HEIC", doc.PrimaryPath);
        Assert.Equal(@"C:\LivePhotos\IMG_0001.MOV", doc.MotionPath);
        Assert.Equal(@"C:\LivePhotos\IMG_0001.MOV", doc.PairedVideoPath);
        Assert.Equal(EditMediaKind.LivePhoto, doc.MediaKind);
        Assert.Equal(LivePhotoType.DualFile, doc.LivePhotoType);
        Assert.True(doc.IsLivePhoto);
        Assert.Equal(LivePhotoProtocolType.Apple, doc.Protocol);
        Assert.Equal(SourceProtocol.AppleLivePhoto, doc.SourceProtocol);
        Assert.Equal(EditPairState.Complete, doc.PairState);
        Assert.True(doc.IsPairComplete);
        Assert.Equal(4032u, doc.Width);
        Assert.Equal(3024u, doc.Height);
        Assert.Equal(2.98, doc.DurationSeconds);
        Assert.Equal(30.0, doc.FrameRate);
        Assert.Equal(ImageContainer.Heic, doc.ImageContainer);
        Assert.Equal(VideoContainer.Mov, doc.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, doc.VideoCodec);
        Assert.NotNull(doc.OriginalKeyPhoto);
        Assert.Equal(42, doc.OriginalKeyPhoto.FrameIndex);
        Assert.Equal(1400000, doc.OriginalKeyPhoto.TimestampUs);
        Assert.True(doc.OriginalKeyPhoto.IsOriginal);
        Assert.Equal(doc.OriginalKeyPhoto, doc.CurrentKeyPhoto);
    }

    [Fact]
    public void SingleFileMotionPhoto_CanExpressMotionRange()
    {
        var doc = EditDocument.FromLivePhoto(
            primaryPath: @"C:\LivePhotos\MVIMG_20250101.jpg",
            motionPath: null,
            livePhotoType: LivePhotoType.SingleFileJpeg,
            protocol: LivePhotoProtocolType.GoogleV2,
            sourceProtocol: SourceProtocol.GoogleMotionPhotoV2,
            pairState: EditPairState.NotApplicable,
            width: 4000,
            height: 3000,
            durationSeconds: 1.5,
            frameRate: 30.0,
            motionVideoByteOffset: 2048500,
            motionVideoByteLength: 1048576,
            imageContainer: ImageContainer.Jpeg,
            videoContainer: VideoContainer.Mp4,
            videoCodec: VideoCodec.H264);

        Assert.Equal(EditMediaKind.LivePhoto, doc.MediaKind);
        Assert.Equal(LivePhotoType.SingleFileJpeg, doc.LivePhotoType);
        Assert.True(doc.IsLivePhoto);
        Assert.Null(doc.MotionPath);
        Assert.True(doc.HasEmbeddedMotionVideo);
        Assert.Equal(2048500, doc.MotionVideoByteOffset);
        Assert.Equal(1048576, doc.MotionVideoByteLength);
        Assert.Equal(1048576, doc.AppendedVideoLength);
        Assert.Equal(EditPairState.NotApplicable, doc.PairState);
        Assert.True(doc.IsPairComplete);
    }

    [Fact]
    public void DualFileLivePhoto_MissingVideo_ExpressesIncompleteState()
    {
        var doc = EditDocument.FromLivePhoto(
            primaryPath: @"C:\LivePhotos\IMG_0002.HEIC",
            motionPath: null,
            livePhotoType: LivePhotoType.DualFile,
            protocol: LivePhotoProtocolType.Apple,
            sourceProtocol: SourceProtocol.AppleLivePhoto,
            pairState: EditPairState.MissingVideo,
            width: 4032,
            height: 3024);

        Assert.Equal(EditMediaKind.LivePhoto, doc.MediaKind);
        Assert.Equal(LivePhotoType.DualFile, doc.LivePhotoType);
        Assert.True(doc.IsLivePhoto);
        Assert.Null(doc.MotionPath);
        Assert.Equal(EditPairState.MissingVideo, doc.PairState);
        Assert.False(doc.IsPairComplete);
    }

    [Fact]
    public void ProtocolAndMediaTypes_AreStronglyTyped()
    {
        // Assert all protocol and media types on EditDocument are strongly typed enums
        var propProtocol = typeof(EditDocument).GetProperty(nameof(EditDocument.Protocol))!;
        Assert.Equal(typeof(LivePhotoProtocolType), propProtocol.PropertyType);
        Assert.True(propProtocol.PropertyType.IsEnum);

        var propSourceProtocol = typeof(EditDocument).GetProperty(nameof(EditDocument.SourceProtocol))!;
        Assert.Equal(typeof(SourceProtocol), propSourceProtocol.PropertyType);
        Assert.True(propSourceProtocol.PropertyType.IsEnum);

        var propLivePhotoType = typeof(EditDocument).GetProperty(nameof(EditDocument.LivePhotoType))!;
        Assert.Equal(typeof(LivePhotoType), propLivePhotoType.PropertyType);
        Assert.True(propLivePhotoType.PropertyType.IsEnum);

        var propMediaKind = typeof(EditDocument).GetProperty(nameof(EditDocument.MediaKind))!;
        Assert.Equal(typeof(EditMediaKind), propMediaKind.PropertyType);
        Assert.True(propMediaKind.PropertyType.IsEnum);

        var propPairState = typeof(EditDocument).GetProperty(nameof(EditDocument.PairState))!;
        Assert.Equal(typeof(EditPairState), propPairState.PropertyType);
        Assert.True(propPairState.PropertyType.IsEnum);

        var propImgContainer = typeof(EditDocument).GetProperty(nameof(EditDocument.ImageContainer))!;
        Assert.Equal(typeof(ImageContainer), propImgContainer.PropertyType);
        Assert.True(propImgContainer.PropertyType.IsEnum);

        var propVidContainer = typeof(EditDocument).GetProperty(nameof(EditDocument.VideoContainer))!;
        Assert.Equal(typeof(VideoContainer), propVidContainer.PropertyType);
        Assert.True(propVidContainer.PropertyType.IsEnum);

        var propVidCodec = typeof(EditDocument).GetProperty(nameof(EditDocument.VideoCodec))!;
        Assert.Equal(typeof(VideoCodec), propVidCodec.PropertyType);
        Assert.True(propVidCodec.PropertyType.IsEnum);
    }

    [Fact]
    public void Document_HasNoWinUIPresentationDependencies()
    {
        // Verify EditDocument is compiled in LivePhotoBox.Core (a UI-free assembly)
        var asm = typeof(EditDocument).Assembly;
        Assert.Equal("LivePhotoBox.Core", asm.GetName().Name);

        // Verify no property on EditDocument references UI types
        foreach (var prop in typeof(EditDocument).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var typeName = prop.PropertyType.FullName ?? string.Empty;
            Assert.DoesNotContain("Microsoft.UI", typeName);
            Assert.DoesNotContain("Windows.UI", typeName);
            Assert.DoesNotContain("ImageSource", typeName);
            Assert.DoesNotContain("BitmapImage", typeName);
            Assert.DoesNotContain("FrameworkElement", typeName);
            Assert.DoesNotContain("Control", typeName);
        }
    }

    [Fact]
    public void ClearAndReplace_DoesNotLeakOldDocumentState()
    {
        // Session container / reference simulation
        EditDocument? currentDoc = null;
        EditSessionState sessionState = EditSessionState.Closed;

        // Step 1: Loading doc1
        sessionState = EditSessionState.Loading;
        var doc1 = EditDocument.FromLivePhoto(
            primaryPath: @"C:\Media\Doc1.jpg",
            motionPath: null,
            livePhotoType: LivePhotoType.SingleFileJpeg,
            protocol: LivePhotoProtocolType.GoogleV2,
            sourceProtocol: SourceProtocol.GoogleMotionPhotoV2,
            width: 4000,
            height: 3000,
            originalKeyPhoto: new EditKeyPhotoInfo { FrameIndex = 15, TimestampSeconds = 0.5 });
        currentDoc = doc1;
        sessionState = EditSessionState.Ready;

        Assert.NotNull(currentDoc);
        Assert.Equal(EditSessionState.Ready, sessionState);
        Assert.Equal(@"C:\Media\Doc1.jpg", currentDoc.PrimaryPath);
        Assert.Equal(15, currentDoc.CurrentKeyPhoto?.FrameIndex);
        Assert.True(currentDoc.IsLivePhoto);

        // Step 2: Replace with doc2 (pure video, completely different facts)
        sessionState = EditSessionState.Loading;
        var doc2 = EditDocument.FromVideo(
            primaryPath: @"C:\Media\Doc2.mp4",
            width: 1920,
            height: 1080,
            durationSeconds: 10.0,
            frameRate: 30.0);
        currentDoc = doc2;
        sessionState = EditSessionState.Ready;

        Assert.NotNull(currentDoc);
        Assert.Equal(@"C:\Media\Doc2.mp4", currentDoc.PrimaryPath);
        Assert.False(currentDoc.IsLivePhoto);
        Assert.Equal(EditMediaKind.Video, currentDoc.MediaKind);
        Assert.Null(currentDoc.CurrentKeyPhoto);
        Assert.Null(currentDoc.OriginalKeyPhoto);
        Assert.Equal(LivePhotoProtocolType.Unknown, currentDoc.Protocol);

        // Step 3: Clear document
        currentDoc = null;
        sessionState = EditSessionState.Closed;

        Assert.Null(currentDoc);
        Assert.Equal(EditSessionState.Closed, sessionState);
    }

    [Fact]
    public void FromInspectedFacts_MapsSourceMediaFactsAccurately()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Container = ImageContainer.Jpeg,
                Width = 4032,
                Height = 3024,
                ByteOffset = 0,
                ByteLength = 3_000_000
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Container = VideoContainer.Mov,
                Codec = VideoCodec.H264,
                Width = 1920,
                Height = 1080,
                DurationSeconds = 3.0,
                Fps = 30.0,
                HasAudio = true,
                RotationDegrees = 90,
                ByteOffset = 0,
                ByteLength = 4_500_000,
                SourceIndex = 1
            },
            Timing = new TimingFacts
            {
                CoverFrameIndex = 45,
                CoverTimestampUs = 1_500_000,
                TotalFrames = 90
            }
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Test\IMG_100.JPG",
            motionPath: @"C:\Test\IMG_100.MOV",
            livePhotoType: LivePhotoType.DualFile,
            pairState: EditPairState.Complete,
            facts: facts);

        Assert.Equal(@"C:\Test\IMG_100.JPG", doc.PrimaryPath);
        Assert.Equal(@"C:\Test\IMG_100.MOV", doc.MotionPath);
        Assert.Equal(EditMediaKind.LivePhoto, doc.MediaKind);
        Assert.Equal(LivePhotoType.DualFile, doc.LivePhotoType);
        Assert.Equal(LivePhotoProtocolType.Apple, doc.Protocol);
        Assert.Equal(SourceProtocol.AppleLivePhoto, doc.SourceProtocol);
        Assert.Equal(4032u, doc.Width);
        Assert.Equal(3024u, doc.Height);
        Assert.Equal(3.0, doc.DurationSeconds);
        Assert.Equal(30.0, doc.FrameRate);
        Assert.Equal(ImageContainer.Jpeg, doc.ImageContainer);
        Assert.Equal(VideoContainer.Mov, doc.VideoContainer);
        Assert.Equal(VideoCodec.H264, doc.VideoCodec);
        Assert.True(doc.HasAudio);
        Assert.Equal(90, doc.RotationDegrees);
        Assert.NotNull(doc.OriginalKeyPhoto);
        Assert.Equal(45, doc.OriginalKeyPhoto.FrameIndex);
        Assert.Equal(1_500_000, doc.OriginalKeyPhoto.TimestampUs);
        Assert.Equal(1.5, doc.OriginalKeyPhoto.TimestampSeconds);
    }

    [Fact]
    public void RecordEvolutionWithExpression_IsImmutable()
    {
        var original = EditDocument.FromLivePhoto(
            primaryPath: @"C:\Photos\Test.jpg",
            motionPath: null,
            livePhotoType: LivePhotoType.SingleFileJpeg,
            protocol: LivePhotoProtocolType.GoogleV2,
            originalKeyPhoto: new EditKeyPhotoInfo { FrameIndex = 10, TimestampSeconds = 0.33, IsOriginal = true });

        var newKeyPhoto = new EditKeyPhotoInfo { FrameIndex = 25, TimestampSeconds = 0.83, IsOriginal = false };
        var modified = original with { CurrentKeyPhoto = newKeyPhoto };

        // Original document remains completely unchanged
        Assert.Equal(10, original.CurrentKeyPhoto?.FrameIndex);
        Assert.True(original.CurrentKeyPhoto?.IsOriginal);

        // Modified document has the new key photo, but preserves all other facts
        Assert.Equal(25, modified.CurrentKeyPhoto?.FrameIndex);
        Assert.False(modified.CurrentKeyPhoto?.IsOriginal);
        Assert.Equal(10, modified.OriginalKeyPhoto?.FrameIndex);
        Assert.Equal(original.PrimaryPath, modified.PrimaryPath);
        Assert.Equal(original.Protocol, modified.Protocol);
    }

    [Fact]
    public void FromInspectedFacts_PreservesDistinctImageAndVideoDimensions()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.AppleLivePhoto,
            PrimaryImage = new ImageFacts
            {
                IsPresent = true,
                Width = 4032,
                Height = 3024
            },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Width = 1920,
                Height = 1080,
                DurationSeconds = 3.0,
                Fps = 30.0
            }
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Test\IMG_100.JPG",
            motionPath: @"C:\Test\IMG_100.MOV",
            livePhotoType: LivePhotoType.DualFile,
            pairState: EditPairState.Complete,
            facts: facts);

        Assert.Equal(4032u, doc.Width);
        Assert.Equal(3024u, doc.Height);
        Assert.Equal(1920u, doc.VideoWidth);
        Assert.Equal(1080u, doc.VideoHeight);
    }

    [Fact]
    public void PairState_MissingVideo_ReportsIncomplete()
    {
        var doc = EditDocument.FromLivePhoto(
            primaryPath: @"C:\Test\IMG_100.HEIC",
            motionPath: null,
            livePhotoType: LivePhotoType.DualFile,
            protocol: LivePhotoProtocolType.Apple,
            pairState: EditPairState.MissingVideo);

        Assert.Equal(EditPairState.MissingVideo, doc.PairState);
        Assert.False(doc.IsPairComplete);
    }

    [Fact]
    public void FromVideo_InitializesWidthAndVideoWidthIdentically()
    {
        var doc = EditDocument.FromVideo(
            primaryPath: @"C:\Test\sample.mp4",
            width: 3840,
            height: 2160,
            durationSeconds: 15.0,
            frameRate: 60.0);

        Assert.Equal(3840u, doc.Width);
        Assert.Equal(2160u, doc.Height);
        Assert.Equal(3840u, doc.VideoWidth);
        Assert.Equal(2160u, doc.VideoHeight);
        Assert.Equal(EditMediaKind.Video, doc.MediaKind);
        Assert.False(doc.IsLivePhoto);
    }

    [Fact]
    public void FromInspectedFacts_StandaloneVideoWithoutPrimaryImage_SetsMediaKindVideo()
    {
        var facts = new SourceMediaFacts
        {
            Protocol = SourceProtocol.NonLive,
            PrimaryImage = new ImageFacts { IsPresent = false },
            MotionVideo = new VideoFacts
            {
                IsPresent = true,
                Width = 1280,
                Height = 720,
                DurationSeconds = 5.0,
                Fps = 24.0,
                Codec = VideoCodec.H264
            }
        };

        var doc = EditDocumentMapper.FromInspectedFacts(
            primaryPath: @"C:\Test\standalone.mp4",
            motionPath: null,
            livePhotoType: LivePhotoType.None,
            pairState: EditPairState.NotApplicable,
            facts: facts);

        Assert.Equal(EditMediaKind.Video, doc.MediaKind);
        Assert.False(doc.IsLivePhoto);
        Assert.Equal(1280u, doc.Width);
        Assert.Equal(720u, doc.Height);
        Assert.Equal(1280u, doc.VideoWidth);
        Assert.Equal(720u, doc.VideoHeight);
        Assert.Equal(VideoCodec.H264, doc.VideoCodec);
    }

    [Fact]
    public void EditMetadataFacts_Defaults_AreEmpty()
    {
        var meta = new EditMetadataFacts();
        Assert.Equal(string.Empty, meta.CameraModel);
        Assert.Equal(string.Empty, meta.LensModel);
        Assert.Equal(string.Empty, meta.ShootingParams);
        Assert.Equal(string.Empty, meta.PlaceName);
        Assert.Null(meta.DateTaken);
    }
}
