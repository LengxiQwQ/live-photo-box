namespace LivePhotoBox.Models;

/// <summary>
/// Persisted processing session/configuration metadata.
/// Rebuilt is the sole native pipeline; no legacy mode choices exist.
/// </summary>
public sealed class ProcessingBackendSettings
{
    public const int CurrentSchemaVersion = 3;

    public long Revision { get; internal set; }

    public ProcessingBackendSettings Clone() => new()
    {
        Revision = Revision
    };
}
