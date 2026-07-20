using System.Diagnostics;

// ═══════════════════════════════════════════════════════
// ThumbnailService 压力测试（独立版 — 直接调用底层 API）
// 用法: ThumbnailStressTest <文件夹路径> [并发数] [目标尺寸]
// ═══════════════════════════════════════════════════════

if (args.Length == 0)
{
    Console.WriteLine("用法: ThumbnailStressTest <文件夹路径> [并发数] [目标尺寸]");
    Console.WriteLine("示例: ThumbnailStressTest \"D:\\Pictures\" 8 112");
    return 1;
}

string dirPath = args[0];
int concurrency = args.Length > 1 ? int.Parse(args[1]) : 8;
uint targetSize = args.Length > 2 ? uint.Parse(args[2]) : 112;

if (!Directory.Exists(dirPath))
{
    Console.WriteLine($"[ERROR] 文件夹不存在: {dirPath}");
    return 1;
}

var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories)
    .Where(f => IsImageOrVideo(f))
    .ToArray();

Console.WriteLine($"文件夹: {dirPath}");
Console.WriteLine($"文件数: {files.Length}");
Console.WriteLine($"  HEIC: {files.Count(IsHeic)}");
Console.WriteLine($"  JPG:  {files.Count(f => IsExt(f, ".jpg", ".jpeg"))}");
Console.WriteLine($"  PNG:  {files.Count(f => IsExt(f, ".png"))}");
Console.WriteLine($"  视频: {files.Count(IsVideo)}");
Console.WriteLine($"并发: {concurrency}, 尺寸: {targetSize}px");
Console.WriteLine();

// ═══ 测试 1：顺序 ═══
Console.WriteLine($"═══ 测试 1：顺序加载（前 {Math.Min(30, files.Length)} 个文件）═══");
{
    var sw = Stopwatch.StartNew();
    int ok = 0, ng = 0;
    long totalMs = 0;
    for (int i = 0; i < Math.Min(30, files.Length); i++)
    {
        var f = files[i];
        var t = Stopwatch.StartNew();
        try
        {
            uint s = IsVideo(f) ? (uint)(targetSize * 1.5) : targetSize;
            if (IsHeic(f))
            {
                var data = LoadHeic(f, s);
                if (data is { Length: > 0 }) ok++; else ng++;
            }
            else if (!IsVideo(f))
            {
                var data = await LoadShellAsync(f, s);
                if (data is { Length: > 0 }) ok++; else ng++;
            }
            else ng++;
        }
        catch (Exception ex) { ng++; Console.WriteLine($"  [FAIL] {Path.GetFileName(f)}: {ex.GetType().Name}"); }
        t.Stop(); totalMs += t.ElapsedMilliseconds;
        if (ok + ng > 0 && (ok + ng) % 10 == 0)
            Console.Write($"\r  进度: {ok + ng}  ok={ok} fail={ng} avg={totalMs / (ok + ng)}ms");
    }
    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"✅ 测试 1: ok={ok} fail={ng} 总={sw.ElapsedMilliseconds}ms 均={totalMs / Math.Max(1, ok + ng)}ms/张");
    Console.WriteLine();
}

// ═══ 测试 2：并发 ═══
Console.WriteLine($"═══ 测试 2：{concurrency}路并发（前 {Math.Min(60, files.Length)} 个）═══");
{
    var sw = Stopwatch.StartNew();
    int ok = 0, ng = 0;
    var sem = new SemaphoreSlim(concurrency);
    var tasks = files.Take(60).Select(async f =>
    {
        await sem.WaitAsync();
        try
        {
            uint s = IsVideo(f) ? (uint)(targetSize * 1.5) : targetSize;
            if (IsHeic(f))
            {
                var data = await Task.Run(() => LoadHeic(f, s));
                if (data is { Length: > 0 }) Interlocked.Increment(ref ok);
                else Interlocked.Increment(ref ng);
            }
            else if (!IsVideo(f))
            {
                var data = await LoadShellAsync(f, s);
                if (data is { Length: > 0 }) Interlocked.Increment(ref ok);
                else Interlocked.Increment(ref ng);
            }
            else Interlocked.Increment(ref ng);
        }
        catch { Interlocked.Increment(ref ng); }
        finally { sem.Release(); }
    }).ToArray();
    await Task.WhenAll(tasks);
    sw.Stop();
    Console.WriteLine($"✅ 测试 2: ok={ok} fail={ng} 总={sw.ElapsedMilliseconds}ms 均={sw.ElapsedMilliseconds / (double)(ok + ng):F1}ms/张");
    Console.WriteLine();
}

// ═══ 测试 3：快速滚动 ═══
Console.WriteLine("═══ 测试 3：快速滚动模拟（10批×4个，间隔5ms，取消旧请求）═══");
{
    int done = 0, cancel = 0;
    for (int batch = 0; batch < 10 && batch * 4 < files.Length; batch++)
    {
        var batchFiles = files.Skip(batch * 4).Take(4).Where(IsHeic).ToArray();
        if (batchFiles.Length == 0) continue;
        await Task.WhenAll(batchFiles.Select(async f =>
        {
            try
            {
                LoadHeic(f, targetSize);
                Interlocked.Increment(ref done);
            }
            catch { Interlocked.Increment(ref cancel); }
        }));
        await Task.Delay(5);
    }
    Console.WriteLine($"✅ 测试 3: done={done} fail={cancel}");
    Console.WriteLine();
}

// ═══ 测试 4：重复加载 ═══
Console.WriteLine("═══ 测试 4：同 HEIC 文件 10 次重复加载 ═══");
{
    var heic = files.FirstOrDefault(IsHeic);
    if (heic != null)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) LoadHeic(heic, targetSize);
        sw.Stop();
        Console.WriteLine($"✅ 测试 4: 10次={sw.ElapsedMilliseconds}ms 均={sw.ElapsedMilliseconds / 10.0:F1}ms/张");
    }
    else Console.WriteLine("⚠ 无 HEIC 文件");
    Console.WriteLine();
}

Console.WriteLine("═══ 全部测试完成 ═══");
return 0;

// ── helpers ──
static bool IsHeic(string p) => IsExt(p, ".heic", ".heif");
static bool IsVideo(string p) => IsExt(p, ".mp4", ".mov");
static bool IsImageOrVideo(string p) => IsExt(p, ".heic", ".heif", ".jpg", ".jpeg", ".png", ".mp4", ".mov");
static bool IsExt(string p, params string[] exts)
{
    string e = Path.GetExtension(p).ToLowerInvariant();
    foreach (var x in exts) if (e == x) return true;
    return false;
}

static byte[] LoadHeic(string path, uint size)
{
    using var img = new ImageMagick.MagickImage(path);
    img.AutoOrient();
    img.Strip();
    img.Sample(size, size);
    img.Format = ImageMagick.MagickFormat.Jpeg;
    return img.ToByteArray();
}

static async Task<byte[]?> LoadShellAsync(string path, uint size)
{
    try
    {
        var f = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var t = await f.GetThumbnailAsync(
            Windows.Storage.FileProperties.ThumbnailMode.ListView, size,
            Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
        if (t is { Size: > 0 })
        {
            var ms = new MemoryStream();
            await t.AsStream().CopyToAsync(ms);
            return ms.ToArray();
        }
    }
    catch { }
    return null;
}
