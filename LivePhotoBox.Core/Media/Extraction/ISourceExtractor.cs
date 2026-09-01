using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Media.Extraction;

/// <summary>
/// Extracts primary image, motion video, and auxiliary media into an isolated workspace based on inspected facts.
/// </summary>
public interface ISourceExtractor
{
    Task<ExtractedMediaBundle> ExtractAsync(
        SourceMediaFacts facts,
        IMediaWorkspace workspace,
        CancellationToken ct = default);
}
