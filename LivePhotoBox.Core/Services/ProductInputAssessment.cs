using LivePhotoBox.Models;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;

namespace LivePhotoBox.Services;

internal static class ProductInputAssessment
{
    // Expected external refusals are results at product entrypoints. The later
    // strict extraction-plan API still validates the actual leased file objects.
    internal static async Task<SourceInspectionFailure?> RejectionAsync(string primary, string? secondary, CancellationToken token, SelectionIntent intent = SelectionIntent.SourceRelationship)
    {
        // Component inspection/extraction follows immediately under the execution
        // leases. Do not prepay a second full inspection or demand a factory pair.
        if (intent == SelectionIntent.UserComposition && secondary != null) return null;
        var inspection = await new SourceInspector().InspectForDiscoveryAsync(primary, secondary, token).ConfigureAwait(false);
        if (inspection.Failure is not null) return inspection.Failure;
        if (intent == SelectionIntent.AppleIdentity && inspection.Facts?.Protocol != LivePhotoBox.Media.Models.SourceProtocol.AppleLivePhoto ||
            intent == SelectionIntent.VivoIdentity && inspection.Facts?.Protocol != LivePhotoBox.Media.Models.SourceProtocol.VivoLegacyDualFile)
            return new(SourceInspectionFailureCategory.Ambiguous,SourceInspectionStage.Pairing,0,"The actual source pair no longer matches the selected identity protocol.");
        if (secondary is null && inspection.Facts?.MotionVideo is null)
            return new(SourceInspectionFailureCategory.Unsupported, SourceInspectionStage.Protocol, 0,
                "No motion video was identified. Supply an explicit companion video or select a supported live photo.");
        return null;
    }
}
