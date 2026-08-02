/*
 * LivePhotoBoxStressTest — 模拟 EditPage 真实扫描链路的完整压力测试
 *
 * 真实扫描不只是枚举文件名，还包括:
 *   1. 文件枚举
 *   2. JPEG XMP 标记检测 (识别实况照片协议)
 *   3. HEIC ftyp box 检测
 *   4. JPG/HEIC ↔ MOV 配对 (ContentIdentifier UUID 匹配)
 *   5. exiftool 批量查询元数据 (分辨率/日期/相机型号/协议标签)
 *
 * 用法: dotnet run -c Release -- --dir <path> [--mode full|scan|switch|timeline]
 */

using System.Diagnostics;

string? testDir = null;
int iterations = 3; // scan 默认只跑 3 轮（因为有CID匹配，很慢）
int delayMs = 100;
string mode = "scan";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--dir" && i + 1 < args.Length) testDir = args[++i];
    else if (args[i] == "--iterations" && i + 1 < args.Length) iterations = int.Parse(args[++i]);
    else if (args[i] == "--delay" && i + 1 < args.Length) delayMs = int.Parse(args[++i]);
    else if (args[i] == "--mode" && i + 1 < args.Length) mode = args[++i];
}

if (testDir == null || !Directory.Exists(testDir))
{
    Console.WriteLine("Usage: dotnet run -c Release -- --dir <path> [--mode scan|switch|timeline|full] [--iterations N]");
    return 1;
}

// Tool paths — check output Tools/ dir, then source Tools/, then PATH
string exifTool = "exiftool.exe";
string ffmpeg = "ffmpeg.exe";
string[] searchDirs = {
    Path.Combine(AppContext.BaseDirectory, "Tools"),
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../LivePhotoBox/Tools"))
};
foreach (var d in searchDirs) {
    if (!File.Exists(exifTool)) { var p = Path.Combine(d, "exiftool.exe"); if (File.Exists(p)) exifTool = p; }
    if (!File.Exists(ffmpeg)) { var p = Path.Combine(d, "ffmpeg.exe"); if (File.Exists(p)) ffmpeg = p; }
}

Console.WriteLine("==============================================");
Console.WriteLine("  Live Photo Box - FULL Scan Stress Test");
Console.WriteLine("==============================================");
Console.WriteLine($"  Dir:       {testDir}");
Console.WriteLine($"  Mode:      {mode}");
Console.WriteLine($"  Iters:     {iterations}");
Console.WriteLine($"  exiftool:  {(File.Exists(exifTool) ? "OK" : "NO")}");
Console.WriteLine($"  ffmpeg:    {(File.Exists(ffmpeg) ? "OK" : "NO")}");
Console.WriteLine();

// Supported extensions
var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".heic", ".heif", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp" };
var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".mov", ".mp4" };

var overallSw = Stopwatch.StartNew();
var allPhaseTimings = new List<(string Phase, long Ms, string Detail)>();

// ═══════════════════════════════════════════════════
//  MODE: header — 模拟 FastMetadataReader，C# 二进制读宽高+EXIF日期
// ═══════════════════════════════════════════════════
if (mode == "header")
{
    Console.WriteLine("=== C# Binary Header Read Test (FastMetadataReader simulation) ===");
    Console.WriteLine();

    // 枚举文件
    var swEnum = Stopwatch.StartNew();
    var imagePaths = new List<string>();
    foreach (var f in Directory.EnumerateFiles(testDir))
        if (imageExts.Contains(Path.GetExtension(f)))
            imagePaths.Add(f);
    Console.WriteLine($"  Enumeration: {swEnum.ElapsedMilliseconds}ms ({imagePaths.Count} images)");
    Console.WriteLine();

    for (int iter = 0; iter < iterations; iter++)
    {
        int okW = 0, fail = 0;
        var sw = Stopwatch.StartNew();

        // 模拟 FastMetadataReader: 读文件头取宽高+日期
        Parallel.ForEach(imagePaths, new ParallelOptions { MaxDegreeOfParallelism = 8 }, filePath =>
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (ext is ".jpg" or ".jpeg")
                {
                    int len = (int)Math.Min(fs.Length, 65536);
                    byte[] buf = new byte[len];
                    fs.ReadExactly(buf, 0, len);
                    if (buf.Length >= 4 && buf[0] == 0xFF && buf[1] == 0xD8)
                    {
                        int w = 0, pos = 2;
                        while (pos < len - 1)
                        {
                            if (buf[pos] != 0xFF) { pos++; continue; }
                            byte marker = buf[pos + 1];
                            if (marker == 0xFF) { pos++; continue; }
                            pos += 2;
                            if (marker is 0xD9 or 0xDA) break;
                            if (pos + 2 > len) break;
                            int segLen = (buf[pos] << 8) | buf[pos + 1];
                            if (segLen < 2) break;
                            pos += segLen;
                            if (marker is 0xC0 or 0xC1 or 0xC2 && pos - segLen + 8 <= len)
                            {
                                int dataStart = pos - segLen + 2;
                                if (dataStart + 7 <= len)
                                {
                                    int h = (buf[dataStart + 1] << 8) | buf[dataStart + 2];
                                    w = (buf[dataStart + 3] << 8) | buf[dataStart + 4];
                                }
                            }
                            if (w > 0) break;
                        }
                        if (w > 0) Interlocked.Increment(ref okW);
                        else Interlocked.Increment(ref fail);
                    }
                }
                else if (ext == ".png")
                {
                    Span<byte> hdr = stackalloc byte[24];
                    fs.ReadExactly(hdr);
                    if (hdr[0] == 0x89 && hdr[1] == 'P' && hdr[2] == 'N' && hdr[3] == 'G')
                    {
                        int w = (hdr[16] << 24) | (hdr[17] << 16) | (hdr[18] << 8) | hdr[19];
                        if (w > 0) Interlocked.Increment(ref okW);
                        else Interlocked.Increment(ref fail);
                    }
                }
                else Interlocked.Increment(ref fail); // HEIC/GIF/BMP/WebP — 跳过
            }
            catch { Interlocked.Increment(ref fail); }
        });

        Console.WriteLine($"  Iter {iter + 1}: {sw.ElapsedMilliseconds}ms  " +
            $"(ok={okW}, fail={fail})  [{imagePaths.Count} files, 8 threads]");
    }

    Console.WriteLine();
    Console.WriteLine("  === Compare with scan mode (exiftool on every file) ===");
    Console.WriteLine("  This test reads ONLY file headers in C#, no exiftool.");
    Console.WriteLine("  The real app also reads DateTimeOriginal from EXIF,");
    Console.WriteLine("  and queries ContentIdentifier via exiftool for DualFile only.");
}

if (mode is "scan" or "full")
{
    Console.WriteLine("=== REALISTIC Scan Test ===");
    Console.WriteLine("  (enumerate + XMP detect + HEIC detect + CID match + exiftool)");
    Console.WriteLine();

    for (int iter = 0; iter < iterations; iter++)
    {
        var iterSw = Stopwatch.StartNew();
        Console.WriteLine($"--- Iteration {iter + 1}/{iterations} ---");

        // ═══════════════════════════════════════════════════
        // Phase 1: File enumeration
        // ═══════════════════════════════════════════════════
        var p1 = Stopwatch.StartNew();
        var imagePaths = new List<string>();
        var videoPaths = new List<string>();
        foreach (var f in Directory.EnumerateFiles(testDir))
        {
            var ext = Path.GetExtension(f);
            if (imageExts.Contains(ext)) imagePaths.Add(f);
            else if (videoExts.Contains(ext)) videoPaths.Add(f);
        }
        allPhaseTimings.Add(("枚举", p1.ElapsedMilliseconds, $"{imagePaths.Count} imgs + {videoPaths.Count} vids"));
        Console.WriteLine($"  Phase 1 枚举:       {p1.ElapsedMilliseconds,5}ms  ({imagePaths.Count} images, {videoPaths.Count} videos)");

        // ═══════════════════════════════════════════════════
        // Phase 2: JPEG XMP marker detection + HEIC track detection
        // (和 QuickCheckLivePhoto 完全相同的逻辑)
        // ═══════════════════════════════════════════════════
        var p2 = Stopwatch.StartNew();
        int liveSingleFile = 0;  // SingleFileJpeg/SingleFileHeic (Google/OPPO)
        int liveHeic = 0;
        var livePhotoImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(imagePaths, filePath =>
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext is ".heic" or ".heif")
            {
                // HEIC: read ftyp box → check for video track (mdat + moov)
                try
                {
                    var buf = new byte[8192];
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    int len = fs.Read(buf, 0, buf.Length);
                    var span = buf.AsSpan(0, len);
                    if (span.IndexOf("mdat"u8) >= 0 && span.IndexOf("moov"u8) >= 0)
                    {
                        Interlocked.Increment(ref liveHeic);
                        lock (livePhotoImages) livePhotoImages.Add(filePath);
                    }
                }
                catch { }
            }
            else if (ext is ".jpg" or ".jpeg")
            {
                // JPEG: 读末尾 4KB 找 MP4 标记 (ftyp/moov) — Google/Oppo 内嵌视频
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (fs.Length > 4096)
                    {
                        fs.Seek(-Math.Min(fs.Length, 4096), SeekOrigin.End);
                        var buf = new byte[4096];
                        fs.ReadExactly(buf, 0, buf.Length);
                        var span = buf.AsSpan();
                        if (span.IndexOf("ftyp"u8) >= 0 || span.IndexOf("moov"u8) >= 0)
                        {
                            Interlocked.Increment(ref liveSingleFile);
                            lock (livePhotoImages) livePhotoImages.Add(filePath);
                        }
                    }

                    // 同时读头部 64KB 找 XMP 协议标记 (Container:Directory, MotionPhoto, GCamera)
                    // 这模拟了 LivePhotoSplitService.ReadMetadataTextSync
                    fs.Seek(0, SeekOrigin.Begin);
                    var headBuf = new byte[Math.Min(fs.Length, 65536)];
                    int headLen = fs.Read(headBuf, 0, headBuf.Length);
                    var headText = System.Text.Encoding.UTF8.GetString(headBuf, 0, headLen);
                    if (headText.Contains("Container:Directory") ||
                        headText.Contains("GCamera:MicroVideo") ||
                        headText.Contains("MotionPhoto"))
                    {
                        lock (livePhotoImages) livePhotoImages.Add(filePath);
                    }
                }
                catch { }
            }
        });

        allPhaseTimings.Add(("XMP检测", p2.ElapsedMilliseconds,
            $"SingleFile: {liveSingleFile}, HEIC track: {liveHeic}, total w/ markers: {livePhotoImages.Count}"));
        Console.WriteLine($"  Phase 2 XMP检测:    {p2.ElapsedMilliseconds,5}ms  (SingleFileJpeg={liveSingleFile}, HEIC={liveHeic}, total={livePhotoImages.Count})");

        // ═══════════════════════════════════════════════════
        // Phase 3: JPG/HEIC ↔ MOV 配对 (CID match by name)
        // 真实代码: LivePhotoMetadataMatcher.MatchAsync 用 exiftool 读 ContentIdentifier UUID
        // 这里我们用文件名精确配对 (Apple 导出格式: 同 basename)
        // ═══════════════════════════════════════════════════
        var p3 = Stopwatch.StartNew();
        var movByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in videoPaths)
            movByName[Path.GetFileNameWithoutExtension(v)] = v;

        int pairedByCid = 0;
        var dualFilePhotos = new List<(string Photo, string Video)>();

        // First: fast name-based pairing (大部分 Apple 导出都是同名的)
        foreach (var img in imagePaths)
        {
            var baseName = Path.GetFileNameWithoutExtension(img);
            if (movByName.TryGetValue(baseName, out var vid))
            {
                dualFilePhotos.Add((img, vid));
                pairedByCid++;
            }
        }

        allPhaseTimings.Add(("配对", p3.ElapsedMilliseconds, $"DualFile pairs: {pairedByCid}"));
        Console.WriteLine($"  Phase 3 文件配对:    {p3.ElapsedMilliseconds,5}ms  (DualFile pairs: {pairedByCid})");

        // ═══════════════════════════════════════════════════
        // Phase 4: exiftool batch metadata query (分辨率/日期/相机/协议)
        // 真实代码: ReadResolutionsAsync 用 PersistentExifTool 2线程池
        // 我们模拟同样的查询，但用独立 exiftool 进程
        // ═══════════════════════════════════════════════════
        var p4 = Stopwatch.StartNew();
        int exifOk = 0, exifFail = 0;
        int exifBatchSize = 4; // 4 并发
        var sem = new SemaphoreSlim(exifBatchSize);

        if (File.Exists(exifTool))
        {
            var exifTasks = new List<Task>();
            int processed = 0;
            var tags = "-j -ImageWidth -ImageHeight -Make -Model -DateTimeOriginal " +
                       "-MotionPhotoPresentationTimestampUs -MicroVideoPresentationTimestampUs " +
                       "-ContentIdentifier -MediaDuration -AvgBitrate";

            foreach (var img in imagePaths)
            {
                await sem.WaitAsync();
                exifTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var psi = new ProcessStartInfo(exifTool, $"{tags} \"{img}\"")
                        { UseShellExecute = false, CreateNoWindow = true,
                          RedirectStandardOutput = true, RedirectStandardError = true };
                        using var p = Process.Start(psi)!;
                        p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        Interlocked.Increment(ref exifOk);
                    }
                    catch { Interlocked.Increment(ref exifFail); }
                    finally { sem.Release(); }
                }));

                var done = Interlocked.Increment(ref processed);
                if (done % 500 == 0) Console.Write($"\r    exiftool: {done}/{imagePaths.Count}...");
            }
            await Task.WhenAll(exifTasks);
            Console.Write($"\r    exiftool: {imagePaths.Count}/{imagePaths.Count}   \n");
        }

        allPhaseTimings.Add(("exiftool", p4.ElapsedMilliseconds,
            $"OK={exifOk}, Fail={exifFail}"));
        Console.WriteLine($"  Phase 4 exiftool元数据: {p4.ElapsedMilliseconds,5}ms  ({exifOk} ok, {exifFail} fail)");

        // Summary for this iteration
        var totalIter = iterSw.ElapsedMilliseconds;
        allPhaseTimings.Add(("总计", totalIter,
            $"枚举+{liveSingleFile}+{liveHeic}+{pairedByCid}+{exifOk}"));
        Console.WriteLine($"  >>> Iteration total:  {totalIter / 1000.0:F1}s");
        Console.WriteLine();
    }
}

// ═══════════════════════════════════════════════════
// Report
// ═══════════════════════════════════════════════════
Console.WriteLine("==============================================");
Console.WriteLine("  COMPLETE SCAN STRESS TEST REPORT");
Console.WriteLine("==============================================");
Console.WriteLine($"  Total time: {overallSw.ElapsedMilliseconds / 1000.0:F1}s");
Console.WriteLine();

// Group by phase
var phases = allPhaseTimings.GroupBy(t => t.Phase);
foreach (var g in phases)
{
    var avg = g.Average(t => t.Ms);
    var min = g.Min(t => t.Ms);
    var max = g.Max(t => t.Ms);
    Console.WriteLine($"  {g.Key,-16} avg={avg,6:F0}ms  min={min,5}ms  max={max,5}ms  [{g.First().Detail}]");
}

Console.WriteLine();
Console.WriteLine("==============================================");
Console.WriteLine("  Notes:");
Console.WriteLine("  - Phase 2 reads file headers (64KB JPEG / 8KB HEIC)");
Console.WriteLine("  - Phase 3 pairs JPG/HEIC with MOV by base name");
Console.WriteLine("  - Phase 4 runs exiftool with -j on EVERY image file");
Console.WriteLine("  - This simulates LivePhotoDiscoveryService.ScanAsync");
Console.WriteLine("    with JpegMarkers + HeicTrack + CidMatch modes");
Console.WriteLine("==============================================");

return 0;
