using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Media.Extraction;

public interface ISourceExtractor
{
    Task<ExtractedMediaBundle> ExtractAsync(
        ExtractionPlan plan,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default);
}
