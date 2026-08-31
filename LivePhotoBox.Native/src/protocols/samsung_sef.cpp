#include "foundation/internal.h"
#include "binary/binary_io.h"

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

    size_t pos = input_size - 4;
    binary_reader reader(input, input_size);
    if (!reader.try_seek(pos)) return LPB_RESULT_INVALID_ARGUMENT;

    std::span<const uint8_t> tail;
    if (!reader.try_read_span(4, tail) || tail[0] != 'S' || tail[1] != 'E' || tail[2] != 'F' || tail[3] != 'T') {
        set_error(context, "SEFT marker not found at end of file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    reader.try_seek(pos - 4);
    uint32_t total_size = 0;
    if (!reader.try_read_u32_endian(total_size, false)) return LPB_RESULT_INVALID_ARGUMENT; // LE

    if (total_size < 16 || total_size > input_size) {
        set_error(context, "Invalid SEF total size.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    size_t sef_start = input_size - total_size;
    reader.try_seek(sef_start);

    std::span<const uint8_t> head;
    if (!reader.try_read_span(4, head) || head[0] != 'S' || head[1] != 'E' || head[2] != 'F' || head[3] != 'H') {
        set_error(context, "SEFH marker not found at expected offset.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    uint32_t version = 0;
    uint32_t count = 0;
    if (!reader.try_read_u32_endian(version, false) || !reader.try_read_u32_endian(count, false)) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    for (uint32_t i = 0; i < count; i++) {
        uint16_t prefix = 0;
        uint16_t marker = 0;
        uint32_t offset = 0;
        uint32_t size = 0;

        if (!reader.try_read_u16_endian(prefix, false)) break;
        if (!reader.try_read_u16_endian(marker, false)) break;
        if (!reader.try_read_u32_endian(offset, false)) break;
        if (!reader.try_read_u32_endian(size, false)) break;

        if (marker == 0x0A30) { if (size < 24 || offset > sef_start) { set_error(context, "Invalid MotionPhoto_Data size or offset."); return LPB_RESULT_INVALID_ARGUMENT; }
            *out_video_offset = (uint64_t)sef_start - offset + 24;
            *out_video_size = size - 24;
            return LPB_RESULT_OK;
        }
    }

    set_error(context, "MotionPhoto_Data entry not found in SEF.");
    return LPB_RESULT_INVALID_ARGUMENT;
}

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
    (void)image_size; // ABI-retained; mpv2 offsets are mpvd-relative.

    bool is_heic_bool = (is_heic != 0);
    size_t payload_len = is_heic_bool ? 12 : video_size;
    size_t tag1_len = 24 + payload_len;
    size_t tag2_len = 31;
    size_t total_tags_len = tag1_len + tag2_len;
    size_t sef_len = 44;

    size_t final_size = total_tags_len + sef_len;
    if (is_heic_bool) {
        final_size += 16 + video_size;
    }

    std::vector<uint8_t> buffer(final_size);
    binary_writer writer(buffer);

    if (is_heic_bool) {
        uint32_t sefd_len = 8 + (uint32_t)total_tags_len + (uint32_t)sef_len;
        uint32_t mpvd_len = 8 + (uint32_t)video_size + sefd_len;
        
        writer.try_write_be32u(mpvd_len);
        writer.try_write_bytes((const uint8_t*)"mpvd", 4);
        writer.try_write_bytes(video_data, video_size);
        
        writer.try_write_be32u(sefd_len);
        writer.try_write_bytes((const uint8_t*)"sefd", 4);
    }

    // Tag 1 (MotionPhoto_Data)
    writer.try_write_u16_endian(0, false);
    writer.try_write_u16_endian(0x0A30, false);
    writer.try_write_u32_endian(16, false);
    writer.try_write_bytes((const uint8_t*)"MotionPhoto_Data", 16);
    if (is_heic_bool) {
        writer.try_write_bytes((const uint8_t*)"mpv2", 4);
        // mpv2 offsets are relative to the mpvd box start; the MP4 follows
        // the 8-byte ISOBMFF header immediately.
        writer.try_write_be32u(8);
        writer.try_write_be32u((uint32_t)video_size);
    } else {
        writer.try_write_bytes(video_data, video_size);
    }

    // Tag 2 (MotionPhoto_Version)
    writer.try_write_u16_endian(0, false);
    writer.try_write_u16_endian(0x0A31, false);
    writer.try_write_u32_endian(19, false);
    writer.try_write_bytes((const uint8_t*)"MotionPhoto_Version", 19);
    writer.try_write_bytes((const uint8_t*)"mpv3", 4);

    // SEFH
    writer.try_write_bytes((const uint8_t*)"SEFH", 4);
    writer.try_write_u32_endian(107, false);
    writer.try_write_u32_endian(2, false);

    // entry 1 (Data)
    writer.try_write_u16_endian(0, false);
    writer.try_write_u16_endian(0x0A30, false);
    writer.try_write_u32_endian((uint32_t)total_tags_len, false);
    writer.try_write_u32_endian((uint32_t)tag1_len, false);

    // entry 2 (Version)
    writer.try_write_u16_endian(0, false);
    writer.try_write_u16_endian(0x0A31, false);
    writer.try_write_u32_endian((uint32_t)tag2_len, false);
    writer.try_write_u32_endian((uint32_t)tag2_len, false);

    writer.try_write_u32_endian((uint32_t)sef_len, false);
    writer.try_write_bytes((const uint8_t*)"SEFT", 4);

    return copy_output(context, buffer, output, output_size, out_written);
}

