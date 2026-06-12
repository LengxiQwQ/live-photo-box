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
            var gpus = DetectGpus();
            gpus = gpus.OrderByDescending(g => GetGpuPerformanceScore(g.Name)).ToList();
            hardware.AddRange(gpus);

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
        /// 使用 WMI 检测 GPU
        /// </summary>
        private static List<HardwareInfo> DetectGpus()
        {
            var gpus = new List<HardwareInfo>();

            // 需要过滤的关键词（模拟器、虚拟化软件等）
            string[] excludeKeywords = {
                "模拟器", "simulator", "emu", "android", "bluestacks", "nox", "mumu",
                "ldplayer", "leidian", "逍遥", "天天", "雷电", "夜神",
                "virtual", "vmware", "parallels", "hyper-v", "wsl",
                "microsoft basic", "llvmpipe", "swiftshader", "software"
            };

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, Description FROM Win32_VideoController");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string? name = obj["Name"]?.ToString();
                    string? description = obj["Description"]?.ToString();

                    if (!string.IsNullOrEmpty(name))
                    {
                        // 检查是否应该过滤
                        string lowerName = name.ToLowerInvariant();
                        bool shouldExclude = false;

                        foreach (var keyword in excludeKeywords)
                        {
                            if (lowerName.Contains(keyword))
                            {
                                shouldExclude = true;
                                break;
                            }
                        }

                        if (shouldExclude)
                        {
                            continue;
                        }

                        var gpuInfo = new HardwareInfo
                        {
                            Name = name,
                            Description = description ?? string.Empty,
                            Type = HardwareType.Gpu
                        };

                        // 判断 GPU 类型并设置对应的 FFmpeg 编码器
                        (gpuInfo.IsHardwareEncodingSupported, gpuInfo.FfmpegEncoder) = DetermineFfmpegEncoder(name);

                        // 只有真正支持硬件编码的 GPU 才添加
                        if (gpuInfo.IsHardwareEncodingSupported)
                        {
                            gpus.Add(gpuInfo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogService.Split($"DetectGpus WMI error: {ex.Message}", LogLevel.Warning);
            }

            return gpus;
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
    }
}
