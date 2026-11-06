using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Services;

// Assess before publication. Unknown vendor-private preservation is outside the
// A/B contract and alone is not a reason to quarantine an otherwise valid export.
internal static class ProductExportAssessment
{
    internal static IReadOnlyList<string> Warnings(NeutralMediaBundle bundle,
        IEnumerable<NotCarriedItem>? notes = null, bool usesCoverBinding = true)
    {
        var warnings = new List<string>();
        if (bundle.PreparationExecution?.OrientationResolution is not null)
            warnings.Add(ResourceService.GetString("ProductWarning_StoredDirection"));
        else if (bundle.Semantics.Orientation.PrimaryImage.RotationEvidence == NeutralEvidenceState.Unknown)
            warnings.Add(ResourceService.GetString("ProductWarning_UnknownDirection"));
        if (usesCoverBinding && bundle.Semantics.Timing.EvidenceState == NeutralEvidenceState.Unknown)
            warnings.Add(ResourceService.GetString("ProductWarning_UnknownCover"));
        if (bundle.PreparationExecution?.Image?.Truth.HdrGainMap is ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost ||
            bundle.PreparationExecution?.Videos?.Any(video => video.Truth.HdrGainMap is ConversionComponentOutcome.Degraded or ConversionComponentOutcome.Lost) == true)
            warnings.Add(ResourceService.GetString("ProductWarning_Hdr"));
        warnings.AddRange((notes ?? []).Where(n => n.Item == "CoverTimestamp").Select(n => n.Reason));
        return warnings.Distinct().ToArray();
    }

    internal static string IsolationDirectory(string requestedDirectory, Guid operationId)
    {
        string folderName = ResourceService.GetString("OutputDir_Quarantined");
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "Quarantine";
        return Path.Combine(Path.GetFullPath(requestedDirectory), folderName);
    }

    internal static string Details(IReadOnlyList<string> warnings, string directory) =>
        ResourceService.GetString("ProductOutcome_Warning") + Environment.NewLine +
        string.Join(Environment.NewLine, warnings) + Environment.NewLine +
        ResourceService.GetString("ProductWarning_Isolation") + ": " + directory;
}
