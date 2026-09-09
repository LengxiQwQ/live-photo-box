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

    struct auxiliary_property {
        uint32_t item_id{};
        std::string type;
    };

    bool parse_heif_auxiliary_properties(const uint8_t* data, const locator_box& iprp,
        const std::vector<locator_item>& items, std::vector<auxiliary_property>& auxc_items,
        std::string& error) {
        auxc_items.clear();
        if (iprp.body_size < 8) { error = "HEIF iprp box is truncated."; return false; }
        std::vector<locator_box> children;
        if (!parse_children_strict(data, iprp.body_start, iprp.start + iprp.size, children)) {
            error = "Malformed HEIF iprp children."; return false;
        }
        const locator_box* ipco = nullptr; const locator_box* ipma = nullptr;
        for (const auto& child : children) {
            if (is_type(child, "ipco")) { if (ipco) { error = "Duplicate HEIF ipco box."; return false; } ipco = &child; }
            else if (is_type(child, "ipma")) { if (ipma) { error = "Duplicate HEIF ipma box."; return false; } ipma = &child; }
        }
        if (!ipco || !ipma || ipco->body_size == 0) { error = "HEIF auxiliary properties are incomplete."; return false; }
        std::vector<std::string> auxiliary_types(1);
        std::vector<locator_box> properties;
        if (!parse_children_strict(data, ipco->body_start, ipco->start + ipco->size, properties)) {
            error = "Malformed HEIF ipco properties."; return false;
        }
        for (const auto& property : properties) {
            std::string auxiliary_type;
            if (is_type(property, "auxC")) {
                if (property.body_size < 5 || data[property.body_start] || data[property.body_start + 1] ||
                    data[property.body_start + 2] || data[property.body_start + 3]) {
                    error = "Malformed HEIF auxC property."; return false;
                }
                size_t p = property.body_start + 4;
                std::string aux_type;
                if (!read_c_string(data, p, property.start + property.size, aux_type) || aux_type.empty()) {
                    error = "HEIF auxC property has no auxiliary type."; return false;
                }
                auxiliary_type = std::move(aux_type);
            }
            auxiliary_types.push_back(std::move(auxiliary_type));
        }
        if (ipma->body_size < 8) { error = "HEIF ipma box is truncated."; return false; }
        const uint8_t version = data[ipma->body_start];
        const uint32_t flags = (static_cast<uint32_t>(data[ipma->body_start + 1]) << 16) |
            (static_cast<uint32_t>(data[ipma->body_start + 2]) << 8) | data[ipma->body_start + 3];
        if (version > 1 || (flags & ~1u) != 0) { error = "Unsupported HEIF ipma version or flags."; return false; }
        size_t p = ipma->body_start + 4;
        uint32_t entry_count = 0;
        if (p + 4 > ipma->start + ipma->size) {
            error = "HEIF ipma entry count is truncated.";
            return false;
        }
        entry_count = read_be32u(data + p);
        p += 4;
        std::vector<uint32_t> seen_items;
        for (uint32_t i = 0; i < entry_count; ++i) {
            const size_t end = ipma->start + ipma->size;
            uint32_t item_id = 0;
            if (version == 0) { if (p + 2 > end) return false; item_id = read_be16u(data + p); p += 2; }
            else { if (p + 4 > end) return false; item_id = read_be32u(data + p); p += 4; }
            if (item_id == 0 || std::find_if(items.begin(), items.end(), [&](const locator_item& item) { return item.id == item_id; }) == items.end() ||
                std::find(seen_items.begin(), seen_items.end(), item_id) != seen_items.end() || p >= end) {
                error = "HEIF ipma references a duplicate or unknown item."; return false;
            }
            seen_items.push_back(item_id);
            const uint8_t association_count = data[p++];
            std::vector<uint32_t> seen_properties;
            for (uint8_t a = 0; a < association_count; ++a) {
                uint32_t association = 0;
                if (flags & 1u) { if (p + 2 > end) return false; association = read_be16u(data + p) & 0x7FFFu; p += 2; }
                else { if (p >= end) return false; association = data[p++] & 0x7Fu; }
                if (association == 0 || association >= auxiliary_types.size() ||
                    std::find(seen_properties.begin(), seen_properties.end(), association) != seen_properties.end()) {
                    error = "HEIF ipma has an invalid or duplicate property association."; return false;
                }
                seen_properties.push_back(association);
                if (!auxiliary_types[association].empty()) {
                    const auto prior = std::find_if(auxc_items.begin(), auxc_items.end(),
                        [&](const auxiliary_property& value) { return value.item_id == item_id; });
                    if (prior != auxc_items.end()) {
                        error = "HEIF item has multiple auxiliary type properties.";
                        return false;
                    }
                    auxc_items.push_back({ item_id, auxiliary_types[association] });
                }
            }
        }
        if (p != ipma->start + ipma->size) { error = "HEIF ipma has trailing bytes."; return false; }
        return true;
    }

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
            } else if (item_type == 0x75726920u) { // 'uri '
                // ISO/IEC 14496-12 item_uri_type is a required
                // NUL-terminated field following item_name.  Apple uses
                // this for auxiliary style metadata in otherwise valid HEIF.
                std::string item_uri_type;
                if (!read_c_string(data, q, child_end, item_uri_type) || item_uri_type.empty()) {
                    return fail("infe item_uri_type missing or unterminated");
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
        const char* label, uint64_t* out_offset, uint64_t* out_length, std::string& error,
        uint32_t preferred_item_id = 0) {
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
        const locator_box* pitm = nullptr;
        const locator_box* iref = nullptr;
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
            } else if (is_type(box, "pitm")) {
                if (pitm) { error = "Duplicate authoritative pitm box."; return locator_result::malformed; }
                pitm = &box;
            } else if (is_type(box, "iref")) {
                if (iref) { error = "Duplicate authoritative iref box."; return locator_result::malformed; }
                iref = &box;
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
        std::vector<uint32_t> target_item_ids;
        for (const auto& item : items) {
            const bool is_target = wanted_type == 0x6D696D65u
                ? item.type == 0x6D696D65u && item.content_type.rfind("application/rdf+xml", 0) == 0
                : item.type == wanted_type;
            if (is_target) target_item_ids.push_back(item.id);
        }

        if (preferred_item_id != 0 &&
            std::find(target_item_ids.begin(), target_item_ids.end(), preferred_item_id) == target_item_ids.end()) {
            error = std::string("Confirmed absent: requested ") + label + " item is not an XMP item.";
            return locator_result::absent;
        }
        // Apple Exif authority is a one-to-one graph: there must be exactly
        // one Exif item in the validated iinf, and that item must be the one
        // reached through the primary's sole cdsc relation.  Do not select
        // the first owned candidate while silently leaving an extra,
        // unowned/shadow Exif item in the file.
        if (wanted_type == 0x45786966u && target_item_ids.size() > 1) {
            error = "Ambiguous authoritative graph: multiple Exif items.";
            return locator_result::malformed;
        }
        uint32_t target_item_id = preferred_item_id != 0
            ? preferred_item_id
            : (target_item_ids.empty() ? 0 : target_item_ids.front());
        // The pitm -> cdsc owner relation is an Apple Exif authority rule.
        // Do not apply it to the generic XMP locator: Google Motion Photo
        // HEIFs may contain multiple RDF/XMP items whose ownership is
        // described by their own metadata and which were accepted by the
        // pre-owner-graph locator.  Treating those items as Apple Exif would
        // make an otherwise valid Google candidate disappear as NonLive.
        const bool requires_primary_cdsc = wanted_type == 0x45786966u;
        if (requires_primary_cdsc && !target_item_ids.empty()) {
            if (!pitm || !iref || pitm->body_size < 6 || iref->body_size < 4) {
                error = std::string("Ambiguous authoritative graph: multiple ") + label + " items.";
                return locator_result::malformed;
            }

            const uint8_t pitm_version = input[pitm->body_start];
            if (input[pitm->body_start + 1] || input[pitm->body_start + 2] || input[pitm->body_start + 3] ||
                pitm_version > 1 || (pitm_version == 1 && pitm->body_size < 8)) {
                error = "Malformed authoritative pitm box.";
                return locator_result::malformed;
            }
            const uint32_t primary_item_id = pitm_version == 0
                ? read_be16u(input + pitm->body_start + 4)
                : read_be32u(input + pitm->body_start + 4);

            const uint8_t iref_version = input[iref->body_start];
            if (iref_version > 1 || input[iref->body_start + 1] ||
                input[iref->body_start + 2] || input[iref->body_start + 3]) {
                error = "Malformed authoritative iref box.";
                return locator_result::malformed;
            }
            std::vector<locator_box> references;
            if (!parse_children_strict(input, iref->body_start + 4, iref->start + iref->size, references)) {
                error = "Malformed authoritative iref children.";
                return locator_result::malformed;
            }

            std::vector<uint32_t> primary_owned_candidates;
            for (const auto& reference : references) {
                if (!is_type(reference, "cdsc")) continue;
                size_t rp = reference.body_start;
                const size_t reference_end = reference.start + reference.size;
                uint32_t from_item_id = 0;
                if (iref_version == 0) {
                    if (rp + 4 > reference_end) { error = "Malformed HEIF cdsc reference."; return locator_result::malformed; }
                    from_item_id = read_be16u(input + rp); rp += 2;
                } else {
                    if (rp + 6 > reference_end) { error = "Malformed HEIF cdsc reference."; return locator_result::malformed; }
                    from_item_id = read_be32u(input + rp); rp += 4;
                }
                const uint16_t reference_count = read_be16u(input + rp); rp += 2;
                if (reference_count == 0) { error = "Malformed empty HEIF cdsc reference."; return locator_result::malformed; }
                bool describes_primary = false;
                std::vector<uint32_t> reference_targets;
                for (uint16_t index = 0; index < reference_count; ++index) {
                    uint32_t to_item_id = 0;
                    if (iref_version == 0) {
                        if (rp + 2 > reference_end) { error = "Truncated HEIF cdsc reference."; return locator_result::malformed; }
                        to_item_id = read_be16u(input + rp); rp += 2;
                    } else {
                        if (rp + 4 > reference_end) { error = "Truncated HEIF cdsc reference."; return locator_result::malformed; }
                        to_item_id = read_be32u(input + rp); rp += 4;
                    }
                    if (std::find(reference_targets.begin(), reference_targets.end(), to_item_id) != reference_targets.end()) {
                        error = "Duplicate HEIF cdsc target in authoritative iref.";
                        return locator_result::malformed;
                    }
                    reference_targets.push_back(to_item_id);
                    if (std::find_if(items.begin(), items.end(), [&](const locator_item& item) { return item.id == to_item_id; }) == items.end() ||
                        std::find_if(items.begin(), items.end(), [&](const locator_item& item) { return item.id == from_item_id; }) == items.end() ||
                        to_item_id == from_item_id) {
                        error = "HEIF cdsc relation references an unknown or identical item.";
                        return locator_result::malformed;
                    }
                    describes_primary = describes_primary || to_item_id == primary_item_id;
                }
                if (rp != reference_end) { error = "HEIF cdsc reference has trailing bytes."; return locator_result::malformed; }
                if (describes_primary &&
                    std::find(target_item_ids.begin(), target_item_ids.end(), from_item_id) != target_item_ids.end()) {
                    if (reference_count != 1) {
                        error = "Ambiguous HEIF cdsc relation for the primary item.";
                        return locator_result::malformed;
                    }
                    if (std::find(primary_owned_candidates.begin(), primary_owned_candidates.end(), from_item_id) != primary_owned_candidates.end()) {
                        error = "Duplicate HEIF cdsc relation to the primary item.";
                        return locator_result::malformed;
                    }
                    primary_owned_candidates.push_back(from_item_id);
                }
            }
            if (primary_owned_candidates.size() != 1) {
                error = std::string("Ambiguous authoritative graph: ") + label +
                    " does not have exactly one cdsc owner relation to pitm.";
                return locator_result::malformed;
            }
            target_item_id = primary_owned_candidates.front();
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

    struct aux_range { uint32_t id{}; uint64_t offset{}; uint64_t length{}; };

    bool parse_auxiliary_graph(const uint8_t* input, size_t input_size,
        std::vector<lpb_auxiliary_item_facts>& output, std::string& error) {
        output.clear();
        std::vector<locator_box> top;
        if (!parse_children_strict(input, 0, input_size, top)) { error = "Malformed HEIF top-level box region."; return false; }
        const locator_box* meta = nullptr;
        for (const auto& b : top) if (is_type(b, "meta")) { if (meta) { error = "Duplicate HEIF meta box."; return false; } meta = &b; }
        if (!meta || meta->body_size < 4) { error = "Missing HEIF meta box."; return false; }
        const size_t meta_end = meta->start + meta->size;
        if (input[meta->body_start] != 0 || input[meta->body_start + 1] != 0 || input[meta->body_start + 2] != 0 || input[meta->body_start + 3] != 0) { error = "Unsupported HEIF meta version or flags."; return false; }
        std::vector<locator_box> children;
        if (!parse_children_strict(input, meta->body_start + 4, meta_end, children)) { error = "Malformed HEIF meta children."; return false; }
        const locator_box *pitm=nullptr,*iinf=nullptr,*iloc=nullptr,*iref=nullptr,*idat=nullptr,*iprp=nullptr;
        for (const auto& b : children) {
            const locator_box** slot = is_type(b,"pitm") ? &pitm : is_type(b,"iinf") ? &iinf : is_type(b,"iloc") ? &iloc : is_type(b,"iref") ? &iref : is_type(b,"idat") ? &idat : is_type(b,"iprp") ? &iprp : nullptr;
            if (slot) { if (*slot) { error = "Duplicate HEIF authoritative child box."; return false; } *slot = &b; }
        }
        if (!pitm || !iinf || !iloc || pitm->body_size < 6) { error = "HEIF primary/item location graph is incomplete."; return false; }
        uint32_t primary_id = 0;
        const uint8_t pitm_version = input[pitm->body_start];
        if (input[pitm->body_start+1] || input[pitm->body_start+2] || input[pitm->body_start+3] || pitm_version > 1) { error = "Malformed HEIF pitm box."; return false; }
        if (pitm_version == 0) primary_id = read_be16u(input + pitm->body_start + 4);
        else { if (pitm->body_size < 8) { error = "Malformed HEIF pitm item id."; return false; } primary_id = read_be32u(input + pitm->body_start + 4); }
        std::vector<locator_item> items; std::string iinf_error;
        if (!parse_iinf(input, *iinf, items, &iinf_error)) { error = "Malformed HEIF iinf: " + iinf_error; return false; }
        bool primary_exists = false; for (const auto& item : items) if (item.id == primary_id) primary_exists = true;
        if (!primary_exists) { error = "HEIF pitm references an unknown item."; return false; }

        std::vector<auxiliary_property> auxiliary_properties;
        if (iprp && !parse_heif_auxiliary_properties(input, *iprp, items, auxiliary_properties, error)) {
            return false;
        }

        binary_reader reader(input, input_size);
        if (iloc->body_size < 8 || !reader.try_seek(iloc->body_start)) { error = "Malformed HEIF iloc header."; return false; }
        uint8_t version=0,f0=0,f1=0,f2=0,b1=0,b2=0;
        if (!reader.try_read_u8(version)||!reader.try_read_u8(f0)||!reader.try_read_u8(f1)||!reader.try_read_u8(f2)||!reader.try_read_u8(b1)||!reader.try_read_u8(b2) || version>2 || f0||f1||f2) { error = "Malformed HEIF iloc header."; return false; }
        const uint8_t offset_size=(b1>>4)&15, length_size=b1&15, base_size=(b2>>4)&15, index_size=(version?b2&15:0);
        const auto valid_size=[](uint8_t x){return x==0||x==4||x==8;}; if(!valid_size(offset_size)||!valid_size(length_size)||!valid_size(base_size)||!valid_size(index_size)){error="Unsupported HEIF iloc field size.";return false;}
        uint32_t item_count=0; if(version<2){uint16_t n=0;if(!reader.try_read_be16u(n)){error="Malformed HEIF iloc item count.";return false;}item_count=n;}else if(!reader.try_read_be32u(item_count)){error="Malformed HEIF iloc item count.";return false;}
        std::vector<aux_range> ranges;
        std::vector<uint32_t> located_items;
        bool primary_has_location = false;
        for(uint32_t i=0;i<item_count;++i){
            uint32_t id=0; if(version<2){uint16_t n=0;if(!reader.try_read_be16u(n)){error="Truncated HEIF iloc item id.";return false;}id=n;}else if(!reader.try_read_be32u(id)){error="Truncated HEIF iloc item id.";return false;}
            if (id == 0 ||
                std::find_if(items.begin(), items.end(), [&](const locator_item& item) { return item.id == id; }) == items.end() ||
                std::find(located_items.begin(), located_items.end(), id) != located_items.end()) {
                error = "HEIF iloc references a duplicate or unknown item.";
                return false;
            }
            located_items.push_back(id);
            uint16_t method=0,ref=0; if(version&&(!reader.try_read_be16u(method)||(method&0xFFF0u)||method>1)){error="Unsupported HEIF iloc construction method.";return false;} if(!reader.try_read_be16u(ref)||ref!=0){error="Unsupported HEIF iloc data reference.";return false;}
            uint64_t base=0;if(base_size&&!read_uint_local(reader,base_size,base)){error="Truncated HEIF iloc base offset.";return false;} uint16_t count=0;if(!reader.try_read_be16u(count)){error="Truncated HEIF iloc extent count.";return false;}
            const uint64_t owner_base=method==1?(idat?idat->body_start:0):0; const uint64_t owner_size=method==1?(idat?idat->body_size:0):input_size; if((method==1&&!idat)||base>owner_size){error="HEIF iloc extent owner is invalid.";return false;}
            uint64_t first_offset=0,total_length=0; bool have_range=false;
            for(uint16_t j=0;j<count;++j){ uint64_t index=0,eo=0,el=0; if(version&&index_size&&!read_uint_local(reader,index_size,index)){error="Truncated HEIF iloc extent index.";return false;} if(offset_size&&!read_uint_local(reader,offset_size,eo)){error="Truncated HEIF iloc extent offset.";return false;} if(length_size&&!read_uint_local(reader,length_size,el)){error="Truncated HEIF iloc extent length.";return false;} if(el==0||eo>owner_size-base||el>owner_size-base-eo){error="HEIF iloc extent is empty or out of bounds.";return false;} const uint64_t absolute=owner_base+base+eo; if(absolute>input_size||el>input_size-absolute){error="HEIF iloc absolute extent is out of bounds.";return false;} if(!have_range){first_offset=absolute;total_length=el;have_range=true;} else { if(absolute!=first_offset+total_length){error="HEIF iloc multi-extent item is non-contiguous.";return false;} if(total_length>std::numeric_limits<uint64_t>::max()-el){error="HEIF iloc extent length overflow.";return false;} total_length+=el; } }
            if (id == primary_id) primary_has_location = have_range;
            if (have_range && count == 1) ranges.push_back({id,first_offset,total_length});
        }
        if(reader.position()!=iloc->start+iloc->size){error="HEIF iloc has trailing bytes.";return false;}
        if (!primary_has_location) {
            error = "HEIF primary item has no valid iloc extent.";
            return false;
        }
        if(!iref) {
            return true; // A valid primary-only HEIF has no auxiliary relation.
        }
        if(iref->body_size<4) {error="Malformed HEIF iref box.";return false;}
        std::vector<locator_box> refs; if(!parse_children_strict(input,iref->body_start+4,iref->start+iref->size,refs)){error="Malformed HEIF iref children.";return false;}
        std::vector<uint32_t> seen;
        for(const auto& refbox:refs){
            if(!is_type(refbox,"auxl")) continue;
            size_t p=refbox.body_start;
            const size_t end=refbox.start+refbox.size;
            uint8_t rv=input[iref->body_start];
            uint32_t from=0;
            if(rv==0){if(p+6>end){error="Truncated HEIF auxl reference.";return false;}from=read_be16u(input+p);p+=2;}
            else{if(p+8>end){error="Truncated HEIF auxl reference.";return false;}from=read_be32u(input+p);p+=4;}
            uint16_t count=0;if(p+2>end){error="Truncated HEIF auxl reference count.";return false;}count=read_be16u(input+p);p+=2;
            if(count==0){error="Empty HEIF auxl reference.";return false;}
            for(uint16_t i=0;i<count;++i){
                uint32_t to=0;
                if(rv==0){if(p+2>end){error="Truncated HEIF auxl target.";return false;}to=read_be16u(input+p);p+=2;}
                else{if(p+4>end){error="Truncated HEIF auxl target.";return false;}to=read_be32u(input+p);p+=4;}
                // HEIF auxl is normally authored from the auxiliary item to
                // its master (Apple's real files use this orientation).  A
                // few producers emit the inverse relation; accept either
                // only when exactly one endpoint is the validated primary.
                uint32_t aux_id=0;
                if (to==primary_id && from!=primary_id) aux_id=from;
                else if (from==primary_id && to!=primary_id) aux_id=to;
                else {
                    // A HEIF may contain valid auxiliary relationships for a
                    // non-primary derived/master item.  Validate both graph
                    // endpoints, but do not publish that unrelated relation
                    // as an auxiliary of pitm.
                    const auto from_item = std::find_if(items.begin(), items.end(),
                        [&](const locator_item& item) { return item.id == from; });
                    const auto to_item = std::find_if(items.begin(), items.end(),
                        [&](const locator_item& item) { return item.id == to; });
                    if (from == to || from_item == items.end() || to_item == items.end()) {
                        error="HEIF auxl relation references an unknown or identical endpoint.";
                        return false;
                    }
                    continue;
                }
                if(std::find(seen.begin(),seen.end(),aux_id)!=seen.end()){error="Duplicate HEIF auxiliary relationship.";return false;}
                seen.push_back(aux_id);
                auto ri=std::find_if(ranges.begin(),ranges.end(),[&](const aux_range& r){return r.id==aux_id;});
                auto ii=std::find_if(items.begin(),items.end(),[&](const locator_item& x){return x.id==aux_id;});
                if(ri==ranges.end()||ii==items.end()){error="HEIF auxiliary item has no single contiguous iloc extent.";return false;}
                const auto property = std::find_if(auxiliary_properties.begin(), auxiliary_properties.end(),
                    [&](const auxiliary_property& value) { return value.item_id == aux_id; });
                const char* relationship = property == auxiliary_properties.end()
                    ? "auxl" : property->type.c_str();
                if (std::strlen(relationship) >= sizeof(lpb_auxiliary_item_facts::relationship)) {
                    error = "HEIF auxiliary relationship exceeds the source-facts ABI capacity.";
                    return false;
                }
                for (const auto& prior : output) {
                    const uint64_t prior_end = prior.file_range.offset + prior.file_range.length;
                    const uint64_t current_end = ri->offset + ri->length;
                    if (ri->offset < prior_end && prior.file_range.offset < current_end) {
                        error = "HEIF auxiliary item ranges overlap.";
                        return false;
                    }
                }
                lpb_auxiliary_item_facts f{};f.struct_size=sizeof(f);f.is_present=1;f.container=LPB_IMAGE_CONTAINER_HEIC;f.representation=LPB_AUX_REPRESENTATION_EMBEDDED;f.ownership=LPB_AUX_OWNER_AUXILIARY;f.item_id=aux_id;f.file_range={ri->offset,ri->length};strncpy_s(f.relationship,relationship,_TRUNCATE);output.push_back(f);
            }
            if(p!=end){error="HEIF auxl reference has trailing bytes.";return false;}
        }
        return true;
    }
}

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_enumerate_auxiliary_items(
    lpb_context* context, const uint8_t* input, size_t input_size,
    lpb_auxiliary_item_facts* output_items, size_t output_capacity, size_t* output_count)
{
    if (!context || !input || !output_count || (output_capacity > 0 && !output_items)) return LPB_RESULT_INVALID_ARGUMENT;
    try {
        std::vector<lpb_auxiliary_item_facts> items; std::string error;
        if (!parse_auxiliary_graph(input, input_size, items, error)) { set_error(context, error.c_str()); return LPB_RESULT_INVALID_ARGUMENT; }
        *output_count = items.size();
        if (output_capacity < items.size()) return LPB_RESULT_BUFFER_TOO_SMALL;
        if (!items.empty()) std::memcpy(output_items, items.data(), items.size() * sizeof(lpb_auxiliary_item_facts));
        return LPB_RESULT_OK;
    } catch (const std::exception& ex) { set_error(context, ex.what()); return LPB_RESULT_INTERNAL_ERROR; }
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

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_enumerate_xmp_items(
    lpb_context* context,
    const uint8_t* input,
    size_t input_size,
    lpb_media_range* output_items,
    size_t output_capacity,
    size_t* output_count)
{
    if (context == nullptr || input == nullptr || output_count == nullptr ||
        (output_capacity != 0 && output_items == nullptr)) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    try {
        // First obtain the authoritative item ids from the same strict iinf
        // hierarchy used by the single-item locator.  The Google HEIF
        // corpus legitimately contains several RDF items (Apple matte/HDR
        // metadata and a Google MotionPhoto packet); selecting the first one
        // would make the protocol depend on iinf ordering.
        std::vector<locator_box> top_boxes;
        std::string error;
        if (!parse_children_strict(input, 0, input_size, top_boxes)) {
            set_error(context, "Malformed top-level HEIF box region.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const locator_box* meta = nullptr;
        for (const auto& box : top_boxes) {
            if (!is_type(box, "meta")) continue;
            if (meta != nullptr) {
                set_error(context, "Duplicate authoritative meta box.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            meta = &box;
        }
        if (meta == nullptr || meta->body_size < 4) {
            set_error(context, "Missing or malformed meta box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const uint8_t* body = input + meta->body_start;
        if (body[0] != 0 || body[1] != 0 || body[2] != 0 || body[3] != 0) {
            set_error(context, "Unsupported meta FullBox version or flags.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        std::vector<locator_box> children;
        if (!parse_children_strict(input, meta->body_start + 4, meta->start + meta->size, children)) {
            set_error(context, "Malformed meta child box region.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const locator_box* iinf = nullptr;
        const locator_box* iloc = nullptr;
        for (const auto& box : children) {
            if (is_type(box, "iinf")) {
                if (iinf != nullptr) { set_error(context, "Duplicate authoritative iinf box."); return LPB_RESULT_INVALID_ARGUMENT; }
                iinf = &box;
            } else if (is_type(box, "iloc")) {
                if (iloc != nullptr) { set_error(context, "Duplicate authoritative iloc box."); return LPB_RESULT_INVALID_ARGUMENT; }
                iloc = &box;
            }
        }
        if (iinf == nullptr || iloc == nullptr) {
            set_error(context, "Missing authoritative iinf or iloc box.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        std::vector<locator_item> items;
        std::string iinf_failure;
        if (!parse_iinf(input, *iinf, items, &iinf_failure)) {
            set_error(context, (std::string("Malformed or unsupported iinf/infe structure: ") + iinf_failure).c_str());
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        std::vector<lpb_media_range> ranges;
        for (const auto& item : items) {
            if (item.type != 0x6D696D65u || item.content_type.rfind("application/rdf+xml", 0) != 0) continue;
            lpb_media_range range{};
            const locator_result located = locate_item(input, input_size, 0x6D696D65u, "XMP",
                &range.offset, &range.length, error, item.id);
            if (located == locator_result::malformed) {
                set_error(context, error.c_str());
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (located == locator_result::present) ranges.push_back(range);
        }

        *output_count = ranges.size();
        if (output_capacity < ranges.size()) return LPB_RESULT_BUFFER_TOO_SMALL;
        if (!ranges.empty()) std::memcpy(output_items, ranges.data(), ranges.size() * sizeof(lpb_media_range));
        return LPB_RESULT_OK;
    } catch (const std::exception& ex) {
        set_error(context, ex.what());
        return LPB_RESULT_INVALID_ARGUMENT;
    }
}
