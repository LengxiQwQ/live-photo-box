#include "containers/isobmff_video_profile.h"

#include "containers/isobmff.h"
#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <span>
#include <string_view>
#include <vector>

namespace lpb::media {
namespace {

struct box_ref {
    size_t start{};
    size_t size{};
    size_t header_size{};
    std::array<char, 4> type{};

    size_t payload_start() const noexcept { return start + header_size; }
    size_t end() const noexcept { return start + size; }
    bool is(std::string_view value) const noexcept {
        return value.size() == type.size() &&
            std::memcmp(type.data(), value.data(), type.size()) == 0;
    }
};

bool read_children(
    std::span<const uint8_t> bytes,
    size_t start,
    size_t end,
    std::vector<box_ref>& output,
    bool allow_four_zero_padding = false)
{
    if (start > end || end > bytes.size()) return false;
    size_t position = start;
    while (position < end) {
        if (allow_four_zero_padding && end - position == 4 &&
            bytes[position] == 0 && bytes[position + 1] == 0 &&
            bytes[position + 2] == 0 && bytes[position + 3] == 0) {
            position = end;
            break;
        }
        isobmff_box_header header{};
        if (!try_read_box_header(bytes.data(), position, end, header) ||
            header.size == 0 || header.size > end - position) return false;
        box_ref box{};
        box.start = position;
        box.size = header.size;
        box.header_size = header.header_size;
        std::memcpy(box.type.data(), bytes.data() + position + 4, box.type.size());
        output.push_back(box);
        position += header.size;
    }
    return position == end;
}

bool unique_child(
    const std::vector<box_ref>& boxes,
    std::string_view type,
    box_ref& output,
    bool required,
    video_profile_failure& failure)
{
    size_t count = 0;
    for (const auto& box : boxes) {
        if (!box.is(type)) continue;
        output = box;
        ++count;
    }
    if (count > 1) {
        failure = video_profile_failure::ambiguous;
        return false;
    }
    if (required && count != 1) {
        failure = video_profile_failure::malformed;
        return false;
    }
    return true;
}

uint32_t read_be32(const uint8_t* bytes) noexcept {
    return (static_cast<uint32_t>(bytes[0]) << 24) |
        (static_cast<uint32_t>(bytes[1]) << 16) |
        (static_cast<uint32_t>(bytes[2]) << 8) |
        static_cast<uint32_t>(bytes[3]);
}

uint16_t read_be16(const uint8_t* bytes) noexcept {
    return static_cast<uint16_t>((static_cast<uint16_t>(bytes[0]) << 8) | bytes[1]);
}

void write_be16(uint8_t* bytes, uint16_t value) noexcept {
    bytes[0] = static_cast<uint8_t>(value >> 8);
    bytes[1] = static_cast<uint8_t>(value & 0xffU);
}

class bit_reader {
public:
    explicit bit_reader(std::span<const uint8_t> bytes) noexcept : bytes_(bytes) {}

    bool read(unsigned count, uint32_t& value) noexcept {
        if (count > 32 || count > remaining()) return false;
        value = 0;
        for (unsigned i = 0; i < count; ++i) {
            const size_t index = bit_position_++;
            value = (value << 1) |
                ((bytes_[index / 8] >> (7 - (index % 8))) & 1U);
        }
        return true;
    }

    bool skip(size_t count) noexcept {
        if (count > remaining()) return false;
        bit_position_ += count;
        return true;
    }

    bool read_ue(uint32_t& value) noexcept {
        unsigned leading_zeroes = 0;
        uint32_t bit = 0;
        while (true) {
            if (!read(1, bit)) return false;
            if (bit != 0) break;
            if (++leading_zeroes > 31) return false;
        }
        uint32_t suffix = 0;
        if (leading_zeroes != 0 && !read(leading_zeroes, suffix)) return false;
        const uint64_t decoded = ((uint64_t{1} << leading_zeroes) - 1) + suffix;
        if (decoded > std::numeric_limits<uint32_t>::max()) return false;
        value = static_cast<uint32_t>(decoded);
        return true;
    }

    bool read_se(int32_t& value) noexcept {
        uint32_t code = 0;
        if (!read_ue(code)) return false;
        const int64_t decoded = (code & 1U) != 0
            ? static_cast<int64_t>((static_cast<uint64_t>(code) + 1U) / 2U)
            : -static_cast<int64_t>(code / 2U);
        if (decoded < std::numeric_limits<int32_t>::min() ||
            decoded > std::numeric_limits<int32_t>::max()) return false;
        value = static_cast<int32_t>(decoded);
        return true;
    }

    size_t position() const noexcept { return bit_position_; }

private:
    size_t remaining() const noexcept {
        const size_t total = bytes_.size() * 8;
        return bit_position_ <= total ? total - bit_position_ : 0;
    }
    std::span<const uint8_t> bytes_;
    size_t bit_position_{};
};

struct sps_video_facts {
    uint8_t chroma_format{};
    uint8_t bit_depth_luma{};
    uint8_t bit_depth_chroma{};
    uint8_t full_range_present{};
    uint8_t full_range{};
    uint8_t color_description_present{};
    uint16_t color_primaries{};
    uint16_t transfer_characteristics{};
    uint16_t matrix_coefficients{};
};

struct sps_color_offsets {
    size_t primaries{};
    size_t transfer{};
    size_t matrix{};
    bool present{};
};

struct video_profile_box_layout {
    uint64_t moov_offset{};
    size_t moov_size{};
    size_t codec_config_box_offset{};
    size_t codec_config_offset{};
    size_t codec_config_size{};
    size_t color_box_offset{};
    size_t color_payload_offset{};
    bool color_is_nclx{};
};

bool same_sps_facts(const sps_video_facts& left, const sps_video_facts& right) noexcept {
    return left.chroma_format == right.chroma_format &&
        left.bit_depth_luma == right.bit_depth_luma &&
        left.bit_depth_chroma == right.bit_depth_chroma &&
        left.full_range_present == right.full_range_present &&
        left.full_range == right.full_range &&
        left.color_description_present == right.color_description_present &&
        left.color_primaries == right.color_primaries &&
        left.transfer_characteristics == right.transfer_characteristics &&
        left.matrix_coefficients == right.matrix_coefficients;
}

void copy_sps_facts(const sps_video_facts& facts, isobmff_video_profile& output) noexcept {
    output.chroma_format = facts.chroma_format;
    output.bit_depth_luma = facts.bit_depth_luma;
    output.bit_depth_chroma = facts.bit_depth_chroma;
    output.bitstream_full_range_present = facts.full_range_present;
    output.bitstream_full_range = facts.full_range;
    output.bitstream_color_description_present = facts.color_description_present;
    output.bitstream_color_primaries = facts.color_primaries;
    output.bitstream_transfer_characteristics = facts.transfer_characteristics;
    output.bitstream_matrix_coefficients = facts.matrix_coefficients;
}

bool remove_emulation_prevention(
    std::span<const uint8_t> source,
    std::vector<uint8_t>& rbsp)
{
    rbsp.clear();
    rbsp.reserve(source.size());
    unsigned zero_count = 0;
    for (size_t i = 0; i < source.size(); ++i) {
        const uint8_t value = source[i];
        if (zero_count >= 2 && value == 3) {
            if (i + 1 >= source.size() || source[i + 1] > 3) return false;
            zero_count = 0;
            continue;
        }
        rbsp.push_back(value);
        zero_count = value == 0 ? std::min(zero_count + 1, 2U) : 0;
    }
    return true;
}

bool add_emulation_prevention(
    std::span<const uint8_t> rbsp,
    std::vector<uint8_t>& ebsp)
{
    ebsp.clear();
    ebsp.reserve(rbsp.size());
    unsigned zero_count = 0;
    for (const uint8_t value : rbsp) {
        if (zero_count >= 2 && value <= 3) {
            ebsp.push_back(3);
            zero_count = 0;
        }
        ebsp.push_back(value);
        zero_count = value == 0 ? std::min(zero_count + 1, 2U) : 0;
    }
    return true;
}

bool overwrite_fixed_width_bits(
    std::vector<uint8_t>& bytes,
    size_t bit_position,
    uint8_t value) noexcept
{
    if (bytes.size() > (std::numeric_limits<size_t>::max)() / 8) return false;
    const size_t total_bits = bytes.size() * 8;
    if (bit_position > total_bits || total_bits - bit_position < 8) return false;
    for (unsigned bit = 0; bit < 8; ++bit) {
        const size_t position = bit_position + bit;
        const uint8_t mask = static_cast<uint8_t>(1U << (7U - static_cast<unsigned>(position % 8)));
        const uint8_t source_bit = static_cast<uint8_t>((value >> (7U - bit)) & 1U);
        if (source_bit != 0) bytes[position / 8] = static_cast<uint8_t>(bytes[position / 8] | mask);
        else bytes[position / 8] = static_cast<uint8_t>(bytes[position / 8] & static_cast<uint8_t>(~mask));
    }
    return true;
}

bool parse_avc_sps(
    std::span<const uint8_t> nal,
    sps_video_facts& facts,
    uint8_t& profile,
    uint8_t& compatibility,
    uint8_t& level,
    sps_color_offsets* color_offsets = nullptr)
{
    if (color_offsets) *color_offsets = {};
    if (nal.size() < 4 || (nal[0] & 0x1fU) != 7 || (nal[0] & 0x80U) != 0) return false;
    profile = nal[1];
    compatibility = nal[2];
    level = nal[3];
    std::vector<uint8_t> rbsp;
    if (!remove_emulation_prevention(nal.subspan(1), rbsp) || rbsp.size() < 3) return false;
    bit_reader bits(rbsp);
    uint32_t profile_idc = 0, constraints = 0, level_idc = 0, sps_id = 0;
    if (!bits.read(8, profile_idc) || !bits.read(8, constraints) ||
        (constraints & 0x03U) != 0 || !bits.read(8, level_idc) ||
        !bits.read_ue(sps_id) || sps_id > 31) return false;

    uint32_t value = 0;
    facts.chroma_format = 1;
    facts.bit_depth_luma = 8;
    facts.bit_depth_chroma = 8;
    bool high_profile = false;
    switch (profile_idc) {
    case 44: case 83: case 86: case 100: case 110: case 118: case 122:
    case 128: case 134: case 135: case 138: case 139: case 144: case 244: {
        high_profile = true;
        uint32_t chroma = 0;
        if (!bits.read_ue(chroma) || chroma > 3) return false;
        facts.chroma_format = static_cast<uint8_t>(chroma);
        if (chroma == 3) {
            uint32_t separate_colour_plane = 0;
            if (!bits.read(1, separate_colour_plane)) return false;
        }
        uint32_t luma_minus8 = 0, chroma_minus8 = 0;
        if (!bits.read_ue(luma_minus8) || !bits.read_ue(chroma_minus8) ||
            luma_minus8 > 6 || chroma_minus8 > 6) return false;
        facts.bit_depth_luma = static_cast<uint8_t>(8 + luma_minus8);
        facts.bit_depth_chroma = static_cast<uint8_t>(8 + chroma_minus8);
        if (!bits.read(1, value)) return false; // qpprime_y_zero_transform_bypass_flag
        uint32_t scaling_matrix_present = 0;
        if (!bits.read(1, scaling_matrix_present)) return false;
        if (scaling_matrix_present != 0) {
            const uint32_t scaling_list_count = chroma == 3 ? 12 : 8;
            for (uint32_t i = 0; i < scaling_list_count; ++i) {
                uint32_t present = 0;
                if (!bits.read(1, present)) return false;
                if (present == 0) continue;
                const uint32_t coefficient_count = i < 6 ? 16 : 64;
                int32_t last_scale = 8;
                int32_t next_scale = 8;
                for (uint32_t j = 0; j < coefficient_count; ++j) {
                    if (next_scale != 0) {
                        int32_t delta = 0;
                        if (!bits.read_se(delta)) return false;
                        next_scale = (last_scale + delta + 256) % 256;
                    }
                    if (next_scale != 0) last_scale = next_scale;
                }
            }
        }
        break;
    }
    default:
        break;
    }

    if (!bits.read_ue(value) || value > 12) return false; // log2_max_frame_num_minus4
    uint32_t picture_order_count_type = 0;
    if (!bits.read_ue(picture_order_count_type) || picture_order_count_type > 2) return false;
    if (picture_order_count_type == 0) {
        if (!bits.read_ue(value) || value > 12) return false;
    } else if (picture_order_count_type == 1) {
        uint32_t always_zero = 0, cycle_count = 0;
        int32_t signed_value = 0;
        if (!bits.read(1, always_zero) || !bits.read_se(signed_value) ||
            !bits.read_se(signed_value) || !bits.read_ue(cycle_count) || cycle_count > 256) return false;
        for (uint32_t i = 0; i < cycle_count; ++i) if (!bits.read_se(signed_value)) return false;
    }
    uint32_t max_reference_frames = 0, gaps = 0, width_mbs = 0, height_map_units = 0;
    uint32_t frame_mbs_only = 0, flag = 0;
    if (!bits.read_ue(max_reference_frames) || max_reference_frames > 256 ||
        !bits.read(1, gaps) || !bits.read_ue(width_mbs) || !bits.read_ue(height_map_units) ||
        width_mbs > 65535 || height_map_units > 65535 || !bits.read(1, frame_mbs_only)) return false;
    if (frame_mbs_only == 0 && !bits.read(1, flag)) return false;
    if (!bits.read(1, flag) || !bits.read(1, flag)) return false; // direct_8x8, frame_cropping_flag
    if (flag != 0) {
        for (unsigned i = 0; i < 4; ++i) if (!bits.read_ue(value)) return false;
    }
    uint32_t vui_present = 0;
    if (!bits.read(1, vui_present)) return false;
    if (vui_present != 0) {
        uint32_t present = 0;
        if (!bits.read(1, present)) return false; // aspect_ratio_info_present_flag
        if (present != 0) {
            uint32_t aspect_ratio_idc = 0;
            if (!bits.read(8, aspect_ratio_idc)) return false;
            if (aspect_ratio_idc == 255 && !bits.skip(32)) return false;
        }
        if (!bits.read(1, present)) return false; // overscan_info_present_flag
        if (present != 0 && !bits.read(1, value)) return false;
        if (!bits.read(1, present)) return false; // video_signal_type_present_flag
        if (present != 0) {
            uint32_t video_format = 0, full_range = 0, color_present = 0;
            if (!bits.read(3, video_format) || !bits.read(1, full_range) ||
                !bits.read(1, color_present)) return false;
            facts.full_range_present = 1;
            facts.full_range = static_cast<uint8_t>(full_range);
            if (color_present != 0) {
                uint32_t primaries = 0, transfer = 0, matrix = 0;
                if (color_offsets) {
                    color_offsets->present = true;
                    color_offsets->primaries = bits.position();
                }
                if (!bits.read(8, primaries) || !bits.read(8, transfer) || !bits.read(8, matrix)) return false;
                if (color_offsets) {
                    color_offsets->transfer = color_offsets->primaries + 8;
                    color_offsets->matrix = color_offsets->primaries + 16;
                }
                facts.color_description_present = 1;
                facts.color_primaries = static_cast<uint16_t>(primaries);
                facts.transfer_characteristics = static_cast<uint16_t>(transfer);
                facts.matrix_coefficients = static_cast<uint16_t>(matrix);
            }
        }
    }
    return true;
}

bool skip_hevc_profile_tier_level(bit_reader& bits, uint32_t max_sub_layers_minus1) noexcept {
    if (max_sub_layers_minus1 > 6 || !bits.skip(96)) return false;
    std::array<uint32_t, 7> profile_present{};
    std::array<uint32_t, 7> level_present{};
    for (uint32_t i = 0; i < max_sub_layers_minus1; ++i) {
        if (!bits.read(1, profile_present[i]) || !bits.read(1, level_present[i])) return false;
    }
    if (max_sub_layers_minus1 > 0) {
        for (uint32_t i = max_sub_layers_minus1; i < 8; ++i) {
            uint32_t reserved = 0;
            if (!bits.read(2, reserved) || reserved != 0) return false;
        }
    }
    for (uint32_t i = 0; i < max_sub_layers_minus1; ++i) {
        if (profile_present[i] != 0 && !bits.skip(88)) return false;
        if (level_present[i] != 0 && !bits.skip(8)) return false;
    }
    return true;
}

bool skip_hevc_scaling_list_data(bit_reader& bits) noexcept {
    for (uint32_t size_id = 0; size_id < 4; ++size_id) {
        const uint32_t matrix_step = size_id == 3 ? 3 : 1;
        for (uint32_t matrix_id = 0; matrix_id < 6; matrix_id += matrix_step) {
            uint32_t prediction_mode = 0;
            if (!bits.read(1, prediction_mode)) return false;
            if (prediction_mode == 0) {
                uint32_t delta = 0;
                if (!bits.read_ue(delta) || delta > matrix_id) return false;
                continue;
            }
            const uint32_t coefficient_count = std::min(64U, 1U << (4 + (size_id << 1)));
            if (size_id > 1) {
                int32_t dc_minus8 = 0;
                if (!bits.read_se(dc_minus8)) return false;
            }
            for (uint32_t i = 0; i < coefficient_count; ++i) {
                int32_t delta = 0;
                if (!bits.read_se(delta)) return false;
            }
        }
    }
    return true;
}

bool parse_hevc_vui_fields(
    bit_reader& bits,
    sps_video_facts& facts,
    sps_color_offsets* color_offsets = nullptr) noexcept {
    uint32_t present = 0, value = 0;
    if (!bits.read(1, present)) return false; // aspect_ratio_info_present_flag
    if (present != 0) {
        uint32_t idc = 0;
        if (!bits.read(8, idc)) return false;
        if (idc == 255 && !bits.skip(32)) return false;
    }
    if (!bits.read(1, present)) return false; // overscan_info_present_flag
    if (present != 0 && !bits.read(1, value)) return false;
    if (!bits.read(1, present)) return false; // video_signal_type_present_flag
    if (present == 0) return true;
    uint32_t video_format = 0, full_range = 0, color_present = 0;
    if (!bits.read(3, video_format) || !bits.read(1, full_range) ||
        !bits.read(1, color_present)) return false;
    facts.full_range_present = 1;
    facts.full_range = static_cast<uint8_t>(full_range);
    if (color_present != 0) {
        uint32_t primaries = 0, transfer = 0, matrix = 0;
        if (color_offsets) {
            color_offsets->present = true;
            color_offsets->primaries = bits.position();
        }
        if (!bits.read(8, primaries) || !bits.read(8, transfer) || !bits.read(8, matrix)) return false;
        if (color_offsets) {
            color_offsets->transfer = color_offsets->primaries + 8;
            color_offsets->matrix = color_offsets->primaries + 16;
        }
        facts.color_description_present = 1;
        facts.color_primaries = static_cast<uint16_t>(primaries);
        facts.transfer_characteristics = static_cast<uint16_t>(transfer);
        facts.matrix_coefficients = static_cast<uint16_t>(matrix);
    }
    return true;
}

bool skip_hevc_short_term_ref_pic_set(
    bit_reader& bits,
    uint32_t index,
    const std::array<uint32_t, 64>& prior_delta_poc_counts,
    uint32_t& delta_poc_count) noexcept
{
    uint32_t predicted = 0;
    if (index != 0 && !bits.read(1, predicted)) return false;
    if (predicted != 0) {
        uint32_t value = 0;
        if (!bits.read(1, value) || !bits.read_ue(value)) return false; // delta_rps_sign, abs_delta_rps_minus1
        const uint32_t reference_count = prior_delta_poc_counts[index - 1];
        if (reference_count > 64) return false;
        delta_poc_count = 0;
        for (uint32_t i = 0; i <= reference_count; ++i) {
            uint32_t used_by_current = 0, use_delta = 1;
            if (!bits.read(1, used_by_current)) return false;
            if (used_by_current == 0 && !bits.read(1, use_delta)) return false;
            if (used_by_current != 0 || use_delta != 0) ++delta_poc_count;
        }
        return delta_poc_count <= 64;
    }
    uint32_t negative_count = 0, positive_count = 0;
    if (!bits.read_ue(negative_count) || !bits.read_ue(positive_count) ||
        negative_count > 64 || positive_count > 64 || negative_count + positive_count > 64) return false;
    uint32_t value = 0;
    for (uint32_t i = 0; i < negative_count; ++i) {
        if (!bits.read_ue(value) || !bits.read(1, value)) return false;
    }
    for (uint32_t i = 0; i < positive_count; ++i) {
        if (!bits.read_ue(value) || !bits.read(1, value)) return false;
    }
    delta_poc_count = negative_count + positive_count;
    return true;
}

bool parse_hevc_sps(
    std::span<const uint8_t> nal,
    sps_video_facts& facts,
    sps_color_offsets* color_offsets = nullptr)
{
    if (color_offsets) *color_offsets = {};
    if (nal.size() < 4 || ((nal[0] >> 1) & 0x3fU) != 33 || (nal[0] & 0x80U) != 0) return false;
    std::vector<uint8_t> rbsp;
    if (!remove_emulation_prevention(nal.subspan(2), rbsp)) return false;
    bit_reader bits(rbsp);
    uint32_t value = 0;
    if (!bits.read(4, value)) return false; // sps_video_parameter_set_id
    uint32_t max_sub_layers_minus1 = 0;
    if (!bits.read(3, max_sub_layers_minus1) || max_sub_layers_minus1 > 6 ||
        !bits.read(1, value) || !skip_hevc_profile_tier_level(bits, max_sub_layers_minus1)) return false;
    uint32_t sps_id = 0, chroma = 0;
    if (!bits.read_ue(sps_id) || sps_id > 15 || !bits.read_ue(chroma) || chroma > 3) return false;
    if (chroma == 3 && !bits.read(1, value)) return false;
    uint32_t width = 0, height = 0, conformance = 0;
    if (!bits.read_ue(width) || !bits.read_ue(height) || width == 0 || height == 0 ||
        !bits.read(1, conformance)) return false;
    if (conformance != 0) {
        for (unsigned i = 0; i < 4; ++i) {
            if (!bits.read_ue(value)) return false;
        }
    }
    uint32_t luma_minus8 = 0, chroma_minus8 = 0;
    if (!bits.read_ue(luma_minus8) || !bits.read_ue(chroma_minus8) ||
        luma_minus8 > 8 || chroma_minus8 > 8) return false;
    facts.chroma_format = static_cast<uint8_t>(chroma);
    facts.bit_depth_luma = static_cast<uint8_t>(8 + luma_minus8);
    facts.bit_depth_chroma = static_cast<uint8_t>(8 + chroma_minus8);

    uint32_t log2_max_pic_order_cnt_lsb_minus4 = 0, ordering_present = 0;
    if (!bits.read_ue(log2_max_pic_order_cnt_lsb_minus4) ||
        log2_max_pic_order_cnt_lsb_minus4 > 12 || !bits.read(1, ordering_present)) return false;
    const uint32_t ordering_start = ordering_present != 0 ? 0 : max_sub_layers_minus1;
    for (uint32_t i = ordering_start; i <= max_sub_layers_minus1; ++i) {
        uint32_t max_dec_pic_buffering_minus1 = 0, max_num_reorder_pics = 0, max_latency_plus1 = 0;
        if (!bits.read_ue(max_dec_pic_buffering_minus1) || max_dec_pic_buffering_minus1 > 64 ||
            !bits.read_ue(max_num_reorder_pics) || max_num_reorder_pics > max_dec_pic_buffering_minus1 + 1 ||
            !bits.read_ue(max_latency_plus1)) return false;
    }
    uint32_t ignored = 0;
    for (unsigned i = 0; i < 6; ++i) if (!bits.read_ue(ignored) || ignored > 32) return false;
    uint32_t scaling_list_enabled = 0;
    if (!bits.read(1, scaling_list_enabled)) return false;
    if (scaling_list_enabled != 0) {
        uint32_t scaling_list_present = 0;
        if (!bits.read(1, scaling_list_present)) return false;
        if (scaling_list_present != 0 && !skip_hevc_scaling_list_data(bits)) return false;
    }
    uint32_t amp_enabled = 0, sao_enabled = 0, pcm_enabled = 0;
    if (!bits.read(1, amp_enabled) || !bits.read(1, sao_enabled) || !bits.read(1, pcm_enabled)) return false;
    if (pcm_enabled != 0 && (!bits.skip(32) || !bits.read(1, ignored))) return false;
    uint32_t short_term_set_count = 0;
    if (!bits.read_ue(short_term_set_count) || short_term_set_count > 64) return false;
    std::array<uint32_t, 64> delta_poc_counts{};
    for (uint32_t i = 0; i < short_term_set_count; ++i) {
        if (!skip_hevc_short_term_ref_pic_set(bits, i, delta_poc_counts, delta_poc_counts[i])) return false;
    }
    uint32_t long_term_present = 0;
    if (!bits.read(1, long_term_present)) return false;
    if (long_term_present != 0) {
        uint32_t long_term_count = 0;
        if (!bits.read_ue(long_term_count) || long_term_count > 64) return false;
        const uint32_t poc_bits = log2_max_pic_order_cnt_lsb_minus4 + 4;
        for (uint32_t i = 0; i < long_term_count; ++i) {
            if (!bits.skip(poc_bits) || !bits.read(1, ignored)) return false;
        }
    }
    uint32_t temporal_mvp_enabled = 0, strong_smoothing_enabled = 0, vui_present = 0;
    if (!bits.read(1, temporal_mvp_enabled) || !bits.read(1, strong_smoothing_enabled) ||
        !bits.read(1, vui_present)) return false;
    if (vui_present != 0 && !parse_hevc_vui_fields(bits, facts, color_offsets)) return false;
    return true;
}

bool patch_sps_cicp(
    std::span<const uint8_t> nal,
    bool avc,
    uint8_t primaries,
    uint8_t transfer,
    uint8_t matrix,
    std::vector<uint8_t>& patched)
{
    const size_t header_size = avc ? 1U : 2U;
    if (nal.size() <= header_size) return false;

    std::vector<uint8_t> rbsp;
    if (!remove_emulation_prevention(nal.subspan(header_size), rbsp)) return false;
    sps_video_facts facts{};
    sps_color_offsets offsets{};
    if (avc) {
        uint8_t profile = 0, compatibility = 0, level = 0;
        if (!parse_avc_sps(nal, facts, profile, compatibility, level, &offsets)) return false;
    } else if (!parse_hevc_sps(nal, facts, &offsets)) {
        return false;
    }
    if (!offsets.present ||
        !overwrite_fixed_width_bits(rbsp, offsets.primaries, primaries) ||
        !overwrite_fixed_width_bits(rbsp, offsets.transfer, transfer) ||
        !overwrite_fixed_width_bits(rbsp, offsets.matrix, matrix)) return false;

    std::vector<uint8_t> ebsp;
    if (!add_emulation_prevention(rbsp, ebsp)) return false;
    patched.clear();
    patched.reserve(header_size + ebsp.size());
    patched.insert(patched.end(), nal.begin(), nal.begin() + static_cast<std::ptrdiff_t>(header_size));
    patched.insert(patched.end(), ebsp.begin(), ebsp.end());
    return patched.size() == nal.size();
}

bool patch_avcc_sps_cicp(
    std::span<const uint8_t> config,
    uint8_t primaries,
    uint8_t transfer,
    uint8_t matrix,
    std::vector<uint8_t>& patched)
{
    if (config.size() < 7 || config[0] != 1) return false;
    patched.assign(config.begin(), config.end());
    size_t position = 6;
    const uint8_t sps_count = config[5] & 0x1fU;
    if (sps_count == 0) return false;
    for (uint8_t i = 0; i < sps_count; ++i) {
        if (config.size() - position < 2) return false;
        const uint16_t length = read_be16(config.data() + position);
        position += 2;
        if (length == 0 || length > config.size() - position) return false;
        std::vector<uint8_t> nal;
        if (!patch_sps_cicp(config.subspan(position, length), true,
                primaries, transfer, matrix, nal) || nal.size() != length) return false;
        std::copy(nal.begin(), nal.end(), patched.begin() + static_cast<std::ptrdiff_t>(position));
        position += length;
    }
    return true;
}

bool patch_hvcc_sps_cicp(
    std::span<const uint8_t> config,
    uint8_t primaries,
    uint8_t transfer,
    uint8_t matrix,
    std::vector<uint8_t>& patched)
{
    if (config.size() < 23 || config[0] != 1) return false;
    patched.assign(config.begin(), config.end());
    size_t position = 23;
    const uint8_t array_count = config[22];
    bool found_sps = false;
    for (uint8_t array = 0; array < array_count; ++array) {
        if (config.size() - position < 3) return false;
        const uint8_t nal_type = config[position++] & 0x3fU;
        const uint16_t nal_count = read_be16(config.data() + position);
        position += 2;
        if (nal_count == 0) return false;
        for (uint16_t i = 0; i < nal_count; ++i) {
            if (config.size() - position < 2) return false;
            const uint16_t length = read_be16(config.data() + position);
            position += 2;
            if (length == 0 || length > config.size() - position) return false;
            if (nal_type == 33) {
                std::vector<uint8_t> nal;
                if (!patch_sps_cicp(config.subspan(position, length), false,
                        primaries, transfer, matrix, nal) || nal.size() != length) return false;
                std::copy(nal.begin(), nal.end(), patched.begin() + static_cast<std::ptrdiff_t>(position));
                found_sps = true;
            }
            position += length;
        }
    }
    return position == config.size() && found_sps;
}

bool parse_avcc(
    std::span<const uint8_t> bytes,
    isobmff_video_profile& output,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic)
{
    if (bytes.size() < 7 || bytes[0] != 1 || (bytes[4] & 0xfcU) != 0xfcU ||
        (bytes[5] & 0xe0U) != 0xe0U) {
        failure = video_profile_failure::malformed;
        return false;
    }
    const uint8_t expected_profile = bytes[1];
    const uint8_t expected_compatibility = bytes[2];
    const uint8_t expected_level = bytes[3];
    size_t position = 6;
    const uint8_t sps_count = bytes[5] & 0x1fU;
    if (sps_count == 0) {
        failure = video_profile_failure::malformed;
        return false;
    }
    bool have_profile = false;
    sps_video_facts selected_facts{};
    for (uint8_t i = 0; i < sps_count; ++i) {
        if (bytes.size() - position < 2) { failure = video_profile_failure::malformed; return false; }
        const uint16_t length = read_be16(bytes.data() + position);
        position += 2;
        if (length == 0 || length > bytes.size() - position) { failure = video_profile_failure::malformed; return false; }
        sps_video_facts current_facts{};
        uint8_t profile = 0, compatibility = 0, level = 0;
        if (diagnostic) *diagnostic = video_profile_diagnostic::avc_sps;
        if (!parse_avc_sps(bytes.subspan(position, length), current_facts,
                profile, compatibility, level)) {
            failure = video_profile_failure::malformed;
            return false;
        }
        if (profile != expected_profile || compatibility != expected_compatibility || level != expected_level) {
            failure = video_profile_failure::ambiguous;
            return false;
        }
        if (!have_profile) {
            selected_facts = current_facts;
            have_profile = true;
        } else if (!same_sps_facts(selected_facts, current_facts)) {
            failure = video_profile_failure::ambiguous;
            return false;
        }
        position += length;
    }
    if (position >= bytes.size()) { failure = video_profile_failure::malformed; return false; }
    const uint8_t pps_count = bytes[position++];
    if (pps_count == 0) { failure = video_profile_failure::malformed; return false; }
    for (uint8_t i = 0; i < pps_count; ++i) {
        if (bytes.size() - position < 2) { failure = video_profile_failure::malformed; return false; }
        const uint16_t length = read_be16(bytes.data() + position);
        position += 2;
        if (length == 0 || length > bytes.size() - position || (bytes[position] & 0x1fU) != 8) {
            failure = video_profile_failure::malformed;
            return false;
        }
        position += length;
    }
    // High-profile avcC extension fields mirror chroma/depth facts. Validate
    // them against SPS rather than letting duplicate declarations disagree.
    if (position < bytes.size()) {
        if (bytes.size() - position < 4 ||
            (expected_profile != 100 && expected_profile != 110 && expected_profile != 122 &&
             expected_profile != 144 && expected_profile != 44 && expected_profile != 83 &&
             expected_profile != 86 && expected_profile != 118 && expected_profile != 128 &&
             expected_profile != 134 && expected_profile != 135 && expected_profile != 138 &&
             expected_profile != 139 && expected_profile != 244)) {
            failure = video_profile_failure::unsupported;
            return false;
        }
        const uint8_t chroma_header = bytes[position++];
        const uint8_t luma_header = bytes[position++];
        const uint8_t chroma_depth_header = bytes[position++];
        const uint8_t extension_count = bytes[position++];
        if ((chroma_header & 0xfcU) != 0xfcU || (luma_header & 0xf8U) != 0xf8U ||
            (chroma_depth_header & 0xf8U) != 0xf8U ||
            (chroma_header & 0x03U) != output.chroma_format ||
            (luma_header & 0x07U) + 8 != output.bit_depth_luma ||
            (chroma_depth_header & 0x07U) + 8 != output.bit_depth_chroma) {
            failure = video_profile_failure::ambiguous;
            return false;
        }
        for (uint8_t i = 0; i < extension_count; ++i) {
            if (bytes.size() - position < 2) { failure = video_profile_failure::malformed; return false; }
            const uint16_t length = read_be16(bytes.data() + position);
            position += 2;
            if (length == 0 || length > bytes.size() - position || (bytes[position] & 0x1fU) != 13) {
                failure = video_profile_failure::malformed;
                return false;
            }
            position += length;
        }
    }
    if (position != bytes.size()) {
        failure = video_profile_failure::malformed;
        return false;
    }
    if (have_profile) copy_sps_facts(selected_facts, output);
    return have_profile;
}

bool parse_hvcc(
    std::span<const uint8_t> bytes,
    isobmff_video_profile& output,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic)
{
    if (bytes.size() < 23 || bytes[0] != 1 ||
        (bytes[13] & 0xf0U) != 0xf0U || (bytes[15] & 0xfcU) != 0xfcU ||
        (bytes[16] & 0xfcU) != 0xfcU || (bytes[17] & 0xf8U) != 0xf8U ||
        (bytes[18] & 0xf8U) != 0xf8U) {
        failure = video_profile_failure::malformed;
        return false;
    }
    const uint8_t config_chroma = bytes[16] & 0x03U;
    const uint8_t config_luma_depth = static_cast<uint8_t>(8 + (bytes[17] & 0x07U));
    const uint8_t config_chroma_depth = static_cast<uint8_t>(8 + (bytes[18] & 0x07U));
    size_t position = 23;
    const uint8_t array_count = bytes[22];
    bool found_sps = false;
    sps_video_facts selected_facts{};
    for (uint8_t array = 0; array < array_count; ++array) {
        if (bytes.size() - position < 3) { failure = video_profile_failure::malformed; return false; }
        const uint8_t nal_type = bytes[position++] & 0x3fU;
        const uint16_t nal_count = read_be16(bytes.data() + position);
        position += 2;
        if (nal_count == 0) { failure = video_profile_failure::malformed; return false; }
        for (uint16_t i = 0; i < nal_count; ++i) {
            if (bytes.size() - position < 2) { failure = video_profile_failure::malformed; return false; }
            const uint16_t length = read_be16(bytes.data() + position);
            position += 2;
            if (length == 0 || length > bytes.size() - position) { failure = video_profile_failure::malformed; return false; }
            if (nal_type == 33) {
                sps_video_facts current_facts{};
                if (diagnostic) *diagnostic = video_profile_diagnostic::hevc_sps;
                if (!parse_hevc_sps(bytes.subspan(position, length), current_facts)) {
                    failure = video_profile_failure::malformed;
                    return false;
                }
                if (current_facts.chroma_format != config_chroma ||
                    current_facts.bit_depth_luma != config_luma_depth ||
                    current_facts.bit_depth_chroma != config_chroma_depth) {
                    failure = video_profile_failure::ambiguous;
                    return false;
                }
                if (!found_sps) {
                    selected_facts = current_facts;
                    found_sps = true;
                } else if (!same_sps_facts(selected_facts, current_facts)) {
                    failure = video_profile_failure::ambiguous;
                    return false;
                }
            }
            position += length;
        }
    }
    if (position != bytes.size() || !found_sps) {
        failure = found_sps ? video_profile_failure::malformed : video_profile_failure::unsupported;
        return false;
    }
    copy_sps_facts(selected_facts, output);
    return true;
}

bool is_known_primaries(uint16_t value) noexcept {
    switch (value) {
    case 1: case 4: case 5: case 6: case 7: case 8: case 9: case 10: case 11: case 12: case 22:
        return true;
    default: return false;
    }
}

bool is_known_transfer(uint16_t value) noexcept {
    switch (value) {
    case 1: case 4: case 5: case 6: case 7: case 8: case 9: case 10: case 11:
    case 12: case 13: case 14: case 15: case 16: case 17: case 18:
        return true;
    default: return false;
    }
}

bool is_known_matrix(uint16_t value) noexcept {
    switch (value) {
    case 0: case 1: case 4: case 5: case 6: case 7: case 8: case 9: case 10:
    case 11: case 12: case 13: case 14:
        return true;
    default: return false;
    }
}

bool is_sdr_transfer(uint16_t value) noexcept {
    switch (value) {
    case 1: case 4: case 5: case 6: case 7: case 8: case 9: case 10:
    case 11: case 12: case 13:
        return true;
    default: return false;
    }
}

bool parse_video_track(
    std::span<const uint8_t> moov,
    const box_ref& trak,
    isobmff_video_profile& output,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic,
    video_profile_box_layout* layout)
{
    if (diagnostic) *diagnostic = video_profile_diagnostic::track_layout;
    std::vector<box_ref> trak_children;
    if (!read_children(moov, trak.payload_start(), trak.end(), trak_children)) {
        failure = video_profile_failure::malformed;
        return false;
    }
    box_ref mdia{};
    if (!unique_child(trak_children, "mdia", mdia, true, failure)) return false;
    std::vector<box_ref> mdia_children;
    if (!read_children(moov, mdia.payload_start(), mdia.end(), mdia_children)) {
        failure = video_profile_failure::malformed;
        return false;
    }
    box_ref minf{};
    if (!unique_child(mdia_children, "minf", minf, true, failure)) return false;
    std::vector<box_ref> minf_children;
    if (!read_children(moov, minf.payload_start(), minf.end(), minf_children)) {
        failure = video_profile_failure::malformed;
        return false;
    }
    box_ref stbl{};
    if (!unique_child(minf_children, "stbl", stbl, true, failure)) return false;
    std::vector<box_ref> stbl_children;
    if (!read_children(moov, stbl.payload_start(), stbl.end(), stbl_children)) {
        failure = video_profile_failure::malformed;
        return false;
    }
    box_ref stsd{}, stsc{};
    if (!unique_child(stbl_children, "stsd", stsd, true, failure) ||
        !unique_child(stbl_children, "stsc", stsc, true, failure)) return false;

    if (diagnostic) *diagnostic = video_profile_diagnostic::sample_description;
    const size_t stsd_body = stsd.payload_start();
    if (stsd.size < stsd.header_size + 8 || moov[stsd_body] != 0 ||
        moov[stsd_body + 1] != 0 || moov[stsd_body + 2] != 0 || moov[stsd_body + 3] != 0 ||
        read_be32(moov.data() + stsd_body + 4) != 1) {
        failure = video_profile_failure::unsupported;
        return false;
    }
    std::vector<box_ref> sample_entries;
    if (!read_children(moov, stsd_body + 8, stsd.end(), sample_entries) || sample_entries.size() != 1) {
        failure = video_profile_failure::ambiguous;
        return false;
    }

    if (diagnostic) *diagnostic = video_profile_diagnostic::sample_to_chunk;
    const size_t stsc_body = stsc.payload_start();
    if (stsc.size < stsc.header_size + 8 || moov[stsc_body] != 0 ||
        moov[stsc_body + 1] != 0 || moov[stsc_body + 2] != 0 || moov[stsc_body + 3] != 0) {
        failure = video_profile_failure::malformed;
        return false;
    }
    const uint32_t stsc_count = read_be32(moov.data() + stsc_body + 4);
    if (stsc_count == 0 || stsc_count > (stsc.size - stsc.header_size - 8) / 12 ||
        stsc.size != stsc.header_size + 8 + static_cast<size_t>(stsc_count) * 12) {
        failure = video_profile_failure::malformed;
        return false;
    }
    uint32_t previous_first_chunk = 0;
    for (uint32_t i = 0; i < stsc_count; ++i) {
        const size_t row = stsc_body + 8 + static_cast<size_t>(i) * 12;
        const uint32_t first_chunk = read_be32(moov.data() + row);
        const uint32_t samples_per_chunk = read_be32(moov.data() + row + 4);
        const uint32_t description_index = read_be32(moov.data() + row + 8);
        if ((i == 0 && first_chunk != 1) || first_chunk <= previous_first_chunk ||
            samples_per_chunk == 0 || description_index != 1) {
            failure = description_index != 1 ? video_profile_failure::ambiguous : video_profile_failure::malformed;
            return false;
        }
        previous_first_chunk = first_chunk;
    }

    if (diagnostic) *diagnostic = video_profile_diagnostic::sample_entry;
    const auto& entry = sample_entries.front();
    if (entry.size < entry.header_size + 78) {
        failure = video_profile_failure::malformed;
        return false;
    }
    const bool avc = entry.is("avc1") || entry.is("avc3");
    const bool hevc = entry.is("hvc1") || entry.is("hev1");
    if (!avc && !hevc) {
        failure = video_profile_failure::unsupported;
        return false;
    }
    output.codec_fourcc = read_be32(reinterpret_cast<const uint8_t*>(entry.type.data()));
    std::vector<box_ref> sample_children;
    if (!read_children(moov, entry.payload_start() + 78, entry.end(), sample_children, true)) {
        failure = video_profile_failure::malformed;
        return false;
    }
    box_ref codec_config{}, color{};
    if (!unique_child(sample_children, avc ? "avcC" : "hvcC", codec_config, true, failure) ||
        !unique_child(sample_children, "colr", color, true, failure)) return false;
    box_ref wrong_config{};
    if (!unique_child(sample_children, avc ? "hvcC" : "avcC", wrong_config, false, failure)) return false;
    if (wrong_config.size != 0) { failure = video_profile_failure::ambiguous; return false; }
    for (const auto& child : sample_children) {
        if (child.is("mdcv") || child.is("clli") || child.is("dvcC") || child.is("dvvC")) {
            failure = video_profile_failure::unsupported;
            return false;
        }
    }
    const auto config_bytes = moov.subspan(codec_config.payload_start(), codec_config.end() - codec_config.payload_start());
    if (layout) {
        layout->codec_config_box_offset = codec_config.start;
        layout->codec_config_offset = codec_config.payload_start();
        layout->codec_config_size = config_bytes.size();
        layout->color_box_offset = color.start;
    }
    if (diagnostic) *diagnostic = video_profile_diagnostic::codec_configuration;
    if (!(avc ? parse_avcc(config_bytes, output, failure, diagnostic) : parse_hvcc(config_bytes, output, failure, diagnostic))) return false;

    if (diagnostic) *diagnostic = video_profile_diagnostic::color_description;
    const size_t color_payload = color.payload_start();
    const bool nclx = color.end() - color_payload == 11 &&
        std::memcmp(moov.data() + color_payload, "nclx", 4) == 0;
    const bool nclc = color.end() - color_payload == 10 &&
        std::memcmp(moov.data() + color_payload, "nclc", 4) == 0;
    if (layout) {
        layout->color_payload_offset = color_payload;
        layout->color_is_nclx = nclx;
    }
    if (!nclx && !nclc) {
        failure = video_profile_failure::unsupported;
        return false;
    }
    output.color_primaries = read_be16(moov.data() + color_payload + 4);
    output.transfer_characteristics = read_be16(moov.data() + color_payload + 6);
    output.matrix_coefficients = read_be16(moov.data() + color_payload + 8);
    if (output.bitstream_color_description_present != 0) {
        const auto reconcile_color = [&](uint16_t& container_value, uint16_t bitstream_value) noexcept {
            if (container_value == 2) {
                if (bitstream_value == 2) return false;
                container_value = bitstream_value;
                return true;
            }
            return bitstream_value == 2 || container_value == bitstream_value;
        };
        if (!reconcile_color(output.color_primaries, output.bitstream_color_primaries) ||
            !reconcile_color(output.transfer_characteristics, output.bitstream_transfer_characteristics) ||
            !reconcile_color(output.matrix_coefficients, output.bitstream_matrix_coefficients)) {
            failure = video_profile_failure::ambiguous;
            return false;
        }
    }
    if (!is_known_primaries(output.color_primaries) ||
        !is_known_transfer(output.transfer_characteristics) ||
        !is_known_matrix(output.matrix_coefficients)) {
        failure = video_profile_failure::unsupported;
        return false;
    }
    if (nclx) {
        const uint8_t range = moov[color_payload + 10];
        if ((range & 0x7fU) != 0) { failure = video_profile_failure::malformed; return false; }
        output.full_range = static_cast<uint8_t>((range >> 7) & 1U);
        output.full_range_known = 1;
    } else if (output.bitstream_full_range_present != 0) {
        output.full_range = output.bitstream_full_range;
        output.full_range_known = 1;
    } else {
        failure = video_profile_failure::unsupported;
        return false;
    }
    if (output.bitstream_full_range_present != 0 &&
        output.bitstream_full_range != output.full_range) {
        failure = video_profile_failure::ambiguous;
        return false;
    }
    const bool hdr_transfer = output.transfer_characteristics == 14 ||
        output.transfer_characteristics == 15 || output.transfer_characteristics == 16 ||
        output.transfer_characteristics == 17 || output.transfer_characteristics == 18;
    if (diagnostic) *diagnostic = video_profile_diagnostic::classification;
    output.classification = output.bit_depth_luma > 8 || output.bit_depth_chroma > 8 || hdr_transfer
        ? video_profile_class::preservation_critical
        : (output.bit_depth_luma == 8 && output.bit_depth_chroma == 8 &&
            is_sdr_transfer(output.transfer_characteristics)
            ? video_profile_class::ordinary_sdr
            : video_profile_class::unknown);
    if (output.classification == video_profile_class::unknown) {
        failure = video_profile_failure::unsupported;
        return false;
    }
    return true;
}

} // namespace

static bool inspect_isobmff_video_profile_impl(
    const lpb::random_access_reader& source,
    isobmff_video_profile& output,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic,
    video_profile_box_layout* layout) noexcept
{
    output = {};
    failure = video_profile_failure::none;
    if (layout) *layout = {};
    if (diagnostic) *diagnostic = video_profile_diagnostic::source_layout;
    try {
        if (source.read_at == nullptr || source.length < 16 ||
            source.length > static_cast<uint64_t>(std::numeric_limits<size_t>::max())) {
            failure = video_profile_failure::malformed;
            return false;
        }
        auto top = ::scan_top_level_boxes(source);
        if (top.empty()) { failure = video_profile_failure::malformed; return false; }
        uint64_t next = 0;
        size_t moov_count = 0;
        size_t mdat_count = 0;
        const top_level_box* moov_box = nullptr;
        for (const auto& box : top) {
            if (box.offset != next || box.size == 0 || box.size > source.length - next) {
                failure = video_profile_failure::malformed;
                return false;
            }
            next += box.size;
            if (std::memcmp(box.type, "moov", 4) == 0) { ++moov_count; moov_box = &box; }
            if (std::memcmp(box.type, "mdat", 4) == 0) ++mdat_count;
        }
        if (next != source.length || mdat_count == 0 || moov_count != 1 || moov_box == nullptr) {
            failure = moov_count > 1 ? video_profile_failure::ambiguous : video_profile_failure::malformed;
            return false;
        }
        constexpr uint64_t max_moov_size = 64ULL * 1024ULL * 1024ULL;
        if (moov_box->size < 8 || moov_box->size > max_moov_size ||
            moov_box->size > static_cast<uint64_t>(std::numeric_limits<size_t>::max())) {
            failure = video_profile_failure::unsupported;
            return false;
        }
        std::vector<uint8_t> moov(static_cast<size_t>(moov_box->size));
        if (!source.read_exact(moov_box->offset, moov)) { failure = video_profile_failure::io; return false; }
        if (layout) {
            layout->moov_offset = moov_box->offset;
            layout->moov_size = moov.size();
        }
        isobmff_box_header moov_header{};
        if (!try_read_box_header(moov.data(), 0, moov.size(), moov_header) ||
            moov_header.size != moov.size() || std::memcmp(moov.data() + 4, "moov", 4) != 0) {
            failure = video_profile_failure::malformed;
            return false;
        }
        if (diagnostic) *diagnostic = video_profile_diagnostic::moov_layout;
        std::vector<box_ref> children;
        if (!read_children(moov, moov_header.header_size, moov.size(), children)) { failure = video_profile_failure::malformed; return false; }
        size_t video_tracks = 0;
        for (const auto& trak : children) {
            if (!trak.is("trak")) continue;
            if (diagnostic) *diagnostic = video_profile_diagnostic::track_layout;
            std::vector<box_ref> trak_children;
            if (!read_children(moov, trak.payload_start(), trak.end(), trak_children)) {
                failure = video_profile_failure::malformed;
                return false;
            }
            box_ref mdia{};
            if (!unique_child(trak_children, "mdia", mdia, true, failure)) return false;
            std::vector<box_ref> mdia_children;
            if (!read_children(moov, mdia.payload_start(), mdia.end(), mdia_children)) {
                failure = video_profile_failure::malformed;
                return false;
            }
            box_ref hdlr{};
            if (!unique_child(mdia_children, "hdlr", hdlr, true, failure)) return false;
            if (hdlr.end() - hdlr.payload_start() < 12) { failure = video_profile_failure::malformed; return false; }
            if (std::memcmp(moov.data() + hdlr.payload_start() + 8, "vide", 4) == 0) {
                if (++video_tracks > 1) { failure = video_profile_failure::ambiguous; return false; }
                if (!parse_video_track(moov, trak, output, failure, diagnostic, layout)) return false;
            }
        }
        if (video_tracks != 1) { failure = video_profile_failure::unsupported; return false; }
        return true;
    } catch (...) {
        output = {};
        failure = video_profile_failure::io;
        return false;
    }
}

bool inspect_isobmff_video_profile(
    const lpb::random_access_reader& source,
    isobmff_video_profile& output,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic) noexcept
{
    return inspect_isobmff_video_profile_impl(source, output, failure, diagnostic, nullptr);
}

namespace {

bool is_mf_transfer_alias(uint16_t left, uint16_t right) noexcept {
    return (left == 1 && right == 6) || (left == 6 && right == 1);
}

bool is_mf_matrix_alias(uint16_t left, uint16_t right) noexcept {
    return (left == 5 && right == 6) || (left == 6 && right == 5);
}

bool bitstream_cicp_matches_or_is_unspecified(uint16_t bitstream_value, uint16_t container_value) noexcept {
    return bitstream_value == 2 || bitstream_value == container_value;
}

bool exact_sdr_profile_cicp(
    const isobmff_video_profile& actual,
    const isobmff_video_profile& expected) noexcept
{
    return actual.classification == video_profile_class::ordinary_sdr &&
        actual.bit_depth_luma == 8 && actual.bit_depth_chroma == 8 &&
        actual.color_primaries == expected.color_primaries &&
        actual.transfer_characteristics == expected.transfer_characteristics &&
        actual.matrix_coefficients == expected.matrix_coefficients &&
        actual.full_range_known == expected.full_range_known &&
        actual.full_range == expected.full_range;
}

struct overlay_reader_state {
    const lpb::random_access_reader* source{};
    uint64_t patch_offset{};
    const std::vector<uint8_t>* patch_bytes{};
};

bool read_overlay(void* user, uint64_t offset, std::span<uint8_t> output) noexcept {
    auto& state = *static_cast<overlay_reader_state*>(user);
    if (state.source == nullptr || state.patch_bytes == nullptr ||
        !state.source->read_exact(offset, output)) return false;
    if (output.empty() || state.patch_bytes->empty()) return true;
    if (offset > (std::numeric_limits<uint64_t>::max)() - output.size() ||
        state.patch_offset > (std::numeric_limits<uint64_t>::max)() - state.patch_bytes->size()) return false;

    const uint64_t read_end = offset + output.size();
    const uint64_t patch_end = state.patch_offset + state.patch_bytes->size();
    const uint64_t overlap_start = (std::max)(offset, state.patch_offset);
    const uint64_t overlap_end = (std::min)(read_end, patch_end);
    if (overlap_start >= overlap_end) return true;
    const size_t source_offset = static_cast<size_t>(overlap_start - state.patch_offset);
    const size_t destination_offset = static_cast<size_t>(overlap_start - offset);
    const size_t overlap_size = static_cast<size_t>(overlap_end - overlap_start);
    std::copy_n(state.patch_bytes->data() + source_offset, overlap_size,
        output.data() + destination_offset);
    return true;
}

} // namespace

media_foundation_cicp_patch_mode select_media_foundation_cicp_patch_mode(
    const isobmff_video_profile& actual,
    const isobmff_video_profile& source_profile) noexcept
{
    const bool avc = actual.codec_fourcc == 0x61766331U || actual.codec_fourcc == 0x61766333U;
    const bool hevc = actual.codec_fourcc == 0x68766331U || actual.codec_fourcc == 0x68657631U;
    if ((!avc && !hevc) || source_profile.classification != video_profile_class::ordinary_sdr ||
        source_profile.bit_depth_luma != 8 || source_profile.bit_depth_chroma != 8 ||
        source_profile.full_range_known == 0 || source_profile.color_primaries > 255 ||
        source_profile.transfer_characteristics > 255 || source_profile.matrix_coefficients > 255 ||
        actual.classification != video_profile_class::ordinary_sdr ||
        actual.bit_depth_luma != 8 || actual.bit_depth_chroma != 8 ||
        actual.color_primaries != source_profile.color_primaries ||
        actual.full_range_known == 0 || actual.full_range_known != source_profile.full_range_known ||
        actual.full_range != source_profile.full_range ||
        (!is_mf_transfer_alias(actual.transfer_characteristics, source_profile.transfer_characteristics) &&
            actual.transfer_characteristics != source_profile.transfer_characteristics) ||
        (!is_mf_matrix_alias(actual.matrix_coefficients, source_profile.matrix_coefficients) &&
            actual.matrix_coefficients != source_profile.matrix_coefficients) ||
        actual.bitstream_color_description_present > 1) {
        return media_foundation_cicp_patch_mode::reject;
    }

    if (actual.bitstream_color_description_present == 0) {
        if (actual.bitstream_color_primaries != 0 ||
            actual.bitstream_transfer_characteristics != 0 ||
            actual.bitstream_matrix_coefficients != 0) {
            return media_foundation_cicp_patch_mode::reject;
        }
        return exact_sdr_profile_cicp(actual, source_profile)
            ? media_foundation_cicp_patch_mode::unchanged
            : media_foundation_cicp_patch_mode::colr_only;
    }

    if (!bitstream_cicp_matches_or_is_unspecified(
            actual.bitstream_color_primaries, actual.color_primaries) ||
        !bitstream_cicp_matches_or_is_unspecified(
            actual.bitstream_transfer_characteristics, actual.transfer_characteristics) ||
        !bitstream_cicp_matches_or_is_unspecified(
            actual.bitstream_matrix_coefficients, actual.matrix_coefficients)) {
        return media_foundation_cicp_patch_mode::reject;
    }
    return exact_sdr_profile_cicp(actual, source_profile)
        ? media_foundation_cicp_patch_mode::unchanged
        : media_foundation_cicp_patch_mode::colr_and_bitstream;
}

bool prepare_media_foundation_cicp_patch(
    const lpb::random_access_reader& staged_output,
    const isobmff_video_profile& source_profile,
    media_foundation_cicp_patch& patch,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic) noexcept
{
    patch = {};
    failure = video_profile_failure::none;
    if (diagnostic) *diagnostic = video_profile_diagnostic::source_layout;
    try {
        if (source_profile.classification != video_profile_class::ordinary_sdr ||
            source_profile.bit_depth_luma != 8 || source_profile.bit_depth_chroma != 8 ||
            source_profile.full_range_known == 0 || source_profile.color_primaries > 255 ||
            source_profile.transfer_characteristics > 255 || source_profile.matrix_coefficients > 255) {
            failure = video_profile_failure::unsupported;
            return false;
        }

        isobmff_video_profile actual{};
        video_profile_box_layout layout{};
        if (!inspect_isobmff_video_profile_impl(
                staged_output, actual, failure, diagnostic, &layout)) return false;
        if (diagnostic) *diagnostic = video_profile_diagnostic::classification;
        const auto mode = select_media_foundation_cicp_patch_mode(actual, source_profile);
        if (mode == media_foundation_cicp_patch_mode::reject) {
            failure = video_profile_failure::ambiguous;
            return false;
        }
        if (mode == media_foundation_cicp_patch_mode::unchanged) return true;
        if (diagnostic) *diagnostic = video_profile_diagnostic::cicp_normalization;
        if (layout.moov_size == 0 || layout.codec_config_size == 0 ||
            layout.codec_config_offset > layout.moov_size ||
            layout.codec_config_size > layout.moov_size - layout.codec_config_offset ||
            layout.color_payload_offset > layout.moov_size ||
            layout.moov_size - layout.color_payload_offset < (layout.color_is_nclx ? 11U : 10U)) {
            failure = video_profile_failure::ambiguous;
            return false;
        }

        patch.moov_bytes.resize(layout.moov_size);
        if (!staged_output.read_exact(layout.moov_offset, patch.moov_bytes)) {
            patch = {};
            failure = video_profile_failure::io;
            return false;
        }

        const size_t codec_config_end = layout.codec_config_offset + layout.codec_config_size;
        std::vector<uint8_t> original_codec_config;
        if (mode == media_foundation_cicp_patch_mode::colr_only) {
            original_codec_config.assign(
                patch.moov_bytes.begin() + static_cast<std::ptrdiff_t>(layout.codec_config_offset),
                patch.moov_bytes.begin() + static_cast<std::ptrdiff_t>(codec_config_end));
        }

        const std::span<const uint8_t> config(
            patch.moov_bytes.data() + layout.codec_config_offset, layout.codec_config_size);
        if (mode == media_foundation_cicp_patch_mode::colr_and_bitstream) {
            std::vector<uint8_t> patched_config;
            const bool avc = actual.codec_fourcc == 0x61766331U || actual.codec_fourcc == 0x61766333U;
            const bool config_patched = avc
                ? patch_avcc_sps_cicp(config,
                    static_cast<uint8_t>(source_profile.color_primaries),
                    static_cast<uint8_t>(source_profile.transfer_characteristics),
                    static_cast<uint8_t>(source_profile.matrix_coefficients), patched_config)
                : patch_hvcc_sps_cicp(config,
                    static_cast<uint8_t>(source_profile.color_primaries),
                    static_cast<uint8_t>(source_profile.transfer_characteristics),
                    static_cast<uint8_t>(source_profile.matrix_coefficients), patched_config);
            if (!config_patched || patched_config.size() != layout.codec_config_size) {
                patch = {};
                failure = video_profile_failure::unsupported;
                if (diagnostic) *diagnostic = avc
                    ? video_profile_diagnostic::avc_sps : video_profile_diagnostic::hevc_sps;
                return false;
            }
            std::copy(patched_config.begin(), patched_config.end(),
                patch.moov_bytes.begin() + static_cast<std::ptrdiff_t>(layout.codec_config_offset));
        } else if (mode != media_foundation_cicp_patch_mode::colr_only || config.size() != layout.codec_config_size) {
            patch = {};
            failure = video_profile_failure::ambiguous;
            return false;
        }

        const size_t color_payload = layout.color_payload_offset;
        if ((layout.color_is_nclx && std::memcmp(patch.moov_bytes.data() + color_payload, "nclx", 4) != 0) ||
            (!layout.color_is_nclx && std::memcmp(patch.moov_bytes.data() + color_payload, "nclc", 4) != 0)) {
            patch = {};
            failure = video_profile_failure::malformed;
            return false;
        }
        write_be16(patch.moov_bytes.data() + color_payload + 4, source_profile.color_primaries);
        write_be16(patch.moov_bytes.data() + color_payload + 6, source_profile.transfer_characteristics);
        write_be16(patch.moov_bytes.data() + color_payload + 8, source_profile.matrix_coefficients);
        if (layout.color_is_nclx &&
            ((patch.moov_bytes[color_payload + 10] >> 7) & 1U) != source_profile.full_range) {
            patch = {};
            failure = video_profile_failure::ambiguous;
            return false;
        }
        if (mode == media_foundation_cicp_patch_mode::colr_only &&
            !std::equal(original_codec_config.begin(), original_codec_config.end(),
                patch.moov_bytes.begin() + static_cast<std::ptrdiff_t>(layout.codec_config_offset))) {
            patch = {};
            failure = video_profile_failure::ambiguous;
            return false;
        }

        overlay_reader_state overlay_state{&staged_output, layout.moov_offset, &patch.moov_bytes};
        const lpb::random_access_reader overlay{
            staged_output.length, &overlay_state, read_overlay};
        isobmff_video_profile normalized{};
        video_profile_box_layout normalized_layout{};
        video_profile_failure normalized_failure{};
        if (!inspect_isobmff_video_profile_impl(
                overlay, normalized, normalized_failure, diagnostic, &normalized_layout) ||
            !exact_sdr_profile_cicp(normalized, source_profile) ||
            (mode == media_foundation_cicp_patch_mode::colr_only &&
                (normalized.bitstream_color_description_present != 0 ||
                    normalized.bitstream_color_primaries != 0 ||
                    normalized.bitstream_transfer_characteristics != 0 ||
                    normalized.bitstream_matrix_coefficients != 0)) ||
            (mode == media_foundation_cicp_patch_mode::colr_and_bitstream &&
                (normalized.bitstream_color_description_present == 0 ||
                    normalized.bitstream_color_primaries != source_profile.color_primaries ||
                    normalized.bitstream_transfer_characteristics != source_profile.transfer_characteristics ||
                    normalized.bitstream_matrix_coefficients != source_profile.matrix_coefficients)) ||
            normalized_layout.moov_offset != layout.moov_offset ||
            normalized_layout.moov_size != layout.moov_size) {
            patch = {};
            failure = normalized_failure == video_profile_failure::none
                ? video_profile_failure::ambiguous : normalized_failure;
            return false;
        }

        patch.moov_offset = layout.moov_offset;
        patch.codec_config_box_offset = layout.codec_config_box_offset;
        patch.codec_config_offset = layout.codec_config_offset;
        patch.color_payload_offset = layout.color_payload_offset;
        patch.changed = true;
        return true;
    } catch (...) {
        patch = {};
        failure = video_profile_failure::io;
        return false;
    }
}

video_conversion_owner select_video_conversion_owner(
    const isobmff_video_profile& source,
    requested_video_codec target) noexcept
{
    if (source.classification == video_profile_class::unknown || source.bit_depth_luma == 0 ||
        source.bit_depth_chroma == 0) return video_conversion_owner::reject;
    if (target == requested_video_codec::copy) return video_conversion_owner::project_remux;
    if (target != requested_video_codec::h264 && target != requested_video_codec::hevc) {
        return video_conversion_owner::reject;
    }
    if (source.classification == video_profile_class::preservation_critical) {
        return video_conversion_owner::minimal_libav_sidecar;
    }
    return video_conversion_owner::media_foundation_software;
}

} // namespace lpb::media
