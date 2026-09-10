#include "foundation/internal.h"
#include "foundation/sha256.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include <algorithm>
#include <cstring>
#include <functional>
#include <limits>
#include <map>
#include <set>
#include <string>
#include <string_view>
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

    struct heif_extent { uint64_t offset{}; uint64_t length{}; };

    struct heif_item_location {
        uint32_t id{};
        uint32_t type{};
        std::string content_type;
        std::vector<heif_extent> extents;
        heif_extent contiguous{};
    };

    struct heif_reference {
        std::string type;
        uint32_t from{};
        std::vector<uint32_t> targets;
    };

    std::string item_type_string(uint32_t type) {
        std::string value(4, '\0');
        value[0] = static_cast<char>((type >> 24) & 0xFFu);
        value[1] = static_cast<char>((type >> 16) & 0xFFu);
        value[2] = static_cast<char>((type >> 8) & 0xFFu);
        value[3] = static_cast<char>(type & 0xFFu);
        return value;
    }

    const heif_item_location* find_location(
        const std::vector<heif_item_location>& locations, uint32_t id) {
        const auto it = std::find_if(locations.begin(), locations.end(),
            [id](const heif_item_location& value) { return value.id == id; });
        return it == locations.end() ? nullptr : &*it;
    }

    const locator_item* find_item(const std::vector<locator_item>& items, uint32_t id) {
        const auto it = std::find_if(items.begin(), items.end(),
            [id](const locator_item& value) { return value.id == id; });
        return it == items.end() ? nullptr : &*it;
    }

    bool is_supported_derived_type(uint32_t type) noexcept {
        return type == 0x67726964u /* grid */ || type == 0x746D6170u /* tmap */;
    }

    bool parse_auxiliary_graph(const uint8_t* input, size_t input_size,
        std::vector<lpb_auxiliary_item_facts>& output, std::string& error) {
        output.clear();
        std::vector<locator_box> top;
        if (!parse_children_strict(input, 0, input_size, top)) {
            error = "Malformed HEIF top-level box region.";
            return false;
        }

        const locator_box* meta = nullptr;
        for (const auto& box : top) {
            if (!is_type(box, "meta")) continue;
            if (meta != nullptr) { error = "Duplicate HEIF meta box."; return false; }
            meta = &box;
        }
        if (meta == nullptr || meta->body_size < 4) {
            error = "Missing HEIF meta box.";
            return false;
        }
        if (input[meta->body_start] != 0 || input[meta->body_start + 1] != 0 ||
            input[meta->body_start + 2] != 0 || input[meta->body_start + 3] != 0) {
            error = "Unsupported HEIF meta version or flags.";
            return false;
        }

        std::vector<locator_box> children;
        if (!parse_children_strict(input, meta->body_start + 4, meta->start + meta->size, children)) {
            error = "Malformed HEIF meta children.";
            return false;
        }
        const locator_box *pitm = nullptr, *iinf = nullptr, *iloc = nullptr,
            *iref = nullptr, *idat = nullptr, *iprp = nullptr;
        for (const auto& box : children) {
            const locator_box** slot =
                is_type(box, "pitm") ? &pitm :
                is_type(box, "iinf") ? &iinf :
                is_type(box, "iloc") ? &iloc :
                is_type(box, "iref") ? &iref :
                is_type(box, "idat") ? &idat :
                is_type(box, "iprp") ? &iprp : nullptr;
            if (slot != nullptr) {
                if (*slot != nullptr) { error = "Duplicate HEIF authoritative child box."; return false; }
                *slot = &box;
            }
        }
        if (pitm == nullptr || iinf == nullptr || iloc == nullptr || pitm->body_size < 6) {
            error = "HEIF primary/item location graph is incomplete.";
            return false;
        }

        const uint8_t pitm_version = input[pitm->body_start];
        if (pitm_version > 1 || input[pitm->body_start + 1] != 0 ||
            input[pitm->body_start + 2] != 0 || input[pitm->body_start + 3] != 0) {
            error = "Malformed HEIF pitm box.";
            return false;
        }
        uint32_t primary_id = 0;
        if (pitm_version == 0) {
            primary_id = read_be16u(input + pitm->body_start + 4);
        } else {
            if (pitm->body_size < 8) { error = "Malformed HEIF pitm item id."; return false; }
            primary_id = read_be32u(input + pitm->body_start + 4);
        }

        std::vector<locator_item> items;
        std::string iinf_error;
        if (!parse_iinf(input, *iinf, items, &iinf_error)) {
            error = "Malformed HEIF iinf: " + iinf_error;
            return false;
        }
        if (find_item(items, primary_id) == nullptr) {
            error = "HEIF pitm references an unknown item.";
            return false;
        }

        std::vector<auxiliary_property> auxiliary_properties;
        if (iprp != nullptr && !parse_heif_auxiliary_properties(
            input, *iprp, items, auxiliary_properties, error)) {
            return false;
        }

        binary_reader reader(input, input_size);
        if (iloc->body_size < 8 || !reader.try_seek(iloc->body_start)) {
            error = "Malformed HEIF iloc header.";
            return false;
        }
        uint8_t version = 0, flags0 = 0, flags1 = 0, flags2 = 0, byte1 = 0, byte2 = 0;
        if (!reader.try_read_u8(version) || !reader.try_read_u8(flags0) ||
            !reader.try_read_u8(flags1) || !reader.try_read_u8(flags2) ||
            !reader.try_read_u8(byte1) || !reader.try_read_u8(byte2) ||
            version > 2 || flags0 != 0 || flags1 != 0 || flags2 != 0) {
            error = "Malformed HEIF iloc header.";
            return false;
        }
        const uint8_t offset_size = static_cast<uint8_t>((byte1 >> 4) & 0x0F);
        const uint8_t length_size = static_cast<uint8_t>(byte1 & 0x0F);
        const uint8_t base_size = static_cast<uint8_t>((byte2 >> 4) & 0x0F);
        const uint8_t index_size = version == 0 ? 0 : static_cast<uint8_t>(byte2 & 0x0F);
        const auto valid_field_size = [](uint8_t value) noexcept {
            return value == 0 || value == 4 || value == 8;
        };
        if (!valid_field_size(offset_size) || !valid_field_size(length_size) ||
            !valid_field_size(base_size) || !valid_field_size(index_size)) {
            error = "Unsupported HEIF iloc field size.";
            return false;
        }

        uint32_t item_count = 0;
        if (version < 2) {
            uint16_t count16 = 0;
            if (!reader.try_read_be16u(count16)) { error = "Malformed HEIF iloc item count."; return false; }
            item_count = count16;
        } else if (!reader.try_read_be32u(item_count)) {
            error = "Malformed HEIF iloc item count.";
            return false;
        }

        std::vector<heif_item_location> locations;
        locations.reserve(item_count);
        std::set<uint32_t> located_ids;
        std::vector<heif_extent> all_extents;
        for (uint32_t index = 0; index < item_count; ++index) {
            uint32_t item_id = 0;
            if (version < 2) {
                uint16_t id16 = 0;
                if (!reader.try_read_be16u(id16)) { error = "Truncated HEIF iloc item id."; return false; }
                item_id = id16;
            } else if (!reader.try_read_be32u(item_id)) {
                error = "Truncated HEIF iloc item id.";
                return false;
            }
            if (item_id == 0 || find_item(items, item_id) == nullptr || !located_ids.insert(item_id).second) {
                error = "HEIF iloc references a duplicate or unknown item.";
                return false;
            }

            uint16_t construction_method = 0;
            if (version != 0) {
                if (!reader.try_read_be16u(construction_method) || construction_method > 1) {
                    error = "Unsupported HEIF iloc construction method.";
                    return false;
                }
            }
            uint16_t data_reference_index = 0;
            if (!reader.try_read_be16u(data_reference_index) || data_reference_index != 0) {
                error = "Unsupported HEIF iloc data reference.";
                return false;
            }
            uint64_t base_offset = 0;
            if (base_size != 0 && !read_uint_local(reader, base_size, base_offset)) {
                error = "Truncated HEIF iloc base offset.";
                return false;
            }
            uint16_t extent_count = 0;
            if (!reader.try_read_be16u(extent_count) || extent_count == 0) {
                error = "HEIF iloc item has no extents.";
                return false;
            }

            const uint64_t owner_base = construction_method == 1
                ? (idat == nullptr ? 0 : static_cast<uint64_t>(idat->body_start)) : 0;
            const uint64_t owner_size = construction_method == 1
                ? (idat == nullptr ? 0 : static_cast<uint64_t>(idat->body_size))
                : static_cast<uint64_t>(input_size);
            if (construction_method == 1 && idat == nullptr) {
                error = "HEIF iloc construction method 1 has no idat owner.";
                return false;
            }
            if (base_offset > owner_size) {
                error = "HEIF iloc extent owner is invalid.";
                return false;
            }

            heif_item_location location;
            location.id = item_id;
            const locator_item* item = find_item(items, item_id);
            location.type = item->type;
            location.content_type = item->content_type;
            uint64_t total_length = 0;
            for (uint16_t extent_index = 0; extent_index < extent_count; ++extent_index) {
                uint64_t ignored_index = 0, extent_offset = 0, extent_length = 0;
                if (version != 0 && index_size != 0 &&
                    !read_uint_local(reader, index_size, ignored_index)) {
                    error = "Truncated HEIF iloc extent index.";
                    return false;
                }
                if (offset_size != 0 && !read_uint_local(reader, offset_size, extent_offset)) {
                    error = "Truncated HEIF iloc extent offset.";
                    return false;
                }
                if (length_size != 0 && !read_uint_local(reader, length_size, extent_length)) {
                    error = "Truncated HEIF iloc extent length.";
                    return false;
                }
                if (extent_length == 0 || extent_offset > owner_size - base_offset ||
                    extent_length > owner_size - base_offset - extent_offset) {
                    error = "HEIF iloc extent is empty or out of bounds.";
                    return false;
                }
                const uint64_t absolute = owner_base + base_offset + extent_offset;
                if (absolute > input_size || extent_length > input_size - absolute) {
                    error = "HEIF iloc absolute extent is out of bounds.";
                    return false;
                }
                location.extents.push_back({ absolute, extent_length });
                all_extents.push_back({ absolute, extent_length });
                if (total_length > std::numeric_limits<uint64_t>::max() - extent_length) {
                    error = "HEIF iloc extent length overflow.";
                    return false;
                }
                total_length += extent_length;
            }
            if (location.extents.size() == 1) {
                location.contiguous = location.extents.front();
            } else {
                const heif_extent first = location.extents.front();
                uint64_t expected = first.offset;
                for (const heif_extent extent : location.extents) {
                    if (extent.offset != expected || expected > std::numeric_limits<uint64_t>::max() - extent.length) {
                        error = "HEIF iloc multi-extent item is non-contiguous.";
                        return false;
                    }
                    expected += extent.length;
                }
                location.contiguous = { first.offset, total_length };
            }
            locations.push_back(std::move(location));
        }
        if (reader.position() != iloc->start + iloc->size) {
            error = "HEIF iloc has trailing bytes.";
            return false;
        }
        if (find_location(locations, primary_id) == nullptr) {
            error = "HEIF primary item has no valid iloc extent.";
            return false;
        }

        std::sort(all_extents.begin(), all_extents.end(),
            [](const heif_extent& left, const heif_extent& right) {
                return left.offset < right.offset;
            });
        for (size_t i = 1; i < all_extents.size(); ++i) {
            const uint64_t previous_end = all_extents[i - 1].offset + all_extents[i - 1].length;
            if (all_extents[i].offset < previous_end) {
                error = "HEIF iloc extents overlap.";
                return false;
            }
        }

        std::vector<heif_reference> references;
        if (iref != nullptr) {
            if (iref->body_size < 4) { error = "Malformed HEIF iref box."; return false; }
            const uint8_t reference_version = input[iref->body_start];
            if (reference_version > 1 || input[iref->body_start + 1] != 0 ||
                input[iref->body_start + 2] != 0 || input[iref->body_start + 3] != 0) {
                error = "Unsupported HEIF iref version or flags.";
                return false;
            }
            std::vector<locator_box> reference_boxes;
            if (!parse_children_strict(input, iref->body_start + 4, iref->start + iref->size, reference_boxes)) {
                error = "Malformed HEIF iref children.";
                return false;
            }
            for (const locator_box& reference_box : reference_boxes) {
                if (!is_type(reference_box, "dimg") && !is_type(reference_box, "auxl") &&
                    !is_type(reference_box, "thmb") && !is_type(reference_box, "cdsc")) {
                    error = "Unsupported HEIF relationship type.";
                    return false;
                }
                size_t position = reference_box.body_start;
                const size_t end = reference_box.start + reference_box.size;
                const size_t id_width = reference_version == 0 ? 2 : 4;
                if (position + id_width + 2 > end) {
                    error = "Truncated HEIF relationship reference.";
                    return false;
                }
                auto read_id = [&](uint32_t& value) {
                    if (position + id_width > end) return false;
                    value = id_width == 2 ? read_be16u(input + position) : read_be32u(input + position);
                    position += id_width;
                    return true;
                };
                heif_reference reference;
                reference.type.assign(reference_box.type, 4);
                if (!read_id(reference.from)) { error = "Truncated HEIF relationship source."; return false; }
                uint16_t count = read_be16u(input + position);
                position += 2;
                if (count == 0 || find_item(items, reference.from) == nullptr) {
                    error = "HEIF relationship has an unknown or empty source.";
                    return false;
                }
                std::set<uint32_t> relation_targets;
                for (uint16_t i = 0; i < count; ++i) {
                    uint32_t target = 0;
                    if (!read_id(target)) { error = "Truncated HEIF relationship target."; return false; }
                    if (find_item(items, target) == nullptr || target == reference.from) {
                        error = "HEIF relationship references a missing or identical item.";
                        return false;
                    }
                    if (!relation_targets.insert(target).second) {
                        error = "Duplicate HEIF relationship dependency edge.";
                        return false;
                    }
                    reference.targets.push_back(target);
                }
                if (position != end) {
                    error = "HEIF relationship has trailing bytes.";
                    return false;
                }
                references.push_back(std::move(reference));
            }
        }

        std::map<uint32_t, std::vector<uint32_t>> dimg_dependencies;
        std::map<uint32_t, std::vector<uint32_t>> primary_auxiliary_owners;
        std::set<uint32_t> primary_owned_auxiliary;
        std::set<std::string> primary_relationships;
        for (const heif_reference& reference : references) {
            const locator_item* source_item = find_item(items, reference.from);
            if (reference.type == "dimg") {
                if (source_item == nullptr || !is_supported_derived_type(source_item->type)) {
                    error = "Unsupported HEIF derived item type in dimg relationship.";
                    return false;
                }
                if (!dimg_dependencies.emplace(reference.from, reference.targets).second) {
                    error = "Shadow HEIF dimg relationship for derived item.";
                    return false;
                }
            } else if (reference.type == "auxl") {
                uint32_t auxiliary_id = 0;
                if (reference.from == primary_id && reference.targets.size() == 1) {
                    auxiliary_id = reference.targets.front();
                } else if (reference.targets.size() == 1 && reference.targets.front() == primary_id) {
                    auxiliary_id = reference.from;
                } else if (reference.from == primary_id ||
                    std::find(reference.targets.begin(), reference.targets.end(), primary_id) != reference.targets.end()) {
                    error = "Shadow or ambiguous HEIF auxiliary relationship to primary.";
                    return false;
                }
                if (auxiliary_id != 0) {
                    if (!primary_owned_auxiliary.insert(auxiliary_id).second) {
                        error = "Shadow HEIF auxiliary relationship has multiple primary owners.";
                        return false;
                    }
                    const auto property = std::find_if(auxiliary_properties.begin(), auxiliary_properties.end(),
                        [auxiliary_id](const auxiliary_property& value) { return value.item_id == auxiliary_id; });
                    const std::string relationship = property == auxiliary_properties.end()
                        ? "auxl" : property->type;
                    if (!primary_relationships.insert(relationship).second) {
                        error = "Shadow HEIF auxiliary relationship is not unique.";
                        return false;
                    }
                    primary_auxiliary_owners[auxiliary_id].push_back(primary_id);
                }
            }
        }

        std::map<uint32_t, uint8_t> visit_state;
        std::map<uint32_t, std::vector<uint32_t>> transitive_dependencies;
        std::function<bool(uint32_t)> visit = [&](uint32_t item_id) {
            uint8_t& state = visit_state[item_id];
            if (state == 1) { error = "HEIF dimg dependency cycle detected."; return false; }
            if (state == 2) return true;
            state = 1;
            const locator_item* item = find_item(items, item_id);
            if (item == nullptr || find_location(locations, item_id) == nullptr) {
                error = "HEIF dimg dependency references a missing tile/item.";
                return false;
            }
            const auto dependency_it = dimg_dependencies.find(item_id);
            if (dependency_it != dimg_dependencies.end()) {
                if (!is_supported_derived_type(item->type)) {
                    error = "Unsupported HEIF derived item type.";
                    return false;
                }
                auto& flattened = transitive_dependencies[item_id];
                for (uint32_t dependency_id : dependency_it->second) {
                    if (std::find(flattened.begin(), flattened.end(), dependency_id) != flattened.end()) {
                        error = "Duplicate HEIF dimg dependency edge.";
                        return false;
                    }
                    if (!visit(dependency_id)) return false;
                    flattened.push_back(dependency_id);
                    const auto child_it = transitive_dependencies.find(dependency_id);
                    if (child_it != transitive_dependencies.end()) {
                        for (uint32_t nested_id : child_it->second) {
                            if (std::find(flattened.begin(), flattened.end(), nested_id) != flattened.end()) {
                                error = "Duplicate HEIF dimg dependency edge.";
                                return false;
                            }
                            flattened.push_back(nested_id);
                        }
                    }
                }
                if (flattened.size() > LPB_HEIF_MAX_DEPENDENCIES) {
                    error = "HEIF derived dependency graph exceeds the supported capacity.";
                    return false;
                }
            }
            state = 2;
            return true;
        };

        for (const auto& dependency : dimg_dependencies) {
            if (!visit(dependency.first)) return false;
        }

        for (uint32_t auxiliary_id : primary_owned_auxiliary) {
            const locator_item* item = find_item(items, auxiliary_id);
            const heif_item_location* location = find_location(locations, auxiliary_id);
            if (item == nullptr || location == nullptr) {
                error = "HEIF auxiliary item has a missing iloc range.";
                return false;
            }
            const auto dependency_it = dimg_dependencies.find(auxiliary_id);
            const std::vector<uint32_t>* dependencies = dependency_it == dimg_dependencies.end()
                ? nullptr : &dependency_it->second;
            const auto property = std::find_if(auxiliary_properties.begin(), auxiliary_properties.end(),
                [auxiliary_id](const auxiliary_property& value) { return value.item_id == auxiliary_id; });
            const std::string relationship = property == auxiliary_properties.end()
                ? "auxl" : property->type;
            if (relationship.size() >= sizeof(lpb_auxiliary_item_facts::relationship)) {
                error = "HEIF auxiliary relationship exceeds the source-facts ABI capacity.";
                return false;
            }
            lpb_auxiliary_item_facts facts{};
            facts.struct_size = sizeof(facts);
            facts.is_present = 1;
            facts.container = LPB_IMAGE_CONTAINER_HEIC;
            facts.representation = LPB_AUX_REPRESENTATION_EMBEDDED;
            facts.ownership = LPB_AUX_OWNER_PRIMARY;
            facts.item_id = auxiliary_id;
            facts.file_range = { location->contiguous.offset, location->contiguous.length };
            facts.codec = item->type == 0x6D696D65u && item->content_type.find("jpeg") != std::string::npos
                ? LPB_AUX_CODEC_JPEG : LPB_AUX_CODEC_HEVC;
            facts.source_index = 0;
            lpb::crypto::sha256_buffer(input + static_cast<size_t>(location->contiguous.offset),
                static_cast<size_t>(location->contiguous.length), facts.sha256);
            const std::string item_type = item_type_string(item->type);
            const std::string stable_identity = "heif:item:" + std::to_string(auxiliary_id);
            strncpy_s(facts.item_type, item_type.c_str(), _TRUNCATE);
            strncpy_s(facts.stable_identity, stable_identity.c_str(), _TRUNCATE);
            strncpy_s(facts.owner_identity, "primary:0", _TRUNCATE);
            strncpy_s(facts.relationship, relationship.c_str(), _TRUNCATE);
            strncpy_s(facts.semantic,
                relationship == "urn:com:apple:photo:2020:aux:hdrgainmap" ||
                relationship == "urn:com:samsung:photo:2024:aux:hdrgainmap" ? "GainMap" : "Auxiliary",
                _TRUNCATE);
            facts.graph_flags = LPB_HEIF_GRAPH_COMPLETE;
            if (dependencies != nullptr) {
                facts.graph_flags |= LPB_HEIF_GRAPH_DERIVED | LPB_HEIF_GRAPH_SUPPORTED_DERIVED;
                facts.dependency_count = static_cast<uint32_t>(dependencies->size());
                for (size_t i = 0; i < dependencies->size(); ++i) {
                    const heif_item_location* dependency_location = find_location(locations, (*dependencies)[i]);
                    const locator_item* dependency_item = find_item(items, (*dependencies)[i]);
                    if (dependency_location == nullptr || dependency_item == nullptr) {
                        error = "HEIF dimg dependency references a missing tile/item.";
                        return false;
                    }
                    facts.dependency_item_ids[i] = (*dependencies)[i];
                    facts.dependency_offsets[i] = dependency_location->contiguous.offset;
                    facts.dependency_lengths[i] = dependency_location->contiguous.length;
                    strncpy_s(facts.dependency_item_types[i], item_type_string(dependency_item->type).c_str(), _TRUNCATE);
                }
            }
            output.push_back(facts);
            if (output.size() > 8) {
                error = "HEIF auxiliary item count exceeds the source-facts ABI capacity.";
                return false;
            }
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
