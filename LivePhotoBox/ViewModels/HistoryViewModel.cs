using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.ViewModels
{
    public partial class HistoryViewModel : ViewModelBase
    {
        // ── Protocol XMP namespaces ────────────────────────────────────────
        private static readonly XNamespace RdfNs   = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        private static readonly XNamespace DcNs    = "http://purl.org/dc/elements/1.1/";
        private static readonly XNamespace GCamera = "http://ns.google.com/photos/1.0/camera/";
        private static readonly XNamespace Container = "http://ns.google.com/photos/1.0/container/";
        private static readonly XNamespace OpCamera = "http://ns.oplus.com/photos/1.0/camera/";
        private static readonly XNamespace LivePhotoBoxNs = "https://github.com/LengxiQwQ/live-photo-box";

        // ── Observable properties ──────────────────────────────────────────

        [ObservableProperty]
        private string _selectedFolder = string.Empty;

        [ObservableProperty]
        private bool _isScanning;

        [ObservableProperty]
        private bool _hasResults;

        [ObservableProperty]
        private string _statusText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryStatsText))]
        private int _totalFiles;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryStatsText))]
        private int _livePhotoCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryStatsText))]
        private int _livePhotoBoxCount;

        /// <summary>Summary line shown after scan completes.</summary>
        public string SummaryStatsText
        {
            get
            {
                string files = ResourceService.GetString("History_TotalLabel");
                string live = ResourceService.GetString("History_LivePhotoLabel");
                string marked = ResourceService.GetString("History_MarkedLabel");
                return $"{files} {TotalFiles}  •  {live} {LivePhotoCount}  •  {marked} {LivePhotoBoxCount}";
            }
        }

        public ObservableCollection<FileHistoryInfo> Files { get; } = new();

        public override string? PageStatusTag => null;

        // ── Commands ───────────────────────────────────────────────────────

        [RelayCommand]
        private async Task ScanFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
                return;

            IsScanning = true;
            HasResults = false;
            Files.Clear();
            StatusText = ResourceService.GetString("History_Scanning");
            TotalFiles = 0;
            LivePhotoCount = 0;
            LivePhotoBoxCount = 0;

            try
            {
                // Find all image files
                var imageFiles = Directory.EnumerateFiles(SelectedFolder, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsSupportedImage(f))
                    .OrderBy(f => f)
                    .ToList();

                TotalFiles = imageFiles.Count;

                if (TotalFiles == 0)
                {
                    StatusText = ResourceService.GetString("History_NoImages");
                    return;
                }

                int processed = 0;
                var exifToolPath = ExternalToolLocator.FindExifTool();

                // Process files with limited parallelism (4 at a time)
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = CancellationToken.None, // we could wire up cancellation
                };

                await Parallel.ForEachAsync(imageFiles, parallelOptions, async (file, ct) =>
                {
                    try
                    {
                        var history = await AnalyzeFileAsync(exifToolPath, file, ct);
                        if (history != null)
                        {
                            lock (Files)
                            {
                                Files.Add(history);
                                if (history.IsLivePhoto) LivePhotoCount++;
                                if (history.IsLivePhotoBoxGenerated) LivePhotoBoxCount++;
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        LogService.History($"Error scanning {Path.GetFileName(file)}: {ex.Message}", Models.LogLevel.Warning);
                    }

                    int done = Interlocked.Increment(ref processed);
                    if (done % 5 == 0 || done == TotalFiles)
                    {
                        // Update status text occasionally
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            StatusText = ResourceService.Format("History_ScanProgress", done, TotalFiles);
                        });
                    }
                });

                HasResults = Files.Count > 0;

                // Sort files by file name for consistent display
                var sorted = Files.OrderBy(f => f.FileName).ToList();
                Files.Clear();
                foreach (var f in sorted) Files.Add(f);

                if (Files.Count == 0)
                {
                    StatusText = ResourceService.GetString("History_NoLivePhotos");
                }
                else
                {
                    StatusText = ResourceService.Format("History_ScanComplete",
                        Files.Count, LivePhotoCount, LivePhotoBoxCount);
                }
            }
            finally
            {
                IsScanning = false;
            }
        }

        // ── File analysis ──────────────────────────────────────────────────

        private async Task<FileHistoryInfo?> AnalyzeFileAsync(
            string? exifToolPath, string filePath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(exifToolPath))
            {
                LogService.History($"ExifTool not found at '{exifToolPath}'", Models.LogLevel.Warning);
                return null;
            }

            // 1. Get raw XMP XML via exiftool
            string xmpXml = await ReadRawXmpAsync(exifToolPath, filePath, ct);
            if (string.IsNullOrWhiteSpace(xmpXml))
            {
                // No XMP at all — not a Live Photo
                return null;
            }

            // 2. Parse XMP
            var info = ParseXmp(xmpXml, filePath);
            return info;
        }

        private static async Task<string> ReadRawXmpAsync(
            string exifToolPath, string filePath, CancellationToken ct)
        {
            var tempDir = Path.GetTempPath();
            var toolDir = Path.GetDirectoryName(exifToolPath) ?? AppContext.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = exifToolPath,
                WorkingDirectory = toolDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMP"] = tempDir;

            // Request full XMP XML
            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("utf8");
            psi.ArgumentList.Add("-xmp");
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add(filePath);

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { process.Kill(); throw; }

            string output = await outputTask;
            string error = await errorTask;

            // exiftool returns empty string if no XMP
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            return output;
        }

        private FileHistoryInfo? ParseXmp(string xmpXml, string filePath)
        {
            // ── Sanitize exiftool output ──────────────────────────────
            // exiftool -b outputs the raw XMP including the xpacket wrapper and
            // possible trailing padding bytes. Strip everything after the closing
            // xpacket tag so XDocument.Parse doesn't choke on binary garbage.
            // Also strip any invalid XML characters (control chars except \t\r\n).

            // 1. Strip trailing junk after <?xpacket end=...>
            int xpacketEnd = xmpXml.LastIndexOf("<?xpacket end=", StringComparison.Ordinal);
            if (xpacketEnd >= 0)
            {
                int closeTag = xmpXml.IndexOf('>', xpacketEnd);
                if (closeTag >= 0)
                {
                    xmpXml = xmpXml[..(closeTag + 1)];
                }
            }

            // 2. Remove any XML-invalid control characters (except \t \r \n)
            //    that might survive inside the xpacket wrapper.
            var cleaned = new StringBuilder(xmpXml.Length);
            foreach (char c in xmpXml)
            {
                if (c == '\t' || c == '\r' || c == '\n' || c >= ' ')
                    cleaned.Append(c);
            }
            xmpXml = cleaned.ToString();

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xmpXml);
            }
            catch
            {
                // Fallback: try to parse just the <rdf:RDF>…</rdf:RDF> portion
                try
                {
                    int rdfStart = xmpXml.IndexOf("<rdf:RDF", StringComparison.Ordinal);
                    int rdfEnd = xmpXml.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal);
                    if (rdfStart >= 0 && rdfEnd > rdfStart)
                    {
                        doc = XDocument.Parse(xmpXml[rdfStart..(rdfEnd + "</rdf:RDF>".Length)]);
                    }
                    else
                    {
                        return null;
                    }
                }
                catch
                {
                    return null;
                }
            }

            // Find the rdf:Description element
            var desc = doc.Descendants(RdfNs + "Description").FirstOrDefault();
            if (desc == null)
            {
                // XMP but no Description — not a useful live photo
                return null;
            }

            var info = new FileHistoryInfo { FilePath = filePath };

            // ── Detect LivePhotoBox generation ─────────────────────────
            var lpbAction = GetAttributeValue(desc, LivePhotoBoxNs, "Action");
            if (lpbAction != null)
            {
                info.IsLivePhotoBoxGenerated = true;
                info.MergeProtocol = GetAttributeValue(desc, LivePhotoBoxNs, "Protocol") ?? string.Empty;
                info.MergeVersion  = GetAttributeValue(desc, LivePhotoBoxNs, "Version")  ?? string.Empty;
            }

            // Parse dc:subject entries (Split / Repair history)
            var subjectEl = desc.Element(DcNs + "subject");
            if (subjectEl != null)
            {
                foreach (var li in subjectEl.Descendants(RdfNs + "li"))
                {
                    var entry = ParseHistorySubject(li.Value);
                    if (entry != null) info.Entries.Add(entry);
                }
            }

            // ── Detect protocol type from known XMP tags ───────────────
            bool hasMicroVideo = GetAttributeValue(desc, GCamera, "MicroVideo") == "1" ||
                                 GetAttributeValue(desc, GCamera, "MicroVideoVersion") != null;
            bool hasMotionPhoto = GetAttributeValue(desc, GCamera, "MotionPhoto") == "1" ||
                                  GetAttributeValue(desc, GCamera, "MotionPhotoVersion") != null;
            bool hasOppo = GetAttributeValue(desc, OpCamera, "OLivePhotoVersion") != null ||
                           GetAttributeValue(desc, OpCamera, "MotionPhotoOwner") != null;

            info.IsLivePhoto = hasMicroVideo || hasMotionPhoto || hasOppo;

            if (hasMicroVideo)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_MicroVideoV1");
            else if (hasOppo)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_OPPO");
            else if (hasMotionPhoto)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_MotionPhotoV2");

            // ── Add Merge entry if LivePhotoBox generated ──────────────
            if (info.IsLivePhotoBoxGenerated && info.Entries.All(e => e.Action != "Merge"))
            {
                var comboEntry = new HistoryEntry
                {
                    Action = "Merge",
                    Version = info.MergeVersion,
                    Description = ResourceService.Format("History_MergeDesc", info.MergeProtocol),
                };

                // If no Split/Repair entries have timestamps, Merge has none either
                // Insert at beginning since Merge happens first
                info.Entries.Insert(0, comboEntry);
            }

            // ── Sort all entries chronologically ───────────────────────
            var sorted = info.Entries
                .OrderBy(e => e.Timestamp ?? DateTime.MinValue)
                .ThenBy(e => e.Action == "Merge" ? 0 : 1) // Merge first if same timestamp
                .ToList();
            info.Entries.Clear();
            foreach (var e in sorted) info.Entries.Add(e);

            // ── Build summary ──────────────────────────────────────────
            if (!info.IsLivePhoto && !info.IsLivePhotoBoxGenerated)
                return null; // Not a live photo at all — skip

            if (info.IsLivePhotoBoxGenerated)
                info.Summary = info.Entries.Count > 1
                    ? ResourceService.Format("History_Summary_Generated", info.Entries.Count)
                    : ResourceService.GetString("History_Summary_MergeOnly");
            else if (info.IsLivePhoto)
                info.Summary = ResourceService.GetString("History_Summary_LivePhoto");

            return info;
        }

        /// <summary>Parse a dc:subject entry like "LivePhotoBox:Split@2026-06-25T14:30:22@v1.2.0@Format=JPEG+MP4"</summary>
        private static HistoryEntry? ParseHistorySubject(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
                return null;

            // Expected format:
            //   LivePhotoBox:Split@{timestamp}@v{version}@{details}
            //   LivePhotoBox:Repair@{timestamp}@v{version}@{details}
            const string prefix = "LivePhotoBox:";
            if (!subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            string body = subject[prefix.Length..]; // e.g. "Split@2026-06-25T14:30:22@v1.2.0@Format=..."

            var parts = body.Split('@');
            if (parts.Length < 2) return null;

            string action = parts[0]; // "Split" or "Repair"

            var entry = new HistoryEntry
            {
                Action = action switch
                {
                    "Split"  => "Split",
                    "Repair" => "Repair",
                    _ => action,
                },
            };

            // Parse timestamp (part[1])
            if (parts.Length > 1 && DateTime.TryParse(parts[1], out var ts))
                entry.Timestamp = ts;

            // Parse version (e.g. "v1.2.0" in parts[2])
            if (parts.Length > 2 && parts[2].StartsWith("v", StringComparison.OrdinalIgnoreCase))
                entry.Version = parts[2][1..]; // strip "v" prefix

            // Parse details (parts[3], e.g. "Format=JPEG+MP4" or "Fix=Rotation+Thumbnail")
            if (parts.Length > 3)
            {
                string details = parts[3];
                int eqIdx = details.IndexOf('=');
                if (eqIdx >= 0)
                {
                    string value = details[(eqIdx + 1)..];
                    // Humanize the value
                    if (details.StartsWith("Format=", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Description = ResourceService.Format(
                            "History_FormatDesc",
                            value.Replace("+", " + "));
                    }
                    else if (details.StartsWith("Fix=", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Description = ResourceService.Format(
                            "History_FixDesc",
                            value.Replace("+", " + "));
                    }
                    else
                    {
                        entry.Description = value.Replace("+", " + ");
                    }
                }
                else
                {
                    entry.Description = details;
                }
            }

            return entry;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static string? GetAttributeValue(XElement element, XNamespace ns, string localName)
        {
            var attr = element.Attribute(ns + localName);
            return attr?.Value;
        }

        private static bool IsSupportedImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".png";
        }
    }
}
