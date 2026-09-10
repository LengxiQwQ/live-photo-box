using System;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Media.Inspection;

public interface ISourceInspector
{
    Task<SourceMediaFacts> InspectAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects a source and issues the opaque authority used by production
    /// extraction. The default keeps diagnostic-only Inspector fakes source
    /// compatible; the Neutral extraction pipeline requires the plan method.
    /// </summary>
    Task<InspectedSource> InspectWithPlanAsync(
        string primaryPath,
        string? secondaryPath = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<InspectedSource>(new NotSupportedException(
            "This Inspector does not issue extraction authority plans."));
}
