using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Media.Extraction;

public interface ISourceExtractor
{
    Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        string primaryPath,
        string? secondaryPath,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default);
}
