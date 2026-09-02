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
        public const int FormatHeicMp4H265 = 4; // HUAWEI only: HEIC + MP4 (H.265/HEVC)

        // 格式显示名称（+ 两侧带空格，便于阅读）
        public static readonly string[] FormatNames =
            ["JPEG + MP4", "JPEG + MOV", "HEIC + MP4", "HEIC + MOV", "HEIC + MP4 (H.265)"];

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
            [true,  false, true,  false, true ], // HUAWEI: JPG+MP4 / HEIC+MP4 / HEIC+MP4(H.265)
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

        // 拆分协议索引常量
        public const int SplitProtocolNone = 0;
        public const int SplitProtocolApple = 1;
        public const int SplitProtocolVivo = 2;

        // 拆分格式索引常量
        public const int SplitFormatKeep = 0;
        public const int SplitFormatJpgMov = 1;
        public const int SplitFormatHeicMov = 2;
        public const int SplitFormatJpgMp4 = 3;

        // 拆分格式短名称（CLI / Matrix 共享）
        public static readonly string[] SplitFormatNames =
            ["keep", "jpg+mov", "heic+mov", "jpg+mp4"];

        // 拆分协议 × 格式可用性矩阵（单一事实源：GUI、CLI、ProtocolMediaRequirements 共享）
        // protocolIndex: 0=none / 1=Apple / 2=vivo
        // formatIndex:   0=keep / 1=jpg+mov / 2=heic+mov / 3=jpg+mp4
        public static readonly bool[][] SplitMatrix =
        [
            [true,  true,  true,  true ],  // none:  keep / jpg+mov / heic+mov / jpg+mp4
            [false, true,  true,  false],  // apple: jpg+mov / heic+mov
            [false, false, false, true ],  // vivo:  jpg+mp4
        ];

        // 检查指定拆分协议索引和格式索引的组合是否可用
        public static bool IsSplitAvailable(int splitProtocolIndex, int splitFormatIndex)
        {
            if (splitProtocolIndex < 0 || splitProtocolIndex >= SplitMatrix.Length) return false;
            if (splitFormatIndex < 0 || splitFormatIndex >= SplitMatrix[splitProtocolIndex].Length) return false;
            return SplitMatrix[splitProtocolIndex][splitFormatIndex];
        }

        // 获取指定拆分协议可用的格式索引列表
        public static int[] GetAvailableSplitFormats(int splitProtocolIndex)
        {
            if (splitProtocolIndex < 0 || splitProtocolIndex >= SplitMatrix.Length) return [];
            return Enumerable.Range(0, SplitMatrix[splitProtocolIndex].Length)
                .Where(i => SplitMatrix[splitProtocolIndex][i])
                .ToArray();
        }

        // 获取指定拆分协议的默认格式索引（第一个可用格式）
        public static int GetDefaultSplitFormat(int splitProtocolIndex)
        {
            var formats = GetAvailableSplitFormats(splitProtocolIndex);
            return formats.Length > 0 ? formats[0] : SplitFormatKeep;
        }
    }
}
