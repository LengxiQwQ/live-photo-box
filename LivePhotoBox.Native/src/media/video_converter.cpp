#include "media/video_converter.h"
#include "media/media_inspector.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include "platform/windows_filesystem.h"
#include <fstream>
#include <filesystem>
#include <vector>
#include <string_view>
#include <cmath>
#include <algorithm>
#include <memory>

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")

namespace fs = std::filesystem;

namespace lpb::media {

static bool file_read_at(void* user, uint64_t offset, std::span<uint8_t> bytes) noexcept {
    auto& file = *static_cast<std::ifstream*>(user);
    if (offset > static_cast<uint64_t>(std::numeric_limits<std::streamoff>::max()) ||
        bytes.size() > static_cast<size_t>(std::numeric_limits<std::streamsize>::max())) return false;
    file.clear();
    file.seekg(static_cast<std::streamoff>(offset), std::ios::beg);
    if (!file.good()) return false;
    file.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return file.gcount() == static_cast<std::streamsize>(bytes.size());
}

struct mf_byte_stream_closer {
    void operator()(IMFByteStream* stream) const noexcept {
        if (stream) { static_cast<void>(stream->Close()); stream->Release(); }
    }
};

static lpb_result probe_video_reader(
    lpb_context* context,
    const lpb::random_access_reader& source,
    lpb_video_item_facts* out_video_facts) noexcept
{
    std::memset(out_video_facts, 0, sizeof(lpb_video_item_facts));
    out_video_facts->struct_size = sizeof(lpb_video_item_facts);
    out_video_facts->is_present = 1;
    out_video_facts->file_range.offset = 0;
    out_video_facts->file_range.length = source.length;

    auto boxes = ::scan_top_level_boxes(source);
    if (boxes.empty() || boxes.back().size != source.length - boxes.back().offset) {
        set_error(context, "Video file does not contain a complete ISO-BMFF box layout.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    
    // Check ftyp for container
    bool found_ftyp = false;
    for (const auto& b : boxes) {
        if (std::memcmp(b.type, "ftyp", 4) == 0 && b.size >= 12) {
            found_ftyp = true;
            uint8_t brand[4]{};
            if (!source.read_exact(b.offset + 8, brand)) {
                set_error(context, "Cannot read ISO-BMFF ftyp brand.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (std::memcmp(brand, "qt  ", 4) == 0) {
                out_video_facts->container = LPB_VIDEO_CONTAINER_MOV;
            } else {
                out_video_facts->container = LPB_VIDEO_CONTAINER_MP4;
            }
            break;
        }
    }
    if (!found_ftyp) {
        // QuickTime files sometimes omit ftyp and start with moov or mdat
        out_video_facts->container = LPB_VIDEO_CONTAINER_MOV;
    }

    // Locate and read moov box
    const top_level_box* moov_box = nullptr;
    bool has_mdat = false;
    for (const auto& b : boxes) {
        if (std::memcmp(b.type, "moov", 4) == 0) {
            moov_box = &b;
        }
        if (std::memcmp(b.type, "mdat", 4) == 0) has_mdat = true;
    }

    if (moov_box && moov_box->size > 8 && moov_box->size <= 64 * 1024 * 1024) {
        std::vector<uint8_t> moov_data(static_cast<size_t>(moov_box->size));
        if (!source.read_exact(moov_box->offset, moov_data)) {
            set_error(context, "Cannot read the complete ISO-BMFF moov box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        size_t moov_end = moov_data.size();

        // Parse mvhd for overall duration
        size_t mvhd_pos = find_child_box(moov_data, 8, moov_end, "mvhd");
        if (mvhd_pos != SIZE_MAX && mvhd_pos + 32 <= moov_end) {
            uint8_t version = moov_data[mvhd_pos + 8];
            uint32_t timescale = 0;
            uint64_t duration = 0;

            if (version == 0) {
                timescale = (static_cast<uint32_t>(moov_data[mvhd_pos + 20]) << 24) |
                            (static_cast<uint32_t>(moov_data[mvhd_pos + 21]) << 16) |
                            (static_cast<uint32_t>(moov_data[mvhd_pos + 22]) << 8)  |
                            (static_cast<uint32_t>(moov_data[mvhd_pos + 23]));
                duration = (static_cast<uint32_t>(moov_data[mvhd_pos + 24]) << 24) |
                           (static_cast<uint32_t>(moov_data[mvhd_pos + 25]) << 16) |
                           (static_cast<uint32_t>(moov_data[mvhd_pos + 26]) << 8)  |
                           (static_cast<uint32_t>(moov_data[mvhd_pos + 27]));
            } else if (version == 1 && mvhd_pos + 40 <= moov_end) {
                timescale = (static_cast<uint32_t>(moov_data[mvhd_pos + 28]) << 24) |
                            (static_cast<uint32_t>(moov_data[mvhd_pos + 29]) << 16) |
                            (static_cast<uint32_t>(moov_data[mvhd_pos + 30]) << 8)  |
                            (static_cast<uint32_t>(moov_data[mvhd_pos + 31]));
                duration = (static_cast<uint64_t>(moov_data[mvhd_pos + 32]) << 56) |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 33]) << 48) |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 34]) << 40) |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 35]) << 32) |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 36]) << 24) |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 37]) << 16) |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 38]) << 8)  |
                           (static_cast<uint64_t>(moov_data[mvhd_pos + 39]));
            }

            if (timescale > 0) {
                out_video_facts->duration_seconds = static_cast<double>(duration) / timescale;
            }
        }

        // Loop over tracks (trak)
        size_t pos = 8;
        while (pos < moov_end) {
            size_t trak_pos = find_child_box(moov_data, pos, moov_end, "trak");
            if (trak_pos == SIZE_MAX || trak_pos + 8 > moov_end) break;

            uint32_t trak_size = (static_cast<uint32_t>(moov_data[trak_pos]) << 24) |
                                 (static_cast<uint32_t>(moov_data[trak_pos + 1]) << 16) |
                                 (static_cast<uint32_t>(moov_data[trak_pos + 2]) << 8)  |
                                 (static_cast<uint32_t>(moov_data[trak_pos + 3]));

            if (trak_size < 8) break;
            size_t trak_end = std::min(trak_pos + trak_size, moov_end);

            size_t mdia_pos = find_child_box(moov_data, trak_pos + 8, trak_end, "mdia");
            if (mdia_pos != SIZE_MAX && mdia_pos + 8 <= trak_end) {
                uint32_t mdia_size = (static_cast<uint32_t>(moov_data[mdia_pos]) << 24) |
                                     (static_cast<uint32_t>(moov_data[mdia_pos + 1]) << 16) |
                                     (static_cast<uint32_t>(moov_data[mdia_pos + 2]) << 8)  |
                                     (static_cast<uint32_t>(moov_data[mdia_pos + 3]));
                size_t mdia_end = std::min(mdia_pos + mdia_size, trak_end);

                // Structure-based hdlr handler detection
                size_t hdlr_pos = find_child_box(moov_data, mdia_pos + 8, mdia_end, "hdlr");
                if (hdlr_pos != SIZE_MAX && hdlr_pos + 20 <= mdia_end) {
                    const char* handler_type = reinterpret_cast<const char*>(moov_data.data() + hdlr_pos + 16);

                    if (std::memcmp(handler_type, "soun", 4) == 0) {
                        out_video_facts->has_audio = 1;
                    } else if (std::memcmp(handler_type, "vide", 4) == 0) {
                        // Parse tkhd for rotation and dimensions
                        size_t tkhd_pos = find_child_box(moov_data, trak_pos + 8, trak_end, "tkhd");
                        if (tkhd_pos != SIZE_MAX && tkhd_pos + 84 <= trak_end) {
                            uint8_t tkhd_ver = moov_data[tkhd_pos + 8];
                            size_t matrix_offset = (tkhd_ver == 1) ? (tkhd_pos + 56) : (tkhd_pos + 48);
                            size_t dim_offset = (tkhd_ver == 1) ? (tkhd_pos + 92) : (tkhd_pos + 84);

                            if (matrix_offset + 36 <= trak_end) {
                                int32_t a = (static_cast<int32_t>(moov_data[matrix_offset]) << 24) |
                                            (static_cast<int32_t>(moov_data[matrix_offset + 1]) << 16) |
                                            (static_cast<int32_t>(moov_data[matrix_offset + 2]) << 8)  |
                                            (static_cast<int32_t>(moov_data[matrix_offset + 3]));
                                int32_t b = (static_cast<int32_t>(moov_data[matrix_offset + 4]) << 24) |
                                            (static_cast<int32_t>(moov_data[matrix_offset + 5]) << 16) |
                                            (static_cast<int32_t>(moov_data[matrix_offset + 6]) << 8)  |
                                            (static_cast<int32_t>(moov_data[matrix_offset + 7]));

                                if (a == 0 && b == 0x00010000) out_video_facts->rotation_degrees = 90;
                                else if (a == -0x00010000 && b == 0) out_video_facts->rotation_degrees = 180;
                                else if (a == 0 && (b == -0x00010000 || static_cast<uint32_t>(b) == 0xFFFF0000)) out_video_facts->rotation_degrees = 270;
                                else out_video_facts->rotation_degrees = 0;
                            }

                            if (dim_offset + 8 <= trak_end) {
                                uint32_t w = (static_cast<uint32_t>(moov_data[dim_offset]) << 8) |
                                             (static_cast<uint32_t>(moov_data[dim_offset + 1]));
                                uint32_t h = (static_cast<uint32_t>(moov_data[dim_offset + 4]) << 8) |
                                             (static_cast<uint32_t>(moov_data[dim_offset + 5]));
                                if (w > 0 && h > 0) {
                                    out_video_facts->width = w;
                                    out_video_facts->height = h;
                                }
                            }
                        }

                        // Parse mdhd for timescale
                        size_t mdhd_pos = find_child_box(moov_data, mdia_pos + 8, mdia_end, "mdhd");
                        uint32_t track_timescale = 0;
                        if (mdhd_pos != SIZE_MAX && mdhd_pos + 28 <= mdia_end) {
                            uint8_t mdhd_ver = moov_data[mdhd_pos + 8];
                            if (mdhd_ver == 0 && mdhd_pos + 24 <= mdia_end) {
                                track_timescale = (static_cast<uint32_t>(moov_data[mdhd_pos + 20]) << 24) |
                                                  (static_cast<uint32_t>(moov_data[mdhd_pos + 21]) << 16) |
                                                  (static_cast<uint32_t>(moov_data[mdhd_pos + 22]) << 8)  |
                                                  (static_cast<uint32_t>(moov_data[mdhd_pos + 23]));
                            } else if (mdhd_ver == 1 && mdhd_pos + 32 <= mdia_end) {
                                track_timescale = (static_cast<uint32_t>(moov_data[mdhd_pos + 28]) << 24) |
                                                  (static_cast<uint32_t>(moov_data[mdhd_pos + 29]) << 16) |
                                                  (static_cast<uint32_t>(moov_data[mdhd_pos + 30]) << 8)  |
                                                  (static_cast<uint32_t>(moov_data[mdhd_pos + 31]));
                            }
                        }

                        // Parse minf -> stbl -> stsd (codec) & stts (fps)
                        size_t minf_pos = find_child_box(moov_data, mdia_pos + 8, mdia_end, "minf");
                        if (minf_pos != SIZE_MAX && minf_pos + 8 <= mdia_end) {
                            uint32_t minf_size = (static_cast<uint32_t>(moov_data[minf_pos]) << 24) |
                                                 (static_cast<uint32_t>(moov_data[minf_pos + 1]) << 16) |
                                                 (static_cast<uint32_t>(moov_data[minf_pos + 2]) << 8)  |
                                                 (static_cast<uint32_t>(moov_data[minf_pos + 3]));
                            size_t minf_end = std::min(minf_pos + minf_size, mdia_end);

                            size_t stbl_pos = find_child_box(moov_data, minf_pos + 8, minf_end, "stbl");
                            if (stbl_pos != SIZE_MAX && stbl_pos + 8 <= minf_end) {
                                uint32_t stbl_size = (static_cast<uint32_t>(moov_data[stbl_pos]) << 24) |
                                                     (static_cast<uint32_t>(moov_data[stbl_pos + 1]) << 16) |
                                                     (static_cast<uint32_t>(moov_data[stbl_pos + 2]) << 8)  |
                                                     (static_cast<uint32_t>(moov_data[stbl_pos + 3]));
                                size_t stbl_end = std::min(stbl_pos + stbl_size, minf_end);

                                // stsd entry inspection
                                size_t stsd_pos = find_child_box(moov_data, stbl_pos + 8, stbl_end, "stsd");
                                if (stsd_pos != SIZE_MAX && stsd_pos + 24 <= stbl_end) {
                                    uint32_t entry_count = (static_cast<uint32_t>(moov_data[stsd_pos + 12]) << 24) |
                                                           (static_cast<uint32_t>(moov_data[stsd_pos + 13]) << 16) |
                                                           (static_cast<uint32_t>(moov_data[stsd_pos + 14]) << 8)  |
                                                           (static_cast<uint32_t>(moov_data[stsd_pos + 15]));
                                    if (entry_count > 0) {
                                        const char* format_4cc = reinterpret_cast<const char*>(moov_data.data() + stsd_pos + 20);
                                        if (std::memcmp(format_4cc, "avc1", 4) == 0 || std::memcmp(format_4cc, "avc3", 4) == 0) {
                                            out_video_facts->codec = LPB_VIDEO_CODEC_H264;
                                        } else if (std::memcmp(format_4cc, "hvc1", 4) == 0 || std::memcmp(format_4cc, "hev1", 4) == 0) {
                                            out_video_facts->codec = LPB_VIDEO_CODEC_HEVC;
                                        }
                                    }
                                }

                                // stts fps inspection
                                size_t stts_pos = find_child_box(moov_data, stbl_pos + 8, stbl_end, "stts");
                                if (stts_pos != SIZE_MAX && stts_pos + 16 <= stbl_end && track_timescale > 0) {
                                    uint32_t entry_count = (static_cast<uint32_t>(moov_data[stts_pos + 12]) << 24) |
                                                           (static_cast<uint32_t>(moov_data[stts_pos + 13]) << 16) |
                                                           (static_cast<uint32_t>(moov_data[stts_pos + 14]) << 8)  |
                                                           (static_cast<uint32_t>(moov_data[stts_pos + 15]));
                                    if (entry_count == 1 && stts_pos + 24 <= stbl_end) {
                                        uint32_t sample_delta = (static_cast<uint32_t>(moov_data[stts_pos + 20]) << 24) |
                                                                (static_cast<uint32_t>(moov_data[stts_pos + 21]) << 16) |
                                                                (static_cast<uint32_t>(moov_data[stts_pos + 22]) << 8)  |
                                                                (static_cast<uint32_t>(moov_data[stts_pos + 23]));
                                        if (sample_delta > 0) {
                                            out_video_facts->fps = static_cast<double>(track_timescale) / sample_delta;
                                        }
                                    } else if (entry_count > 1) {
                                        uint64_t total_samples = 0;
                                        uint64_t total_duration = 0;
                                        for (uint32_t i = 0; i < entry_count; ++i) {
                                            size_t entry_offset = stts_pos + 16 + static_cast<size_t>(i) * 8;
                                            if (entry_offset + 8 > stbl_end) break;
                                            uint32_t s_count = (static_cast<uint32_t>(moov_data[entry_offset]) << 24) |
                                                               (static_cast<uint32_t>(moov_data[entry_offset + 1]) << 16) |
                                                               (static_cast<uint32_t>(moov_data[entry_offset + 2]) << 8)  |
                                                               (static_cast<uint32_t>(moov_data[entry_offset + 3]));
                                            uint32_t s_delta = (static_cast<uint32_t>(moov_data[entry_offset + 4]) << 24) |
                                                               (static_cast<uint32_t>(moov_data[entry_offset + 5]) << 16) |
                                                               (static_cast<uint32_t>(moov_data[entry_offset + 6]) << 8)  |
                                                               (static_cast<uint32_t>(moov_data[entry_offset + 7]));
                                            total_samples += s_count;
                                            total_duration += static_cast<uint64_t>(s_count) * s_delta;
                                        }
                                        if (total_duration > 0) {
                                            out_video_facts->fps = static_cast<double>(total_samples * track_timescale) / total_duration;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            pos = trak_end;
        }
    }

    const bool has_video_track = out_video_facts->codec != LPB_VIDEO_CODEC_UNKNOWN &&
        out_video_facts->width > 0 && out_video_facts->height > 0;
    if (moov_box == nullptr || !has_mdat || !has_video_track ||
        out_video_facts->duration_seconds <= 0 || out_video_facts->fps <= 0) {
        set_error(context, "Video probe could not establish a complete video stream (moov/mdat, codec, dimensions, duration, or frame rate missing).");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    return LPB_RESULT_OK;
}

lpb_result probe_video_file(
    lpb_context* context,
    const char* video_path,
    lpb_video_item_facts* out_video_facts) noexcept
{
    if (!video_path || !out_video_facts) {
        set_error(context, "Invalid arguments for video probe.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (out_video_facts->struct_size < sizeof(lpb_video_item_facts)) {
        set_error(context, "out_video_facts struct_size is invalid.");
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

    const lpb::random_access_reader source{static_cast<uint64_t>(file_size), &file, file_read_at};
    return probe_video_reader(context, source, out_video_facts);
}

static lpb_result remux_video_reader(
    lpb_context* context,
    const lpb::random_access_reader& source,
    const char* output_video_path,
    lpb_video_container target_container) noexcept
{
    auto boxes = ::scan_top_level_boxes(source);
    if (boxes.empty()) {
        set_error(context, "No valid ISO-BMFF boxes found in input video.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    uint64_t scanned_size = 0;
    for (const auto& box : boxes) {
        if (box.offset != scanned_size || box.size < box.header_size || box.size > source.length - scanned_size) {
            set_error(context, "Input video contains a truncated or overlapping ISO-BMFF box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        scanned_size += box.size;
    }
    if (scanned_size != source.length ||
        std::memcmp(boxes.front().type, "ftyp", 4) != 0 ||
        boxes.front().size < boxes.front().header_size + 8) {
        set_error(context, "Input video does not contain a complete ISO-BMFF range beginning with ftyp.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Structural pre-flight validation: must have moov and mdat
    bool has_moov = false;
    bool has_mdat = false;
    for (const auto& b : boxes) {
        if (std::memcmp(b.type, "moov", 4) == 0) has_moov = true;
        if (std::memcmp(b.type, "mdat", 4) == 0) has_mdat = true;
    }
    if (!has_moov || !has_mdat) {
        set_error(context, "Unsupported ISO-BMFF layout for zero-copy remux (missing moov or mdat).");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // Build target ftyp box
    std::vector<uint8_t> new_ftyp;
    if (target_container == LPB_VIDEO_CONTAINER_MOV) {
        // QuickTime: length=20, brand='qt  ', minor=0, compatible=['qt  ']
        new_ftyp = {
            0x00, 0x00, 0x00, 0x14,
            'f', 't', 'y', 'p',
            'q', 't', ' ', ' ',
            0x00, 0x00, 0x00, 0x00,
            'q', 't', ' ', ' '
        };
    } else {
        // MP4: length=24, brand='mp42', minor=0, compatible=['mp42', 'isom']
        new_ftyp = {
            0x00, 0x00, 0x00, 0x18,
            'f', 't', 'y', 'p',
            'm', 'p', '4', '2',
            0x00, 0x00, 0x00, 0x00,
            'm', 'p', '4', '2',
            'i', 's', 'o', 'm'
        };
    }

    // Determine old ftyp size and delta
    uint64_t old_ftyp_size = 0;
    if (std::memcmp(boxes[0].type, "ftyp", 4) == 0) {
        old_ftyp_size = boxes[0].size;
    }

    int64_t delta = static_cast<int64_t>(new_ftyp.size()) - static_cast<int64_t>(old_ftyp_size);

    auto p_out = utf8_to_path(output_video_path);
    windows_owned_output output;
    if (!output.create(p_out, L"lpb-remux")) {
        set_error(context, "Cannot create an owned output video staging file for remux.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto fail_remux = [&](lpb_result result, const char* message) noexcept {
        output.abort();
        set_error(context, message);
        return result;
    };

    // 1. Write new ftyp box
    if (!output.write_all(new_ftyp))
        return fail_remux(LPB_RESULT_INTERNAL_ERROR, "Failed to write remuxed ftyp.");

    // 2. Process and write remaining top-level boxes
    constexpr size_t chunk_buf_size = 1024 * 1024;
    std::vector<char> transfer_buf(chunk_buf_size);

    for (const auto& b : boxes) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return fail_remux(LPB_RESULT_CANCELLED, "Video remux cancelled.");
        }

        if (std::memcmp(b.type, "ftyp", 4) == 0) {
            // Already replaced by new_ftyp
            continue;
        }

        if (std::memcmp(b.type, "moov", 4) == 0) {
            // Read moov, adjust chunk offsets, write moov
            std::vector<uint8_t> moov_data(static_cast<size_t>(b.size));
            if (!source.read_exact(b.offset, moov_data)) {
                return fail_remux(LPB_RESULT_INTERNAL_ERROR, "Failed to read moov during video remux.");
            }

            if (delta != 0) {
                if (!shift_chunk_offsets(moov_data, 0, old_ftyp_size, delta)) {
                    return fail_remux(LPB_RESULT_INTERNAL_ERROR, "ISO-BMFF chunk offset shift failed (underflow or corrupted table).");
                }
            }

            if (!output.write_all(moov_data))
                return fail_remux(LPB_RESULT_INTERNAL_ERROR, "Failed to write remuxed moov.");
        } else {
            // Stream-copy box (e.g. mdat, free, etc.)
            uint64_t remaining = b.size;
            while (remaining > 0) {
                if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                    return fail_remux(LPB_RESULT_CANCELLED, "Video remux cancelled.");
                }

                size_t to_read = static_cast<size_t>(std::min<uint64_t>(remaining, transfer_buf.size()));
                if (!source.read_exact(b.offset + b.size - remaining,
                        std::span<uint8_t>(reinterpret_cast<uint8_t*>(transfer_buf.data()), to_read))) {
                    return fail_remux(LPB_RESULT_INTERNAL_ERROR, "Failed to read a complete ISO-BMFF box during remux.");
                }

                if (!output.write_all(std::span<const uint8_t>(
                        reinterpret_cast<const uint8_t*>(transfer_buf.data()),
                        to_read)))
                    return fail_remux(LPB_RESULT_INTERNAL_ERROR, "Failed to write remuxed video.");
                remaining -= to_read;
            }
        }
    }

    lpb_video_item_facts remuxed_facts{};
    if (!output.ready_to_consume() ||
        probe_video_reader(context, output.reader(), &remuxed_facts) != LPB_RESULT_OK ||
        remuxed_facts.container != target_container) {
        return fail_remux(LPB_RESULT_INTERNAL_ERROR,
            "Remuxed video failed structural validation before publication.");
    }
    if (!output.publish_no_replace(p_out)) {
        set_error(context, "Failed to publish the owned remuxed video without replacing an existing artifact.");
        return LPB_RESULT_INTERNAL_ERROR;
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
    if (paths_alias(input_video_path, output_video_path)) {
        set_error(context, "Video remux output must not overwrite its source file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto p_in = utf8_to_path(input_video_path);
    std::ifstream in(p_in, std::ios::binary | std::ios::ate);
    if (!in.is_open()) {
        set_error(context, "Cannot open input video file for remux.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::streamsize file_size = in.tellg();
    if (file_size < 16) {
        set_error(context, "Input video file too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const lpb::random_access_reader source{static_cast<uint64_t>(file_size), &in, file_read_at};
    return remux_video_reader(context, source, output_video_path, target_container);
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
    if (!input_video_path || !output_video_path) {
        set_error(context, "Invalid arguments for video transcode.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (paths_alias(input_video_path, output_video_path)) {
        set_error(context, "Video transcode output must not overwrite its source file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Probe input facts
    lpb_video_item_facts src_facts = {0};
    src_facts.struct_size = sizeof(lpb_video_item_facts);
    lpb_result probe_res = probe_video_file(context, input_video_path, &src_facts);
    if (probe_res != LPB_RESULT_OK) {
        return probe_res;
    }

    // If target codec is copy or matches source codec, perform stream remux directly
    if (target_codec == LPB_VIDEO_CODEC_COPY || target_codec == src_facts.codec) {
        if (out_encoder_used && encoder_buf_len > 0) {
            strncpy_s(out_encoder_used, encoder_buf_len, "Native-StreamRemux", _TRUNCATE);
        }
        return remux_video_file(context, input_video_path, output_video_path, target_container);
    }

    // Real cross-codec transcoding via Windows Media Foundation
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    bool co_inited = SUCCEEDED(hr);

    hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
    if (FAILED(hr)) {
        if (co_inited) CoUninitialize();
        set_error(context, "Failed to initialize Windows Media Foundation.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    auto p_in = utf8_to_path(input_video_path);
    auto p_out = utf8_to_path(output_video_path);

    // Always transcode to a sibling temporary file. Publishing directly to
    // the requested path would leave a plausible-looking partial artifact on
    // encoder failure or cancellation.
    const bool needs_mov_remux = (target_container == LPB_VIDEO_CONTAINER_MOV);
    windows_owned_output output;
    if (!output.create(p_out, L"lpb-transcode", L".mp4")) {
        MFShutdown();
        if (co_inited) CoUninitialize();
        set_error(context, "Failed to create an owned transcoding staging file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    const fs::path temp_mp4_path = output.path();

    // 1. Create SourceReader
    IMFAttributes* pReaderAttrs = nullptr;
    MFCreateAttributes(&pReaderAttrs, 2);
    if (pReaderAttrs) {
        pReaderAttrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
        pReaderAttrs->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
    }

    IMFSourceReader* pReader = nullptr;
    hr = MFCreateSourceReaderFromURL(p_in.c_str(), pReaderAttrs, &pReader);
    if (pReaderAttrs) pReaderAttrs->Release();

    if (FAILED(hr) || !pReader) {
        MFShutdown();
        if (co_inited) CoUninitialize();
        set_error(context, "Failed to open source video with Media Foundation reader.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // Query native video stream info
    IMFMediaType* pNativeVideoType = nullptr;
    hr = pReader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &pNativeVideoType);
    if (FAILED(hr) || !pNativeVideoType) {
        pReader->Release();
        MFShutdown();
        if (co_inited) CoUninitialize();
        set_error(context, "Failed to query native video stream media type.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    UINT32 width = src_facts.width > 0 ? src_facts.width : 1920;
    UINT32 height = src_facts.height > 0 ? src_facts.height : 1080;
    UINT32 fps_num = 30, fps_den = 1;
    MFGetAttributeSize(pNativeVideoType, MF_MT_FRAME_SIZE, &width, &height);
    MFGetAttributeRatio(pNativeVideoType, MF_MT_FRAME_RATE, &fps_num, &fps_den);
    if (fps_den == 0) fps_den = 1;

    pNativeVideoType->Release();

    // 2. Create SinkWriter
    IMFAttributes* pWriterAttrs = nullptr;
    MFCreateAttributes(&pWriterAttrs, 2);
    if (pWriterAttrs) {
        pWriterAttrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
        pWriterAttrs->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    }

    IMFSinkWriter* pWriter = nullptr;
    IMFByteStream* raw_stream = nullptr;
    std::unique_ptr<IMFByteStream, mf_byte_stream_closer> owned_stream;
    const char* sink_stage = "MFCreateFile";
    hr = MFCreateFile(MF_ACCESSMODE_WRITE, MF_OPENMODE_FAIL_IF_NOT_EXIST,
        MF_FILEFLAGS_ALLOW_WRITE_SHARING, temp_mp4_path.c_str(), &raw_stream);
    owned_stream.reset(raw_stream);
    if (SUCCEEDED(hr) && owned_stream) {
        // MF writes the CREATE_NEW-owned object through an opened byte stream;
        // the platform backend retains the original handle until verification.
        sink_stage = "MFCreateSinkWriterFromURL";
        hr = MFCreateSinkWriterFromURL(temp_mp4_path.c_str(), owned_stream.get(),
            pWriterAttrs, &pWriter);
    }
    if (pWriterAttrs) pWriterAttrs->Release();

    if (FAILED(hr) || !pWriter) {
        pReader->Release();
        MFShutdown();
        if (co_inited) CoUninitialize();
        const std::string error = std::string("Failed to create Media Foundation sink writer at ") +
            sink_stage + " (HRESULT " + std::to_string(static_cast<unsigned long>(hr)) + ").";
        set_error(context, error.c_str());
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // 3. Configure Video Output on SinkWriter
    GUID target_guid = (target_codec == LPB_VIDEO_CODEC_HEVC) ? MFVideoFormat_HEVC : MFVideoFormat_H264;
    IMFMediaType* pOutVideoType = nullptr;
    MFCreateMediaType(&pOutVideoType);
    pOutVideoType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    pOutVideoType->SetGUID(MF_MT_SUBTYPE, target_guid);
    
    // Calculate reasonable average bitrate based on resolution, fps, and quality/CRF mapping
    double bpp = (target_codec == LPB_VIDEO_CODEC_HEVC) ? 0.08 : 0.12;
    double fps_val = (fps_den > 0) ? (static_cast<double>(fps_num) / fps_den) : 30.0;
    if (fps_val <= 0 || fps_val > 240) fps_val = 30.0;
    uint32_t avg_bitrate = static_cast<uint32_t>(width * height * fps_val * bpp);
    if (crf > 0 && crf <= 51) {
        double crf_factor = std::pow(2.0, (23.0 - static_cast<double>(crf)) / 6.0);
        avg_bitrate = static_cast<uint32_t>(avg_bitrate * crf_factor);
    }
    if (avg_bitrate < 500000) avg_bitrate = 500000;
    if (avg_bitrate > 50000000) avg_bitrate = 50000000;

    pOutVideoType->SetUINT32(MF_MT_AVG_BITRATE, avg_bitrate);
    MFSetAttributeSize(pOutVideoType, MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(pOutVideoType, MF_MT_FRAME_RATE, fps_num, fps_den);
    MFSetAttributeRatio(pOutVideoType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    pOutVideoType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    DWORD sinkVideoIndex = 0;
    hr = pWriter->AddStream(pOutVideoType, &sinkVideoIndex);
    pOutVideoType->Release();

    if (FAILED(hr)) {
        pWriter->Release();
        pReader->Release();
        output.abort();
        MFShutdown();
        if (co_inited) CoUninitialize();
        set_error(context, "Target video codec encoder MFT is not available or rejected parameters.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // 4. Configure Uncompressed Video Input on SinkWriter and Output on SourceReader
    IMFMediaType* pInVideoType = nullptr;
    MFCreateMediaType(&pInVideoType);
    pInVideoType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    pInVideoType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    MFSetAttributeSize(pInVideoType, MF_MT_FRAME_SIZE, width, height);
    MFSetAttributeRatio(pInVideoType, MF_MT_FRAME_RATE, fps_num, fps_den);
    MFSetAttributeRatio(pInVideoType, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    pInVideoType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);

    hr = pWriter->SetInputMediaType(sinkVideoIndex, pInVideoType, nullptr);
    if (SUCCEEDED(hr)) {
        hr = pReader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, pInVideoType);
    }
    pInVideoType->Release();

    if (FAILED(hr)) {
        pWriter->Release();
        pReader->Release();
        output.abort();
        MFShutdown();
        if (co_inited) CoUninitialize();
        set_error(context, "Failed to configure uncompressed video pipeline on reader/writer.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    // 5. Handle Audio Stream (if present)
    DWORD sinkAudioIndex = 0;
    bool has_audio_stream = (src_facts.has_audio != 0);
    if (has_audio_stream) {
        IMFMediaType* pNativeAudio = nullptr;
        if (SUCCEEDED(pReader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0, &pNativeAudio)) && pNativeAudio) {
            UINT32 raw_sample_rate = 48000;
            UINT32 raw_channels = 2;
            pNativeAudio->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &raw_sample_rate);
            pNativeAudio->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &raw_channels);
            pNativeAudio->Release();

            // Normalize audio parameters: AAC encoder reliably supports 1 (mono) or 2 (stereo)
            UINT32 target_channels = (raw_channels == 1) ? 1 : 2;
            UINT32 target_sample_rate = (raw_sample_rate == 44100) ? 44100 : 48000;
            UINT32 avg_bytes = (target_channels == 1) ? 12000 : 16000; // 96 kbps mono / 128 kbps stereo

            IMFMediaType* pOutAudioType = nullptr;
            MFCreateMediaType(&pOutAudioType);
            pOutAudioType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            pOutAudioType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
            pOutAudioType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            pOutAudioType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, target_sample_rate);
            pOutAudioType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, target_channels);
            pOutAudioType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, avg_bytes);

            if (SUCCEEDED(pWriter->AddStream(pOutAudioType, &sinkAudioIndex))) {
                IMFMediaType* pInAudioType = nullptr;
                MFCreateMediaType(&pInAudioType);
                pInAudioType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                pInAudioType->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
                pInAudioType->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                pInAudioType->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, target_sample_rate);
                pInAudioType->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, target_channels);
                pInAudioType->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, target_channels * 2);
                pInAudioType->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, target_sample_rate * target_channels * 2);

                hr = pWriter->SetInputMediaType(sinkAudioIndex, pInAudioType, nullptr);
                if (SUCCEEDED(hr)) {
                    hr = pReader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), nullptr, pInAudioType);
                }
                pInAudioType->Release();

                if (FAILED(hr)) {
                    has_audio_stream = false;
                }
            } else {
                has_audio_stream = false;
            }
            pOutAudioType->Release();
        } else {
            has_audio_stream = false;
        }
    }

    // 6. Begin writing and transcode samples
    hr = pWriter->BeginWriting();
    if (FAILED(hr)) {
        pWriter->Release();
        pReader->Release();
        output.abort();
        MFShutdown();
        if (co_inited) CoUninitialize();
        set_error(context, "Media Foundation SinkWriter BeginWriting failed.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    bool video_finished = false;
    bool audio_finished = !has_audio_stream;
    lpb_result transcode_res = LPB_RESULT_OK;

    while (!video_finished || !audio_finished) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            transcode_res = LPB_RESULT_CANCELLED;
            break;
        }

        // Pump Video Sample
        if (!video_finished) {
            DWORD stream_idx = 0, flags = 0;
            LONGLONG timestamp = 0;
            IMFSample* pSample = nullptr;
            hr = pReader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &stream_idx, &flags, &timestamp, &pSample);
            if (FAILED(hr)) {
                set_error(context, "Media Foundation video ReadSample failed.");
                transcode_res = LPB_RESULT_INTERNAL_ERROR;
                break;
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
                video_finished = true;
            } else if (pSample) {
                hr = pWriter->WriteSample(sinkVideoIndex, pSample);
                pSample->Release();
                if (FAILED(hr)) {
                    set_error(context, "Media Foundation video WriteSample failed.");
                    transcode_res = LPB_RESULT_INTERNAL_ERROR;
                    break;
                }
            }
        }

        // Pump Audio Sample
        if (has_audio_stream && !audio_finished) {
            DWORD stream_idx = 0, flags = 0;
            LONGLONG timestamp = 0;
            IMFSample* pSample = nullptr;
            hr = pReader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0, &stream_idx, &flags, &timestamp, &pSample);
            if (FAILED(hr)) {
                set_error(context, "Media Foundation audio ReadSample failed.");
                transcode_res = LPB_RESULT_INTERNAL_ERROR;
                break;
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
                audio_finished = true;
            } else if (pSample) {
                hr = pWriter->WriteSample(sinkAudioIndex, pSample);
                pSample->Release();
                if (FAILED(hr)) {
                    set_error(context, "Media Foundation audio WriteSample failed.");
                    transcode_res = LPB_RESULT_INTERNAL_ERROR;
                    break;
                }
            }
        }
    }

    HRESULT finalize_hr = pWriter->Finalize();
    if (FAILED(finalize_hr) && transcode_res == LPB_RESULT_OK) {
        set_error(context, "Media Foundation SinkWriter Finalize failed.");
        transcode_res = LPB_RESULT_INTERNAL_ERROR;
    }

    pWriter->Release();
    owned_stream.reset();
    pReader->Release();
    MFShutdown();
    if (co_inited) CoUninitialize();

    if (transcode_res != LPB_RESULT_OK) {
        output.abort();
        return transcode_res;
    }

    // 7. If MOV container was requested, remux temp MP4 to MOV
    if (needs_mov_remux) {
        if (!output.ready_to_consume()) {
            set_error(context, "Transcoded staging MP4 no longer matches its creating handle.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        lpb_result remux_res = remux_video_reader(context, output.reader(),
            output_video_path, LPB_VIDEO_CONTAINER_MOV);
        output.abort();
        if (remux_res != LPB_RESULT_OK) {
            return remux_res;
        }
    } else {
        lpb_video_item_facts transcoded_facts{};
        transcoded_facts.struct_size = sizeof(lpb_video_item_facts);
        if (!output.ready_to_consume() ||
            probe_video_reader(context, output.reader(), &transcoded_facts) != LPB_RESULT_OK) {
            const std::string probe_reason = context != nullptr ? context->last_error : "unknown";
            output.abort();
            set_error(context, ("Transcoded MP4 failed Native structural validation: " + probe_reason).c_str());
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if (!output.publish_no_replace(p_out)) {
            set_error(context, "Failed to publish the owned transcoded MP4 without replacing an existing artifact.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
    }

    if (out_encoder_used && encoder_buf_len > 0) {
        const char* enc_name = (target_codec == LPB_VIDEO_CODEC_HEVC) ? "MF-HEVC-Encoder-MFT" : "MF-H264-Encoder-MFT";
        strncpy_s(out_encoder_used, encoder_buf_len, enc_name, _TRUNCATE);
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
