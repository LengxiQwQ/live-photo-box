using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Inspection;

/// <summary>
/// Thin control plane wrapper that delegates source media inspection to LivePhotoBox.Native.
/// </summary>
public sealed class SourceInspector : ISourceInspector
{
    public Task<SourceMediaFacts> InspectAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default)
    {
        return NativeMediaService.InspectMediaAsync(primaryPath, secondaryPath, cancellationToken);
    }

    public Task<InspectedSource> InspectWithPlanAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default)
    {
        return NativeMediaService.InspectMediaWithPlanAsync(primaryPath, secondaryPath, cancellationToken);
    }
}
