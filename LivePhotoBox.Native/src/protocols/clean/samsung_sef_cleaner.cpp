#include "samsung_sef_cleaner.h"
#include "xmp_cleaner.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include "metadata/jpeg.h"
#include <fstream>
#include <filesystem>
#include <cstring>
#include <algorithm>
#include <limits>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>

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
            if (header_len < 2 || static_cast<size_t>(header_len) > limit - pos - 2) break;
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

static std::string extract_jpeg_xmp(const std::vector<uint8_t>& data) {
    if (data.size() < 2 || data[0] != 0xFF || data[1] != 0xD8) return {};
    constexpr char header[] = "http://ns.adobe.com/xap/1.0/\0";
    constexpr size_t header_size = sizeof(header) - 1;
    size_t p = 2;
    while (p + 2 <= data.size()) {
        if (data[p] != 0xFF) return {};
        while (p < data.size() && data[p] == 0xFF) ++p;
        if (p >= data.size()) return {};
        const uint8_t marker = data[p++];
        if (marker == 0xDA || marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
        if (p + 2 > data.size()) return {};
        const size_t length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
        if (length < 2 || length - 2 > data.size() - (p + 2)) return {};
        const size_t payload = p + 2;
        const size_t payload_size = length - 2;
        if (marker == 0xE1 && payload_size >= header_size &&
            std::memcmp(data.data() + payload, header, header_size) == 0) {
            const std::string_view xml(reinterpret_cast<const char*>(data.data() + payload + header_size), payload_size - header_size);
            const size_t start = xml.find("<x:xmpmeta");
            const size_t end_tag = start == std::string_view::npos ? start : xml.find("</x:xmpmeta>", start);
            if (start != std::string_view::npos && end_tag != std::string_view::npos) {
                return std::string(xml.substr(start, end_tag + 12 - start));
            }
        }
        p = payload + payload_size;
    }
    return {};
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

    // The footer's total_size starts at SEFH, while tag payloads are stored
    // before SEFH and addressed backwards from it. Do not locate SEFH by a
    // string search: a payload is allowed to contain the same bytes.
    if (input_size >= 20 && std::memcmp(data.data() + input_size - 4, "SEFT", 4) == 0) {
        const size_t footer_pos = input_size - 8;
        const auto le16 = [&](size_t at) noexcept -> uint16_t {
            return static_cast<uint16_t>(data[at]) | (static_cast<uint16_t>(data[at + 1]) << 8);
        };
        const auto le32 = [&](size_t at) noexcept -> uint32_t {
            return static_cast<uint32_t>(data[at]) |
                (static_cast<uint32_t>(data[at + 1]) << 8) |
                (static_cast<uint32_t>(data[at + 2]) << 16) |
                (static_cast<uint32_t>(data[at + 3]) << 24);
        };
        const uint32_t total_size = le32(footer_pos);
        if (total_size >= 12 && static_cast<uint64_t>(total_size) <= input_size - 8) {
            const size_t actual_sefh = input_size - 8 - static_cast<size_t>(total_size);
            const uint32_t entry_count = actual_sefh <= footer_pos && footer_pos - actual_sefh >= 12 ? le32(actual_sefh + 8) : 0;
            if (actual_sefh <= footer_pos && actual_sefh + 12 <= footer_pos &&
                std::memcmp(data.data() + actual_sefh, "SEFH", 4) == 0 &&
                entry_count <= (footer_pos - (actual_sefh + 12)) / 12 &&
                actual_sefh + 12 + static_cast<size_t>(entry_count) * 12 == footer_pos) {
                parsed_sef = true;
                sef_version = le32(actual_sefh + 4);
                size_t max_payload_offset = 0;
                std::vector<std::pair<size_t, size_t>> payload_ranges;
                for (uint32_t i = 0; i < entry_count; i++) {
                    const size_t entry_pos = actual_sefh + 12 + static_cast<size_t>(i) * 12;
                    const uint16_t prefix = le16(entry_pos);
                    const uint16_t marker = le16(entry_pos + 2);
                    const uint32_t offset = le32(entry_pos + 4);
                    const uint32_t size = le32(entry_pos + 8);
                    if (size < 8 || static_cast<uint64_t>(offset) > actual_sefh || size > offset) {
                        parsed_sef = false;
                        break;
                    }
                    const size_t payload_pos = actual_sefh - static_cast<size_t>(offset);
                    if (payload_pos > actual_sefh - size || payload_pos > input_size - 8 ||
                        le16(payload_pos) != prefix || le16(payload_pos + 2) != marker) {
                        parsed_sef = false;
                        break;
                    }
                    const size_t payload_end = payload_pos + static_cast<size_t>(size);
                    for (const auto& range : payload_ranges) {
                        if (payload_pos < range.second && range.first < payload_end) {
                            parsed_sef = false;
                            break;
                        }
                    }
                    if (!parsed_sef) break;
                    payload_ranges.emplace_back(payload_pos, payload_end);
                    const uint32_t name_size = le32(payload_pos + 4);
                    if (name_size > size - 8 || name_size > input_size - (payload_pos + 8)) {
                        parsed_sef = false;
                        break;
                    }
                    if (marker == 0x0A30) {
                        if (had_motion_photo || prefix != 0 || name_size != 16 || size < 24 ||
                            std::memcmp(data.data() + payload_pos + 8, "MotionPhoto_Data", 16) != 0 ||
                            !is_valid_isobmff_media_range(data.data(), data.size(), payload_pos + 24, size - 24)) {
                            parsed_sef = false;
                            break;
                        }
                        had_motion_photo = true;
                    } else if (marker == 0x0A31) {
                        if (prefix != 0 || name_size != 19 || size < 31 ||
                            std::memcmp(data.data() + payload_pos + 8, "MotionPhoto_Version", 19) != 0) {
                            parsed_sef = false;
                            break;
                        }
                    } else {
                        SefEntry retained{};
                        retained.prefix = prefix;
                        retained.marker = marker;
                        retained.payload.assign(data.data() + payload_pos, data.data() + payload_pos + size);
                        retained_entries.push_back(std::move(retained));
                    }
                    max_payload_offset = std::max(max_payload_offset, static_cast<size_t>(offset));
                }
                if (parsed_sef && had_motion_photo) {
                    add_fact(out_facts, "Samsung", "SEF Trailer", "Removed 0x0A30 MotionPhoto_Data from SEF");
                    const size_t payload_start = actual_sefh >= max_payload_offset ? actual_sefh - max_payload_offset : actual_sefh;
                    eoi = find_jpeg_eoi(data, payload_start);
                    if (eoi < 2 || eoi > payload_start || data[eoi - 2] != 0xFF || data[eoi - 1] != 0xD9) {
                        set_error(context, "Samsung SEF payload is not preceded by a complete JPEG EOI.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
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
    const std::string xmp_str = extract_jpeg_xmp(data);
    if (!xmp_str.empty()) {
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

    // If there were non-live SEF entries to retain, rebuild and append them
    if (!retained_entries.empty()) {
        size_t total_payloads_len = 0;
        for (const auto& e : retained_entries) {
            total_payloads_len += e.payload.size();
        }

        if (retained_entries.size() > (std::numeric_limits<size_t>::max() - 12) / 12) {
            set_error(context, "Retained Samsung SEF directory is too large to rebuild.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        size_t table_len = 12 + retained_entries.size() * 12; // SEFH(4) + ver(4) + count(4) + entries(N*12)
        if (retained_entries.size() > (std::numeric_limits<size_t>::max() - 20) / 12 ||
            total_payloads_len > std::numeric_limits<size_t>::max() - table_len - 8) {
            set_error(context, "Retained Samsung SEF metadata is too large to rebuild.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        size_t new_sef_len = total_payloads_len + table_len + 8; // payloads + SEFH/table + footer
        const size_t new_sef_section_len = table_len; // total_size ends before its own field and SEFT
        if (new_sef_len > std::numeric_limits<uint32_t>::max() || new_sef_section_len > std::numeric_limits<uint32_t>::max()) {
            set_error(context, "Rebuilt Samsung SEF exceeds its 32-bit size fields.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        std::vector<uint8_t> new_sef(new_sef_len);
        binary_writer writer(new_sef);

        // 1. Write payloads
        uint32_t current_offset = static_cast<uint32_t>(total_payloads_len);
        std::vector<uint32_t> entry_offsets;
        for (const auto& e : retained_entries) {
            if (!writer.try_write_bytes(e.payload.data(), e.payload.size())) {
                set_error(context, "Failed to rebuild retained Samsung SEF payloads.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
            entry_offsets.push_back(current_offset);
            current_offset -= static_cast<uint32_t>(e.payload.size());
        }

        // 2. Write SEFH header
        if (!writer.try_write_bytes(reinterpret_cast<const uint8_t*>("SEFH"), 4) ||
            !writer.try_write_u32_endian(sef_version, false) ||
            !writer.try_write_u32_endian(static_cast<uint32_t>(retained_entries.size()), false)) {
            set_error(context, "Failed to rebuild Samsung SEF header.");
            return LPB_RESULT_INTERNAL_ERROR;
        }

        // 3. Write SEF entries
        for (size_t i = 0; i < retained_entries.size(); i++) {
            if (!writer.try_write_u16_endian(retained_entries[i].prefix, false) ||
                !writer.try_write_u16_endian(retained_entries[i].marker, false) ||
                !writer.try_write_u32_endian(entry_offsets[i], false) ||
                !writer.try_write_u32_endian(static_cast<uint32_t>(retained_entries[i].payload.size()), false)) {
                set_error(context, "Failed to rebuild Samsung SEF directory.");
                return LPB_RESULT_INTERNAL_ERROR;
            }
        }

        // 4. Write total SEF size + SEFT
        if (!writer.try_write_u32_endian(static_cast<uint32_t>(new_sef_section_len), false) ||
            !writer.try_write_bytes(reinterpret_cast<const uint8_t*>("SEFT"), 4) ||
            writer.position() != new_sef.size()) {
            set_error(context, "Failed to finalize rebuilt Samsung SEF trailer.");
            return LPB_RESULT_INTERNAL_ERROR;
        }

        // Append rebuilt SEF directly after the clean JPEG
        data.insert(data.end(), new_sef.begin(), new_sef.end());
    }

    auto p_out = utf8_to_path(output_path.c_str());
    std::error_code write_ec;
    auto temp_dir = p_out.parent_path();
    if (temp_dir.empty()) temp_dir = std::filesystem::current_path(write_ec);
    wchar_t temp_name[MAX_PATH]{};
    if (write_ec || temp_dir.empty() || GetTempFileNameW(temp_dir.c_str(), L"lpb", 0, temp_name) == 0) {
        set_error(context, "Failed to open output Samsung JPEG for writing.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    const auto temp_path = std::filesystem::path(temp_name);
    std::ofstream out(temp_path, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) { std::filesystem::remove(temp_path, write_ec); return LPB_RESULT_INTERNAL_ERROR; }
    out.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    out.flush();
    const bool write_ok = out.good();
    out.close();
    if (!write_ok || !MoveFileExW(temp_path.c_str(), p_out.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        std::filesystem::remove(temp_path, write_ec);
        set_error(context, "Failed to write clean Samsung JPEG.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::protocols::clean
