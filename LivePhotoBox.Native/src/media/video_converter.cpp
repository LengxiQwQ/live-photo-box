#include "media/video_converter.h"
#include "media/media_inspector.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include "containers/isobmff_mebx_transplant.h"
#include "containers/isobmff_video_profile.h"
#include "media/video_sidecar_bridge.h"
#include "media/video_presentation_timing.h"
#include "platform/windows_filesystem.h"
#include <fstream>
#include <filesystem>
#include <array>
#include <vector>
#include <string_view>
#include <cmath>
#include <algorithm>
#include <memory>
#include <limits>
#include <cstdio>
#include <cstring>

#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
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

static void set_backend_diagnostics(
    lpb_video_backend_diagnostics* diagnostics,
    lpb_video_backend_kind backend,
    lpb_video_hardware_mode hardware_mode,
    bool fallback_occurred,
    const char* selected_encoder,
    const char* fallback_reason) noexcept
{
    if (!diagnostics) return;
    std::memset(diagnostics, 0, sizeof(*diagnostics));
    diagnostics->struct_size = sizeof(*diagnostics);
    diagnostics->backend = backend;
    diagnostics->hardware_mode = hardware_mode;
    diagnostics->hardware_fallback_occurred = fallback_occurred ? 1 : 0;
    strncpy_s(diagnostics->selected_encoder, selected_encoder ? selected_encoder : "", _TRUNCATE);
    strncpy_s(diagnostics->fallback_reason, fallback_reason ? fallback_reason : "", _TRUNCATE);
}

static uint32_t diagnostic_fourcc(char a, char b, char c, char d) noexcept {
    return (static_cast<uint32_t>(static_cast<uint8_t>(a)) << 24U) |
        (static_cast<uint32_t>(static_cast<uint8_t>(b)) << 16U) |
        (static_cast<uint32_t>(static_cast<uint8_t>(c)) << 8U) |
        static_cast<uint32_t>(static_cast<uint8_t>(d));
}

// The frozen Native diagnostics POD already has a 64-byte selected_encoder
// field. These versioned, bounded tokens carry Native parser facts through
// that existing ABI without changing its layout or asking managed code to
// infer media profile from codec/encoder names.
static bool video_profile_token(
    const isobmff_video_profile& profile,
    char* destination,
    size_t capacity) noexcept
{
    if (destination == nullptr || capacity == 0U) return false;
    char codec = '?';
    if (profile.codec_fourcc == diagnostic_fourcc('a', 'v', 'c', '1') ||
        profile.codec_fourcc == diagnostic_fourcc('a', 'v', 'c', '3')) {
        codec = 'A';
    } else if (profile.codec_fourcc == diagnostic_fourcc('h', 'v', 'c', '1') ||
        profile.codec_fourcc == diagnostic_fourcc('h', 'e', 'v', '1')) {
        codec = 'H';
    }
    if (codec == '?' || profile.classification == video_profile_class::unknown) return false;
    const char range = profile.full_range_known == 0U ? '?' : profile.full_range != 0U ? 'F' : 'L';
    const int written = std::snprintf(destination, capacity, "%c%u.%u.%u.%u.%u.%c",
        codec,
        static_cast<unsigned>(profile.bit_depth_luma),
        static_cast<unsigned>(profile.bit_depth_chroma),
        static_cast<unsigned>(profile.color_primaries),
        static_cast<unsigned>(profile.transfer_characteristics),
        static_cast<unsigned>(profile.matrix_coefficients),
        range);
    return written > 0 && static_cast<size_t>(written) < capacity;
}

static bool profile_only_identity(
    const isobmff_video_profile& input,
    char* destination,
    size_t capacity) noexcept
{
    char input_token[24]{};
    if (!video_profile_token(input, input_token, sizeof(input_token))) return false;
    const int written = std::snprintf(destination, capacity, "p1|i%s|o?", input_token);
    return written > 0 && static_cast<size_t>(written) < capacity;
}

static bool remux_identity(
    const isobmff_video_profile& input,
    const isobmff_video_profile& output,
    char* destination,
    size_t capacity) noexcept
{
    char input_token[24]{};
    char output_token[24]{};
    if (!video_profile_token(input, input_token, sizeof(input_token)) ||
        !video_profile_token(output, output_token, sizeof(output_token))) return false;
    const int written = std::snprintf(destination, capacity, "r1|i%s|o%s", input_token, output_token);
    return written > 0 && static_cast<size_t>(written) < capacity;
}

static bool media_foundation_identity(
    const isobmff_video_profile& input,
    const isobmff_video_profile* output,
    lpb_video_codec encoder_codec,
    char* destination,
    size_t capacity) noexcept
{
    char input_token[24]{};
    char output_token[24] = "?";
    if (!video_profile_token(input, input_token, sizeof(input_token)) ||
        (output != nullptr && !video_profile_token(*output, output_token, sizeof(output_token)))) return false;
    const char* encoder = encoder_codec == LPB_VIDEO_CODEC_H264 ? "h264" :
        encoder_codec == LPB_VIDEO_CODEC_HEVC ? "hevc" : nullptr;
    if (encoder == nullptr) return false;
    const int written = std::snprintf(destination, capacity, "m1|i%s|o%s|%s",
        input_token, output_token, encoder);
    return written > 0 && static_cast<size_t>(written) < capacity;
}

static bool sidecar_identity(
    char phase,
    const char* runtime_version,
    const char* encoder,
    const isobmff_video_profile& input,
    const isobmff_video_profile* output,
    char* destination,
    size_t capacity) noexcept
{
    char input_token[24]{};
    char output_token[24] = "?";
    if (!video_profile_token(input, input_token, sizeof(input_token))) return false;
    if (output != nullptr && !video_profile_token(*output, output_token, sizeof(output_token))) return false;
    const char phase_token = phase == 's' || phase == 'c' || phase == 'v' ? phase : '?';
    if (phase_token == '?') return false;
    const int written = std::snprintf(destination, capacity, "s1%c|%s|%s|i%s|o%s",
        phase_token,
        runtime_version == nullptr ? "?" : runtime_version,
        encoder == nullptr ? "?" : encoder,
        input_token,
        output_token);
    return written > 0 && static_cast<size_t>(written) < capacity;
}

static bool map_profile_color_to_media_foundation(
    const isobmff_video_profile& profile,
    UINT32& primaries,
    UINT32& transfer,
    UINT32& matrix,
    UINT32& nominal_range) noexcept
{
    switch (profile.color_primaries) {
    case 1: primaries = MFVideoPrimaries_BT709; break;
    case 4: primaries = MFVideoPrimaries_BT470_2_SysM; break;
    case 5: primaries = MFVideoPrimaries_BT470_2_SysBG; break;
    case 6: primaries = MFVideoPrimaries_SMPTE170M; break;
    case 7: primaries = MFVideoPrimaries_SMPTE240M; break;
    case 9: primaries = MFVideoPrimaries_BT2020; break;
    case 10: primaries = MFVideoPrimaries_XYZ; break;
    case 11: primaries = MFVideoPrimaries_DCI_P3; break;
    case 12: primaries = MFVideoPrimaries_Display_P3; break;
    case 22: primaries = MFVideoPrimaries_EBU3213; break;
    default: return false;
    }

    switch (profile.transfer_characteristics) {
    case 1: case 6: transfer = MFVideoTransFunc_709; break;
    case 4: transfer = MFVideoTransFunc_22; break;
    case 5: transfer = MFVideoTransFunc_28; break;
    case 7: transfer = MFVideoTransFunc_240M; break;
    case 8: transfer = MFVideoTransFunc_10; break;
    case 9: transfer = MFVideoTransFunc_Log_100; break;
    case 10: transfer = MFVideoTransFunc_Log_316; break;
    case 11: transfer = MFVideoTransFunc_709_sym; break;
    case 12: transfer = MFVideoTransFunc_BT1361_ECG; break;
    case 13: transfer = MFVideoTransFunc_sRGB; break;
    case 14: transfer = MFVideoTransFunc_2020; break;
    case 15: transfer = MFVideoTransFunc_2084; break;
    case 16: transfer = MFVideoTransFunc_SMPTE428; break;
    case 17: transfer = MFVideoTransFunc_10_rel; break;
    case 18: transfer = MFVideoTransFunc_HLG; break;
    default: return false;
    }

    switch (profile.matrix_coefficients) {
    case 0: matrix = MFVideoTransferMatrix_Identity; break;
    case 1: matrix = MFVideoTransferMatrix_BT709; break;
    case 4: matrix = MFVideoTransferMatrix_FCC47; break;
    case 5: case 6: matrix = MFVideoTransferMatrix_BT601; break;
    case 7: matrix = MFVideoTransferMatrix_SMPTE240M; break;
    case 8: matrix = MFVideoTransferMatrix_YCgCo; break;
    case 9: matrix = MFVideoTransferMatrix_BT2020_10; break;
    case 10: matrix = MFVideoTransferMatrix_BT2020_12; break;
    case 11: matrix = MFVideoTransferMatrix_SMPTE2085; break;
    case 12: matrix = MFVideoTransferMatrix_Chroma; break;
    case 13: matrix = MFVideoTransferMatrix_Chroma_const; break;
    case 14: matrix = MFVideoTransferMatrix_ICtCp; break;
    default: return false;
    }

    if (profile.full_range_known == 0U) return false;
    nominal_range = profile.full_range != 0U ? MFNominalRange_0_255 : MFNominalRange_16_235;
    return true;
}
static bool read_all_bytes(const lpb::random_access_reader& source,
    std::vector<uint8_t>& output) noexcept {
    output.clear();
    if (source.read_at == nullptr || source.length == 0U ||
        source.length > static_cast<uint64_t>((std::numeric_limits<size_t>::max)())) return false;
    try {
        output.resize(static_cast<size_t>(source.length));
        constexpr size_t chunk_size = 1024U * 1024U;
        size_t offset = 0U;
        while (offset < output.size()) {
            const size_t count = std::min(chunk_size, output.size() - offset);
            if (!source.read_exact(offset, std::span<uint8_t>(output.data() + offset, count))) {
                output.clear();
                return false;
            }
            offset += count;
        }
        return true;
    } catch (...) {
        output.clear();
        return false;
    }
}

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

template <typename T>
class mf_owned_ptr final {
public:
    mf_owned_ptr() noexcept = default;
    explicit mf_owned_ptr(T* value) noexcept : value_(value) {}
    ~mf_owned_ptr() { reset(); }
    mf_owned_ptr(const mf_owned_ptr&) = delete;
    mf_owned_ptr& operator=(const mf_owned_ptr&) = delete;
    mf_owned_ptr(mf_owned_ptr&& other) noexcept : value_(other.detach()) {}
    mf_owned_ptr& operator=(mf_owned_ptr&& other) noexcept {
        if (this != &other) reset(other.detach());
        return *this;
    }

    T* get() const noexcept { return value_; }
    T** put() noexcept {
        reset();
        return &value_;
    }
    explicit operator bool() const noexcept { return value_ != nullptr; }
    T* operator->() const noexcept { return value_; }
    T* detach() noexcept {
        T* value = value_;
        value_ = nullptr;
        return value;
    }
    void reset(T* value = nullptr) noexcept {
        if (value_ != nullptr) value_->Release();
        value_ = value;
    }

private:
    T* value_{};
};

struct mf_activation_list final {
    IMFActivate** values{};
    UINT32 count{};
    ~mf_activation_list() {
        if (values != nullptr) {
            for (UINT32 index = 0U; index < count; ++index) {
                if (values[index] != nullptr) values[index]->Release();
            }
            CoTaskMemFree(values);
        }
    }
};

class mf_runtime_guard final {
public:
    mf_runtime_guard() noexcept = default;
    ~mf_runtime_guard() {
        if (mf_started_) static_cast<void>(MFShutdown());
        if (com_initialized_) CoUninitialize();
    }
    mf_runtime_guard(const mf_runtime_guard&) = delete;
    mf_runtime_guard& operator=(const mf_runtime_guard&) = delete;

    bool start(lpb_context* context) noexcept {
        const HRESULT com_hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        com_initialized_ = SUCCEEDED(com_hr);
        if (FAILED(com_hr) && com_hr != RPC_E_CHANGED_MODE) {
            set_error(context, "Failed to initialize COM for software Media Foundation conversion.");
            return false;
        }
        const HRESULT mf_hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(mf_hr)) {
            set_error(context, "Failed to initialize Windows Media Foundation.");
            return false;
        }
        mf_started_ = true;
        return true;
    }

private:
    bool com_initialized_{};
    bool mf_started_{};
};

struct mf_retained_video_sample {
    mf_owned_ptr<IMFSample> sample;
    LONGLONG time_100ns{};
    LONGLONG duration_100ns{};
    DWORD buffer_count{};
    uint64_t byte_count{};
    UINT32 clean_point{};
    UINT64 decode_timestamp{};
    bool clean_point_present{};
    bool decode_timestamp_present{};
};

struct mf_retained_audio_sample {
    mf_owned_ptr<IMFSample> sample;
    LONGLONG time_100ns{};
    LONGLONG duration_100ns{};
    DWORD buffer_count{};
    DWORD byte_count{};
};

static bool snapshot_mf_video_sample(
    IMFSample* sample,
    mf_retained_video_sample& facts) noexcept
{
    if (sample == nullptr) return false;
    LONGLONG time = 0;
    LONGLONG duration = 0;
    HRESULT hr = sample->GetSampleTime(&time);
    if (FAILED(hr) || time < 0) return false;
    hr = sample->GetSampleDuration(&duration);
    if (FAILED(hr) || duration <= 0) return false;

    DWORD buffer_count = 0U;
    hr = sample->GetBufferCount(&buffer_count);
    if (FAILED(hr) || buffer_count == 0U) return false;
    uint64_t total_bytes = 0U;
    for (DWORD index = 0U; index < buffer_count; ++index) {
        mf_owned_ptr<IMFMediaBuffer> buffer;
        hr = sample->GetBufferByIndex(index, buffer.put());
        if (FAILED(hr) || !buffer) return false;
        DWORD length = 0U;
        hr = buffer->GetCurrentLength(&length);
        if (FAILED(hr) || total_bytes > (std::numeric_limits<uint64_t>::max)() - length) return false;
        total_bytes += length;
    }
    if (total_bytes == 0U) return false;

    UINT32 clean_point = 0U;
    const HRESULT clean_hr = sample->GetUINT32(MFSampleExtension_CleanPoint, &clean_point);
    if (FAILED(clean_hr) && clean_hr != MF_E_ATTRIBUTENOTFOUND) return false;
    UINT64 decode_timestamp = 0U;
    const HRESULT decode_hr = sample->GetUINT64(MFSampleExtension_DecodeTimestamp, &decode_timestamp);
    if (FAILED(decode_hr) && decode_hr != MF_E_ATTRIBUTENOTFOUND) return false;

    facts.time_100ns = time;
    facts.duration_100ns = duration;
    facts.buffer_count = buffer_count;
    facts.byte_count = total_bytes;
    facts.clean_point = clean_point;
    facts.decode_timestamp = decode_timestamp;
    facts.clean_point_present = SUCCEEDED(clean_hr);
    facts.decode_timestamp_present = SUCCEEDED(decode_hr);
    return true;
}

static bool same_mf_video_sample_facts(
    const mf_retained_video_sample& left,
    const mf_retained_video_sample& right) noexcept
{
    return left.time_100ns == right.time_100ns &&
        left.duration_100ns == right.duration_100ns &&
        left.buffer_count == right.buffer_count &&
        left.byte_count == right.byte_count &&
        left.clean_point_present == right.clean_point_present &&
        left.clean_point == right.clean_point &&
        left.decode_timestamp_present == right.decode_timestamp_present &&
        left.decode_timestamp == right.decode_timestamp;
}

static bool snapshot_mf_audio_sample(
    IMFSample* sample,
    UINT32 block_alignment,
    mf_retained_audio_sample& facts) noexcept
{
    if (sample == nullptr || block_alignment == 0U) return false;
    LONGLONG time = 0;
    LONGLONG duration = 0;
    HRESULT hr = sample->GetSampleTime(&time);
    if (FAILED(hr) || time < 0) return false;
    hr = sample->GetSampleDuration(&duration);
    if (FAILED(hr) || duration <= 0) return false;

    DWORD buffer_count = 0U;
    hr = sample->GetBufferCount(&buffer_count);
    if (FAILED(hr) || buffer_count == 0U) return false;
    uint64_t total_bytes = 0U;
    for (DWORD index = 0U; index < buffer_count; ++index) {
        mf_owned_ptr<IMFMediaBuffer> buffer;
        hr = sample->GetBufferByIndex(index, buffer.put());
        if (FAILED(hr) || !buffer) return false;
        DWORD length = 0U;
        hr = buffer->GetCurrentLength(&length);
        if (FAILED(hr) || total_bytes > (std::numeric_limits<uint64_t>::max)() - length) return false;
        total_bytes += length;
    }
    if (total_bytes == 0U || total_bytes > (std::numeric_limits<DWORD>::max)() ||
        total_bytes % block_alignment != 0U) return false;

    facts.time_100ns = time;
    facts.duration_100ns = duration;
    facts.buffer_count = buffer_count;
    facts.byte_count = static_cast<DWORD>(total_bytes);
    return true;
}

static bool same_mf_audio_sample_facts(
    const mf_retained_audio_sample& left,
    const mf_retained_audio_sample& right) noexcept
{
    return left.time_100ns == right.time_100ns &&
        left.duration_100ns == right.duration_100ns &&
        left.buffer_count == right.buffer_count &&
        left.byte_count == right.byte_count;
}

static bool set_mf_video_color_attributes(
    IMFMediaType* type,
    UINT32 primaries,
    UINT32 transfer,
    UINT32 matrix,
    UINT32 nominal_range) noexcept
{
    return type != nullptr &&
        SUCCEEDED(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, primaries)) &&
        SUCCEEDED(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, transfer)) &&
        SUCCEEDED(type->SetUINT32(MF_MT_YUV_MATRIX, matrix)) &&
        SUCCEEDED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, nominal_range));
}

static bool copy_mf_type_with_optional_rate(
    IMFMediaType* source,
    bool include_rate,
    UINT32 fps_num,
    UINT32 fps_den,
    mf_owned_ptr<IMFMediaType>& result) noexcept
{
    if (source == nullptr) return false;
    HRESULT hr = MFCreateMediaType(result.put());
    if (SUCCEEDED(hr)) hr = source->CopyAllItems(result.get());
    if (SUCCEEDED(hr) && include_rate) {
        if (fps_num == 0U || fps_den == 0U) return false;
        hr = MFSetAttributeRatio(result.get(), MF_MT_FRAME_RATE, fps_num, fps_den);
    } else if (SUCCEEDED(hr)) {
        const HRESULT delete_hr = result->DeleteItem(MF_MT_FRAME_RATE);
        if (FAILED(delete_hr) && delete_hr != MF_E_ATTRIBUTENOTFOUND) hr = delete_hr;
    }
    return SUCCEEDED(hr);
}

static bool try_set_mf_transform_type(
    IMFTransform* transform,
    bool input,
    DWORD stream,
    IMFMediaType* base_type,
    bool include_rate,
    UINT32 fps_num,
    UINT32 fps_den) noexcept
{
    mf_owned_ptr<IMFMediaType> type;
    if (!copy_mf_type_with_optional_rate(base_type, include_rate, fps_num, fps_den, type)) return false;
    HRESULT hr = input
        ? transform->SetInputType(stream, type.get(), MFT_SET_TYPE_TEST_ONLY)
        : transform->SetOutputType(stream, type.get(), MFT_SET_TYPE_TEST_ONLY);
    if (FAILED(hr)) return false;
    hr = input
        ? transform->SetInputType(stream, type.get(), 0U)
        : transform->SetOutputType(stream, type.get(), 0U);
    return SUCCEEDED(hr);
}

static bool set_mf_transform_type_with_source_rate_fallback(
    IMFTransform* transform,
    bool input,
    DWORD stream,
    IMFMediaType* base_type,
    bool source_rate_valid,
    UINT32 fps_num,
    UINT32 fps_den,
    bool& used_source_rate) noexcept
{
    used_source_rate = false;
    if (try_set_mf_transform_type(transform, input, stream, base_type, false, fps_num, fps_den)) {
        return true;
    }
    if (!source_rate_valid ||
        !try_set_mf_transform_type(transform, input, stream, base_type, true, fps_num, fps_den)) {
        return false;
    }
    used_source_rate = true;
    return true;
}

static bool create_mf_video_output_type(
    lpb_video_codec target_codec,
    UINT32 width,
    UINT32 height,
    UINT32 bitrate,
    UINT32 primaries,
    UINT32 transfer,
    UINT32 matrix,
    UINT32 nominal_range,
    mf_owned_ptr<IMFMediaType>& output) noexcept
{
    HRESULT hr = MFCreateMediaType(output.put());
    if (SUCCEEDED(hr)) hr = output->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = output->SetGUID(
        MF_MT_SUBTYPE, target_codec == LPB_VIDEO_CODEC_HEVC ? MFVideoFormat_HEVC : MFVideoFormat_H264);
    if (SUCCEEDED(hr)) hr = output->SetUINT32(MF_MT_AVG_BITRATE, bitrate);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(output.get(), MF_MT_FRAME_SIZE, width, height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(output.get(), MF_MT_PIXEL_ASPECT_RATIO, 1U, 1U);
    if (SUCCEEDED(hr)) hr = output->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr) && !set_mf_video_color_attributes(
            output.get(), primaries, transfer, matrix, nominal_range)) hr = E_INVALIDARG;
    return SUCCEEDED(hr);
}

static bool configure_software_video_mft(
    IMFTransform* transform,
    IMFMediaType* input_type,
    lpb_video_codec target_codec,
    UINT32 width,
    UINT32 height,
    UINT32 bitrate,
    UINT32 primaries,
    UINT32 transfer,
    UINT32 matrix,
    UINT32 nominal_range,
    bool source_rate_valid,
    UINT32 fps_num,
    UINT32 fps_den,
    DWORD& input_stream,
    DWORD& output_stream,
    bool& input_uses_source_rate,
    bool& output_uses_source_rate,
    MFT_OUTPUT_STREAM_INFO& output_info) noexcept
{
    DWORD input_count = 0U;
    DWORD output_count = 0U;
    HRESULT hr = transform->GetStreamIDs(1U, &input_stream, 1U, &output_stream);
    if (hr == E_NOTIMPL) {
        input_stream = 0U;
        output_stream = 0U;
    } else if (FAILED(hr)) {
        return false;
    }
    hr = transform->GetStreamCount(&input_count, &output_count);
    if (FAILED(hr) || input_count != 1U || output_count != 1U) return false;

    mf_owned_ptr<IMFMediaType> output_type;
    if (!create_mf_video_output_type(target_codec, width, height, bitrate,
            primaries, transfer, matrix, nominal_range, output_type)) return false;
    if (!set_mf_transform_type_with_source_rate_fallback(
            transform, false, output_stream, output_type.get(), source_rate_valid,
            fps_num, fps_den, output_uses_source_rate)) return false;

    mf_owned_ptr<IMFMediaType> encoder_input;
    if (!copy_mf_type_with_optional_rate(input_type, false, fps_num, fps_den, encoder_input) ||
        !set_mf_video_color_attributes(encoder_input.get(), primaries, transfer, matrix, nominal_range) ||
        !set_mf_transform_type_with_source_rate_fallback(
            transform, true, input_stream, encoder_input.get(), source_rate_valid,
            fps_num, fps_den, input_uses_source_rate)) return false;

    hr = transform->GetOutputStreamInfo(output_stream, &output_info);
    return SUCCEEDED(hr);
}

enum class mf_encoder_drain_status {
    need_input,
    failed,
    cancelled
};

static mf_encoder_drain_status drain_software_video_mft(
    lpb_context* context,
    IMFTransform* transform,
    DWORD output_stream,
    const MFT_OUTPUT_STREAM_INFO& output_info,
    size_t maximum_samples,
    std::vector<mf_retained_video_sample>& output,
    bool& made_progress) noexcept
{
    made_progress = false;
    for (size_t iteration = 0U; iteration < 256U; ++iteration) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            set_error(context, "Video conversion was cancelled while draining the software encoder.");
            return mf_encoder_drain_status::cancelled;
        }

        mf_owned_ptr<IMFSample> caller_sample;
        MFT_OUTPUT_DATA_BUFFER data{};
        data.dwStreamID = output_stream;
        const bool transform_allocates =
            (output_info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0U ||
            (output_info.dwFlags & MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES) != 0U;
        if (!transform_allocates) {
            if (output_info.cbSize == 0U) {
                set_error(context, "Software video encoder requires an unusable empty output buffer.");
                return mf_encoder_drain_status::failed;
            }
            mf_owned_ptr<IMFMediaBuffer> buffer;
            HRESULT create_hr = MFCreateSample(caller_sample.put());
            if (SUCCEEDED(create_hr)) create_hr = MFCreateMemoryBuffer(output_info.cbSize, buffer.put());
            if (SUCCEEDED(create_hr)) create_hr = caller_sample->AddBuffer(buffer.get());
            if (FAILED(create_hr)) {
                set_error(context, "Failed to allocate a software video encoder output sample.");
                return mf_encoder_drain_status::failed;
            }
            data.pSample = caller_sample.get();
        }

        DWORD process_status = 0U;
        const HRESULT hr = transform->ProcessOutput(0U, 1U, &data, &process_status);
        IMFSample* produced_sample = data.pSample;
        if (data.pEvents != nullptr) data.pEvents->Release();
        mf_owned_ptr<IMFSample> produced_owner;
        if (produced_sample != nullptr && produced_sample != caller_sample.get()) {
            produced_owner.reset(produced_sample);
        }

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
            return mf_encoder_drain_status::need_input;
        }
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE || FAILED(hr)) {
            set_error(context, "Software video encoder output failed or changed media type.");
            return mf_encoder_drain_status::failed;
        }
        if ((process_status & MFT_PROCESS_OUTPUT_STATUS_NEW_STREAMS) != 0U ||
            produced_sample == nullptr) {
            set_error(context, "Software video encoder changed streams or returned an empty output sample.");
            return mf_encoder_drain_status::failed;
        }
        if (output.size() >= maximum_samples) {
            set_error(context, "OutputValidation: software video encoder emitted more frames than the Native visible timeline.");
            return mf_encoder_drain_status::failed;
        }

        mf_retained_video_sample retained{};
        if (!snapshot_mf_video_sample(produced_sample, retained)) {
            set_error(context, "OutputValidation: software video encoder emitted a sample without valid timing or payload.");
            return mf_encoder_drain_status::failed;
        }
        if (produced_sample == caller_sample.get()) {
            produced_sample->AddRef();
            retained.sample.reset(produced_sample);
        } else {
            retained.sample = std::move(produced_owner);
        }
        try {
            output.push_back(std::move(retained));
        } catch (...) {
            set_error(context, "Unable to retain software video encoder output samples.");
            return mf_encoder_drain_status::failed;
        }
        made_progress = true;
    }
    set_error(context, "Software video encoder output drain exceeded its bounded iteration limit.");
    return mf_encoder_drain_status::failed;
}

static bool validate_software_video_mft_timing(
    lpb_context* context,
    const isobmff_video_presentation_timeline& source,
    const std::vector<mf_retained_video_sample>& encoded) noexcept
{
    if (encoded.size() != source.visible_sample_count ||
        encoded.size() > static_cast<size_t>((std::numeric_limits<uint32_t>::max)())) {
        set_error(context, "OutputValidation: software video encoder output count differs from Native visible-frame count.");
        return false;
    }
    struct timing_fact {
        int64_t time_100ns{};
        int64_t duration_100ns{};
    };
    try {
        std::vector<timing_fact> sorted;
        sorted.reserve(encoded.size());
        for (const auto& sample : encoded) {
            sorted.push_back(timing_fact{sample.time_100ns, sample.duration_100ns});
        }
        std::sort(sorted.begin(), sorted.end(), [](const auto& left, const auto& right) {
            return left.time_100ns < right.time_100ns;
        });
        isobmff_video_presentation_timeline output{};
        output.movie_timescale = 10000000U;
        output.media_timescale = 10000000U;
        output.sample_count = static_cast<uint32_t>(sorted.size());
        output.visible_sample_count = output.sample_count;
        output.presentation_order.reserve(sorted.size());
        for (size_t index = 0U; index < sorted.size(); ++index) {
            const auto& sample = sorted[index];
            if (sample.time_100ns < 0 || sample.duration_100ns <= 0 ||
                (index != 0U && sorted[index - 1U].time_100ns >= sample.time_100ns)) {
                set_error(context, "OutputValidation: software video encoder emitted invalid or duplicate presentation timestamps.");
                return false;
            }
            output.presentation_order.push_back(isobmff_video_presentation_sample{
                static_cast<uint32_t>(index), sample.time_100ns,
                static_cast<uint64_t>(sample.duration_100ns), true,
                sample.time_100ns, sample.duration_100ns});
        }
        uint32_t failing_frame = 0U;
        if (!presentation_timing_within_source_frame_intervals(source, output, failing_frame)) {
            set_error(context, "OutputValidation: software video encoder timing failed the Native source-frame interval rule.");
            return false;
        }
        return true;
    } catch (...) {
        set_error(context, "Unable to validate software video encoder presentation timing.");
        return false;
    }
}

static lpb_result run_software_video_mft(
    lpb_context* context,
    IMFSourceReader* reader,
    IMFMediaType* reader_video_type,
    UINT32 width,
    UINT32 height,
    UINT32 bitrate,
    UINT32 primaries,
    UINT32 transfer,
    UINT32 matrix,
    UINT32 nominal_range,
    bool source_rate_valid,
    UINT32 fps_num,
    UINT32 fps_den,
    lpb_video_codec target_codec,
    const isobmff_video_presentation_timeline& source_timeline,
    std::vector<mf_retained_video_sample>& encoded,
    mf_owned_ptr<IMFMediaType>& final_video_type)
{
    if (reader == nullptr || reader_video_type == nullptr ||
        source_timeline.sample_count == 0U || source_timeline.visible_sample_count == 0U ||
        source_timeline.presentation_order.size() != source_timeline.sample_count) {
        set_error(context, "Video conversion failed closed: software encoder prerequisites are incomplete.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    GUID target_subtype = target_codec == LPB_VIDEO_CODEC_HEVC ? MFVideoFormat_HEVC : MFVideoFormat_H264;
    MFT_REGISTER_TYPE_INFO input_filter{MFMediaType_Video, MFVideoFormat_NV12};
    MFT_REGISTER_TYPE_INFO output_filter{MFMediaType_Video, target_subtype};
    mf_activation_list activations{};
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_SYNCMFT,
        &input_filter, &output_filter, &activations.values, &activations.count);
    if (FAILED(hr) || activations.values == nullptr || activations.count == 0U) {
        set_error(context, "Unsupported: no synchronous software Media Foundation encoder was enumerated for the requested NV12/output codec; no fallback.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    mf_owned_ptr<IMFTransform> encoder;
    DWORD input_stream = 0U;
    DWORD output_stream = 0U;
    bool input_uses_source_rate = false;
    bool output_uses_source_rate = false;
    MFT_OUTPUT_STREAM_INFO output_info{};
    for (UINT32 index = 0U; index < activations.count && !encoder; ++index) {
        mf_owned_ptr<IMFTransform> candidate;
        hr = activations.values[index]->ActivateObject(IID_PPV_ARGS(candidate.put()));
        if (FAILED(hr) || !candidate) continue;
        mf_owned_ptr<IMFAttributes> attributes;
        hr = candidate->GetAttributes(attributes.put());
        if (FAILED(hr) || !attributes) continue;
        UINT32 asynchronous = FALSE;
        const HRESULT async_hr = attributes->GetUINT32(MF_TRANSFORM_ASYNC, &asynchronous);
        if ((SUCCEEDED(async_hr) && asynchronous != FALSE) ||
            (FAILED(async_hr) && async_hr != MF_E_ATTRIBUTENOTFOUND)) continue;

        DWORD candidate_input_stream = 0U;
        DWORD candidate_output_stream = 0U;
        bool candidate_input_rate = false;
        bool candidate_output_rate = false;
        MFT_OUTPUT_STREAM_INFO candidate_output_info{};
        if (!configure_software_video_mft(candidate.get(), reader_video_type, target_codec,
                width, height, bitrate, primaries, transfer, matrix, nominal_range,
                source_rate_valid, fps_num, fps_den,
                candidate_input_stream, candidate_output_stream,
                candidate_input_rate, candidate_output_rate, candidate_output_info)) continue;
        encoder = std::move(candidate);
        input_stream = candidate_input_stream;
        output_stream = candidate_output_stream;
        input_uses_source_rate = candidate_input_rate;
        output_uses_source_rate = candidate_output_rate;
        output_info = candidate_output_info;
    }
    if (!encoder) {
        set_error(context, "Unsupported: no enumerated synchronous software encoder accepted the negotiated NV12 and requested codec types; no fallback.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    static_cast<void>(input_uses_source_rate);
    static_cast<void>(output_uses_source_rate);

    try {
        encoded.reserve(source_timeline.visible_sample_count);
    } catch (...) {
        set_error(context, "Unable to reserve retained software video encoder samples.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    hr = encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0U);
    if (SUCCEEDED(hr)) hr = encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, input_stream);
    if (FAILED(hr)) {
        set_error(context, "Software video encoder failed to begin streaming.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    size_t timeline_index = 0U;
    uint32_t decoded_samples = 0U;
    uint32_t visible_inputs = 0U;
    bool initial_type_change_seen = false;
    bool reached_end = false;
    while (!reached_end) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            set_error(context, "Video conversion was cancelled while reading software-decoded video.");
            return LPB_RESULT_CANCELLED;
        }
        DWORD stream_index = 0U;
        DWORD flags = 0U;
        LONGLONG reader_time = 0;
        mf_owned_ptr<IMFSample> sample;
        hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0U,
            &stream_index, &flags, &reader_time, sample.put());
        if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR) != 0U) {
            set_error(context, "Media Foundation software video SourceReader failed.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0U) {
            if (decoded_samples != 0U || initial_type_change_seen) {
                set_error(context, "Video SourceReader changed its negotiated NV12 type after decoding began.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
            initial_type_change_seen = true;
            mf_owned_ptr<IMFMediaType> changed_type;
            hr = reader->GetCurrentMediaType(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), changed_type.put());
            GUID major = GUID_NULL;
            GUID subtype = GUID_NULL;
            UINT32 changed_width = 0U;
            UINT32 changed_height = 0U;
            const HRESULT major_hr = changed_type
                ? changed_type->GetGUID(MF_MT_MAJOR_TYPE, &major) : E_POINTER;
            const HRESULT subtype_hr = changed_type
                ? changed_type->GetGUID(MF_MT_SUBTYPE, &subtype) : E_POINTER;
            const HRESULT size_hr = changed_type
                ? MFGetAttributeSize(changed_type.get(), MF_MT_FRAME_SIZE, &changed_width, &changed_height)
                : E_POINTER;
            if (FAILED(hr) || !changed_type ||
                FAILED(major_hr) || FAILED(subtype_hr) || FAILED(size_hr) ||
                major != MFMediaType_Video || subtype != MFVideoFormat_NV12 ||
                changed_width != width || changed_height != height) {
                char diagnostic[256]{};
                std::snprintf(diagnostic, sizeof(diagnostic),
                    "Video SourceReader changed to an unexpected format instead of negotiated NV12 "
                    "(type=0x%08X/0x%08X, size=%u x %u; expected %u x %u; hr=0x%08X/0x%08X/0x%08X/0x%08X).",
                    static_cast<unsigned int>(major.Data1), static_cast<unsigned int>(subtype.Data1),
                    changed_width, changed_height, width, height,
                    static_cast<unsigned int>(hr), static_cast<unsigned int>(major_hr),
                    static_cast<unsigned int>(subtype_hr), static_cast<unsigned int>(size_hr));
                set_error(context, diagnostic);
                return LPB_RESULT_INTERNAL_ERROR;
            }
        }
        if (sample) {
            if (timeline_index >= source_timeline.presentation_order.size() ||
                decoded_samples == (std::numeric_limits<uint32_t>::max)()) {
                set_error(context, "OutputValidation: Media Foundation decoded more video samples than the Native timeline.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
            const auto& timeline_entry = source_timeline.presentation_order[timeline_index++];
            ++decoded_samples;
            if (timeline_entry.visible) {
                if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                    set_error(context, "Video conversion was cancelled while feeding the software encoder.");
                    return LPB_RESULT_CANCELLED;
                }
                hr = sample->SetSampleTime(timeline_entry.normalized_time_100ns);
                if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(timeline_entry.duration_100ns);
                if (FAILED(hr)) {
                    set_error(context, "Media Foundation could not apply Native-validated visible video timing.");
                    return LPB_RESULT_INTERNAL_ERROR;
                }
                bool accepted = false;
                for (size_t attempt = 0U; attempt < 4U; ++attempt) {
                    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                        set_error(context, "Video conversion was cancelled while feeding the software encoder.");
                        return LPB_RESULT_CANCELLED;
                    }
                    hr = encoder->ProcessInput(input_stream, sample.get(), 0U);
                    if (hr == MF_E_NOTACCEPTING) {
                        bool made_progress = false;
                        const auto drain = drain_software_video_mft(context, encoder.get(), output_stream,
                            output_info, source_timeline.visible_sample_count, encoded, made_progress);
                        if (drain == mf_encoder_drain_status::cancelled) return LPB_RESULT_CANCELLED;
                        if (drain == mf_encoder_drain_status::failed || !made_progress) {
                            if (drain != mf_encoder_drain_status::failed) {
                                set_error(context, "Software video encoder refused input without producing drain progress.");
                            }
                            return LPB_RESULT_INTERNAL_ERROR;
                        }
                        continue;
                    }
                    if (FAILED(hr)) {
                        set_error(context, "Software video encoder rejected a Native-timed NV12 input sample.");
                        return LPB_RESULT_INTERNAL_ERROR;
                    }
                    accepted = true;
                    ++visible_inputs;
                    break;
                }
                if (!accepted) {
                    set_error(context, "Software video encoder exceeded its bounded MF_E_NOTACCEPTING retry limit.");
                    return LPB_RESULT_INTERNAL_ERROR;
                }
                bool made_progress = false;
                const auto drain = drain_software_video_mft(context, encoder.get(), output_stream,
                    output_info, source_timeline.visible_sample_count, encoded, made_progress);
                if (drain == mf_encoder_drain_status::cancelled) return LPB_RESULT_CANCELLED;
                if (drain == mf_encoder_drain_status::failed) return LPB_RESULT_INTERNAL_ERROR;
            }
        } else if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) == 0U) {
            set_error(context, "Video SourceReader returned no decoded sample before end-of-stream.");
            return LPB_RESULT_INTERNAL_ERROR;
        } else if ((flags & MF_SOURCE_READERF_STREAMTICK) != 0U) {
            set_error(context, "Video SourceReader reported a stream tick without a decoded sample.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0U) reached_end = true;
    }

    if (decoded_samples != source_timeline.sample_count ||
        timeline_index != source_timeline.presentation_order.size() ||
        visible_inputs != source_timeline.visible_sample_count) {
        set_error(context, "OutputValidation: decoded video sample count differs from the Native timeline.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    hr = encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, input_stream);
    if (SUCCEEDED(hr)) hr = encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0U);
    if (FAILED(hr)) {
        set_error(context, "Software video encoder failed its end-of-stream drain command.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    while (true) {
        bool made_progress = false;
        const auto drain = drain_software_video_mft(context, encoder.get(), output_stream,
            output_info, source_timeline.visible_sample_count, encoded, made_progress);
        if (drain == mf_encoder_drain_status::cancelled) return LPB_RESULT_CANCELLED;
        if (drain == mf_encoder_drain_status::failed) return LPB_RESULT_INTERNAL_ERROR;
        if (drain == mf_encoder_drain_status::need_input) break;
    }
    hr = encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0U);
    if (FAILED(hr)) {
        set_error(context, "Software video encoder failed to end streaming.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    hr = encoder->GetOutputCurrentType(output_stream, final_video_type.put());
    if (FAILED(hr) || !final_video_type) {
        set_error(context, "Software video encoder did not expose its negotiated compressed output media type.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    GUID final_major = GUID_NULL;
    GUID final_subtype = GUID_NULL;
    UINT32 final_width = 0U;
    UINT32 final_height = 0U;
    const GUID expected_subtype =
        target_codec == LPB_VIDEO_CODEC_HEVC ? MFVideoFormat_HEVC : MFVideoFormat_H264;
    if (FAILED(final_video_type->GetGUID(MF_MT_MAJOR_TYPE, &final_major)) ||
        FAILED(final_video_type->GetGUID(MF_MT_SUBTYPE, &final_subtype)) ||
        FAILED(MFGetAttributeSize(final_video_type.get(), MF_MT_FRAME_SIZE, &final_width, &final_height)) ||
        final_major != MFMediaType_Video || final_subtype != expected_subtype ||
        final_width != width || final_height != height) {
        set_error(context, "Software video encoder negotiated an unexpected compressed codec or dimensions.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (!set_mf_video_color_attributes(final_video_type.get(), primaries, transfer, matrix, nominal_range)) {
        set_error(context, "Software video encoder output type could not retain the source SDR color attributes.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return validate_software_video_mft_timing(context, source_timeline, encoded)
        ? LPB_RESULT_OK : LPB_RESULT_INTERNAL_ERROR;
}

static bool mf_pcm_type_matches(
    IMFMediaType* type,
    UINT32 sample_rate,
    UINT32 channels) noexcept
{
    if (type == nullptr || sample_rate == 0U || channels == 0U) return false;
    GUID major = GUID_NULL;
    GUID subtype = GUID_NULL;
    UINT32 bits = 0U;
    UINT32 actual_rate = 0U;
    UINT32 actual_channels = 0U;
    UINT32 block_alignment = 0U;
    UINT32 average_bytes = 0U;
    const UINT32 expected_alignment = channels * 2U;
    return SUCCEEDED(type->GetGUID(MF_MT_MAJOR_TYPE, &major)) &&
        SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &subtype)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bits)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &actual_rate)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &actual_channels)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, &block_alignment)) &&
        SUCCEEDED(type->GetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, &average_bytes)) &&
        major == MFMediaType_Audio && subtype == MFAudioFormat_PCM &&
        bits == 16U && actual_rate == sample_rate && actual_channels == channels &&
        block_alignment == expected_alignment &&
        average_bytes == sample_rate * expected_alignment;
}

static lpb_result read_software_pcm_audio(
    lpb_context* context,
    const fs::path& input_path,
    UINT32& output_rate,
    UINT32& output_channels,
    mf_owned_ptr<IMFMediaType>& verified_pcm_type,
    std::vector<mf_retained_audio_sample>& samples)
{
    mf_owned_ptr<IMFAttributes> reader_attributes;
    HRESULT hr = MFCreateAttributes(reader_attributes.put(), 1U);
    if (SUCCEEDED(hr)) {
        hr = reader_attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    }
    if (FAILED(hr)) {
        set_error(context, "Failed to configure a software-only audio SourceReader.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    mf_owned_ptr<IMFSourceReader> reader;
    hr = MFCreateSourceReaderFromURL(input_path.c_str(), reader_attributes.get(), reader.put());
    if (FAILED(hr) || !reader) {
        set_error(context, "Failed to create a separate software-only audio SourceReader.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    if (SUCCEEDED(hr)) {
        hr = reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), TRUE);
    }
    if (FAILED(hr)) {
        set_error(context, "Failed to select only the first audio stream.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    mf_owned_ptr<IMFMediaType> native_audio_type;
    hr = reader->GetNativeMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0U, native_audio_type.put());
    if (FAILED(hr) || !native_audio_type) {
        set_error(context, "Source declares audio, but Media Foundation cannot read its native audio type.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    UINT32 raw_sample_rate = 48000U;
    UINT32 raw_channels = 2U;
    static_cast<void>(native_audio_type->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &raw_sample_rate));
    static_cast<void>(native_audio_type->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &raw_channels));

    // Preserve the existing normalization policy: mono stays mono, all other
    // layouts become stereo; only a 44.1 kHz source remains 44.1 kHz.
    output_channels = raw_channels == 1U ? 1U : 2U;
    output_rate = raw_sample_rate == 44100U ? 44100U : 48000U;
    const UINT32 block_alignment = output_channels * 2U;
    mf_owned_ptr<IMFMediaType> requested_pcm_type;
    hr = MFCreateMediaType(requested_pcm_type.put());
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16U);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, output_rate);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, output_channels);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, block_alignment);
    if (SUCCEEDED(hr)) hr = requested_pcm_type->SetUINT32(
        MF_MT_AUDIO_AVG_BYTES_PER_SECOND, output_rate * block_alignment);
    if (SUCCEEDED(hr)) {
        hr = reader->SetCurrentMediaType(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), nullptr, requested_pcm_type.get());
    }
    if (FAILED(hr)) {
        set_error(context, "Media Foundation could not negotiate the selected PCM16 audio normalization.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    hr = reader->GetCurrentMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), verified_pcm_type.put());
    if (FAILED(hr) || !mf_pcm_type_matches(verified_pcm_type.get(), output_rate, output_channels)) {
        set_error(context, "Media Foundation negotiated an audio type that failed strict PCM16 validation.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    LONGLONG previous_time = 0;
    bool has_previous_time = false;
    bool reached_end = false;
    while (!reached_end) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            set_error(context, "Video conversion was cancelled while reading source PCM audio.");
            return LPB_RESULT_CANCELLED;
        }
        DWORD stream_index = 0U;
        DWORD flags = 0U;
        LONGLONG reader_time = 0;
        mf_owned_ptr<IMFSample> sample;
        hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM), 0U,
            &stream_index, &flags, &reader_time, sample.put());
        if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR) != 0U ||
            (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0U) {
            set_error(context, "Media Foundation audio SourceReader failed or changed its negotiated PCM type.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if (sample) {
            mf_retained_audio_sample retained{};
            if (!snapshot_mf_audio_sample(sample.get(), block_alignment, retained) ||
                (has_previous_time && retained.time_100ns < previous_time)) {
                set_error(context, "OutputValidation: decoded PCM sample has empty payload, invalid timing, or decreasing source timestamp.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
            previous_time = retained.time_100ns;
            has_previous_time = true;
            retained.sample = std::move(sample);
            try {
                samples.push_back(std::move(retained));
            } catch (...) {
                set_error(context, "Unable to retain decoded PCM audio samples.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
        } else if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) == 0U) {
            set_error(context, "Audio SourceReader returned no PCM sample before end-of-stream.");
            return LPB_RESULT_INTERNAL_ERROR;
        } else if ((flags & MF_SOURCE_READERF_STREAMTICK) != 0U) {
            set_error(context, "Audio SourceReader reported a stream tick without a PCM sample.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0U) reached_end = true;
    }
    if (samples.empty()) {
        set_error(context, "Source declares audio, but the separate PCM SourceReader produced no samples.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static bool prove_sink_writer_has_no_video_transform(
    lpb_context* context,
    IMFSinkWriterEx* writer,
    DWORD stream_index) noexcept
{
    GUID category = GUID_NULL;
    mf_owned_ptr<IMFTransform> transform;
    const HRESULT hr = writer->GetTransformForStream(stream_index, 0U, &category, transform.put());
    if (hr == MF_E_INVALIDINDEX && !transform) return true;
    set_error(context, "OutputValidation: SinkWriter inserted or could not disprove a video transform.");
    return false;
}

static bool prove_sink_writer_has_audio_transform(
    lpb_context* context,
    IMFSinkWriterEx* writer,
    DWORD stream_index) noexcept
{
    size_t transform_count = 0U;
    for (DWORD index = 0U;; ++index) {
        GUID category = GUID_NULL;
        mf_owned_ptr<IMFTransform> transform;
        const HRESULT hr = writer->GetTransformForStream(stream_index, index, &category, transform.put());
        if (hr == MF_E_INVALIDINDEX && !transform) break;
        if (FAILED(hr) || !transform) {
            set_error(context, "OutputValidation: SinkWriter audio transform chain could not be enumerated.");
            return false;
        }
        ++transform_count;
        if (index == (std::numeric_limits<DWORD>::max)()) {
            set_error(context, "OutputValidation: SinkWriter audio transform enumeration did not terminate.");
            return false;
        }
    }
    if (transform_count == 0U) {
        set_error(context, "OutputValidation: SinkWriter did not select an AAC audio transform.");
        return false;
    }
    return true;
}

static lpb_result mux_retained_software_samples(
    lpb_context* context,
    const fs::path& output_path,
    IMFMediaType* video_type,
    bool has_audio,
    IMFMediaType* pcm_type,
    UINT32 audio_rate,
    UINT32 audio_channels,
    const std::vector<mf_retained_video_sample>& video_samples,
    const std::vector<mf_retained_audio_sample>& audio_samples)
{
    if (video_type == nullptr || video_samples.empty() ||
        (has_audio && (pcm_type == nullptr || audio_samples.empty()))) {
        set_error(context, "OutputValidation: retained software video/audio mux inputs are incomplete.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    IMFByteStream* raw_stream = nullptr;
    HRESULT hr = MFCreateFile(MF_ACCESSMODE_WRITE, MF_OPENMODE_FAIL_IF_NOT_EXIST,
        MF_FILEFLAGS_ALLOW_WRITE_SHARING, output_path.c_str(), &raw_stream);
    std::unique_ptr<IMFByteStream, mf_byte_stream_closer> owned_stream(raw_stream);
    if (FAILED(hr) || !owned_stream) {
        set_error(context, "Failed to open the owned MP4 staging stream for compressed sample muxing.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    mf_owned_ptr<IMFAttributes> writer_attributes;
    hr = MFCreateAttributes(writer_attributes.put(), 2U);
    if (SUCCEEDED(hr)) hr = writer_attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    if (SUCCEEDED(hr)) hr = writer_attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
    if (FAILED(hr)) {
        set_error(context, "Failed to configure a software-only Media Foundation SinkWriter.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    mf_owned_ptr<IMFSinkWriter> writer;
    hr = MFCreateSinkWriterFromURL(output_path.c_str(), owned_stream.get(),
        writer_attributes.get(), writer.put());
    if (FAILED(hr) || !writer) {
        set_error(context, "Failed to create a Media Foundation SinkWriter for owned MP4 staging.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    DWORD video_stream = 0U;
    hr = writer->AddStream(video_type, &video_stream);
    if (SUCCEEDED(hr)) hr = writer->SetInputMediaType(video_stream, video_type, nullptr);
    DWORD audio_stream = 0U;
    mf_owned_ptr<IMFMediaType> aac_type;
    if (SUCCEEDED(hr) && has_audio) {
        hr = MFCreateMediaType(aac_type.put());
        if (SUCCEEDED(hr)) hr = aac_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        if (SUCCEEDED(hr)) hr = aac_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        if (SUCCEEDED(hr)) hr = aac_type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16U);
        if (SUCCEEDED(hr)) hr = aac_type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, audio_rate);
        if (SUCCEEDED(hr)) hr = aac_type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, audio_channels);
        if (SUCCEEDED(hr)) hr = aac_type->SetUINT32(
            MF_MT_AUDIO_AVG_BYTES_PER_SECOND, audio_channels == 1U ? 12000U : 16000U);
        if (SUCCEEDED(hr)) hr = writer->AddStream(aac_type.get(), &audio_stream);
        if (SUCCEEDED(hr)) hr = writer->SetInputMediaType(audio_stream, pcm_type, nullptr);
    }
    if (FAILED(hr)) {
        set_error(context, "Media Foundation SinkWriter rejected a compressed video or validated PCM/AAC stream type.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    hr = writer->BeginWriting();
    if (FAILED(hr)) {
        set_error(context, "Media Foundation SinkWriter could not begin compressed sample muxing.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    mf_owned_ptr<IMFSinkWriterEx> writer_ex;
    hr = writer->QueryInterface(IID_PPV_ARGS(writer_ex.put()));
    if (FAILED(hr) || !writer_ex) {
        set_error(context, "Media Foundation SinkWriter transform ownership could not be queried.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (!prove_sink_writer_has_no_video_transform(context, writer_ex.get(), video_stream) ||
        (has_audio && !prove_sink_writer_has_audio_transform(context, writer_ex.get(), audio_stream))) {
        return LPB_RESULT_INTERNAL_ERROR;
    }

    for (size_t index = 0U; index < video_samples.size(); ++index) {
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            set_error(context, "Video conversion was cancelled while muxing retained compressed video samples.");
            return LPB_RESULT_CANCELLED;
        }
        const auto& retained = video_samples[index];
        mf_retained_video_sample current{};
        if (!retained.sample || !snapshot_mf_video_sample(retained.sample.get(), current) ||
            !same_mf_video_sample_facts(retained, current)) {
            set_error(context, "OutputValidation: retained MFT video sample changed before muxing.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        hr = writer->WriteSample(video_stream, retained.sample.get());
        if (FAILED(hr)) {
            set_error(context, "Media Foundation SinkWriter rejected a retained compressed video sample.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
    }

    if (has_audio) {
        const UINT32 block_alignment = audio_channels * 2U;
        LONGLONG previous_audio_time = 0;
        bool has_previous_audio_time = false;
        for (size_t index = 0U; index < audio_samples.size(); ++index) {
            if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
                set_error(context, "Video conversion was cancelled while muxing retained PCM audio samples.");
                return LPB_RESULT_CANCELLED;
            }
            const auto& retained = audio_samples[index];
            mf_retained_audio_sample current{};
            if (!retained.sample ||
                !snapshot_mf_audio_sample(retained.sample.get(), block_alignment, current) ||
                !same_mf_audio_sample_facts(retained, current) ||
                (has_previous_audio_time && current.time_100ns < previous_audio_time)) {
                set_error(context, "OutputValidation: retained PCM sample changed or left source-reader order before muxing.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
            previous_audio_time = current.time_100ns;
            has_previous_audio_time = true;
            hr = writer->WriteSample(audio_stream, retained.sample.get());
            if (FAILED(hr)) {
                set_error(context, "Media Foundation SinkWriter rejected a retained PCM audio sample.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
        }
    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
        set_error(context, "Video conversion was cancelled before SinkWriter finalization.");
        return LPB_RESULT_CANCELLED;
    }
    hr = writer->Finalize();
    if (FAILED(hr)) {
        set_error(context, "Media Foundation SinkWriter failed to finalize the completed compressed-sample mux.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_OK;
}

static bool disable_source_reader_video_frame_rate_conversion(
    lpb_context* context,
    IMFSourceReader* reader) noexcept
{
    if (reader == nullptr) return false;
    mf_owned_ptr<IMFSourceReaderEx> reader_ex;
    HRESULT hr = reader->QueryInterface(IID_PPV_ARGS(reader_ex.put()));
    if (FAILED(hr) || !reader_ex) {
        set_error(context, "Media Foundation SourceReader cannot expose its software video-processing chain.");
        return false;
    }

    for (DWORD index = 0U; index < 32U; ++index) {
        GUID category = GUID_NULL;
        mf_owned_ptr<IMFTransform> transform;
        hr = reader_ex->GetTransformForStream(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), index,
            &category, transform.put());
        if (hr == MF_E_INVALIDINDEX && !transform) return true;
        if (FAILED(hr) || !transform) {
            set_error(context, "Media Foundation SourceReader video-processing chain could not be enumerated.");
            return false;
        }
        if (category == MFT_CATEGORY_VIDEO_PROCESSOR) {
            mf_owned_ptr<IMFAttributes> attributes;
            hr = transform->GetAttributes(attributes.put());
            if (FAILED(hr) || !attributes ||
                FAILED(attributes->SetUINT32(MF_XVP_DISABLE_FRC, TRUE))) {
                set_error(context, "Media Foundation video processor could not disable frame-rate conversion.");
                return false;
            }
        }
    }
    set_error(context, "Media Foundation SourceReader video-processing chain exceeded its bounded transform count.");
    return false;
}

static lpb_result transcode_sdr_video_to_owned_staging(
    lpb_context* context,
    const fs::path& input_path,
    const fs::path& output_path,
    const lpb_video_item_facts& source_facts,
    const isobmff_video_profile& source_profile,
    lpb_video_codec target_codec,
    int32_t crf,
    const isobmff_video_presentation_timeline& source_timeline)
{
    mf_runtime_guard runtime;
    if (!runtime.start(context)) return LPB_RESULT_INTERNAL_ERROR;

    UINT32 primaries = 0U;
    UINT32 transfer = 0U;
    UINT32 matrix = 0U;
    UINT32 nominal_range = 0U;
    if (!map_profile_color_to_media_foundation(
            source_profile, primaries, transfer, matrix, nominal_range)) {
        set_error(context, "Unsupported: Media Foundation cannot represent the source SDR CICP profile; no fallback.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    mf_owned_ptr<IMFAttributes> video_reader_attributes;
    HRESULT hr = MFCreateAttributes(video_reader_attributes.put(), 3U);
    if (SUCCEEDED(hr)) hr = video_reader_attributes->SetUINT32(
        MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE);
    if (SUCCEEDED(hr)) hr = video_reader_attributes->SetUINT32(
        MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, TRUE);
    if (SUCCEEDED(hr)) hr = video_reader_attributes->SetUINT32(
        MF_READWRITE_DISABLE_CONVERTERS, FALSE);
    if (FAILED(hr)) {
        set_error(context, "Failed to configure a software-only video SourceReader.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    mf_owned_ptr<IMFSourceReader> video_reader;
    hr = MFCreateSourceReaderFromURL(input_path.c_str(), video_reader_attributes.get(), video_reader.put());
    if (FAILED(hr) || !video_reader) {
        set_error(context, "Failed to open source video with a software-only Media Foundation reader.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    hr = video_reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE);
    if (SUCCEEDED(hr)) {
        hr = video_reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE);
    }
    if (FAILED(hr)) {
        set_error(context, "Failed to select only the first video stream.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    mf_owned_ptr<IMFMediaType> native_video_type;
    hr = video_reader->GetNativeMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0U, native_video_type.put());
    if (FAILED(hr) || !native_video_type) {
        set_error(context, "Failed to query native video stream media type.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    UINT32 width = source_facts.width > 0U ? source_facts.width : 1920U;
    UINT32 height = source_facts.height > 0U ? source_facts.height : 1080U;
    UINT32 fps_num = 0U;
    UINT32 fps_den = 0U;
    const bool source_rate_valid =
        SUCCEEDED(MFGetAttributeRatio(native_video_type.get(), MF_MT_FRAME_RATE, &fps_num, &fps_den)) &&
        fps_num != 0U && fps_den != 0U;
    UINT32 native_width = 0U;
    UINT32 native_height = 0U;
    if (SUCCEEDED(MFGetAttributeSize(native_video_type.get(), MF_MT_FRAME_SIZE, &native_width, &native_height)) &&
        native_width != 0U && native_height != 0U) {
        width = native_width;
        height = native_height;
    }

    double bitrate_fps = source_rate_valid
        ? static_cast<double>(fps_num) / fps_den : 30.0;
    if (bitrate_fps <= 0.0 || bitrate_fps > 240.0) bitrate_fps = 30.0;
    const double bits_per_pixel = target_codec == LPB_VIDEO_CODEC_HEVC ? 0.08 : 0.12;
    uint32_t average_bitrate = static_cast<uint32_t>(width * height * bitrate_fps * bits_per_pixel);
    if (crf > 0 && crf <= 51) {
        const double crf_factor = std::pow(2.0, (23.0 - static_cast<double>(crf)) / 6.0);
        average_bitrate = static_cast<uint32_t>(average_bitrate * crf_factor);
    }
    if (average_bitrate < 500000U) average_bitrate = 500000U;
    if (average_bitrate > 50000000U) average_bitrate = 50000000U;

    mf_owned_ptr<IMFMediaType> reader_request_type;
    hr = MFCreateMediaType(reader_request_type.put());
    if (SUCCEEDED(hr)) hr = reader_request_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = reader_request_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = MFSetAttributeSize(reader_request_type.get(), MF_MT_FRAME_SIZE, width, height);
    if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(reader_request_type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1U, 1U);
    if (SUCCEEDED(hr)) hr = reader_request_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (SUCCEEDED(hr) && !set_mf_video_color_attributes(
            reader_request_type.get(), primaries, transfer, matrix, nominal_range)) hr = E_INVALIDARG;
    if (SUCCEEDED(hr)) {
        hr = video_reader->SetCurrentMediaType(
            static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, reader_request_type.get());
    }
    if (SUCCEEDED(hr) && !disable_source_reader_video_frame_rate_conversion(context, video_reader.get())) {
        return LPB_RESULT_INTERNAL_ERROR;
    }
    if (FAILED(hr)) {
        set_error(context, "Media Foundation could not negotiate software-decoded NV12 at the source dimensions and CICP.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    mf_owned_ptr<IMFMediaType> reader_video_type;
    hr = video_reader->GetCurrentMediaType(
        static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), reader_video_type.put());
    GUID reader_major = GUID_NULL;
    GUID reader_subtype = GUID_NULL;
    UINT32 reader_width = 0U;
    UINT32 reader_height = 0U;
    if (FAILED(hr) || !reader_video_type ||
        FAILED(reader_video_type->GetGUID(MF_MT_MAJOR_TYPE, &reader_major)) ||
        FAILED(reader_video_type->GetGUID(MF_MT_SUBTYPE, &reader_subtype)) ||
        FAILED(MFGetAttributeSize(reader_video_type.get(), MF_MT_FRAME_SIZE, &reader_width, &reader_height)) ||
        reader_major != MFMediaType_Video || reader_subtype != MFVideoFormat_NV12 ||
        reader_width != width || reader_height != height) {
        set_error(context, "Media Foundation negotiated an unexpected video SourceReader type instead of NV12.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::vector<mf_retained_video_sample> encoded_video;
    mf_owned_ptr<IMFMediaType> final_video_type;
    lpb_result result = run_software_video_mft(context, video_reader.get(), reader_video_type.get(),
        width, height, average_bitrate, primaries, transfer, matrix, nominal_range,
        source_rate_valid, fps_num, fps_den, target_codec, source_timeline,
        encoded_video, final_video_type);
    if (result != LPB_RESULT_OK) return result;
    video_reader.reset();

    const bool has_audio = source_facts.has_audio != 0U;
    UINT32 audio_rate = 0U;
    UINT32 audio_channels = 0U;
    mf_owned_ptr<IMFMediaType> verified_pcm_type;
    std::vector<mf_retained_audio_sample> decoded_audio;
    if (has_audio) {
        result = read_software_pcm_audio(
            context, input_path, audio_rate, audio_channels, verified_pcm_type, decoded_audio);
        if (result != LPB_RESULT_OK) return result;
    }
    return mux_retained_software_samples(context, output_path, final_video_type.get(),
        has_audio, verified_pcm_type.get(), audio_rate, audio_channels, encoded_video, decoded_audio);
}

lpb_result transcode_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    char* out_encoder_used,
    size_t encoder_buf_len,
    lpb_video_backend_diagnostics* out_diagnostics) noexcept
{
    set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN, LPB_VIDEO_HARDWARE_UNKNOWN,
        false, "", "No backend selected.");
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

    const auto p_in = utf8_to_path(input_video_path);
    const auto p_out = utf8_to_path(output_video_path);
    std::ifstream semantic_input(p_in, std::ios::binary | std::ios::ate);
    if (!semantic_input.is_open()) {
        set_error(context, "Cannot reopen video source for strict pre-dispatch profile inspection.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const std::streamoff source_length = semantic_input.tellg();
    if (source_length < 16) {
        set_error(context, "Video source is too short for strict pre-dispatch profile inspection.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    lpb::random_access_reader semantic_reader{
        static_cast<uint64_t>(source_length), &semantic_input, file_read_at};
    isobmff_video_profile semantic_profile{};
    video_profile_failure profile_failure{};
    if (!inspect_isobmff_video_profile(semantic_reader, semantic_profile, profile_failure)) {
        set_error(context, "Video conversion failed closed: source codec, bit depth, CICP, or sample-entry facts are malformed, ambiguous, or unsupported.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const auto make_fourcc = [](char a, char b, char c, char d) noexcept {
        return (static_cast<uint32_t>(static_cast<uint8_t>(a)) << 24) |
            (static_cast<uint32_t>(static_cast<uint8_t>(b)) << 16) |
            (static_cast<uint32_t>(static_cast<uint8_t>(c)) << 8) |
            static_cast<uint32_t>(static_cast<uint8_t>(d));
    };
    lpb_video_codec strict_source_codec = LPB_VIDEO_CODEC_UNKNOWN;
    if (semantic_profile.codec_fourcc == make_fourcc('a', 'v', 'c', '1') ||
        semantic_profile.codec_fourcc == make_fourcc('a', 'v', 'c', '3')) {
        strict_source_codec = LPB_VIDEO_CODEC_H264;
    } else if (semantic_profile.codec_fourcc == make_fourcc('h', 'v', 'c', '1') ||
        semantic_profile.codec_fourcc == make_fourcc('h', 'e', 'v', '1')) {
        strict_source_codec = LPB_VIDEO_CODEC_HEVC;
    }
    if (strict_source_codec == LPB_VIDEO_CODEC_UNKNOWN || strict_source_codec != src_facts.codec) {
        set_error(context, "Video conversion failed closed: strict sample-entry identity disagrees with the Native video probe.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    char source_profile_identity[64]{};
    if (!profile_only_identity(semantic_profile, source_profile_identity, sizeof(source_profile_identity))) {
        set_error(context, "Video conversion failed closed: Native could not encode its bounded strict source-profile diagnostic.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN, LPB_VIDEO_HARDWARE_UNKNOWN,
        false, source_profile_identity, "Native strict source profile parsed; conversion owner not selected.");

    requested_video_codec requested = requested_video_codec::unknown;
    if (target_codec == LPB_VIDEO_CODEC_COPY) requested = requested_video_codec::copy;
    else if (target_codec == LPB_VIDEO_CODEC_H264) requested = requested_video_codec::h264;
    else if (target_codec == LPB_VIDEO_CODEC_HEVC) requested = requested_video_codec::hevc;
    const video_conversion_owner owner = select_video_conversion_owner(semantic_profile, requested);

    // Only an explicit Copy request selects the project-owned stream remux.
    // An explicit codec request always requests a codec transform, even when
    // the selected codec happens to match the source codec.
    if (owner == video_conversion_owner::project_remux) {
        char identity[64]{};
        if (!remux_identity(semantic_profile, semantic_profile, identity, sizeof(identity))) {
            set_error(context, "Video remux failed closed: Native could not encode strict input/output profile diagnostics.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_PROJECT_ISOBMFF_REMUX,
            LPB_VIDEO_HARDWARE_NOT_APPLICABLE, false, identity,
            "Project-owned remux selected; source sample-entry profile is retained.");
        if (out_encoder_used && encoder_buf_len > 0) {
            strncpy_s(out_encoder_used, encoder_buf_len, "ProjectIsoBmffStreamRemux", _TRUNCATE);
        }
        lpb_result remux_result = remux_video_file(context, input_video_path, output_video_path, target_container);
        return remux_result;
    }

    if (owner == video_conversion_owner::minimal_libav_sidecar) {
        char selected_identity[64]{};
        if (!sidecar_identity('s', "?", "?", semantic_profile, nullptr,
                selected_identity, sizeof(selected_identity))) {
            set_error(context, "Video conversion failed closed: Native could not encode selected minimal-libav diagnostics.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN,
            LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, selected_identity,
            "minimal-libav selected from the Native preservation profile; no fallback.");
        if (out_encoder_used && encoder_buf_len > 0U) {
            strncpy_s(out_encoder_used, encoder_buf_len, "minimal-libav/P5-R5-v1", _TRUNCATE);
        }

        std::vector<uint8_t> source_bytes;
        bool has_mebx = false;
        isobmff_mebx_track_facts source_mebx_facts{};
        if (!read_all_bytes(semantic_reader, source_bytes) ||
            !inspect_isobmff_mebx_track(source_bytes, has_mebx, source_mebx_facts)) {
            set_error(context, "Preservation-critical source contains malformed, ambiguous, or unsupported MEBX track facts; no output was created.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        windows_owned_output stage_a;
        windows_owned_output stage_b;
        if (!stage_a.create(p_out, L"lpb-video-sidecar-stage-a", L".tmp", true)) {
            set_error(context, "Cannot create an owned staging object for the preservation-critical sidecar conversion.");
            return LPB_RESULT_INTERNAL_ERROR;
        }
        auto fail_sidecar = [&](lpb_result result, const char* message) noexcept {
            const bool stage_a_cleaned = stage_a.abort_and_confirm();
            const bool stage_b_cleaned = stage_b.abort_and_confirm();
            const bool cleaned = stage_a_cleaned && stage_b_cleaned;
            set_error(context, cleaned ? message :
                "Video sidecar conversion failed and Native could not confirm disposal of every owned staging object.");
            return result;
        };

        const auto bridge = run_video_sidecar_transcode(
            context, semantic_reader, stage_a, target_container, target_codec, crf, has_mebx);
        if (bridge.native_result != LPB_RESULT_OK) {
            const auto& failed_sidecar = bridge.sidecar_result;
            const bool sidecar_missing = std::strncmp(bridge.reason.data(), "BackendUnavailable:",
                sizeof("BackendUnavailable:") - 1U) == 0;
            char failure_identity[64]{};
            if (sidecar_identity('s', sidecar_missing ? "np" :
                    (failed_sidecar.library_version[0] == '\0' ? "?" : failed_sidecar.library_version),
                    failed_sidecar.encoder_name[0] == '\0' ? "na" : failed_sidecar.encoder_name,
                    semantic_profile, nullptr, failure_identity, sizeof(failure_identity))) {
                set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN,
                    LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, failure_identity,
                    "minimal-libav selected from the Native preservation profile; no fallback.");
            }
            return fail_sidecar(bridge.native_result, bridge.reason.data());
        }
        if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
            return fail_sidecar(LPB_RESULT_CANCELLED, "Video sidecar conversion was cancelled before Native post-validation.");
        }

        const auto& sidecar_facts = bridge.sidecar_result;
        char candidate_identity[64]{};
        if (!sidecar_identity('c', sidecar_facts.library_version, sidecar_facts.encoder_name,
                semantic_profile, nullptr, candidate_identity, sizeof(candidate_identity))) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native could not encode bounded minimal-libav candidate diagnostics.");
        }
        set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN,
            LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, candidate_identity,
            "minimal-libav Stage A candidate; no fallback.");

        lpb_video_item_facts stage_a_facts{};
        stage_a_facts.struct_size = sizeof(stage_a_facts);
        const lpb::random_access_reader stage_a_reader = stage_a.reader();
        isobmff_video_profile stage_a_profile{};
        video_profile_failure stage_a_profile_failure{};
        uint64_t stage_a_size = 0U;
        const bool stage_a_ready = stage_a.ready_to_consume() &&
            stage_a.size(stage_a_size) && stage_a_size > 0U;
        const lpb_result stage_a_probe_result = stage_a_ready
            ? probe_video_reader(context, stage_a_reader, &stage_a_facts)
            : LPB_RESULT_INTERNAL_ERROR;
        const bool stage_a_profile_inspected = stage_a_ready &&
            stage_a_probe_result == LPB_RESULT_OK &&
            inspect_isobmff_video_profile(stage_a_reader, stage_a_profile, stage_a_profile_failure);
        if (!stage_a_ready || stage_a_probe_result != LPB_RESULT_OK) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Minimal-libav Stage A failed Native pre-publication container/profile validation.");
        }
        if (!stage_a_profile_inspected) {
            if (target_codec == LPB_VIDEO_CODEC_H264) {
                return fail_sidecar(LPB_RESULT_INVALID_ARGUMENT,
                    "Unsupported: Native cannot verify that minimal-libav H.264 output preserves the required HDR profile; no Media Foundation retry.");
            }
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Minimal-libav Stage A failed Native pre-publication container/profile validation.");
        }

        if (!sidecar_identity('c', sidecar_facts.library_version, sidecar_facts.encoder_name,
                semantic_profile, &stage_a_profile, candidate_identity, sizeof(candidate_identity))) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native could not encode bounded minimal-libav Stage A profile diagnostics.");
        }
        set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN,
            LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, candidate_identity,
            "minimal-libav Stage A profile inspected; no fallback.");

        const uint32_t stage_a_profile_depth = std::min<uint32_t>(
            stage_a_profile.bit_depth_luma, stage_a_profile.bit_depth_chroma);
        const int32_t stage_a_expected_range = stage_a_profile.full_range != 0U ? 2 : 1;
        bool stage_a_has_mebx = false;
        isobmff_mebx_track_facts unexpected_stage_a_mebx{};
        std::vector<uint8_t> stage_a_bytes;
        if (has_mebx && (!read_all_bytes(stage_a_reader, stage_a_bytes) ||
                !inspect_isobmff_mebx_track(stage_a_bytes, stage_a_has_mebx, unexpected_stage_a_mebx))) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Minimal-libav Stage A could not be re-read to verify its deferred MEBX track inventory.");
        }
        const bool stage_a_profile_valid =
            stage_a_facts.container == target_container && stage_a_facts.codec == target_codec &&
            stage_a_facts.width == sidecar_facts.output_width && stage_a_facts.height == sidecar_facts.output_height &&
            stage_a_facts.width == src_facts.width && stage_a_facts.height == src_facts.height &&
            stage_a_facts.rotation_degrees == src_facts.rotation_degrees &&
            stage_a_facts.has_audio == src_facts.has_audio &&
            stage_a_profile.classification == video_profile_class::preservation_critical &&
            stage_a_profile.bit_depth_luma > 8U && stage_a_profile.bit_depth_chroma > 8U &&
            sidecar_facts.output_pixel_depth == stage_a_profile_depth &&
            stage_a_profile.color_primaries == semantic_profile.color_primaries &&
            stage_a_profile.transfer_characteristics == semantic_profile.transfer_characteristics &&
            stage_a_profile.matrix_coefficients == semantic_profile.matrix_coefficients &&
            stage_a_profile.full_range_known != 0U && semantic_profile.full_range_known != 0U &&
            stage_a_profile.full_range == semantic_profile.full_range &&
            sidecar_facts.color_range == stage_a_expected_range &&
            sidecar_facts.color_primaries == stage_a_profile.color_primaries &&
            sidecar_facts.color_transfer == stage_a_profile.transfer_characteristics &&
            sidecar_facts.color_matrix == stage_a_profile.matrix_coefficients &&
            sidecar_facts.output_bytes == stage_a_size && sidecar_facts.video_frames > 0U &&
            (src_facts.has_audio == 0 || sidecar_facts.audio_packets > 0U) &&
            (!has_mebx || !stage_a_has_mebx);
        if (!stage_a_profile_valid) {
            if (target_codec == LPB_VIDEO_CODEC_H264) {
                return fail_sidecar(LPB_RESULT_INVALID_ARGUMENT,
                    "Unsupported: minimal-libav H.264 output did not preserve the required HDR codec, depth, CICP/range, audio, or orientation facts; no Media Foundation retry.");
            }
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Minimal-libav Stage A did not preserve the requested codec, container, depth, CICP/range, audio, or orientation facts.");
        }

        windows_owned_output* publish_stage = &stage_a;
        lpb::random_access_reader final_staged_reader = stage_a_reader;
        isobmff_mebx_track_facts transplanted_facts{};
        if (has_mebx) {
            std::vector<uint8_t> stage_b_bytes;
            if (!transplant_isobmff_mebx_track(source_bytes, stage_a_bytes, stage_b_bytes, transplanted_facts) ||
                transplanted_facts.track_id != source_mebx_facts.track_id ||
                transplanted_facts.time_scale != source_mebx_facts.time_scale ||
                transplanted_facts.media_duration != source_mebx_facts.media_duration ||
                transplanted_facts.sample_count != source_mebx_facts.sample_count ||
                transplanted_facts.chunk_count != source_mebx_facts.chunk_count ||
                transplanted_facts.payload_bytes != source_mebx_facts.payload_bytes) {
                return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                    "Native could not safely transplant and validate the source MEBX track into Stage B.");
            }
            if (!stage_b.create(p_out, L"lpb-video-sidecar-stage-b", L".tmp", true) ||
                !stage_b.write_all(stage_b_bytes) || !stage_b.ready_to_consume()) {
                return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                    "Native could not materialize its MEBX-preserving Stage B output.");
            }
            final_staged_reader = stage_b.reader();
            std::vector<uint8_t> stage_b_readback;
            if (!read_all_bytes(final_staged_reader, stage_b_readback) ||
                !validate_isobmff_mebx_transplant(source_bytes, stage_b_readback, transplanted_facts)) {
                return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                    "Native Stage B readback did not match the exact source MEBX track and payload bytes.");
            }
            publish_stage = &stage_b;
        }

        lpb_video_item_facts output_facts{};
        output_facts.struct_size = sizeof(output_facts);
        isobmff_video_profile output_profile{};
        video_profile_failure output_profile_failure{};
        uint64_t output_size = 0U;
        const bool final_output_valid = publish_stage->ready_to_consume() &&
            publish_stage->size(output_size) && output_size > 0U &&
            probe_video_reader(context, final_staged_reader, &output_facts) == LPB_RESULT_OK &&
            inspect_isobmff_video_profile(final_staged_reader, output_profile, output_profile_failure);
        if (!final_output_valid) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native Stage B output failed structural/profile post-validation before publication.");
        }

        if (!sidecar_identity('c', sidecar_facts.library_version, sidecar_facts.encoder_name,
                semantic_profile, &output_profile, candidate_identity, sizeof(candidate_identity))) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native could not encode bounded final Stage B profile diagnostics.");
        }
        set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN,
            LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, candidate_identity,
            "minimal-libav Native Stage B profile inspected; no fallback.");

        const bool expected_sample_entry = target_codec == LPB_VIDEO_CODEC_HEVC
            ? output_profile.codec_fourcc == make_fourcc('h', 'v', 'c', '1') ||
                output_profile.codec_fourcc == make_fourcc('h', 'e', 'v', '1')
            : output_profile.codec_fourcc == make_fourcc('a', 'v', 'c', '1') ||
                output_profile.codec_fourcc == make_fourcc('a', 'v', 'c', '3');
        const uint32_t output_profile_depth = std::min<uint32_t>(
            output_profile.bit_depth_luma, output_profile.bit_depth_chroma);
        const int32_t expected_range = output_profile.full_range != 0U ? 2 : 1;
        const bool preserved_profile =
            output_facts.container == target_container && output_facts.codec == target_codec &&
            output_facts.width == sidecar_facts.output_width && output_facts.height == sidecar_facts.output_height &&
            output_facts.width == src_facts.width && output_facts.height == src_facts.height &&
            output_facts.rotation_degrees == src_facts.rotation_degrees &&
            output_facts.has_audio == src_facts.has_audio &&
            expected_sample_entry &&
            output_profile.classification == video_profile_class::preservation_critical &&
            output_profile.bit_depth_luma > 8U && output_profile.bit_depth_chroma > 8U &&
            sidecar_facts.output_pixel_depth == output_profile_depth &&
            output_profile.color_primaries == semantic_profile.color_primaries &&
            output_profile.transfer_characteristics == semantic_profile.transfer_characteristics &&
            output_profile.matrix_coefficients == semantic_profile.matrix_coefficients &&
            output_profile.full_range_known != 0U && semantic_profile.full_range_known != 0U &&
            output_profile.full_range == semantic_profile.full_range &&
            sidecar_facts.color_range == expected_range &&
            sidecar_facts.color_primaries == output_profile.color_primaries &&
            sidecar_facts.color_transfer == output_profile.transfer_characteristics &&
            sidecar_facts.color_matrix == output_profile.matrix_coefficients &&
            sidecar_facts.output_bytes == stage_a_size && sidecar_facts.video_frames > 0U &&
            (src_facts.has_audio == 0 || sidecar_facts.audio_packets > 0U);
        if (!preserved_profile) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Minimal-libav output did not preserve the requested codec, container, depth, CICP/range, audio, or orientation facts.");
        }

        char validated_identity[64]{};
        if (!sidecar_identity('v', sidecar_facts.library_version, sidecar_facts.encoder_name,
                semantic_profile, &output_profile, validated_identity, sizeof(validated_identity))) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native could not encode bounded validated minimal-libav diagnostics.");
        }
        char backend_encoder[64]{};
        const int encoder_written = std::snprintf(backend_encoder, sizeof(backend_encoder),
            "minimal-libav/P5-R5-v1/%s", sidecar_facts.encoder_name);
        if (encoder_written < 0 || static_cast<size_t>(encoder_written) >= sizeof(backend_encoder)) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native could not encode the minimal-libav encoder identity.");
        }

        if (has_mebx && !stage_a.abort_and_confirm()) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Native could not confirm disposal of Stage A before Stage B publication.");
        }
        if (!publish_stage->publish_no_replace(p_out)) {
            return fail_sidecar(LPB_RESULT_INTERNAL_ERROR,
                "Validated minimal-libav output could not be published without replacement.");
        }

        if (out_encoder_used && encoder_buf_len > 0U) {
            strncpy_s(out_encoder_used, encoder_buf_len, backend_encoder, _TRUNCATE);
        }
        std::array<char, 256> success_reason{};
        if (has_mebx) {
            static_cast<void>(_snprintf_s(success_reason.data(), success_reason.size(), _TRUNCATE,
                "Native MEBX preserved %u samples/%llu bytes; FFmpeg %s; no fallback.",
                transplanted_facts.sample_count,
                static_cast<unsigned long long>(transplanted_facts.payload_bytes), sidecar_facts.library_version));
        }
        set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_UNKNOWN,
            LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, validated_identity,
            has_mebx ? success_reason.data() : bridge.reason.data());
        return LPB_RESULT_OK;
    }
    if (owner != video_conversion_owner::media_foundation_software) {
        set_error(context, "Unsupported: Video conversion request or source profile is unsupported; no output was created.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    isobmff_video_presentation_timeline source_timeline{};
    {
        std::vector<uint8_t> source_timeline_bytes;
        if (!read_all_bytes(semantic_reader, source_timeline_bytes) ||
            !inspect_isobmff_video_presentation_timeline(source_timeline_bytes, source_timeline) ||
            source_timeline.sample_count == 0U ||
            source_timeline.presentation_order.size() != source_timeline.sample_count) {
            set_error(context, "Video conversion failed closed: Native could not validate one complete, unambiguous source presentation timeline; no output was created.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
    }

    char selected_mf_identity[64]{};
    if (!media_foundation_identity(semantic_profile, nullptr, target_codec,
            selected_mf_identity, sizeof(selected_mf_identity))) {
        set_error(context, "Video conversion failed closed: Native could not encode selected Media Foundation diagnostics.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_WINDOWS_MEDIA_FOUNDATION,
        LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, selected_mf_identity,
        "Software-only Media Foundation selected by Native; no fallback.");

    // Always transcode into a sibling staging file. The helper releases every
    // SourceReader, MFT, SinkWriter and byte stream before the caller validates,
    // removes or publishes the owned output.
    const bool needs_mov_remux = (target_container == LPB_VIDEO_CONTAINER_MOV);
    windows_owned_output output;
    if (!output.create(p_out, L"lpb-transcode", L".mp4")) {
        set_error(context, "Failed to create an owned transcoding staging file.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    const fs::path temp_mp4_path = output.path();

    const lpb_result transcode_res = transcode_sdr_video_to_owned_staging(
        context, p_in, temp_mp4_path, src_facts, semantic_profile,
        target_codec, crf, source_timeline);
    if (transcode_res != LPB_RESULT_OK) {
        const bool cleanup_confirmed = output.abort_and_confirm();
        if (!cleanup_confirmed) {
            set_error(context, "CleanupFailure: Media Foundation conversion failed and Native could not confirm staging cleanup.");
        }
        return transcode_res;
    }
    // Strictly re-read the software-forced output profile while it remains in
    // Native-owned staging. Do not publish an output whose actual sample
    // entry/profile disagrees with the selected SDR transcode owner.
    lpb_video_item_facts transcoded_facts{};
    transcoded_facts.struct_size = sizeof(lpb_video_item_facts);
    isobmff_video_profile transcoded_profile{};
    video_profile_failure transcoded_profile_failure{};
    const lpb::random_access_reader transcoded_reader = output.reader();
    video_profile_diagnostic transcoded_profile_diagnostic{};
    const lpb_result transcoded_probe_result = output.ready_to_consume()
        ? probe_video_reader(context, transcoded_reader, &transcoded_facts)
        : LPB_RESULT_INTERNAL_ERROR;
    const bool transcoded_profile_valid = transcoded_probe_result == LPB_RESULT_OK &&
        inspect_isobmff_video_profile(transcoded_reader, transcoded_profile, transcoded_profile_failure,
            &transcoded_profile_diagnostic);
    const auto report_output_validation_failure = [&](const char* detail) noexcept {
        const bool cleanup_confirmed = output.abort_and_confirm();
        set_error(context, cleanup_confirmed ? detail
            : "CleanupFailure: Media Foundation output validation failed and Native could not confirm staging cleanup.");
        return LPB_RESULT_INTERNAL_ERROR;
    };
    if (!transcoded_profile_valid ||
        transcoded_facts.codec != target_codec ||
        transcoded_profile.classification != video_profile_class::ordinary_sdr ||
        transcoded_profile.bit_depth_luma != 8U || transcoded_profile.bit_depth_chroma != 8U) {
        char validation_error[320]{};
        static_cast<void>(std::snprintf(validation_error, sizeof(validation_error),
            "OutputValidation: Media Foundation output profile failed strict Native validation (probe=%d, profile=%u/%u@%u, codec=%u/%u, class=%u, depth=%u/%u, CICP=%u/%u/%u, rangeKnown=%u).",
            static_cast<int>(transcoded_probe_result),
            transcoded_profile_valid ? 1U : 0U,
            static_cast<unsigned>(transcoded_profile_failure),
            static_cast<unsigned>(transcoded_profile_diagnostic),
            static_cast<unsigned>(transcoded_facts.codec), static_cast<unsigned>(target_codec),
            static_cast<unsigned>(transcoded_profile.classification),
            static_cast<unsigned>(transcoded_profile.bit_depth_luma),
            static_cast<unsigned>(transcoded_profile.bit_depth_chroma),
            static_cast<unsigned>(transcoded_profile.color_primaries),
            static_cast<unsigned>(transcoded_profile.transfer_characteristics),
            static_cast<unsigned>(transcoded_profile.matrix_coefficients),
            static_cast<unsigned>(transcoded_profile.full_range_known)));
        return report_output_validation_failure(validation_error);
    }

    isobmff_video_presentation_timeline transcoded_timeline{};
    std::vector<uint8_t> transcoded_timeline_bytes;
    uint32_t timing_failure_frame = 0U;
    const bool transcoded_timing_valid = output.ready_to_consume() &&
        read_all_bytes(transcoded_reader, transcoded_timeline_bytes) &&
        inspect_isobmff_video_presentation_timeline(transcoded_timeline_bytes, transcoded_timeline) &&
        presentation_timing_within_source_frame_intervals(
            source_timeline, transcoded_timeline, timing_failure_frame);
    if (!transcoded_timing_valid) {
        char validation_error[288]{};
        static_cast<void>(std::snprintf(validation_error, sizeof(validation_error),
            "OutputValidation: Media Foundation output presentation timing failed the frozen source-frame count/interval rule (sourceVisible=%u, outputVisible=%u, frame=%u).",
            source_timeline.visible_sample_count, transcoded_timeline.visible_sample_count,
            timing_failure_frame));
        return report_output_validation_failure(validation_error);
    }

    media_foundation_cicp_patch cicp_patch{};
    video_profile_failure cicp_patch_failure{};
    video_profile_diagnostic cicp_patch_diagnostic{};
    if (!prepare_media_foundation_cicp_patch(transcoded_reader, semantic_profile,
            cicp_patch, cicp_patch_failure, &cicp_patch_diagnostic)) {
        char validation_error[384]{};
        static_cast<void>(std::snprintf(validation_error, sizeof(validation_error),
            "OutputValidation: Media Foundation output CICP/range differs from the source outside the frozen enum-collapse rule (failure=%u, diagnostic=%u, CICP=%u/%u/%u expected=%u/%u/%u, range=%u/%u expected=%u/%u, SPS=%u/%u/%u/%u).",
            static_cast<unsigned>(cicp_patch_failure),
            static_cast<unsigned>(cicp_patch_diagnostic),
            static_cast<unsigned>(transcoded_profile.color_primaries),
            static_cast<unsigned>(transcoded_profile.transfer_characteristics),
            static_cast<unsigned>(transcoded_profile.matrix_coefficients),
            static_cast<unsigned>(semantic_profile.color_primaries),
            static_cast<unsigned>(semantic_profile.transfer_characteristics),
            static_cast<unsigned>(semantic_profile.matrix_coefficients),
            static_cast<unsigned>(transcoded_profile.full_range_known),
            static_cast<unsigned>(transcoded_profile.full_range),
            static_cast<unsigned>(semantic_profile.full_range_known),
            static_cast<unsigned>(semantic_profile.full_range),
            static_cast<unsigned>(transcoded_profile.bitstream_color_description_present),
            static_cast<unsigned>(transcoded_profile.bitstream_color_primaries),
            static_cast<unsigned>(transcoded_profile.bitstream_transfer_characteristics),
            static_cast<unsigned>(transcoded_profile.bitstream_matrix_coefficients)));
        return report_output_validation_failure(validation_error);
    }
    if (cicp_patch.changed &&
        (!output.ready_to_consume() || !output.write_at(cicp_patch.moov_offset, cicp_patch.moov_bytes) ||
            !output.flush())) {
        return report_output_validation_failure(
            "OutputValidation: Native could not apply the size-preserving CICP correction to MF staging.");
    }

    lpb_video_item_facts final_transcoded_facts{};
    final_transcoded_facts.struct_size = sizeof(lpb_video_item_facts);
    isobmff_video_profile final_transcoded_profile{};
    video_profile_failure final_profile_failure{};
    video_profile_diagnostic final_profile_diagnostic{};
    const lpb::random_access_reader final_transcoded_reader = output.ready_to_consume()
        ? output.reader() : lpb::random_access_reader{};
    const lpb_result final_probe_result = final_transcoded_reader.read_at != nullptr
        ? probe_video_reader(context, final_transcoded_reader, &final_transcoded_facts)
        : LPB_RESULT_INTERNAL_ERROR;
    const bool final_profile_valid = final_probe_result == LPB_RESULT_OK &&
        inspect_isobmff_video_profile(final_transcoded_reader, final_transcoded_profile,
            final_profile_failure, &final_profile_diagnostic);
    if (!final_profile_valid || final_transcoded_facts.codec != target_codec ||
        final_transcoded_profile.classification != video_profile_class::ordinary_sdr ||
        final_transcoded_profile.bit_depth_luma != 8U || final_transcoded_profile.bit_depth_chroma != 8U ||
        final_transcoded_profile.color_primaries != semantic_profile.color_primaries ||
        final_transcoded_profile.transfer_characteristics != semantic_profile.transfer_characteristics ||
        final_transcoded_profile.matrix_coefficients != semantic_profile.matrix_coefficients ||
        final_transcoded_profile.full_range_known != semantic_profile.full_range_known ||
        final_transcoded_profile.full_range != semantic_profile.full_range) {
        char validation_error[320]{};
        static_cast<void>(std::snprintf(validation_error, sizeof(validation_error),
            "OutputValidation: Normalized MF output failed exact Native profile validation (probe=%d, profile=%u/%u@%u, codec=%u/%u, class=%u, depth=%u/%u, CICP=%u/%u/%u expected=%u/%u/%u, range=%u/%u expected=%u/%u).",
            static_cast<int>(final_probe_result),
            final_profile_valid ? 1U : 0U,
            static_cast<unsigned>(final_profile_failure),
            static_cast<unsigned>(final_profile_diagnostic),
            static_cast<unsigned>(final_transcoded_facts.codec), static_cast<unsigned>(target_codec),
            static_cast<unsigned>(final_transcoded_profile.classification),
            static_cast<unsigned>(final_transcoded_profile.bit_depth_luma),
            static_cast<unsigned>(final_transcoded_profile.bit_depth_chroma),
            static_cast<unsigned>(final_transcoded_profile.color_primaries),
            static_cast<unsigned>(final_transcoded_profile.transfer_characteristics),
            static_cast<unsigned>(final_transcoded_profile.matrix_coefficients),
            static_cast<unsigned>(semantic_profile.color_primaries),
            static_cast<unsigned>(semantic_profile.transfer_characteristics),
            static_cast<unsigned>(semantic_profile.matrix_coefficients),
            static_cast<unsigned>(final_transcoded_profile.full_range_known),
            static_cast<unsigned>(final_transcoded_profile.full_range),
            static_cast<unsigned>(semantic_profile.full_range_known),
            static_cast<unsigned>(semantic_profile.full_range)));
        return report_output_validation_failure(validation_error);
    }

    char validated_mf_identity[64]{};
    if (!media_foundation_identity(semantic_profile, &final_transcoded_profile, target_codec,
            validated_mf_identity, sizeof(validated_mf_identity))) {
        const bool cleanup_confirmed = output.abort_and_confirm();
        set_error(context, cleanup_confirmed
            ? "OutputValidation: Native could not encode bounded Media Foundation profile diagnostics."
            : "CleanupFailure: Native could not encode Media Foundation profile diagnostics or confirm staging cleanup.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    set_backend_diagnostics(out_diagnostics, LPB_VIDEO_BACKEND_WINDOWS_MEDIA_FOUNDATION,
        LPB_VIDEO_HARDWARE_SOFTWARE_FORCED, false, validated_mf_identity,
        "Hardware transforms are disabled by frozen P4 policy; software forced; no fallback.");

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
        lpb_video_item_facts output_mp4_facts{};
        output_mp4_facts.struct_size = sizeof(lpb_video_item_facts);
        if (!output.ready_to_consume() ||
            probe_video_reader(context, output.reader(), &output_mp4_facts) != LPB_RESULT_OK) {
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

    const char* enc_name = (target_codec == LPB_VIDEO_CODEC_HEVC)
        ? "WindowsMediaFoundationSoftwareHEVC"
        : "WindowsMediaFoundationSoftwareH264";
    if (out_encoder_used && encoder_buf_len > 0) {
        strncpy_s(out_encoder_used, encoder_buf_len, enc_name, _TRUNCATE);
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
