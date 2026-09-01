using System.Collections.Generic;

namespace LivePhotoBox.Media.Models;

/// <summary>
/// Bundle of extracted artifacts produced by SourceExtractor in a transaction workspace.
/// </summary>
public sealed record ExtractedMediaBundle
{
    public required MediaArtifact PrimaryImage { get; init; }
    public MediaArtifact? MotionVideo { get; init; }
    public MediaArtifact? GainMap { get; init; }
    public IReadOnlyList<MediaArtifact> AuxiliaryArtifacts { get; init; } = [];
    public required SourceMediaFacts Facts { get; init; }
}
