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
            ["v1"]       = 1, ["microvideo"] = 1, ["micro"] = 1,
            ["v2"]       = 2, ["motionphoto"] = 2, ["motion"] = 2, ["mp"] = 2,
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
            0 => "Fusion (V2 + OPPO + vivo + Samsung)",
            1 => "MicroVideo V1 (Google, deprecated)",
            2 => "MotionPhoto V2 (Google, modern)",
            3 => "OPPO / OnePlus O-Live",
            4 => "vivo Live Photo",
            5 => "Samsung Motion Photo",
            6 => "HUAWEI Moving Photo",
            _ => $"Unknown ({index})"
        };

        public static string[] ProtocolNames { get; } =
            ["Fusion", "V1_MicroVideo", "V2_MotionPhoto",
             "OPPO_OLive", "vivo_LivePhoto", "Samsung_MotionPhoto",
             "HUAWEI_MovingPhoto"];
    }
}
