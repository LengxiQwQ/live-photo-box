#include "foundation/portable_internal.h"
#include "binary/endian.h"
#include "containers/isobmff.h"
#include "media/vivo_modern_inventory.h"

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
    constexpr std::array<uint8_t, 11> camera_album_marker{
        'c', 'a', 'm', 'e', 'r', 'a', 'l', 'b', 'u', 'm', '!'};

    bool is_valid_vivo_image_tail(const uint8_t* start, const uint8_t* end)
    {
        if (!start || !end || start > end || end - start < static_cast<ptrdiff_t>(8 + camera_album_marker.size() + 4 + 4 + vivo_tail_signature.size())) {
            return false;
        }
        if (!std::equal(vivo_marker.begin(), vivo_marker.end(), start)) return false;
        const uint8_t* album = std::search(
            start + vivo_marker.size(), end, camera_album_marker.begin(), camera_album_marker.end());
        if (album == end || album < start + 8) return false;

        const uint8_t* json_length = album - 4;
        if (read_be32u(json_length) != static_cast<uint32_t>(album - start - 8)) return false;
        if (end - album < static_cast<ptrdiff_t>(camera_album_marker.size() + 4)) return false;

        const uint32_t id_record_length = read_be32u(album + camera_album_marker.size());
        const uint8_t* record_start = album + camera_album_marker.size();
        if (id_record_length < 19 || id_record_length != static_cast<uint64_t>(end - record_start)) return false;

        const uint8_t* record_end = record_start + id_record_length;
        if (record_end < album || record_end != end || id_record_length < 19) return false;
        const size_t id_size = static_cast<size_t>(id_record_length - 19);
        const uint8_t* separator = record_start + 4 + id_size;
        if (std::any_of(separator, separator + 4, [](uint8_t b) { return b != 0xFF; })) return false;
        return std::equal(vivo_tail_signature.begin(), vivo_tail_signature.end(), separator + 4);
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
        if (id_end == json + json_size || id_end == id_start) {
            error = "vivo JSON contains an unterminated or empty livephoto ID.";
            return false;
        }
        const size_t id_size = static_cast<size_t>(id_end - id_start);

        size_t total_size = json_size;
        const auto add_size = [&](size_t value) {
            if (value > std::numeric_limits<size_t>::max() - total_size) return false;
            total_size += value;
            return true;
        };
        if (!add_size(4) || !add_size(11) || !add_size(4) || !add_size(id_size) ||
            !add_size(4) || !add_size(vivo_tail_signature.size())) {
            error = "vivo metadata size overflows the host size type.";
            return false;
        }
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

    bool strip_vivo_uuid_boxes(
        const uint8_t* input,
        size_t input_size,
        std::vector<uint8_t>& result,
        std::string& error)
    {
        struct target_box { size_t start; size_t size; };
        std::vector<target_box> targets;
        size_t position = 0;
        while (position < input_size)
        {
            isobmff_box_header box{};
            if (!try_read_box_header(input, position, input_size, box))
            {
                error = "Input video contains a malformed ISO-BMFF box.";
                return false;
            }
            const bool uuid = input[position + 4] == 'u' && input[position + 5] == 'u'
                && input[position + 6] == 'i' && input[position + 7] == 'd';
            if (uuid && box.size >= box.header_size + vivo_user_type.size()
                && std::equal(vivo_user_type.begin(), vivo_user_type.end(), input + position + box.header_size))
            {
                targets.push_back({ position, box.size });
            }
            position += box.size;
        }

        if (targets.empty())
        {
            result.clear();
            if (input_size != 0)
            {
                result.assign(input, input + input_size);
            }
            return true;
        }

        size_t removed = 0;
        for (const target_box& target : targets)
        {
            if (target.size > input_size - removed)
            {
                error = "vivo UUID metadata removal size overflow.";
                return false;
            }
            removed += target.size;
        }
        result.clear();
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
            size_t prior_removed = 0;
            for (const target_box& target : targets) {
                if (target.start < prior_removed ||
                    !adjust_chunk_offsets(result, moov, target.start - prior_removed, target.size)) {
                    error = "Unable to safely relocate MP4 chunk offsets after vivo UUID removal.";
                    return false;
                }
                prior_removed += target.size;
            }
        }
        return true;
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

static lpb_result rewrite_image_metadata_impl(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* vivo_json,
    size_t vivo_json_size,
    int32_t replace_existing,
    uint8_t* output,
    size_t output_size,
    size_t* required_size, [[maybe_unused]] bool internal)
{
#if !defined(LPB_NATIVE_TEST_HARNESS)
    if (context != nullptr && !internal && !lpb_has_clean_authority(context))
    {
        if (required_size) *required_size = 0;
        set_error(context, "[AuthorityViolation] Legacy raw mutation requires active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

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
            const uint8_t* search_end = end;
            while (search_end > begin) {
                const uint8_t* found = std::find_end(
                    begin, search_end, vivo_marker.begin(), vivo_marker.end());
                if (found == search_end) break;
                if (is_valid_vivo_image_tail(found, end)) {
                    prefix_size = static_cast<size_t>(found - input);
                    break;
                }
                search_end = found;
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

static lpb_result rewrite_video_metadata_impl(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    const uint8_t* vivo_json,
    size_t vivo_json_size,
    uint8_t* output,
    size_t output_size,
    size_t* required_size, [[maybe_unused]] bool internal)
{
#if !defined(LPB_NATIVE_TEST_HARNESS)
    if (context != nullptr && !internal && !lpb_has_clean_authority(context))
    {
        if (required_size) *required_size = 0;
        set_error(context, "[AuthorityViolation] Legacy raw mutation requires active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

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

        std::vector<uint8_t> result;
        if (!strip_vivo_uuid_boxes(input, input_size, result, error))
        {
            set_error(context, error.c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        result.insert(result.end(), box.begin(), box.end());
        return copy_output(context, result, output, output_size, required_size);
    }
    catch (...)
    {
        set_error(context, "Unexpected failure while building vivo video metadata.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
}



lpb_result LPB_CALL lpb_vivo_rewrite_image_metadata(
    lpb_context* context, const uint8_t* input, size_t input_size,
    const uint8_t* vivo_json, size_t vivo_json_size, int32_t replace_existing,
    uint8_t* output, size_t output_size, size_t* required_size)
{
    return rewrite_image_metadata_impl(context, input, input_size, vivo_json, vivo_json_size, replace_existing,
        output, output_size, required_size, false);
}


lpb_result LPB_CALL lpb_vivo_rewrite_video_metadata(
    lpb_context* context, const uint8_t* input, size_t input_size,
    const uint8_t* vivo_json, size_t vivo_json_size,
    uint8_t* output, size_t output_size, size_t* required_size)
{
    return rewrite_video_metadata_impl(context, input, input_size, vivo_json, vivo_json_size,
        output, output_size, required_size, false);
}

namespace lpb::target {
lpb_result vivo_pair(lpb_context* ctx, const std::vector<uint8_t>& image,
    const std::vector<uint8_t>& video, uint32_t frame, const std::string& id,
    std::vector<uint8_t>& image_out, std::vector<uint8_t>& video_out) {
    if(image.size()<4 || image[0]!=0xff || image[1]!=0xd8) return LPB_RESULT_INVALID_ARGUMENT;
    const auto begin=image.data(), end=begin+image.size();
    const uint8_t* search_end=end;
    while(search_end>begin) {
        const auto marker=std::find_end(begin,search_end,vivo_marker.begin(),vivo_marker.end());
        if(marker==search_end) break;
        if(is_valid_vivo_image_tail(marker,end)) {
            set_error(ctx,"Input contains an existing vivo image binding."); return LPB_RESULT_UNSUPPORTED;
        }
        search_end=marker;
    }
    std::vector<uint8_t> clean; std::string error;
    if(!strip_vivo_uuid_boxes(video.data(),video.size(),clean,error)) {
        set_error(ctx,error.c_str()); return LPB_RESULT_INVALID_ARGUMENT;
    }
    // A neutral movie may retain an ordinary metadata owner after its live
    // binding was removed. Reuse only a fully observed, binding-free owner;
    // preserve its ordinary JSON members instead of silently discarding them.
    std::string ordinary;
    if(clean!=video) {
        lpb::media::vivo_modern_inventory metadata;
        if(!lpb::media::observe_vivo_modern(video,metadata)||!metadata.found||metadata.binding||metadata.box_end!=video.size()) {
            set_error(ctx,"Input contains an unverified or live-bound vivo UUID.");return LPB_RESULT_UNSUPPORTED;
        }
        for(const auto& member:metadata.members) {
            if(member.key=="version"||member.key=="com.vivo.gallery.livePhoto.newCoverTime"||member.key=="com.vivo.gallery.livePhoto.bestTime")continue;
            ordinary.push_back(',');ordinary.append(reinterpret_cast<const char*>(video.data()+member.begin),member.end-member.begin);
        }
    }
    const std::string image_json="vivo{\"com.android.camera.livephoto\":\""+id+"\",\"version\":2104}";
    const std::string video_json="vivo{\"com.android.camera.imageTime\":"+std::to_string(frame)+",\"com.android.camera.livephoto\":\""+id+"\",\"version\":2104"+ordinary+"}";
    auto rewrite=[&](bool is_image,const std::vector<uint8_t>& in,const std::string& json,std::vector<uint8_t>& out) {
        size_t size=0;
        auto invoke=[&](uint8_t* dst,size_t cap) {
            const auto* bytes=reinterpret_cast<const uint8_t*>(json.data());
            return is_image?rewrite_image_metadata_impl(ctx,in.data(),in.size(),bytes,json.size(),0,dst,cap,&size,true):
                rewrite_video_metadata_impl(ctx,in.data(),in.size(),bytes,json.size(),dst,cap,&size,true);
        };
        auto result=invoke(nullptr,0); if(result!=LPB_RESULT_BUFFER_TOO_SMALL) return result;
        out.resize(size); result=invoke(out.data(),out.size());
        if(result!=LPB_RESULT_OK) out.clear(); else out.resize(size);
        return result;
    };
    auto result=rewrite(true,image,image_json,image_out);
    if(result==LPB_RESULT_OK) result=rewrite(false,video,video_json,video_out);
    if(result!=LPB_RESULT_OK) {image_out.clear();video_out.clear();}
    return result;
}
}
