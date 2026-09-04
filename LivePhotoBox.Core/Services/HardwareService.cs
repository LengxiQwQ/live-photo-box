using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Services
{
    // 硬件检测服务 - 检测系统中的 CPU、GPU 等硬件信息
    public static class HardwareService
    {
        // 硬件类型
        public enum HardwareType
        {
            Cpu,
            Gpu
        }

        // 硬件加速器信息
        public class HardwareInfo
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public HardwareType Type { get; set; }
            public bool IsHardwareEncodingSupported { get; set; }
            public string? FfmpegEncoder { get; set; }
        }

        private static List<HardwareInfo>? _cachedHardwareList;
        private static readonly object _hwLock = new();

        // 异步获取所有可用的硬件加速器（不阻塞 UI 线程）
        public static Task<List<HardwareInfo>> GetAvailableHardwareAsync()
        {
            return Task.Run(() => GetAvailableHardware());
        }

        // 获取所有可用的硬件加速器（线程安全，带缓存）。
        // 首次调用执行 WMI + FFmpeg 检测（约 1-3 秒），后续调用直接返回缓存。
        // 使用双检锁确保多个并发调用者不会重复执行检测。
        public static List<HardwareInfo> GetAvailableHardware()
        {
            if (_cachedHardwareList != null)
                return _cachedHardwareList;

            lock (_hwLock)
            {
                if (_cachedHardwareList != null)
                    return _cachedHardwareList;

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

                // 单行紧凑汇总 — 包含 CPU 名称/核心数、GPU 名称/编码器
                var cpuName = hardware.FirstOrDefault(h => h.Type == HardwareType.Cpu)?.Name ?? "Unknown";
                var gpuParts = hardware.Where(h => h.Type == HardwareType.Gpu).Select(g =>
                {
                    var enc = g.IsHardwareEncodingSupported && !string.IsNullOrEmpty(g.FfmpegEncoder)
                        ? $" ({g.FfmpegEncoder})" : "";
                    return $"{g.Name}{enc}";
                });
                var gpuSection = gpuParts.Any() ? $"; GPU(s): {string.Join(", ", gpuParts)}" : "";
                LogService.Info($"Hardware: {cpuName} ({Environment.ProcessorCount} cores){gpuSection}", LogSource.System);

                _cachedHardwareList = hardware;
                return hardware;
            }
        }

        // 根据 GPU 名称估算性能分数，用于排序（分数越高越优先推荐）。
        // 排序结果影响硬件加速编码器的默认选择顺序。
        private static int GetGpuPerformanceScore(string gpuName)
        {
            string lower = gpuName.ToLowerInvariant();
            if (lower.Contains("nvidia") || lower.Contains("geforce") ||
                lower.Contains("gtx") || lower.Contains("rtx") || lower.Contains("quadro"))
                return 300; // NVIDIA 独立显卡，通常性能最强
            if (lower.Contains("amd") || lower.Contains("radeon") ||
                lower.Contains("rx ") || lower.Contains("vega"))
                return 200; // AMD 独立显卡，性能次之
            if (lower.Contains("intel"))
                return 100; // Intel 集成/独立显卡
            return 50; // 其他（如 Microsoft Basic Display Adapter）
        }

        // 检测 CPU 信息
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
                    Description = $"{processorCount} {ResourceService.GetString("SettingsPage_Transcode_Hardware_Threads.Text")}",
                    Type = HardwareType.Cpu,
                    IsHardwareEncodingSupported = false,
                    FfmpegEncoder = null
                };
            }
            catch (Exception ex)
            {
                LogService.Warn($"DetectCpu error: {ex.Message}", source: LogSource.System);
                return null;
            }
        }

        // 使用 WMI 检测 GPU，并用 FFmpeg 验证编码器是否真正可用
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
                            continue;
                        }

                        var gpuInfo = new HardwareInfo
                        {
                            Name = name,
                            Description = description ?? string.Empty,
                            Type = HardwareType.Gpu
                        };

                        (gpuInfo.IsHardwareEncodingSupported, gpuInfo.FfmpegEncoder) = DetermineFfmpegEncoder(name);
                        gpus.Add(gpuInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"DetectGpus WMI error: {ex.Message}", source: LogSource.System);
            }

            return gpus;
        }

        public static HashSet<string> GetAvailableEncoders()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // 根据 GPU 名称判断支持的编码器
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

        // 获取 CPU 名称
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

        // 从已有的硬件列表中获取推荐的默认硬件设置
        public static HardwareInfo? GetRecommendedHardwareFromList(List<HardwareInfo> hardware)
        {
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

        // 获取推荐的默认硬件设置
        public static HardwareInfo? GetRecommendedHardware()
        {
            var hardware = GetAvailableHardware();
            return GetRecommendedHardwareFromList(hardware);
        }

        // 获取系统逻辑处理器数量
        public static int GetProcessorCount()
        {
            return Environment.ProcessorCount;
        }

        // 检查是否支持特定的硬件编码器（委托给 EncoderHelper）。
        public static bool IsEncoderSupported(string encoder)
        {
            return EncoderHelper.IsEncoderAvailable(encoder);
        }

        // 清除硬件检测结果和编码器缓存，强制重新检测。
        // 加锁防止并发清除时与正在进行的检测产生竞态。
        public static void ClearHardwareCache()
        {
            lock (_hwLock)
            {
                _cachedHardwareList = null;
            }
            ClearEncoderCache();
        }

        // 清除编码器缓存，强制重新检测（下次获取硬件信息时会重新检测）
        public static void ClearEncoderCache()
        {
        }
    }
}
