/*
 * HistoryViewModel.cs
 *
 * HistoryPage（历史记录页面）的视图模型，扫描并展示实况照片及历史操作记录。
 *
 *   - 扫描文件夹中的图片文件，解析 XMP 元数据检测实况照片
 *   - 识别 LivePhotoBox 的历史操作记录（合并/拆分/修复）
 *   - 以列表形式展示检测结果
 */

using System;
using System.Collections.Generic;
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
        private static readonly XNamespace VCamera = "http://ns.vivo.com/photos/1.0/camera/";

        // ── Observable properties ──────────────────────────────────────────

        // 用户选择的待扫描文件夹路径。
        [ObservableProperty]
        private string _selectedFolder = string.Empty;

        // 是否正在扫描中。
        [ObservableProperty]
        private bool _isScanning;

        // 扫描完成后是否有检测结果。
        [ObservableProperty]
        private bool _hasResults;

        // 是否已完成一次扫描（无论有没有结果，都显示摘要栏给用户反馈）。
        [ObservableProperty]
        private bool _showSummary;

        // 当前状态文本（扫描中/完成/无结果等）。
        [ObservableProperty]
        private string _statusText = string.Empty;

        // 扫描到的总文件数。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryStatsText))]
        private int _totalFiles;

        // 检测到的实况照片数。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryStatsText))]
        private int _livePhotoCount;

        // 检测到的 LivePhotoBox 生成文件数。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryStatsText))]
        private int _livePhotoBoxCount;

        // 扫描完成后的汇总文本行（如"共 100 个文件  •  实况照片 50  •  已标记 30"）。
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

        // 扫描结果的文件历史信息列表。
        public ObservableCollection<FileHistoryInfo> Files { get; } = new();

        // <inheritdoc/>
        public override string? PageStatusTag => null;

        // ── Commands ───────────────────────────────────────────────────────

        // 扫描所选文件夹中所有图片的实况照片历史记录。
        [RelayCommand]
        private async Task ScanFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedFolder) || !Directory.Exists(SelectedFolder))
                return;

            IsScanning = true;
            HasResults = false;
            ShowSummary = false;
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
                    ShowSummary = true;
                    return;
                }

                int processed = 0;
                var exifToolPath = ExternalToolLocator.FindExifTool();
                // 并行线程只写入本地集合，最后回到 UI 线程再统一加入绑定集合，
                // 避免跨线程修改 ObservableCollection 触发 WinUI 线程封送异常
                // （否则 Files 始终为空，界面"点了没反应"）。
                var found = new List<FileHistoryInfo>();
                int liveCount = 0, lpbCount = 0;
                var scanLock = new object();

                // Process files with limited parallelism (4 at a time)
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = CancellationToken.None,
                };

                await Parallel.ForEachAsync(imageFiles, parallelOptions, async (file, ct) =>
                {
                    try
                    {
                        var history = await AnalyzeFileAsync(exifToolPath, file, ct);
                        if (history != null)
                        {
                            lock (scanLock)
                            {
                                found.Add(history);
                                if (history.IsLivePhoto) liveCount++;
                                if (history.IsLivePhotoBoxGenerated) lpbCount++;
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

                // 回到 UI 线程（命令上下文），统一更新绑定集合与统计。
                foreach (var h in found) Files.Add(h);
                LivePhotoCount = liveCount;
                LivePhotoBoxCount = lpbCount;
                HasResults = Files.Count > 0;
                ShowSummary = true;

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

        // 使用 ExifTool 读取图片的 XMP 元数据，解析并返回文件历史信息。
        // 如果文件不包含实况照片或 LivePhotoBox 相关元数据，返回 null。
        private async Task<FileHistoryInfo?> AnalyzeFileAsync(
            string? exifToolPath, string filePath, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(exifToolPath))
            {
                LogService.History($"ExifTool not found at '{exifToolPath}'", Models.LogLevel.Warning);
                return null;
            }

            // 1. Get raw XMP XML: HEIC 优先字节级读取（本工具注入的 Adobe XMP uuid box
            //    是最新写入的 XMP；华为合并型 HEIC exiftool 读不到，双 XMP 时还会
            //    读到旧的 mime 条目），读不到再退回 exiftool。
            string xmpXml = string.Empty;
            if (IsHeicFile(filePath))
            {
                xmpXml = await HeicXmpInjector.TryReadXmpTextAsync(filePath, ct)
                    ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(xmpXml))
            {
                xmpXml = await ReadRawXmpAsync(exifToolPath, filePath, ct);
            }
            if (string.IsNullOrWhiteSpace(xmpXml))
            {
                // No XMP at all — not a Live Photo
                return null;
            }

            // 2. Parse XMP
            var info = ParseXmp(xmpXml, filePath);
            return info;
        }

        // 调用 ExifTool 命令行工具，读取文件的原始 XMP XML 数据。
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

        // 解析 XMP XML，提取实况照片协议信息（Google MicroVideo、MotionPhoto、OPPO）
        // 以及 LivePhotoBox 的历史操作记录（合并/拆分/修复）。
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
            // Parse dc:subject entries first (history is also used for generation detection).
            var subjectEl = desc.Element(DcNs + "subject");
            if (subjectEl != null)
            {
                foreach (var li in subjectEl.Descendants(RdfNs + "li"))
                {
                    // 条目解析统一走 Core（XmpMarkerService），保证与写入格式同源。
                    var record = XmpMarkerService.ParseHistoryEntry(li.Value);
                    if (record != null) info.Entries.Add(ToHistoryEntry(record));
                }
            }

            // Detect LivePhotoBox generation: legacy Action/Protocol attributes (old files),
            // namespace declaration + Version/Timestamp attrs (new files), or history entries.
            bool hasLpbAttr = GetAttributeValue(desc, LivePhotoBoxNs, "Action") != null
                || GetAttributeValue(desc, LivePhotoBoxNs, "Version") != null
                || GetAttributeValue(desc, LivePhotoBoxNs, "Timestamp") != null;
            bool hasLpbNamespace = desc.Attribute(XNamespace.Xmlns + "LivePhotoBox") != null;
            if (hasLpbAttr || hasLpbNamespace || info.Entries.Count > 0)
            {
                info.IsLivePhotoBoxGenerated = true;
                info.MergeVersion = GetAttributeValue(desc, LivePhotoBoxNs, "Version") ?? string.Empty;
                // MergeProtocol: legacy attribute first, else the first Merge entry's Target detail.
                info.MergeProtocol = GetAttributeValue(desc, LivePhotoBoxNs, "Protocol")
                    ?? info.Entries.FirstOrDefault(e => e.Action == "Merge")?.Details.GetValueOrDefault("Target")
                    ?? string.Empty;
            }

            // ── Detect protocol type from known XMP tags (priority order) ──
            // 优先级：OPPO > vivo > MicroVideo(V1) > MotionPhoto(V2)
            // 小米直接使用 Google V2 协议，MiCamera 字段为相机数据非实况识别字段
            // Fusion 由 LivePhotoBox 命名空间识别（info.IsLivePhotoBoxGenerated）
            bool hasMicroVideo = GetAttributeValue(desc, GCamera, "MicroVideo") == "1" ||
                                 GetAttributeValue(desc, GCamera, "MicroVideoVersion") != null;
            bool hasMotionPhoto = GetAttributeValue(desc, GCamera, "MotionPhoto") == "1" ||
                                  GetAttributeValue(desc, GCamera, "MotionPhotoVersion") != null;
            bool hasOppo = GetAttributeValue(desc, OpCamera, "OLivePhotoVersion") != null ||
                           GetAttributeValue(desc, OpCamera, "MotionPhotoOwner") != null;
            bool hasVivo = GetAttributeValue(desc, VCamera, "VMotionPhotoVersion") != null;

            info.IsLivePhoto = hasMicroVideo || hasMotionPhoto || hasOppo || hasVivo;

            if (hasOppo)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_OPPO");
            else if (hasVivo)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_Vivo");
            else if (hasMicroVideo)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_MicroVideoV1");
            else if (hasMotionPhoto)
                info.DetectedProtocol = ResourceService.GetString("History_Protocol_MotionPhotoV2");

            // ── 旧版文件补 Merge 条目（仅属性、无历史条目的情况）──────
            // v2.1.x 及更早只写 LivePhotoBox:Action="Merge" / Protocol / Version 属性，
            // 没有 dc:subject 历史条目，此时补一条 Merge 展示；
            // 新版文件（有 Split/Repair/Cover 等条目）绝不补，避免凭空多算一次"合并"。
            string? legacyAction = GetAttributeValue(desc, LivePhotoBoxNs, "Action");
            bool hasLegacyProtocol = GetAttributeValue(desc, LivePhotoBoxNs, "Protocol") != null;
            bool isLegacyMerge = (legacyAction == null && hasLegacyProtocol)
                || string.Equals(legacyAction, "Merge", StringComparison.OrdinalIgnoreCase);
            if (info.IsLivePhotoBoxGenerated && info.Entries.Count == 0 && isLegacyMerge)
            {
                var mergeEntry = new HistoryEntry
                {
                    Action = "Merge",
                    Version = info.MergeVersion,
                    Description = ResourceService.Format("History_MergeDesc", info.MergeProtocol),
                };

                // Merge 最先发生，插到开头。
                info.Entries.Insert(0, mergeEntry);
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
            {
                info.Summary = info.Entries.Count switch
                {
                    > 1 => ResourceService.Format("History_Summary_Generated", info.Entries.Count),
                    1 => info.Entries[0].ActionDisplayName, // 合成/拆分/修复/封面
                    _ => ResourceService.GetString("History_Summary_MergeOnly"),
                };
            }
            else if (info.IsLivePhoto)
                info.Summary = ResourceService.GetString("History_Summary_LivePhoto");

            return info;
        }

        // 把 Core 解析出的 HistoryRecord 映射为历史页展示用的 HistoryEntry。
        private static HistoryEntry ToHistoryEntry(Models.HistoryRecord record)
        {
            var entry = new HistoryEntry
            {
                Action = record.Action,
                Timestamp = record.Timestamp,
                Version = record.Version,
                Description = record.Description,
            };
            foreach (var (key, value) in record.Details)
            {
                entry.Details[key] = value;
            }
            return entry;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        // 获取 XML 元素的指定命名空间属性值。
        private static string? GetAttributeValue(XElement element, XNamespace ns, string localName)
        {
            var attr = element.Attribute(ns + localName);
            return attr?.Value;
        }

        // 判断文件是否为支持的图片格式（jpg/jpeg/heic/heif/png）。
        private static bool IsSupportedImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".heic" or ".heif" or ".png";
        }

        // 判断文件是否为 HEIC/HEIF（华为合并型 HEIC 需要字节级 XMP 读取回退）。
        private static bool IsHeicFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".heic" or ".heif";
        }
    }
}
