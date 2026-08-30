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

        if (marker == 0x0A20) { if (size < 24 || offset > sef_start) { set_error(context, " Invalid MotionPhoto_Data size or offset.\); return LPB_RESULT_INVALID_ARGUMENT; }
            *out_video_offset = (uint64_t)sef_start - offset + 24;
            *out_video_size = size - 24;
            return LPB_RESULT_OK;
        }
    }

    set_error(context, "MotionPhoto_Data entry not found in SEF.");
    return LPB_RESULT_INVALID_ARGUMENT;
}
