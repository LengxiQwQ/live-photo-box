using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Inspection;

// Discovery reports refused inputs as data. It must never consume out_facts
// from a failed Native inspection or turn them into execution authority.
internal sealed record SourceInspectionFailure(
    SourceInspectionFailureCategory Category, SourceInspectionStage Stage, ulong Capability, string Reason)
{
    internal SourceInspectionException ToException() => new(Category, Stage, Capability, Reason);
}

internal sealed record SourceInspectionResult
{
    private SourceInspectionResult(SourceMediaFacts? facts, SourceInspectionFailure? failure)
    {
        Facts = facts;
        Failure = failure;
    }

    internal SourceMediaFacts? Facts { get; }
    internal SourceInspectionFailure? Failure { get; }
    internal static SourceInspectionResult Accepted(SourceMediaFacts facts) => new(facts, null);
    internal static SourceInspectionResult Rejected(SourceInspectionFailure failure) => new(null, failure);
}
