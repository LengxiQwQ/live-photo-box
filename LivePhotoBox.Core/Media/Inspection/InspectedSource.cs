using System;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Inspection;

/// <summary>
/// The Inspector result for an extraction-capable source. Facts are retained
/// for diagnostics and provenance; the opaque plan is the only production
/// extraction authority.
/// </summary>
public sealed class InspectedSource : IDisposable
{
    internal InspectedSource(SourceMediaFacts facts, ExtractionPlan extractionPlan)
    {
        Facts = ExtractionPlan.CloneFacts(facts ?? throw new ArgumentNullException(nameof(facts)));
        ExtractionPlan = extractionPlan ?? throw new ArgumentNullException(nameof(extractionPlan));
    }

    public SourceMediaFacts Facts { get; }

    public ExtractionPlan ExtractionPlan { get; }

    internal static InspectedSource CreateForTests(SourceMediaFacts facts) =>
        new(facts, ExtractionPlan.CreateForTests(facts));

    public void Dispose() => ExtractionPlan.Dispose();
}
