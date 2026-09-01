#include "media/media_inspector.h"
#include "media/video_converter.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include <fstream>
#include <filesystem>
#include <string_view>
#include <vector>
#include <cstdlib>

namespace fs = std::filesystem;

namespace lpb::media {

static std::vector<uint8_t> read_file_bytes(const char* path, size_t max_bytes = 0) {
    if (!path) return {};
    auto p = utf8_to_path(path);
    std::ifstream file(p, std::ios::binary | std::ios::ate);
    if (!file.is_open()) return {};

    std::streamsize file_size = file.tellg();
    if (file_size <= 0) return {};

    size_t to_read = (max_bytes > 0 && max_bytes < static_cast<size_t>(file_size)) 
        ? max_bytes 
        : static_cast<size_t>(file_size);

    file.seekg(0, std::ios::beg);
    std::vector<uint8_t> buffer(to_read);
    file.read(reinterpret_cast<char*>(buffer.data()), to_read);
    return buffer;
}

static uint64_t get_file_size(const char* path) {
    if (!path) return 0;
    auto p = utf8_to_path(path);
    std::error_code ec;
    auto size = fs::file_size(p, ec);
    return ec ? 0 : size;
}

lpb_image_container detect_image_container(std::span<const uint8_t> header) noexcept {
    if (header.size() >= 2 && header[0] == 0xFF && header[1] == 0xD8) {
        return LPB_IMAGE_CONTAINER_JPEG;
    }
    if (header.size() >= 8 && header[0] == 0x89 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G') {
        return LPB_IMAGE_CONTAINER_PNG;
    }
    if (header.size() >= 12 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p') {
        std::string_view brand(reinterpret_cast<const char*>(header.data() + 8), 4);
        if (brand == "heic" || brand == "heix" || brand == "heim" || brand == "heis" ||
            brand == "mif1" || brand == "msf1" || brand == "mp42" || brand == "isom") {
            return LPB_IMAGE_CONTAINER_HEIC;
        }
    }
    return LPB_IMAGE_CONTAINER_UNKNOWN;
}

lpb_video_container detect_video_container(std::span<const uint8_t> header) noexcept {
    if (header.size() >= 12 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p') {
        std::string_view brand(reinterpret_cast<const char*>(header.data() + 8), 4);
        if (brand == "qt  ") return LPB_VIDEO_CONTAINER_MOV;
        return LPB_VIDEO_CONTAINER_MP4;
    }
    if (header.size() >= 8 && header[4] == 'm' && header[5] == 'o' && header[6] == 'o' && header[7] == 'v') {
        return LPB_VIDEO_CONTAINER_MOV;
    }
    return LPB_VIDEO_CONTAINER_UNKNOWN;
}

static std::string extract_xmp_string(const std::vector<uint8_t>& data) {
    const std::string start_tag = "<x:xmpmeta";
    const std::string end_tag = "</x:xmpmeta>";

    std::string_view sv(reinterpret_cast<const char*>(data.data()), data.size());
    auto start_pos = sv.find(start_tag);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(end_tag, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + end_tag.length() - start_pos));
        }
    }

    const std::string rdf_start = "<rdf:RDF";
    const std::string rdf_end = "</rdf:RDF>";
    start_pos = sv.find(rdf_start);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(rdf_end, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + rdf_end.length() - start_pos));
        }
    }

    return {};
}

static bool extract_attr_u64(std::string_view xml, std::string_view attr_name, uint64_t& out_val) {
    auto pos = xml.find(attr_name);
    if (pos == std::string_view::npos) return false;
    pos = xml.find('"', pos + attr_name.length());
    if (pos == std::string_view::npos) return false;
    auto end_quote = xml.find('"', pos + 1);
    if (end_quote == std::string_view::npos) return false;
    std::string val_str(xml.substr(pos + 1, end_quote - pos - 1));
    char* endptr = nullptr;
    out_val = std::strtoull(val_str.c_str(), &endptr, 10);
    return endptr != val_str.c_str();
}

static bool check_huawei_moving_photo(
    const std::vector<uint8_t>& data,
    uint64_t file_size,
    uint64_t& out_video_offset,
    uint64_t& out_video_len,
    int64_t& out_cover_time_us,
    bool& is_honor)
{
    if (data.size() < 60) return false;

    size_t scan_start = data.size() > 4096 ? data.size() - 4096 : 0;
    std::string_view tail(reinterpret_cast<const char*>(data.data() + scan_start), data.size() - scan_start);

    auto live_pos = tail.rfind("LIVE_");
    if (live_pos == std::string_view::npos) return false;

    size_t actual_live_pos = scan_start + live_pos;

    // Parse LIVE_NNNNNNN
    std::string_view num_part = tail.substr(live_pos + 5);
    char* endptr = nullptr;
    uint64_t mp4_plus_20 = std::strtoull(std::string(num_part.substr(0, 15)).c_str(), &endptr, 10);
    if (mp4_plus_20 > 20 && mp4_plus_20 <= file_size) {
        out_video_len = mp4_plus_20 - 20;

        size_t trailer_start = (actual_live_pos >= 40) ? (actual_live_pos - 40) : 0;
        out_video_offset = trailer_start - out_video_len;

        // Check if Honor (uses v2_f prefix or contains srcDstWh)
        if (tail.find("v2_f") != std::string_view::npos || 
            tail.find("srcDstWh") != std::string_view::npos ||
            tail.find("v1_f") != std::string_view::npos) {
            is_honor = true;
        } else {
            is_honor = false;
        }

        if (trailer_start + 40 <= data.size()) {
            std::string_view time_part(reinterpret_cast<const char*>(data.data() + trailer_start + 20), 20);
            auto colon = time_part.find(':');
            if (colon != std::string_view::npos) {
                char* endp = nullptr;
                uint64_t cover_ms = std::strtoull(std::string(time_part.substr(0, colon)).c_str(), &endp, 10);
                if (endp != time_part.data()) {
                    out_cover_time_us = cover_ms * 1000;
                }
            }
        }
        return true;
    }

    return false;
}

static bool check_samsung_sef_jpeg(
    const std::vector<uint8_t>& data,
    uint64_t file_size,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    (void)file_size;
    if (data.size() < 32) return false;

    size_t len = data.size();
    if (data[len - 4] != 'S' || data[len - 3] != 'E' || data[len - 2] != 'F' || data[len - 1] != 'T') {
        return false;
    }

    std::string_view tail(reinterpret_cast<const char*>(data.data() + (len > 2048 ? len - 2048 : 0)), len > 2048 ? 2048 : len);
    auto sefh_pos = tail.rfind("SEFH");
    if (sefh_pos == std::string_view::npos) return false;

    size_t actual_sefh = (len > 2048 ? len - 2048 : 0) + sefh_pos;
    if (actual_sefh + 12 > len) return false;

    binary_reader reader(data.data() + actual_sefh, len - actual_sefh);
    reader.skip(4); // Skip SEFH

    uint32_t version = 0, count = 0;
    if (!reader.try_read_u32_endian(version, false) || !reader.try_read_u32_endian(count, false)) return false;

    for (uint32_t i = 0; i < count; i++) {
        uint16_t prefix = 0, marker = 0;
        uint32_t offset = 0, size = 0;
        if (!reader.try_read_u16_endian(prefix, false)) break;
        if (!reader.try_read_u16_endian(marker, false)) break;
        if (!reader.try_read_u32_endian(offset, false)) break;
        if (!reader.try_read_u32_endian(size, false)) break;

        if (marker == 0x0A30 && size >= 24 && offset <= actual_sefh) {
            out_video_offset = actual_sefh - offset + 24;
            out_video_len = size - 24;
            return true;
        }
    }

    return false;
}

static bool check_samsung_sef_heic(
    const std::vector<uint8_t>& data,
    uint64_t file_size,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    (void)file_size;
    size_t pos = 0;
    while (pos + 8 <= data.size()) {
        uint32_t box_size = (static_cast<uint32_t>(data[pos]) << 24) |
                            (static_cast<uint32_t>(data[pos + 1]) << 16) |
                            (static_cast<uint32_t>(data[pos + 2]) << 8) |
                            (static_cast<uint32_t>(data[pos + 3]));

        if (box_size < 8) break;

        std::string_view type(reinterpret_cast<const char*>(data.data() + pos + 4), 4);
        if (type == "mpvd") {
            out_video_offset = pos + 8;
            out_video_len = box_size - 8;
            return true;
        }

        pos += box_size;
    }
    return false;
}

static bool check_vivo_x300(
    const std::string& xmp,
    uint64_t file_size,
    uint64_t& out_primary_len,
    uint64_t& out_gm_offset,
    uint64_t& out_gm_len,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    if (xmp.find("VCamera") != std::string::npos) {
        // Collect all non-zero Item:Length values
        size_t pos = 0;
        std::vector<uint64_t> lengths;
        while (pos < xmp.length()) {
            auto item_pos = xmp.find("Item:Length=", pos);
            if (item_pos == std::string::npos) break;
            uint64_t len = 0;
            if (extract_attr_u64(std::string_view(xmp).substr(item_pos), "Item:Length=", len) && len > 0) {
                lengths.push_back(len);
            }
            pos = item_pos + 12;
        }

        if (lengths.size() >= 2) {
            uint64_t vid_len = lengths.back();
            uint64_t gm_len = lengths[lengths.size() - 2];
            if (vid_len > 0 && gm_len > 0 && vid_len + gm_len < file_size) {
                out_video_len = vid_len;
                out_video_offset = file_size - vid_len;
                out_gm_len = gm_len;
                out_gm_offset = out_video_offset - gm_len;
                out_primary_len = out_gm_offset;
                return true;
            }
        }
    }
    return false;
}

lpb_result inspect_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts) noexcept
{
    if (!out_facts || !primary_path) {
        set_error(context, "Invalid arguments for source inspection.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::memset(out_facts, 0, sizeof(lpb_source_media_facts));
    out_facts->struct_size = sizeof(lpb_source_media_facts);
    out_facts->primary_image.struct_size = sizeof(lpb_image_item_facts);
    out_facts->motion_video.struct_size = sizeof(lpb_video_item_facts);
    out_facts->gain_map.struct_size = sizeof(lpb_gainmap_item_facts);
    out_facts->timing.struct_size = sizeof(lpb_timing_facts);

    uint64_t primary_size = get_file_size(primary_path);
    if (primary_size == 0) {
        set_error(context, "Primary file does not exist or is empty.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto primary_data = read_file_bytes(primary_path);
    if (primary_data.empty()) {
        set_error(context, "Failed to read primary file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    lpb_image_container img_cont = detect_image_container(primary_data);
    lpb_video_container vid_cont = detect_video_container(primary_data);

    out_facts->primary_image.container = img_cont;
    out_facts->primary_image.is_present = (img_cont != LPB_IMAGE_CONTAINER_UNKNOWN) ? 1 : 0;
    out_facts->primary_image.file_range.offset = 0;
    out_facts->primary_image.file_range.length = primary_size;

    // Dual file check
    if (secondary_path && std::strlen(secondary_path) > 0) {
        uint64_t secondary_size = get_file_size(secondary_path);
        auto sec_data = read_file_bytes(secondary_path, 4096);
        lpb_video_container sec_vid_cont = detect_video_container(sec_data);

        if (sec_vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN && secondary_size > 0) {
            std::string_view pri_sv(reinterpret_cast<const char*>(primary_data.data()), primary_data.size());
            if (pri_sv.find("vivo") != std::string_view::npos && pri_sv.find("cameralbum!") != std::string_view::npos) {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL;
            } else {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
            }

            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = sec_vid_cont;
            out_facts->motion_video.file_range.offset = 0;
            out_facts->motion_video.file_range.length = secondary_size;

            probe_video_file(context, secondary_path, &out_facts->motion_video);
            return LPB_RESULT_OK;
        }
    }

    // Single file checks
    std::string xmp = extract_xmp_string(primary_data);

    // 1. Check vivo X300+ 3-item container
    uint64_t pri_len = 0, gm_off = 0, gm_len = 0, vid_off = 0, vid_len = 0;
    if (check_vivo_x300(xmp, primary_size, pri_len, gm_off, gm_len, vid_off, vid_len)) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_X300;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = pri_len;

        out_facts->gain_map.is_present = 1;
        out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
        out_facts->gain_map.file_range.offset = gm_off;
        out_facts->gain_map.file_range.length = gm_len;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        return LPB_RESULT_OK;
    }

    // 2. Check Samsung JPEG (SEF Trailer)
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && check_samsung_sef_jpeg(primary_data, primary_size, vid_off, vid_len)) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = vid_off;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        return LPB_RESULT_OK;
    }

    // 3. Check Samsung HEIC (mpvd box)
    if (img_cont == LPB_IMAGE_CONTAINER_HEIC && check_samsung_sef_heic(primary_data, primary_size, vid_off, vid_len)) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = vid_off - 8;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        return LPB_RESULT_OK;
    }

    // 4. Check Huawei / Honor Moving Photo
    int64_t cover_time_us = 0;
    bool is_honor = false;
    if (check_huawei_moving_photo(primary_data, primary_size, vid_off, vid_len, cover_time_us, is_honor)) {
        out_facts->protocol = is_honor ? LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO : LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = vid_off;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        out_facts->timing.cover_timestamp_us = cover_time_us;
        return LPB_RESULT_OK;
    }

    // 5. Check OPPO / OnePlus O-Live (XMP OpCamera:VideoLength)
    uint64_t op_vid_len = 0;
    if (!xmp.empty() && extract_attr_u64(xmp, "OpCamera:VideoLength=", op_vid_len) && op_vid_len > 0) {
        uint64_t item_len = op_vid_len;
        extract_attr_u64(xmp, "Item:Length=", item_len);

        out_facts->protocol = LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = primary_size - item_len;
        out_facts->motion_video.file_range.length = op_vid_len;

        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = out_facts->motion_video.file_range.offset;
        return LPB_RESULT_OK;
    }

    // 6. Check Google Motion Photo V2 / Xiaomi (Container:Directory)
    if (!xmp.empty() && (xmp.find("MotionPhoto") != std::string::npos || xmp.find("Container:Directory") != std::string::npos)) {
        auto vid_item_pos = xmp.find("video/mp4");
        if (vid_item_pos == std::string::npos) {
            vid_item_pos = xmp.find("MotionPhoto");
        }

        if (vid_item_pos != std::string::npos) {
            auto item_start = xmp.rfind("<Container:Item", vid_item_pos);
            if (item_start == std::string::npos) item_start = xmp.rfind("<rdf:li", vid_item_pos);
            if (item_start == std::string::npos) item_start = vid_item_pos > 100 ? vid_item_pos - 100 : 0;

            auto item_end = xmp.find("/>", vid_item_pos);
            if (item_end == std::string::npos) item_end = xmp.find("</rdf:li>", vid_item_pos);
            if (item_end == std::string::npos) item_end = vid_item_pos + 200;

            std::string_view item_xml(xmp.data() + item_start, std::min(item_end - item_start, xmp.length() - item_start));
            uint64_t mp_vid_len = 0;
            if (extract_attr_u64(item_xml, "Item:Length=", mp_vid_len) && mp_vid_len > 0 && mp_vid_len < primary_size) {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
                out_facts->motion_video.file_range.offset = primary_size - mp_vid_len;
                out_facts->motion_video.file_range.length = mp_vid_len;

                // Check GainMap
                auto gm_pos = xmp.find("GainMap");
                if (gm_pos != std::string::npos) {
                    auto gm_item_start = xmp.rfind("<Container:Item", gm_pos);
                    if (gm_item_start == std::string::npos) gm_item_start = xmp.rfind("<rdf:li", gm_pos);
                    if (gm_item_start != std::string::npos) {
                        auto gm_item_end = xmp.find("/>", gm_pos);
                        std::string_view gm_xml(xmp.data() + gm_item_start, gm_item_end != std::string::npos ? (gm_item_end - gm_item_start) : 200);
                        uint64_t g_len = 0;
                        if (extract_attr_u64(gm_xml, "Item:Length=", g_len) && g_len > 0 && g_len < out_facts->motion_video.file_range.offset) {
                            out_facts->gain_map.is_present = 1;
                            out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
                            out_facts->gain_map.file_range.offset = out_facts->motion_video.file_range.offset - g_len;
                            out_facts->gain_map.file_range.length = g_len;
                            out_facts->primary_image.file_range.length = out_facts->gain_map.file_range.offset;
                            return LPB_RESULT_OK;
                        }
                    }
                }

                out_facts->primary_image.file_range.offset = 0;
                out_facts->primary_image.file_range.length = out_facts->motion_video.file_range.offset;
                return LPB_RESULT_OK;
            }
        }
    }

    // 7. Check Google MicroVideo V1 (GCamera:MicroVideoOffset)
    uint64_t mv_offset = 0;
    if (!xmp.empty() && extract_attr_u64(xmp, "GCamera:MicroVideoOffset=", mv_offset) && mv_offset > 0 && mv_offset < primary_size) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = primary_size - mv_offset;
        out_facts->motion_video.file_range.length = mv_offset;

        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = out_facts->motion_video.file_range.offset;
        return LPB_RESULT_OK;
    }

    // 8. Non-Live Media fallback
    out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
    if (img_cont != LPB_IMAGE_CONTAINER_UNKNOWN) {
        out_facts->primary_image.is_present = 1;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = primary_size;
    } else if (vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN) {
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = vid_cont;
        out_facts->motion_video.file_range.offset = 0;
        out_facts->motion_video.file_range.length = primary_size;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
