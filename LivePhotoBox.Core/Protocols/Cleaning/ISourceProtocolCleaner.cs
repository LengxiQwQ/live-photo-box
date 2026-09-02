using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Service interface for stripping source vendor-specific Live/Motion Photo metadata
/// and container markers from extracted media artifacts.
/// </summary>
public interface ISourceProtocolCleaner
{
    Task<ProtocolCleanResult> CleanAsync(
        ProtocolCleanRequest request,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default);
}
