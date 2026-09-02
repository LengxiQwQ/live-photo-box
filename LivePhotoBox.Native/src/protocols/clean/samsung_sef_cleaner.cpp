#include "samsung_sef_cleaner.h"
#include "xmp_cleaner.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "metadata/jpeg.h"
#include <fstream>
#include <cstring>
#include <algorithm>

namespace lpb::protocols::clean {

struct SefEntry {
    uint16_t prefix;
    uint16_t marker;
    std::vector<uint8_t> payload;
};

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

static size_t find_jpeg_eoi(const std::vector<uint8_t>& data, size_t max_search) {
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return data.size();
    size_t pos = 2;
    size_t limit = std::min(data.size(), max_search);

    while (pos + 1 < limit) {
        if (data[pos] != 0xFF) {
            pos++;
            continue;
        }

        uint8_t marker = data[pos + 1];
        if (marker == 0x00 || marker == 0xFF) {
            pos += 2;
            continue;
        }

        if (marker == 0xD9) {
            return pos + 2;
        }

        if (marker == 0xDA) { // SOS
            if (pos + 4 > limit) break;
            uint16_t header_len = (static_cast<uint16_t>(data[pos + 2]) << 8) | data[pos + 3];
            pos += 2 + header_len;

            while (pos + 1 < limit) {
                if (data[pos] == 0xFF) {
                    uint8_t m = data[pos + 1];
                    if (m == 0xD9) {
                        return pos + 2;
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

        if (pos + 4 > limit) break;
        uint16_t seg_len = (static_cast<uint16_t>(data[pos + 2]) << 8) | data[pos + 3];
        pos += 2 + seg_len;
    }
    return limit;
}

lpb_result clean_samsung_sef_jpeg(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    std::vector<lpb_removed_protocol_fact>& out_facts)
{
    auto p_in = utf8_to_path(input_path.c_str());
    std::ifstream in(p_in, std::ios::binary | std::ios::ate);
    if (!in.is_open()) {
        set_error(context, "Failed to open input Samsung JPEG for cleaning.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto file_sz = in.tellg();
    if (file_sz < 16) {
        set_error(context, "Input file too small for Samsung JPEG.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<uint8_t> data(static_cast<size_t>(file_sz));
    in.seekg(0, std::ios::beg);
    in.read(reinterpret_cast<char*>(data.data()), file_sz);
    if (!in.good()) {
        set_error(context, "Failed to read Samsung JPEG data.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    in.close();

    size_t input_size = data.size();
    std::vector<SefEntry> retained_entries;
    uint32_t sef_version = 107;
    size_t eoi = input_size;
    bool parsed_sef = false;
    bool had_motion_photo = false;

    // Check for SEFT at the end of the file
    if (input_size >= 16 && data[input_size - 4] == 'S' && data[input_size - 3] == 'E' &&
        data[input_size - 2] == 'F' && data[input_size - 1] == 'T')
    {
        size_t scan_len = input_size > 4096 ? 4096 : input_size;
        std::string_view tail(reinterpret_cast<const char*>(data.data() + (input_size - scan_len)), scan_len);
        auto sefh_pos = tail.rfind("SEFH");
        if (sefh_pos != std::string_view::npos) {
            size_t actual_sefh = (input_size - scan_len) + sefh_pos;
            if (actual_sefh + 12 <= input_size) {
                binary_reader reader(data.data() + actual_sefh, input_size - actual_sefh);
                reader.skip(4); // Skip SEFH

                uint32_t version = 0;
                uint32_t entry_count = 0;
                if (reader.try_read_u32_endian(version, false) && reader.try_read_u32_endian(entry_count, false) && entry_count > 0 && entry_count < 100) {
                    parsed_sef = true;
                    sef_version = version;
                    size_t max_payload_offset = 0;

                    for (uint32_t i = 0; i < entry_count; i++) {
                        uint16_t prefix = 0;
                        uint16_t marker = 0;
                        uint32_t offset = 0;
                        uint32_t size = 0;

                        if (!reader.try_read_u16_endian(prefix, false) ||
                            !reader.try_read_u16_endian(marker, false) ||
                            !reader.try_read_u32_endian(offset, false) ||
                            !reader.try_read_u32_endian(size, false)) {
                            parsed_sef = false;
                            break;
                        }

                        if (offset > max_payload_offset) max_payload_offset = offset;

                        if (marker == 0x0A30 || marker == 0x0A31) {
                            had_motion_photo = true;
                            continue;
                        }

                        // Retain non-live SEF tag
                        if (offset <= actual_sefh && size <= offset) {
                            size_t payload_pos = actual_sefh - offset;
                            if (size <= payload_pos && payload_pos <= actual_sefh && size <= actual_sefh - payload_pos) {
                                SefEntry entry{};
                                entry.prefix = prefix;
                                entry.marker = marker;
                                entry.payload.assign(data.data() + payload_pos, data.data() + payload_pos + size);
                                retained_entries.push_back(std::move(entry));
                            }
                        }
                    }

                    if (had_motion_photo) {
                        add_fact(out_facts, "Samsung", "SEF Trailer", "Removed 0x0A30 MotionPhoto_Data from SEF");
                    }

                    // Find true JPEG EOI before the start of all SEF payloads
                    size_t sef_payloads_start = (actual_sefh >= max_payload_offset) ? (actual_sefh - max_payload_offset) : actual_sefh;
                    eoi = find_jpeg_eoi(data, sef_payloads_start);
                }
            }
        }
    }

    if (!parsed_sef || !had_motion_photo) {
        set_error(context, "Samsung SEF trailer is missing or malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Truncate data to pure JPEG
    data.resize(eoi);

    // Clean XMP metadata inside the pure JPEG if present
    std::string_view sv(reinterpret_cast<const char*>(data.data()), data.size());
    const std::string xmp_start = "<x:xmpmeta";
    const std::string xmp_end = "</x:xmpmeta>";
    auto s_pos = sv.find(xmp_start);
    if (s_pos != std::string_view::npos) {
        auto e_pos = sv.find(xmp_end, s_pos);
        if (e_pos != std::string_view::npos) {
            std::string xmp_str(sv.substr(s_pos, e_pos + xmp_end.length() - s_pos));
            std::string cleaned_xmp;
            if (clean_xmp_metadata(xmp_str, LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG, cleaned_xmp, out_facts)) {
                std::vector<uint8_t> out_buf(data.size() + cleaned_xmp.size() + 4096);
                size_t written = 0;
                if (lpb_jpeg_inject_xmp(context, data.data(), data.size(), reinterpret_cast<const uint8_t*>(cleaned_xmp.data()), cleaned_xmp.size(), out_buf.data(), out_buf.size(), &written) == LPB_RESULT_OK && written > 0) {
                    out_buf.resize(written);
                    data = std::move(out_buf);
                }
            }
        }
    }

    // If there were non-live SEF entries to retain, rebuild and append them
    if (!retained_entries.empty()) {
        size_t total_payloads_len = 0;
        for (const auto& e : retained_entries) {
            total_payloads_len += e.payload.size();
        }

        size_t table_len = 12 + retained_entries.size() * 12; // SEFH(4) + ver(4) + count(4) + entries(N*12)
        size_t new_sef_len = total_payloads_len + table_len + 8; // + total_size(4) + SEFT(4)

        std::vector<uint8_t> new_sef(new_sef_len);
        binary_writer writer(new_sef);

        // 1. Write payloads
        uint32_t current_offset = static_cast<uint32_t>(total_payloads_len);
        std::vector<uint32_t> entry_offsets;
        for (const auto& e : retained_entries) {
            writer.try_write_bytes(e.payload.data(), e.payload.size());
            entry_offsets.push_back(current_offset);
            current_offset -= static_cast<uint32_t>(e.payload.size());
        }

        // 2. Write SEFH header
        writer.try_write_bytes(reinterpret_cast<const uint8_t*>("SEFH"), 4);
        writer.try_write_u32_endian(sef_version, false);
        writer.try_write_u32_endian(static_cast<uint32_t>(retained_entries.size()), false);

        // 3. Write SEF entries
        for (size_t i = 0; i < retained_entries.size(); i++) {
            writer.try_write_u16_endian(retained_entries[i].prefix, false);
            writer.try_write_u16_endian(retained_entries[i].marker, false);
            writer.try_write_u32_endian(entry_offsets[i], false);
            writer.try_write_u32_endian(static_cast<uint32_t>(retained_entries[i].payload.size()), false);
        }

        // 4. Write total SEF size + SEFT
        writer.try_write_u32_endian(static_cast<uint32_t>(new_sef_len), false);
        writer.try_write_bytes(reinterpret_cast<const uint8_t*>("SEFT"), 4);

        // Append rebuilt SEF directly after the clean JPEG
        data.insert(data.end(), new_sef.begin(), new_sef.end());
    }

    auto p_out = utf8_to_path(output_path.c_str());
    std::ofstream out(p_out, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        set_error(context, "Failed to open output Samsung JPEG for writing.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    if (!out.good()) {
        set_error(context, "Failed to write clean Samsung JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::protocols::clean
