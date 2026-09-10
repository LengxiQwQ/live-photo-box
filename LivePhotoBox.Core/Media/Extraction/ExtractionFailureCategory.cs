namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Machine-distinguishable failure categories for source extraction.
/// </summary>
public enum ExtractionFailureCategory
{
    AuthorityViolation,
    PlanReplay,
    InvalidFacts,
    SourceRangeUnreadable,
    SourceChanged,
    Cancelled,
    DiskFull,
    OutputWriteFailed,
    OutputPublishFailed,
    UnsupportedLayout,
    InvalidAlias,
    CleanupFailed,
    InternalError
}
