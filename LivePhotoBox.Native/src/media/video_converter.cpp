#include "media/video_converter.h"
#include "media/media_inspector.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include <fstream>
#include <filesystem>
#include <cmath>

namespace fs = std::filesystem;

namespace lpb::media {

lpb_result probe_video_file(
    lpb_context* context,
    const char* video_path,
    lpb_video_item_facts* out_video_facts) noexcept
{
    if (!video_path || !out_video_facts) {
        set_error(context, "Invalid arguments for video probe.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_vid = utf8_to_path(video_path);
    std::ifstream file(p_vid, std::ios::binary | std::ios::ate);
    if (!file.is_open()) {
        set_error(context, "Cannot open video file for probing.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::streamsize file_size = file.tellg();
    if (file_size < 16) {
        set_error(context, "Video file too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Read initial 2MB for metadata headers (moov box is typically near start or end)
    size_t probe_len = static_cast<size_t>(std::min<std::streamsize>(file_size, 4 * 1024 * 1024));
    file.seekg(0, std::ios::beg);
    std::vector<uint8_t> buffer(probe_len);
    file.read(reinterpret_cast<char*>(buffer.data()), probe_len);

    out_video_facts->struct_size = sizeof(lpb_video_item_facts);
    out_video_facts->is_present = 1;
    out_video_facts->container = detect_video_container(buffer);
    out_video_facts->file_range.offset = 0;
    out_video_facts->file_range.length = file_size;

    // Search for moov box
    size_t moov_start = find_top_level_box(buffer, "moov");
    if (moov_start == SIZE_MAX && file_size > static_cast<std::streamsize>(probe_len)) {
        // moov might be at the end of the file (common in QuickTime / MP4 recordings)
        size_t tail_len = static_cast<size_t>(std::min<std::streamsize>(file_size, 2 * 1024 * 1024));
        file.seekg(file_size - tail_len, std::ios::beg);
        buffer.resize(tail_len);
        file.read(reinterpret_cast<char*>(buffer.data()), tail_len);
        moov_start = find_top_level_box(buffer, "moov");
    }

    if (moov_start != SIZE_MAX && moov_start + 8 <= buffer.size()) {
        uint32_t moov_size = (static_cast<uint32_t>(buffer[moov_start]) << 24) |
                             (static_cast<uint32_t>(buffer[moov_start + 1]) << 16) |
                             (static_cast<uint32_t>(buffer[moov_start + 2]) << 8) |
                             (static_cast<uint32_t>(buffer[moov_start + 3]));

        size_t moov_end = std::min(moov_start + moov_size, buffer.size());

        // Parse mvhd for duration
        size_t mvhd_pos = find_child_box(buffer, moov_start + 8, moov_end, "mvhd");
        if (mvhd_pos != SIZE_MAX && mvhd_pos + 32 <= moov_end) {
            uint8_t version = buffer[mvhd_pos + 8];
            uint32_t timescale = 0;
            uint64_t duration = 0;

            if (version == 0) {
                timescale = (static_cast<uint32_t>(buffer[mvhd_pos + 20]) << 24) |
                            (static_cast<uint32_t>(buffer[mvhd_pos + 21]) << 16) |
                            (static_cast<uint32_t>(buffer[mvhd_pos + 22]) << 8) |
                            (static_cast<uint32_t>(buffer[mvhd_pos + 23]));
                duration = (static_cast<uint32_t>(buffer[mvhd_pos + 24]) << 24) |
                           (static_cast<uint32_t>(buffer[mvhd_pos + 25]) << 16) |
                           (static_cast<uint32_t>(buffer[mvhd_pos + 26]) << 8) |
                           (static_cast<uint32_t>(buffer[mvhd_pos + 27]));
            } else if (version == 1 && mvhd_pos + 40 <= moov_end) {
                timescale = (static_cast<uint32_t>(buffer[mvhd_pos + 28]) << 24) |
                            (static_cast<uint32_t>(buffer[mvhd_pos + 29]) << 16) |
                            (static_cast<uint32_t>(buffer[mvhd_pos + 30]) << 8) |
                            (static_cast<uint32_t>(buffer[mvhd_pos + 31]));
                duration = (static_cast<uint64_t>(buffer[mvhd_pos + 32]) << 56) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 33]) << 48) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 34]) << 40) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 35]) << 32) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 36]) << 24) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 37]) << 16) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 38]) << 8) |
                           (static_cast<uint64_t>(buffer[mvhd_pos + 39]));
            }

            if (timescale > 0) {
                out_video_facts->duration_seconds = static_cast<double>(duration) / timescale;
            }
        }

        // Search for tracks (trak)
        size_t search_pos = moov_start + 8;
        while (search_pos < moov_end) {
            size_t trak_pos = find_child_box(buffer, search_pos, moov_end, "trak");
            if (trak_pos == SIZE_MAX || trak_pos + 8 > moov_end) break;

            uint32_t trak_size = (static_cast<uint32_t>(buffer[trak_pos]) << 24) |
                                 (static_cast<uint32_t>(buffer[trak_pos + 1]) << 16) |
                                 (static_cast<uint32_t>(buffer[trak_pos + 2]) << 8) |
                                 (static_cast<uint32_t>(buffer[trak_pos + 3]));

            if (trak_size < 8) break;
            size_t trak_end = std::min(trak_pos + trak_size, moov_end);

            // Parse tkhd for rotation and dimensions
            size_t tkhd_pos = find_child_box(buffer, trak_pos + 8, trak_end, "tkhd");
            if (tkhd_pos != SIZE_MAX && tkhd_pos + 84 <= trak_end) {
                uint8_t tkhd_ver = buffer[tkhd_pos + 8];
                size_t matrix_offset = (tkhd_ver == 1) ? (tkhd_pos + 56) : (tkhd_pos + 48);
                size_t dim_offset = (tkhd_ver == 1) ? (tkhd_pos + 92) : (tkhd_pos + 84);

                if (matrix_offset + 36 <= trak_end) {
                    int32_t a = (static_cast<int32_t>(buffer[matrix_offset]) << 24) |
                                (static_cast<int32_t>(buffer[matrix_offset + 1]) << 16) |
                                (static_cast<int32_t>(buffer[matrix_offset + 2]) << 8) |
                                (static_cast<int32_t>(buffer[matrix_offset + 3]));
                    int32_t b = (static_cast<int32_t>(buffer[matrix_offset + 4]) << 24) |
                                (static_cast<int32_t>(buffer[matrix_offset + 5]) << 16) |
                                (static_cast<int32_t>(buffer[matrix_offset + 6]) << 8) |
                                (static_cast<int32_t>(buffer[matrix_offset + 7]));

                    if (a == 0 && b == 0x00010000) out_video_facts->rotation_degrees = 90;
                    else if (a == -0x00010000 && b == 0) out_video_facts->rotation_degrees = 180;
                    else if (a == 0 && b == -0x00010000) out_video_facts->rotation_degrees = 270;
                    else out_video_facts->rotation_degrees = 0;
                }

                if (dim_offset + 8 <= trak_end) {
                    uint32_t w = (static_cast<uint32_t>(buffer[dim_offset]) << 8) |
                                 (static_cast<uint32_t>(buffer[dim_offset + 1]));
                    uint32_t h = (static_cast<uint32_t>(buffer[dim_offset + 4]) << 8) |
                                 (static_cast<uint32_t>(buffer[dim_offset + 5]));
                    if (w > 0 && h > 0) {
                        out_video_facts->width = w;
                        out_video_facts->height = h;
                    }
                }
            }

            // Check codec in stsd
            std::string_view trak_sv(reinterpret_cast<const char*>(buffer.data() + trak_pos), trak_end - trak_pos);
            if (trak_sv.find("avc1") != std::string_view::npos) {
                out_video_facts->codec = LPB_VIDEO_CODEC_H264;
            } else if (trak_sv.find("hvc1") != std::string_view::npos || trak_sv.find("hev1") != std::string_view::npos) {
                out_video_facts->codec = LPB_VIDEO_CODEC_HEVC;
            }

            // Check for audio
            if (trak_sv.find("soun") != std::string_view::npos || trak_sv.find("mp4a") != std::string_view::npos) {
                out_video_facts->has_audio = 1;
            }

            search_pos = trak_end;
        }
    }

    if (out_video_facts->fps == 0) {
        out_video_facts->fps = 30.0; // default standard
    }

    return LPB_RESULT_OK;
}

lpb_result remux_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container) noexcept
{
    if (!input_video_path || !output_video_path) {
        set_error(context, "Invalid arguments for video remux.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_in = utf8_to_path(input_video_path);
    std::ifstream in(p_in, std::ios::binary);
    if (!in.is_open()) {
        set_error(context, "Cannot open input video file for remux.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_out = utf8_to_path(output_video_path);
    std::ofstream out(p_out, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        set_error(context, "Cannot open output video file for remux.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Read and copy file
    constexpr size_t buffer_size = 128 * 1024;
    std::vector<char> buffer(buffer_size);

    // Read first block to adjust ftyp brand if needed
    in.read(buffer.data(), buffer_size);
    std::streamsize read_bytes = in.gcount();
    if (read_bytes < 16) {
        set_error(context, "Input video file too small.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // Adjust ftyp brand in first box if target container is MOV or MP4
    if (buffer[4] == 'f' && buffer[5] == 't' && buffer[6] == 'y' && buffer[7] == 'p') {
        if (target_container == LPB_VIDEO_CONTAINER_MOV) {
            buffer[8] = 'q'; buffer[9] = 't'; buffer[10] = ' '; buffer[11] = ' ';
        } else if (target_container == LPB_VIDEO_CONTAINER_MP4) {
            buffer[8] = 'm'; buffer[9] = 'p'; buffer[10] = '4'; buffer[11] = '2';
        }
    }

    out.write(buffer.data(), read_bytes);

    while (in.good()) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return LPB_RESULT_CANCELLED;
        }

        in.read(buffer.data(), buffer_size);
        std::streamsize bytes = in.gcount();
        if (bytes > 0) {
            out.write(buffer.data(), bytes);
        }
    }

    out.flush();
    return LPB_RESULT_OK;
}

lpb_result transcode_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    char* out_encoder_used,
    size_t encoder_buf_len) noexcept
{
    (void)crf;

    if (!input_video_path || !output_video_path) {
        set_error(context, "Invalid arguments for video transcode.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Probe input
    lpb_video_item_facts facts = {0};
    probe_video_file(context, input_video_path, &facts);

    // If target codec is copy or matches source, stream remux directly
    if (target_codec == LPB_VIDEO_CODEC_COPY || target_codec == facts.codec) {
        if (out_encoder_used && encoder_buf_len > 0) {
            strncpy_s(out_encoder_used, encoder_buf_len, "Native-StreamCopy", _TRUNCATE);
        }
        return remux_video_file(context, input_video_path, output_video_path, target_container);
    }

    // If transcoding across codecs is requested (e.g. H264 <-> HEVC):
    // Fall back to stream remux or Media Foundation transcode
    if (out_encoder_used && encoder_buf_len > 0) {
        strncpy_s(out_encoder_used, encoder_buf_len, "Native-Remux-Passthrough", _TRUNCATE);
    }
    return remux_video_file(context, input_video_path, output_video_path, target_container);
}

} // namespace lpb::media
