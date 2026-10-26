#include "foundation/portable_internal.h"
#include "foundation/sha256_core.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include "containers/heif_internal.h"
#include <array>
#include <algorithm>
#include <cstring>
#include <functional>
#include <limits>
#include <map>
#include <new>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

using namespace lpb;
using lpb::containers::heif_graph_error;

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
        std::string& error, heif_graph_error& failure) {
        auxc_items.clear();
        if (iprp.body_size < 8) { error = "HEIF iprp box is truncated."; return false; }
        std::vector<locator_box> children;
        if (!parse_children_strict(data, iprp.body_start, iprp.start + iprp.size, children)) {
            error = "Malformed HEIF iprp children."; return false;
        }
        const locator_box* ipco = nullptr; const locator_box* ipma = nullptr;
        for (const auto& child : children) {
            if (is_type(child, "ipco")) { if (ipco) { failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF ipco box."; return false; } ipco = &child; }
            else if (is_type(child, "ipma")) { if (ipma) { failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF ipma box."; return false; } ipma = &child; }
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
        if (version > 1 || (flags & ~1u) != 0) { failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF ipma version or flags."; return false; }
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
            if (version == 0) { if (p + 2 > end) { error = "Truncated HEIF ipma field."; return false; } item_id = read_be16u(data + p); p += 2; }
            else { if (p + 4 > end) { error = "Truncated HEIF ipma field."; return false; } item_id = read_be32u(data + p); p += 4; }
            if (item_id == 0 || std::find_if(items.begin(), items.end(), [&](const locator_item& item) { return item.id == item_id; }) == items.end()) {
                failure = heif_graph_error::missing_reference;
                error = "HEIF ipma references an unknown item."; return false;
            }
            if (std::find(seen_items.begin(), seen_items.end(), item_id) != seen_items.end()) {
                failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF ipma item."; return false;
            }
            if (p >= end) { error = "Truncated HEIF ipma association count."; return false; }
            seen_items.push_back(item_id);
            const uint8_t association_count = data[p++];
            std::vector<uint32_t> seen_properties;
            for (uint8_t a = 0; a < association_count; ++a) {
                uint32_t association = 0;
                if (flags & 1u) { if (p + 2 > end) { error = "Truncated HEIF ipma field."; return false; } association = read_be16u(data + p) & 0x7FFFu; p += 2; }
                else { if (p >= end) { error = "Truncated HEIF ipma association."; return false; } association = data[p++] & 0x7Fu; }
                if (association == 0 || association >= auxiliary_types.size()) {
                    error = "HEIF ipma has an invalid property association."; return false;
                }
                if (std::find(seen_properties.begin(), seen_properties.end(), association) != seen_properties.end()) {
                    failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF ipma property association."; return false;
                }
                seen_properties.push_back(association);
                if (!auxiliary_types[association].empty()) {
                    const auto prior = std::find_if(auxc_items.begin(), auxc_items.end(),
                        [&](const auxiliary_property& value) { return value.item_id == item_id; });
                    if (prior != auxc_items.end()) {
                        failure = heif_graph_error::duplicate_identity; error = "HEIF item has multiple auxiliary type properties.";
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
        std::vector<locator_item>& out_items, std::string* failure = nullptr,
        heif_graph_error* graph_failure = nullptr) {
        const auto fail = [&](const char* reason, heif_graph_error code = heif_graph_error::malformed) {
            if (graph_failure) *graph_failure = code;
            if (failure) *failure = reason;
            return false;
        };
        out_items.clear();
        if (iinf.body_size < 6) return fail("iinf body too short");
        size_t p = iinf.body_start;
        const size_t end = iinf.start + iinf.size;
        const uint8_t version = data[p++];
        if (version > 1 || data[p] != 0 || data[p + 1] != 0 || data[p + 2] != 0) return fail("iinf version or flags", heif_graph_error::unsupported_layout);
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
        if (count > 1'000'000u) return fail("iinf count too large", heif_graph_error::capacity_exceeded);

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
            if (infe_version < 2 || infe_version > 3 || (infe_flags & ~1u) != 0) return fail("infe version or flags", heif_graph_error::unsupported_layout);
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
            if (item_id == 0) return fail("zero infe item id");
            if (std::find(seen_ids.begin(), seen_ids.end(), item_id) != seen_ids.end())
                return fail("duplicate infe item id", heif_graph_error::duplicate_identity);
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
        std::vector<lpb_auxiliary_item_facts>& output, std::string& error, heif_graph_error& failure) {
        failure = heif_graph_error::malformed;
        output.clear();
        std::vector<locator_box> top;
        if (!parse_children_strict(input, 0, input_size, top)) {
            error = "Malformed HEIF top-level box region.";
            return false;
        }

        const locator_box* meta = nullptr;
        for (const auto& box : top) {
            if (!is_type(box, "meta")) continue;
            if (meta != nullptr) { failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF meta box."; return false; }
            meta = &box;
        }
        if (meta == nullptr || meta->body_size < 4) {
            error = "Missing HEIF meta box.";
            return false;
        }
        if (input[meta->body_start] != 0 || input[meta->body_start + 1] != 0 ||
            input[meta->body_start + 2] != 0 || input[meta->body_start + 3] != 0) {
            failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF meta version or flags.";
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
                if (*slot != nullptr) { failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF authoritative child box."; return false; }
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
        if (!parse_iinf(input, *iinf, items, &iinf_error, &failure)) {
            error = "Malformed HEIF iinf: " + iinf_error;
            return false;
        }
        if (find_item(items, primary_id) == nullptr) {
            error = "HEIF pitm references an unknown item.";
            return false;
        }

        std::vector<auxiliary_property> auxiliary_properties;
        if (iprp != nullptr && !parse_heif_auxiliary_properties(
            input, *iprp, items, auxiliary_properties, error, failure)) {
            return false;
        }

        binary_reader reader(input, iloc->start + iloc->size);
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
            failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF iloc field size.";
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

        // Bound allocation by entries that can physically fit in this iloc.
        const size_t minimum_item_bytes = (version < 2 ? 2u : 4u) +
            (version == 0 ? 0u : 2u) + 2u + base_size + 2u +
            index_size + offset_size + length_size;
        if (item_count > reader.remaining() / minimum_item_bytes) {
            error = "Truncated HEIF iloc item table.";
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
            if (item_id == 0 || find_item(items, item_id) == nullptr) {
                failure = heif_graph_error::missing_reference;
                error = "HEIF iloc references an unknown item.";
                return false;
            }
            if (!located_ids.insert(item_id).second) {
                failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF iloc item.";
                return false;
            }

            uint16_t construction_method = 0;
            if (version != 0) {
                if (!reader.try_read_be16u(construction_method)) {
                    error = "Truncated HEIF iloc construction method."; return false;
                }
                if (construction_method > 1) {
                    failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF iloc construction method.";
                    return false;
                }
            }
            uint16_t data_reference_index = 0;
            if (!reader.try_read_be16u(data_reference_index)) {
                error = "Truncated HEIF iloc data reference."; return false;
            }
            if (data_reference_index != 0) {
                failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF iloc data reference.";
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
                failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF iref version or flags.";
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
                    failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF relationship type.";
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
                        failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF relationship dependency edge.";
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
                    failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF derived item type in dimg relationship.";
                    return false;
                }
                if (!dimg_dependencies.emplace(reference.from, reference.targets).second) {
                    failure = heif_graph_error::duplicate_identity; error = "Shadow HEIF dimg relationship for derived item.";
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
                    failure = heif_graph_error::duplicate_identity; error = "Shadow or ambiguous HEIF auxiliary relationship to primary.";
                    return false;
                }
                if (auxiliary_id != 0) {
                    if (!primary_owned_auxiliary.insert(auxiliary_id).second) {
                        failure = heif_graph_error::duplicate_identity; error = "Shadow HEIF auxiliary relationship has multiple primary owners.";
                        return false;
                    }
                    const auto property = std::find_if(auxiliary_properties.begin(), auxiliary_properties.end(),
                        [auxiliary_id](const auxiliary_property& value) { return value.item_id == auxiliary_id; });
                    const std::string relationship = property == auxiliary_properties.end()
                        ? "auxl" : property->type;
                    if (!primary_relationships.insert(relationship).second) {
                        failure = heif_graph_error::duplicate_identity; error = "Shadow HEIF auxiliary relationship is not unique.";
                        return false;
                    }
                    primary_auxiliary_owners[auxiliary_id].push_back(primary_id);
                }
            }
        }

        std::map<uint32_t, uint8_t> visit_state;
        std::map<uint32_t, std::vector<uint32_t>> transitive_dependencies;
        std::function<bool(uint32_t, size_t)> visit = [&](uint32_t item_id, size_t depth) {
            uint8_t& state = visit_state[item_id];
            if (state == 1) { failure = heif_graph_error::reference_cycle; error = "HEIF dimg dependency cycle detected."; return false; }
            if (state == 2) return true;
            if (depth > LPB_HEIF_MAX_DEPENDENCIES) {
                failure = heif_graph_error::capacity_exceeded;
                error = "HEIF dependency depth exceeds the supported capacity.";
                return false;
            }
            state = 1;
            const locator_item* item = find_item(items, item_id);
            if (item == nullptr || find_location(locations, item_id) == nullptr) {
                error = "HEIF dimg dependency references a missing tile/item.";
                return false;
            }
            const auto dependency_it = dimg_dependencies.find(item_id);
            if (dependency_it != dimg_dependencies.end()) {
                if (!is_supported_derived_type(item->type)) {
                    failure = heif_graph_error::unsupported_layout; error = "Unsupported HEIF derived item type.";
                    return false;
                }
                auto& flattened = transitive_dependencies[item_id];
                for (uint32_t dependency_id : dependency_it->second) {
                    if (std::find(flattened.begin(), flattened.end(), dependency_id) != flattened.end()) {
                        failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF dimg dependency edge.";
                        return false;
                    }
                    if (!visit(dependency_id, depth + 1)) return false;
                    flattened.push_back(dependency_id);
                    const auto child_it = transitive_dependencies.find(dependency_id);
                    if (child_it != transitive_dependencies.end()) {
                        for (uint32_t nested_id : child_it->second) {
                            if (std::find(flattened.begin(), flattened.end(), nested_id) != flattened.end()) {
                                failure = heif_graph_error::duplicate_identity; error = "Duplicate HEIF dimg dependency edge.";
                                return false;
                            }
                            flattened.push_back(nested_id);
                        }
                    }
                }
                if (flattened.size() > LPB_HEIF_MAX_DEPENDENCIES) {
                    failure = heif_graph_error::capacity_exceeded; error = "HEIF derived dependency graph exceeds the supported capacity.";
                    return false;
                }
            }
            state = 2;
            return true;
        };

        for (const auto& dependency : dimg_dependencies) {
            if (!visit(dependency.first, 0)) return false;
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
                failure = heif_graph_error::capacity_exceeded; error = "HEIF auxiliary relationship exceeds the source-facts ABI capacity.";
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
            copy_fixed_string(facts.item_type, item_type);
            copy_fixed_string(facts.stable_identity, stable_identity);
            copy_fixed_string(facts.owner_identity, "primary:0");
            copy_fixed_string(facts.relationship, relationship);
            copy_fixed_string(facts.semantic,
                relationship == "urn:com:apple:photo:2020:aux:hdrgainmap" ||
                relationship == "urn:com:samsung:photo:2024:aux:hdrgainmap" ||
                relationship == "urn:com:photo:aux:hdrgainmap" ? "GainMap" : "Auxiliary");
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
                    copy_fixed_string(facts.dependency_item_types[i], item_type_string(dependency_item->type));
                }
            }
            output.push_back(facts);
            if (output.size() > 8) {
                failure = heif_graph_error::capacity_exceeded; error = "HEIF auxiliary item count exceeds the source-facts ABI capacity.";
                return false;
            }
        }
        failure = heif_graph_error::none;
        return true;
    }
}

namespace {
struct ipma_entry {
    uint32_t item_id{};
    std::vector<uint16_t> associations;
};

uint32_t read_u32_at(const uint8_t* data, size_t position) noexcept {
    return read_be32u(data + position);
}

void append_u16(std::vector<uint8_t>& output, uint16_t value) {
    output.push_back(static_cast<uint8_t>(value >> 8));
    output.push_back(static_cast<uint8_t>(value));
}

void append_u32(std::vector<uint8_t>& output, uint32_t value) {
    output.push_back(static_cast<uint8_t>(value >> 24));
    output.push_back(static_cast<uint8_t>(value >> 16));
    output.push_back(static_cast<uint8_t>(value >> 8));
    output.push_back(static_cast<uint8_t>(value));
}

void write_be_value(std::vector<uint8_t>& output, size_t position, size_t size, uint64_t value) {
    for (size_t index = 0; index < size; ++index) {
        output[position + size - index - 1] = static_cast<uint8_t>(value >> (index * 8));
    }
}

uint64_t read_be_value(const std::vector<uint8_t>& input, size_t position, size_t size) noexcept {
    uint64_t value = 0;
    for (size_t index = 0; index < size; ++index) value = (value << 8) | input[position + index];
    return value;
}

bool make_box(const char type[4], const std::vector<uint8_t>& payload,
    std::vector<uint8_t>& output) {
    if (payload.size() > std::numeric_limits<uint32_t>::max() - 8u) return false;
    output.clear();
    output.reserve(payload.size() + 8);
    append_u32(output, static_cast<uint32_t>(payload.size() + 8));
    output.insert(output.end(), type, type + 4);
    output.insert(output.end(), payload.begin(), payload.end());
    return true;
}

bool append_child(const std::vector<uint8_t>& input, const locator_box& parent,
    const std::vector<uint8_t>& child, std::vector<uint8_t>& output) {
    if (parent.header_size != 8 || parent.size > std::numeric_limits<uint32_t>::max() ||
        child.size() > std::numeric_limits<uint32_t>::max() - parent.size) return false;
    output.assign(input.begin() + static_cast<std::ptrdiff_t>(parent.start),
        input.begin() + static_cast<std::ptrdiff_t>(parent.start + parent.size));
    const uint32_t new_size = static_cast<uint32_t>(output.size() + child.size());
    output[0] = static_cast<uint8_t>(new_size >> 24);
    output[1] = static_cast<uint8_t>(new_size >> 16);
    output[2] = static_cast<uint8_t>(new_size >> 8);
    output[3] = static_cast<uint8_t>(new_size);
    output.insert(output.end(), child.begin(), child.end());
    return true;
}

bool rebuild_parent(const std::vector<uint8_t>& input, const locator_box& parent,
    size_t children_start, const std::map<std::string_view, const std::vector<uint8_t>*>& replacements,
    std::vector<uint8_t>& output, std::string& error) {
    if (parent.header_size != 8 || children_start < parent.body_start ||
        children_start > parent.start + parent.size) {
        error = "Generated HEIF contains an unsupported parent box layout.";
        return false;
    }
    std::vector<locator_box> children;
    if (!parse_children_strict(input.data(), children_start, parent.start + parent.size, children)) {
        error = "Generated HEIF has malformed nested boxes before GainMap graph assembly.";
        return false;
    }
    std::map<std::string_view, size_t> replacement_counts;
    std::vector<uint8_t> payload;
    payload.insert(payload.end(), input.begin() + static_cast<std::ptrdiff_t>(parent.start + parent.header_size),
        input.begin() + static_cast<std::ptrdiff_t>(children_start));
    for (const auto& child : children) {
        const auto replacement = replacements.find(std::string_view(child.type, 4));
        if (replacement == replacements.end()) {
            payload.insert(payload.end(), input.begin() + static_cast<std::ptrdiff_t>(child.start),
                input.begin() + static_cast<std::ptrdiff_t>(child.start + child.size));
        } else {
            ++replacement_counts[replacement->first];
            const auto& bytes = *replacement->second;
            payload.insert(payload.end(), bytes.begin(), bytes.end());
        }
    }
    for (const auto& [type, replacement] : replacements) {
        (void)replacement;
        if (replacement_counts[type] != 1) {
            error = "Generated HEIF lacks one unique box required for GainMap graph assembly.";
            return false;
        }
    }
    if (!make_box(parent.type, payload, output)) {
        error = "GainMap graph assembly exceeded a HEIF box size limit.";
        return false;
    }
    return true;
}

bool parse_ipma(const std::vector<uint8_t>& input, const locator_box& ipma,
    std::vector<ipma_entry>& entries, std::string& error) {
    entries.clear();
    if (ipma.header_size != 8 || ipma.body_size < 8) {
        error = "Generated HEIF ipma box is truncated or unsupported.";
        return false;
    }
    const size_t end = ipma.start + ipma.size;
    const uint8_t version = input[ipma.body_start];
    const uint32_t flags = (static_cast<uint32_t>(input[ipma.body_start + 1]) << 16) |
        (static_cast<uint32_t>(input[ipma.body_start + 2]) << 8) | input[ipma.body_start + 3];
    if (version > 1 || (flags & ~1u) != 0) {
        error = "Generated HEIF ipma version or flags are unsupported.";
        return false;
    }
    const bool wide = (flags & 1u) != 0;
    size_t position = ipma.body_start + 4;
    if (position + 4 > end) return false;
    const uint32_t count = read_u32_at(input.data(), position);
    position += 4;
    if (count > 65535) {
        error = "Generated HEIF ipma item count exceeds the supported writer bound.";
        return false;
    }
    std::set<uint32_t> item_ids;
    entries.reserve(count);
    for (uint32_t index = 0; index < count; ++index) {
        const size_t id_width = version == 0 ? 2 : 4;
        if (position + id_width + 1 > end) {
            error = "Generated HEIF ipma entry is truncated.";
            return false;
        }
        ipma_entry entry{};
        entry.item_id = id_width == 2 ? read_be16u(input.data() + position) : read_u32_at(input.data(), position);
        position += id_width;
        if (entry.item_id == 0 || !item_ids.insert(entry.item_id).second) {
            error = "Generated HEIF ipma contains duplicate or zero item identities.";
            return false;
        }
        const uint8_t association_count = input[position++];
        entry.associations.reserve(association_count);
        for (uint8_t association = 0; association < association_count; ++association) {
            uint16_t value = 0;
            if (wide) {
                if (position + 2 > end) {
                    error = "Generated HEIF wide ipma association is truncated.";
                    return false;
                }
                value = read_be16u(input.data() + position);
                position += 2;
            } else {
                if (position + 1 > end) {
                    error = "Generated HEIF ipma association is truncated.";
                    return false;
                }
                const uint8_t small = input[position++];
                value = static_cast<uint16_t>(((small & 0x80u) ? 0x8000u : 0u) | (small & 0x7Fu));
            }
            const uint16_t property_index = static_cast<uint16_t>(value & 0x7FFFu);
            if (property_index == 0) {
                error = "Generated HEIF ipma contains a zero property association.";
                return false;
            }
            entry.associations.push_back(value);
        }
        entries.push_back(std::move(entry));
    }
    if (position != end) {
        error = "Generated HEIF ipma has trailing bytes.";
        return false;
    }
    return true;
}

bool build_ipma(const std::vector<ipma_entry>& entries, std::vector<uint8_t>& output) {
    std::vector<uint8_t> payload{ 1, 0, 0, 1 };
    append_u32(payload, static_cast<uint32_t>(entries.size()));
    for (const auto& entry : entries) {
        if (entry.item_id == 0 || entry.associations.size() > 255) return false;
        append_u32(payload, entry.item_id);
        payload.push_back(static_cast<uint8_t>(entry.associations.size()));
        for (const uint16_t association : entry.associations) append_u16(payload, association);
    }
    return make_box("ipma", payload, output);
}

bool shift_iloc_file_extents(const std::vector<uint8_t>& input, const locator_box& iloc,
    size_t insertion_point, uint64_t delta, std::vector<uint8_t>& output, std::string& error) {
    if (iloc.header_size != 8 || iloc.body_size < 8) {
        error = "Generated HEIF iloc box is truncated or unsupported.";
        return false;
    }
    output.assign(input.begin() + static_cast<std::ptrdiff_t>(iloc.start),
        input.begin() + static_cast<std::ptrdiff_t>(iloc.start + iloc.size));
    const size_t end = output.size();
    const size_t body = 8;
    const uint8_t version = output[body];
    if (version > 2 || output[body + 1] || output[body + 2] || output[body + 3]) {
        error = "Generated HEIF iloc version or flags are unsupported.";
        return false;
    }
    size_t position = body + 4;
    if (position + 2 > end) return false;
    const uint8_t byte1 = output[position++];
    const uint8_t byte2 = output[position++];
    const size_t offset_size = static_cast<size_t>(byte1 >> 4);
    const size_t length_size = static_cast<size_t>(byte1 & 0x0F);
    const size_t base_size = static_cast<size_t>(byte2 >> 4);
    const size_t index_size = version == 0 ? 0 : static_cast<size_t>(byte2 & 0x0F);
    if (offset_size > 8 || length_size > 8 || base_size > 8 || index_size > 8 ||
        (version == 0 && (byte2 & 0x0Fu) != 0)) {
        error = "Generated HEIF iloc field widths are unsupported.";
        return false;
    }
    uint32_t item_count = 0;
    if (version < 2) {
        if (position + 2 > end) return false;
        item_count = read_be16u(output.data() + position);
        position += 2;
    } else {
        if (position + 4 > end) return false;
        item_count = read_u32_at(output.data(), position);
        position += 4;
    }
    if (item_count > 1'000'000) {
        error = "Generated HEIF iloc item count exceeds the supported writer bound.";
        return false;
    }
    const auto max_for_width = [](size_t width) noexcept -> uint64_t {
        return width == 8 ? std::numeric_limits<uint64_t>::max() :
            (width == 0 ? 0 : ((uint64_t{1} << (width * 8)) - 1));
    };
    for (uint32_t item_index = 0; item_index < item_count; ++item_index) {
        const size_t id_width = version < 2 ? 2 : 4;
        if (position + id_width > end) return false;
        position += id_width;
        uint16_t construction_method = 0;
        if (version == 1 || version == 2) {
            if (position + 2 > end) return false;
            construction_method = static_cast<uint16_t>(read_be16u(output.data() + position) & 0x000Fu);
            if (construction_method > 1 || (read_be16u(output.data() + position) & 0xFFF0u) != 0) {
                error = "Generated HEIF iloc construction method is unsupported.";
                return false;
            }
            position += 2;
        }
        if (position + 2 + base_size + 2 > end) return false;
        const uint16_t data_reference = read_be16u(output.data() + position);
        position += 2;
        if (data_reference != 0) {
            error = "Generated HEIF iloc uses an external data reference.";
            return false;
        }
        const size_t base_position = position;
        const uint64_t base_offset = base_size == 0 ? 0 : read_be_value(output, position, base_size);
        position += base_size;
        const uint16_t extent_count = read_be16u(output.data() + position);
        position += 2;
        struct extent_field { size_t offset_position{}; uint64_t offset{}; bool shift{}; };
        std::vector<extent_field> extents;
        extents.reserve(extent_count);
        for (uint16_t extent = 0; extent < extent_count; ++extent) {
            if ((version == 1 || version == 2) && index_size != 0) {
                if (position + index_size > end) return false;
                position += index_size;
            }
            if (position + offset_size + length_size > end) return false;
            const size_t offset_position = position;
            const uint64_t extent_offset = offset_size == 0 ? 0 : read_be_value(output, position, offset_size);
            position += offset_size;
            const uint64_t extent_length = length_size == 0 ? 0 : read_be_value(output, position, length_size);
            position += length_size;
            if (construction_method == 0) {
                if ((extent_offset > std::numeric_limits<uint64_t>::max() - base_offset) ||
                    (extent_length != 0 && base_offset + extent_offset >
                        std::numeric_limits<uint64_t>::max() - extent_length)) {
                    error = "Generated HEIF iloc extent arithmetic overflowed.";
                    return false;
                }
                const uint64_t absolute = base_offset + extent_offset;
                const uint64_t absolute_end = absolute + extent_length;
                if (absolute < insertion_point && absolute_end > insertion_point) {
                    error = "Generated HEIF iloc extent crosses the metadata insertion boundary.";
                    return false;
                }
                extents.push_back({ offset_position, extent_offset, absolute >= insertion_point });
            } else {
                extents.push_back({ offset_position, extent_offset, false });
            }
        }
        if (construction_method == 0) {
            const bool needs_shift = std::any_of(extents.begin(), extents.end(),
                [](const extent_field& extent) { return extent.shift; });
            if (needs_shift) {
                const bool all_shift = std::all_of(extents.begin(), extents.end(),
                    [](const extent_field& extent) { return extent.shift; });
                bool can_shift_offsets = offset_size != 0 && delta <= max_for_width(offset_size);
                if (can_shift_offsets) {
                    for (const auto& extent : extents) {
                        if (extent.shift && extent.offset > max_for_width(offset_size) - delta) {
                            can_shift_offsets = false;
                            break;
                        }
                    }
                }
                if (can_shift_offsets) {
                    for (const auto& extent : extents) {
                        if (extent.shift) write_be_value(output, extent.offset_position,
                            offset_size, extent.offset + delta);
                    }
                } else if (all_shift && base_size != 0 && delta <= max_for_width(base_size) &&
                    base_offset <= max_for_width(base_size) - delta) {
                    write_be_value(output, base_position, base_size, base_offset + delta);
                } else {
                    error = "Generated HEIF iloc cannot represent shifted absolute media extents.";
                    return false;
                }
            }
        }
    }
    if (position != end) {
        error = "Generated HEIF iloc has trailing bytes.";
        return false;
    }
    return true;
}

} // namespace

namespace lpb::containers {

lpb_inspection_failure_category inspection_category(heif_graph_error error) noexcept {
    switch (error) {
    case heif_graph_error::none: return LPB_INSPECTION_FAILURE_NONE;
    case heif_graph_error::duplicate_identity:
    case heif_graph_error::reference_cycle: return LPB_INSPECTION_FAILURE_AMBIGUOUS;
    case heif_graph_error::unsupported_layout:
    case heif_graph_error::capacity_exceeded: return LPB_INSPECTION_FAILURE_UNSUPPORTED;
    default: return LPB_INSPECTION_FAILURE_MALFORMED;
    }
}

bool enumerate_auxiliary_items(std::span<const uint8_t> input,
    std::vector<lpb_auxiliary_item_facts>& items, heif_graph_diagnostic& diagnostic) noexcept {
    diagnostic = {};
    items.clear();
    try {
        if (parse_auxiliary_graph(input.data(), input.size(), items, diagnostic.message, diagnostic.code))
            return true;
    } catch (const std::bad_alloc&) {
        diagnostic.code = heif_graph_error::capacity_exceeded;
        diagnostic.message = "HEIF graph allocation is unavailable.";
    } catch (const std::length_error&) {
        diagnostic.code = heif_graph_error::capacity_exceeded;
        diagnostic.message = "HEIF graph exceeds the allocator's supported capacity.";
    } catch (const std::exception& ex) {
        diagnostic.code = heif_graph_error::internal_failure;
        diagnostic.message = ex.what();
    } catch (...) {
        diagnostic.code = heif_graph_error::internal_failure;
        diagnostic.message = "HEIF graph inspection failed.";
    }
    // Never return a partially populated graph on failure.
    items.clear();
    return false;
}

bool enumerate_xmp_ranges_describing_item(std::span<const uint8_t> input,
    uint32_t described_item_id, std::vector<lpb_media_range>& ranges,
    std::string& error) noexcept {
    ranges.clear();
    error.clear();
    if (input.empty() || described_item_id == 0) {
        error = "XMP relationship inspection requires a bounded file and item identity.";
        return false;
    }
    try {
        std::vector<locator_box> top;
        if (!parse_children_strict(input.data(), 0, input.size(), top)) {
            error = "Malformed HEIF top-level boxes during XMP relationship inspection.";
            return false;
        }
        const locator_box* meta = nullptr;
        for (const auto& box : top) {
            if (!is_type(box, "meta")) continue;
            if (meta) { error = "Duplicate HEIF meta boxes during XMP relationship inspection."; return false; }
            meta = &box;
        }
        if (!meta || meta->body_size < 4) { error = "HEIF meta box is missing or truncated."; return false; }
        std::vector<locator_box> children;
        if (!parse_children_strict(input.data(), meta->body_start + 4, meta->start + meta->size, children)) {
            error = "Malformed HEIF meta children during XMP relationship inspection.";
            return false;
        }
        const locator_box *iinf = nullptr, *iloc = nullptr, *iref = nullptr;
        for (const auto& box : children) {
            const locator_box** slot = is_type(box, "iinf") ? &iinf : is_type(box, "iloc") ? &iloc :
                is_type(box, "iref") ? &iref : nullptr;
            if (!slot) continue;
            if (*slot) { error = "Duplicate authoritative HEIF item graph box."; return false; }
            *slot = &box;
        }
        if (!iinf || !iloc || !iref || iref->body_size < 4) {
            error = "HEIF lacks an item info, location, or reference box.";
            return false;
        }
        std::vector<locator_item> items;
        std::string parse_error;
        if (!parse_iinf(input.data(), *iinf, items, &parse_error)) {
            error = "Malformed HEIF item info: " + parse_error;
            return false;
        }
        const auto item_by_id = [&](uint32_t id) -> const locator_item* {
            const auto found = std::find_if(items.begin(), items.end(), [id](const locator_item& item) { return item.id == id; });
            return found == items.end() ? nullptr : &*found;
        };
        std::vector<uint32_t> xmp_ids;
        for (const auto& item : items) {
            if (item.type == 0x6D696D65u && item.content_type.rfind("application/rdf+xml", 0) == 0) {
                xmp_ids.push_back(item.id);
            }
        }
        if (xmp_ids.empty()) return true;
        const uint8_t version = input[iref->body_start];
        if (version > 1 || input[iref->body_start + 1] || input[iref->body_start + 2] || input[iref->body_start + 3]) {
            error = "HEIF iref version or flags are malformed.";
            return false;
        }
        std::vector<locator_box> references;
        if (!parse_children_strict(input.data(), iref->body_start + 4, iref->start + iref->size, references)) {
            error = "Malformed HEIF iref children during XMP relationship inspection.";
            return false;
        }
        std::set<uint32_t> described_xmp_ids;
        for (const auto& reference : references) {
            if (!is_type(reference, "cdsc")) continue;
            size_t position = reference.body_start;
            const size_t end = reference.start + reference.size;
            const size_t id_width = version == 0 ? 2 : 4;
            if (position + id_width + 2 > end) { error = "Truncated HEIF cdsc relation."; return false; }
            const uint32_t from_id = id_width == 2 ? read_be16u(input.data() + position) : read_u32_at(input.data(), position);
            position += id_width;
            const uint16_t target_count = read_be16u(input.data() + position);
            position += 2;
            if (target_count == 0) { error = "Empty HEIF cdsc relation."; return false; }
            bool describes_target = false;
            std::set<uint32_t> targets;
            for (uint16_t index = 0; index < target_count; ++index) {
                if (position + id_width > end) { error = "Truncated HEIF cdsc target."; return false; }
                const uint32_t target_id = id_width == 2 ? read_be16u(input.data() + position) : read_u32_at(input.data(), position);
                position += id_width;
                if (!targets.insert(target_id).second || !item_by_id(target_id)) {
                    error = "Duplicate or unknown HEIF cdsc target item.";
                    return false;
                }
                describes_target = describes_target || target_id == described_item_id;
            }
            if (position != end || !item_by_id(from_id) || from_id == described_item_id) {
                error = "Malformed HEIF cdsc item identity or trailing bytes.";
                return false;
            }
            if (!describes_target) continue;
            if (target_count != 1 || std::find(xmp_ids.begin(), xmp_ids.end(), from_id) == xmp_ids.end() ||
                !described_xmp_ids.insert(from_id).second) {
                error = "HEIF XMP metadata relationship to the GainMap is ambiguous or duplicated.";
                return false;
            }
            uint64_t offset = 0, length = 0;
            const locator_result located = locate_item(input.data(), input.size(), 0x6D696D65u, "XMP",
                &offset, &length, error, from_id);
            if (located != locator_result::present || length == 0) {
                if (error.empty()) error = "HEIF GainMap XMP item has no valid iloc extent.";
                return false;
            }
            ranges.push_back({ offset, length });
        }
        return true;
    } catch (const std::exception& ex) {
        ranges.clear();
        error = ex.what();
        return false;
    } catch (...) {
        ranges.clear();
        error = "HEIF XMP relationship inspection failed without a typed diagnostic.";
        return false;
    }
}

bool attach_gainmap_auxiliary_graph(std::span<const uint8_t> input,
    uint32_t primary_item_id, uint32_t gainmap_item_id,
    std::vector<uint8_t>& output, std::string& error) noexcept {
    output.clear();
    error.clear();
    constexpr std::string_view iso_gainmap_type = "urn:com:photo:aux:hdrgainmap";
    if (input.empty() || primary_item_id == 0 || gainmap_item_id == 0 || primary_item_id == gainmap_item_id) {
        error = "GainMap graph assembly requires exact distinct primary and auxiliary item identities.";
        return false;
    }
    try {
        constexpr uint64_t kMaxHeifBytes = 512ull * 1024ull * 1024ull;
        if (input.size() > kMaxHeifBytes) {
            error = "Generated HEIF exceeds the bounded GainMap assembly limit.";
            return false;
        }
        const std::vector<uint8_t> raw(input.begin(), input.end());
        std::vector<locator_box> top;
        if (!parse_children_strict(raw.data(), 0, raw.size(), top)) {
            error = "Generated HEIF top-level structure is malformed.";
            return false;
        }
        const locator_box* meta = nullptr;
        for (const auto& box : top) {
            if (!is_type(box, "meta")) continue;
            if (meta) { error = "Generated HEIF contains duplicate meta boxes."; return false; }
            meta = &box;
        }
        if (!meta || meta->body_size < 4 || meta->header_size != 8) {
            error = "Generated HEIF has no supported meta box.";
            return false;
        }
        std::vector<locator_box> children;
        if (!parse_children_strict(raw.data(), meta->body_start + 4, meta->start + meta->size, children)) {
            error = "Generated HEIF meta children are malformed.";
            return false;
        }
        const locator_box *pitm = nullptr, *iinf = nullptr, *iloc = nullptr, *iref = nullptr, *iprp = nullptr;
        for (const auto& box : children) {
            const locator_box** slot = is_type(box, "pitm") ? &pitm : is_type(box, "iinf") ? &iinf :
                is_type(box, "iloc") ? &iloc : is_type(box, "iref") ? &iref : is_type(box, "iprp") ? &iprp : nullptr;
            if (!slot) continue;
            if (*slot) { error = "Generated HEIF has duplicate authoritative graph boxes."; return false; }
            *slot = &box;
        }
        if (!pitm || !iinf || !iloc || !iref || !iprp || pitm->body_size < 6 || iref->body_size < 4) {
            error = "Generated HEIF lacks the boxes required for GainMap graph assembly.";
            return false;
        }
        const uint8_t pitm_version = raw[pitm->body_start];
        if (pitm_version > 1 || raw[pitm->body_start + 1] || raw[pitm->body_start + 2] || raw[pitm->body_start + 3] ||
            (pitm_version == 1 && pitm->body_size < 8)) {
            error = "Generated HEIF pitm is malformed.";
            return false;
        }
        const uint32_t primary = pitm_version == 0 ? read_be16u(raw.data() + pitm->body_start + 4) :
            read_u32_at(raw.data(), pitm->body_start + 4);
        if (primary != primary_item_id) {
            error = "Generated HEIF primary identity changed before GainMap graph assembly.";
            return false;
        }
        std::vector<locator_item> items;
        std::string parse_error;
        if (!parse_iinf(raw.data(), *iinf, items, &parse_error)) {
            error = "Generated HEIF item info is malformed: " + parse_error;
            return false;
        }
        const auto primary_item = std::find_if(items.begin(), items.end(), [primary_item_id](const locator_item& item) { return item.id == primary_item_id; });
        const auto gainmap_item = std::find_if(items.begin(), items.end(), [gainmap_item_id](const locator_item& item) { return item.id == gainmap_item_id; });
        if (primary_item == items.end() || gainmap_item == items.end() ||
            (primary_item->type != 0x68766331u && primary_item->type != 0x68657631u) ||
            (gainmap_item->type != 0x68766331u && gainmap_item->type != 0x68657631u)) {
            error = "Generated HEIF encoder did not return two distinct coded image items.";
            return false;
        }
        std::vector<lpb_media_range> xmp_ranges;
        if (!enumerate_xmp_ranges_describing_item(raw, gainmap_item_id, xmp_ranges, error) || xmp_ranges.size() != 1) {
            if (error.empty()) error = "Generated HEIF lacks exactly one XMP packet associated with the exact GainMap item.";
            return false;
        }

        const uint8_t iref_version = raw[iref->body_start];
        if (iref_version > 1 || raw[iref->body_start + 1] || raw[iref->body_start + 2] || raw[iref->body_start + 3]) {
            error = "Generated HEIF iref version or flags are unsupported.";
            return false;
        }
        std::vector<locator_box> references;
        if (!parse_children_strict(raw.data(), iref->body_start + 4, iref->start + iref->size, references)) {
            error = "Generated HEIF iref children are malformed.";
            return false;
        }
        for (const auto& reference : references) {
            if (is_type(reference, "auxl")) {
                error = "Generated HEIF already contains an auxiliary relationship before GainMap assembly.";
                return false;
            }
        }

        std::vector<locator_box> iprp_children;
        if (iprp->header_size != 8 || !parse_children_strict(raw.data(), iprp->body_start,
                iprp->start + iprp->size, iprp_children)) {
            error = "Generated HEIF iprp children are malformed or unsupported.";
            return false;
        }
        const locator_box *ipco = nullptr, *ipma = nullptr;
        for (const auto& box : iprp_children) {
            const locator_box** slot = is_type(box, "ipco") ? &ipco : is_type(box, "ipma") ? &ipma : nullptr;
            if (!slot) continue;
            if (*slot) { error = "Generated HEIF iprp has duplicate ipco/ipma boxes."; return false; }
            *slot = &box;
        }
        if (!ipco || !ipma || ipco->header_size != 8 || ipma->header_size != 8) {
            error = "Generated HEIF iprp lacks supported ipco/ipma boxes.";
            return false;
        }
        std::vector<locator_box> properties;
        if (!parse_children_strict(raw.data(), ipco->body_start, ipco->start + ipco->size, properties) ||
            properties.size() >= 0x7FFFu) {
            error = "Generated HEIF ipco property graph is malformed or too large.";
            return false;
        }
        const uint16_t property_index = static_cast<uint16_t>(properties.size() + 1);
        std::vector<ipma_entry> associations;
        if (!parse_ipma(raw, *ipma, associations, error)) return false;
        for (const auto& property : properties) {
            if (!is_type(property, "auxC")) continue;
            size_t target_property = 0;
            const size_t property_number = static_cast<size_t>(&property - properties.data()) + 1;
            const auto item_entry = std::find_if(associations.begin(), associations.end(), [gainmap_item_id](const ipma_entry& entry) {
                return entry.item_id == gainmap_item_id;
            });
            if (item_entry != associations.end()) {
                for (const uint16_t association : item_entry->associations) {
                    if ((association & 0x7FFFu) == property_number) ++target_property;
                }
            }
            if (target_property != 0) {
                error = "Generated HEIF GainMap item already has an auxiliary type property.";
                return false;
            }
        }
        std::vector<uint8_t> auxc_payload{ 0, 0, 0, 0 };
        auxc_payload.insert(auxc_payload.end(), iso_gainmap_type.begin(), iso_gainmap_type.end());
        auxc_payload.push_back(0);
        std::vector<uint8_t> auxc_box;
        std::vector<uint8_t> new_ipco;
        if (!make_box("auxC", auxc_payload, auxc_box) || !append_child(raw, *ipco, auxc_box, new_ipco)) {
            error = "GainMap auxC property could not be represented in the generated HEIF.";
            return false;
        }
        auto gainmap_entry = std::find_if(associations.begin(), associations.end(), [gainmap_item_id](const ipma_entry& entry) {
            return entry.item_id == gainmap_item_id;
        });
        if (gainmap_entry == associations.end()) {
            associations.push_back({ gainmap_item_id, { property_index } });
        } else {
            gainmap_entry->associations.push_back(property_index);
        }
        std::vector<uint8_t> new_ipma;
        if (!build_ipma(associations, new_ipma)) {
            error = "GainMap item property association could not be encoded.";
            return false;
        }
        const std::map<std::string_view, const std::vector<uint8_t>*> iprp_replacements{
            { "ipco", &new_ipco }, { "ipma", &new_ipma }
        };
        std::vector<uint8_t> new_iprp;
        if (!rebuild_parent(raw, *iprp,
                iprp->body_start, iprp_replacements, new_iprp, error)) return false;

        const size_t id_width = iref_version == 0 ? 2 : 4;
        if (id_width == 2 && (gainmap_item_id > 0xFFFFu || primary_item_id > 0xFFFFu)) {
            error = "Generated HEIF iref version cannot represent the item identities.";
            return false;
        }
        std::vector<uint8_t> auxl_payload;
        if (id_width == 2) append_u16(auxl_payload, static_cast<uint16_t>(gainmap_item_id));
        else append_u32(auxl_payload, gainmap_item_id);
        append_u16(auxl_payload, 1);
        if (id_width == 2) append_u16(auxl_payload, static_cast<uint16_t>(primary_item_id));
        else append_u32(auxl_payload, primary_item_id);
        std::vector<uint8_t> auxl_box;
        std::vector<uint8_t> new_iref;
        if (!make_box("auxl", auxl_payload, auxl_box) ||
            !append_child(raw, *iref, auxl_box, new_iref)) {
            error = "GainMap auxl reference could not be represented in the generated HEIF.";
            return false;
        }
        const size_t meta_end = meta->start + meta->size;
        const size_t delta = (new_iprp.size() - iprp->size) + (new_iref.size() - iref->size);
        std::vector<uint8_t> new_iloc;
        if (!shift_iloc_file_extents(raw,
                *iloc, meta_end, delta, new_iloc, error)) return false;
        const std::map<std::string_view, const std::vector<uint8_t>*> meta_replacements{
            { "iloc", &new_iloc }, { "iprp", &new_iprp }, { "iref", &new_iref }
        };
        std::vector<uint8_t> new_meta;
        if (!rebuild_parent(raw, *meta,
                meta->body_start + 4, meta_replacements, new_meta, error)) return false;
        const size_t expected_delta = new_meta.size() - meta->size;
        if (expected_delta != delta || new_meta.size() > std::numeric_limits<uint32_t>::max() ||
            meta_end > input.size() || delta > kMaxHeifBytes - raw.size()) {
            error = "GainMap HEIF metadata box size calculation was inconsistent.";
            return false;
        }
        output.reserve(raw.size() + delta);
        output.insert(output.end(), raw.begin(), raw.begin() + static_cast<std::ptrdiff_t>(meta->start));
        output.insert(output.end(), new_meta.begin(), new_meta.end());
        output.insert(output.end(), raw.begin() + static_cast<std::ptrdiff_t>(meta_end), raw.end());
        std::vector<lpb_auxiliary_item_facts> parsed_auxiliaries;
        heif_graph_error graph_failure{};
        if (!parse_auxiliary_graph(output.data(), output.size(), parsed_auxiliaries, error, graph_failure) ||
            parsed_auxiliaries.size() != 1 || parsed_auxiliaries[0].item_id != gainmap_item_id ||
            std::string_view(parsed_auxiliaries[0].semantic) != "GainMap" ||
            std::string_view(parsed_auxiliaries[0].relationship) != iso_gainmap_type) {
            if (error.empty()) error = "Native HEIF structural re-read did not confirm exactly one ISO GainMap auxiliary.";
            output.clear();
            return false;
        }
        return true;
    } catch (const std::exception& ex) {
        output.clear();
        error = ex.what();
        return false;
    } catch (...) {
        output.clear();
        error = "Native HEIF GainMap graph assembly failed without a typed diagnostic.";
        return false;
    }
}

} // namespace lpb::containers

namespace {
// This rewrite is deliberately independent of media backend serialization.  Only
// item metadata tables and the selected packet's storage bytes are changed.
struct xmp_rewrite_extent { uint64_t offset{}, length{}, index{}; };
struct xmp_rewrite_location {
    uint32_t id{};
    uint16_t method{};
    std::vector<xmp_rewrite_extent> extents;
};
struct xmp_rewrite_graph {
    locator_box meta{}, iinf{}, iloc{}, iref{}, idat{};
    bool has_iref{}, has_idat{};
    uint32_t primary{}, xmp{};
    std::vector<locator_box> top, children;
    std::vector<locator_item> items;
    std::vector<xmp_rewrite_location> locations;
    std::map<uint32_t, std::vector<uint32_t>> derived_dependencies;
};

bool parse_xmp_rewrite_graph(std::span<const uint8_t> input,
    xmp_rewrite_graph& graph, std::string& error) {
    const auto fail = [&](const char* why) { error = why; return false; };
    if (input.empty() || !parse_children_strict(input.data(), 0, input.size(), graph.top))
        return fail("HEIF XMP rewrite: malformed top-level box region.");
    const locator_box* meta = nullptr;
    const locator_box* file_type = nullptr;
    for (const auto& box : graph.top) {
        if (read_be32u(input.data() + box.start) == 0)
            return fail("HEIF XMP rewrite: size-zero top-level box cannot safely precede an appended extent.");
        if (is_type(box, "moov") || is_type(box, "moof") || is_type(box, "sidx") ||
            is_type(box, "mfra") || is_type(box, "iloc"))
            return fail("HEIF XMP rewrite: top-level offset-bearing movie/index layout is unsupported.");
        if (is_type(box, "mpvd") || is_type(box, "sefd"))
            return fail("HEIF XMP rewrite: input already has a live video/trailer box.");
        if (is_type(box, "ftyp")) {
            if (file_type || box.body_size < 8 || (box.body_size - 8) % 4 != 0)
                return fail("HEIF XMP rewrite: duplicate or malformed file type authority.");
            file_type = &box;
        }
        if (is_type(box, "meta")) {
            if (meta) return fail("HEIF XMP rewrite: duplicate meta authority.");
            meta = &box;
        }
    }
    if (!file_type) return fail("HEIF XMP rewrite: missing file type authority.");
    if (!meta || meta->body_size < 4 || meta->header_size != 8)
        return fail("HEIF XMP rewrite: missing or unsupported meta header.");
    if (meta->size > 64u * 1024u * 1024u)
        return fail("HEIF XMP rewrite: meta exceeds the explicit 64 MiB structural resource limit.");
    graph.meta = *meta;
    if (read_be32u(input.data() + meta->body_start) != 0 ||
        !parse_children_strict(input.data(), meta->body_start + 4,
            meta->start + meta->size, graph.children))
        return fail("HEIF XMP rewrite: invalid meta fullbox or child region.");
    std::map<std::string, const locator_box*> authoritative;
    for (const auto& child : graph.children) {
        if (read_be32u(input.data() + child.start) == 0)
            return fail("HEIF XMP rewrite: size-zero meta child is unsupported.");
        if (is_type(child, "pitm") || is_type(child, "iinf") || is_type(child, "iloc") ||
            is_type(child, "iref") || is_type(child, "idat") || is_type(child, "iprp") ||
            is_type(child, "dinf")) {
            if (!authoritative.emplace(std::string(child.type, 4), &child).second)
                return fail("HEIF XMP rewrite: duplicate authoritative meta child.");
        }
    }
    if (!authoritative.count("pitm") || !authoritative.count("iinf") || !authoritative.count("iloc"))
        return fail("HEIF XMP rewrite: incomplete primary item graph.");
    const auto& pitm = *authoritative.at("pitm");
    if (pitm.body_size < 6 || input[pitm.body_start] > 1 ||
        input[pitm.body_start + 1] || input[pitm.body_start + 2] || input[pitm.body_start + 3])
        return fail("HEIF XMP rewrite: unsupported pitm version/flags.");
    const size_t primary_width = input[pitm.body_start] == 0 ? 2 : 4;
    if (pitm.body_size != 4 + primary_width)
        return fail("HEIF XMP rewrite: malformed pitm identity.");
    graph.primary = primary_width == 2 ? read_be16u(input.data() + pitm.body_start + 4)
        : read_be32u(input.data() + pitm.body_start + 4);
    graph.iinf = *authoritative.at("iinf");
    graph.iloc = *authoritative.at("iloc");
    if (!parse_iinf(input.data(), graph.iinf, graph.items, &error)) return false;
    if (graph.items.size() > 65535)
        return fail("HEIF XMP rewrite: item count exceeds the explicit 65535-item resource limit.");
    if (!find_item(graph.items, graph.primary))
        return fail("HEIF XMP rewrite: primary item identity is missing.");
    graph.has_iref = authoritative.count("iref") != 0;
    graph.has_idat = authoritative.count("idat") != 0;
    if (graph.has_iref) graph.iref = *authoritative.at("iref");
    if (graph.has_idat) graph.idat = *authoritative.at("idat");

    std::map<uint32_t, std::vector<uint32_t>> xmp_owners;
    std::vector<heif_reference> all_references;
    std::set<std::pair<std::string, uint32_t>> reference_sources;
    if (graph.has_iref) {
        if (graph.iref.body_size < 4 || input[graph.iref.body_start] > 1 ||
            input[graph.iref.body_start + 1] || input[graph.iref.body_start + 2] || input[graph.iref.body_start + 3])
            return fail("HEIF XMP rewrite: unsupported iref version/flags.");
        const size_t width = input[graph.iref.body_start] == 0 ? 2 : 4;
        std::vector<locator_box> refs;
        if (!parse_children_strict(input.data(), graph.iref.body_start + 4,
                graph.iref.start + graph.iref.size, refs))
            return fail("HEIF XMP rewrite: malformed iref child region.");
        for (const auto& ref : refs) {
            if (read_be32u(input.data() + ref.start) == 0)
                return fail("HEIF XMP rewrite: size-zero iref child is unsupported.");
            size_t p = ref.body_start;
            const size_t end = ref.start + ref.size;
            const auto read_id = [&]() -> uint32_t {
                const auto id = width == 2 ? read_be16u(input.data() + p) : read_be32u(input.data() + p);
                p += width; return id;
            };
            if (end - p < width + 2) return fail("HEIF XMP rewrite: truncated item reference.");
            const uint32_t from = read_id();
            const uint16_t count = read_be16u(input.data() + p); p += 2;
            if (!count || !find_item(graph.items, from) || end - p != width * count ||
                !reference_sources.emplace(std::string(ref.type, 4), from).second)
                return fail("HEIF XMP rewrite: duplicate/shadow or malformed item reference.");
            std::vector<uint32_t> targets;
            std::set<uint32_t> unique;
            for (uint16_t i = 0; i < count; ++i) {
                const uint32_t to = read_id();
                if (to == from || !find_item(graph.items, to) || !unique.insert(to).second)
                    return fail("HEIF XMP rewrite: missing/self/duplicate reference target.");
                targets.push_back(to);
            }
            all_references.push_back({std::string(ref.type, 4), from, targets});
            if (is_type(ref, "cdsc")) xmp_owners.emplace(from, std::move(targets));
        }
    }
    std::map<uint32_t, std::vector<uint32_t>> dependencies;
    for (const auto& ref : all_references) if (ref.type == "dimg") dependencies.emplace(ref.from, ref.targets);
    graph.derived_dependencies = dependencies;
    std::map<uint32_t, uint8_t> visit_state;
    std::function<bool(uint32_t, size_t)> visit = [&](uint32_t id, size_t depth) {
        if (depth > 256 || visit_state[id] == 1) return false;
        if (visit_state[id] == 2) return true;
        visit_state[id] = 1;
        if (const auto at = dependencies.find(id); at != dependencies.end())
            for (const auto target : at->second) if (!visit(target, depth + 1)) return false;
        visit_state[id] = 2;
        return true;
    };
    for (const auto& [id, targets] : dependencies) {
        (void)targets;
        if (!visit(id, 0)) return fail("HEIF XMP rewrite: cyclic dimg graph or dependency depth exceeds 256.");
    }
    if (authoritative.count("iprp")) {
        std::vector<auxiliary_property> aux;
        heif_graph_error failure{};
        if (!parse_heif_auxiliary_properties(input.data(), *authoritative.at("iprp"),
                graph.items, aux, error, failure)) return false;
    }
    for (const auto& item : graph.items) {
        if (item.type != 0x6d696d65u || item.content_type.rfind("application/rdf+xml", 0) != 0) continue;
        if (item.content_type != "application/rdf+xml")
            return fail("HEIF XMP rewrite: unsupported or shadow XMP MIME content type.");
        const auto owner = xmp_owners.find(item.id);
        if (owner == xmp_owners.end() || owner->second.size() != 1)
            return fail("HEIF XMP rewrite: XMP requires one unique cdsc item owner.");
        if (owner->second.front() == graph.primary) {
            if (graph.xmp) return fail("HEIF XMP rewrite: multiple primary-owned XMP items.");
            graph.xmp = item.id;
        }
    }
    if (graph.xmp) for (const auto& ref : all_references) {
        if ((ref.from == graph.xmp && ref.type != "cdsc") ||
            std::find(ref.targets.begin(), ref.targets.end(), graph.xmp) != ref.targets.end())
            return fail("HEIF XMP rewrite: selected XMP item participates in a shared or non-owner relationship.");
    }
    // Protected or encoded XMP cannot be treated as an uncompressed XML packet.
    std::vector<locator_box> infes;
    const size_t count_width = input[graph.iinf.body_start] == 0 ? 2 : 4;
    if (!parse_children_strict(input.data(), graph.iinf.body_start + 4 + count_width,
            graph.iinf.start + graph.iinf.size, infes)) return fail("HEIF XMP rewrite: malformed infe table.");
    for (const auto& infe : infes) {
        if (read_be32u(input.data() + infe.start) == 0)
            return fail("HEIF XMP rewrite: size-zero infe child is unsupported.");
        size_t p = infe.body_start + 4;
        const size_t width = input[infe.body_start] == 2 ? 2 : 4;
        const uint32_t id = width == 2 ? read_be16u(input.data() + p) : read_be32u(input.data() + p);
        p += width;
        const auto* item = find_item(graph.items, id);
        if (!item || item->type != 0x6d696d65u || item->content_type != "application/rdf+xml") continue;
        if (read_be16u(input.data() + p) != 0)
            return fail("HEIF XMP rewrite: protected XMP item is unsupported.");
        p += 6;
        std::string name, mime, encoding;
        const size_t end = infe.start + infe.size;
        if (!read_c_string(input.data(), p, end, name)) return false;
        if (name.rfind("application/", 0) == 0) mime = name;
        else if (!read_c_string(input.data(), p, end, mime)) return false;
        if (p < end && (!read_c_string(input.data(), p, end, encoding) || !encoding.empty()))
            return fail("HEIF XMP rewrite: encoded XMP packet is unsupported.");
    }

    const auto& iloc = graph.iloc;
    binary_reader reader(input.data(), iloc.start + iloc.size);
    if (iloc.body_size < 8 || !reader.try_seek(iloc.body_start))
        return fail("HEIF XMP rewrite: truncated iloc header.");
    uint32_t fullbox = 0;
    uint8_t byte1 = 0, byte2 = 0;
    if (!reader.try_read_be32u(fullbox) || (fullbox & 0xffffffu) || (fullbox >> 24) > 2 ||
        !reader.try_read_u8(byte1) || !reader.try_read_u8(byte2))
        return fail("HEIF XMP rewrite: unsupported iloc version/flags.");
    const unsigned version = fullbox >> 24;
    const size_t offset_width = byte1 >> 4, length_width = byte1 & 15,
        base_width = byte2 >> 4, index_width = version == 0 ? 0 : byte2 & 15;
    if (offset_width > 8 || length_width == 0 || length_width > 8 || base_width > 8 || index_width > 8 ||
        (version == 0 && (byte2 & 15)))
        return fail("HEIF XMP rewrite: unsupported iloc field width or implicit extent length.");
    uint32_t count = 0;
    if (version < 2) { uint16_t small = 0; if (!reader.try_read_be16u(small)) return fail("HEIF XMP rewrite: truncated iloc count or item id."); count = small; }
    else if (!reader.try_read_be32u(count)) return fail("HEIF XMP rewrite: truncated iloc item count.");
    if (count > 65535) return fail("HEIF XMP rewrite: iloc exceeds the explicit 65535-item resource limit.");
    std::set<uint32_t> ids;
    std::vector<heif_extent> all_extents;
    for (uint32_t i = 0; i < count; ++i) {
        xmp_rewrite_location location;
        if (version < 2) { uint16_t small = 0; if (!reader.try_read_be16u(small)) return fail("HEIF XMP rewrite: truncated iloc count or item id."); location.id = small; }
        else if (!reader.try_read_be32u(location.id)) return fail("HEIF XMP rewrite: truncated iloc item id.");
        if (!find_item(graph.items, location.id) || !ids.insert(location.id).second)
            return fail("HEIF XMP rewrite: missing or duplicate iloc item identity.");
        uint16_t reference = 0, extents = 0;
        uint64_t base = 0;
        if ((version && !reader.try_read_be16u(location.method)) || location.method > 1 ||
            !reader.try_read_be16u(reference) || reference ||
            !read_uint_local(reader, base_width, base) || !reader.try_read_be16u(extents) || !extents)
            return fail("HEIF XMP rewrite: external reference, unsupported method, or malformed iloc entry.");
        if (location.method == 1 && !graph.has_idat)
            return fail("HEIF XMP rewrite: method1 item has no idat owner.");
        const uint64_t owner_size = location.method == 1 ? graph.idat.body_size : input.size();
        if (base > owner_size) return fail("HEIF XMP rewrite: iloc base exceeds owner.");
        for (uint16_t e = 0; e < extents; ++e) {
            uint64_t index = 0, offset = 0, length = 0;
            if (!read_uint_local(reader, index_width, index) || !read_uint_local(reader, offset_width, offset) ||
                !read_uint_local(reader, length_width, length) || !length ||
                offset > owner_size - base || length > owner_size - base - offset)
                return fail("HEIF XMP rewrite: truncated, empty, overflowing, or out-of-owner extent.");
            offset += base;
            const uint64_t absolute = offset + (location.method == 1 ? graph.idat.body_start : 0);
            if (location.method == 0) {
                bool payload_owned = false;
                for (const auto& box : graph.top) {
                    if (is_type(box, "mdat") && absolute >= box.body_start &&
                        absolute <= box.start + box.size && length <= box.start + box.size - absolute)
                        payload_owned = true;
                }
                if (graph.has_idat && absolute >= graph.idat.body_start &&
                    absolute <= graph.idat.start + graph.idat.size && length <= graph.idat.start + graph.idat.size - absolute)
                    payload_owned = true;
                if (!payload_owned)
                    return fail("HEIF XMP rewrite: method0 extent crosses or lacks a supported mdat/idat payload owner.");
            }
            all_extents.push_back({absolute, length});
            location.extents.push_back({offset, length, index});
            if (all_extents.size() > 1000000)
                return fail("HEIF XMP rewrite: exceeds the explicit one-million-extent resource limit.");
        }
        graph.locations.push_back(std::move(location));
    }
    if (reader.position() != iloc.start + iloc.size || !ids.count(graph.primary) || (graph.xmp && !ids.count(graph.xmp)))
        return fail("HEIF XMP rewrite: trailing iloc bytes or missing primary/XMP location.");
    std::sort(all_extents.begin(), all_extents.end(), [](const heif_extent& a, const heif_extent& b) { return a.offset < b.offset; });
    for (size_t i = 1; i < all_extents.size(); ++i)
        if (all_extents[i].offset < all_extents[i-1].offset + all_extents[i-1].length)
            return fail("HEIF XMP rewrite: overlapping or aliased item extents cannot be mutated safely.");
    return true;
}

void append_u64_xmp(std::vector<uint8_t>& output, uint64_t value) {
    append_u32(output, static_cast<uint32_t>(value >> 32));
    append_u32(output, static_cast<uint32_t>(value));
}
} // namespace

namespace lpb::containers {
bool observe_primary_item_properties(std::span<const uint8_t> input,
    heif_primary_properties& output, std::string& error) noexcept {
    output = {}; error.clear();
    try {
        xmp_rewrite_graph graph;
        if (!parse_xmp_rewrite_graph(input, graph, error)) return false;
        heif_primary_properties result;
        result.item_id = graph.primary;
        const auto* item = find_item(graph.items, graph.primary);
        // A generic HEIF brand alone does not establish HEVC encoding. Follow
        // the actual primary derived-image graph, never unrelated item types.
        std::function<bool(uint32_t, size_t)> hevc_primary = [&](uint32_t id, size_t depth) {
            if (depth > 256) return false;
            const auto* encoded = find_item(graph.items, id);
            if (!encoded) return false;
            const auto children = graph.derived_dependencies.find(id);
            if (encoded->type == 0x68766331u) return children == graph.derived_dependencies.end(); // hvc1
            if (encoded->type != 0x67726964u || children == graph.derived_dependencies.end() || children->second.empty()) return false; // grid
            return std::all_of(children->second.begin(), children->second.end(),
                [&](uint32_t child) { return hevc_primary(child, depth + 1); });
        };
        if (!hevc_primary(graph.primary, 0)) {
            error = "Target HEIC primary must be HEVC or an HEVC-backed grid."; return false;
        }
        result.item_type = item_type_string(item->type);
        for (const auto& location : graph.locations) if (location.id == graph.primary)
            for (const auto& extent : location.extents)
                result.encoding_extents.push_back({extent.offset + (location.method == 1 ? graph.idat.body_start : 0), extent.length});
        const locator_box* iprp = nullptr;
        for (const auto& child : graph.children) if (is_type(child, "iprp")) iprp = &child;
        if (!iprp) { error = "HEIF primary properties require an owned ispe association."; return false; }
        std::vector<locator_box> property_boxes;
        if (!parse_children_strict(input.data(), iprp->body_start, iprp->start + iprp->size, property_boxes)) return false;
        const locator_box *ipco = nullptr, *ipma = nullptr;
        for (const auto& child : property_boxes) {
            if (is_type(child, "ipco")) ipco = &child;
            if (is_type(child, "ipma")) ipma = &child;
        }
        if (!ipco || !ipma) { error = "HEIF primary property association table is incomplete."; return false; }
        std::vector<locator_box> properties;
        if (!parse_children_strict(input.data(), ipco->body_start, ipco->start + ipco->size, properties)) return false;
        const std::vector<uint8_t> raw(input.begin(), input.end());
        std::vector<ipma_entry> entries;
        if (!parse_ipma(raw, *ipma, entries, error)) return false;
        bool saw_size = false, saw_rotation = false, saw_mirror = false, saw_clap = false;
        std::array<uint32_t,8> aperture{};
        struct matrix { int a,b,c,d; } accumulated{1,0,0,1};
        for (const auto& entry : entries) {
            if (entry.item_id != graph.primary) continue;
            for (const auto association : entry.associations) {
                const size_t index = association & 0x7fffu;
                if (!index || index > properties.size()) { error = "HEIF primary property index is invalid."; return false; }
                const auto& property = properties[index - 1];
                if (is_type(property, "ispe")) {
                    if (saw_size || property.body_size != 12 || read_be32u(input.data() + property.body_start)) {
                        error = "HEIF primary has malformed or duplicate ispe authority."; return false;
                    }
                    saw_size = true;
                    result.width = read_be32u(input.data() + property.body_start + 4);
                    result.height = read_be32u(input.data() + property.body_start + 8);
                    if (!result.width || !result.height) { error = "HEIF primary ispe dimensions are empty."; return false; }
                } else if (is_type(property, "irot") || is_type(property, "imir")) {
                    if (property.body_size != 1) { error = "HEIF primary transform is malformed."; return false; }
                    const uint8_t value = input[property.body_start];
                    matrix transform{1,0,0,1};
                    if (is_type(property, "irot")) {
                        if (saw_rotation || (value & 0xfc)) { error = "HEIF primary rotation is duplicate or malformed."; return false; }
                        saw_rotation = true;
                        constexpr matrix rotations[] = {{1,0,0,1},{0,1,-1,0},{-1,0,0,-1},{0,-1,1,0}};
                        transform = rotations[value];
                    } else {
                        if (saw_mirror || (value & 0xfe)) { error = "HEIF primary mirror is duplicate or malformed."; return false; }
                        saw_mirror = true;
                        transform = value ? matrix{-1,0,0,1} : matrix{1,0,0,-1};
                    }
                    accumulated = {transform.a*accumulated.a + transform.b*accumulated.c,
                        transform.a*accumulated.b + transform.b*accumulated.d,
                        transform.c*accumulated.a + transform.d*accumulated.c,
                        transform.c*accumulated.b + transform.d*accumulated.d};
                } else if (is_type(property, "clap")) {
                    if (saw_clap || property.body_size != 32) {
                        error = "HEIF primary clean aperture is duplicate or malformed."; return false;
                    }
                    saw_clap = true;
                    for (size_t i=0;i<aperture.size();++i)
                        aperture[i]=read_be32u(input.data()+property.body_start+i*4);
                }
            }
        }
        if (!saw_size) { error = "HEIF primary has no owned ispe dimensions."; return false; }
        if (saw_clap) {
            // This observer admits exactly integral pixel crops. Signed offset
            // numerators are relative to the centre of the encoded image;
            // validate both edges before reporting the display dimensions.
            const auto crop = [](uint32_t encoded, uint32_t n, uint32_t d,
                uint32_t offset_bits, uint32_t offset_d, uint32_t& visible) {
                if (!n || !d || !offset_d || n%d || encoded>INT32_MAX) return false;
                const uint64_t extent=n/d;
                if (extent>encoded) return false;
                const int64_t offset=offset_bits<=INT32_MAX ? static_cast<int64_t>(offset_bits) :
                    static_cast<int64_t>(offset_bits)-0x100000000LL;
                const int64_t edge_n=static_cast<int64_t>(encoded-extent)*offset_d+2*offset;
                const uint64_t edge_d=2ULL*offset_d;
                if (edge_n<0 || static_cast<uint64_t>(edge_n)%edge_d) return false;
                const uint64_t edge=static_cast<uint64_t>(edge_n)/edge_d;
                if (edge>encoded-extent) return false;
                visible=static_cast<uint32_t>(extent); return true;
            };
            if (!crop(result.width,aperture[0],aperture[1],aperture[4],aperture[5],result.width) ||
                !crop(result.height,aperture[2],aperture[3],aperture[6],aperture[7],result.height)) {
                error="HEIF primary clean aperture is fractional or outside the encoded image."; return false;
            }
        }
        constexpr matrix exif[] = {{1,0,0,1},{-1,0,0,1},{-1,0,0,-1},{1,0,0,-1},
            {0,1,1,0},{0,-1,1,0},{0,-1,-1,0},{0,1,-1,0}};
        for (uint32_t i = 0; i < 8; ++i)
            if (accumulated.a==exif[i].a && accumulated.b==exif[i].b && accumulated.c==exif[i].c && accumulated.d==exif[i].d)
                result.orientation = i + 1;
        if (!result.orientation) { error = "HEIF primary transform cannot be normalized."; return false; }
        uint64_t exif_offset = 0, exif_length = 0;
        const auto located = locate_item(input.data(), input.size(), 0x45786966u, "Exif", &exif_offset, &exif_length, error);
        if (located == locator_result::malformed) return false;
        if (located == locator_result::present) {
            if (exif_length < 4 || exif_offset > input.size() || exif_length > input.size() - exif_offset) {
                error = "HEIF Exif item range is invalid."; return false;
            }
            const uint32_t tiff_offset = read_be32u(input.data() + static_cast<size_t>(exif_offset));
            if (tiff_offset > exif_length - 4 || exif_length - 4 - tiff_offset < 8) {
                error = "HEIF Exif encoded TIFF offset is invalid."; return false;
            }
            const auto tiff = input.subspan(static_cast<size_t>(exif_offset) + 4 + tiff_offset,
                static_cast<size_t>(exif_length) - 4 - tiff_offset);
            result.exif_payload = {'E','x','i','f',0,0};
            result.exif_payload.insert(result.exif_payload.end(), tiff.begin(), tiff.end());
        }
        error.clear(); output = std::move(result); return true;
    } catch (...) { output={}; error="HEIF primary property observation could not allocate or complete."; return false; }
}

bool read_primary_xmp(std::span<const uint8_t> input, uint32_t& primary_item_id,
    std::string& xml, std::string& error) noexcept {
    primary_item_id = 0; xml.clear(); error.clear();
    try {
        xmp_rewrite_graph graph;
        if (!parse_xmp_rewrite_graph(input, graph, error)) return false;
        for (const auto& location : graph.locations) if (location.id == graph.xmp) {
            for (const auto& extent : location.extents) {
                if (extent.length > 16u * 1024u * 1024u - xml.size()) {
                    xml.clear(); error = "HEIF XMP read: exceeds the explicit 16 MiB XML resource limit."; return false;
                }
                const uint64_t absolute = extent.offset + (location.method == 1 ? graph.idat.body_start : 0);
                xml.append(reinterpret_cast<const char*>(input.data() + static_cast<size_t>(absolute)), static_cast<size_t>(extent.length));
            }
        }
        primary_item_id = graph.primary;
        return true;
    } catch (...) { xml.clear(); error = "HEIF primary XMP graph read could not allocate or complete."; return false; }
}

bool rewrite_primary_xmp(std::span<const uint8_t> input, std::string_view replacement_xml,
    std::vector<uint8_t>& output, std::string& error) noexcept {
    output.clear(); error.clear();
    const auto fail = [&](const char* why) { output.clear(); error = why; return false; };
    try {
        if (replacement_xml.empty() || replacement_xml.size() > 16u * 1024u * 1024u)
            return fail("HEIF XMP rewrite: replacement must be nonempty and at most 16 MiB.");
        xmp_rewrite_graph graph;
        if (!parse_xmp_rewrite_graph(input, graph, error)) return false;
        std::vector<uint8_t> raw(input.begin(), input.end());
        std::vector<uint8_t> new_iinf(raw.begin() + static_cast<std::ptrdiff_t>(graph.iinf.start),
            raw.begin() + static_cast<std::ptrdiff_t>(graph.iinf.start + graph.iinf.size));
        std::vector<uint8_t> new_iref;
        if (graph.has_iref) new_iref.assign(raw.begin() + static_cast<std::ptrdiff_t>(graph.iref.start),
            raw.begin() + static_cast<std::ptrdiff_t>(graph.iref.start + graph.iref.size));
        const bool adding = graph.xmp == 0;
        if (adding) {
            std::set<uint32_t> ids;
            for (const auto& item : graph.items) ids.insert(item.id);
            graph.xmp = 1;
            while (ids.count(graph.xmp)) ++graph.xmp;
            if (graph.items.size() == 65535 || (graph.has_iref && raw[graph.iref.body_start] == 0 && (graph.xmp > 65535 || graph.primary > 65535)))
                return fail("HEIF XMP rewrite: no new item identity fits the supported item/reference capacity.");
            std::vector<uint8_t> infe_payload{3, 0, 0, 0};
            append_u32(infe_payload, graph.xmp); append_u16(infe_payload, 0);
            infe_payload.insert(infe_payload.end(), {'m','i','m','e',0});
            constexpr char mime[] = "application/rdf+xml";
            infe_payload.insert(infe_payload.end(), mime, mime + sizeof(mime));
            infe_payload.push_back(0); // empty content_encoding
            std::vector<uint8_t> infe;
            if (!make_box("infe", infe_payload, infe)) return fail("HEIF XMP rewrite: infe size overflow.");
            const size_t old_count_width = raw[graph.iinf.body_start] == 0 ? 2 : 4;
            std::vector<uint8_t> iinf_payload{1, 0, 0, 0};
            append_u32(iinf_payload, static_cast<uint32_t>(graph.items.size() + 1));
            iinf_payload.insert(iinf_payload.end(), raw.begin() + static_cast<std::ptrdiff_t>(graph.iinf.body_start + 4 + old_count_width),
                raw.begin() + static_cast<std::ptrdiff_t>(graph.iinf.start + graph.iinf.size));
            iinf_payload.insert(iinf_payload.end(), infe.begin(), infe.end());
            if (!make_box("iinf", iinf_payload, new_iinf)) return fail("HEIF XMP rewrite: iinf size overflow.");
            const bool wide = !graph.has_iref || raw[graph.iref.body_start] == 1;
            std::vector<uint8_t> cdsc_payload;
            if (wide) append_u32(cdsc_payload, graph.xmp); else append_u16(cdsc_payload, static_cast<uint16_t>(graph.xmp));
            append_u16(cdsc_payload, 1);
            if (wide) append_u32(cdsc_payload, graph.primary); else append_u16(cdsc_payload, static_cast<uint16_t>(graph.primary));
            std::vector<uint8_t> cdsc, iref_payload{static_cast<uint8_t>(wide ? 1 : 0), 0, 0, 0};
            if (!make_box("cdsc", cdsc_payload, cdsc)) return fail("HEIF XMP rewrite: cdsc size overflow.");
            if (graph.has_iref) iref_payload.insert(iref_payload.end(),
                raw.begin() + static_cast<std::ptrdiff_t>(graph.iref.body_start + 4),
                raw.begin() + static_cast<std::ptrdiff_t>(graph.iref.start + graph.iref.size));
            iref_payload.insert(iref_payload.end(), cdsc.begin(), cdsc.end());
            if (!make_box("iref", iref_payload, new_iref)) return fail("HEIF XMP rewrite: iref size overflow.");
            graph.locations.push_back({graph.xmp, 0, {{0, replacement_xml.size(), 0}}});
        }
        // The caller supplies the structurally merged packet.  Other item payloads
        // are never cleared, and overlap was rejected before touching these bytes.
        for (auto& location : graph.locations) if (location.id == graph.xmp) {
            if (!adding) for (const auto& extent : location.extents) {
                const uint64_t absolute = extent.offset + (location.method == 1 ? graph.idat.body_start : 0);
                std::fill(raw.begin() + static_cast<std::ptrdiff_t>(absolute),
                    raw.begin() + static_cast<std::ptrdiff_t>(absolute + extent.length), uint8_t{});
            }
            location.method = 0;
            location.extents = {{0, replacement_xml.size(), 0}};
        }
        const auto build_iloc = [&](std::vector<uint8_t>& bytes) {
            std::vector<uint8_t> payload{2, 0, 0, 0, 0x88, 0x08};
            append_u32(payload, static_cast<uint32_t>(graph.locations.size()));
            for (const auto& location : graph.locations) {
                append_u32(payload, location.id); append_u16(payload, location.method); append_u16(payload, 0);
                append_u16(payload, static_cast<uint16_t>(location.extents.size()));
                for (const auto& extent : location.extents) {
                    append_u64_xmp(payload, extent.index); append_u64_xmp(payload, extent.offset); append_u64_xmp(payload, extent.length);
                }
            }
            return make_box("iloc", payload, bytes);
        };
        std::vector<uint8_t> new_iloc, new_meta;
        if (!build_iloc(new_iloc)) return fail("HEIF XMP rewrite: iloc size overflow.");
        size_t new_idat_body = 0;
        const auto build_meta = [&]() {
            std::vector<uint8_t> payload(4, 0);
            for (const auto& child : graph.children) {
                const auto* replacement = is_type(child, "iinf") ? &new_iinf :
                    is_type(child, "iloc") ? &new_iloc : is_type(child, "iref") ? &new_iref : nullptr;
                if (is_type(child, "idat")) new_idat_body = graph.meta.start + 8 + payload.size() + child.header_size;
                if (replacement) payload.insert(payload.end(), replacement->begin(), replacement->end());
                else payload.insert(payload.end(), raw.begin() + static_cast<std::ptrdiff_t>(child.start),
                    raw.begin() + static_cast<std::ptrdiff_t>(child.start + child.size));
            }
            if (!graph.has_iref) payload.insert(payload.end(), new_iref.begin(), new_iref.end());
            return payload.size() <= 64u * 1024u * 1024u - 8u && make_box("meta", payload, new_meta);
        };
        if (!build_meta()) return fail("HEIF XMP rewrite: rebuilt meta exceeds the 64 MiB structural resource limit.");
        const uint64_t old_end = graph.meta.start + graph.meta.size;
        const uint64_t new_end = graph.meta.start + new_meta.size();
        const auto relocate = [&](uint64_t offset) -> uint64_t {
            if (graph.has_idat && offset >= graph.idat.body_start && offset < graph.idat.start + graph.idat.size)
                return new_idat_body + (offset - graph.idat.body_start);
            return offset >= old_end ? new_end + (offset - old_end) : offset;
        };
        if (input.size() - graph.meta.size > std::numeric_limits<size_t>::max() - new_meta.size() - 8 - replacement_xml.size())
            return fail("HEIF XMP rewrite: output size overflow.");
        const uint64_t xmp_offset = input.size() - graph.meta.size + new_meta.size() + 8;
        for (auto& location : graph.locations) {
            if (location.id == graph.xmp) location.extents.front().offset = xmp_offset;
            else if (location.method == 0) for (auto& extent : location.extents) extent.offset = relocate(extent.offset);
        }
        if (!build_iloc(new_iloc) || !build_meta()) return fail("HEIF XMP rewrite: final table assembly failed.");
        std::vector<uint8_t> result;
        result.reserve(static_cast<size_t>(xmp_offset) + replacement_xml.size());
        result.insert(result.end(), raw.begin(), raw.begin() + static_cast<std::ptrdiff_t>(graph.meta.start));
        result.insert(result.end(), new_meta.begin(), new_meta.end());
        result.insert(result.end(), raw.begin() + static_cast<std::ptrdiff_t>(old_end), raw.end());
        append_u32(result, static_cast<uint32_t>(replacement_xml.size() + 8));
        result.insert(result.end(), {'m','d','a','t'});
        result.insert(result.end(), replacement_xml.begin(), replacement_xml.end());
        output = std::move(result);
        return true;
    } catch (const std::exception& ex) { output.clear(); error = ex.what(); return false; }
    catch (...) { return fail("HEIF XMP rewrite: allocation or internal failure."); }
}
} // namespace lpb::containers

extern "C" LPB_API lpb_result LPB_CALL lpb_heif_enumerate_auxiliary_items(
    lpb_context* context, const uint8_t* input, size_t input_size,
    lpb_auxiliary_item_facts* output_items, size_t output_capacity, size_t* output_count)
{
    if (!context || !input || !output_count || (output_capacity > 0 && !output_items)) return LPB_RESULT_INVALID_ARGUMENT;
    try {
        *output_count = 0;
        std::vector<lpb_auxiliary_item_facts> items;
        lpb::containers::heif_graph_diagnostic diagnostic;
        if (!lpb::containers::enumerate_auxiliary_items({input, input_size}, items, diagnostic)) {
            set_error(context, diagnostic.message.c_str());
            return diagnostic.code == heif_graph_error::internal_failure
                ? LPB_RESULT_INTERNAL_ERROR : LPB_RESULT_INVALID_ARGUMENT;
        }
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
