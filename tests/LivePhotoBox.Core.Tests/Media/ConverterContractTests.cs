using System;
using System.Reflection;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ConverterContractTests
{
    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(EntryPointNotFoundException))]
    [InlineData(typeof(BadImageFormatException))]
    public void NativeLoadFailures_AreRecognizedAsBackendUnavailable(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.True(IsBackendUnavailable(typeof(ImageConverter), exception));
        Assert.True(IsBackendUnavailable(typeof(VideoConverter), exception));
    }

    [Fact]
    public void ExecutionTruth_DefaultsToNoFallbackAndNoFailure()
    {
        var truth = new ConversionExecutionTruth();
        Assert.False(truth.FallbackAllowed);
        Assert.False(truth.FallbackOccurred);
        Assert.Equal(ConversionFailureStage.None, truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.None, truth.FailureCategory);
    }

    [Fact]
    public void PublicConversionModels_DoNotExposeInteropOrProviderSpecificTypes()
    {
        Type[] publicModels = [typeof(ImageConversionRequest), typeof(ImageExecutionRecord), typeof(VideoConversionRequest), typeof(VideoExecutionRecord), typeof(ConversionExecutionTruth)];
        foreach (Type model in publicModels)
        {
            foreach (var property in model.GetProperties())
            {
                Assert.DoesNotContain("Interop", property.PropertyType.Namespace ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("libheif", property.PropertyType.FullName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("MediaFoundation", property.PropertyType.FullName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void GainMapBinding_CannotBeConstructedOrRewrittenByExternalCallers()
    {
        Type binding = typeof(ImageHdrGainMapSourceBinding);

        Assert.Empty(binding.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(binding.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
            Assert.False(property.SetMethod?.IsPublic == true, $"{property.Name} must be read-only."));
    }

    [Fact]
    public void ImageTransformRequest_DefaultsToBackendNeutralNone()
    {
        var request = new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = "source.jpg", Kind = MediaArtifactKind.PrimaryImage },
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = "output"
        };

        Assert.Equal(ImageTransformKind.None, request.Transform);
        Assert.Equal(
            [
                ImageTransformKind.None,
                ImageTransformKind.Rotate90,
                ImageTransformKind.Rotate180,
                ImageTransformKind.Rotate270,
                ImageTransformKind.FlipHorizontal,
                ImageTransformKind.FlipVertical,
                ImageTransformKind.Transpose,
                ImageTransformKind.Transverse
            ],
            Enum.GetValues<ImageTransformKind>());
    }

    [Fact]
    public void PreservationAggregateCannotBePreservedWhenRequiredComponentsAreNotEvaluated()
    {
        var truth = new ConversionExecutionTruth
        {
            Metadata = ConversionComponentOutcome.NotEvaluated,
            ColorIcc = ConversionComponentOutcome.NotEvaluated,
            Audio = ConversionComponentOutcome.NotEvaluated,
            Timing = ConversionComponentOutcome.NotEvaluated,
            Orientation = ConversionComponentOutcome.NotEvaluated,
            PreservationOutcome = PreservationOutcome.PartiallyPreserved
        };

        Assert.NotEqual(PreservationOutcome.Preserved, truth.PreservationOutcome);
    }

    private static bool IsBackendUnavailable(Type converterType, Exception exception)
    {
        MethodInfo classifier = converterType.GetMethod(
            "IsBackendUnavailable",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return Assert.IsType<bool>(classifier.Invoke(null, [exception]));
    }
}
