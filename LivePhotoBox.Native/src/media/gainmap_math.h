#pragma once

#include <array>
#include <cstdint>
#include <span>

namespace lpb::media {

struct iso_gainmap_metadata {
    std::array<double, 3> gain_map_min{};
    std::array<double, 3> gain_map_max{};
    std::array<double, 3> gamma{};
    std::array<double, 3> offset_sdr{};
    std::array<double, 3> offset_hdr{};
    double hdr_capacity_min{};
    double hdr_capacity_max{};
    bool base_rendition_is_hdr{};
};

// Apple documents MakerNote 33/48 as the parameters used to derive the
// GainMap headroom. These functions reject invalid inputs instead of silently
// clamping or inventing metadata.
bool apple_gainmap_headroom(double maker_note_33, double maker_note_48,
    double& out_headroom) noexcept;

// Converts one decoded Apple Rec.709-encoded gain-map sample into an ISO
// 21496-1 normalized recovery sample for the supplied per-channel metadata.
bool apple_gain_to_iso_recovery(double encoded_sample, double maker_note_33,
    double maker_note_48, double& out_recovery,
    iso_gainmap_metadata& in_out_metadata) noexcept;

// Converts a decoded Apple single-channel GainMap represented by equal RGB
// bytes into an ISO recovery JPEG RGB buffer. It rejects color/non-neutral
// maps rather than silently reducing them to luminance.
bool convert_apple_gainmap_rgb_to_iso(std::span<const uint8_t> apple_rgb,
    double maker_note_33, double maker_note_48,
    std::span<uint8_t> iso_rgb, iso_gainmap_metadata& out_metadata) noexcept;

// Applies ISO recovery metadata to one normalized gain-map sample.
bool decode_iso_gainmap_sample(double sample, double gain_map_min,
    double gain_map_max, double gamma, double& out_linear_gain) noexcept;

// Applies the decoded ISO gain to one linear base-rendition channel. The
// caller supplies the display-capacity blend already derived from metadata;
// values are never silently clamped.
bool apply_iso_gainmap_channel(double base_linear, double sample,
    double gain_map_min, double gain_map_max, double gamma,
    double offset_sdr, double offset_hdr, double weight_factor,
    double& out_linear) noexcept;

} // namespace lpb::media
