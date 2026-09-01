#include "media/media_extractor.h"
#include "foundation/internal.h"
#include <fstream>
#include <vector>

namespace lpb::media {

static lpb_result copy_slice_to_file(
    lpb_context* context,
    const char* src_path,
    uint64_t offset,
    uint64_t length,
    const char* dst_path)
{
    if (!src_path || !dst_path || length == 0) return LPB_RESULT_OK;

    auto p_src = utf8_to_path(src_path);
    std::ifstream in(p_src, std::ios::binary);
    if (!in.is_open()) {
        set_error(context, "Failed to open source file for reading.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    in.seekg(offset, std::ios::beg);
    if (!in.good()) {
        set_error(context, "Failed to seek to slice offset in source file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_dst = utf8_to_path(dst_path);
    std::ofstream out(p_dst, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        set_error(context, "Failed to open destination file for writing.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    constexpr size_t buffer_size = 64 * 1024;
    std::vector<char> buffer(buffer_size);
    uint64_t remaining = length;

    while (remaining > 0) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            out.close();
            return LPB_RESULT_CANCELLED;
        }

        size_t to_read = static_cast<size_t>(std::min<uint64_t>(remaining, buffer_size));
        in.read(buffer.data(), to_read);
        std::streamsize read_bytes = in.gcount();
        if (read_bytes <= 0) {
            set_error(context, "Unexpected EOF while extracting slice.");
            return LPB_RESULT_INTERNAL_ERROR;
        }

        out.write(buffer.data(), read_bytes);
        remaining -= read_bytes;
    }

    out.flush();
    return LPB_RESULT_OK;
}

lpb_result extract_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path) noexcept
{
    if (!primary_path || !facts) {
        set_error(context, "Invalid arguments for source extraction.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // 1. Extract Primary Image
    if (facts->primary_image.is_present && output_image_path && facts->primary_image.file_range.length > 0) {
        lpb_result res = copy_slice_to_file(
            context,
            primary_path,
            facts->primary_image.file_range.offset,
            facts->primary_image.file_range.length,
            output_image_path);
        if (res != LPB_RESULT_OK) return res;
    }

    // 2. Extract Motion Video
    if (facts->motion_video.is_present && output_video_path && facts->motion_video.file_range.length > 0) {
        const char* video_src = (secondary_path && std::strlen(secondary_path) > 0) 
            ? secondary_path 
            : primary_path;

        lpb_result res = copy_slice_to_file(
            context,
            video_src,
            facts->motion_video.file_range.offset,
            facts->motion_video.file_range.length,
            output_video_path);
        if (res != LPB_RESULT_OK) return res;
    }

    // 3. Extract Gain Map if present
    if (facts->gain_map.is_present && output_gainmap_path && facts->gain_map.file_range.length > 0) {
        lpb_result res = copy_slice_to_file(
            context,
            primary_path,
            facts->gain_map.file_range.offset,
            facts->gain_map.file_range.length,
            output_gainmap_path);
        if (res != LPB_RESULT_OK) return res;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
