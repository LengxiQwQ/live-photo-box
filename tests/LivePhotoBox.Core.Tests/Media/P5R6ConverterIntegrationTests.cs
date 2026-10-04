using System.Collections.Generic;
using System.Linq;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P5R6ConverterIntegrationTests
{
    private static readonly MergeCell[] MergeCells =
    [
        new(0, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(0, 1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Copy),
        new(1, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(1, 1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Copy),
        new(2, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(2, 1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Copy),
        new(2, 3, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Copy),
        new(3, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(4, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(5, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(5, 2, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Copy),
        new(6, 0, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy),
        new(6, 2, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Copy),
        new(6, 4, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Hevc)
    ];

    private static readonly SplitCell[] SplitCells =
    [
        new(0, 0, ImageContainer.Unknown, VideoContainer.Unknown, VideoCodec.Copy),
        new(0, 1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Hevc),
        new(0, 2, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Hevc),
        new(0, 3, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.H264),
        new(1, 1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Hevc),
        new(1, 2, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Hevc),
        new(2, 3, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.H264)
    ];

    public static IEnumerable<object[]> MergeAvailableRows => MergeCells.Select(static cell =>
        new object[] { cell.ProtocolIndex, cell.FormatIndex, cell.ImageContainer, cell.VideoContainer, cell.VideoCodec });

    public static IEnumerable<object[]> SplitAvailableRows => SplitCells.Select(static cell =>
        new object[] { cell.ProtocolIndex, cell.FormatIndex, cell.ImageContainer, cell.VideoContainer, cell.VideoCodec });

    public static IEnumerable<object[]> MergeUnavailableRows => GetUnavailableRows(ProtocolFormatMatrix.Matrix);

    public static IEnumerable<object[]> SplitUnavailableRows => GetUnavailableRows(ProtocolFormatMatrix.SplitMatrix);

    [Theory]
    [MemberData(nameof(MergeAvailableRows))]
    public void P5R6_MergeAvailableCell_MapsToFrozenRequirement(
        int protocolIndex,
        int formatIndex,
        ImageContainer expectedImageContainer,
        VideoContainer expectedVideoContainer,
        VideoCodec expectedVideoCodec)
    {
        Assert.True(ProtocolFormatMatrix.IsAvailable(protocolIndex, formatIndex));

        MediaFormatRequirement requirement = ProtocolMediaRequirements.GetMergeRequirement(protocolIndex, formatIndex);

        Assert.Equal(expectedImageContainer, requirement.ImageContainer);
        Assert.Equal(expectedVideoContainer, requirement.VideoContainer);
        Assert.Equal(expectedVideoCodec, requirement.VideoCodec);
        Assert.Null(requirement.TargetFps);
    }

    [Theory]
    [MemberData(nameof(SplitAvailableRows))]
    public void P5R6_SplitAvailableCell_MapsToFrozenRequirement(
        int protocolIndex,
        int formatIndex,
        ImageContainer expectedImageContainer,
        VideoContainer expectedVideoContainer,
        VideoCodec expectedVideoCodec)
    {
        Assert.True(ProtocolFormatMatrix.IsSplitAvailable(protocolIndex, formatIndex));

        MediaFormatRequirement requirement = ProtocolMediaRequirements.GetSplitRequirement(protocolIndex, formatIndex);

        Assert.Equal(expectedImageContainer, requirement.ImageContainer);
        Assert.Equal(expectedVideoContainer, requirement.VideoContainer);
        Assert.Equal(expectedVideoCodec, requirement.VideoCodec);
        Assert.Null(requirement.TargetFps);
        if (formatIndex == ProtocolFormatMatrix.SplitFormatKeep)
        {
            Assert.True(requirement.KeepSourceIfSame);
        }
    }

    [Fact]
    public void P5R6_MergeAvailableSet_MatchesFrozenFourteenCells()
    {
        Assert.Equal(7, ProtocolFormatMatrix.Matrix.Length);
        Assert.Equal(new[] { 5, 5, 5, 5, 5, 5, 5 }, ProtocolFormatMatrix.Matrix.Select(static row => row.Length));

        (int ProtocolIndex, int FormatIndex)[] expected = MergeCells
            .Select(static cell => (cell.ProtocolIndex, cell.FormatIndex))
            .OrderBy(static cell => cell.ProtocolIndex)
            .ThenBy(static cell => cell.FormatIndex)
            .ToArray();
        (int ProtocolIndex, int FormatIndex)[] actual = GetAvailableCells(ProtocolFormatMatrix.Matrix);

        Assert.Equal(14, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void P5R6_SplitAvailableSet_MatchesFrozenSevenCells()
    {
        Assert.Equal(3, ProtocolFormatMatrix.SplitMatrix.Length);
        Assert.Equal(new[] { 4, 4, 4 }, ProtocolFormatMatrix.SplitMatrix.Select(static row => row.Length));

        (int ProtocolIndex, int FormatIndex)[] expected = SplitCells
            .Select(static cell => (cell.ProtocolIndex, cell.FormatIndex))
            .OrderBy(static cell => cell.ProtocolIndex)
            .ThenBy(static cell => cell.FormatIndex)
            .ToArray();
        (int ProtocolIndex, int FormatIndex)[] actual = GetAvailableCells(ProtocolFormatMatrix.SplitMatrix);

        Assert.Equal(7, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(MergeUnavailableRows))]
    public void P5R6_MergeUnavailableInRangeCell_Rejects(int protocolIndex, int formatIndex)
    {
        Assert.False(ProtocolFormatMatrix.IsAvailable(protocolIndex, formatIndex));
        Assert.Throws<System.ArgumentException>(() =>
            ProtocolMediaRequirements.GetMergeRequirement(protocolIndex, formatIndex));
    }

    [Theory]
    [MemberData(nameof(SplitUnavailableRows))]
    public void P5R6_SplitUnavailableInRangeCell_Rejects(int protocolIndex, int formatIndex)
    {
        Assert.False(ProtocolFormatMatrix.IsSplitAvailable(protocolIndex, formatIndex));
        Assert.Throws<System.ArgumentException>(() =>
            ProtocolMediaRequirements.GetSplitRequirement(protocolIndex, formatIndex));
    }

    [Fact]
    public void P5R6_MergeRepresentativeOutOfRangeIndices_Reject()
    {
        Assert.Throws<System.ArgumentException>(() => ProtocolMediaRequirements.GetMergeRequirement(-1, 0));
        Assert.Throws<System.ArgumentException>(() => ProtocolMediaRequirements.GetMergeRequirement(0, -1));
        Assert.Throws<System.ArgumentException>(() =>
            ProtocolMediaRequirements.GetMergeRequirement(ProtocolFormatMatrix.Matrix.Length, 0));
        Assert.Throws<System.ArgumentException>(() =>
            ProtocolMediaRequirements.GetMergeRequirement(0, ProtocolFormatMatrix.Matrix[0].Length));
    }

    [Fact]
    public void P5R6_SplitRepresentativeOutOfRangeIndices_Reject()
    {
        Assert.Throws<System.ArgumentException>(() => ProtocolMediaRequirements.GetSplitRequirement(-1, 0));
        Assert.Throws<System.ArgumentException>(() => ProtocolMediaRequirements.GetSplitRequirement(0, -1));
        Assert.Throws<System.ArgumentException>(() =>
            ProtocolMediaRequirements.GetSplitRequirement(ProtocolFormatMatrix.SplitMatrix.Length, 0));
        Assert.Throws<System.ArgumentException>(() =>
            ProtocolMediaRequirements.GetSplitRequirement(0, ProtocolFormatMatrix.SplitMatrix[0].Length));
    }

    [Fact]
    public void P5R6_MergeEqualTypedRequirements_AreProtocolIndexIndependent()
    {
        foreach (IGrouping<(ImageContainer, VideoContainer, VideoCodec), MergeCell> group in MergeCells
                     .GroupBy(static cell => (cell.ImageContainer, cell.VideoContainer, cell.VideoCodec)))
        {
            RequirementShape[] actualShapes = group
                .Select(static cell => ToShape(ProtocolMediaRequirements.GetMergeRequirement(cell.ProtocolIndex, cell.FormatIndex)))
                .Distinct()
                .ToArray();

            Assert.Single(actualShapes);
        }
    }

    [Fact]
    public void P5R6_SplitEqualTypedRequirements_AreProtocolIndexIndependent()
    {
        foreach (IGrouping<(ImageContainer, VideoContainer, VideoCodec), SplitCell> group in SplitCells
                     .GroupBy(static cell => (cell.ImageContainer, cell.VideoContainer, cell.VideoCodec)))
        {
            RequirementShape[] actualShapes = group
                .Select(static cell => ToShape(ProtocolMediaRequirements.GetSplitRequirement(cell.ProtocolIndex, cell.FormatIndex)))
                .Distinct()
                .ToArray();

            Assert.Single(actualShapes);
        }
    }

    private static IEnumerable<object[]> GetUnavailableRows(bool[][] matrix)
    {
        for (int protocolIndex = 0; protocolIndex < matrix.Length; protocolIndex++)
        {
            for (int formatIndex = 0; formatIndex < matrix[protocolIndex].Length; formatIndex++)
            {
                if (!matrix[protocolIndex][formatIndex])
                {
                    yield return new object[] { protocolIndex, formatIndex };
                }
            }
        }
    }

    private static (int ProtocolIndex, int FormatIndex)[] GetAvailableCells(bool[][] matrix)
    {
        var cells = new List<(int ProtocolIndex, int FormatIndex)>();
        for (int protocolIndex = 0; protocolIndex < matrix.Length; protocolIndex++)
        {
            for (int formatIndex = 0; formatIndex < matrix[protocolIndex].Length; formatIndex++)
            {
                if (matrix[protocolIndex][formatIndex])
                {
                    cells.Add((protocolIndex, formatIndex));
                }
            }
        }

        return cells.ToArray();
    }

    private static RequirementShape ToShape(MediaFormatRequirement requirement) =>
        new(requirement.ImageContainer, requirement.VideoContainer, requirement.VideoCodec, requirement.TargetFps);

    private readonly record struct RequirementShape(
        ImageContainer ImageContainer,
        VideoContainer VideoContainer,
        VideoCodec VideoCodec,
        int? TargetFps);

    private sealed record MergeCell(
        int ProtocolIndex,
        int FormatIndex,
        ImageContainer ImageContainer,
        VideoContainer VideoContainer,
        VideoCodec VideoCodec);

    private sealed record SplitCell(
        int ProtocolIndex,
        int FormatIndex,
        ImageContainer ImageContainer,
        VideoContainer VideoContainer,
        VideoCodec VideoCodec);
}
