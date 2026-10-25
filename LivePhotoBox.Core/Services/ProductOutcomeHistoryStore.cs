using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

// Structured terminal records live in the existing retained session logs. This
// reader never scans media folders or reconstructs outcomes from human log text.
internal static class ProductOutcomeHistoryStore
{
    internal const string Marker = "LPB_PRODUCT_OUTCOME_V1 ";
    internal const int MaxRecords = 1000;
    private const int MaxFilesPerDirectory = 100;
    private const int MaxTailBytes = 4 * 1024 * 1024;
    private const int MaxReadBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 64
    };

    // Caller owns LogService's file lock. Flush(true) completes before success.
    internal static bool Append(string? logPath, ProductOperationOutcome outcome)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath) || !IsValid(outcome)) return false;
            string json = JsonSerializer.Serialize(outcome, JsonOptions);
            byte[] bytes = Encoding.UTF8.GetBytes("\n" + Marker + json + "\n");
            if (bytes.Length > MaxTailBytes) return false;
            using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<ProductOperationOutcome> Read(string root, int maxCount = 200)
    {
        if (maxCount <= 0) return Array.Empty<ProductOperationOutcome>();
        maxCount = Math.Min(maxCount, MaxRecords);
        var records = new Dictionary<Guid, ProductOperationOutcome>();
        int budget = MaxReadBytes;
        try
        {
            // Only these two owned directories/prefixes, never a recursive scan.
            var files = OwnFiles(root, "app-*.log")
                .Concat(OwnFiles(Path.Combine(root, "CLI"), "cli-*.log"))
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            foreach (string file in files)
            {
                if (budget <= 0 || records.Count >= MaxRecords) break;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    int length = (int)Math.Min(stream.Length, Math.Min(MaxTailBytes, budget));
                    long start = stream.Length - length;
                    stream.Seek(start, SeekOrigin.Begin);
                    byte[] bytes = new byte[length];
                    int read = 0;
                    while (read < length)
                    {
                        int n = stream.Read(bytes, read, length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    budget -= length;
                    string text = Encoding.UTF8.GetString(bytes, 0, read);
                    // Ignore a cut first line and any unterminated final record.
                    int pos = start == 0 ? 0 : text.IndexOf('\n') + 1;
                    if (pos == 0 && start != 0) continue;
                    var lines = new List<string>();
                    for (int end; (end = text.IndexOf('\n', pos)) >= 0; pos = end + 1)
                        if (text.AsSpan(pos, end - pos).StartsWith(Marker, StringComparison.Ordinal))
                            lines.Add(text.Substring(pos + Marker.Length, end - pos - Marker.Length).TrimEnd('\r'));
                    // Newest duplicate wins, both within a file and across sessions.
                    for (int i = lines.Count - 1; i >= 0 && records.Count < MaxRecords; i--)
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(lines[i]);
                            var element = document.RootElement;
                            if (element.ValueKind != JsonValueKind.Object ||
                                !element.TryGetProperty("SchemaVersion", out var schema) ||
                                schema.ValueKind != JsonValueKind.Number ||
                                !schema.TryGetInt32(out int version) || version != 1 ||
                                !new[] { "OperationId", "StartedUtc", "CompletedUtc", "Operation", "Status", "RequestedSelection", "Inputs", "CommittedOutputs", "Diagnostic", "FinalTargetPreservation" }
                                    .All(name => element.TryGetProperty(name, out _))) continue;
                            var record = JsonSerializer.Deserialize<ProductOperationOutcome>(lines[i], JsonOptions);
                            if (record != null && IsValid(record)) records.TryAdd(record.OperationId, record);
                        }
                        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return records.Values.OrderByDescending(x => x.CompletedUtc).Take(maxCount).ToArray();
    }

    private static IEnumerable<string> OwnFiles(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return Array.Empty<string>();
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Where(x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(MaxFilesPerDirectory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool IsValid(ProductOperationOutcome value) =>
        value.SchemaVersion == 1 && value.OperationId != Guid.Empty &&
        value.StartedUtc != default && value.CompletedUtc >= value.StartedUtc &&
        Enum.IsDefined(value.Operation) && Enum.IsDefined(value.Status) &&
        value.FinalTargetPreservation == ProductTargetPreservation.Unknown &&
        value.RequestedSelection != null && value.Inputs != null && value.CommittedOutputs != null &&
        value.Diagnostic != null && Enum.IsDefined(value.Diagnostic.Stage) && Enum.IsDefined(value.Diagnostic.Category) &&
        value.PreparationEvidenceScope == "PreparedNeutral" &&
        (value.PreparationExecution == null ||
            (value.PreparationExecution.Videos != null && value.PreparationExecution.Videos.All(x => x != null && x.Truth != null) &&
             (value.PreparationExecution.Image == null || value.PreparationExecution.Image.Truth != null))) &&
        value.Inputs.All(x => x != null) &&
        value.CommittedOutputs.All(x => x != null && !string.IsNullOrWhiteSpace(x.Role) && !string.IsNullOrWhiteSpace(x.Path)) &&
        HasValidCommittedOutputs(value);

    private static bool HasValidCommittedOutputs(ProductOperationOutcome value)
    {
        if (value.Status != ProductOperationStatus.Success) return value.CommittedOutputs.Count == 0;
        // Validate the schema's terminal shape, without consulting current files or
        // promoting historical paths into current identity/preservation evidence.
        var outputs = value.CommittedOutputs;
        bool rolesValid = value.Operation switch
        {
            ProductOperationKind.Split => outputs.Count == 2 &&
                outputs.Count(x => x.Role == "PrimaryImage") == 1 &&
                outputs.Count(x => x.Role == "MotionVideo") == 1,
            ProductOperationKind.Merge => outputs.Count == 1 && outputs[0].Role == "TargetLivePhoto",
            _ => false
        };
        if (!rolesValid) return false;
        try
        {
            return outputs.Select(x => Path.GetFullPath(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == outputs.Count;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}
