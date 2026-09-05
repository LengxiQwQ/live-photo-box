using System;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Specialized exception for source extraction errors, providing machine-distinguishable
/// failure category, affected artifact, source range, and technical diagnostics.
/// </summary>
public sealed class ExtractionException : InvalidOperationException
{
    public ExtractionFailureCategory Category { get; }
    public MediaArtifactKind? ArtifactKind { get; }
    public string? SourcePath { get; }
    public long? Offset { get; }
    public long? Length { get; }
    public string? TechnicalReason { get; }

    public ExtractionException(
        ExtractionFailureCategory category,
        string message,
        MediaArtifactKind? artifactKind = null,
        string? sourcePath = null,
        long? offset = null,
        long? length = null,
        string? technicalReason = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        ArtifactKind = artifactKind;
        SourcePath = sourcePath;
        Offset = offset;
        Length = length;
        TechnicalReason = technicalReason;
    }
}
