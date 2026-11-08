using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

internal static class CleanupCompletion
{
    internal static async Task<ProductOperationOutcome> ApplyAsync(ProductOperationOutcome outcome,
        IReadOnlyList<string> failures, string outputDirectory, CancellationToken token)
    {
        if (failures.Count == 0) return outcome;
        var warnings = outcome.Warnings.Concat(failures).Distinct(StringComparer.Ordinal).ToList();
        var outputs = outcome.CommittedOutputs;
        string? isolationDirectory = outcome.IsolationDirectory;
        if (outputs.Count > 0 && isolationDirectory is null)
        {
            var isolation = await CleanupOutputIsolation.TryIsolateAsync(outputs, outcome.Inputs, outputDirectory, token).ConfigureAwait(false);
            outputs = isolation.Outputs;
            isolationDirectory = isolation.Directory;
            if (isolation.Failure is { } failure)
                warnings.Add("成品隔离迁移未完成，已保留并列出实际位置：" + failure.Message);
        }
        bool completed = outcome.Status is ProductOperationStatus.Success or ProductOperationStatus.Warning;
        bool lostOutputs = outputs.Count < outcome.CommittedOutputs.Count;
        var paths = outputs.Count == 0 ? "本次未生成输出。" : "实际输出：" + Environment.NewLine +
            string.Join(Environment.NewLine, outputs.Select(output => output.Role + ": " + output.Path));
        string message = string.Join(Environment.NewLine, new[] { outcome.Details }.Where(text => !string.IsNullOrWhiteSpace(text))) +
            Environment.NewLine + string.Join(Environment.NewLine, warnings) + Environment.NewLine + paths;
        return outcome with
        {
            Status = completed && lostOutputs && outputs.Count == 0 ? ProductOperationStatus.Failed
                : completed ? ProductOperationStatus.Warning : outcome.Status,
            CompletionKind = completed && lostOutputs ? outputs.Count == 0 ? ProductCompletionKind.NoOutput : ProductCompletionKind.RecoveredMedia
                : completed && (outcome.CompletionKind is null or ProductCompletionKind.TargetCompleted)
                ? ProductCompletionKind.CompatibleCompleted : outcome.CompletionKind,
            CommittedOutputs = outputs, IsolationDirectory = isolationDirectory, Warnings = warnings,
            SourceActionGuard = null,
            Diagnostic = outcome.Diagnostic with { Message = message,
                Stage = completed ? ProductOperationStage.Cleanup : outcome.Diagnostic.Stage,
                Category = completed ? ProductFailureCategory.Cleanup : outcome.Diagnostic.Category }
        };
    }
}
