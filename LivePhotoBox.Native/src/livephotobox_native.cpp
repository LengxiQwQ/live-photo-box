#include "livephotobox_native.h"
#include "livephotobox_native_version.h"

#include <cstring>
#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <utility>
#include <vector>

#ifndef LPB_PRODUCT_VERSION
#define LPB_PRODUCT_VERSION "0.0.0.0"
#endif

struct lpb_context
{
    lpb_log_callback log_callback{};
    lpb_cancel_callback cancel_callback{};
    void* user_data{};
    std::mutex error_mutex;
    std::string last_error;
};

namespace
{
    constexpr size_t context_options_v1_size =
        offsetof(lpb_context_options, user_data) + sizeof(lpb_context_options::user_data);

    void set_error(lpb_context* context, const char* message) noexcept
    {
        if (context == nullptr)
        {
            return;
        }

        try
        {
            std::scoped_lock lock(context->error_mutex);
            context->last_error = message == nullptr ? "Unknown native error." : message;
        }
        catch (...)
        {
            // Error reporting must never throw through the C ABI.
        }
    }

    void log_message(lpb_context* context, lpb_log_level level, const char* message) noexcept
    {
        if (context == nullptr || context->log_callback == nullptr || message == nullptr)
        {
            return;
        }

        try
        {
            context->log_callback(context->user_data, level, message, std::strlen(message));
        }
        catch (...)
        {
            set_error(context, "A native log callback failed.");
        }
    }

    constexpr std::array<uint8_t, 11> vivo_tail_signature{
        0x1B, 0x2A, 0x39, 0x48, 0x57, 0x66, 0x75, 0x84, 0x93, 0xA2, 0xB3};
    constexpr std::array<uint8_t, 16> vivo_user_type{
        'v', 'i', 'v', 'o', 'M', 'e', 'd', 'i', 'a', 'E', 'x', 't', 'I', 'n', 'f', 'o'};
    constexpr std::array<uint8_t, 32> vivo_id_key{
        '"', 'c', 'o', 'm', '.', 'a', 'n', 'd', 'r', 'o', 'i', 'd', '.', 'c', 'a', 'm', 'e', 'r', 'a',
        '.', 'l', 'i', 'v', 'e', 'p', 'h', 'o', 't', 'o', '"', ':', '"'};
    constexpr std::array<uint8_t, 5> vivo_marker{'v', 'i', 'v', 'o', '{'};

    uint32_t read_be32u(const uint8_t* data) noexcept
    {
        return (static_cast<uint32_t>(data[0]) << 24)
            | (static_cast<uint32_t>(data[1]) << 16)
            | (static_cast<uint32_t>(data[2]) << 8)
            | static_cast<uint32_t>(data[3]);
    }

    int32_t read_be32(const uint8_t* data) noexcept
    {
        return static_cast<int32_t>(read_be32u(data));
    }

    int64_t read_be64(const uint8_t* data) noexcept
    {
        const uint64_t value = (static_cast<uint64_t>(read_be32u(data)) << 32)
            | static_cast<uint64_t>(read_be32u(data + 4));
        return static_cast<int64_t>(value);
    }

    void write_be32(uint8_t* data, int32_t value) noexcept
    {
        const uint32_t bits = static_cast<uint32_t>(value);
        data[0] = static_cast<uint8_t>(bits >> 24);
        data[1] = static_cast<uint8_t>(bits >> 16);
        data[2] = static_cast<uint8_t>(bits >> 8);
        data[3] = static_cast<uint8_t>(bits);
    }

    void write_be64(uint8_t* data, int64_t value) noexcept
    {
        const uint64_t bits = static_cast<uint64_t>(value);
        write_be32(data, static_cast<int32_t>(bits >> 32));
        write_be32(data + 4, static_cast<int32_t>(bits));
    }

    void append_be32(std::vector<uint8_t>& output, uint32_t value)
    {
        output.push_back(static_cast<uint8_t>(value >> 24));
        output.push_back(static_cast<uint8_t>(value >> 16));
        output.push_back(static_cast<uint8_t>(value >> 8));
        output.push_back(static_cast<uint8_t>(value));
    }

    bool is_type(const std::vector<uint8_t>& data, size_t offset, const char* type) noexcept
    {
        return offset <= data.size() && data.size() - offset >= 8
            && data[offset + 4] == static_cast<uint8_t>(type[0])
            && data[offset + 5] == static_cast<uint8_t>(type[1])
            && data[offset + 6] == static_cast<uint8_t>(type[2])
            && data[offset + 7] == static_cast<uint8_t>(type[3]);
    }

    size_t find_child_box(
        const std::vector<uint8_t>& data,
        size_t start,
        size_t end,
        const char* type) noexcept
    {
        size_t position = start;
        while (position <= end && end - position >= 8 && data.size() - position >= 8)
        {
            const int32_t signed_size = read_be32(data.data() + position);
            if (signed_size < 8)
            {
                break;
            }
            const size_t size = static_cast<size_t>(signed_size);
            if (size > end - position || size > data.size() - position)
            {
                break;
            }
            if (is_type(data, position, type))
            {
                return position;
            }
            position += size;
        }
        return std::numeric_limits<size_t>::max();
    }

    size_t find_top_level_box(const std::vector<uint8_t>& data, const char* type) noexcept
    {
        return find_child_box(data, 0, data.size(), type);
    }

    void adjust_trak_chunk_offsets(
        std::vector<uint8_t>& data,
        size_t trak_start,
        size_t trak_end,
        size_t threshold,
        size_t removed_bytes) noexcept
    {
        const size_t missing = std::numeric_limits<size_t>::max();
        const size_t mdia = find_child_box(data, trak_start + 8, trak_end, "mdia");
        if (mdia == missing)
        {
            return;
        }
        const int32_t mdia_size = read_be32(data.data() + mdia);
        if (mdia_size < 8 || static_cast<size_t>(mdia_size) > data.size() - mdia)
        {
            return;
        }
        const size_t mdia_end = mdia + static_cast<size_t>(mdia_size);
        const size_t minf = find_child_box(data, mdia + 8, mdia_end, "minf");
        if (minf == missing)
        {
            return;
        }
        const int32_t minf_size = read_be32(data.data() + minf);
        if (minf_size < 8 || static_cast<size_t>(minf_size) > data.size() - minf)
        {
            return;
        }
        const size_t minf_end = minf + static_cast<size_t>(minf_size);
        const size_t stbl = find_child_box(data, minf + 8, minf_end, "stbl");
        if (stbl == missing)
        {
            return;
        }
        const int32_t stbl_size = read_be32(data.data() + stbl);
        if (stbl_size < 8 || static_cast<size_t>(stbl_size) > data.size() - stbl)
        {
            return;
        }
        const size_t stbl_end = stbl + static_cast<size_t>(stbl_size);

        const size_t stco = find_child_box(data, stbl + 8, stbl_end, "stco");
        if (stco != missing && stco + 16 <= stbl_end)
        {
            const int32_t count = read_be32(data.data() + stco + 12);
            for (int32_t index = 0; index < count; ++index)
            {
                const size_t field = stco + 16 + static_cast<size_t>(index) * 4;
                if (field + 4 > stbl_end)
                {
                    break;
                }
                const int32_t offset = read_be32(data.data() + field);
                if (offset > 0 && static_cast<size_t>(offset) > threshold)
                {
                    write_be32(data.data() + field,
                        offset - static_cast<int32_t>(removed_bytes));
                }
            }
        }

        const size_t co64 = find_child_box(data, stbl + 8, stbl_end, "co64");
        if (co64 != missing && co64 + 16 <= stbl_end)
        {
            const int32_t count = read_be32(data.data() + co64 + 12);
            for (int32_t index = 0; index < count; ++index)
            {
                const size_t field = co64 + 16 + static_cast<size_t>(index) * 8;
                if (field + 8 > stbl_end)
                {
                    break;
                }
                const int64_t offset = read_be64(data.data() + field);
                if (offset > 0 && static_cast<uint64_t>(offset) > threshold)
                {
                    write_be64(data.data() + field,
                        offset - static_cast<int64_t>(removed_bytes));
                }
            }
        }
    }

    void adjust_chunk_offsets(
        std::vector<uint8_t>& data,
        size_t moov_start,
        size_t threshold,
        size_t removed_bytes) noexcept
    {
        if (moov_start > data.size() || data.size() - moov_start < 8)
        {
            return;
        }
        const int32_t signed_size = read_be32(data.data() + moov_start);
        if (signed_size < 8 || static_cast<size_t>(signed_size) > data.size() - moov_start)
        {
            return;
        }
        const size_t moov_end = moov_start + static_cast<size_t>(signed_size);
        size_t position = moov_start + 8;
        while (position + 8 <= moov_end)
        {
            const int32_t child_size = read_be32(data.data() + position);
            if (child_size < 8 || static_cast<size_t>(child_size) > moov_end - position)
            {
                break;
            }
            if (is_type(data, position, "trak"))
            {
                adjust_trak_chunk_offsets(
                    data, position, position + static_cast<size_t>(child_size),
                    threshold, removed_bytes);
            }
            position += static_cast<size_t>(child_size);
        }
    }

    bool build_vivo_tail(
        const uint8_t* json,
        size_t json_size,
        std::vector<uint8_t>& tail,
        std::string& error)
    {
        if (json == nullptr || json_size < 4)
        {
            error = "vivo JSON must include the complete vivo prefix.";
            return false;
        }

        const auto key = std::search(json, json + json_size,
            vivo_id_key.begin(), vivo_id_key.end());
        if (key == json + json_size)
        {
            error = "vivo JSON does not contain com.android.camera.livephoto ID.";
            return false;
        }
        const uint8_t* id_start = key + vivo_id_key.size();
        const uint8_t* id_end = std::find(id_start, json + json_size, static_cast<uint8_t>('"'));
        const size_t id_size = static_cast<size_t>(id_end - id_start);

        const size_t total_size = json_size + 4 + 11 + 4 + id_size + 4 + vivo_tail_signature.size();
        if (total_size > std::numeric_limits<uint32_t>::max()
            || json_size - 4 > std::numeric_limits<uint32_t>::max()
            || id_size > std::numeric_limits<uint32_t>::max() - 19)
        {
            error = "vivo metadata is too large.";
            return false;
        }

        tail.clear();
        tail.reserve(total_size);
        tail.insert(tail.end(), json, json + json_size);
        append_be32(tail, static_cast<uint32_t>(json_size - 4));
        constexpr std::array<uint8_t, 11> camera_album{
            'c', 'a', 'm', 'e', 'r', 'a', 'l', 'b', 'u', 'm', '!'};
        tail.insert(tail.end(), camera_album.begin(), camera_album.end());
        append_be32(tail, static_cast<uint32_t>(19 + id_size));
        tail.insert(tail.end(), id_start, id_end);
        tail.insert(tail.end(), 4, 0xFF);
        tail.insert(tail.end(), vivo_tail_signature.begin(), vivo_tail_signature.end());
        return true;
    }

    std::vector<uint8_t> strip_vivo_uuid_boxes(const uint8_t* input, size_t input_size)
    {
        struct target_box { size_t start; size_t size; };
        std::vector<target_box> targets;
        size_t position = 0;
        while (position + 8 <= input_size)
        {
            const uint32_t size32 = read_be32u(input + position);
            uint64_t size = size32;
            size_t header_size = 8;
            if (size32 == 1)
            {
                if (position + 16 > input_size)
                {
                    break;
                }
                const int64_t extended_size = read_be64(input + position + 8);
                if (extended_size < 0)
                {
                    break;
                }
                size = static_cast<uint64_t>(extended_size);
                header_size = 16;
            }
            else if (size32 == 0)
            {
                size = input_size - position;
            }

            if (size < header_size || size > static_cast<uint64_t>(std::numeric_limits<int32_t>::max())
                || size > input_size - position)
            {
                break;
            }
            const size_t box_size = static_cast<size_t>(size);
            const bool uuid = input[position + 4] == 'u' && input[position + 5] == 'u'
                && input[position + 6] == 'i' && input[position + 7] == 'd';
            if (uuid && box_size >= 24
                && std::equal(vivo_user_type.begin(), vivo_user_type.end(), input + position + 8))
            {
                targets.push_back({ position, box_size });
            }
            position += box_size;
        }

        if (targets.empty())
        {
            std::vector<uint8_t> unchanged;
            if (input_size != 0)
            {
                unchanged.assign(input, input + input_size);
            }
            return unchanged;
        }

        size_t removed = 0;
        for (const target_box& target : targets)
        {
            removed += target.size;
        }
        std::vector<uint8_t> result;
        result.reserve(input_size - removed);
        size_t source = 0;
        for (const target_box& target : targets)
        {
            if (target.start > source)
            {
                result.insert(result.end(), input + source, input + target.start);
            }
            source = target.start + target.size;
        }
        if (input_size > source)
        {
            result.insert(result.end(), input + source, input + input_size);
        }

        const size_t moov = find_top_level_box(result, "moov");
        if (moov != std::numeric_limits<size_t>::max())
        {
            adjust_chunk_offsets(result, moov, targets.front().start, removed);
        }
        return result;
    }

    bool build_vivo_uuid_box(
        const std::vector<uint8_t>& tail,
        std::vector<uint8_t>& box,
        std::string& error)
    {
        const size_t box_size = 8 + vivo_user_type.size() + tail.size();
        if (box_size > std::numeric_limits<uint32_t>::max())
        {
            error = "vivo uuid metadata box is too large.";
            return false;
        }
        box.clear();
        box.reserve(box_size);
        append_be32(box, static_cast<uint32_t>(box_size));
        box.insert(box.end(), { 'u', 'u', 'i', 'd' });
        box.insert(box.end(), vivo_user_type.begin(), vivo_user_type.end());
        box.insert(box.end(), tail.begin(), tail.end());
        return true;
    }

    lpb_result copy_output(
        lpb_context* context,
        const std::vector<uint8_t>& value,
        uint8_t* output,
        size_t output_size,
        size_t* required_size) noexcept
    {
        if (required_size == nullptr)
        {
            set_error(context, "A required-size pointer is required.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        *required_size = value.size();
        if (output == nullptr || output_size < value.size())
        {
            return LPB_RESULT_BUFFER_TOO_SMALL;
        }
        if (!value.empty())
        {
            std::memcpy(output, value.data(), value.size());
        }
        return LPB_RESULT_OK;
    }
}

uint32_t LPB_CALL lpb_get_abi_version(void)
{
    return LPB_NATIVE_ABI_VERSION;
}

const char* LPB_CALL lpb_get_version(void)
{
    return LPB_PRODUCT_VERSION;
}

lpb_result LPB_CALL lpb_create_context(
    const lpb_context_options* options,
    lpb_context** context)
{
    if (context == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    *context = nullptr;

    if (options != nullptr)
    {
        if (options->struct_size < context_options_v1_size)
        {
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        if (options->abi_version != LPB_NATIVE_ABI_VERSION)
        {
            return LPB_RESULT_ABI_MISMATCH;
        }
    }

    lpb_context* created = nullptr;
    try
    {
        created = new (std::nothrow) lpb_context();
    }
    catch (...)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (created == nullptr)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (options != nullptr)
    {
        created->log_callback = options->log_callback;
        created->cancel_callback = options->cancel_callback;
        created->user_data = options->user_data;
    }

    *context = created;
    log_message(created, LPB_LOG_DEBUG, "LivePhotoBox.Native context created.");
    return LPB_RESULT_OK;
}

void LPB_CALL lpb_destroy_context(lpb_context* context)
{
    if (context == nullptr)
    {
        return;
    }

    log_message(context, LPB_LOG_DEBUG, "LivePhotoBox.Native context destroyed.");
    delete context;
}

lpb_result LPB_CALL lpb_get_runtime_info(
    lpb_context* context,
    lpb_runtime_info* info)
{
    if (context == nullptr || info == nullptr)
    {
        set_error(context, "Context and runtime info are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (info->struct_size < sizeof(lpb_runtime_info))
    {
        set_error(context, "Runtime info structure is too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    info->abi_version = LPB_NATIVE_ABI_VERSION;
    info->capabilities = LPB_CAPABILITY_FOUNDATION | LPB_CAPABILITY_VIVO_LEGACY | LPB_CAPABILITY_HUAWEI_HONOR;
    return LPB_RESULT_OK;
}

lpb_result LPB_CALL lpb_context_check_cancelled(lpb_context* context)
{
    if (context == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    int32_t cancelled = 0;
    try
    {
        if (context->cancel_callback != nullptr)
        {
            cancelled = context->cancel_callback(context->user_data);
        }
    }
    catch (...)
    {
        set_error(context, "A native cancellation callback failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    if (cancelled != 0)
    {
        set_error(context, "The native operation was cancelled.");
        return LPB_RESULT_CANCELLED;
    }

    return LPB_RESULT_OK;
}

lpb_result LPB_CALL lpb_get_last_error(
    lpb_context* context,
    char* utf8_buffer,
    size_t buffer_size,
    size_t* required_size)
{
    if (context == nullptr || required_size == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try
    {
        std::scoped_lock lock(context->error_mutex);
        const size_t needed = context->last_error.size() + 1;
        *required_size = needed;

        if (utf8_buffer == nullptr || buffer_size < needed)
        {
            return LPB_RESULT_BUFFER_TOO_SMALL;
        }

        std::memcpy(utf8_buffer, context->last_error.c_str(), needed);
        return LPB_RESULT_OK;
    }
    catch (...)
    {
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

lpb_result LPB_CALL lpb_vivo_rewrite_image_metadata(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* vivo_json,
    size_t vivo_json_size,
    int32_t replace_existing,
    uint8_t* output,
    size_t output_size,
    size_t* required_size)
{
    if (context == nullptr || (input == nullptr && input_size != 0)
        || vivo_json == nullptr || vivo_json_size == 0)
    {
        set_error(context, "Context, input bytes, and vivo JSON are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try
    {
        std::vector<uint8_t> tail;
        std::string error;
        if (!build_vivo_tail(vivo_json, vivo_json_size, tail, error))
        {
            set_error(context, error.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        size_t prefix_size = input_size;
        if (replace_existing != 0 && input_size >= 8)
        {
            constexpr size_t tail_window_bytes = 2 * 1024 * 1024;
            const size_t window_start = input_size > tail_window_bytes
                ? input_size - tail_window_bytes
                : 0;
            const uint8_t* begin = input + window_start;
            const uint8_t* end = input + input_size;
            const uint8_t* found = std::find_end(
                begin, end, vivo_marker.begin(), vivo_marker.end());
            if (found != end)
            {
                prefix_size = static_cast<size_t>(found - input);
            }
        }

        std::vector<uint8_t> result;
        result.reserve(prefix_size + tail.size());
        if (prefix_size != 0)
        {
            result.insert(result.end(), input, input + prefix_size);
        }
        result.insert(result.end(), tail.begin(), tail.end());
        return copy_output(context, result, output, output_size, required_size);
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while building vivo image metadata.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}

lpb_result LPB_CALL lpb_vivo_rewrite_video_metadata(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* vivo_json,
    size_t vivo_json_size,
    uint8_t* output,
    size_t output_size,
    size_t* required_size)
{
    if (context == nullptr || (input == nullptr && input_size != 0)
        || vivo_json == nullptr || vivo_json_size == 0)
    {
        set_error(context, "Context, input bytes, and vivo JSON are required.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try
    {
        std::vector<uint8_t> tail;
        std::vector<uint8_t> box;
        std::string error;
        if (!build_vivo_tail(vivo_json, vivo_json_size, tail, error)
            || !build_vivo_uuid_box(tail, box, error))
        {
            set_error(context, error.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        std::vector<uint8_t> result = strip_vivo_uuid_boxes(input, input_size);
        result.insert(result.end(), box.begin(), box.end());
        return copy_output(context, result, output, output_size, required_size);
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while building vivo video metadata.");
        return LPB_RESULT_INTERNAL_ERROR;
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
        std::memcpy(output + 40, live.c_str(), std::min<size_t>(live.length(), 14));

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
