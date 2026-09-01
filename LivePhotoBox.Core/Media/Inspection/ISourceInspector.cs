using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Inspection;

/// <summary>
/// Read-only inspector that analyzes source media files and extracts structured facts and byte ranges.
/// </summary>
public interface ISourceInspector
{
    Task<SourceMediaFacts> InspectAsync(string filePath, string? secondaryFilePath = null, CancellationToken ct = default);
}
