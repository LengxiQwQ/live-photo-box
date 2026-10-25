using System.IO;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8InspectionOutcomeClassificationTests
{
    [Theory]
    [InlineData("Inspection", "Inspection")]
    [InlineData("Writer", "Unknown")]
    public void InvalidDataFailure_UsesKnownInspectionBoundaryWithoutInventingWriterCategory(
        string stageName, string categoryName)
    {
        var stage = System.Enum.Parse<ProductOperationStage>(stageName);
        var category = System.Enum.Parse<ProductFailureCategory>(categoryName);
        var failure = new InvalidDataException("Rebuilt split requires a Native-inspected motion video.");
        var diagnostic = ProductOutcomeCompletion.Failure(failure, stage);
        Assert.Equal(stage, diagnostic.Stage);
        Assert.Equal(category, diagnostic.Category);
        Assert.Equal(ProductOperationStatus.Failed, ProductOutcomeCompletion.Status(failure, diagnostic));
        Assert.Null(diagnostic.InspectionCategory);
        Assert.Null(diagnostic.InspectionStage);
        Assert.Equal(failure.Message, diagnostic.Message);
    }
}
