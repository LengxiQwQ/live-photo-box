namespace LivePhotoBox.Media.Models;

using System.Collections.Generic;
using LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Bundle of extracted media artifacts in an isolated transaction workspace.
/// </summary>
public sealed record ExtractedMediaBundle
{
    public required MediaArtifact PrimaryImage { get; init; }
    /// <summary>
    /// Optional full, immutable source-container snapshot required by
    /// container rebuild cleaners whose protocol data lives outside the
    /// exact primary-media range (for example Samsung JPEG SEF).
    /// </summary>
    public MediaArtifact? CleanupSource { get; init; }
    public MediaArtifact? MotionVideo { get; init; }
    public MediaArtifact? GainMap { get; init; }
    public required SourceMediaFacts SourceFacts { get; init; }
    public IReadOnlyList<RemovedProtocolFact> ExtractedProtocolFacts { get; init; } = [];
}
