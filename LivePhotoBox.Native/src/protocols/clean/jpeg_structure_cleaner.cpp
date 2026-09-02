#include "jpeg_structure_cleaner.h"
#include "foundation/internal.h"
#include <fstream>
#include <vector>
#include <cstring>
#include <string_view>

namespace lpb::protocols::clean {

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
        if (data[pos] != 0xFF) {
            pos++;
            continue;
        }

        uint8_t marker = data[pos + 1];
        if (marker == 0x00 || marker == 0xFF) {
            pos += 2;
            continue;
        }

        if (marker == 0xD9) { // EOI
            eoi_pos = pos + 2;
            break;
        }

        if (marker == 0xDA) { // SOS - Start of Scan
            if (pos + 4 > data_len) break;
            uint16_t header_len = (static_cast<uint16_t>(data[pos + 2]) << 8) | data[pos + 3];
            pos += 2 + header_len;

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
        if (pos + 4 > data_len) break;
        uint16_t seg_len = (static_cast<uint16_t>(data[pos + 2]) << 8) | data[pos + 3];
        pos += 2 + seg_len;
    }

    if (eoi_pos > 0 && eoi_pos < data_len) {
        // Trailing tail bytes detected and stripped
        data.resize(eoi_pos);
        add_fact(out_facts, proto, comp, desc);
    }

    auto p_out = utf8_to_path(output_path.c_str());
    std::ofstream out(p_out, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        set_error(context, "Failed to open output JPEG for writing.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    if (!out.good()) {
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

    size_t data_len = data.size();
    size_t scan_start = data_len > 4096 ? data_len - 4096 : 0;
    std::string_view tail(reinterpret_cast<const char*>(data.data() + scan_start), data_len - scan_start);

    auto live_pos = tail.rfind("LIVE_");
    if (live_pos != std::string_view::npos) {
        size_t actual_live_pos = scan_start + live_pos;
        size_t trailer_start = (actual_live_pos >= 40) ? (actual_live_pos - 40) : actual_live_pos;
        data.resize(trailer_start);
        add_fact(out_facts, "Huawei/Honor", "LIVE Tail", "Removed 60-byte LIVE_ tail marker from HEIC");
    }

    auto p_out = utf8_to_path(output_path.c_str());
    std::ofstream out(p_out, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        set_error(context, "Failed to open output Huawei HEIC for writing.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    if (!out.good()) {
        set_error(context, "Failed to write clean Huawei HEIC.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::protocols::clean
