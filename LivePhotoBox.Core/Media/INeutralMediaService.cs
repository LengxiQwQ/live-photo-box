using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media;

/// <summary>
/// Orchestration service that executes the full Rebuilt reference pipeline:
/// Inspect -> Extract -> Clean -> Convert -> NeutralMediaBundle.
/// </summary>
public interface INeutralMediaService
{
    Task<NeutralMediaBundle> CreateNeutralBundleAsync(
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        MediaFormatRequirement? requirement = null,
        PreservationPolicy preservationPolicy = PreservationPolicy.BestEffort,
        CancellationToken cancellationToken = default);
}
