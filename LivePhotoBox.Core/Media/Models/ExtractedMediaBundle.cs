namespace LivePhotoBox.Media.Models;

using System.Collections.Generic;
using LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Bundle of extracted media artifacts in an isolated transaction workspace.
/// </summary>
public sealed record ExtractedMediaBundle
{
    public required MediaArtifact PrimaryImage { get; init; }
    public MediaArtifact? MotionVideo { get; init; }
    public MediaArtifact? GainMap { get; init; }
    public required SourceMediaFacts SourceFacts { get; init; }
    public IReadOnlyList<RemovedProtocolFact> ExtractedProtocolFacts { get; init; } = [];
}
