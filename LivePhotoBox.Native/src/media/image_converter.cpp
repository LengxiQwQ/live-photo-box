#include "media/image_converter.h"

#include "foundation/internal.h"
#include "media/heic_backend.h"
#include "media/jpeg_backend.h"
#include "media/media_inspector.h"
#include "platform/windows_filesystem.h"

#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;

namespace lpb::media {
namespace {
lpb_result fast_file_copy(lpb_context* context, const char* input_path, const char* output_path) noexcept {
    if (!context || !input_path || !output_path) return LPB_RESULT_INVALID_ARGUMENT;
    const fs::path source = utf8_to_path(input_path);
    const fs::path destination = utf8_to_path(output_path);
    windows_owned_output output;
    if (!output.create(destination, L"lpb-image-copy") || !output.copy_from_readonly(source) ||
        !output.publish_no_replace(destination)) {
        set_error(context, "Failed to copy image through an owned output.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

bool read_header(const char* input_path, uint8_t (&header)[16]) noexcept {
    std::ifstream input(utf8_to_path(input_path), std::ios::binary);
    if (!input.is_open()) return false;
    input.read(reinterpret_cast<char*>(header), sizeof(header));
    return input.gcount() >= 8;
}
} // namespace

lpb_result convert_image_file(lpb_context* context, const char* input_image_path, const char* output_image_path,
    lpb_image_container target_container, int32_t quality, int32_t* out_reencoded) noexcept {
    if (!context || !input_image_path || !output_image_path || paths_alias(input_image_path, output_image_path)) {
        if (context) set_error(context, "Image conversion requires distinct input and output paths.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (out_reencoded) *out_reencoded = 0;
    uint8_t header[16]{};
    if (!read_header(input_image_path, header)) {
        set_error(context, "Cannot read the input image header.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const lpb_image_container source_container = detect_image_container(std::span<const uint8_t>(header, sizeof(header)));
    if (target_container != LPB_IMAGE_CONTAINER_JPEG && target_container != LPB_IMAGE_CONTAINER_HEIC) {
        set_error(context, "Only JPEG and HEIC targets are supported.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (source_container == target_container && source_container != LPB_IMAGE_CONTAINER_UNKNOWN) {
        return fast_file_copy(context, input_image_path, output_image_path);
    }

    if (source_container == LPB_IMAGE_CONTAINER_JPEG && target_container == LPB_IMAGE_CONTAINER_HEIC) {
        jpeg_rgb_image jpeg;
        const lpb_result decoded = decode_jpeg_rgb_file(context, input_image_path, jpeg);
        if (decoded != LPB_RESULT_OK) return decoded;
        pixel_surface pixels{};
        pixels.width = jpeg.width;
        pixels.height = jpeg.height;
        pixels.channels = 3;
        pixels.signal_bit_depth = 8;
        pixels.storage_bit_depth = 8;
        pixels.stride = static_cast<uint64_t>(jpeg.width) * 3;
        pixels.color.has_icc = !jpeg.icc_profile.empty();
        pixels.color.icc_profile = std::move(jpeg.icc_profile);
        pixels.pixels = std::move(jpeg.pixels);
        const lpb_result encoded = encode_heic_file(context, output_image_path, pixels, quality);
        if (encoded == LPB_RESULT_OK && out_reencoded) *out_reencoded = 1;
        return encoded;
    }

    if (source_container == LPB_IMAGE_CONTAINER_HEIC && target_container == LPB_IMAGE_CONTAINER_JPEG) {
        pixel_surface pixels{};
        heic_image_facts facts{};
        const lpb_result decoded = decode_heic_primary_file(context, input_image_path, pixels, &facts);
        if (decoded != LPB_RESULT_OK) return decoded;
        // JPEG is 8-bit SDR. P5 owns an explicit HDR/GainMap transform/degradation
        // policy; this generic converter must not silently quantize a 10-bit/HDR source.
        if (pixels.signal_bit_depth > 8 || pixels.storage_bit_depth > 8 || facts.hdr_relevant) {
            set_error(context, "HEIC source is 10-bit or HDR-relevant; explicit P5 degradation/transform policy is required.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const lpb_result encoded = encode_rgb_jpeg_file(context, output_image_path, pixels.width, pixels.height,
            pixels.pixels, quality);
        if (encoded == LPB_RESULT_OK && out_reencoded) *out_reencoded = 1;
        return encoded;
    }

    set_error(context, "Input is not a supported JPEG or HEIC image container.");
    return LPB_RESULT_INVALID_ARGUMENT;
}
} // namespace lpb::media
