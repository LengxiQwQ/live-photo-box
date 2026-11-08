using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Cli.Commands;

// Explicit, read-only invocation of the production scanner. Never runs as part
// of merge/split automatically and never treats small samples as scale acceptance.
internal static class ScanMeasureCommand
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    internal static Command Create()
    {
        var input = new Argument<string>("input") { Description = "Explicit input directory to measure" };
        var mode = new Option<string>("--mode") { DefaultValueFactory = _ => "name", Description = "name, apple, vivo, split" };
        var recursive = new Option<bool>("--recursive");
        var command = new Command("scan-measure", "Measure the actual read-only production scanner; no media processing") { input, mode, recursive };
        command.SetAction(async (parsed, token) =>
        {
            string selected = parsed.GetValue(mode)!;
            if (selected is not ("name" or "apple" or "vivo" or "split")) return 2;
            using var process = Process.GetCurrentProcess();
            bool hasIo = GetProcessIoCounters(process.Handle, out var before);
            long apple = FileIndexService.Shared.AppleReads, vivo = FileIndexService.Shared.VivoReads, markers = FileIndexService.Shared.MarkerReads;
            long reads = FileIndexService.Shared.Reads, bytes = FileIndexService.Shared.BytesRead, calls = FileIndexService.Shared.ReadCalls;
            var clock = Stopwatch.StartNew();
            var events = new List<object>();
            double? enumerationFinished = null, firstCandidate = null;
            ScanWorkStage? last = null;
            var progress = new CallbackProgress<WorkProgressSnapshot>(s =>
            {
                if (s.Stage == ScanWorkStage.Enumerating && s.Total > 0 && s.Completed == s.Total) enumerationFinished ??= clock.Elapsed.TotalMilliseconds;
                if (s.RecognizedCount > 0) firstCandidate ??= clock.Elapsed.TotalMilliseconds;
                if (s.Stage != last || s.Completed == s.Total)
                    events.Add(new { milliseconds = clock.Elapsed.TotalMilliseconds, stage = s.Stage.ToString(), s.Total, s.Completed, s.RecognizedCount });
                last = s.Stage;
            });
            var options = new MergeScanOptions { InputDirectory = parsed.GetValue(input), Recursive = parsed.GetValue(recursive),
                Mode = selected switch { "apple" => PairingMode.Cid, "vivo" => PairingMode.Vivo, _ => PairingMode.Name } };
            object results;
            ScanOperationStatus status;
            bool partial;
            if (selected == "split")
            {
                var scan = await LivePhotoDiscoveryService.ScanFilesAsync(options, DiscoveryScanMode.SplitOnly, token, progress, new CallbackProgress<LivePhotoDiscoveryItem>(_ => firstCandidate ??= clock.Elapsed.TotalMilliseconds));
                results = new { scan.Items, scan.Diagnostics };status = scan.Status;partial = scan.IsPartial;
            }
            else
            {
                var scan = await LivePhotoMergeScanService.ScanAsync(options, token, progress);
                results = new { scan.Pairs, scan.Diagnostics, scan.StandaloneImagePaths, scan.StandaloneVideoPaths };status = scan.Status;partial = scan.IsPartial;
            }
            process.Refresh();
            hasIo &= GetProcessIoCounters(process.Handle, out var after);
            Console.WriteLine(JsonSerializer.Serialize(new { mode = selected, input = options.InputDirectory, status = status.ToString(), partial,
                elapsedMilliseconds = clock.Elapsed.TotalMilliseconds, enumerationFinishedMilliseconds = enumerationFinished, firstCandidateMilliseconds = firstCandidate,
                guiListSubmissionMilliseconds = (double?)null, guiMeasurement = "not run", stages = events,
                appleExtractions = FileIndexService.Shared.AppleReads - apple, vivoExtractions = FileIndexService.Shared.VivoReads - vivo,
                markerExtractions = FileIndexService.Shared.MarkerReads - markers,
                nativeCapabilityRequests = FileIndexService.Shared.Reads - reads,
                nativeLogicalBytes = FileIndexService.Shared.BytesRead - bytes, nativeReadCalls = FileIndexService.Shared.ReadCalls - calls,
                processReadBytes = hasIo ? (ulong?)(after.ReadBytes - before.ReadBytes) : null,
                processReadOperations = hasIo ? (ulong?)(after.ReadOperations - before.ReadOperations) : null,
                process.PeakWorkingSet64, process.HandleCount,
                physicalDiskBytes = (ulong?)null, physicalDiskMeasurement = "not measured",
                cacheCondition = "OS cache not controlled; application session cache initially empty in a new process",
                scaleAcceptance = "not assessed", results }, new JsonSerializerOptions { WriteIndented = true }));
            return status == ScanOperationStatus.Cancelled ? 130 : status == ScanOperationStatus.Failed ? 1 : 0;
        });
        return command;
    }
}
