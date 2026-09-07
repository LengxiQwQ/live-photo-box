#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include <algorithm>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

using namespace lpb;

namespace {
    struct locator_box {
        size_t start{};
        size_t size{};
        size_t header_size{};
        size_t body_start{};
        size_t body_size{};
        char type[5]{};
    };

    enum class locator_result { present, absent, malformed };

    bool parse_children_strict(const uint8_t* data, size_t start, size_t end,
        std::vector<locator_box>& out) {
        out.clear();
        if (!data || start > end) return false;
        size_t p = start;
        while (p < end) {
            if (end - p < 8) return false;
            isobmff_box_header header{};
            if (!try_read_box_header(data, p, end, header) ||
                header.size < header.header_size || header.size > end - p) {
                return false;
            }
            locator_box box{};
            box.start = p;
            box.size = header.size;
            box.header_size = header.header_size;
            box.body_start = p + header.header_size;
            box.body_size = header.size - header.header_size;
            std::memcpy(box.type, data + p + 4, 4);
            box.type[4] = '\0';
            out.push_back(box);
            p += header.size;
        }
        return p == end;
    }

    bool is_type(const locator_box& box, const char* type) noexcept {
        return std::memcmp(box.type, type, 4) == 0;
    }

    bool read_c_string(const uint8_t* data, size_t& p, size_t end, std::string& value) {
        value.clear();
        if (!data || p > end) return false;
        const size_t start = p;
        while (p < end && data[p] != 0) ++p;
        if (p == end) return false;
        if (p - start > 4096) return false;
        value.assign(reinterpret_cast<const char*>(data + start), p - start);
        ++p;
        return true;
    }

    struct locator_item {
        uint32_t id{};
        uint32_t type{};
        std::string content_type;
    };

    bool parse_iinf(const uint8_t* data, const locator_box& iinf,
        std::vector<locator_item>& out_items, std::string* failure = nullptr) {
        const auto fail = [&](const char* reason) {
            if (failure) *failure = reason;
            return false;
        };
        out_items.clear();
        if (iinf.body_size < 6) return fail("iinf body too short");
        size_t p = iinf.body_start;
        const size_t end = iinf.start + iinf.size;
        const uint8_t version = data[p++];
        if (version > 1 || data[p] != 0 || data[p + 1] != 0 || data[p + 2] != 0) return fail("iinf version or flags");
        p += 3;
        uint32_t count = 0;
        if (version == 0) {
            if (p + 2 > end) return fail("iinf count truncated");
            count = read_be16u(data + p);
            p += 2;
        } else {
            if (p + 4 > end) return fail("iinf count truncated");
            count = read_be32u(data + p);
            p += 4;
        }
        if (count > 1'000'000u) return fail("iinf count too large");

        std::vector<locator_box> children;
        if (!parse_children_strict(data, p, end, children)) return fail("iinf child boxes malformed");
        if (children.size() != count) return fail("iinf child count mismatch");
        std::vector<uint32_t> seen_ids;
        seen_ids.reserve(children.size());
        for (const auto& child : children) {
            if (!is_type(child, "infe") || child.body_size < 8) return fail("iinf contains non-infe or short child");
            size_t q = child.body_start;
            const size_t child_end = child.start + child.size;
            const uint8_t infe_version = data[q++];
            const uint32_t infe_flags = (static_cast<uint32_t>(data[q]) << 16) |
                (static_cast<uint32_t>(data[q + 1]) << 8) | data[q + 2];
            // The corpus uses the defined infe flag bit 0; other flags are
            // outside this locator's supported layout.
            if (infe_version < 2 || infe_version > 3 || (infe_flags & ~1u) != 0) return fail("infe version or flags");
            q += 3;
            uint32_t item_id = 0;
            if (infe_version == 2) {
                if (q + 2 > child_end) return fail("infe item id truncated");
                item_id = read_be16u(data + q);
                q += 2;
            } else {
                if (q + 4 > child_end) return fail("infe item id truncated");
                item_id = read_be32u(data + q);
                q += 4;
            }
            if (item_id == 0 || std::find(seen_ids.begin(), seen_ids.end(), item_id) != seen_ids.end()) return fail("duplicate or zero infe item id");
            seen_ids.push_back(item_id);
            if (q + 2 + 4 > child_end) return fail("infe type truncated");
            q += 2; // item_protection_index
            const uint32_t item_type = read_be32u(data + q);
            q += 4;

            std::string item_name;
            if (!read_c_string(data, q, child_end, item_name)) return fail("infe item_name unterminated");
            std::string content_type;
            if (item_type == 0x6D696D65u) { // mime
                // Both layouts occur in the corpus: the canonical form has an
                // empty item_name before content_type, while some producers
                // omit that empty field.  In either form every consumed field
                // must be NUL-terminated and the child must end exactly at
                // the final supported field.
                if (!item_name.empty() && item_name.rfind("application/", 0) == 0) {
                    content_type = item_name;
                    item_name.clear();
                } else if (!read_c_string(data, q, child_end, content_type)) {
                    return fail("infe content_type unterminated");
                }
                // content_encoding is optional in the field layout, but if present it
                // must still be a complete NUL-terminated field.
                if (q < child_end) {
                    std::string content_encoding;
                    if (!read_c_string(data, q, child_end, content_encoding)) return fail("infe content_encoding unterminated");
                }
            }
            if (q != child_end) return fail("infe trailing bytes");
            out_items.push_back({ item_id, item_type, std::move(content_type) });
        }
        return true;
    }

    bool read_uint_local(binary_reader& reader, size_t size, uint64_t& value) {
        if (size > 8 || reader.remaining() < size) return false;
        value = 0;
        for (size_t i = 0; i < size; ++i) {
            uint8_t b = 0;
            if (!reader.try_read_u8(b)) return false;
            if (value > (std::numeric_limits<uint64_t>::max() >> 8)) return false;
            value = (value << 8) | b;
        }
        return true;
    }

    bool parse_iloc_for_item(binary_reader& reader, size_t iloc_body, size_t iloc_end,
        uint32_t target_item_id, uint64_t data_size, uint64_t idat_body, uint64_t idat_size, bool has_idat,
        uint64_t* out_offset, uint64_t* out_length, bool* out_found) {
        if (iloc_body > iloc_end || iloc_end > reader.size()) return false;
        if (out_found) *out_found = false;
        if (!reader.try_seek(iloc_body)) return false;
        
        uint8_t iloc_version = 0;
        if (!reader.try_read_u8(iloc_version)) return false;
        if (iloc_version > 2) return false;
        uint8_t flag0 = 0, flag1 = 0, flag2 = 0;
        if (!reader.try_read_u8(flag0) || !reader.try_read_u8(flag1) || !reader.try_read_u8(flag2)) return false;
        if (flag0 != 0 || flag1 != 0 || flag2 != 0) return false;
        
        uint8_t byte1 = 0, byte2 = 0;
        if (!reader.try_read_u8(byte1) || !reader.try_read_u8(byte2)) return false;
        
        uint8_t offset_size = (byte1 >> 4) & 0x0F;
        uint8_t length_size = byte1 & 0x0F;
        uint8_t base_offset_size = (byte2 >> 4) & 0x0F;
        uint8_t index_size = (iloc_version == 1 || iloc_version == 2) ? (byte2 & 0x0F) : 0;
        if (iloc_version == 0 && (byte2 & 0x0F) != 0) return false;
        const auto valid_field_size = [](uint8_t size) noexcept { return size == 0 || size == 4 || size == 8; };
        if (iloc_version > 2 || !valid_field_size(offset_size) || !valid_field_size(length_size) ||
            !valid_field_size(base_offset_size) || !valid_field_size(index_size)) return false;
        
        uint32_t item_count = 0;
        if (iloc_version < 2) {
            uint16_t count16 = 0;
            if (!reader.try_read_be16u(count16)) return false;
            item_count = count16;
        } else {
            if (!reader.try_read_be32u(item_count)) return false;
        }

        bool found_target = false;
        std::vector<uint32_t> seen_item_ids;
        seen_item_ids.reserve(item_count);
        for (uint32_t i = 0; i < item_count; i++) {
            uint32_t item_id = 0;
            if (iloc_version < 2) {
                uint16_t id16 = 0;
                if (!reader.try_read_be16u(id16)) return false;
                item_id = id16;
            } else {
                if (!reader.try_read_be32u(item_id)) return false;
            }
            if (item_id == 0 || std::find(seen_item_ids.begin(), seen_item_ids.end(), item_id) != seen_item_ids.end()) return false;
            seen_item_ids.push_back(item_id);
            
            uint16_t construction_method = 0;
            if (iloc_version == 1 || iloc_version == 2) {
                if (!reader.try_read_be16u(construction_method)) return false;
                if ((construction_method & 0xFFF0u) != 0 || construction_method > 1) return false;
            }
            uint16_t data_reference_index = 0;
            if (!reader.try_read_be16u(data_reference_index)) return false;
            if (data_reference_index != 0) return false;
            if (reader.position() > iloc_end) return false;
            
            uint64_t base_offset = 0;
            if (base_offset_size > 0) {
                if (!read_uint_local(reader, base_offset_size, base_offset)) return false;
            }
            
            uint16_t extent_count = 0;
            if (!reader.try_read_be16u(extent_count)) return false;
            
            if (item_id == target_item_id && (found_target || extent_count != 1)) return false;
            for (uint16_t j = 0; j < extent_count; j++) {
                if ((iloc_version == 1 || iloc_version == 2) && index_size > 0) {
                    uint64_t ignored = 0;
                    if (!read_uint_local(reader, index_size, ignored)) return false;
                }
                
                uint64_t extent_offset = 0;
                if (offset_size > 0 && !read_uint_local(reader, offset_size, extent_offset)) return false;
                
                uint64_t extent_length = 0;
                if (length_size > 0 && !read_uint_local(reader, length_size, extent_length)) return false;

                const uint64_t owner_base = construction_method == 1 ? idat_body : 0;
                const uint64_t owner_size = construction_method == 1 ? idat_size : data_size;
                if (construction_method == 1 && !has_idat) return false;
                if (base_offset > owner_size || extent_offset > owner_size - base_offset ||
                    extent_length > owner_size - base_offset - extent_offset) return false;
                if (item_id == target_item_id) {
                    if (extent_length == 0 || owner_base > data_size || base_offset > data_size - owner_base ||
                        extent_offset > data_size - owner_base - base_offset ||
                        extent_length > data_size - owner_base - base_offset - extent_offset) return false;
                    *out_offset = owner_base + base_offset + extent_offset;
                    *out_length = extent_length;
                    found_target = true;
                }
            }
            if (reader.position() > iloc_end) return false;
            if (item_id == target_item_id) continue;
        }
        if (out_found) *out_found = found_target;
        return reader.position() == iloc_end;
    }

    locator_result locate_item(const uint8_t* input, size_t input_size, uint32_t wanted_type,
        const char* label, uint64_t* out_offset, uint64_t* out_length, std::string& error) {
        if (!input || !out_offset || !out_length) {
            error = "Invalid locator arguments.";
            return locator_result::malformed;
        }
        std::vector<locator_box> top_boxes;
        if (!parse_children_strict(input, 0, input_size, top_boxes)) {
            error = "Malformed top-level HEIF box region.";
            return locator_result::malformed;
        }
        const locator_box* meta = nullptr;
        for (const auto& box : top_boxes) {
            if (!is_type(box, "meta")) continue;
            if (meta) {
                error = "Duplicate authoritative meta box.";
                return locator_result::malformed;
            }
            meta = &box;
        }
        if (!meta || meta->body_size < 4) {
            error = "Missing or malformed meta box.";
            return locator_result::malformed;
        }
        const size_t meta_end = meta->start + meta->size;
        const uint8_t* meta_body = input + meta->body_start;
        if (meta_body[0] != 0 || meta_body[1] != 0 || meta_body[2] != 0 || meta_body[3] != 0) {
            error = "Unsupported meta FullBox version or flags.";
            return locator_result::malformed;
        }
        std::vector<locator_box> children;
        if (!parse_children_strict(input, meta->body_start + 4, meta_end, children)) {
            error = "Malformed meta child box region.";
            return locator_result::malformed;
        }
        const locator_box* iinf = nullptr;
        const locator_box* iloc = nullptr;
        const locator_box* idat = nullptr;
        for (const auto& box : children) {
            if (is_type(box, "iinf")) {
                if (iinf) { error = "Duplicate authoritative iinf box."; return locator_result::malformed; }
                iinf = &box;
            } else if (is_type(box, "iloc")) {
                if (iloc) { error = "Duplicate authoritative iloc box."; return locator_result::malformed; }
                iloc = &box;
            } else if (is_type(box, "idat")) {
                if (idat) { error = "Duplicate idat box."; return locator_result::malformed; }
                idat = &box;
            }
        }
        if (!iinf || !iloc) {
            error = "Missing authoritative iinf or iloc box.";
            return locator_result::malformed;
        }

        std::vector<locator_item> items;
        std::string iinf_failure;
        if (!parse_iinf(input, *iinf, items, &iinf_failure)) {
            error = "Malformed or unsupported iinf/infe structure: " + iinf_failure;
            return locator_result::malformed;
        }
        uint32_t target_item_id = 0;
        for (const auto& item : items) {
            const bool is_target = item.type == wanted_type ||
                (wanted_type == 0x6D696D65u && item.type == 0x6D696D65u &&
                 item.content_type.rfind("application/rdf+xml", 0) == 0);
            if (is_target && target_item_id == 0) {
                target_item_id = item.id;
            }
        }

        binary_reader reader(input, input_size);
        const uint64_t idat_body = idat ? static_cast<uint64_t>(idat->body_start) : 0;
        const uint64_t idat_size = idat ? static_cast<uint64_t>(idat->body_size) : 0;
        bool found_location = false;
        if (!parse_iloc_for_item(reader, iloc->body_start, iloc->start + iloc->size,
            target_item_id, input_size, idat_body, idat_size, idat != nullptr,
            out_offset, out_length, &found_location)) {
            error = "Malformed or unsupported iloc structure.";
            return locator_result::malformed;
        }
        if (target_item_id == 0) {
            error = std::string("Confirmed absent: no ") + label + " item in validated iinf.";
            return locator_result::absent;
        }
        if (!found_location) {
            error = std::string("Malformed authoritative graph: ") + label + " item has no iloc entry.";
            return locator_result::malformed;
        }
        return locator_result::present;
    }
}

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_locate_exif_item(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_offset,
    uint64_t* out_length)
{
    if (context == nullptr || input == nullptr || out_offset == nullptr || out_length == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try {
        std::string error;
        const locator_result result = locate_item(input, input_size, 0x45786966u, "Exif", out_offset, out_length, error);
        set_error(context, error.c_str());
        return result == locator_result::present ? LPB_RESULT_OK : LPB_RESULT_INVALID_ARGUMENT;
    } catch (const std::exception& ex) {
        set_error(context, ex.what());
        return LPB_RESULT_INVALID_ARGUMENT;
    }
}

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_locate_xmp_item(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    uint64_t* out_offset,
    uint64_t* out_length)
{
    if (context == nullptr || input == nullptr || out_offset == nullptr || out_length == nullptr)
    {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try {
        std::string error;
        const locator_result result = locate_item(input, input_size, 0x6D696D65u, "XMP", out_offset, out_length, error);
        set_error(context, error.c_str());
        return result == locator_result::present ? LPB_RESULT_OK : LPB_RESULT_INVALID_ARGUMENT;
    } catch (const std::exception& ex) {
        set_error(context, ex.what());
        return LPB_RESULT_INVALID_ARGUMENT;
    }
}
