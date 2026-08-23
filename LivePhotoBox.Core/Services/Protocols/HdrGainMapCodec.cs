using System;

namespace LivePhotoBox.Services.Protocols;

/*
 * HdrGainMapCodec.cs
 *
 * Apple hdrgainmap 与 ISO 21496-1 / Google Ultra HDR 增益图的编解码纯数值运算。
 * 只做数值运算，不做像素 I/O；像素读写由调用方（Service）负责。
 *
 *   - Apple 解码：hdr_rgb = sdr_rgb * (1 + (headroom-1) * gainmap)，sdr_rgb/gainmap 先经 sRGB EOTF 线性化，结果位于线性 Display P3
 *   - ISO 21496-1 编码：按 Android Ultra HDR 规范的 pixel_gain / log_recovery / recovery 公式
 */
internal static class HdrGainMapCodec
{
    // Display P3 (D65) RGB -> XYZ 的 Y 行系数，用于把线性 P3 像素转成亮度。
    private const double LumaR = 0.228974564;
    private const double LumaG = 0.691738522;
    private const double LumaB = 0.079286914;

    // Apple 真机样本里 HDRHeadroom(0x21) 的有理数值（1.568873048）。
    // 由目标 headroom 反推 MakerNote 时，maker33 固定沿用该值。
    private const long AppleMaker33Numerator = 46219;
    private const long AppleMaker33Denominator = 29460;

    // maker33 < 1.0 分支的代表值。Apple 官方解码只看「maker33 是否 < 1.0」来选分段，
    // 该分支的 stops 结果与 maker33 具体数值无关（见 ComputeAppleHeadroom），
    // 因此用 0.99 表达 headroom < 4.0（stops < 2.0）的源。真机样本里该分支的
    // 具体取值尚未采集到，若 iOS 有额外校验需按真机样本校准。
    private const double AppleMaker33LowHeadroom = 0.99;

    // Apple 官方 headroom 分段函数里 maker48 的分支分界（stops）。
    private const double AppleHeadroomStopsBoundary = 2.3;

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
    /// sRGB / Rec.709 电光传递函数的反函数（OETF），把线性值转成 0..1 非线性值。
    /// Apple 官方文档把增益图描述为 Rec.709 转移，但解码参考（johncf/apple-hdr-heic）
    /// 用 sRGB EOTF，因此这里用对应的 sRGB OETF 做反向编码；8bit 下两者差异可忽略。
    /// </summary>
    public static float SrgbOetf(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        return linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1.0f / 2.4f) - 0.055f;
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
    /// 依据 ISO 21496-1 规范把 0..1 的 recovery 值解码成每像素线性倍率
    /// （pixel_gain = Yhdr/Ysdr，约等于 exp2(log_boost)）。
    /// </summary>
    public static float DecodeIsoRecovery(float recovery, IsoGainMapMetadata metadata)
    {
        recovery = Math.Clamp(recovery, 0f, 1f);
        double logRecovery = Math.Pow(recovery, 1.0 / Math.Max(metadata.Gamma, 1e-9));
        double logBoost = metadata.GainMapMin * (1.0 - logRecovery)
            + metadata.GainMapMax * logRecovery;
        return (float)Math.Pow(2.0, logBoost);
    }

    /// <summary>
    /// 把每像素线性倍率（pixel_gain）转成 Apple 增益图值。
    /// 返回已经过 sRGB OETF 编码的 0..1 增益值（乘以 255 即量化为 8bit）。
    /// </summary>
    public static float EncodeAppleGain(float pixelGain, double headroom)
    {
        double linear = (pixelGain - 1.0) / Math.Max(headroom - 1.0, 1e-9);
        linear = Math.Clamp(linear, 0.0, 1.0);
        return SrgbOetf((float)linear);
    }

    /// <summary>
    /// 由目标 headroom 计算要写入 Apple MakerNote 的 HDRHeadroom(0x21) 与
    /// HDRGain(0x30) 有理数值。依据 Apple 官方文档分段函数的反函数。
    /// </summary>
    public static (HdrSignedRational Maker33, HdrSignedRational Maker48) ComputeAppleMakerValues(double targetHeadroom)
    {
        double stops = Math.Log2(Math.Max(targetHeadroom, 1.0));

        // Apple 官方分段函数（ComputeAppleHeadroom）的可表达区间：
        //   [2.3, 3.0]  maker33 >= 1.0, maker48 <= 0.01 -> stops = -70*m48 + 3.0
        //   [2.0, 2.3)  maker33 >= 1.0, maker48  > 0.01 -> stops = -0.303*m48 + 2.303
        //   [1.6, 1.8]  maker33 <  1.0, maker48 <= 0.01 -> stops = -20*m48 + 1.8
        //   [1.5, 1.6)  maker33 <  1.0, maker48  > 0.01 -> stops = -0.101*m48 + 1.601
        // 旧实现只覆盖前两段（maker33 固定为真机样本值），headroom < 4.0 的源
        // 会被钳成 4.0；这里补全后两段。
        double maker33;
        double maker48;
        if (stops >= AppleHeadroomStopsBoundary)
        {
            maker33 = (double)AppleMaker33Numerator / AppleMaker33Denominator;
            maker48 = (3.0 - stops) / 70.0;
        }
        else if (stops >= 2.0)
        {
            maker33 = (double)AppleMaker33Numerator / AppleMaker33Denominator;
            maker48 = (2.303 - stops) / 0.303;
        }
        else if (stops >= 1.6)
        {
            maker33 = AppleMaker33LowHeadroom;
            maker48 = (1.8 - stops) / 20.0;
        }
        else
        {
            maker33 = AppleMaker33LowHeadroom;
            maker48 = (1.601 - stops) / 0.101;
        }

        // 低于 1.5 stops（headroom < 2.83x）在 Apple MakerNote 中无法表达，
        // 钳到下限；调用方会比较读回值并记录警告。
        maker48 = Math.Clamp(maker48, 0.0, 1.0);
        return (ToSignedRational(maker33), ToSignedRational(maker48));
    }

    private static HdrSignedRational ToSignedRational(double value)
    {
        // 连分数法求分母 <= 10^7 的最佳有理数逼近。
        const long maxDenominator = 10_000_000L;
        double x = value;
        long p0 = 0, q0 = 1, p1 = 1, q1 = 0;

        while (true)
        {
            long a = (long)Math.Floor(x);
            if (a <= 0)
            {
                break;
            }

            long p2 = a * p1 + p0;
            long q2 = a * q1 + q0;
            if (q2 > maxDenominator || p2 > int.MaxValue || p2 < int.MinValue)
            {
                break;
            }

            p0 = p1;
            q0 = q1;
            p1 = p2;
            q1 = q2;

            double frac = x - a;
            if (frac < 1e-12)
            {
                break;
            }

            x = 1.0 / frac;
        }

        return q1 > 0
            ? new HdrSignedRational(p1, q1)
            : new HdrSignedRational((long)Math.Round(value * 1_000_000L), 1_000_000L);
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
/// 32 位有符号有理数（TIFF SRATIONAL，type 10）。
/// </summary>
internal readonly record struct HdrSignedRational(long Numerator, long Denominator);

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
