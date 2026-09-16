#include "media/heic_backend.h"

#include "foundation/internal.h"
#include "platform/windows_filesystem.h"

#include <libheif/heif.h>

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>

namespace fs = std::filesystem;

namespace {
constexpr uint64_t kMaxCompressedBytes = 512ull * 1024ull * 1024ull;
constexpr uint64_t kMaxPixels = 100ull * 1024ull * 1024ull;

bool okay(heif_error error) noexcept { return error.code == heif_error_Ok; }

bool read_file_bounded(const fs::path& path, std::vector<uint8_t>& bytes) noexcept {
    std::error_code ec;
    const auto size = fs::file_size(path, ec);
    if (ec || size == 0 || size > kMaxCompressedBytes || size > std::numeric_limits<size_t>::max()) return false;
    try { bytes.resize(static_cast<size_t>(size)); }
    catch (...) { return false; }
    std::ifstream input(path, std::ios::binary);
    if (!input.is_open()) return false;
    input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return input.good() || input.gcount() == static_cast<std::streamsize>(bytes.size());
}

bool safe_dimensions(uint32_t width, uint32_t height, uint32_t channels, uint32_t bytes_per_sample,
    uint64_t& row_bytes, uint64_t& total_bytes) noexcept {
    if (width == 0 || height == 0 || channels == 0 || bytes_per_sample == 0 ||
        static_cast<uint64_t>(width) * height > kMaxPixels) return false;
    row_bytes = static_cast<uint64_t>(width) * channels * bytes_per_sample;
    total_bytes = row_bytes * height;
    return row_bytes / channels / bytes_per_sample == width &&
        total_bytes / height == row_bytes && total_bytes <= std::numeric_limits<size_t>::max();
}

void read_color_facts(const heif_image_handle* handle, lpb::media::pixel_color_facts& output) noexcept {
    output = {};
    const size_t icc_size = heif_image_handle_get_raw_color_profile_size(handle);
    if (icc_size > 0 && icc_size <= 4u * 1024u * 1024u) {
        try { output.icc_profile.resize(icc_size); }
        catch (...) { output = {}; return; }
        if (okay(heif_image_handle_get_raw_color_profile(handle, output.icc_profile.data()))) {
            output.has_icc = true;
        } else {
            output.icc_profile.clear();
        }
    }
    heif_color_profile_nclx* nclx = nullptr;
    if (okay(heif_image_handle_get_nclx_color_profile(handle, &nclx)) && nclx) {
        output.has_nclx = true;
        output.primaries = static_cast<uint16_t>(nclx->color_primaries);
        output.transfer = static_cast<uint16_t>(nclx->transfer_characteristics);
        output.matrix = static_cast<uint16_t>(nclx->matrix_coefficients);
        output.full_range = nclx->full_range_flag != 0;
        heif_nclx_color_profile_free(nclx);
    }
}

bool is_hdr_transfer(uint16_t transfer) noexcept {
    return transfer == heif_transfer_characteristic_ITU_R_BT_2100_0_PQ ||
        transfer == heif_transfer_characteristic_ITU_R_BT_2100_0_HLG;
}

bool fill_auxiliary_facts(const heif_image_handle* auxiliary, lpb::media::heic_auxiliary_facts& output) noexcept {
    output = {};
    if (!auxiliary) return false;
    const int width = heif_image_handle_get_width(auxiliary);
    const int height = heif_image_handle_get_height(auxiliary);
    const int bits = heif_image_handle_get_luma_bits_per_pixel(auxiliary);
    if (width <= 0 || height <= 0 || bits <= 0 || static_cast<uint64_t>(width) * height > kMaxPixels) return false;
    output.item_id = heif_image_handle_get_item_id(auxiliary);
    output.width = static_cast<uint32_t>(width);
    output.height = static_cast<uint32_t>(height);
    output.bit_depth = static_cast<uint32_t>(bits);
    output.has_alpha = heif_image_handle_has_alpha_channel(auxiliary) != 0;
    read_color_facts(auxiliary, output.color);
    output.hdr_relevant = output.bit_depth > 8 || is_hdr_transfer(output.color.transfer);
    const char* type = nullptr;
    const heif_error type_result = heif_image_handle_get_auxiliary_type(auxiliary, &type);
    if (okay(type_result) && type) output.type = type;
    if (type) heif_image_handle_release_auxiliary_type(auxiliary, &type);
    return output.item_id != 0;
}

bool fill_facts(const heif_image_handle* primary, lpb::media::heic_image_facts& output) noexcept {
    const int width = heif_image_handle_get_width(primary);
    const int height = heif_image_handle_get_height(primary);
    const int bits = heif_image_handle_get_luma_bits_per_pixel(primary);
    if (width <= 0 || height <= 0 || bits <= 0 || static_cast<uint64_t>(width) * height > kMaxPixels) return false;
    output = {};
    output.primary_item_id = heif_image_handle_get_item_id(primary);
    output.width = static_cast<uint32_t>(width);
    output.height = static_cast<uint32_t>(height);
    output.bit_depth = static_cast<uint32_t>(bits);
    output.has_alpha = heif_image_handle_has_alpha_channel(primary) != 0;
    read_color_facts(primary, output.color);
    output.hdr_relevant = output.bit_depth > 8 || is_hdr_transfer(output.color.transfer);

    const int count = heif_image_handle_get_number_of_auxiliary_images(primary, 0);
    if (count < 0 || count > 64) return false;
    std::vector<heif_item_id> ids(static_cast<size_t>(count));
    if (count && heif_image_handle_get_list_of_auxiliary_image_IDs(primary, 0, ids.data(), count) != count) return false;
    try { output.auxiliaries.reserve(static_cast<size_t>(count)); }
    catch (...) { return false; }
    for (const auto id : ids) {
        heif_image_handle* aux = nullptr;
        if (!okay(heif_image_handle_get_auxiliary_image_handle(primary, id, &aux)) || !aux) return false;
        lpb::media::heic_auxiliary_facts fact{};
        const bool valid = fill_auxiliary_facts(aux, fact) && fact.item_id == id;
        heif_image_handle_release(aux);
        if (!valid) return false;
        output.auxiliaries.push_back(std::move(fact));
    }
    return true;
}

struct heif_context_owner {
    heif_context* value{heif_context_alloc()};
    ~heif_context_owner() { if (value) heif_context_free(value); }
};
struct heif_handle_owner {
    heif_image_handle* value{};
    ~heif_handle_owner() { if (value) heif_image_handle_release(value); }
};
struct heif_image_owner {
    heif_image* value{};
    ~heif_image_owner() { if (value) heif_image_release(value); }
};
struct heif_encoder_owner {
    heif_encoder* value{};
    ~heif_encoder_owner() { if (value) heif_encoder_release(value); }
};
struct heif_decode_options_owner {
    heif_decoding_options* value{heif_decoding_options_alloc()};
    ~heif_decode_options_owner() { if (value) heif_decoding_options_free(value); }
};
struct heif_encode_options_owner {
    heif_encoding_options* value{heif_encoding_options_alloc()};
    ~heif_encode_options_owner() { if (value) heif_encoding_options_free(value); }
};
struct heif_nclx_owner {
    heif_color_profile_nclx* value{heif_nclx_color_profile_alloc()};
    ~heif_nclx_owner() { if (value) heif_nclx_color_profile_free(value); }
};

lpb_result decode_image_handle(lpb_context* context, const heif_image_handle* handle,
    lpb::media::pixel_surface& output) noexcept {
    output = {};
    if (!context || !handle) return LPB_RESULT_INVALID_ARGUMENT;
    const int source_bits = heif_image_handle_get_luma_bits_per_pixel(handle);
    if (source_bits < 8 || source_bits > 16) {
        set_error(context, "HEIC image uses an unsupported source bit depth.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    heif_image_owner decoded;
    heif_decode_options_owner options;
    if (!options.value) {
        set_error(context, "libheif could not allocate decode options.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    options.value->strict_decoding = 1;
    options.value->convert_hdr_to_8bit = 0;
    options.value->output_image_nclx_profile_passthrough = 1;
    options.value->num_library_threads = 1;
    options.value->num_codec_threads = 1;
    const heif_chroma chroma = source_bits > 8 ? heif_chroma_interleaved_RRGGBB_LE : heif_chroma_interleaved_RGB;
    if (!okay(heif_decode_image(handle, &decoded.value, heif_colorspace_RGB, chroma, options.value)) || !decoded.value) {
        set_error(context, "libheif/libde265 could not decode the HEIC image without color or bit-depth fallback.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const int width = heif_image_get_primary_width(decoded.value);
    const int height = heif_image_get_primary_height(decoded.value);
    const int range_bits = heif_image_get_bits_per_pixel_range(decoded.value, heif_channel_interleaved);
    const uint32_t storage_bits = source_bits > 8 ? 16u : 8u;
    uint64_t row_bytes{}, total_bytes{};
    size_t source_stride{};
    const uint8_t* source = heif_image_get_plane_readonly2(decoded.value, heif_channel_interleaved, &source_stride);
    if (!source || width <= 0 || height <= 0 || range_bits < 8 || range_bits > 16 ||
        !safe_dimensions(static_cast<uint32_t>(width), static_cast<uint32_t>(height), 3, storage_bits / 8, row_bytes, total_bytes) ||
        source_stride < row_bytes) {
        set_error(context, "HEIC decoded pixel plane has invalid dimensions or stride.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    try { output.pixels.resize(static_cast<size_t>(total_bytes)); }
    catch (...) { set_error(context, "HEIC decoded pixel allocation failed."); return LPB_RESULT_INTERNAL_ERROR; }
    for (uint32_t y = 0; y < static_cast<uint32_t>(height); ++y) {
        std::memcpy(output.pixels.data() + static_cast<size_t>(y * row_bytes), source + y * source_stride, static_cast<size_t>(row_bytes));
    }
    output.width = static_cast<uint32_t>(width);
    output.height = static_cast<uint32_t>(height);
    output.channels = 3;
    output.signal_bit_depth = static_cast<uint32_t>(range_bits);
    output.storage_bit_depth = storage_bits;
    output.stride = row_bytes;
    read_color_facts(handle, output.color);
    return LPB_RESULT_OK;
}

heif_error write_owned_output(heif_context*, const void* bytes, size_t size, void* user) {
    auto* output = static_cast<windows_owned_output*>(user);
    if (output && output->write_all(std::span<const uint8_t>(static_cast<const uint8_t*>(bytes), size))) return {heif_error_Ok, heif_suberror_Unspecified, ""};
    return {heif_error_Encoding_error, heif_suberror_Cannot_write_output_data, "LivePhotoBox owned output write failed"};
}
} // namespace

namespace lpb::media {

const char* heic_backend_version() noexcept { return "libheif " LIBHEIF_VERSION; }

lpb_result inspect_heic_file(lpb_context* context, const char* input_path, heic_image_facts& output) noexcept {
    output = {};
    if (!context || !input_path) return LPB_RESULT_INVALID_ARGUMENT;
    std::vector<uint8_t> bytes;
    if (!read_file_bounded(utf8_to_path(input_path), bytes)) {
        set_error(context, "HEIC input is unavailable or exceeds the configured safety limit.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    heif_context_owner file;
    heif_handle_owner primary;
    if (!file.value || !okay(heif_context_read_from_memory_without_copy(file.value, bytes.data(), bytes.size(), nullptr)) ||
        !okay(heif_context_get_primary_image_handle(file.value, &primary.value)) || !primary.value ||
        !fill_facts(primary.value, output)) {
        set_error(context, "libheif rejected the HEIC primary image or its auxiliary graph.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    return LPB_RESULT_OK;
}

lpb_result decode_heic_primary_file(lpb_context* context, const char* input_path, pixel_surface& output,
    heic_image_facts* out_facts) noexcept {
    output = {};
    heic_image_facts facts{};
    if (!context || !input_path) return LPB_RESULT_INVALID_ARGUMENT;
    std::vector<uint8_t> bytes;
    if (!read_file_bounded(utf8_to_path(input_path), bytes)) {
        set_error(context, "HEIC input is unavailable or exceeds the configured safety limit.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    heif_context_owner file;
    heif_handle_owner primary;
    if (!file.value ||
        !okay(heif_context_read_from_memory_without_copy(file.value, bytes.data(), bytes.size(), nullptr)) ||
        !okay(heif_context_get_primary_image_handle(file.value, &primary.value)) || !primary.value ||
        !fill_facts(primary.value, facts)) {
        set_error(context, "libheif rejected the HEIC primary image or its auxiliary graph.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const lpb_result decoded = decode_image_handle(context, primary.value, output);
    if (decoded != LPB_RESULT_OK) return decoded;
    if (out_facts) *out_facts = std::move(facts);
    return LPB_RESULT_OK;
}

lpb_result decode_heic_auxiliary_file(lpb_context* context, const char* input_path,
    uint32_t auxiliary_item_id, pixel_surface& output, heic_auxiliary_facts* out_facts) noexcept {
    output = {};
    if (out_facts) *out_facts = {};
    if (!context || !input_path || auxiliary_item_id == 0) return LPB_RESULT_INVALID_ARGUMENT;
    std::vector<uint8_t> bytes;
    if (!read_file_bounded(utf8_to_path(input_path), bytes)) {
        set_error(context, "HEIC input is unavailable or exceeds the configured safety limit.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    heif_context_owner file;
    heif_handle_owner primary;
    heif_handle_owner auxiliary;
    if (!file.value ||
        !okay(heif_context_read_from_memory_without_copy(file.value, bytes.data(), bytes.size(), nullptr)) ||
        !okay(heif_context_get_primary_image_handle(file.value, &primary.value)) || !primary.value ||
        !okay(heif_image_handle_get_auxiliary_image_handle(primary.value, auxiliary_item_id, &auxiliary.value)) || !auxiliary.value) {
        set_error(context, "HEIC auxiliary item is absent or cannot be opened through the primary auxiliary graph.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    heic_auxiliary_facts facts{};
    if (!fill_auxiliary_facts(auxiliary.value, facts) || facts.item_id != auxiliary_item_id) {
        set_error(context, "HEIC auxiliary item has invalid pixel or color facts.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const lpb_result decoded = decode_image_handle(context, auxiliary.value, output);
    if (decoded != LPB_RESULT_OK) return decoded;
    if (out_facts) *out_facts = std::move(facts);
    return LPB_RESULT_OK;
}

lpb_result encode_heic_file(lpb_context* context, const char* output_path, const pixel_surface& input,
    int32_t quality) noexcept {
    if (!context || !output_path || input.width == 0 || input.height == 0 || input.channels != 3 ||
        input.signal_bit_depth != 8 || input.storage_bit_depth != 8 || input.stride != static_cast<uint64_t>(input.width) * 3 ||
        quality < 1 || quality > 100 || !heif_have_encoder_for_format(heif_compression_HEVC)) {
        if (context) set_error(context, "HEIC encode requires an 8-bit RGB surface and the libheif x265 HEVC encoder.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    uint64_t row_bytes{}, total_bytes{};
    if (!safe_dimensions(input.width, input.height, 3, 1, row_bytes, total_bytes) || input.pixels.size() != total_bytes) {
        set_error(context, "HEIC encode received an invalid project-owned pixel surface.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    heif_context_owner file;
    heif_image_owner image;
    heif_encoder_owner encoder;
    heif_encode_options_owner options;
    heif_nclx_owner nclx;
    if (!file.value || !options.value || !nclx.value ||
        !okay(heif_image_create(static_cast<int>(input.width), static_cast<int>(input.height), heif_colorspace_RGB, heif_chroma_interleaved_RGB, &image.value)) ||
        !image.value || !okay(heif_image_add_plane(image.value, heif_channel_interleaved, static_cast<int>(input.width), static_cast<int>(input.height), 8))) {
        set_error(context, "libheif could not allocate the HEIC encoder image.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    size_t dst_stride{};
    uint8_t* destination = heif_image_get_plane2(image.value, heif_channel_interleaved, &dst_stride);
    if (!destination || dst_stride < row_bytes) {
        set_error(context, "libheif returned an invalid HEIC encoder pixel plane.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    for (uint32_t y = 0; y < input.height; ++y) std::memcpy(destination + y * dst_stride, input.pixels.data() + static_cast<size_t>(y * row_bytes), static_cast<size_t>(row_bytes));
    const bool has_icc = input.color.has_icc && !input.color.icc_profile.empty();
    if (has_icc) {
        if (!okay(heif_image_set_raw_color_profile(image.value, "prof", input.color.icc_profile.data(), input.color.icc_profile.size()))) {
            set_error(context, "libheif could not attach the source ICC profile to HEIC output.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
    } else {
        // JPEG surfaces without a source profile are explicitly encoded as
        // sRGB. This is an output policy for untagged SDR pixels, never a
        // transform or relabeling of an ICC-tagged source.
        nclx.value->version = 1;
        nclx.value->color_primaries = heif_color_primaries_ITU_R_BT_709_5;
        nclx.value->transfer_characteristics = heif_transfer_characteristic_IEC_61966_2_1;
        nclx.value->matrix_coefficients = heif_matrix_coefficients_ITU_R_BT_601_6;
        nclx.value->full_range_flag = 1;
    }
    if ((!has_icc && !okay(heif_image_set_nclx_color_profile(image.value, nclx.value))) ||
        !okay(heif_context_get_encoder_for_format(file.value, heif_compression_HEVC, &encoder.value)) || !encoder.value ||
        !okay(heif_encoder_set_lossy_quality(encoder.value, quality)) ||
        !okay(heif_context_encode_image(file.value, image.value, encoder.value, options.value, nullptr))) {
        set_error(context, "libheif/x265 HEIC encode setup failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    windows_owned_output output;
    const fs::path destination_path = utf8_to_path(output_path);
    if (!output.create(destination_path, L"lpb-heic-encode")) {
        set_error(context, "Failed to create an owned HEIC staging file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    heif_writer writer{};
    writer.writer_api_version = 1;
    writer.write = write_owned_output;
    if (!okay(heif_context_write(file.value, &writer, &output)) || !output.flush() || !output.publish_no_replace(destination_path)) {
        output.abort();
        set_error(context, "libheif/x265 HEIC encode failed before publication.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}
} // namespace lpb::media
