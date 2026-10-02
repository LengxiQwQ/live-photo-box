#include "media/gainmap_math.h"

#include <algorithm>
#include <cmath>
#include <cstddef>
#include <limits>
#include <vector>

namespace lpb::media {
namespace {

bool finite_range(double value) noexcept {
    return std::isfinite(value) && value >= 0.0 && value <= 1.0;
}

double rec709_eotf(double encoded) noexcept {
    return encoded < 0.081
        ? encoded / 4.5
        : std::pow((encoded + 0.099) / 1.099, 1.0 / 0.45);
}

} // namespace

bool apple_gainmap_headroom(double maker_note_33, double maker_note_48,
    double& out_headroom) noexcept {
    out_headroom = 0.0;
    if (!std::isfinite(maker_note_33) || !std::isfinite(maker_note_48) ||
        maker_note_33 < 0.0 || maker_note_48 < 0.0) {
        return false;
    }

    double stops{};
    if (maker_note_33 < 1.0) {
        stops = maker_note_48 <= 0.01
            ? -20.0 * maker_note_48 + 1.8
            : -0.101 * maker_note_48 + 1.601;
    } else {
        stops = maker_note_48 <= 0.01
            ? -70.0 * maker_note_48 + 3.0
            : -0.303 * maker_note_48 + 2.303;
    }
    // A non-positive mapping has no HDR range. Reject it instead of silently
    // turning an unrepresentable Apple mapping into a neutral 1x GainMap.
    if (!std::isfinite(stops) || stops <= 0.0) return false;
    const double headroom = std::exp2(stops);
    if (!std::isfinite(headroom) || headroom <= 1.0) return false;
    out_headroom = headroom;
    return true;
}

bool apple_gain_to_iso_recovery(double encoded_sample, double maker_note_33,
    double maker_note_48, double& out_recovery,
    iso_gainmap_metadata& in_out_metadata) noexcept {
    out_recovery = 0.0;
    if (!finite_range(encoded_sample)) return false;
    double headroom{};
    if (!apple_gainmap_headroom(maker_note_33, maker_note_48, headroom)) return false;

    const double linear_gain_sample = rec709_eotf(encoded_sample);
    const double pixel_gain = 1.0 + (headroom - 1.0) * linear_gain_sample;
    const double max_log_gain = std::log2(headroom);
    if (!std::isfinite(pixel_gain) || pixel_gain < 1.0 ||
        !std::isfinite(max_log_gain) || max_log_gain <= 0.0) {
        return false;
    }

    const double recovery = std::log2(pixel_gain) / max_log_gain;
    if (!std::isfinite(recovery) || recovery < 0.0 || recovery > 1.0) return false;
    out_recovery = recovery;
    for (std::size_t channel = 0; channel < 3; ++channel) {
        in_out_metadata.gain_map_min[channel] = 0.0;
        in_out_metadata.gain_map_max[channel] = max_log_gain;
        in_out_metadata.gamma[channel] = 1.0;
        in_out_metadata.offset_sdr[channel] = 0.0;
        in_out_metadata.offset_hdr[channel] = 0.0;
    }
    in_out_metadata.hdr_capacity_min = 0.0;
    in_out_metadata.hdr_capacity_max = max_log_gain;
    in_out_metadata.base_rendition_is_hdr = false;
    return true;
}

bool convert_apple_gainmap_rgb_to_iso(std::span<const uint8_t> apple_rgb,
    double maker_note_33, double maker_note_48,
    std::span<uint8_t> iso_rgb, iso_gainmap_metadata& out_metadata) noexcept {
    if (apple_rgb.empty() || apple_rgb.size() % 3 != 0 || iso_rgb.size() != apple_rgb.size()) return false;
    try {
        std::vector<uint8_t> converted(apple_rgb.size());
        iso_gainmap_metadata metadata{};
        for (size_t offset = 0; offset < apple_rgb.size(); offset += 3) {
            const uint8_t sample = apple_rgb[offset];
            if (apple_rgb[offset + 1] != sample || apple_rgb[offset + 2] != sample) return false;
            double recovery{};
            if (!apple_gain_to_iso_recovery(static_cast<double>(sample) / 255.0,
                    maker_note_33, maker_note_48, recovery, metadata)) return false;
            const long quantized = std::lround(recovery * 255.0);
            if (quantized < 0 || quantized > 255) return false;
            const auto value = static_cast<uint8_t>(quantized);
            converted[offset] = value;
            converted[offset + 1] = value;
            converted[offset + 2] = value;
        }
        std::copy(converted.begin(), converted.end(), iso_rgb.begin());
        out_metadata = metadata;
        return true;
    } catch (...) {
        return false;
    }
}

bool decode_iso_gainmap_sample(double sample, double gain_map_min,
    double gain_map_max, double gamma, double& out_linear_gain) noexcept {
    out_linear_gain = 0.0;
    if (!finite_range(sample) || !std::isfinite(gain_map_min) ||
        !std::isfinite(gain_map_max) || !std::isfinite(gamma) ||
        gain_map_min > gain_map_max || gamma <= 0.0) {
        return false;
    }
    const double recovery = std::pow(sample, 1.0 / gamma);
    const double log_gain = gain_map_min * (1.0 - recovery) + gain_map_max * recovery;
    const double linear_gain = std::exp2(log_gain);
    if (!std::isfinite(recovery) || recovery < 0.0 || recovery > 1.0 ||
        !std::isfinite(linear_gain) || linear_gain <= 0.0) {
        return false;
    }
    out_linear_gain = linear_gain;
    return true;
}

bool apply_iso_gainmap_channel(double base_linear, double sample,
    double gain_map_min, double gain_map_max, double gamma,
    double offset_sdr, double offset_hdr, double weight_factor,
    double& out_linear) noexcept {
    out_linear = 0.0;
    if (!std::isfinite(base_linear) || base_linear < 0.0 ||
        !std::isfinite(offset_sdr) || offset_sdr < 0.0 ||
        !std::isfinite(offset_hdr) || offset_hdr < 0.0 ||
        !finite_range(weight_factor)) {
        return false;
    }
    double full_gain{};
    if (!decode_iso_gainmap_sample(sample, gain_map_min, gain_map_max, gamma, full_gain)) return false;
    const double log_gain = std::log2(full_gain) * weight_factor;
    const double result = (base_linear + offset_sdr) * std::exp2(log_gain) - offset_hdr;
    if (!std::isfinite(result)) return false;
    out_linear = result;
    return true;
}

} // namespace lpb::media
