/*
 * ProtocolTestExporter.cs
 *
 * CLI 模式：把所有协议 × 格式变体导出到输出目录。
 * 调用 LivePhotoMergeRunnerService.ProcessSinglePairAsync，与 GUI 合并页使用
 * 完全相同的代码路径（HEIC 转换、视频转码、协议预处理、写入）。
 *
 * 用法: Live Photo Box.exe --export-all-protocols <JPG> <MOV> [输出目录]
 */
using LivePhotoBox.Services.Protocols;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class ProtocolTestExporter
    {
        /// <summary>
        /// 每个输出格式在各个协议下的可用性。
        /// 格式索引: 0=JPG_MP4, 1=JPG_MOV, 2=HEIC_MP4, 3=HEIC_MOV
        /// 协议索引: 0=Fusion, 1=V1, 2=V2, 3=OPPO, 4=VIVO, 5=Samsung, 6=HUAWEI
        /// 必须与 MergePage.xaml.cs 中的 ProtocolFormatMap 保持同步。
        /// </summary>
        private static readonly bool[][] FormatMatrix =
        [
            [true,  true,  false, false], // Fusion:  JPG MP4, JPG MOV
            [true,  true,  false, false], // V1:      JPG MP4, JPG MOV
            [true,  true,  false, true ], // V2:      JPG MP4, JPG MOV, HEIC MOV
            [true,  false, false, false], // OPPO:    只有 JPG MP4
            [true,  false, false, false], // VIVO:    只有 JPG MP4
            [true,  false, true,  false], // Samsung: JPG MP4, HEIC MP4
            [true,  false, true,  false], // HUAWEI:  JPG MP4, HEIC MP4
        ];

        private static readonly string[] ProtocolNames =
            ["Fusion", "V1_MicroVideo", "V2_MotionPhoto",
             "OPPO_OLive", "vivo_LivePhoto", "Samsung_MotionPhoto", "HUAWEI_MovingPhoto"];

        private static readonly string[] FormatNames =
            ["JPEG+MP4", "JPEG+MOV", "HEIC+MP4", "HEIC+MOV"];

        public static async Task Run(string srcJpg, string srcMov, string outputDir)
        {
            var ct = CancellationToken.None;
            LogService.Info($"ProtocolTestExporter: JPG={srcJpg} MOV={srcMov} OUT={outputDir}");
            Directory.CreateDirectory(outputDir);

            // ── Prepare MP4 copy (used for all MP4-format jobs) ────────
            string baseName = Path.GetFileNameWithoutExtension(srcJpg);
            string mp4Path = Path.Combine(outputDir, $"{baseName}_tmp.mp4");
            if (!File.Exists(mp4Path))
            {
                LogService.Info("Copying MOV as MP4 work file...");
                File.Copy(srcMov, mp4Path, true);
            }

            // ── Build flat job list ────────────────────────────────────
            var jobs = new System.Collections.Generic.List<(int Proto, int Fmt, string Img, string Vid, string Label)>();
            for (int proto = 0; proto < FormatMatrix.Length; proto++)
            {
                for (int fmt = 0; fmt < FormatMatrix[proto].Length; fmt++)
                {
                    if (!FormatMatrix[proto][fmt]) continue;

                    string vid = (fmt == 1 || fmt == 3) ? srcMov : mp4Path;
                    string label = $"{ProtocolNames[proto]} {FormatNames[fmt]}";
                    jobs.Add((proto, fmt, srcJpg, vid, label));
                }
            }

            // ── Run each job through the GUI pipeline ──────────────────
            string tempDir = Path.Combine(outputDir, "Temp");
            Directory.CreateDirectory(tempDir);
            var pause = new ManualResetEventSlim(true); // never paused

            var progressFile = Path.Combine(outputDir, "_progress.txt");
            File.WriteAllText(progressFile, $"Starting {jobs.Count} jobs...\n");

            int ok = 0, fail = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var (proto, fmt, img, vid, label) = jobs[i];
                // baseName = protocol folder name + format, e.g. "V2_MotionPhoto_HEIC+MOV"
                string jobBaseName = $"{ProtocolNames[proto]}_{FormatNames[fmt]}";

                try
                {
                    File.AppendAllText(progressFile, $"[{i + 1}/{jobs.Count}] {label} ... ");

                    var options = new LivePhotoMergeRunOptions
                    {
                        OutputDirectory = outputDir,
                        SelectedModeIndex = proto,
                        OutputFormatIndex = fmt,
                        NamingRuleIndex = 0,    // keep base name as-is
                    };

                    var (isSuccess, details) = await LivePhotoMergeRunnerService
                        .ProcessSinglePairAsync(img, vid, jobBaseName, i + 1,
                            options, tempDir, pause, ct);

                    if (isSuccess)
                    {
                        // Find the output file to report its size
                        string ext = Path.GetExtension(img);
                        if (fmt == 2 || fmt == 3) // HEIC format — extension depends on conversion result
                        {
                            // Check what the pipeline actually wrote
                            string[] candidates = [Path.Combine(outputDir, jobBaseName + ".heic"),
                                                    Path.Combine(outputDir, jobBaseName + ".jpg")];
                            string found = null!;
                            foreach (var c in candidates)
                                if (File.Exists(c)) { found = c; break; }
                            if (found != null)
                            {
                                ext = Path.GetExtension(found);
                                File.AppendAllText(progressFile,
                                    $"OK ({(new FileInfo(found)).Length} bytes)\n");
                            }
                            else
                            {
                                File.AppendAllText(progressFile,
                                    $"OK (output file not found; details: {details})\n");
                            }
                        }
                        else
                        {
                            string outFile = Path.Combine(outputDir, jobBaseName + ext);
                            if (File.Exists(outFile))
                                File.AppendAllText(progressFile,
                                    $"OK ({(new FileInfo(outFile)).Length} bytes)\n");
                            else
                                File.AppendAllText(progressFile, $"OK\n");
                        }
                        ok++;
                    }
                    else
                    {
                        File.AppendAllText(progressFile, $"FAIL: {details}\n");
                        fail++;
                    }
                }
                catch (Exception ex)
                {
                    File.AppendAllText(progressFile,
                        $"FAIL: {ex.GetType().Name}: {ex.Message}\n");
                    fail++;
                }
            }

            File.AppendAllText(progressFile, $"\nDone: {ok} OK, {fail} FAIL\n");

            // Cleanup
            try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
