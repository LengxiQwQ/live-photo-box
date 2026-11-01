using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using LivePhotoBox.Services;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Models;

internal enum ProductOperationKind { Split, Merge }
internal enum ProductOperationStatus { Success, Unsupported, Failed, Cancelled }
internal enum ProductTargetPreservation { Unknown }
internal enum ProductOperationStage
{
    Unknown, Selection, Discovery, Inspection, Extraction, Cleaning, ImageConversion,
    VideoConversion, Writer, Publication, Cleanup
}
internal enum ProductFailureCategory { Unknown, Unsupported, Io, Cancelled, Inspection, Conversion, Cleanup }

internal sealed record ProductRequestedSelection
{
    public TargetProtocol? TargetProtocol { get; init; }
    public OutputMediaFormat? OutputFormat { get; init; }
    public PreservationPolicy? PreservationPolicy { get; init; }
    public uint? TargetVersion { get; init; }
    public SplitMediaFormat? SplitFormat { get; init; }
    public UnspecifiedImageOrientationHandling UnspecifiedImageOrientationHandling { get; init; }
}

internal sealed record ProductCommittedArtifact
{
    public string Role { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    // Only identities observed by the owning operation may be included.
    public string? Sha256 { get; init; }
    public long? ByteLength { get; init; }
}

internal sealed record ProductOperationDiagnostic
{
    public ProductOperationStage Stage { get; init; }
    public ProductFailureCategory Category { get; init; }
    public string Message { get; init; } = string.Empty;
    public ConversionFailureStage? ConversionStage { get; init; }
    public ConversionFailureCategory? ConversionCategory { get; init; }
    public SourceInspectionStage? InspectionStage { get; init; }
    public SourceInspectionFailureCategory? InspectionCategory { get; init; }
    public CleanerFailureStage? CleanerStage { get; init; }
    public CleanerFailureCategory? CleanerCategory { get; init; }
}

/// <summary>Executed converter diagnostics; never Native authority or target correctness.</summary>
internal sealed record NeutralPreparationExecution
{
    public ImageExecutionRecord? Image { get; init; }
    public ImageOrientationResolutionEvidence? OrientationResolution { get; init; }
    public IReadOnlyList<VideoExecutionRecord> Videos { get; init; } = [];
}

internal sealed record NotCarriedItem
{
    public required string Item { get; init; }
    public required string Reason { get; init; }
    public required PreservationOutcome Outcome { get; init; }
}

internal sealed record ProductOperationOutcome
{
    public int SchemaVersion { get; init; } = 1;
    public Guid OperationId { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public ProductOperationKind Operation { get; init; }
    public ProductOperationStatus Status { get; init; }
    public ProductRequestedSelection RequestedSelection { get; init; } = new();
    public IReadOnlyList<string> Inputs { get; init; } = [];
    public string? RequestedDestination { get; init; }
    public IReadOnlyList<ProductCommittedArtifact> CommittedOutputs { get; init; } = [];
    public string PreparationEvidenceScope { get; init; } = "PreparedNeutral";
    public NeutralMediaSemantics? PreparationSemantics { get; init; }
    public IReadOnlyList<NeutralArtifactManifest> PreparationManifest { get; init; } = [];
    public NeutralPreparationExecution? PreparationExecution { get; init; }
    public ProductTargetPreservation FinalTargetPreservation { get; init; } = ProductTargetPreservation.Unknown;
    public IReadOnlyList<NotCarriedItem> NotCarried { get; init; } = [];
    public ProductOperationDiagnostic Diagnostic { get; init; } = new();
    [JsonIgnore] public string? HistoryWarning { get; init; }
    [JsonIgnore] public bool IsSuccess => Status == ProductOperationStatus.Success;
    [JsonIgnore] public int ExitCode => Status == ProductOperationStatus.Cancelled ? 130 : IsSuccess ? 0 : 1;
    [JsonIgnore] public string Details => Diagnostic.Message;
}

/// <summary>Single terminal persistence boundary used by product runners, never callers.</summary>
internal static class ProductOutcomeCompletion
{
    internal static ProductOperationOutcome Record(ProductOperationOutcome outcome)
    {
        try
        {
            if (LogService.RecordProductOutcome(outcome)) return outcome;
        }
        catch
        {
            // History storage cannot reverse a committed media operation.
        }
        return outcome with { HistoryWarning = "Operation history could not be saved." };
    }

    internal static ProductOperationDiagnostic Failure(Exception exception, ProductOperationStage stage,
        NeutralPreparationExecution? execution = null)
    {
        if (NeutralPreparationDiagnostics.TryRead(exception, out var preparation))
        {
            stage = preparation.Stage;
            execution = preparation.Execution;
        }
        ConversionExecutionTruth? truth = stage == ProductOperationStage.ImageConversion
            ? execution?.Image?.Truth : stage == ProductOperationStage.VideoConversion ? execution?.Videos.LastOrDefault()?.Truth : null;
        var inspection = exception as SourceInspectionException;
        var cleaner = exception as CleanerException;
        return new()
        {
            Stage = inspection is not null ? ProductOperationStage.Inspection : cleaner is not null ? ProductOperationStage.Cleaning : stage,
            Category = exception is OperationCanceledException ? ProductFailureCategory.Cancelled :
                truth?.FailureCategory == ConversionFailureCategory.Unsupported || exception is NotSupportedException ||
                inspection?.Category == SourceInspectionFailureCategory.Unsupported || cleaner?.Category == CleanerFailureCategory.UnsupportedProtocol ||
                (stage == ProductOperationStage.Selection && exception is ArgumentException)
                    ? ProductFailureCategory.Unsupported : cleaner is not null ? ProductFailureCategory.Cleanup :
                inspection is not null || (stage == ProductOperationStage.Inspection && exception is InvalidDataException)
                    ? ProductFailureCategory.Inspection : exception is IOException ? ProductFailureCategory.Io :
                truth is not null ? ProductFailureCategory.Conversion : ProductFailureCategory.Unknown,
            Message = exception.Message,
            ConversionStage = truth?.FailureStage,
            ConversionCategory = truth?.FailureCategory,
            InspectionStage = inspection?.Stage, InspectionCategory = inspection?.Category,
            CleanerStage = cleaner?.Stage, CleanerCategory = cleaner?.Category
        };
    }

    internal static ProductOperationStatus Status(Exception exception, ProductOperationDiagnostic diagnostic) =>
        exception is OperationCanceledException ? ProductOperationStatus.Cancelled :
        diagnostic.Category == ProductFailureCategory.Unsupported ? ProductOperationStatus.Unsupported : ProductOperationStatus.Failed;
}

internal sealed record NeutralPreparationDiagnostic(ProductOperationStage Stage, NeutralPreparationExecution Execution);

/// <summary>Attach observations without replacing established exception types, especially OCE.</summary>
internal static class NeutralPreparationDiagnostics
{
    private const string Key = "LivePhotoBox.PreparedNeutral.Execution.v1";
    internal static void Attach(Exception exception, ProductOperationStage stage, NeutralPreparationExecution execution)
    {
        try { exception.Data[Key] = new NeutralPreparationDiagnostic(stage, execution); }
        catch { /* Retain the original failure if its diagnostic bag is unavailable. */ }
    }
    internal static bool TryRead(Exception exception, out NeutralPreparationDiagnostic diagnostic)
    {
        diagnostic = exception.Data[Key] as NeutralPreparationDiagnostic ?? null!;
        return diagnostic is not null;
    }
}
