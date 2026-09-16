#pragma once

#include "livephotobox_native.h"
#include <cstdint>
#include <span>
#include <vector>

namespace lpb::media {

// Native-only codec seam. No libjpeg-turbo type crosses this header.
struct jpeg_rgb_image {
    uint32_t width{};
    uint32_t height{};
    std::vector<uint8_t> icc_profile;
    std::vector<uint8_t> pixels;
};

// Decode common JPEG input into project-owned RGB pixels. The caller decides
// container/metadata policy; this backend never claims JPEG structural truth.
lpb_result decode_jpeg_rgb_file(lpb_context* context, const char* input_path,
    jpeg_rgb_image& output) noexcept;

lpb_result encode_rgb_jpeg_file(lpb_context* context, const char* output_path,
    uint32_t width, uint32_t height, std::span<const uint8_t> rgb, int32_t quality) noexcept;

lpb_result transform_jpeg_losslessly(lpb_context* context, const char* input_path,
    const char* output_path, int32_t transform) noexcept;

// Returns the build-time codec identity without exposing any codec API type.
const char* jpeg_backend_version() noexcept;

} // namespace lpb::media
