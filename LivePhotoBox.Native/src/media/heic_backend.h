#pragma once

#include "livephotobox_native.h"

#include <cstdint>
#include <string>
#include <vector>

namespace lpb::media {

// Project-owned transport surface. Codec types never leave this seam.  Pixels
// are interleaved RGB/RGBA with an explicit stored and signal bit depth so a
// 10-bit source cannot accidentally look like an 8-bit decode to P5.
struct pixel_color_facts {
    bool has_icc{};
    bool has_nclx{};
    uint16_t primaries{};
    uint16_t transfer{};
    uint16_t matrix{};
    bool full_range{};
    std::vector<uint8_t> icc_profile;
};

struct pixel_surface {
    uint32_t width{};
    uint32_t height{};
    uint32_t channels{};
    uint32_t signal_bit_depth{};
    uint32_t storage_bit_depth{};
    uint64_t stride{};
    pixel_color_facts color{};
    std::vector<uint8_t> pixels;
};

struct heic_auxiliary_facts {
    uint32_t item_id{};
    uint32_t width{};
    uint32_t height{};
    uint32_t bit_depth{};
    bool has_alpha{};
    bool hdr_relevant{};
    pixel_color_facts color{};
    std::string type;
};

struct heic_image_facts {
    uint32_t primary_item_id{};
    uint32_t width{};
    uint32_t height{};
    uint32_t bit_depth{};
    bool has_alpha{};
    bool hdr_relevant{};
    pixel_color_facts color{};
    std::vector<heic_auxiliary_facts> auxiliaries;
};

// Codec-only result for a two-image HEIF staging file. The second image is
// deliberately not called an auxiliary here: only the project structural
// writer may attach auxC/auxl and assign its vendor/GainMap semantics.
struct heic_encoded_image_facts {
    uint32_t primary_item_id{};
    uint32_t secondary_item_id{};
    uint32_t primary_width{};
    uint32_t primary_height{};
    uint32_t secondary_width{};
    uint32_t secondary_height{};
};

lpb_result inspect_heic_file(lpb_context* context, const char* input_path,
    heic_image_facts& output) noexcept;
lpb_result decode_heic_primary_file(lpb_context* context, const char* input_path,
    pixel_surface& output, heic_image_facts* out_facts = nullptr) noexcept;
lpb_result decode_heic_auxiliary_file(lpb_context* context, const char* input_path,
    uint32_t auxiliary_item_id, pixel_surface& output, heic_auxiliary_facts* out_facts = nullptr) noexcept;
lpb_result encode_heic_file(lpb_context* context, const char* output_path,
    const pixel_surface& input, int32_t quality) noexcept;
lpb_result encode_heic_primary_and_secondary_file(lpb_context* context, const char* output_path,
    const pixel_surface& primary, const pixel_surface& secondary, int32_t quality,
    heic_encoded_image_facts& output) noexcept;
const char* heic_backend_version() noexcept;

} // namespace lpb::media
