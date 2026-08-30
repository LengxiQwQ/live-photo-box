#include "foundation/internal.h"
#include "containers/isobmff.h"

namespace
{
    constexpr std::array<uint8_t, 11> vivo_tail_signature{
        0x1B, 0x2A, 0x39, 0x48, 0x57, 0x66, 0x75, 0x84, 0x93, 0xA2, 0xB3};
    constexpr std::array<uint8_t, 16> vivo_user_type{
        'v', 'i', 'v', 'o', 'M', 'e', 'd', 'i', 'a', 'E', 'x', 't', 'I', 'n', 'f', 'o'};
    constexpr std::array<uint8_t, 32> vivo_id_key{
        '"', 'c', 'o', 'm', '.', 'a', 'n', 'd', 'r', 'o', 'i', 'd', '.', 'c', 'a', 'm', 'e', 'r', 'a',
        '.', 'l', 'i', 'v', 'e', 'p', 'h', 'o', 't', 'o', '"', ':', '"'};
    constexpr std::array<uint8_t, 5> vivo_marker{'v', 'i', 'v', 'o', '{'};

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
