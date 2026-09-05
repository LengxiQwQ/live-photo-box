namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Machine-distinguishable failure categories for source extraction.
/// </summary>
public enum ExtractionFailureCategory
{
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
