using System;

namespace LivePhotoBox.Services.Protocols;

// Apple hdrgainmap 与 ISO 21496-1 / Google Ultra HDR 增益图的编解码数学。
// 这里只做纯数值运算，不做像素 I/O；像素读写由调用方（Service）负责。
//
// Apple 解码参考 Apple Developer 文档：
//   hdr_rgb = sdr_rgb * (1.0 + (headroom - 1.0) * gainmap)
// 其中 sdr_rgb / gainmap 先经 sRGB EOTF（Rec.709）线性化，结果位于线性 Display P3。
//
// ISO 21496-1 编码参考 Android Ultra HDR 规范：
//   pixel_gain = (Yhdr + offset_hdr) / (Ysdr + offset_sdr)
//   log_recovery = (log2(pixel_gain) - GainMapMin) / (GainMapMax - GainMapMin)
//   recovery = pow(clamp(log_recovery), Gamma)
internal static class HdrGainMapCodec
{
    // Display P3 (D65) RGB -> XYZ 的 Y 行系数，用于把线性 P3 像素转成亮度。
    private const double LumaR = 0.228974564;
    private const double LumaG = 0.691738522;
    private const double LumaB = 0.079286914;

    /// <summary>
    /// 依据 Apple MakerNote 里的 HDRHeadroom(0x0021) 与 HDRGain(0x0030)
    /// 计算 hdrgainmap 的 headroom（最大线性增益）。
    /// </summary>
    public static double ComputeAppleHeadroom(double hdrHeadroom, double hdrGain)
    {
        double stops;
        if (hdrHeadroom < 1.0)
        {
            stops = hdrGain <= 0.01
                ? -20.0 * hdrGain + 1.8
                : -0.101 * hdrGain + 1.601;
        }
        else
        {
            stops = hdrGain <= 0.01
                ? -70.0 * hdrGain + 3.0
                : -0.303 * hdrGain + 2.303;
        }

        return Math.Pow(2.0, Math.Max(stops, 0.0));
    }

    /// <summary>
    /// sRGB / Rec.709 电光传递函数（EOTF），把 0..1 非线性值转成线性值。
    /// </summary>
    public static float SrgbEotf(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value <= 0.04045f
            ? value / 12.92f
            : (float)Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// 计算线性 Display P3 像素的相对亮度（0..1 归一化）。
    /// </summary>
    public static float Luminance(float r, float g, float b)
    {
        return (float)(LumaR * r + LumaG * g + LumaB * b);
    }

    /// <summary>
    /// 由线性 SDR 与线性 HDR（Display P3，参考白 = 1.0）计算 ISO 21496-1 增益图。
    /// 返回每个像素归一化到 0..1 的 recovery 值，并输出对应 XMP 元数据。
    /// </summary>
    public static float[] ComputeIsoGainMap(
        float[] sdrLinearP3,
        float[] hdrLinearP3,
        double headroom,
        double offsetSdr,
        double offsetHdr,
        out IsoGainMapMetadata metadata)
    {
        if (sdrLinearP3.Length != hdrLinearP3.Length || sdrLinearP3.Length % 3 != 0)
        {
            throw new ArgumentException("Linear SDR/HDR buffers must be equal-length RGB triples.");
        }

        int pixelCount = sdrLinearP3.Length / 3;
        double mapMinLog2 = 0.0;
        double mapMaxLog2 = Math.Log2(Math.Max(headroom, 1.0));
        double logRange = Math.Max(mapMaxLog2 - mapMinLog2, double.Epsilon);

        metadata = new IsoGainMapMetadata(
            GainMapMin: mapMinLog2,
            GainMapMax: mapMaxLog2,
            Gamma: 1.0,
            OffsetSDR: offsetSdr,
            OffsetHDR: offsetHdr,
            HDRCapacityMin: 0.0,
            HDRCapacityMax: mapMaxLog2);

        var gain = new float[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 3;
            float sdrR = sdrLinearP3[offset];
            float sdrG = sdrLinearP3[offset + 1];
            float sdrB = sdrLinearP3[offset + 2];
            float hdrR = hdrLinearP3[offset];
            float hdrG = hdrLinearP3[offset + 1];
            float hdrB = hdrLinearP3[offset + 2];

            float ySdr = Luminance(sdrR, sdrG, sdrB);
            float yHdr = Luminance(hdrR, hdrG, hdrB);

            // 与 libultrahdr 的 encodeGain 一致：直接用线性亮度比值，编码阶段不加 offset；
            // offset 只作为元数据写入，供解码端 applyGain 使用。
            double gainRatio = ySdr > 0.0 ? yHdr / ySdr : 1.0;
            gainRatio = Math.Clamp(gainRatio, Math.Pow(2.0, mapMinLog2), Math.Pow(2.0, mapMaxLog2));
            double logRecovery = (Math.Log2(gainRatio) - mapMinLog2) / logRange;
            double clamped = Math.Clamp(logRecovery, 0.0, 1.0);

            // map_gamma == 1.0，因此 recovery == clamped_recovery。
            gain[i] = (float)clamped;
        }

        return gain;
    }

    /// <summary>
    /// 把 0..1 的 recovery 值量化成 8 位灰度字节。
    /// </summary>
    public static byte[] QuantizeGainMap(float[] gain)
    {
        var bytes = new byte[gain.Length];
        for (int i = 0; i < gain.Length; i++)
        {
            bytes[i] = (byte)Math.Clamp((int)MathF.Round(gain[i] * 255.0f), 0, 255);
        }

        return bytes;
    }
}

/// <summary>
/// ISO 21496-1 / Ultra HDR JPEG 的 hdrgm XMP 元数据。
/// </summary>
internal sealed record IsoGainMapMetadata(
    double GainMapMin,
    double GainMapMax,
    double Gamma,
    double OffsetSDR,
    double OffsetHDR,
    double HDRCapacityMin,
    double HDRCapacityMax);
