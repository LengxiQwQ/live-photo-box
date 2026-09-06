#include "protocols/samsung_sef.h"
#include "foundation/residue_fingerprint.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"

using namespace lpb;

extern "C" LPB_API lpb_result LPB_CALL lpb_samsung_sef_parse(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_video_offset,
    uint64_t* out_video_size)
{
    if (!context || !input || !out_video_offset || !out_video_size) return LPB_RESULT_INVALID_ARGUMENT;

    if (input_size < 16) {
        set_error(context, "Input too small for SEF.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const size_t footer_pos = input_size - 8;
    if (std::memcmp(input + input_size - 4, "SEFT", 4) != 0) {
        set_error(context, "SEFT marker not found at end of file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const uint32_t total_size = static_cast<uint32_t>(input[footer_pos]) |
        (static_cast<uint32_t>(input[footer_pos + 1]) << 8) |
        (static_cast<uint32_t>(input[footer_pos + 2]) << 16) |
        (static_cast<uint32_t>(input[footer_pos + 3]) << 24);

    // Samsung's total_size starts at SEFH and ends immediately before the
    // total_size/SEFT footer. Tag payloads are stored immediately before SEFH
    // and are addressed backwards from it.
    if (total_size < 12 || static_cast<uint64_t>(total_size) > input_size - 8) {
        set_error(context, "Invalid SEF total size.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const size_t sef_start = footer_pos - static_cast<size_t>(total_size);
    if (sef_start > footer_pos || std::memcmp(input + sef_start, "SEFH", 4) != 0) {
        set_error(context, "SEFH marker not found at expected offset.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const auto read_le16 = [&](size_t at) noexcept -> uint16_t {
        return static_cast<uint16_t>(input[at]) | (static_cast<uint16_t>(input[at + 1]) << 8);
    };
    const auto read_le32 = [&](size_t at) noexcept -> uint32_t {
        return static_cast<uint32_t>(input[at]) |
            (static_cast<uint32_t>(input[at + 1]) << 8) |
            (static_cast<uint32_t>(input[at + 2]) << 16) |
            (static_cast<uint32_t>(input[at + 3]) << 24);
    };
    const uint32_t version = read_le32(sef_start + 4);
    const uint32_t count = read_le32(sef_start + 8);
    (void)version;
    if (count > (static_cast<size_t>(total_size) - 12) / 12 ||
        sef_start + 12 + static_cast<size_t>(count) * 12 != footer_pos) {
        set_error(context, "SEF directory exceeds the SEFH/SEFT table boundary.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    bool found_motion = false;
    uint64_t motion_offset = 0;
    uint64_t motion_size = 0;
    std::vector<std::pair<size_t, size_t>> payload_ranges;
    for (uint32_t i = 0; i < count; i++) {
        const size_t entry = sef_start + 12 + static_cast<size_t>(i) * 12;
        const uint16_t prefix = read_le16(entry);
        const uint16_t marker = read_le16(entry + 2);
        const uint32_t offset = read_le32(entry + 4);
        const uint32_t size = read_le32(entry + 8);
        if (static_cast<uint64_t>(offset) > sef_start || static_cast<uint64_t>(size) > offset || size < 8) {
            set_error(context, "SEF entry points outside the payload region.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const size_t payload_start = sef_start - static_cast<size_t>(offset);
        if (static_cast<size_t>(size) > sef_start - payload_start) {
            set_error(context, "SEF entry payload is outside the file-owned trailer.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const size_t payload_end = payload_start + static_cast<size_t>(size);
        for (const auto& range : payload_ranges) {
            if (payload_start < range.second && range.first < payload_end) {
                set_error(context, "SEF entries have overlapping payload ownership.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        payload_ranges.emplace_back(payload_start, payload_end);
        if (payload_start > input_size - 8 || read_le16(payload_start) != prefix || read_le16(payload_start + 2) != marker) {
            set_error(context, "SEF entry header does not match its referenced payload.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint32_t name_size = read_le32(payload_start + 4);
        if (name_size > size - 8 || static_cast<size_t>(name_size) > input_size - (payload_start + 8)) {
            set_error(context, "SEF entry name exceeds its payload.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (marker == 0x0A30) {
            if (found_motion || prefix != 0 || name_size != 16 || size < 24 ||
                std::memcmp(input + payload_start + 8, "MotionPhoto_Data", 16) != 0) {
                set_error(context, "Invalid or duplicate MotionPhoto_Data entry.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            const size_t video_start = payload_start + 24;
            const size_t video_length = static_cast<size_t>(size) - 24;
            if (!is_valid_isobmff_media_range(input, input_size, video_start, video_length)) {
                set_error(context, "MotionPhoto_Data does not contain a structurally valid ISO-BMFF video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            found_motion = true;
            motion_offset = video_start;
            motion_size = video_length;
        }
    }

    if (found_motion) {
        *out_video_offset = motion_offset;
        *out_video_size = motion_size;
        return LPB_RESULT_OK;
    }
    set_error(context, "MotionPhoto_Data entry not found in SEF.");
    return LPB_RESULT_INVALID_ARGUMENT;
}

extern "C" LPB_API int32_t LPB_CALL lpb_samsung_sef_has_tag(
    const uint8_t* input,
    size_t input_size,
    uint16_t target_marker)
{
    if (!input || input_size < 16) return 0;
    const size_t footer_pos = input_size - 8;
    if (std::memcmp(input + input_size - 4, "SEFT", 4) != 0) return 0;

    const uint32_t total_size = static_cast<uint32_t>(input[footer_pos]) |
        (static_cast<uint32_t>(input[footer_pos + 1]) << 8) |
        (static_cast<uint32_t>(input[footer_pos + 2]) << 16) |
        (static_cast<uint32_t>(input[footer_pos + 3]) << 24);

    if (total_size < 12 || static_cast<uint64_t>(total_size) > input_size - 8) return 0;

    const size_t sef_start = footer_pos - static_cast<size_t>(total_size);
    if (sef_start > footer_pos || std::memcmp(input + sef_start, "SEFH", 4) != 0) return 0;

    const auto read_le16 = [&](size_t at) noexcept -> uint16_t {
        return static_cast<uint16_t>(input[at]) | (static_cast<uint16_t>(input[at + 1]) << 8);
    };
    const auto read_le32 = [&](size_t at) noexcept -> uint32_t {
        return static_cast<uint32_t>(input[at]) |
            (static_cast<uint32_t>(input[at + 1]) << 8) |
            (static_cast<uint32_t>(input[at + 2]) << 16) |
            (static_cast<uint32_t>(input[at + 3]) << 24);
    };

    const uint32_t count = read_le32(sef_start + 8);
    if (count > (static_cast<size_t>(total_size) - 12) / 12 ||
        sef_start + 12 + static_cast<size_t>(count) * 12 != footer_pos) return 0;

    for (uint32_t i = 0; i < count; i++) {
        const size_t entry = sef_start + 12 + static_cast<size_t>(i) * 12;
        const uint16_t marker = read_le16(entry + 2);
        if (marker == target_marker) return 1;
    }
    return 0;
}

namespace lpb::protocols {

bool samsung_sef_get_entry_fingerprint(
    const uint8_t* input,
    size_t input_size,
    uint16_t target_marker,
    std::string& out_fp)
{
    if (!input || input_size < 16) return false;
    const size_t footer_pos = input_size - 8;
    if (std::memcmp(input + input_size - 4, "SEFT", 4) != 0) return false;

    const uint32_t total_size = static_cast<uint32_t>(input[footer_pos]) |
        (static_cast<uint32_t>(input[footer_pos + 1]) << 8) |
        (static_cast<uint32_t>(input[footer_pos + 2]) << 16) |
        (static_cast<uint32_t>(input[footer_pos + 3]) << 24);

    if (total_size < 12 || static_cast<uint64_t>(total_size) > input_size - 8) return false;

    const size_t sef_start = footer_pos - static_cast<size_t>(total_size);
    if (sef_start > footer_pos || std::memcmp(input + sef_start, "SEFH", 4) != 0) return false;

    const auto read_le16 = [&](size_t at) noexcept -> uint16_t {
        return static_cast<uint16_t>(input[at]) | (static_cast<uint16_t>(input[at + 1]) << 8);
    };
    const auto read_le32 = [&](size_t at) noexcept -> uint32_t {
        return static_cast<uint32_t>(input[at]) |
            (static_cast<uint32_t>(input[at + 1]) << 8) |
            (static_cast<uint32_t>(input[at + 2]) << 16) |
            (static_cast<uint32_t>(input[at + 3]) << 24);
    };

    const uint32_t count = read_le32(sef_start + 8);
    if (count > (static_cast<size_t>(total_size) - 12) / 12 ||
        sef_start + 12 + static_cast<size_t>(count) * 12 != footer_pos) return false;

    for (uint32_t i = 0; i < count; i++) {
        const size_t entry = sef_start + 12 + static_cast<size_t>(i) * 12;
        const uint16_t marker = read_le16(entry + 2);
        if (marker == target_marker) {
            const uint32_t offset = read_le32(entry + 4);
            const uint32_t size = read_le32(entry + 8);
            if (static_cast<uint64_t>(offset) > sef_start || static_cast<uint64_t>(size) > offset || size < 8) return false;
            const size_t payload_start = sef_start - static_cast<size_t>(offset);
            const uint32_t name_size = read_le32(payload_start + 4);
            if (name_size > size - 8 || static_cast<size_t>(name_size) > input_size - (payload_start + 8)) return false;
            std::string_view name(reinterpret_cast<const char*>(input + payload_start + 8), name_size);
            const size_t data_start = payload_start + 8 + name_size;
            const size_t data_len = size - 8 - name_size;
            out_fp = lpb::crypto::compute_samsung_sef_entry_fingerprint(
                marker, name, static_cast<uint32_t>(data_len), input + data_start, data_len);
            return true;
        }
    }
    return false;
}

} // namespace lpb::protocols


extern "C" LPB_API lpb_result LPB_CALL lpb_samsung_sef_build_trailer(
    lpb_context* context,
    const uint8_t* video_data,
    size_t video_size,
    int32_t is_heic,
    uint64_t image_size,
    uint8_t* output,
    size_t output_size,
    size_t* out_written)
{
    if (!context || (!video_data && video_size > 0)) return LPB_RESULT_INVALID_ARGUMENT;
    if (video_size == 0 || !is_valid_isobmff_media_range(video_data, video_size, 0, video_size)) {
        set_error(context, "Samsung MotionPhoto video is not a structurally valid ISO-BMFF range.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (is_heic != 0 && image_size > std::numeric_limits<uint32_t>::max() - 8) {
        set_error(context, "Samsung HEIC mpv2 video offset exceeds its 32-bit field.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    bool is_heic_bool = (is_heic != 0);
    if (video_size > std::numeric_limits<size_t>::max() - 256) {
        set_error(context, "Samsung trailer size overflows the host size type.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    size_t payload_len = is_heic_bool ? 12 : video_size;
    size_t tag1_len = 24 + payload_len;
    size_t tag2_len = 31;
    size_t total_tags_len = tag1_len + tag2_len;
    size_t sef_len = 44;

    if (total_tags_len > std::numeric_limits<size_t>::max() - sef_len) return LPB_RESULT_INVALID_ARGUMENT;
    size_t final_size = total_tags_len + sef_len;
    if (is_heic_bool && (video_size > std::numeric_limits<size_t>::max() - final_size - 16)) return LPB_RESULT_INVALID_ARGUMENT;
    if (is_heic_bool) final_size += 16 + video_size;
    if (final_size > std::numeric_limits<uint32_t>::max() || total_tags_len > std::numeric_limits<uint32_t>::max() ||
        video_size > std::numeric_limits<uint32_t>::max() || sef_len > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "Samsung trailer exceeds its 32-bit size fields.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> buffer(final_size);
    binary_writer writer(buffer);
    bool write_ok = true;
    const auto check_write = [&](bool ok) noexcept { write_ok = write_ok && ok; };

    if (is_heic_bool) {
        uint32_t sefd_len = 8 + static_cast<uint32_t>(total_tags_len) + static_cast<uint32_t>(sef_len);
        uint32_t mpvd_len = 8 + static_cast<uint32_t>(video_size) + sefd_len;
        
        check_write(writer.try_write_be32u(mpvd_len));
        check_write(writer.try_write_bytes((const uint8_t*)"mpvd", 4));
        check_write(writer.try_write_bytes(video_data, video_size));
        
        check_write(writer.try_write_be32u(sefd_len));
        check_write(writer.try_write_bytes((const uint8_t*)"sefd", 4));
    }

    // Tag 1 (MotionPhoto_Data)
    check_write(writer.try_write_u16_endian(0, false));
    check_write(writer.try_write_u16_endian(0x0A30, false));
    check_write(writer.try_write_u32_endian(16, false));
    check_write(writer.try_write_bytes((const uint8_t*)"MotionPhoto_Data", 16));
    if (is_heic_bool) {
        check_write(writer.try_write_bytes((const uint8_t*)"mpv2", 4));
        // Samsung's mpv2 pointer is an absolute file offset; the MP4 follows
        // the 8-byte ISOBMFF header immediately.
        check_write(writer.try_write_be32u(static_cast<uint32_t>(image_size + 8)));
        check_write(writer.try_write_be32u(static_cast<uint32_t>(video_size)));
    } else {
        check_write(writer.try_write_bytes(video_data, video_size));
    }

    // Tag 2 (MotionPhoto_Version)
    check_write(writer.try_write_u16_endian(0, false));
    check_write(writer.try_write_u16_endian(0x0A31, false));
    check_write(writer.try_write_u32_endian(19, false));
    check_write(writer.try_write_bytes((const uint8_t*)"MotionPhoto_Version", 19));
    check_write(writer.try_write_bytes((const uint8_t*)"mpv3", 4));

    // SEFH
    check_write(writer.try_write_bytes((const uint8_t*)"SEFH", 4));
    check_write(writer.try_write_u32_endian(107, false));
    check_write(writer.try_write_u32_endian(2, false));

    // entry 1 (Data)
    check_write(writer.try_write_u16_endian(0, false));
    check_write(writer.try_write_u16_endian(0x0A30, false));
    check_write(writer.try_write_u32_endian((uint32_t)total_tags_len, false));
    check_write(writer.try_write_u32_endian((uint32_t)tag1_len, false));

    // entry 2 (Version)
    check_write(writer.try_write_u16_endian(0, false));
    check_write(writer.try_write_u16_endian(0x0A31, false));
    check_write(writer.try_write_u32_endian((uint32_t)tag2_len, false));
    check_write(writer.try_write_u32_endian((uint32_t)tag2_len, false));

    // total_size excludes the trailing total_size field and SEFT marker.
    check_write(writer.try_write_u32_endian(static_cast<uint32_t>(sef_len - 8), false));
    check_write(writer.try_write_bytes((const uint8_t*)"SEFT", 4));

    if (!write_ok || writer.position() != buffer.size()) {
        set_error(context, "Failed to build a complete Samsung SEF trailer.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return copy_output(context, buffer, output, output_size, out_written);
}

