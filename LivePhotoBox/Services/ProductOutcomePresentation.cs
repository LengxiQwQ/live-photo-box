using System;
using System.Collections.Generic;
using System.Linq;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

// Presentation only. Historical execution diagnostics never authorize a writer.
internal static class ProductOutcomePresentation
{
    internal static string Status(ProductOperationOutcome outcome)
    {
        string status = ResourceService.GetString("ProductOutcome_" + outcome.Status);
        if (outcome.IsSuccess && outcome.PreparationExecution?.Videos.Any(video =>
                video.Truth.FallbackOccurred &&
                video.Truth.FailureCategory == ConversionFailureCategory.None &&
                video.SelectedEncoder == "minimal-libav/libx264") == true)
            status += " · " + ResourceService.GetString("ProductOutcome_BuiltinEngine");
        return status;
    }

    internal static string Describe(ProductOperationOutcome outcome, string capabilityDiagnostic)
    {
        var lines = new List<string> { Status(outcome), outcome.Diagnostic.Message };
        var selection = outcome.RequestedSelection;
        string requested = string.Join(" / ", new[] { selection.TargetProtocol?.ToString(), selection.OutputFormat?.ToString(), selection.SplitFormat?.ToString() }.Where(value => value is not null));
        lines.Add($"{ResourceService.GetString("ProductOutcome_Selection")}: {requested}");
        lines.Add($"{ResourceService.GetString("ProductOutcome_PreparationScope")}");
        if (outcome.PreparationExecution?.Image is { } image)
            lines.Add(Truth(image.Truth));
        foreach (var video in outcome.PreparationExecution?.Videos ?? Array.Empty<VideoExecutionRecord>())
        {
            lines.Add($"{Truth(video.Truth)}; encoder={video.SelectedEncoder}");
            if (video.Truth.FallbackOccurred)
                lines.Add($"{ResourceService.GetString(outcome.IsSuccess && video.Truth.FailureCategory == ConversionFailureCategory.None
                    && video.SelectedEncoder == "minimal-libav/libx264"
                    ? "ProductOutcome_FallbackCompleted" : "ProductOutcome_FallbackSelected")}: {video.Truth.FallbackReason}");
        }
        if (outcome.PreparationExecution?.Image is null && (outcome.PreparationExecution?.Videos.Count ?? 0) == 0)
            lines.Add(ResourceService.GetString("ProductOutcome_BackendUnknown"));
        lines.Add(ResourceService.GetString("ProductOutcome_TargetPreservationUnknown"));
        lines.Add($"{ResourceService.GetString("ProductOutcome_Diagnostic")}: {outcome.Diagnostic.Stage}/{outcome.Diagnostic.Category}");
        lines.AddRange(outcome.CommittedOutputs.Select(output => output.Path));
        if (!string.IsNullOrWhiteSpace(outcome.HistoryWarning)) lines.Add(outcome.HistoryWarning);
        if (!string.IsNullOrWhiteSpace(capabilityDiagnostic)) lines.Add(capabilityDiagnostic);
        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string Truth(ConversionExecutionTruth truth) =>
        $"{truth.BackendName} {truth.BackendVersion}: {truth.ActualOperationKind}; " +
        $"pixel={truth.Pixel}, metadata={truth.Metadata}, color={truth.ColorIcc}, HDR={truth.HdrGainMap}, " +
        $"audio={truth.Audio}, timing={truth.Timing}, orientation={truth.Orientation}";

    internal static string CapabilityDiagnostic(Func<ProductCapabilityAssessment> query)
    {
        try
        {
            var assessment = query();
            return $"{ResourceService.GetString("ProductOutcome_Capability")}: {assessment.Availability}. {assessment.Diagnostic}";
        }
        catch (Exception ex)
        {
            // Capability observations do not replace per-request Native validation.
            return $"{ResourceService.GetString("ProductOutcome_Capability")}: Unknown. {ex.Message}";
        }
    }
}
