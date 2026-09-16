#include "media/jpeg_backend.h"
#include "foundation/internal.h"
#include "platform/windows_filesystem.h"

#include <turbojpeg.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <limits>
#include <vector>

namespace fs = std::filesystem;

namespace {

constexpr uint64_t kMaxCompressedBytes = 512ull * 1024ull * 1024ull;
constexpr uint64_t kMaxPixels = 100ull * 1024ull * 1024ull;

#define LPB_STRINGIFY_IMPL(value) #value
#define LPB_STRINGIFY(value) LPB_STRINGIFY_IMPL(value)
constexpr const char kJpegBackendVersion[] = "libjpeg-turbo " LPB_STRINGIFY(TURBOJPEG_VERSION_NUMBER);

bool read_file_bounded(const fs::path& path, std::vector<unsigned char>& bytes) noexcept {
    std::error_code ec;
    const uintmax_t size = fs::file_size(path, ec);
    if (ec || size < 2 || size > kMaxCompressedBytes || size > std::numeric_limits<size_t>::max()) return false;
    try { bytes.resize(static_cast<size_t>(size)); }
    catch (...) { return false; }
    std::ifstream input(path, std::ios::binary);
    if (!input.is_open()) return false;
    input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return input.good() || input.gcount() == static_cast<std::streamsize>(bytes.size());
}

// Returns the byte immediately after the codestream EOI. It deliberately
// reaches EOI through the legal marker hierarchy instead of treating a string
// hit anywhere in a file as JPEG structure.
bool find_jpeg_eoi(const std::vector<unsigned char>& data, size_t& after_eoi) noexcept {
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
    size_t pos = 2;
    bool in_scan = false;
    while (pos + 1 < data.size()) {
        if (!in_scan) {
            if (data[pos++] != 0xFF) return false;
            while (pos < data.size() && data[pos] == 0xFF) ++pos;
            if (pos >= data.size()) return false;
            const unsigned char marker = data[pos++];
            if (marker == 0xD9) { after_eoi = pos; return true; }
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) continue;
            if (pos + 2 > data.size()) return false;
            const size_t length = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
            if (length < 2 || length > data.size() - pos) return false;
            pos += length;
            if (marker == 0xDA) in_scan = true;
            continue;
        }
        if (data[pos++] != 0xFF) continue;
        if (pos >= data.size()) return false;
        const unsigned char marker = data[pos++];
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (marker == 0xD9) { after_eoi = pos; return true; }
        // A progressive JPEG may have another legal marker segment between
        // scans. Re-enter the marker parser at its 0xFF byte.
        pos -= 2;
        in_scan = false;
    }
    return false;
}

bool valid_rgb_layout(uint32_t width, uint32_t height, std::span<const uint8_t> rgb) noexcept {
    if (width == 0 || height == 0 || static_cast<uint64_t>(width) * height > kMaxPixels) return false;
    const uint64_t bytes = static_cast<uint64_t>(width) * height * 3;
    return bytes <= std::numeric_limits<size_t>::max() && rgb.size() == static_cast<size_t>(bytes);
}

int to_turbo_transform(int32_t value) noexcept {
    switch (value) {
    case LPB_JPEG_TRANSFORM_ROTATE_90: return TJXOP_ROT90;
    case LPB_JPEG_TRANSFORM_ROTATE_180: return TJXOP_ROT180;
    case LPB_JPEG_TRANSFORM_ROTATE_270: return TJXOP_ROT270;
    case LPB_JPEG_TRANSFORM_FLIP_HORIZONTAL: return TJXOP_HFLIP;
    case LPB_JPEG_TRANSFORM_FLIP_VERTICAL: return TJXOP_VFLIP;
    case LPB_JPEG_TRANSFORM_TRANSPOSE: return TJXOP_TRANSPOSE;
    case LPB_JPEG_TRANSFORM_TRANSVERSE: return TJXOP_TRANSVERSE;
    default: return -1;
    }
}

} // namespace

namespace lpb::media {

const char* jpeg_backend_version() noexcept {
    return kJpegBackendVersion;
}

lpb_result decode_jpeg_rgb_file(lpb_context* context, const char* input_path,
    jpeg_rgb_image& output) noexcept {
    output = {};
    if (!context || !input_path) {
        if (context) set_error(context, "JPEG decode requires a context and input path.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<unsigned char> source;
    if (!read_file_bounded(utf8_to_path(input_path), source)) {
        set_error(context, "JPEG input is unavailable, too large for the configured safety limit, or malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    size_t after_eoi = 0;
    if (!find_jpeg_eoi(source, after_eoi)) {
        set_error(context, "JPEG decode requires a complete JPEG codestream.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    // Decode only the structurally delimited codestream. A cross-container
    // caller must report such a conversion as partial preservation; it cannot
    // assert that protocol tails, GainMap carriers, or unknown APP payloads
    // have been reattached. DCT transforms take the stricter no-tail path.
    const auto codestream_size = static_cast<unsigned long>(after_eoi);

    tjhandle codec = tjInitDecompress();
    if (!codec) {
        set_error(context, "libjpeg-turbo decoder initialization failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    int width = 0;
    int height = 0;
    int subsampling = 0;
    int color_space = 0;
    if (tjDecompressHeader3(codec, source.data(), codestream_size,
            &width, &height, &subsampling, &color_space) != 0 || width <= 0 || height <= 0 ||
        static_cast<uint64_t>(width) * static_cast<uint64_t>(height) > kMaxPixels ||
        width > std::numeric_limits<int>::max() / 3) {
        tjDestroy(codec);
        set_error(context, "libjpeg-turbo rejected JPEG dimensions or header data.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const uint64_t byte_count = static_cast<uint64_t>(width) * static_cast<uint64_t>(height) * 3;
    if (byte_count > std::numeric_limits<size_t>::max() || byte_count > std::numeric_limits<unsigned long>::max()) {
        tjDestroy(codec);
        set_error(context, "JPEG decode output exceeds the configured allocation limit.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    try {
        output.pixels.resize(static_cast<size_t>(byte_count));
    }
    catch (const std::bad_alloc&) {
        tjDestroy(codec);
        set_error(context, "JPEG decode could not allocate its bounded RGB output.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    const int decoded = tjDecompress2(codec, source.data(), codestream_size,
        output.pixels.data(), width, width * 3, height, TJPF_RGB, TJFLAG_ACCURATEDCT);
    tjDestroy(codec);
    if (decoded != 0) {
        output = {};
        set_error(context, "libjpeg-turbo JPEG decode failed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    output.width = static_cast<uint32_t>(width);
    output.height = static_cast<uint32_t>(height);
    return LPB_RESULT_OK;
}

lpb_result encode_rgb_jpeg_file(lpb_context* context, const char* output_path,
    uint32_t width, uint32_t height, std::span<const uint8_t> rgb, int32_t quality) noexcept {
    if (!context || !output_path || !valid_rgb_layout(width, height, rgb)) {
        if (context) set_error(context, "JPEG encode received invalid dimensions or RGB buffer.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    tjhandle codec = tjInitCompress();
    if (!codec) { set_error(context, "libjpeg-turbo encoder initialization failed."); return LPB_RESULT_INTERNAL_ERROR; }
    unsigned char* encoded = nullptr;
    unsigned long encoded_size = 0;
    const int clamped_quality = std::clamp(quality, 1, 100);
    const int result = tjCompress2(codec, const_cast<unsigned char*>(rgb.data()),
        static_cast<int>(width), 0, static_cast<int>(height), TJPF_RGB,
        &encoded, &encoded_size, TJSAMP_420, clamped_quality, TJFLAG_ACCURATEDCT);
    tjDestroy(codec);
    if (result != 0 || !encoded || encoded_size == 0) {
        if (encoded) tjFree(encoded);
        set_error(context, "libjpeg-turbo JPEG encoding failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    const fs::path output_path_native = utf8_to_path(output_path);
    windows_owned_output output;
    const bool written = output.create(output_path_native, L"lpb-jpeg-encode") &&
        output.write_all(std::span<const uint8_t>(encoded, static_cast<size_t>(encoded_size))) &&
        output.publish_no_replace(output_path_native);
    tjFree(encoded);
    if (!written) {
        output.abort();
        set_error(context, "Failed to publish the libjpeg-turbo encoded JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

lpb_result transform_jpeg_losslessly(lpb_context* context, const char* input_path,
    const char* output_path, int32_t transform) noexcept {
    if (!context || !input_path || !output_path || paths_alias(input_path, output_path)) {
        if (context) set_error(context, "JPEG lossless transform requires distinct input and output paths.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const int operation = to_turbo_transform(transform);
    if (operation < 0) { set_error(context, "Unsupported JPEG lossless transform operation."); return LPB_RESULT_INVALID_ARGUMENT; }

    // A physical transform plus a retained non-default EXIF Orientation would
    // double-rotate at display time. R4 intentionally fails closed until a
    // project-owned Exif rewrite can make that semantic update atomically.
    lpb_preservation_observation observation{};
    observation.struct_size = sizeof(observation);
    if (lpb_capture_preservation_observation(context, input_path, LPB_SOURCE_PROTOCOL_UNKNOWN,
            LPB_IMAGE_CONTAINER_JPEG, &observation) != LPB_RESULT_OK ||
        (observation.flags & LPB_POBS_EXIF_PARSE_ERROR) != 0 ||
        (observation.orientation != 0 && observation.orientation != 1)) {
        set_error(context, "JPEG lossless transform refuses non-default or unverifiable EXIF Orientation to prevent double rotation.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<unsigned char> source;
    if (!read_file_bounded(utf8_to_path(input_path), source)) {
        set_error(context, "JPEG input is unavailable, too large for the configured safety limit, or malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    size_t after_eoi = 0;
    if (!find_jpeg_eoi(source, after_eoi)) { set_error(context, "JPEG lossless transform requires a complete JPEG codestream."); return LPB_RESULT_INVALID_ARGUMENT; }
    if (after_eoi != source.size()) {
        set_error(context, "JPEG lossless transform refuses appended protocol/GainMap data; project-owned structural reassembly is required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    tjhandle codec = tjInitTransform();
    if (!codec) { set_error(context, "libjpeg-turbo transform initialization failed."); return LPB_RESULT_INTERNAL_ERROR; }
    tjtransform xform{};
    xform.op = operation;
    xform.options = TJXOPT_PERFECT; // Reject MCU-unaligned inputs; never crop or re-encode silently.
    unsigned char* transformed = nullptr;
    unsigned long transformed_size = 0;
    const int result = tjTransform(codec, source.data(), static_cast<unsigned long>(source.size()), 1,
        &transformed, &transformed_size, &xform, TJFLAG_ACCURATEDCT);
    tjDestroy(codec);
    if (result != 0 || !transformed || transformed_size == 0) {
        if (transformed) tjFree(transformed);
        set_error(context, "libjpeg-turbo could not perform a perfect DCT-domain transform for this JPEG.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    windows_owned_output output;
    const fs::path destination = utf8_to_path(output_path);
    const bool written = output.create(destination, L"lpb-jpeg-lossless") &&
        output.write_all(std::span<const uint8_t>(transformed, static_cast<size_t>(transformed_size))) &&
        output.publish_no_replace(destination);
    tjFree(transformed);
    if (!written) {
        output.abort();
        set_error(context, "Failed to publish the losslessly transformed JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

} // namespace lpb::media
