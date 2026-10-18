using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using LivePhotoBox.Models;
using System.Diagnostics;

internal static partial class Program
{
    private static Action<string>? StageObserver;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static object FileIdentity(string path) => new { path, bytes = new FileInfo(path).Length, sha256 = Hash(path) };

    private static int Prepare(string samples, string root)
    {
        Directory.CreateDirectory(root);
        string source = Path.Combine(samples, "苹果双文件.MOV");
        foreach (var row in new[] { ("supported.mov", 128L * 1024 * 1024), ("over4g.mov", 4_500_005_000L) })
        {
            string path = Path.Combine(root, row.Item1);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Require(DeviceIoControl(output.SafeFileHandle, 0x000900C4, 0, 0, 0, 0, out _, 0), "NTFS sparse support required; no skip.");
                using (var input = File.OpenRead(source)) input.CopyTo(output);
                Span<byte> header = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(header, 1);
                "free"u8.CopyTo(header[4..]);
                BinaryPrimitives.WriteUInt64BigEndian(header[8..], checked((ulong)(row.Item2 - output.Length)));
                output.Write(header); output.SetLength(row.Item2); output.Flush(true);
            }
            Save(Path.Combine(root, row.Item1 + ".json"), new { category = "Real-derived synthetic container fixture",
                recipe = "Unchanged complete frozen Apple MOV followed by one legal extended-size top-level free box; sparse zero free payload. Original sample tables/media packet bytes and offsets unchanged.",
                original = FileIdentity(source), derived = FileIdentity(path), sparse = (File.GetAttributes(path) & FileAttributes.SparseFile) != 0 });
        }
        return 0;
    }

    private static async Task<int> InspectLarge(string[] args)
    {
        SetErrorMode(0x0001 | 0x0002); // Only this diagnostic process; no WER modal.
        string samples = Path.GetFullPath(args[1]), video = Path.GetFullPath(args[2]), destination = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(destination);
        string gate = Path.Combine(destination, "job-assigned.gate");
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(gate)) { if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Parent did not attach the bounded Windows Job."); await Task.Delay(10); }
        object before = FileIdentity(video);
        Save(Path.Combine(destination, "before.json"), new { input = before, resources = Snapshot(), gate });
        var attempts = new List<object>();
        string originalHash = Hash(video);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using InspectedSource inspected = await new SourceInspector().InspectWithPlanAsync(
                    attempt % 2 == 0 ? Path.Combine(samples, "苹果双文件.HEIC") : video,
                    attempt % 2 == 0 ? video : null);
                throw new InvalidDataException("This real resource-constrained scenario requires typed refusal.");
            }
            catch (SourceInspectionException error)
            {
                Require(error.Category == SourceInspectionFailureCategory.Unsupported && error.Stage == SourceInspectionStage.Read,
                    "Wrong allocation failure type.");
                using (File.Open(video, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                attempts.Add(new { attempt, role = attempt % 2 == 0 ? "secondary" : "primary", error.Category, error.Stage, error.Reason,
                    exclusiveSourceOpen = true, resources = Snapshot() });
            }
        }
        Require(Hash(video) == originalHash, "Large source changed during rejection.");
        Save(Path.Combine(destination, "inspection.json"), new { success = false, passed = true, attempts,
            before, after = FileIdentity(video), resources = Snapshot() });
        return 0;
    }

    private static async Task<int> Large(string[] args)
    {
        string samples = Path.GetFullPath(args[1]), video = Path.GetFullPath(args[2]), destination = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(destination);
        string before = Hash(video); string workspacePath;
        object record;
        using (var workspace = new MediaWorkspace())
        {
            workspacePath = workspace.RootDirectory;
            var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                Path.Combine(samples, "苹果双文件.HEIC"), video, workspace);
            Require(bundle.MotionVideo is not null, "Large real-derived source lost its motion video.");
            Validate(bundle);
            File.Copy(bundle.PrimaryImage.Path, Path.Combine(destination, "image.heic"));
            File.Copy(bundle.MotionVideo!.Path, Path.Combine(destination, "video.mov"));
            record = new { input = FileIdentity(video), bundle.SourceProvenance, bundle.Manifest, bundle.Semantics };
        }
        Require(!Directory.Exists(workspacePath), "Large production workspace remains.");
        Require(before == Hash(video), "Large source changed.");
        Save(Path.Combine(destination, "large.json"), new { passed = true, record, workspaceRemoved = true,
            image = FileIdentity(Path.Combine(destination, "image.heic")), video = FileIdentity(Path.Combine(destination, "video.mov")), resources = Snapshot() });
        return 0;
    }

    private static void Validate(NeutralMediaBundle bundle)
    {
        Require(bundle.Manifest.Count > 0, "No final artifact manifest.");
        foreach (var item in bundle.Manifest)
        {
            Require(File.Exists(item.Path), "Manifest artifact missing: " + item.Role);
            // Embedded semantic assets have owner ranges; their hash is not the
            // entire owner's hash. Materialized roles are checked as whole files.
            if (item.OwnerByteOffset is long offset && Path.GetFullPath(item.Path) == Path.GetFullPath(bundle.PrimaryImage.Path))
            {
                using var owner = File.OpenRead(item.Path);
                owner.Position = offset;
                long remaining = item.ByteLength;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] bytes = new byte[65536];
                while (remaining > 0)
                {
                    int count = owner.Read(bytes, 0, (int)Math.Min(bytes.Length, remaining));
                    Require(count > 0, "Embedded manifest range truncated.");
                    digest.AppendData(bytes, 0, count); remaining -= count;
                }
                Require(Convert.ToHexString(digest.GetHashAndReset()).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase), "Embedded manifest hash mismatch.");
                continue;
            }
            Require(new FileInfo(item.Path).Length == item.ByteLength, "Manifest byte length mismatch.");
            Require(Hash(item.Path).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase), "Manifest content hash mismatch.");
        }
    }

    private static readonly (string Image, string? Video, bool Unsupported)[] Routes =
    [
        ("红米老款-GV1.JPG", null, false), ("小米.jpg", null, true), ("oppo.jpg", null, false),
        ("一加.jpg", null, false), ("一加-改了封面照片.jpg", null, false), ("vivo.jpg", null, false),
        ("三星.jpg", null, false), ("三星.heic", null, false), ("华为-Mate80.jpg", null, false),
        ("华为Mate80.heic", null, false), ("荣耀.jpg", null, false),
        ("vivo双文件.jpg", "vivo双文件.mp4", false),
        ("苹果-双文件.JPG", "苹果-双文件.MOV", false), ("苹果双文件.HEIC", "苹果双文件.MOV", false)
    ];

    private static async Task<object> NeutralRoute(string samples, int index, string? retained)
    {
        var route = Routes[index]; string path;
        object result;
        using (var workspace = new MediaWorkspace())
        {
            path = workspace.RootDirectory;
            try
            {
                var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
                    Path.Combine(samples, route.Image), route.Video is null ? null : Path.Combine(samples, route.Video), workspace);
                Require(!route.Unsupported, "Expected explicit Xiaomi unsupported, got success.");
                Validate(bundle);
                // Neutral artifacts are independently usable; supplying them as
                // a new source pair requests pairing authority rather than media
                // validity. Match the accepted P6 final-primary validation.
                var facts = await new SourceInspector().InspectAsync(bundle.PrimaryImage.Path);
                Require(facts.Protocol == SourceProtocol.NonLive, "Neutral media still has source binding.");
                if (bundle.MotionVideo is not null)
                {
                    var motionFacts = await new SourceInspector().InspectAsync(bundle.MotionVideo.Path);
                    Require(motionFacts.Protocol == SourceProtocol.NonLive && motionFacts.MotionVideo is { IsPresent: true }, "Neutral motion media invalid.");
                }
                if (retained is not null)
                {
                    Directory.CreateDirectory(retained);
                    File.Copy(bundle.PrimaryImage.Path, Path.Combine(retained, "image" + Path.GetExtension(bundle.PrimaryImage.Path)));
                    if (bundle.MotionVideo is not null) File.Copy(bundle.MotionVideo.Path, Path.Combine(retained, "video" + Path.GetExtension(bundle.MotionVideo.Path)));
                }
                result = new { route.Image, outcome = "Neutral", bundle.Manifest, bundle.Semantics };
            }
            catch (NotSupportedException error) when (route.Unsupported)
            {
                Require(error.Message.Contains("final PrimaryImage orientation", StringComparison.OrdinalIgnoreCase), "Wrong unsupported boundary.");
                result = new { route.Image, outcome = "ApprovedUnsupported", error.Message };
            }
        }
        Require(!Directory.Exists(path), "Neutral workspace remains after disposal.");
        return result;
    }

    private static async Task<object> MergeBatch(string samples, string directory)
    {
        Directory.CreateDirectory(directory);
        string sharedTemp = Path.Combine(directory, "Temp"); Directory.CreateDirectory(sharedTemp);
        string foreign = Path.Combine(sharedTemp, "foreign.txt"); File.WriteAllText(foreign, "R4 third-party sentinel");
        string foreignHash = Hash(foreign);
        var pairs = Enumerable.Range(0, 4).Select(i => new Pair(i,
            Path.Combine(samples, "苹果-双文件.JPG"), Path.Combine(samples, "苹果-双文件.MOV"), "same-name")).ToArray();
        using var pause = new ManualResetEventSlim(true); int starts = 0, completions = 0;
        await LivePhotoMergeRunnerService.RunAsync(pairs,
            new LivePhotoMergeRunOptions { OutputDirectory = directory, SelectedModeIndex = 2,
                OutputFormatIndex = ProtocolFormatMatrix.FormatJpgMov, MaxDegreeOfParallelism = 3, TaskStartInterval = TimeSpan.Zero },
            pause, CancellationToken.None, _ => Interlocked.Increment(ref starts),
            (pair, success, details, _) => { pair.Status = success ? ProcessStatus.Success : ProcessStatus.Failed;
                pair.Details = details; Interlocked.Increment(ref completions); });
        Require(starts == 4 && completions == 4 && pairs.All(p => p.Status == ProcessStatus.Success),
            "Real merge batch did not complete four successes: " + string.Join(";", pairs.Select(p => p.Details)));
        string[] outputs = Directory.GetFiles(directory);
        Require(outputs.Length == 4 && outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4, "Batch collision lost/duplicated output.");
        Require(Hash(foreign) == foreignHash, "Batch removed or changed shared Temp object.");
        var identities = outputs.Select(FileIdentity).ToArray();
        foreach (string output in outputs)
        {
            var facts = await new SourceInspector().InspectAsync(output);
            Require(facts.MotionVideo is { IsPresent: true }, "Batch published no motion media.");
            File.Delete(output); // Exact test-owned output after identity checks.
        }
        File.Delete(foreign); Directory.Delete(sharedTemp); Directory.Delete(directory);
        return new { starts, completions, outputs = identities, thirdPartyTempPreserved = true };
    }

    private static async Task<object> SplitBatch(string samples, string directory)
    {
        Directory.CreateDirectory(directory);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => LivePhotoSplitService.SplitAsync(
            Path.Combine(samples, "红米老款-GV1.JPG"), directory,
            ProtocolFormatMatrix.SplitProtocolNone, ProtocolFormatMatrix.SplitFormatJpgMp4,
            CancellationToken.None, outputBaseName: "split-" + i)));
        Require(Directory.GetFiles(directory).Length == 6, "Split batch not exactly three pairs.");
        var identities = Directory.GetFiles(directory).Select(FileIdentity).ToArray();
        foreach (var result in results)
        {
            var facts = await new SourceInspector().InspectAsync(result.ImageOutputPath);
            Require(facts.Protocol == SourceProtocol.NonLive, "Split output is not neutral.");
            var videoFacts = await new SourceInspector().InspectAsync(result.VideoOutputPath);
            Require(videoFacts.Protocol == SourceProtocol.NonLive && videoFacts.MotionVideo is { IsPresent: true }, "Split video invalid.");
            File.Delete(result.ImageOutputPath); File.Delete(result.VideoOutputPath);
        }
        Directory.Delete(directory); return new { pairs = 3, outputs = identities };
    }

    private static async Task<object> CodecRound(string samples, string? retained)
    {
        using var workspace = new MediaWorkspace();
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(Path.Combine(samples, "华为-Mate80.jpg"), null, workspace);
        var heic = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = bundle.PrimaryImage, TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory
        });
        Require(heic.Success && heic.OutputArtifact is not null, "JPEG->HEIC: " + heic.ErrorMessage);
        StageObserver?.Invoke("heic");
        var jpeg = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = heic.OutputArtifact!, TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory
        });
        Require(jpeg.Success && jpeg.OutputArtifact is not null, "HEIC->JPEG: " + jpeg.ErrorMessage);
        StageObserver?.Invoke("jpeg");
        string apple = Path.Combine(samples, "苹果双文件.MOV");
        var sdr = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = apple, Kind = MediaArtifactKind.MotionVideo,
                VideoContainer = VideoContainer.Mov, VideoCodec = VideoCodec.Hevc, ByteLength = new FileInfo(apple).Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = VideoCodec.H264, TargetDirectory = workspace.RootDirectory
        });
        Require(sdr.Success && sdr.OutputArtifact is not null, "HEVC SDR: " + sdr.ErrorMessage);
        StageObserver?.Invoke("hevc-sdr");
        Require(sdr.ExecutionRecord.Truth.BackendName == "minimal-libav" &&
            sdr.ExecutionRecord.Truth.ActualCapability == ConversionCapability.VideoTranscodeSdr &&
            sdr.ExecutionRecord.HardwareMode == VideoHardwareMode.SoftwareForced && !sdr.ExecutionRecord.Truth.FallbackOccurred,
            "Wrong SDR backend truth.");
        string hdrSource = Path.Combine(FindRoot(), ".ai-tmp/workspace/P5/R5/huawei-extraction-pass-c/motion.mp4");
        Require(Hash(hdrSource) == "664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6", "HDR derived input identity.");
        var hdr = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = hdrSource, Kind = MediaArtifactKind.MotionVideo,
                VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.Hevc, ByteLength = new FileInfo(hdrSource).Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = VideoCodec.Hevc, TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        });
        Require(hdr.Success && hdr.OutputArtifact is not null, "Main10: " + hdr.ErrorMessage);
        StageObserver?.Invoke("hdr");
        Require(hdr.ExecutionRecord.Truth.BackendName == "minimal-libav" &&
            hdr.ExecutionRecord.Truth.ActualCapability == ConversionCapability.VideoTranscodeHdr10Bit &&
            !hdr.ExecutionRecord.Truth.FallbackOccurred, "Wrong HDR backend truth.");
        var hevc8 = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = apple, Kind = MediaArtifactKind.MotionVideo,
                VideoContainer = VideoContainer.Mov, VideoCodec = VideoCodec.Hevc, ByteLength = new FileInfo(apple).Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = VideoCodec.Hevc, TargetDirectory = workspace.RootDirectory
        });
        Require(hevc8.Success && hevc8.OutputArtifact is not null, "8-bit HEVC: " + hevc8.ErrorMessage);
        Require(hevc8.ExecutionRecord.Truth.BackendName == "minimal-libav" &&
            hevc8.ExecutionRecord.Truth.ActualCapability == ConversionCapability.VideoTranscodeSdr &&
            hevc8.ExecutionRecord.Truth.OutputProfile.Contains("lumaDepth=8;chromaDepth=8", StringComparison.Ordinal) &&
            !hevc8.ExecutionRecord.Truth.FallbackOccurred, "8-bit HEVC owner/depth differs.");
        StageObserver?.Invoke("hevc8");
        string vivo = Path.Combine(samples, "vivo双文件.mp4");
        var mf = await new VideoConverter().ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = new MediaArtifact { Path = vivo, Kind = MediaArtifactKind.MotionVideo,
                VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, ByteLength = new FileInfo(vivo).Length },
            TargetContainer = VideoContainer.Mp4, TargetCodec = VideoCodec.H264, TargetDirectory = workspace.RootDirectory
        });
        Require(mf.Success && mf.OutputArtifact is not null, "MF H264 control: " + mf.ErrorMessage);
        Require(mf.ExecutionRecord.Truth.BackendName == "WindowsMediaFoundation" &&
            mf.ExecutionRecord.Truth.ActualCapability == ConversionCapability.VideoTranscodeSdr &&
            mf.ExecutionRecord.HardwareMode == VideoHardwareMode.SoftwareForced && !mf.ExecutionRecord.Truth.FallbackOccurred,
            "H264 control did not retain the explicit MF software owner.");
        StageObserver?.Invoke("mf-h264");
        foreach (var artifact in new[] { heic.OutputArtifact!, jpeg.OutputArtifact!, sdr.OutputArtifact!, hdr.OutputArtifact!, hevc8.OutputArtifact!, mf.OutputArtifact! })
            Require(Hash(artifact.Path).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase), "Codec output identity mismatch.");
        if (retained is not null)
        {
            Directory.CreateDirectory(retained);
            foreach (var row in new[] { (bundle.PrimaryImage.Path, "input.jpg"), (heic.OutputArtifact!.Path, "image.heic"),
                (jpeg.OutputArtifact!.Path, "image.jpg"), (sdr.OutputArtifact!.Path, "sdr.mp4"), (hdr.OutputArtifact!.Path, "hdr.mp4"),
                (hevc8.OutputArtifact!.Path, "hevc8.mp4"), (mf.OutputArtifact!.Path, "mf.mp4") })
                File.Copy(row.Item1, Path.Combine(retained, row.Item2));
        }
        return new { heic = heic.ExecutionRecord, jpeg = jpeg.ExecutionRecord,
            sdr = sdr.ExecutionRecord, hdr = hdr.ExecutionRecord, hevc8 = hevc8.ExecutionRecord, mf = mf.ExecutionRecord };
    }

    private static async Task<int> MfRepeat(string[] args, int repetitions = 24, bool expectMediaFoundation = false)
    {
        string samples = Path.GetFullPath(args[1]), destination = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(destination);
        var rows = new List<object>();
        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            string root;
            object truth;
            using (var workspace = new MediaWorkspace())
            {
                root = workspace.RootDirectory;
                string source = args.Length > 3 ? Path.GetFullPath(args[3]) : Path.Combine(samples, "苹果双文件.MOV");
                var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest
                {
                    SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo,
                        VideoContainer = VideoContainer.Mov, VideoCodec = VideoCodec.Hevc, ByteLength = new FileInfo(source).Length },
                    TargetContainer = VideoContainer.Mp4, TargetCodec = VideoCodec.H264, TargetDirectory = root
                });
                Save(Path.Combine(destination, "attempt-" + iteration.ToString("D2") + ".json"), result);
                Require(result.Success && result.OutputArtifact is not null, "Video repeat failed: " + result.ErrorMessage);
                Require(result.ExecutionRecord.Truth.BackendName == (expectMediaFoundation ? "WindowsMediaFoundation" : "minimal-libav") && !result.ExecutionRecord.Truth.FallbackOccurred,
                    "MF repeat wrong backend.");
                Require(Hash(result.OutputArtifact!.Path) == result.OutputArtifact.Sha256, "MF repeat output hash.");
                truth = result.ExecutionRecord;
            }
            Require(!Directory.Exists(root), "MF repeat workspace residue.");
            var row = new { iteration, natural = Snapshot(), semaphores = SemaphoreCount(), truth, workspaceRemoved = true };
            rows.Add(row); Save(Path.Combine(destination, "iteration-" + iteration.ToString("D2") + ".json"), row);
            Console.WriteLine("MF iteration=" + iteration);
        }
        object finalNatural = Snapshot();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Save(Path.Combine(destination, "mf-repeat.json"), new { passed = true, rows, finalNatural, finalDiagnosticGc = Snapshot() });
        return 0;
    }

    // Diagnostic-only quiet window. This mode deliberately accepts the explicitly
    // marked decode-only probe refusal; it cannot be used as a workload PASS.
    private static async Task<int> MfQuiet(string[] args)
    {
        string destination = Path.GetFullPath(args[2]);
        string? diagnosticStop = null;
        try { await MfRepeat(args); }
        catch (InvalidDataException error) when (error.Message.Contains("R4 diagnostic decode-only stop", StringComparison.Ordinal))
        { diagnosticStop = error.Message; }
        Require(diagnosticStop is not null, "Quiet probe requires the explicit diagnostic-only native runtime.");
        var rows = new List<object>();
        var clock = Stopwatch.StartNew();
        for (int second = 0; second <= 60; second++)
        {
            rows.Add(new { elapsedSeconds = clock.Elapsed.TotalSeconds, natural = Snapshot(), semaphores = SemaphoreCount() });
            if (second < 60) await Task.Delay(1000);
        }
        Save(Path.Combine(destination, "quiet-diagnostic.json"), new { diagnosticOnly = true,
            nativeRuntimeIntentionallyRefusedOutput = true, diagnosticStop, rows });
        return 0;
    }

    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, ".ai/state.json"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Workspace root missing.");
    }

    private static async Task<object> CancelAfterLoad(string samples, string destination)
    {
        using var cancellation = new CancellationTokenSource();
        string? observed = null;
        string root;
        bool cancelled = false;
        object remaining;
        using (var workspace = new MediaWorkspace())
        {
            root = workspace.RootDirectory;
            using var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true };
            watcher.Created += (_, change) =>
            {
                if (Directory.Exists(change.FullPath)) return;
                if (Interlocked.CompareExchange(ref observed, change.FullPath, null) is null) cancellation.Cancel();
            };
            watcher.EnableRaisingEvents = true;
            try
            {
                await new NeutralMediaService().CreateNeutralBundleAsync(Path.Combine(samples, "苹果双文件.HEIC"),
                    Path.Combine(FindRoot(), ".ai-tmp/workspace/P7/R4-large-batch-soak/fixtures/supported.mov"), workspace,
                    cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            watcher.EnableRaisingEvents = false;
            remaining = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(FileIdentity).ToArray();
        }
        Require(observed is not null && cancelled, "Real in-flight cancellation was not observed.");
        Require(!Directory.Exists(root), "Cancelled workspace remains after dispose.");
        Require(Pause(Path.Combine(destination, "post-load-pause")) == 0, "Post-load paused cancellation failed.");
        object sentinel = await MergeBatch(samples, Path.Combine(destination, "post-cancel-sentinel"));
        return new { observed, cancelled, remainingBeforeDispose = remaining, workspaceRemoved = true, sentinel };
    }

    private static async Task<int> Mixed(string[] args, int measuredMinutes)
    {
        string samples = Path.GetFullPath(args[1]), destination = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(destination);
        LogService.Initialize(Path.Combine(destination, "logs"), "r4");
        var inputs = Routes.SelectMany(r => r.Video is null ? new[] { r.Image } : new[] { r.Image, r.Video }).Distinct().ToDictionary(n => n, n => Hash(Path.Combine(samples, n)));
        var elapsed = Stopwatch.StartNew(); var measurements = new List<object>(); int measured = 0;
        var measuredClock = new Stopwatch();
        using var resourceLog = new StreamWriter(new FileStream(Path.Combine(destination, "resources.jsonl"), FileMode.CreateNew));
        using var resourceTimer = new Timer(_ =>
        {
            lock (resourceLog) { resourceLog.WriteLine(System.Text.Json.JsonSerializer.Serialize(Snapshot())); resourceLog.Flush(); }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        try
        {
            for (int round = 0; round < 3 || measuredClock.Elapsed < TimeSpan.FromMinutes(measuredMinutes) || measured < (measuredMinutes == 0 ? 1 : 12); round++)
            {
                bool warmup = round < 3;
                if (!warmup && !measuredClock.IsRunning) measuredClock.Start();
                string label = "r" + round.ToString("D4");
                object before = Snapshot();
                var neutral = new List<object>();
                for (int i = 0; i < Routes.Length; i += 3)
                    neutral.AddRange(await Task.WhenAll(Enumerable.Range(i, Math.Min(3, Routes.Length - i)).Select(n => NeutralRoute(samples, n, null))));
                object batch = await MergeBatch(samples, Path.Combine(destination, label + "-merge"));
                object split = await SplitBatch(samples, Path.Combine(destination, label + "-split"));
                object codecs = await CodecRound(samples, null);
                foreach (var input in inputs) Require(Hash(Path.Combine(samples, input.Key)) == input.Value, "Frozen source changed: " + input.Key);
                string temp = Path.Combine(Path.GetTempPath(), "LivePhotoBox");
                Require(!Directory.Exists(temp) || !Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories).Any(), "Unexplained product TEMP file.");
                object natural = Snapshot();
                object? diagnosticGc = null;
                if (round == 2)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    diagnosticGc = Snapshot();
                }
                var row = new { round, warmup, seconds = elapsed.Elapsed.TotalSeconds,
                    measuredSeconds = measuredClock.Elapsed.TotalSeconds, before, natural, diagnosticGc,
                    neutral, batch, split, codecs };
                Save(Path.Combine(destination, label + ".json"), row);
                measurements.Add(new { round, warmup, natural, diagnosticGc });
                Console.WriteLine($"round={round} warmup={warmup} measuredSeconds={measuredClock.Elapsed.TotalSeconds:F1}");
                if (!warmup) measured++;
            }
            measuredClock.Stop();
            object finalNatural = Snapshot();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            object finalDiagnosticGc = Snapshot();
            object cancellation = await CancelAfterLoad(samples, destination);
            // Retained final media are produced after the continuous measured
            // interval; their copies are not mistaken for production TEMP residue.
            Directory.CreateDirectory(Path.Combine(destination, "retained"));
            for (int i = 0; i < Routes.Length; i++)
                Save(Path.Combine(destination, "retained", "route-" + i + ".json"), await NeutralRoute(samples, i, Path.Combine(destination, "retained", "route-" + i)));
            Save(Path.Combine(destination, "retained", "codecs.json"), await CodecRound(samples, Path.Combine(destination, "retained", "codecs")));
            var quiet = new List<object>();
            var quietClock = Stopwatch.StartNew();
            while (quietClock.Elapsed < TimeSpan.FromSeconds(60))
            {
                quiet.Add(new { elapsedSeconds = quietClock.Elapsed.TotalSeconds, natural = Snapshot() });
                await Task.Delay(1000);
            }
            Save(Path.Combine(destination, "quiet.json"), new { seconds = quietClock.Elapsed.TotalSeconds,
                observations = quiet, explicitGcInQuietWindow = false,
                scope = "After all workload, cancellation/recovery and retained-output production calls; excluded from measured workload duration." });
            Save(Path.Combine(destination, "mixed.json"), new { passed = true, measuredMinutes, measuredEpochs = measured,
                measuredSeconds = measuredClock.Elapsed.TotalSeconds, inputHashes = inputs, measurements,
                finalNatural, finalDiagnosticGc, cancellation,
                meaning = "Correctness and resource observations only; no new performance SLA." });
            return 0;
        }
        finally { await resourceTimer.DisposeAsync(); LogService.MarkCleanShutdown(); }
    }
}
