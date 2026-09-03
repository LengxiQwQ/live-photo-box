#include "foundation/internal.h"

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

    if (data_size < 16)
    {
        return LPB_RESULT_OK;
    }

    try
    {
        uint32_t box_size = read_be32u(data);
        if (box_size < 16 || box_size > data_size)
        {
            return LPB_RESULT_OK;
        }

        size_t last_brand = box_size - 4;
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

    if (data_size < 24)
    {
        return LPB_RESULT_OK;
    }

    try
    {
        if (data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p')
        {
            data[8] = 'm'; data[9] = 'p'; data[10] = '4'; data[11] = '2';
            data[12] = 0; data[13] = 0; data[14] = 0; data[15] = 0;
            data[16] = 'i'; data[17] = 's'; data[18] = 'o'; data[19] = '2';
            data[20] = 'm'; data[21] = 'p'; data[22] = '4'; data[23] = '2';
        }

        const uint8_t lavf[] = { 'L', 'a', 'v', 'f' };
        const uint8_t oh6[] = { 'o', 'p', 'e', 'n', 'h', 'a', 'r', 'm', 'o', 'n', 'y', '6' };
        auto it = std::search(data, data + data_size, lavf, lavf + 4);
        if (it != data + data_size && (size_t)((it - data) + 12) <= data_size)
        {
            std::memcpy(it, oh6, 12);
        }

        return LPB_RESULT_OK;
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while patching MP4.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}
