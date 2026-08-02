using System.Collections.Generic;
using System.Linq;

namespace LivePhotoBox.Services
{
    // 协议-格式兼容矩阵 —— 所有协议 × 输出格式组合的可用性定义。
    // 这是唯一数据源，GUI（MergePage.xaml.cs）和 CLI（MergeCommand）都从此读取。
    public static class ProtocolFormatMatrix
    {
        // 格式索引常量
        public const int FormatJpgMp4 = 0;
        public const int FormatJpgMov = 1;
        public const int FormatHeicMp4 = 2;
        public const int FormatHeicMov = 3;
        public const int FormatHeicMp4H265 = 4; // HUAWEI only: HEIC + H.265 MP4

        // 格式显示名称
        public static readonly string[] FormatNames =
            ["JPEG+MP4", "JPEG+MOV", "HEIC+MP4", "HEIC+MOV", "HEIC+MP4 (H.265)"];

        // 协议索引: 0=Fusion, 1=V1, 2=V2, 3=OPPO, 4=VIVO, 5=Samsung, 6=HUAWEI
        // 格式索引: 0=JPG_MP4, 1=JPG_MOV, 2=HEIC_MP4, 3=HEIC_MOV, 4=HEIC_MP4_H265
        public static readonly bool[][] Matrix =
        [
            [true,  true,  false, false, false], // Fusion
            [true,  true,  false, false, false], // V1
            [true,  true,  false, true,  false], // V2
            [true,  false, false, false, false], // OPPO
            [true,  false, false, false, false], // VIVO
            [true,  false, true,  false, false], // Samsung
            [true,  false, true,  false, true ], // HUAWEI: + HEIC H.265
        ];

        // 检查指定协议索引和格式索引的组合是否可用
        public static bool IsAvailable(int protocolIndex, int formatIndex)
        {
            if (protocolIndex < 0 || protocolIndex >= Matrix.Length) return false;
            if (formatIndex < 0 || formatIndex >= Matrix[protocolIndex].Length) return false;
            return Matrix[protocolIndex][formatIndex];
        }

        // 获取指定协议可用的格式索引列表
        public static int[] GetAvailableFormats(int protocolIndex)
        {
            if (protocolIndex < 0 || protocolIndex >= Matrix.Length) return [];
            return Enumerable.Range(0, Matrix[protocolIndex].Length)
                .Where(i => Matrix[protocolIndex][i])
                .ToArray();
        }

        // 获取指定协议的默认格式索引（第一个可用格式）
        public static int GetDefaultFormat(int protocolIndex)
        {
            var formats = GetAvailableFormats(protocolIndex);
            return formats.Length > 0 ? formats[0] : FormatJpgMp4;
        }
    }
}
