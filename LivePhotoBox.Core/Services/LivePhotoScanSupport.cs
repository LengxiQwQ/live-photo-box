using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services;

// Shared discovery boundaries. Strict inspection and execution APIs do not use these catches.
internal static class LivePhotoScanSupport
{
    // Only prune former selections whose absence is known. Access/share errors
    // remain in scope and are reported; they cannot conceal an unknown ID.
    internal static IEnumerable<string> RetainSelections(IEnumerable<string> paths) =>
        paths.Where(path =>
        {
            try { _ = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }).Distinct(StringComparer.OrdinalIgnoreCase);

    internal static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".heic" or ".heif";
    internal static bool IsVideo(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mov";

    internal static bool IsInputFailure(Exception error) => error switch
    {
        SourceInspectionException e => e.Category != SourceInspectionFailureCategory.InvalidArgument &&
            !(e.Category == SourceInspectionFailureCategory.Unsupported &&
              e.Capability == NativeRuntime.HeifOriginalFileResourceCapability),
        IOException or UnauthorizedAccessException or InvalidDataException => true,
        _ => false
    };

    internal static bool IsInputFailure(SourceInspectionFailure failure) =>
        failure.Category != SourceInspectionFailureCategory.InvalidArgument &&
        !(failure.Category == SourceInspectionFailureCategory.Unsupported &&
          failure.Capability == NativeRuntime.HeifOriginalFileResourceCapability);

    internal static ScanDiagnostic Failure(string path, SourceInspectionFailure failure,
        string code = "SingleFileInspectionFailed", IReadOnlyList<string>? related = null)
    {
        LogService.Scan($"{code}: '{path}': {failure.Category}/{failure.Stage}: {failure.Reason}", LogLevel.Warning);
        return new ScanDiagnostic
        {
            DiagnosticCode = code, Severity = ScanDiagnosticSeverity.Error,
            Stage = failure.Stage switch
            {
                SourceInspectionStage.Container => ScanDiagnosticStage.Container,
                SourceInspectionStage.Metadata => ScanDiagnosticStage.Metadata,
                SourceInspectionStage.Pairing => ScanDiagnosticStage.Pairing,
                SourceInspectionStage.Protocol => ScanDiagnosticStage.Capability,
                _ => ScanDiagnosticStage.Read
            },
            PrimaryPath = path, RelatedPaths = related ?? Array.Empty<string>(),
            MessageKey = code == "ScanOperationFailed" ? "ScanDiagnostic_OperationFailed" :
                code == "UnresolvedBinding" ? "ScanDiagnostic_UnresolvedBinding" :
                failure.Category == SourceInspectionFailureCategory.Unsupported ? "ScanDiagnostic_UnsupportedInput" : "ScanDiagnostic_InputFailed",
            Summary = failure.Reason, RawCategory = failure.Category, RawStage = failure.Stage,
            RawCapability = failure.Capability, LogMessage = failure.Reason
        };
    }

    internal static bool CanReportOperationFailure(Exception error) =>
        error is not OperationCanceledException and not OutOfMemoryException and not AccessViolationException and not StackOverflowException;

    internal static ScanDiagnostic Failure(string path, Exception error, string code = "SingleFileInspectionFailed",
        IReadOnlyList<string>? related = null, ScanDiagnosticStage? stage = null)
    {
        var inspection = error as SourceInspectionException;
        LogService.Scan($"{code}: '{path}': {error}", LogLevel.Warning);
        return new ScanDiagnostic
        {
            DiagnosticCode = code,
            Severity = ScanDiagnosticSeverity.Error,
            Stage = stage ?? (inspection?.Stage switch
            {
                SourceInspectionStage.Container => ScanDiagnosticStage.Container,
                SourceInspectionStage.Metadata => ScanDiagnosticStage.Metadata,
                SourceInspectionStage.Pairing => ScanDiagnosticStage.Pairing,
                SourceInspectionStage.Protocol => ScanDiagnosticStage.Capability,
                _ => ScanDiagnosticStage.Read
            }),
            PrimaryPath = path,
            RelatedPaths = related ?? Array.Empty<string>(),
            MessageKey = code switch
            {
                "ScanOperationFailed" => "ScanDiagnostic_OperationFailed",
                "BatchSafetyFailed" => "ScanDiagnostic_BatchSafetyFailed",
                "UnresolvedBinding" => "ScanDiagnostic_UnresolvedBinding",
                _ => "ScanDiagnostic_InputFailed"
            },
            Summary = error.Message,
            RawCategory = inspection?.Category,
            RawStage = inspection?.Stage,
            RawCapability = inspection?.Capability,
            LogMessage = error.ToString()
        };
    }

    internal static ScanDiagnostic Info(string path, string code, string summary) => new()
    {
        DiagnosticCode = code,
        Severity = ScanDiagnosticSeverity.Info,
        Stage = ScanDiagnosticStage.Pairing,
        PrimaryPath = path,
        MessageKey = "ScanDiagnostic_" + code,
        Summary = summary
    };

    internal static (List<string> Images, List<string> Videos, bool IsPartial, bool RootFailed) Collect(
        MergeScanOptions options, List<ScanDiagnostic> diagnostics, CancellationToken ct)
    {
        var images = new List<string>();
        var videos = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool partial = false, rootFailed = false;

        void AddFile(string path)
        {
            if (ct.IsCancellationRequested) return;
            if (!seenFiles.Add(path)) return;
            if (IsImage(path)) images.Add(path);
            else if (IsVideo(path)) videos.Add(path);
            else diagnostics.Add(Info(path, "UnsupportedExtension", "The file extension is not supported for this scan."));
        }

        foreach (string path in options.ExplicitFiles ?? Array.Empty<string>())
        {
            if (ct.IsCancellationRequested) return (images, videos, true, rootFailed);
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    diagnostics.Add(Failure(fullPath,new SourceInspectionFailure(SourceInspectionFailureCategory.Io,
                        SourceInspectionStage.Read,0,"The selected file is unavailable.")));
                    partial = true;
                    continue;
                }
                AddFile(fullPath);
            }
            catch (Exception ex) when (IsInputFailure(ex) || ex is ArgumentException or NotSupportedException)
            {
                partial = true;
                diagnostics.Add(Failure(path, ex, "FileNotFound"));
            }
        }

        var roots = (options.InputDirectories ?? Array.Empty<string>()).ToList();
        if (!string.IsNullOrWhiteSpace(options.InputDirectory)) roots.Add(options.InputDirectory);
        foreach (string root in roots)
        {
            if (ct.IsCancellationRequested) return (images, videos, true, rootFailed);
            string fullRoot;
            try { fullRoot = Path.GetFullPath(root); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                diagnostics.Add(Failure(root, ex, "DirectoryEnumerationFailed", stage: ScanDiagnosticStage.Enumeration));
                rootFailed = partial = true;
                continue;
            }
            var pending = new Queue<string>();
            pending.Enqueue(fullRoot);
            while (pending.Count > 0)
            {
                if (ct.IsCancellationRequested) return (images, videos, true, rootFailed);
                string directory = pending.Dequeue();
                if (!seenDirectories.Add(directory)) continue;
                try
                {
                    var directoryRead = ScanFileSystem.Read(directory);
                    if (directoryRead.Failure is { } directoryFailure)
                    {
                        partial = true;
                        if (string.Equals(directory, fullRoot, StringComparison.OrdinalIgnoreCase)) rootFailed = true;
                        diagnostics.Add(Failure(directory, directoryFailure, "DirectoryEnumerationFailed", stage: ScanDiagnosticStage.Enumeration));
                        continue;
                    }
                    if ((directoryRead.Value!.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(Info(directory, "ReparsePointSkipped", "Directory links are not followed during scanning."));
                        continue;
                    }
                    // Do not publish candidates from a partially enumerated directory.
                    var enumeration = ScanFileSystem.Enumerate(directory, ct);
                    if (ct.IsCancellationRequested) return (images, videos, true, rootFailed);
                    if (enumeration.Failure is { } enumerationFailure)
                    {
                        partial = true;
                        if (string.Equals(directory, fullRoot, StringComparison.OrdinalIgnoreCase)) rootFailed = true;
                        diagnostics.Add(Failure(directory, enumerationFailure, "DirectoryEnumerationFailed", stage: ScanDiagnosticStage.Enumeration));
                        continue;
                    }
                    foreach (string entry in enumeration.Entries!)
                    {
                        if (ct.IsCancellationRequested) return (images, videos, true, rootFailed);
                        try
                        {
                            var entryRead = ScanFileSystem.Read(entry);
                            if (entryRead.Failure is { } entryFailure)
                            {
                                partial = true;
                                diagnostics.Add(Failure(entry, entryFailure, "FileReadFailed"));
                                continue;
                            }
                            var attributes = entryRead.Value!.Attributes;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (options.Recursive) pending.Enqueue(entry);
                            }
                            else AddFile(Path.GetFullPath(entry));
                        }
                        catch (Exception ex) when (IsInputFailure(ex))
                        {
                            partial = true;
                            diagnostics.Add(Failure(entry, ex, "FileReadFailed"));
                        }
                    }
                }
                catch (Exception ex) when (IsInputFailure(ex))
                {
                    partial = true;
                    if (string.Equals(directory, fullRoot, StringComparison.OrdinalIgnoreCase)) rootFailed = true;
                    diagnostics.Add(Failure(directory, ex, "DirectoryEnumerationFailed", stage: ScanDiagnosticStage.Enumeration));
                }
            }
        }
        images.Sort(StringComparer.OrdinalIgnoreCase);
        videos.Sort(StringComparer.OrdinalIgnoreCase);
        return (images, videos, partial, rootFailed);
    }
}
