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

    internal static bool CanReportOperationFailure(Exception error) =>
        error is not OutOfMemoryException and not AccessViolationException and not StackOverflowException;

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
            ct.ThrowIfCancellationRequested();
            if (!seenFiles.Add(path)) return;
            if (IsImage(path)) images.Add(path);
            else if (IsVideo(path)) videos.Add(path);
            else diagnostics.Add(Info(path, "UnsupportedExtension", "The file extension is not supported for this scan."));
        }

        foreach (string path in options.ExplicitFiles ?? Array.Empty<string>())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected file is unavailable.", fullPath);
                AddFile(fullPath);
            }
            catch (Exception ex) when (IsInputFailure(ex) || ex is ArgumentException or NotSupportedException)
            {
                diagnostics.Add(Failure(path, ex, "FileNotFound"));
            }
        }

        var roots = (options.InputDirectories ?? Array.Empty<string>()).ToList();
        if (!string.IsNullOrWhiteSpace(options.InputDirectory)) roots.Add(options.InputDirectory);
        foreach (string root in roots)
        {
            ct.ThrowIfCancellationRequested();
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
                ct.ThrowIfCancellationRequested();
                string directory = pending.Dequeue();
                if (!seenDirectories.Add(directory)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        diagnostics.Add(Info(directory, "ReparsePointSkipped", "Directory links are not followed during scanning."));
                        continue;
                    }
                    // Do not publish candidates from a partially enumerated directory.
                    string[] entries = Directory.GetFileSystemEntries(directory);
                    foreach (string entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = File.GetAttributes(entry);
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
