using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Inspection;

public interface ISourceInspector
{
    Task<SourceMediaFacts> InspectAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default);
}
