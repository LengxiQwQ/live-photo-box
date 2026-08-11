using System;
using System.Collections.Generic;

namespace LivePhotoBox.Cli.Infrastructure
{
    // 协议名称 ↔ 索引映射，供 CLI 命令使用
    internal static class ProtocolNameResolver
    {
        private static readonly Dictionary<string, int> ProtocolMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fusion"]   = 0, ["f"] = 0,
            ["v1"]       = 1, ["microvideo"] = 1, ["micro video"] = 1, ["micro"] = 1,
            ["v2"]       = 2, ["motionphoto"] = 2, ["motion photo"] = 2, ["motion"] = 2, ["mp"] = 2,
            ["oppo"]     = 3, ["olive"] = 3, ["o"] = 3,
            ["vivo"]     = 4, ["v"] = 4,
            ["samsung"]  = 5, ["ss"] = 5, ["sam"] = 5,
            ["huawei"]   = 6, ["hw"] = 6, ["h"] = 6,
        };

        private static readonly Dictionary<string, int> FormatMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jpg+mp4"]  = Services.ProtocolFormatMatrix.FormatJpgMp4,
            ["jpeg+mp4"] = Services.ProtocolFormatMatrix.FormatJpgMp4,
            ["jpg+mov"]  = Services.ProtocolFormatMatrix.FormatJpgMov,
            ["jpeg+mov"] = Services.ProtocolFormatMatrix.FormatJpgMov,
            ["heic+mp4"] = Services.ProtocolFormatMatrix.FormatHeicMp4,
            ["heic+mov"] = Services.ProtocolFormatMatrix.FormatHeicMov,
            ["heic+mp4-h265"] = Services.ProtocolFormatMatrix.FormatHeicMp4H265,
        };

        public static bool TryResolveProtocol(string name, out int index) =>
            ProtocolMap.TryGetValue(name.Trim(), out index);

        public static bool TryResolveFormat(string name, out int index) =>
            FormatMap.TryGetValue(name.Trim().Replace(" ", ""), out index);

        public static string GetProtocolDisplayName(int index) => index switch
        {
            0 => "Fusion (universal Android)",
            1 => "Micro Video (Windows / Xiaomi / Pixel)",
            2 => "Motion Photo (Windows / Xiaomi / Pixel)",
            3 => "OPPO O-Live (Windows / Xiaomi / OPPO)",
            4 => "vivo Live Photo (Windows / vivo X300+)",
            5 => "Samsung Motion Photo (Windows / Samsung)",
            6 => "HUAWEI Moving Photo (HUAWEI / Honor)",
            _ => $"Unknown ({index})"
        };

        // 用户可见的协议显示名（正确大小写、带空格）
        public static string[] ProtocolDisplayNames { get; } =
            ["Fusion", "Micro Video", "Motion Photo",
             "OPPO O-Live", "vivo Live Photo", "Samsung Motion Photo",
             "HUAWEI Moving Photo"];

        // 协议标识符：用于 --all-variants 文件名与 JSON name 字段，保持无空格
        public static string[] ProtocolNames { get; } =
            ["Fusion", "MicroVideo", "MotionPhoto",
             "OPPO_OLive", "vivo_LivePhoto", "Samsung_MotionPhoto",
             "HUAWEI_MovingPhoto"];
    }
}
