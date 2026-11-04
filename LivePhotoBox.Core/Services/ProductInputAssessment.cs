using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;

namespace LivePhotoBox.Services;

internal static class ProductInputAssessment
{
    // Expected external refusals are results at product entrypoints. The later
    // strict extraction-plan API still validates the actual leased file objects.
    internal static async Task<SourceInspectionFailure?> RejectionAsync(string primary, string? secondary, CancellationToken token)
    {
        var inspection = await new SourceInspector().InspectForDiscoveryAsync(primary, secondary, token).ConfigureAwait(false);
        if (inspection.Failure is not null) return inspection.Failure;
        if (secondary is null && inspection.Facts?.MotionVideo is null)
            return new(SourceInspectionFailureCategory.Unsupported, SourceInspectionStage.Protocol, 0,
                "No motion video was identified. Supply an explicit companion video or select a supported live photo.");
        return null;
    }
}
