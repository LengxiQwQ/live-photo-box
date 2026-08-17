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

        private static HashSet<string>? _cachedAvailableEncoders;
        private static DateTime _encoderCacheTime = DateTime.MinValue;
        private static readonly TimeSpan EncoderCacheDuration = TimeSpan.FromMinutes(5);
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

            LogService.Debug($"WMI: Searching for GPUs, FFmpeg encoders available: {availableEncoders.Count}", LogSource.System);

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
                            LogService.Debug($"WMI: GPU '{name}' excluded by keyword '{excludeReason}'", LogSource.System);
                            continue;
                        }

                        LogService.Debug($"WMI: GPU candidate: '{name}', description: '{description}'", LogSource.System);

                        var gpuInfo = new HardwareInfo
                        {
                            Name = name,
                            Description = description ?? string.Empty,
                            Type = HardwareType.Gpu
                        };

                        // 根据 GPU 名称猜测可能的编码器
                        (gpuInfo.IsHardwareEncodingSupported, gpuInfo.FfmpegEncoder) = DetermineFfmpegEncoder(name);
                        LogService.Debug($"WMI: Guessed encoder '{gpuInfo.FfmpegEncoder}' for '{name}', supported={gpuInfo.IsHardwareEncodingSupported}", LogSource.System);

                        // 如果猜测支持硬件编码，验证 FFmpeg 是否真的可用
                        if (gpuInfo.IsHardwareEncodingSupported && !string.IsNullOrEmpty(gpuInfo.FfmpegEncoder))
                        {
                            // 检查这个编码器是否在 FFmpeg 中真正可用
                            if (availableEncoders.Contains(gpuInfo.FfmpegEncoder.ToLowerInvariant()))
                            {
                                gpus.Add(gpuInfo);
                                LogService.Debug($"WMI: GPU '{name}' ADDED with encoder '{gpuInfo.FfmpegEncoder}'", LogSource.System);
                            }
                            else
                            {
                                // 编码器不可用，标记为不支持
                                gpuInfo.IsHardwareEncodingSupported = false;
                                gpuInfo.FfmpegEncoder = null;
                                LogService.Debug($"WMI: GPU '{name}' REJECTED - encoder '{gpuInfo.FfmpegEncoder}' not in FFmpeg list", LogSource.System);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"DetectGpus WMI error: {ex.Message}", source: LogSource.System);
            }

            LogService.Debug($"WMI: All detected GPUs: {string.Join(", ", allDetectedGpus)}", LogSource.System);
            LogService.Debug($"WMI: Qualified GPUs: {gpus.Count}", LogSource.System);

            return gpus;
        }

        // 通过 FFmpeg 获取所有可用的硬件编码器名称（小写，带 5 分钟缓存）
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
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    LogService.Warn("FFmpeg not found, cannot detect hardware encoders", source: LogSource.System);
                    _cachedAvailableEncoders = availableEncoders;
                    _encoderCacheTime = DateTime.Now;
                    return availableEncoders;
                }

                LogService.Debug($"Using FFmpeg at: {ffmpegPath}", LogSource.System);

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

                // 同步读取输出
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);

                // FFmpeg encoders 输出到 stdout
                string output = !string.IsNullOrEmpty(stdout) ? stdout : stderr;

                LogService.Debug($"FFmpeg raw output ({output.Length} chars)", LogSource.System);

                // 逐行解析
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int parseCount = 0;
                int skippedEmpty = 0;
                int skippedLegend = 0;
                int skippedShort = 0;
                int skippedNoV = 0;
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

                            // 单个解析行不再输出（Parse stats 已覆盖）
                        }
                    }
                }

                LogService.Debug($"Parse stats: total={lines.Length}, empty={skippedEmpty}, legend={skippedLegend}, noV={skippedNoV}, short={skippedShort}, parsed={parseCount}", LogSource.System);
                LogService.Debug($"FFmpeg found {availableEncoders.Count} unique encoders", LogSource.System);

                // 更新缓存
                _cachedAvailableEncoders = availableEncoders;
                _encoderCacheTime = DateTime.Now;

                LogService.Debug($"FFmpeg available encoders: {string.Join(", ", availableEncoders)}", LogSource.System);
            }
            catch (Exception ex)
            {
                LogService.Warn($"DetectAvailableEncodersViaFFmpeg error: {ex.Message}", source: LogSource.System);
                _cachedAvailableEncoders = availableEncoders;
                _encoderCacheTime = DateTime.Now;
            }

            return availableEncoders;
        }

        // 获取 FFmpeg 中所有可用编码器的缓存集合（5 分钟有效）。
        // 供 EncoderHelper.IsEncoderAvailable 快速路径使用，避免每次检查都 spawn ffmpeg。
        public static HashSet<string> GetAvailableEncoders()
        {
            if (_cachedAvailableEncoders == null || DateTime.Now - _encoderCacheTime >= EncoderCacheDuration)
                DetectAvailableEncodersViaFFmpeg();
            return _cachedAvailableEncoders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // 通过 FFmpeg 检测可用的硬件编码器
        private static List<HardwareInfo> DetectGpusViaFFmpeg()
        {
            var gpus = new List<HardwareInfo>();

            try
            {
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
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
                LogService.Warn($"DetectGpusViaFFmpeg error: {ex.Message}", source: LogSource.System);
            }

            return gpus;
        }

        // 检查 FFmpeg 编码器是否可用
        private static bool IsEncoderAvailable(string ffmpegPath, string encoder)
        {
            try
            {
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
            catch
            {
                return false;
            }
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
            _cachedAvailableEncoders = null;
            _encoderCacheTime = DateTime.MinValue;
        }
    }
}
