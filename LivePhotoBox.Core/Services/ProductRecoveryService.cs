using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Services;

// Recovery preserves user inputs through the same identity/transaction boundary
// as normal exports. It never turns an unverified target into a success.
internal static class ProductRecoveryService
{
    internal static async Task<ProductOperationOutcome> SaveOriginalsAsync(
        ProductOperationOutcome baseline, ProductOperationDiagnostic diagnostic,
        string directory, CancellationToken token,
        IReadOnlyList<MediaArtifact>? recoveredMedia = null)
    {
        if (token.IsCancellationRequested)
            return baseline with { Status = ProductOperationStatus.Cancelled,
                CompletionKind = ProductCompletionKind.Cancelled,
                Diagnostic = diagnostic with { Category = ProductFailureCategory.Cancelled } };
        var committed = new List<ProductCommittedArtifact>();
        var limitations = new List<string> { diagnostic.Message };
        string isolation = ProductExportAssessment.IsolationDirectory(directory, baseline.OperationId);
        // Per-operation folder prevents partial recovery from colliding with
        // another item; each file is still transactionally published.
        string destination = Path.Combine(isolation, "original-" + baseline.OperationId.ToString("N"));
        if (OutputDirectoryAttempt.Ensure(destination) is { } directoryError)
        {
            return baseline with { Status = ProductOperationStatus.Failed, CompletionKind = ProductCompletionKind.NoOutput,
                Diagnostic = diagnostic with { Category = ProductFailureCategory.Io,
                    Message = "未生成输出；无法创建隔离目录：" + directoryError.Message } };
        }
        var usableMedia = recoveredMedia?.ToList() ?? new List<MediaArtifact>();
        using var recoveryLeases = new NeutralArtifactReadLeaseSet();
        // A rejected still must not hide an independently readable explicit movie.
        // This route runs only when inspection stopped before any media preparation.
        if (usableMedia.Count == 0 && diagnostic.Stage == ProductOperationStage.Inspection)
        {
            foreach (string path in baseline.Inputs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string extension = Path.GetExtension(path);
                if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = new MediaArtifact { Path = path, Kind = MediaArtifactKind.MotionVideo };
                if (recoveryLeases.TryPin(candidate) is not null) continue;
                var probe = await NativeMediaService.TryProbeVideoAsync(path, token).ConfigureAwait(false);
                if (probe.Result == NativeResult.Ok && probe.Facts is not null) usableMedia.Add(candidate);
            }
        }
        bool recover = usableMedia.Count > 0;
        var candidates = new List<(MediaArtifact Artifact,bool Recovered)>();
        if(recover) candidates.AddRange(usableMedia.Select(a=>(a,true)));
        foreach(string path in baseline.Inputs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool video=Path.GetExtension(path).Equals(".mp4",StringComparison.OrdinalIgnoreCase)||
                Path.GetExtension(path).Equals(".mov",StringComparison.OrdinalIgnoreCase);
            var kind=video?MediaArtifactKind.MotionVideo:MediaArtifactKind.PrimaryImage;
            // A prepared still does not replace an unconverted companion movie.
            // Save that original too when no usable movie was recovered.
            if(!recover || !usableMedia.Any(a=>a.Kind==kind) ||
                (baseline.Inputs.Count==1 && !usableMedia.Any(a=>a.Kind==MediaArtifactKind.MotionVideo)))
                candidates.Add((new MediaArtifact { Path=path,Kind=kind },false));
        }
        foreach (var candidate in candidates)
        {
            var artifact=candidate.Artifact;
            string source = artifact.Path;
            if (token.IsCancellationRequested) break;
            if (!File.Exists(source)) { limitations.Add("无法读取来源：" + source); continue; }
            try
            {
                string final = Path.Combine(destination, Path.GetFileName(source));
                if (committed.Any(x => string.Equals(x.Path, final, StringComparison.OrdinalIgnoreCase)))
                    final = Path.Combine(destination, committed.Count + "-" + Path.GetFileName(source));
                var publication=await WindowsOutputArtifactPublisher.TryPublishCopyAsync(
                    artifact,
                    final, false, token).ConfigureAwait(false);
                if(publication.Failure is { } failure)
                {
                    if(failure is OperationCanceledException)break;
                    limitations.Add("原样保存未完成："+failure.Message);
                    continue;
                }
                committed.Add(new ProductCommittedArtifact { Role = candidate.Recovered ? "Recovered" + artifact.Kind : "OriginalUnconverted", Path = final });
                if(recover && !candidate.Recovered)
                    limitations.Add(Text("ProductRecovery_UnconvertedPart","Also saved unchanged, without conversion: ") + final);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Filesystem result migration is independent of media recovery;
                // keep any committed originals accurately reported on failure.
                limitations.Add("原样保存未完成：" + error.Message);
            }
        }
        bool saved = committed.Count > 0;
        bool recovered = committed.Any(x => x.Role.StartsWith("Recovered", StringComparison.Ordinal));
        string message = saved
            ? (recovered ? Text("ProductRecovery_Recovered","Usable media recovered; the requested live photo was not created.")
                : Text("ProductRecovery_Original","Originals saved unchanged; conversion is incomplete and the requested live photo was not created.")) + Environment.NewLine +
                string.Join(Environment.NewLine, limitations) + Environment.NewLine + Text("ProductRecovery_Directory","Isolation directory: ") + destination
            : Text("ProductRecovery_NoOutput","No output was created.") + Environment.NewLine + string.Join(Environment.NewLine, limitations);
        return baseline with
        {
            Status = token.IsCancellationRequested ? ProductOperationStatus.Cancelled : saved
                ? ProductOperationStatus.Warning : ProductOperationStatus.Failed,
            CompletionKind = token.IsCancellationRequested ? ProductCompletionKind.Cancelled : saved
                ? recovered ? ProductCompletionKind.RecoveredMedia : ProductCompletionKind.OriginalSaved : ProductCompletionKind.NoOutput,
            Warnings = saved ? limitations : [], IsolationDirectory = saved ? destination : null,
            CommittedOutputs = committed, SourceActionGuard = null,
            Diagnostic = diagnostic with { Message = message }
        };
    }
    private static string Text(string key,string fallback)
    {
        string value=ResourceService.GetString(key);
        return string.IsNullOrWhiteSpace(value)||value==key ? fallback : value;
    }

}
