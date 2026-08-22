using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 统一的 LivePhotoBox XMP 标识与历史写入服务。
    /// 负责：3 段版本号、LivePhotoBox 命名空间属性（Version/Timestamp）、
    /// dc:subject 历史条目（LivePhotoBox:{action}@{timestamp}@v{version}@{details}）的
    /// 读取、合并、去重与写入。JPEG / HEIC / MP4 / MOV 通用；
    /// 华为合并型 HEIC 等 exiftool 无法重写的文件由写入失败自动跳过（返回 false）。
    /// </summary>
    public static class XmpMarkerService
    {
        /// <summary>LivePhotoBox 命名空间 URI（识别文件是否由本工具处理的唯一标识）。</summary>
        public const string NamespaceUri = "https://github.com/LengxiQwQ/live-photo-box";

        /// <summary>历史条目前缀。</summary>
        public const string EntryPrefix = "LivePhotoBox:";

        private const string XmpPacketBegin =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>";

        private const string XmpPacketEnd = "<?xpacket end=\"w\"?>";

        private static readonly XNamespace RdfNs =
            "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        private static readonly XNamespace DcNs =
            "http://purl.org/dc/elements/1.1/";

        private static readonly XNamespace ContainerNs =
            "http://ns.google.com/photos/1.0/container/";

        private static readonly XNamespace ItemNs =
            "http://ns.google.com/photos/1.0/container/item/";

        private static readonly XNamespace HdrgmNs =
            "http://ns.adobe.com/hdr-gain-map/1.0/";

        private static readonly XNamespace LpbNs = NamespaceUri;

        private static readonly string _appVersion = GetAppVersion();

        /// <summary>当前应用版本（3 段，如 2.2.1）。</summary>
        public static string AppVersion => _appVersion;

        private static string GetAppVersion()
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version
                ?? Assembly.GetExecutingAssembly().GetName().Version;
            if (version == null) return "0.0.0";
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        /// <summary>
        /// 构造详细历史条目：LivePhotoBox:{action}@{timestamp}@v{version}@{details}。
        /// </summary>
        public static string BuildEntry(string action, DateTimeOffset timestamp, string details)
            => $"{EntryPrefix}{action}@{timestamp:yyyy-MM-ddTHH:mm:sszzz}@v{AppVersion}@{details}";

        /// <summary>
        /// 构造轻量历史条目：LivePhotoBox:{action}@@v{version}@（详细记录关闭时使用）。
        /// </summary>
        public static string BuildLightweightEntry(string action)
            => $"{EntryPrefix}{action}@@v{AppVersion}@";

        /// <summary>
        /// 构造机器可读的结构化详细信息：Key=Value;Key=Value...
        /// 空值字段自动跳过；值中不允许出现 ';' 或 '='，避免破坏格式。
        /// 字段顺序按传入顺序固定，新增字段往后追加即可（可扩展）。
        /// </summary>
        public static string BuildDetails(params (string Key, string Value)[] fields)
        {
            var parts = new List<string>();
            foreach (var (key, value) in fields)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
                if (value.Contains(';') || value.Contains('=')) continue;
                parts.Add($"{key}={value}");
            }
            return string.Join(";", parts);
        }

        /// <summary>
        /// 检测源文件的实况协议（用于历史记录 Source 字段）。
        /// 返回协议 key（如 MotionPhotoV2 / HuaweiMovingPhoto / Apple），无协议返回 None，
        /// 无法识别返回 Unknown。
        /// companionVideoPath：可选的配对视频路径。传入时按双文件检测——
        /// 内容标记优先（XMP / 尾标），内容无法识别时用图片+视频的
        /// ContentIdentifier UUID 配对判断 Apple Live Photo。
        /// </summary>
        public static async Task<string> DetectSourceProtocolAsync(
            string filePath, CancellationToken token, string? companionVideoPath = null)
        {
            try
            {
                // exiftool 的 WorkingDirectory 是工具目录，相对路径会解析失败，
                // 统一转成绝对路径再检测（文件本身只读，不落盘）。
                string fullPath = Path.GetFullPath(filePath);
                string? companion = string.IsNullOrWhiteSpace(companionVideoPath)
                    ? null
                    : Path.GetFullPath(companionVideoPath);

                string? xmp = await ReadXmpTextAsync(fullPath, token);
                if (string.IsNullOrWhiteSpace(xmp) && companion == null)
                    return "None"; // 无 XMP 且无配对视频：确认普通照片，非实况

                bool isHeic = fullPath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
                              fullPath.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
                var type = companion != null
                    ? LivePhotoType.DualFile
                    : isHeic ? LivePhotoType.SingleFileHeic : LivePhotoType.SingleFileJpeg;

                // 先按内容标记检测（XMP / 尾标，如 OPPO/vivo/华为 等有明确签名的协议）。
                var detected = LivePhotoProtocolDetector.Detect(
                    fullPath, type, contentIdentifier: null, xmp);

                // 内容无法识别时才尝试 Apple 双文件配对：图片与视频的
                // ContentIdentifier UUID 必须都存在且一致，避免单文件误判。
                if (detected == LivePhotoProtocolType.Unknown && companion != null)
                {
                    string? imageCid = await ReadContentIdentifierAsync(fullPath, token);
                    string? videoCid = await ReadContentIdentifierAsync(companion, token);
                    if (string.Equals(imageCid, videoCid, StringComparison.OrdinalIgnoreCase))
                    {
                        detected = LivePhotoProtocolDetector.Detect(
                            fullPath, type, imageCid, xmp);
                    }
                }

                // 无 XMP 且 CID 配对失败：确认是普通照片（有视频也不代表实况）。
                if (detected == LivePhotoProtocolType.Unknown &&
                    string.IsNullOrWhiteSpace(xmp))
                    return "None";

                return ProtocolKey(detected);
            }
            catch (OperationCanceledException) { throw; }
            catch { return "Unknown"; }
        }

        /// <summary>
        /// 读取文件的 Apple ContentIdentifier UUID（exiftool -ContentIdentifier）。
        /// 读取失败或不存在返回 null。
        /// </summary>
        private static async Task<string?> ReadContentIdentifierAsync(
            string filePath, CancellationToken token)
        {
            try
            {
                string? output = await RunExifToolCaptureAsync(
                    token, "-j", "-ContentIdentifier", filePath);
                if (string.IsNullOrWhiteSpace(output)) return null;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    return null;
                if (!root[0].TryGetProperty("ContentIdentifier", out var prop))
                    return null;
                return prop.ValueKind == JsonValueKind.String
                    ? prop.GetString()
                    : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// 将协议类型映射为稳定的协议 key（用于历史记录 Source/Target 字段）。
        /// </summary>
        public static string ProtocolKey(LivePhotoProtocolType type) => type switch
        {
            LivePhotoProtocolType.GoogleV1 => "MicroVideoV1",
            LivePhotoProtocolType.GoogleV2 => "MotionPhotoV2",
            LivePhotoProtocolType.OPPO => "OppoLivePhoto",
            LivePhotoProtocolType.Vivo => "VivoLivePhoto",
            LivePhotoProtocolType.Samsung => "SamsungMotionPhoto",
            LivePhotoProtocolType.Huawei => "HuaweiMovingPhoto",
            LivePhotoProtocolType.Apple => "Apple",
            LivePhotoProtocolType.Fusion => "MotionPhotoFusion",
            _ => "Unknown",
        };

        /// <summary>
        /// 合并历史条目（保留顺序 + 精确去重）。
        /// </summary>
        public static List<string> MergeEntries(IEnumerable<string>? existing, IEnumerable<string>? additional)
        {
            var result = new List<string>();
            foreach (var entry in (existing ?? Array.Empty<string>()).Concat(additional ?? Array.Empty<string>()))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                if (!result.Contains(entry, StringComparer.Ordinal))
                    result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// 读取文件 XMP dc:subject 中本软件的历史条目（LivePhotoBox: 前缀），保留顺序并去重。
        /// 读取失败或 exiftool 不可用时返回空列表（不抛异常）。
        /// </summary>
        public static async Task<List<string>> ReadExistingEntriesAsync(string filePath, CancellationToken token)
        {
            var entries = new List<string>();
            try
            {
                string? output = await RunExifToolCaptureAsync(token, "-j", "-XMP-dc:Subject", filePath);
                if (string.IsNullOrWhiteSpace(output)) return entries;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return entries;
                if (!root[0].TryGetProperty("Subject", out var subject)) return entries;

                void Collect(string value)
                {
                    if (value.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase))
                        entries.Add(value);
                }

                switch (subject.ValueKind)
                {
                    case JsonValueKind.String:
                        Collect(subject.GetString() ?? string.Empty);
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in subject.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                                Collect(item.GetString() ?? string.Empty);
                        }
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }

            return entries.Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// 合成页专用：读取源图片已有历史，合并一条新的 Merge 条目（Protocol=...），
        /// 嵌入到协议生成的 XMP 字节中（保留协议原有全部 XMP 内容）。
        /// </summary>
        public static async Task<byte[]> EmbedMergeHistoryAsync(
            string sourcePath, byte[] xmpBytes, string details, CancellationToken token)
        {
            var inherited = await ReadExistingEntriesAsync(sourcePath, token);
            var newEntry = BuildEntry("Merge", DateTimeOffset.Now, details);
            byte[] result = EmbedEntries(xmpBytes, MergeEntries(inherited, new[] { newEntry }));

            // Ultra HDR：源图 XMP 若带 GainMap Container 项（Google Ultra HDR / ISO 21496-1），
            // 把该项与 hdrgm 命名空间合并进新 XMP，保证单段 XMP 下 HDR 增益图仍可识别。
            string? sourceXmp = await ReadXmpTextAsync(sourcePath, token);
            if (!string.IsNullOrWhiteSpace(sourceXmp) &&
                sourceXmp.Contains("GainMap", StringComparison.Ordinal))
            {
                result = EmbedGainMapFromSource(sourceXmp, result);
            }

            return result;
        }

        /// <summary>
        /// 华为合成专用：生成完整 XMP 字节（命名空间属性 Action/Protocol/Version/Timestamp +
        /// dc:subject 历史，含源图旧历史迁移与新的 Merge 条目）。华为协议本身不生成协议 XMP，
        /// 由 HeicXmpInjector 或 JPEG 前置段使用。
        /// </summary>
        public static async Task<byte[]> BuildHuaweiMergeXmpAsync(
            string sourcePath, string details, CancellationToken token)
        {
            var inherited = await ReadExistingEntriesAsync(sourcePath, token);
            var newEntry = BuildEntry("Merge", DateTimeOffset.Now, details);
            return BuildFreshXmp(MergeEntries(inherited, new[] { newEntry }));
        }

        /// <summary>
        /// 将历史条目作为 dc:subject 嵌入既有 XMP 字节（保留原有全部 XMP 内容），
        /// 缺失时补充 LivePhotoBox:Timestamp 属性。返回重新包装后的 XMP 字节。
        /// </summary>
        public static byte[] EmbedEntries(byte[] xmpBytes, IEnumerable<string>? entries)
        {
            string xmpText = Encoding.UTF8.GetString(xmpBytes);
            XDocument doc = ParseXmp(xmpText);
            var desc = doc.Descendants(RdfNs + "Description").FirstOrDefault()
                ?? throw new InvalidDataException("XMP has no rdf:Description element.");

            // 命名空间属性只保留 Version，并补充缺失的 Timestamp；
            // 旧文件的 Action/Protocol 属性在此迁移时删除（信息已进 dc:subject 历史）。
            desc.SetAttributeValue(XNamespace.Xmlns + "LivePhotoBox", NamespaceUri);
            desc.SetAttributeValue(LpbNs + "Version", AppVersion);
            // 旧文件的 Action / Protocol 属性在此迁移时删除（信息已进 dc:subject 历史）。
            desc.Attribute(LpbNs + "Action")?.Remove();
            desc.Attribute(LpbNs + "Protocol")?.Remove();
            if (desc.Attribute(LpbNs + "Timestamp") == null)
                desc.SetAttributeValue(LpbNs + "Timestamp", NowString());

            SetSubjectEntries(desc, MergeEntries(ExtractSubjectEntries(desc), entries));
            return BuildXmpBytes(doc);
        }

        /// <summary>
        /// 统一的标识写入入口（拆分 / 修复等）：读取目标文件现有 XMP（保留其余内容），
        /// 合并传入的历史条目 + 新条目，写入命名空间属性与 dc:subject。
        /// 文件原本没有 XMP 时创建最小 XMP。失败（如华为合并型 HEIC 无法写入）返回 false。
        /// </summary>
        public static async Task<bool> TryWriteUnifiedMarkerAsync(
            string filePath, string action, string details,
            CancellationToken token, IEnumerable<string>? inheritedEntries = null)
        {
            if (string.IsNullOrEmpty(ExternalToolLocator.FindExifTool()))
                return false;

            try
            {
                bool detailed = AppSettingsService.GetValue("IsDetailedHistoryEnabled", true);
                string newEntry = detailed
                    ? BuildEntry(action, DateTimeOffset.Now, details)
                    : BuildLightweightEntry(action);

                string? existing = await ReadXmpTextAsync(filePath, token);
                byte[] xmpBytes;
                if (string.IsNullOrWhiteSpace(existing))
                {
                    xmpBytes = BuildFreshXmp(MergeEntries(inheritedEntries, new[] { newEntry }));
                }
                else
                {
                    XDocument doc = ParseXmp(existing);
                    var desc = doc.Descendants(RdfNs + "Description").FirstOrDefault();
                    if (desc == null) return false;

                    // Version/Timestamp 记录"最近一次操作"；Action/Protocol 已移除，信息存于 dc:subject 历史条目。
                    desc.SetAttributeValue(XNamespace.Xmlns + "LivePhotoBox", NamespaceUri);
                    desc.SetAttributeValue(LpbNs + "Version", AppVersion);
                    desc.SetAttributeValue(LpbNs + "Timestamp", NowString());
                    desc.Attribute(LpbNs + "Action")?.Remove();
                    desc.Attribute(LpbNs + "Protocol")?.Remove();

                    var merged = MergeEntries(ExtractSubjectEntries(desc), inheritedEntries);
                    merged = MergeEntries(merged, new[] { newEntry });
                    SetSubjectEntries(desc, merged);
                    xmpBytes = BuildXmpBytes(doc);
                }

                await WriteXmpViaExifToolAsync(filePath, xmpBytes, token);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        /// <summary>
        /// 读取文件当前完整 XMP 文本（exiftool -xmp:all -b），无 XMP 时返回 null。
        /// </summary>
        public static async Task<string?> ReadXmpTextAsync(string filePath, CancellationToken token)
            => await RunExifToolCaptureAsync(token, "-xmp", "-b", filePath);

        /// <summary>
        /// 从源图 XMP 中提取 GainMap Container 项与 hdrgm 命名空间属性，嵌入目标 XMP 字节。
        /// 源图无 GainMap 或解析失败时原样返回目标字节。
        /// </summary>
        private static byte[] EmbedGainMapFromSource(string sourceXmpText, byte[] targetXmpBytes)
        {
            XDocument sourceDoc;
            XDocument targetDoc;
            try { sourceDoc = ParseXmp(sourceXmpText); } catch { return targetXmpBytes; }
            try { targetDoc = ParseXmp(Encoding.UTF8.GetString(targetXmpBytes)); } catch { return targetXmpBytes; }

            var sourceDesc = sourceDoc.Descendants(RdfNs + "Description").FirstOrDefault();
            var targetDesc = targetDoc.Descendants(RdfNs + "Description").FirstOrDefault();
            if (sourceDesc == null || targetDesc == null) return targetXmpBytes;

            bool changed = false;

            // 1. hdrgm 命名空间属性（xmlns:hdrgm + hdrgm:*）复制到目标 rdf:Description。
            foreach (var attr in sourceDesc.Attributes().ToList())
            {
                if (attr.Name.NamespaceName == HdrgmNs && targetDesc.Attribute(attr.Name) == null)
                {
                    targetDesc.SetAttributeValue(attr.Name, attr.Value);
                    changed = true;
                }
                else if (attr.IsNamespaceDeclaration &&
                         attr.Name.LocalName == "hdrgm" &&
                         targetDesc.Attribute(attr.Name) == null)
                {
                    targetDesc.SetAttributeValue(attr.Name, attr.Value);
                    changed = true;
                }
            }

            // 2. GainMap Container 项复制到目标 Container:Directory（Primary 之后、MotionPhoto 之前）。
            var sourceGainMap = sourceDesc.Descendants(ContainerNs + "Directory")
                .Elements(RdfNs + "Seq")
                .SelectMany(seq => seq.Elements(RdfNs + "li"))
                .FirstOrDefault(IsGainMapItem);
            if (sourceGainMap != null)
            {
                var targetSeq = targetDesc.Descendants(ContainerNs + "Directory")
                    .Elements(RdfNs + "Seq")
                    .FirstOrDefault();
                if (targetSeq != null)
                {
                    var motionLi = targetSeq.Elements(RdfNs + "li").FirstOrDefault(IsMotionPhotoItem);
                    var clone = new XElement(sourceGainMap);
                    if (motionLi != null) motionLi.AddBeforeSelf(clone);
                    else targetSeq.Add(clone);
                    changed = true;
                }
            }

            return changed ? BuildXmpBytes(targetDoc) : targetXmpBytes;
        }

        private static bool IsGainMapItem(XElement li)
            => li.Descendants(ContainerNs + "Item")
                .Any(item => (string?)item.Attribute(ItemNs + "Semantic") == "GainMap");

        private static bool IsMotionPhotoItem(XElement li)
            => li.Descendants(ContainerNs + "Item")
                .Any(item => (string?)item.Attribute(ItemNs + "Semantic") == "MotionPhoto");

        /// <summary>
        /// 构造全新的最小 XMP（命名空间属性 + dc:subject 历史）。
        /// </summary>
        private static byte[] BuildFreshXmp(List<string> entries)
        {
            var xmpmeta = XNamespace.Get("adobe:ns:meta/") + "xmpmeta";
            var desc = new XElement(RdfNs + "Description",
                new XAttribute(RdfNs + "about", ""),
                new XAttribute(XNamespace.Xmlns + "LivePhotoBox", NamespaceUri),
                new XAttribute(LpbNs + "Version", AppVersion),
                new XAttribute(LpbNs + "Timestamp", NowString()));

            var doc = new XDocument(
                new XElement(xmpmeta,
                    new XAttribute(XNamespace.Xmlns + "x", "adobe:ns:meta/"),
                    new XElement(RdfNs + "RDF",
                        new XAttribute(XNamespace.Xmlns + "rdf", RdfNs.ToString()),
                        desc)));

            SetSubjectEntries(desc, entries);
            return BuildXmpBytes(doc);
        }

        /// <summary>
        /// 在 rdf:Description 上替换 dc:subject 数组（rdf:Seq），保留既有其他元素。
        /// </summary>
        private static void SetSubjectEntries(XElement desc, IEnumerable<string> entries)
        {
            desc.Element(DcNs + "subject")?.Remove();
            var subject = new XElement(DcNs + "subject",
                new XAttribute(XNamespace.Xmlns + "dc", DcNs.ToString()),
                new XElement(RdfNs + "Seq",
                    entries.Select(e => new XElement(RdfNs + "li", e))));
            desc.Add(subject);
        }

        /// <summary>
        /// 提取 rdf:Description 下 dc:subject 中本软件的历史条目。
        /// </summary>
        private static List<string> ExtractSubjectEntries(XElement desc)
        {
            var result = new List<string>();
            var subject = desc.Element(DcNs + "subject");
            if (subject == null) return result;
            foreach (var li in subject.Descendants(RdfNs + "li"))
            {
                string value = li.Value;
                if (value.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase))
                    result.Add(value);
            }
            return result;
        }

        /// <summary>
        /// 解析 XMP 文本为 XDocument：裁掉 xpacket 尾部填充、剔除非法控制字符、
        /// 移除 xpacket 处理指令，解析失败时回退到 rdf:RDF 片段。
        /// </summary>
        private static XDocument ParseXmp(string xmpText)
        {
            int xpacketEnd = xmpText.LastIndexOf("<?xpacket end=", StringComparison.Ordinal);
            if (xpacketEnd >= 0)
            {
                int closeTag = xmpText.IndexOf('>', xpacketEnd);
                if (closeTag >= 0) xmpText = xmpText[..(closeTag + 1)];
            }

            var cleaned = new StringBuilder(xmpText.Length);
            foreach (char c in xmpText)
            {
                if (c >= ' ' || c == '\t' || c == '\r' || c == '\n')
                    cleaned.Append(c);
            }
            string text = cleaned.ToString();

            XDocument doc;
            try
            {
                doc = XDocument.Parse(text);
            }
            catch
            {
                int rdfStart = text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
                int rdfEnd = text.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal);
                if (rdfStart < 0 || rdfEnd <= rdfStart)
                    throw new InvalidDataException("Failed to parse XMP document.");
                doc = XDocument.Parse(text[rdfStart..(rdfEnd + "</rdf:RDF>".Length)]);
            }

            // 去掉 xpacket 处理指令，统一由 BuildXmpBytes 重新包装。
            foreach (var pi in doc.Nodes().OfType<XProcessingInstruction>().ToList())
                pi.Remove();
            return doc;
        }

        /// <summary>
        /// 序列化 XDocument 并用标准 xpacket 包装为 XMP 字节。
        /// </summary>
        private static byte[] BuildXmpBytes(XDocument doc)
        {
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            var sb = new StringBuilder();
            sb.Append(XmpPacketBegin).Append('\n');
            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.Save(writer);
            }
            sb.Append('\n').Append(XmpPacketEnd);
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>
        /// 通过 exiftool -xmp&lt;= 原子替换文件 XMP：先写临时 .xmp 与临时输出，成功后覆盖目标。
        /// </summary>
        private static async Task WriteXmpViaExifToolAsync(string filePath, byte[] xmpBytes, CancellationToken token)
        {
            string dir = Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory;
            string ext = Path.GetExtension(filePath);
            string tempXmp = Path.Combine(dir, $".lpb_xmp_{Guid.NewGuid():N}.xmp");
            string tempOut = Path.Combine(dir, $".lpb_out_{Guid.NewGuid():N}{ext}");
            try
            {
                await File.WriteAllBytesAsync(tempXmp, xmpBytes, token);
                await LivePhotoRepairService.RunExifToolAsync(token,
                    $"-xmp<={tempXmp}",
                    "-o", tempOut,
                    filePath);
                if (!File.Exists(tempOut))
                    throw new InvalidDataException("exiftool did not produce output file.");
                File.Move(tempOut, filePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempXmp)) File.Delete(tempXmp); } catch { }
                try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
            }
        }

        /// <summary>
        /// 运行 exiftool 并捕获 stdout（stdin 管道 UTF-8 传参，兼容任意语言文件名）。
        /// 失败返回 null；取消时抛 OperationCanceledException。
        /// </summary>
        private static async Task<string?> RunExifToolCaptureAsync(CancellationToken token, params string[] args)
        {
            string? exifPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifPath)) return null;
            string toolDir = Path.GetDirectoryName(exifPath) ?? AppContext.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = exifPath,
                WorkingDirectory = toolDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("filename=utf8");
            psi.ArgumentList.Add("-@");
            psi.ArgumentList.Add("-");

            using var process = Process.Start(psi);
            if (process == null) return null;
            using var ctr = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            });

            try
            {
                foreach (var arg in args)
                    await process.StandardInput.WriteLineAsync(arg);
                await process.StandardInput.WriteLineAsync("-execute");
                process.StandardInput.Close();

                var sb = new StringBuilder();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string? line = await process.StandardOutput.ReadLineAsync(token);
                    if (line == null) break;
                    if (line.TrimEnd() == "{ready}") break;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                }

                await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                return sb.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static string NowString()
            => DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    }
}
