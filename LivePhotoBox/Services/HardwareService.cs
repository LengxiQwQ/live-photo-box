using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 硬件检测服务 - 检测系统中的 CPU、GPU 等硬件信息
    /// </summary>
    public static class HardwareService
    {
        /// <summary>
        /// 硬件类型
        /// </summary>
        public enum HardwareType
        {
            Cpu,
            Gpu
        }

        /// <summary>
        /// 硬件加速器信息
        /// </summary>
        public class HardwareInfo
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public HardwareType Type { get; set; }
            public bool IsHardwareEncodingSupported { get; set; }
            public string? FfmpegEncoder { get; set; }
        }

        private static string? _cachedFFmpegPath;
        private static HashSet<string>? _cachedAvailableEncoders;
        private static DateTime _encoderCacheTime = DateTime.MinValue;
        private static readonly TimeSpan EncoderCacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 获取所有可用的硬件加速器
        /// </summary>
        public static List<HardwareInfo> GetAvailableHardware()
        {
            var hardware = new List<HardwareInfo>();

            // 检测 CPU
            var cpuInfo = DetectCpu();
            if (cpuInfo != null)
            {
                hardware.Add(cpuInfo);
            }

            // 检测 GPU（按性能排序：NVIDIA > AMD > Intel > 其他）
            // 先通过 WMI 获取 GPU 列表，再用 FFmpeg 验证编码器是否真正可用
            var gpus = DetectGpus();
            gpus = gpus.OrderByDescending(g => GetGpuPerformanceScore(g.Name)).ToList();
            hardware.AddRange(gpus);

            // 记录检测结果
            AppLogService.Split($"Hardware detection complete: {hardware.Count} device(s) found");
            foreach (var h in hardware)
            {
                AppLogService.Split($"  - {h.Name}: {h.Type}, Encoder={h.FfmpegEncoder ?? "N/A"}, HWEncoding={h.IsHardwareEncodingSupported}");
            }

            return hardware;
        }

        private static int GetGpuPerformanceScore(string gpuName)
        {
            string lower = gpuName.ToLowerInvariant();
            if (lower.Contains("nvidia") || lower.Contains("geforce") ||
                lower.Contains("gtx") || lower.Contains("rtx") || lower.Contains("quadro"))
                return 300; // NVIDIA 通常最强
            if (lower.Contains("amd") || lower.Contains("radeon") ||
                lower.Contains("rx ") || lower.Contains("vega"))
                return 200; // AMD 次之
            if (lower.Contains("intel"))
                return 100; // Intel 核显较弱
            return 50; // 其他
        }

        /// <summary>
        /// 检测 CPU 信息
        /// </summary>
        private static HardwareInfo? DetectCpu()
        {
            try
            {
                string cpuName = GetCpuName();
                if (string.IsNullOrEmpty(cpuName))
                {
                    cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
                }

                // 获取逻辑处理器数量
                int processorCount = Environment.ProcessorCount;

                return new HardwareInfo
                {
                    Name = cpuName,
                    Description = $"{processorCount} {ResourceService.GetString("SettingsPage_Split_Hardware_Threads")}",
                    Type = HardwareType.Cpu,
                    IsHardwareEncodingSupported = false,
                    FfmpegEncoder = null
                };
            }
            catch (Exception ex)
            {
                AppLogService.Split($"DetectCpu error: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// 使用 WMI 检测 GPU，并用 FFmpeg 验证编码器是否真正可用
        /// </summary>
        private static List<HardwareInfo> DetectGpus()
        {
            var gpus = new List<HardwareInfo>();
            var allDetectedGpus = new List<string>(); // 调试用

            // 需要过滤的关键词（模拟器、虚拟化软件等）
            string[] excludeKeywords = {
                "模拟器", "simulator", "emu", "android", "bluestacks", "nox", "mumu",
                "ldplayer", "leidian", "逍遥", "天天", "雷电", "夜神",
                "virtual", "vmware", "parallels", "hyper-v", "wsl",
                "microsoft basic", "llvmpipe", "swiftshader", "software"
            };

            // 先通过 FFmpeg 获取所有可用的硬件编码器
            var availableEncoders = DetectAvailableEncodersViaFFmpeg();

            AppLogService.Split($"[DEBUG] WMI: Searching for GPUs, FFmpeg encoders available: {availableEncoders.Count}", LogLevel.Info);

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, Description FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"]?.ToString();
                    string? description = obj["Description"]?.ToString();

                    allDetectedGpus.Add(name ?? "null");

                    if (!string.IsNullOrEmpty(name))
                    {
                        // 检查是否应该过滤
                        string lowerName = name.ToLowerInvariant();
                        bool shouldExclude = false;
                        string? excludeReason = null;

                        foreach (var keyword in excludeKeywords)
                        {
                            if (lowerName.Contains(keyword))
                            {
                                shouldExclude = true;
                                excludeReason = keyword;
                                break;
                            }
                        }

                        if (shouldExclude)
                        {
                            AppLogService.Split($"[DEBUG] WMI: GPU '{name}' excluded by keyword '{excludeReason}'", LogLevel.Info);
                            continue;
                        }

                        AppLogService.Split($"[DEBUG] WMI: GPU candidate: '{name}', description: '{description}'", LogLevel.Info);

                        var gpuInfo = new HardwareInfo
                        {
                            Name = name,
                            Description = description ?? string.Empty,
                            Type = HardwareType.Gpu
                        };

                        // 根据 GPU 名称猜测可能的编码器
                        (gpuInfo.IsHardwareEncodingSupported, gpuInfo.FfmpegEncoder) = DetermineFfmpegEncoder(name);
                        AppLogService.Split($"[DEBUG] WMI: Guessed encoder '{gpuInfo.FfmpegEncoder}' for '{name}', supported={gpuInfo.IsHardwareEncodingSupported}", LogLevel.Info);

                        // 如果猜测支持硬件编码，验证 FFmpeg 是否真的可用
                        if (gpuInfo.IsHardwareEncodingSupported && !string.IsNullOrEmpty(gpuInfo.FfmpegEncoder))
                        {
                            // 检查这个编码器是否在 FFmpeg 中真正可用
                            if (availableEncoders.Contains(gpuInfo.FfmpegEncoder.ToLowerInvariant()))
                            {
                                gpus.Add(gpuInfo);
                                AppLogService.Split($"[DEBUG] WMI: GPU '{name}' ADDED with encoder '{gpuInfo.FfmpegEncoder}'", LogLevel.Info);
                            }
                            else
                            {
                                // 编码器不可用，标记为不支持
                                gpuInfo.IsHardwareEncodingSupported = false;
                                gpuInfo.FfmpegEncoder = null;
                                AppLogService.Split($"[DEBUG] WMI: GPU '{name}' REJECTED - encoder '{gpuInfo.FfmpegEncoder}' not in FFmpeg list", LogLevel.Info);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogService.Split($"DetectGpus WMI error: {ex.Message}", LogLevel.Warning);
            }

            AppLogService.Split($"[DEBUG] WMI: All detected GPUs: {string.Join(", ", allDetectedGpus)}", LogLevel.Info);
            AppLogService.Split($"[DEBUG] WMI: Qualified GPUs: {gpus.Count}", LogLevel.Info);

            return gpus;
        }

        /// <summary>
        /// 通过 FFmpeg 获取所有可用的硬件编码器名称（小写，带 5 分钟缓存）
        /// </summary>
        private static HashSet<string> DetectAvailableEncodersViaFFmpeg()
        {
            // 检查缓存是否有效
            if (_cachedAvailableEncoders != null && DateTime.Now - _encoderCacheTime < EncoderCacheDuration)
            {
                return _cachedAvailableEncoders;
            }

            var availableEncoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    string? ffmpegPath = FindFFmpeg();
                    if (string.IsNullOrEmpty(ffmpegPath))
                    {
                        AppLogService.Split("FFmpeg not found, cannot detect hardware encoders", LogLevel.Warning);
                        _cachedAvailableEncoders = availableEncoders;
                        _encoderCacheTime = DateTime.Now;
                        return availableEncoders;
                    }

                    AppLogService.Split($"[DEBUG] Using FFmpeg at: {ffmpegPath}", LogLevel.Info);

                var process = new Process
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

                // 同步读取输出
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                // FFmpeg encoders 输出到 stdout
                string output = !string.IsNullOrEmpty(stdout) ? stdout : stderr;

                AppLogService.Split($"[DEBUG] FFmpeg raw output ({output.Length} chars)", LogLevel.Info);

                // 逐行解析
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int parseCount = 0;
                int skippedEmpty = 0;
                int skippedLegend = 0;
                int skippedShort = 0;
                int skippedNoV = 0;
                int debugLineCount = 0;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.TrimStart(); // 只 trim 开头，保留尾部

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        skippedEmpty++;
                        continue;
                    }

                    // 跳过图例行 (包含 "=")
                    if (line.Contains("="))
                    {
                        skippedLegend++;
                        continue;
                    }

                    // 跳过 "------" 分隔线
                    if (line.StartsWith("------"))
                        continue;

                    // 必须是 Video 编码器 (V 开头)
                    if (line.Length < 8 || line[0] != 'V')
                    {
                        skippedNoV++;
                        continue;
                    }

                    // 跳过长度不够的
                    if (line.Length < 7)
                    {
                        skippedShort++;
                        continue;
                    }

                    // 位置 6 必须是空格，位置 0-5 是标记字符
                    if (line.Length > 6 && line[6] == ' ')
                    {
                        // 编码器名紧跟在空格后面
                        string afterFlag = line.Substring(7).Trim();
                        string encoder = afterFlag.Split(' ')[0];

                        // 验证编码器名有效
                        if (!string.IsNullOrEmpty(encoder) &&
                            encoder.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                        {
                            availableEncoders.Add(encoder.ToLowerInvariant());
                            parseCount++;

                            // 记录前 5 个解析的编码器用于调试
                            if (debugLineCount < 5)
                            {
                                AppLogService.Split($"[DEBUG] Parse: '{line.Substring(0, Math.Min(60, line.Length))}' -> encoder='{encoder}'", LogLevel.Info);
                                debugLineCount++;
                            }
                        }
                    }
                }

                AppLogService.Split($"[DEBUG] Parse stats: total={lines.Length}, empty={skippedEmpty}, legend={skippedLegend}, noV={skippedNoV}, short={skippedShort}, parsed={parseCount}", LogLevel.Info);
                AppLogService.Split($"[DEBUG] FFmpeg found {availableEncoders.Count} unique encoders", LogLevel.Info);
                if (availableEncoders.Count > 0)
                {
                    var sorted = availableEncoders.OrderBy(e => e).Take(20).ToList();
                    AppLogService.Split($"[DEBUG] First 20: {string.Join(", ", sorted)}", LogLevel.Info);
                }

                // 更新缓存
                _cachedAvailableEncoders = availableEncoders;
                _encoderCacheTime = DateTime.Now;

                AppLogService.Split($"FFmpeg available encoders: {string.Join(", ", availableEncoders)}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                AppLogService.Split($"DetectAvailableEncodersViaFFmpeg error: {ex.Message}", LogLevel.Warning);
                _cachedAvailableEncoders = availableEncoders;
                _encoderCacheTime = DateTime.Now;
            }

            return availableEncoders;
        }

        /// <summary>
        /// 查找 FFmpeg 可执行文件
        /// </summary>
        private static string? FindFFmpeg()
        {
            if (_cachedFFmpegPath != null)
            {
                return _cachedFFmpegPath;
            }

            string[] candidates =
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                "ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        _cachedFFmpegPath = candidate;
                        return _cachedFFmpegPath;
                    }

                    if (candidate == "ffmpeg")
                    {
                        var pathEnv = Environment.GetEnvironmentVariable("PATH");
                        if (!string.IsNullOrEmpty(pathEnv))
                        {
                            foreach (var part in pathEnv.Split(System.IO.Path.PathSeparator))
                            {
                                string fullPath = System.IO.Path.Combine(part.Trim(), "ffmpeg.exe");
                                if (System.IO.File.Exists(fullPath))
                                {
                                    _cachedFFmpegPath = fullPath;
                                    return _cachedFFmpegPath;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 通过 FFmpeg 检测可用的硬件编码器
        /// </summary>
        private static List<HardwareInfo> DetectGpusViaFFmpeg()
        {
            var gpus = new List<HardwareInfo>();

            try
            {
                string? ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    return gpus;
                }

                // 检测 NVENC (NVIDIA)
                if (IsEncoderAvailable(ffmpegPath, "h264_nvenc"))
                {
                    gpus.Add(new HardwareInfo
                    {
                        Name = "NVIDIA GPU (NVENC)",
                        Description = "NVIDIA 显卡硬件加速",
                        Type = HardwareType.Gpu,
                        IsHardwareEncodingSupported = true,
                        FfmpegEncoder = "h264_nvenc"
                    });
                }

                // 检测 QSV (Intel)
                if (IsEncoderAvailable(ffmpegPath, "h264_qsv"))
                {
                    gpus.Add(new HardwareInfo
                    {
                        Name = "Intel GPU (QSV)",
                        Description = "Intel 核显/独显硬件加速",
                        Type = HardwareType.Gpu,
                        IsHardwareEncodingSupported = true,
                        FfmpegEncoder = "h264_qsv"
                    });
                }

                // 检测 AMF (AMD)
                if (IsEncoderAvailable(ffmpegPath, "h264_amf"))
                {
                    gpus.Add(new HardwareInfo
                    {
                        Name = "AMD GPU (AMF)",
                        Description = "AMD 显卡硬件加速",
                        Type = HardwareType.Gpu,
                        IsHardwareEncodingSupported = true,
                        FfmpegEncoder = "h264_amf"
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogService.Split($"DetectGpusViaFFmpeg error: {ex.Message}", LogLevel.Warning);
            }

            return gpus;
        }

        /// <summary>
        /// 检查 FFmpeg 编码器是否可用
        /// </summary>
        private static bool IsEncoderAvailable(string ffmpegPath, string encoder)
        {
            try
            {
                var process = new Process
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
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 根据 GPU 名称判断支持的编码器
        /// </summary>
        private static (bool supported, string? encoder) DetermineFfmpegEncoder(string gpuName)
        {
            string lowerName = gpuName.ToLowerInvariant();

            // NVIDIA
            if (lowerName.Contains("nvidia") || lowerName.Contains("geforce") ||
                lowerName.Contains("gtx") || lowerName.Contains("rtx") || lowerName.Contains("quadro"))
            {
                return (true, "h264_nvenc");
            }

            // AMD
            if (lowerName.Contains("amd") || lowerName.Contains("radeon") ||
                lowerName.Contains("rx ") || lowerName.Contains("vega"))
            {
                return (true, "h264_amf");
            }

            // Intel
            if (lowerName.Contains("intel") || lowerName.Contains("uhd") ||
                lowerName.Contains("iris") || lowerName.Contains("arc"))
            {
                return (true, "h264_qsv");
            }

            return (false, null);
        }

        /// <summary>
        /// 获取 CPU 名称
        /// </summary>
        private static string GetCpuName()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    return obj["Name"]?.ToString() ?? string.Empty;
                }
            }
            catch { }

            return string.Empty;
        }

        /// <summary>
        /// 获取推荐的默认硬件设置
        /// </summary>
        public static HardwareInfo? GetRecommendedHardware()
        {
            var hardware = GetAvailableHardware();

            // 优先选择支持硬件编码的 GPU
            var gpu = hardware.FirstOrDefault(h => h.Type == HardwareType.Gpu && h.IsHardwareEncodingSupported);
            if (gpu != null)
            {
                return gpu;
            }

            // 其次选择任何可用的 GPU
            gpu = hardware.FirstOrDefault(h => h.Type == HardwareType.Gpu);
            if (gpu != null)
            {
                return gpu;
            }

            // 最后使用 CPU
            return hardware.FirstOrDefault(h => h.Type == HardwareType.Cpu);
        }

        /// <summary>
        /// 获取系统逻辑处理器数量
        /// </summary>
        public static int GetProcessorCount()
        {
            return Environment.ProcessorCount;
        }

        /// <summary>
        /// 检查是否支持特定的硬件编码器
        /// </summary>
        public static bool IsEncoderSupported(string encoder)
        {
            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                return false;
            }

            return IsEncoderAvailable(ffmpegPath, encoder);
        }

        /// <summary>
        /// 清除编码器缓存，强制重新检测（下次获取硬件信息时会重新检测）
        /// </summary>
        public static void ClearEncoderCache()
        {
            _cachedAvailableEncoders = null;
            _encoderCacheTime = DateTime.MinValue;
        }
    }
}
