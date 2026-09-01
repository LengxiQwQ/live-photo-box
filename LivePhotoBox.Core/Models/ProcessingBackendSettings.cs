namespace LivePhotoBox.Models;

/// <summary>
/// Chooses the product-wide processing branch. Rebuilt is intentionally the
/// default: it is an empty boundary until the neutral-media pipeline exists.
/// </summary>
public enum ProcessingPipelineMode
{
    Legacy,
    Rebuilt
}

/// <summary>Persisted product-wide processing-branch setting.</summary>
public sealed class ProcessingBackendSettings
{
    public const int CurrentSchemaVersion = 3;

    public long Revision { get; internal set; }

    public ProcessingPipelineMode Mode { get; set; } = ProcessingPipelineMode.Rebuilt;

    public ProcessingBackendSettings Clone() => new()
    {
        Revision = Revision,
        Mode = Mode
    };
}
