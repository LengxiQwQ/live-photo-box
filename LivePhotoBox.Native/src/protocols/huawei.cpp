#include "foundation/internal.h"
#include "containers/isobmff.h"

namespace {

static bool is_container_type(const uint8_t* type) noexcept
{
    return std::memcmp(type, "moov", 4) == 0 || std::memcmp(type, "trak", 4) == 0 ||
        std::memcmp(type, "mdia", 4) == 0 || std::memcmp(type, "minf", 4) == 0 ||
        std::memcmp(type, "stbl", 4) == 0 || std::memcmp(type, "edts", 4) == 0 ||
        std::memcmp(type, "dinf", 4) == 0 || std::memcmp(type, "udta", 4) == 0 ||
        std::memcmp(type, "ilst", 4) == 0;
}

static bool find_lavf_in_tool_atom(
    const uint8_t* data,
    const isobmff_box_header& tool,
    size_t& out_offset,
    size_t& out_count) noexcept
{
    const size_t body_start = tool.start + tool.header_size;
    const size_t body_end = tool.start + tool.size;
    size_t candidate = std::numeric_limits<size_t>::max();
    size_t count = 0;

    auto consider = [&](size_t value_start, size_t value_end) noexcept {
        if (value_end - value_start < 12 || std::memcmp(data + value_start, "Lavf", 4) != 0) return;
        candidate = value_start;
        ++count;
    };

    size_t pos = body_start;
    while (pos < body_end) {
        isobmff_box_header child{};
        if (!try_read_box_header(data, pos, body_end, child)) return false;
        const uint8_t* type = data + child.start + 4;
        if (std::memcmp(type, "data", 4) == 0) {
            const size_t payload = child.start + child.header_size;
            if (child.size >= child.header_size + 8) consider(payload + 8, child.start + child.size);
        }
        pos += child.size;
    }
    if (pos != body_end) return false;

    // Some encoders place the value directly in the ©too atom.  Accept that
    // only when the atom is otherwise not box-packed, and still require one
    // complete, bounded value.
    if (count == 0) consider(body_start, body_end);
    out_offset = candidate;
    out_count = count;
    return true;
}

static bool find_unique_lavf_in_range(
    const uint8_t* data,
    size_t start,
    size_t end,
    size_t& out_offset,
    size_t& out_count) noexcept
{
    if (start > end) return false;
    size_t pos = start;
    while (pos < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, pos, end, box)) return false;
        const uint8_t* type = data + box.start + 4;
        if (type[0] == 0xA9 && type[1] == 't' && type[2] == 'o' && type[3] == 'o') {
            size_t candidate = std::numeric_limits<size_t>::max();
            size_t count = 0;
            if (!find_lavf_in_tool_atom(data, box, candidate, count)) return false;
            if (count > 0) {
                if (out_count > 0) return false;
                out_offset = candidate;
                out_count = count;
            }
        }
        size_t child_start = box.start + box.header_size;
        if (std::memcmp(type, "meta", 4) == 0) {
            if (box.size < box.header_size + 4) return false;
            child_start += 4; // FullBox version and flags.
        }
        if (is_container_type(type) || std::memcmp(type, "meta", 4) == 0) {
            if (child_start < box.start + box.size &&
                !find_unique_lavf_in_range(data, child_start, box.start + box.size, out_offset, out_count)) {
                return false;
            }
        }
        pos += box.size;
    }
    return pos == end;
}

static bool validate_top_level_mp4(
    const uint8_t* data,
    size_t data_size,
    isobmff_box_header& out_ftyp,
    isobmff_box_header& out_moov) noexcept
{
    size_t pos = 0;
    bool saw_ftyp = false;
    bool saw_mdat = false;
    bool saw_moov = false;
    while (pos < data_size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, pos, data_size, box)) return false;
        const uint8_t* type = data + box.start + 4;
        if (pos == 0) {
            if (std::memcmp(type, "ftyp", 4) != 0) return false;
            out_ftyp = box;
            saw_ftyp = true;
        }
        if (std::memcmp(type, "moov", 4) == 0) {
            if (saw_moov) return false;
            out_moov = box;
            saw_moov = true;
        }
        if (std::memcmp(type, "mdat", 4) == 0) saw_mdat = true;
        pos += box.size;
    }
    return pos == data_size && saw_ftyp && saw_moov && saw_mdat;
}

static bool has_complete_box_sequence(const uint8_t* data, size_t data_size) noexcept
{
    if (!data || data_size < 8) return false;
    size_t pos = 0;
    while (pos < data_size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, pos, data_size, box)) return false;
        pos += box.size;
    }
    return pos == data_size;
}

}

lpb_result LPB_CALL lpb_huawei_build_tail(
    lpb_context* context,
    int32_t cover_frame,
    int32_t total_frames,
    uint64_t mp4_size,
    int32_t original_cover_ms,
    int32_t original_duration_ms,
    const char* prefix,
    uint8_t* output,
    size_t output_size,
    size_t* required_size)
{
    if (context == nullptr || required_size == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    *required_size = 60;
    if (output == nullptr || output_size < 60)
    {
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }

    try
    {
        if (mp4_size > std::numeric_limits<uint64_t>::max() - 20) {
            set_error(context, "Huawei LIVE_ length overflows the protocol field.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        std::memset(output, 0x20, 60);

        const char* p = (prefix && prefix[0] != '\0') ? prefix : "v6_f";
        std::string vf = std::string(p) + std::to_string(cover_frame);
        std::memcpy(output, vf.c_str(), std::min<size_t>(vf.length(), 6));

        std::string pq;
        if (original_duration_ms > 0)
        {
            pq = std::to_string(original_cover_ms) + ":" + std::to_string(original_duration_ms);
        }
        else
        {
            pq = std::to_string(cover_frame) + ":" + std::to_string(total_frames);
        }
        std::memcpy(output + 20, pq.c_str(), std::min<size_t>(pq.length(), 8));

        std::string live = "LIVE_" + std::to_string(mp4_size + 20);
        if (live.length() > 15) {
            set_error(context, "Huawei LIVE_ length does not fit the fixed trailer field.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        std::memcpy(output + 40, live.c_str(), live.length());

        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while building Huawei tail.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

lpb_result LPB_CALL lpb_huawei_patch_heic_ftyp(
    lpb_context* context,
    uint8_t* data,
    size_t data_size)
{
    if (context == nullptr || data == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (data_size < 16) return LPB_RESULT_INVALID_ARGUMENT;

    try
    {
        isobmff_box_header ftyp{};
        if (!has_complete_box_sequence(data, data_size) ||
            !try_read_box_header(data, 0, data_size, ftyp) ||
            ftyp.header_size != 8 || ftyp.size < 16 ||
            std::memcmp(data + 4, "ftyp", 4) != 0) {
            set_error(context, "Input does not start with a complete normal-size HEIC ftyp box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        size_t last_brand = ftyp.start + ftyp.size - 4;
        data[last_brand + 0] = 't';
        data[last_brand + 1] = 'm';
        data[last_brand + 2] = 'a';
        data[last_brand + 3] = 'p';

        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while patching HEIC ftyp.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

lpb_result LPB_CALL lpb_huawei_patch_mp4(
    lpb_context* context,
    uint8_t* data,
    size_t data_size)
{
    if (context == nullptr || data == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (data_size < 24) return LPB_RESULT_INVALID_ARGUMENT;

    try
    {
        isobmff_box_header ftyp{};
        isobmff_box_header moov{};
        if (!validate_top_level_mp4(data, data_size, ftyp, moov) ||
            ftyp.header_size != 8 || ftyp.size < 24) {
            set_error(context, "Input is not a complete MP4 with a normal-size ftyp and moov.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        data[8] = 'm'; data[9] = 'p'; data[10] = '4'; data[11] = '2';
        data[12] = 0; data[13] = 0; data[14] = 0; data[15] = 0;
        data[16] = 'i'; data[17] = 's'; data[18] = 'o'; data[19] = '2';
        data[20] = 'm'; data[21] = 'p'; data[22] = '4'; data[23] = '2';

        size_t lavf_offset = std::numeric_limits<size_t>::max();
        size_t lavf_count = 0;
        if (!find_unique_lavf_in_range(data, moov.start + moov.header_size,
            moov.start + moov.size, lavf_offset, lavf_count)) {
            set_error(context, "MP4 ©too metadata has malformed or ambiguous box structure.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (lavf_count > 1) {
            set_error(context, "MP4 contains multiple ambiguous Lavf tool values.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (lavf_count == 1) {
            const uint8_t oh6[] = { 'o', 'p', 'e', 'n', 'h', 'a', 'r', 'm', 'o', 'n', 'y', '6' };
            std::memcpy(data + lavf_offset, oh6, sizeof(oh6));
        }

        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while patching MP4.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}
