/*
 * ProtocolTestExporter.cs
 *
 * CLI 模式：把所有协议 × 格式变体导出到输出目录。
 * 直接调用项目内的 LivePhotoMergeService，确保逻辑和 GUI 完全一致。
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
        public static async Task Run(string srcJpg, string srcMov, string outputDir)
        {
            var ct = CancellationToken.None;
            LogService.Info($"ProtocolTestExporter: JPG={srcJpg} MOV={srcMov} OUT={outputDir}");

            // ── Prepare MP4 copy ──────────────────────────────────────────
            string workDir = Path.Combine(outputDir, ".work");
            Directory.CreateDirectory(workDir);
            string baseName = Path.GetFileNameWithoutExtension(srcJpg);
            string mp4Path = Path.Combine(workDir, $"{baseName}.mp4");

            if (!File.Exists(mp4Path))
            {
                LogService.Info("Copying MOV as MP4 work file...");
                File.Copy(srcMov, mp4Path, true);
            }

            // ── All jobs: (protocolIndex, folder, img, vid, label) ──────
            var jobs = new (int Index, string Folder, string Img, string Vid, string Label)[]
            {
                // V1 - JPEG+MP4 only
                (1, "V1_MicroVideo/JPEG+MP4", srcJpg, mp4Path, "V1 JPEG+MP4"),
                // V2 - 4 variants
                (2, "V2_MotionPhoto/JPEG+MP4", srcJpg, mp4Path, "V2 JPEG+MP4"),
                (2, "V2_MotionPhoto/JPEG+MOV", srcJpg, srcMov, "V2 JPEG+MOV"),
                // OPPO - JPEG only
                (3, "OPPO_OLive/JPEG+MP4", srcJpg, mp4Path, "OPPO JPEG+MP4"),
                // vivo - 4 variants
                (4, "vivo_LivePhoto/JPEG+MP4", srcJpg, mp4Path, "vivo JPEG+MP4"),
                (4, "vivo_LivePhoto/JPEG+MOV", srcJpg, srcMov, "vivo JPEG+MOV"),
                // Samsung - 2 JPEG variants
                (5, "Samsung_MotionPhoto/JPEG+MP4", srcJpg, mp4Path, "Samsung JPEG+MP4"),
                (5, "Samsung_MotionPhoto/JPEG+MOV", srcJpg, srcMov, "Samsung JPEG+MOV"),
                // Huawei - 2 variants
                (6, "HUAWEI_MovingPhoto/JPEG+MP4", srcJpg, mp4Path, "Huawei JPEG+MP4"),
                // Fusion - 2 JPEG variants
                (0, "Fusion/JPEG+MP4", srcJpg, mp4Path, "Fusion JPEG+MP4"),
                (0, "Fusion/JPEG+MOV", srcJpg, srcMov, "Fusion JPEG+MOV"),
            };

            var progressFile = Path.Combine(outputDir, "_progress.txt");
            File.WriteAllText(progressFile, $"Starting {jobs.Length} jobs...\n");

            int ok = 0, fail = 0;
            for (int i = 0; i < jobs.Length; i++)
            {
                var (idx, folder, img, vid, label) = jobs[i];
                string dir = Path.Combine(outputDir, folder);
                Directory.CreateDirectory(dir);
                string ext = Path.GetExtension(img);
                string outFile = Path.Combine(dir, $"merge_sample_01{ext}");

                try
                {
                    File.AppendAllText(progressFile, $"[{i+1}/{jobs.Length}] {label} ... ");
                    await LivePhotoMergeService.WriteLivePhotoAsync(
                        img, vid, outFile, idx, ct);
                    File.AppendAllText(progressFile, $"OK ({(new FileInfo(outFile)).Length} bytes)\n");
                    ok++;
                }
                catch (Exception ex)
                {
                    File.AppendAllText(progressFile, $"FAIL: {ex.GetType().Name}: {ex.Message}\n");
                    fail++;
                }
            }

            File.AppendAllText(progressFile, $"\nDone: {ok} OK, {fail} FAIL\n");

            // Cleanup
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
