using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace LivePhotoBox.Services
{
    // 编码器中心 — 硬件加速编码器的检测、选择、参数、线程数等所有共享逻辑。
    // VideoTranscodeService 和 LivePhotoRepairService 都通过此类获取编码器能力，
    // 不再各自维护重复的编码器检查/参数/线程代码。
    public static class EncoderHelper
    {
        // ── 编码器可用性 ────────────────────────────────

        // 检查 FFmpeg 编码器是否可用。优先走 HardwareService 的缓存（5 分钟有效），
        // 缓存不可用时 spawn ffmpeg -encoders 直接检查。
        // 这是全项目唯一的编码器可用性检查入口。
        public static bool IsEncoderAvailable(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return false;

            // 快速路径：HardwareService 缓存的编码器集合
            var cached = HardwareService.GetAvailableEncoders();
            if (cached.Count > 0)
                return cached.Contains(encoder);

            // 慢速路径：直接 spawn ffmpeg
            return CheckEncoderViaFFmpeg(encoder);
        }

        private static bool CheckEncoderViaFFmpeg(string encoder)
        {
            try
            {
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath)) return false;

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-hide_banner -encoders",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return output.Contains(encoder, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ── 硬件编码器判定 ─────────────────────────────

        // 判断编码器名是否为硬件加速编码器（NVENC / QSV / AMF / VAAPI）。
        public static bool IsHardwareEncoder(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return false;
            string lower = encoder.ToLowerInvariant();
            return lower.Contains("nvenc") || lower.Contains("qsv")
                || lower.Contains("amf") || lower.Contains("vaapi");
        }

        // 检查当前设置中是否启用了硬件加速（两个 codec 任一有 HW 编码器即 true）。
        // 供 RepairViewModel 决定并行度使用。
        public static bool IsUsingHardwareAcceleration()
        {
            foreach (var codec in new[] { "h264", "hevc" })
            {
                string? encoder = AppSettingsService.GetValue<string?>($"SplitEncoder_{codec}", null);
                if (!string.IsNullOrEmpty(encoder) && IsHardwareEncoder(encoder))
                    return true;
            }
            return false;
        }

        // ── 编码器参数 ──────────────────────────────────

        // 获取硬件编码器的 FFmpeg 质量参数。
        // fallbackCrf: (h264 CRF, hevc CRF)，用于不认识的编码器名兜底。
        // 转码用 (19, 21)，修复用 (13, 14)。
        public static string GetHardwareEncoderParams(string encoder, (int h264Crf, int hevcCrf) fallbackCrf)
        {
            string lower = encoder.ToLowerInvariant();

            if (lower.StartsWith("h264"))
            {
                return lower switch
                {
                    "h264_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 19 -b:v 0 -maxrate:v 30M -bufsize:v 60M -profile:v high",
                    "h264_qsv"   => "-global_quality 19 -look_ahead 1",
                    "h264_amf"   => "-preset quality -rc cqp -qp 19",
                    "h264_vaapi" => "-quality 85 -rc_mode 1",
                    _            => $"-preset medium -crf {fallbackCrf.h264Crf}"
                };
            }

            return lower switch
            {
                "hevc_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 21 -b:v 0 -maxrate:v 25M -bufsize:v 50M -tune hq",
                "hevc_qsv"   => "-global_quality 21 -look_ahead 1",
                "hevc_amf"   => "-preset quality -rc cqp -qp 21",
                "hevc_vaapi" => "-quality 85 -rc_mode 1",
                _            => $"-preset medium -crf {fallbackCrf.hevcCrf}"
            };
        }

        // 获取软件编码器名 + 参数。
        public static (string encoder, string encoderParams) GetSoftwareEncoder(string codec, int crf)
        {
            return codec == "hevc"
                ? ("libx265", $"-preset medium -crf {crf}")
                : ("libx264", $"-preset medium -crf {crf}");
        }

        // ── 编码器线程数 ────────────────────────────────

        // 获取 FFmpeg -threads 参数值。
        // 硬件编码：固定 1（编码在 GPU，CPU 线程无用）。
        // 软件编码：用户设置值，受 maxSoftwareThreads 上限约束。
        // VTS 传 null（不限制），Repair 传 6（x264/x265 超 6 线程收益递减 + 多任务并行）。
        public static int GetThreadCount(string? encoder, int? maxSoftwareThreads = null)
        {
            int userThreads = AppSettingsService.GetValue("SplitThreadCount", Environment.ProcessorCount);

            if (!string.IsNullOrEmpty(encoder) && IsHardwareEncoder(encoder))
                return 1;

            if (maxSoftwareThreads.HasValue)
                return Math.Min(userThreads, maxSoftwareThreads.Value);

            return userThreads;
        }

        // ── 跨 Codec 推导 ──────────────────────────────

        // 根据一个 codec 的编码器推导另一个 codec 的编码器名。
        // h264_nvenc → hevc_nvenc，hevc_qsv → h264_qsv。
        // 如果传入的编码器不匹配已知前缀则返回 null。
        public static string? DeriveCrossCodecEncoder(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return null;

            if (encoder.StartsWith("h264_", StringComparison.OrdinalIgnoreCase))
                return "hevc_" + encoder.Substring(5);

            if (encoder.StartsWith("hevc_", StringComparison.OrdinalIgnoreCase))
                return "h264_" + encoder.Substring(5);

            return null;
        }

        // ── 设置读写 ────────────────────────────────────

        // 从设置中读取指定 codec 的已保存编码器。
        // 自动处理旧版 SplitHardwareEncoder → SplitEncoder_hevc 迁移。
        // 如果编码器已保存但当前 FFmpeg 不可用，返回 null（触发上层重新检测）。
        public static string? GetSavedEncoder(string codec)
        {
            string key = $"SplitEncoder_{codec}";
            string? encoder = AppSettingsService.GetValue<string?>(key, null);

            if (!string.IsNullOrEmpty(encoder))
            {
                return IsEncoderAvailable(encoder) ? encoder : null;
            }

            // HEVC 缺失时尝试从旧版 SplitHardwareEncoder 迁移
            if (codec == "hevc")
            {
                string? legacyH264 = AppSettingsService.GetValue<string?>("SplitHardwareEncoder", null);
                if (!string.IsNullOrEmpty(legacyH264) && legacyH264.StartsWith("h264_", StringComparison.OrdinalIgnoreCase))
                {
                    string migrated = "hevc" + legacyH264.Substring(4);
                    if (IsEncoderAvailable(migrated))
                    {
                        AppSettingsService.SetValue(key, migrated);
                        LogService.Info($"EncoderHelper: migrated legacy '{legacyH264}' → '{migrated}'", LogSource.System);
                        return migrated;
                    }
                }
            }

            return null;
        }

        // 自动检测指定 codec 的可用硬件编码器（spawn ffmpeg -encoders）。
        // 优先级：NVENC > AMF > QSV > VAAPI。
        // 如果没有任何可用的硬件编码器返回 null。
        public static string? DetectHardwareEncoderForCodec(string codec)
        {
            string[] candidates = codec == "h264"
                ? new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_vaapi" }
                : new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv", "hevc_vaapi" };

            foreach (var candidate in candidates)
            {
                if (IsEncoderAvailable(candidate))
                    return candidate;
            }
            return null;
        }

        // 根据一个 codec 的编码器，同时保存两个 codec 的设置。
        // 例如传入 h264_nvenc → 存 SplitEncoder_h264=h264_nvenc, SplitEncoder_hevc=hevc_nvenc。
        // 防止切换 GPU 后另一个 codec 残留旧设置。
        public static void SaveEncoderForBothCodecs(string? encoder)
        {
            if (string.IsNullOrEmpty(encoder))
            {
                AppSettingsService.SetValue("SplitEncoder_h264", string.Empty);
                AppSettingsService.SetValue("SplitEncoder_hevc", string.Empty);
                return;
            }

            string lower = encoder.ToLowerInvariant();

            if (lower.StartsWith("h264_"))
            {
                AppSettingsService.SetValue("SplitEncoder_h264", encoder);
                string? hevcEncoder = DeriveCrossCodecEncoder(encoder);
                AppSettingsService.SetValue("SplitEncoder_hevc",
                    !string.IsNullOrEmpty(hevcEncoder) && IsEncoderAvailable(hevcEncoder)
                        ? hevcEncoder : string.Empty);
            }
            else if (lower.StartsWith("hevc_"))
            {
                AppSettingsService.SetValue("SplitEncoder_hevc", encoder);
                string? h264Encoder = DeriveCrossCodecEncoder(encoder);
                AppSettingsService.SetValue("SplitEncoder_h264",
                    !string.IsNullOrEmpty(h264Encoder) && IsEncoderAvailable(h264Encoder)
                        ? h264Encoder : string.Empty);
            }
            else
            {
                // CPU 或其他：两个 codec 都清空
                AppSettingsService.SetValue("SplitEncoder_h264", string.Empty);
                AppSettingsService.SetValue("SplitEncoder_hevc", string.Empty);
            }
        }
    }
}
