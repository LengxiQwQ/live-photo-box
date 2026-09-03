#include "jpeg_structure_cleaner.h"
#include "foundation/internal.h"
#include "containers/isobmff.h"
#include <charconv>
#include <fstream>
#include <vector>
#include <cstring>
#include <string_view>
#include <filesystem>
#include <limits>
#define NOMINMAX
#include <Windows.h>

namespace fs = std::filesystem;

namespace lpb::protocols::clean {

static bool has_complete_heif_prefix(const std::vector<uint8_t>& data, size_t end) noexcept
{
    if (end > data.size() || end < 8) return false;
    size_t pos = 0;
    bool first = true;
    while (pos < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), pos, end, box)) return false;
        if (first) {
            if (std::memcmp(data.data() + pos + 4, "ftyp", 4) != 0) return false;
            first = false;
        }
        pos += box.size;
    }
    return pos == end && !first;
}

static bool write_atomic(const fs::path& path, const std::vector<uint8_t>& data)
{
    fs::path temp = path;
    temp += L".lpb-jpeg-cleaning-tmp";
    std::error_code ec;
    fs::remove(temp, ec);
    {
        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
        if (!out.is_open()) return false;
        out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
        out.flush();
        if (!out.good()) { out.close(); fs::remove(temp, ec); return false; }
    }
    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        fs::remove(temp, ec);
        return false;
    }
    return true;
}

static void add_fact(
    std::vector<lpb_removed_protocol_fact>& out_facts,
    const char* proto,
    const char* comp,
    const char* desc)
{
    lpb_removed_protocol_fact fact{};
    fact.struct_size = sizeof(lpb_removed_protocol_fact);
    strncpy_s(fact.protocol_name, proto, _TRUNCATE);
    strncpy_s(fact.component, comp, _TRUNCATE);
    strncpy_s(fact.description, desc, _TRUNCATE);
    out_facts.push_back(fact);
}

lpb_result strip_jpeg_tail_data(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    const char* proto,
    const char* comp,
    const char* desc,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    auto p_in = utf8_to_path(input_path.c_str());
    std::ifstream in(p_in, std::ios::binary | std::ios::ate);
    if (!in.is_open()) {
        set_error(context, "Failed to open input JPEG for tail stripping.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto file_sz = in.tellg();
    if (file_sz < 4) {
        set_error(context, "Input file too small for JPEG.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> data(static_cast<size_t>(file_sz));
    in.seekg(0, std::ios::beg);
    in.read(reinterpret_cast<char*>(data.data()), file_sz);
    if (!in.good()) {
        set_error(context, "Failed to read JPEG data.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    in.close();

    size_t data_len = data.size();
    if (data_len < 4 || data[0] != 0xFF || data[1] != 0xD8) {
        set_error(context, "Invalid JPEG SOI marker.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Scan top-level segments to find SOS and EOI
    size_t pos = 2;
    size_t eoi_pos = 0;

    while (pos + 1 < data_len) {
        if (data[pos] != 0xFF) break;
        while (pos < data_len && data[pos] == 0xFF) ++pos;
        if (pos >= data_len) break;

        uint8_t marker = data[pos++];
        if (marker == 0x00 || marker == 0xFF || (marker >= 0xD0 && marker <= 0xD7)) break;

        if (marker == 0xD9) { // EOI
            eoi_pos = pos;
            break;
        }

        if (marker == 0xDA) { // SOS - Start of Scan
            if (pos + 2 > data_len) break;
            uint16_t header_len = (static_cast<uint16_t>(data[pos]) << 8) | data[pos + 1];
            if (header_len < 2 || static_cast<size_t>(header_len) > data_len - pos) break;
            pos += header_len;

            // Scan entropy-coded data to find EOI
            while (pos + 1 < data_len) {
                if (data[pos] == 0xFF) {
                    uint8_t m = data[pos + 1];
                    if (m == 0xD9) { // Real EOI
                        eoi_pos = pos + 2;
                        break;
                    }
                    if (m == 0x00 || (m >= 0xD0 && m <= 0xD7)) {
                        pos += 2;
                        continue;
                    }
                }
                pos++;
            }
            break;
        }

        // Variable length marker
        if (pos + 2 > data_len) break;
        uint16_t seg_len = (static_cast<uint16_t>(data[pos]) << 8) | data[pos + 1];
        if (seg_len < 2 || static_cast<size_t>(seg_len) > data_len - pos) break;
        pos += seg_len;
    }

    if (eoi_pos == 0 || eoi_pos >= data_len || data_len < 60 ||
        std::memcmp(data.data() + data_len - 20, "LIVE_", 5) != 0) {
        set_error(context, "Validated Huawei/Honor LIVE trailer was not found after the JPEG.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // The last 20 bytes contain LIVE_ plus a decimal MP4-size+20 field.  Do
    // not strip arbitrary bytes after a JPEG EOI merely because they happen
    // to contain the marker; confirm the declared media range as well.
    const char* number = reinterpret_cast<const char*>(data.data() + data_len - 15);
    const char* number_end = number + 15;
    const char* digits_end = number;
    while (digits_end < number_end && *digits_end >= '0' && *digits_end <= '9') ++digits_end;
    uint64_t mp4_plus_20 = 0;
    const auto parsed = std::from_chars(number, digits_end, mp4_plus_20, 10);
    bool padding_ok = digits_end != number;
    for (const char* p = digits_end; p < number_end; ++p) padding_ok = padding_ok && (*p == ' ' || *p == '\0');
    if (parsed.ec != std::errc{} || parsed.ptr != digits_end || !padding_ok ||
        mp4_plus_20 <= 20 || mp4_plus_20 > data_len) {
        set_error(context, "Huawei/Honor LIVE trailer contains an invalid MP4 length.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t video_length = static_cast<size_t>(mp4_plus_20 - 20);
    const size_t trailer_start = data_len - 60;
    if (video_length > trailer_start) {
        set_error(context, "Huawei/Honor LIVE trailer points before the file start.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t video_offset = trailer_start - video_length;
    if (video_offset < eoi_pos ||
        !is_valid_isobmff_media_range(data.data(), data.size(), video_offset, video_length)) {
        set_error(context, "Huawei/Honor LIVE trailer does not describe a structurally valid MP4 range.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Remove the complete embedded MP4 and its 60-byte protocol footer while
    // retaining the JPEG and any validated bytes between its EOI and ftyp.
    data.resize(video_offset);
    add_fact(out_facts, proto, comp, desc);

    if (!write_atomic(utf8_to_path(output_path.c_str()), data)) {
        set_error(context, "Failed to write clean JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

lpb_result clean_huawei_image(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    lpb_image_container container,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    if (container == LPB_IMAGE_CONTAINER_JPEG) {
        return strip_jpeg_tail_data(context, input_path, output_path, "Huawei/Honor", "LIVE Tail", "Removed 60-byte LIVE_ tail marker", out_facts);
    }

    // HEIC Container: check for LIVE_ at the end of the file
    auto p_in = utf8_to_path(input_path.c_str());
    std::ifstream in(p_in, std::ios::binary | std::ios::ate);
    if (!in.is_open()) {
        set_error(context, "Failed to open input Huawei HEIC for cleaning.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto file_sz = in.tellg();
    if (file_sz < 64) {
        set_error(context, "Input HEIC too small.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> data(static_cast<size_t>(file_sz));
    in.seekg(0, std::ios::beg);
    in.read(reinterpret_cast<char*>(data.data()), file_sz);
    if (!in.good()) {
        set_error(context, "Failed to read Huawei HEIC data.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    in.close();

    const size_t data_len = data.size();
    if (data_len < 60 || std::memcmp(data.data() + data_len - 20, "LIVE_", 5) != 0) {
        set_error(context, "Validated Huawei/Honor LIVE trailer was not found in the HEIC.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const char* number = reinterpret_cast<const char*>(data.data() + data_len - 15);
    const char* number_end = number + 15;
    const char* digits_end = number;
    while (digits_end < number_end && *digits_end >= '0' && *digits_end <= '9') ++digits_end;
    uint64_t mp4_plus_20 = 0;
    const auto parsed = std::from_chars(number, digits_end, mp4_plus_20, 10);
    bool padding_ok = digits_end != number;
    for (const char* p = digits_end; p < number_end; ++p) padding_ok = padding_ok && (*p == ' ' || *p == '\0');
    if (parsed.ec != std::errc{} || parsed.ptr != digits_end || !padding_ok ||
        mp4_plus_20 <= 20 || mp4_plus_20 > data_len) {
        set_error(context, "Huawei/Honor HEIC LIVE trailer contains an invalid MP4 length.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t video_length = static_cast<size_t>(mp4_plus_20 - 20);
    const size_t trailer_start = data_len - 60;
    if (video_length > trailer_start) {
        set_error(context, "Huawei/Honor HEIC LIVE trailer points before the file start.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t video_offset = trailer_start - video_length;
    if (!has_complete_heif_prefix(data, video_offset) ||
        !is_valid_isobmff_media_range(data.data(), data.size(), video_offset, video_length)) {
        set_error(context, "Huawei/Honor HEIC LIVE trailer does not describe a structurally valid MP4 range.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    data.resize(video_offset);
    add_fact(out_facts, "Huawei/Honor", "LIVE Tail", "Removed embedded MP4 and 60-byte LIVE_ tail from HEIC");

    if (!write_atomic(utf8_to_path(output_path.c_str()), data)) {
        set_error(context, "Failed to write clean Huawei HEIC.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::protocols::clean
