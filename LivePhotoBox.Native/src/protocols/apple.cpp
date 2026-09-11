#include <cstdio>
#include "protocols/apple.h"
#include "foundation/residue_fingerprint.h"
#include "foundation/internal.h"
#include "binary/endian.h"
#include "containers/isobmff.h"
#include <cstring>
#include <algorithm>
#include <vector>
#include <string>
#include <limits>

namespace {
static bool is_box_type(const uint8_t* p, const char* type) {
    return p[4] == static_cast<uint8_t>(type[0]) && 
           p[5] == static_cast<uint8_t>(type[1]) && 
           p[6] == static_cast<uint8_t>(type[2]) && 
           p[7] == static_cast<uint8_t>(type[3]);
}

static bool find_box(
    const uint8_t* data, size_t start, size_t end, const char* type,
    size_t& box_start, size_t& box_len, size_t& body_start) {
    if (!data || !type || start > end) return false;
    size_t p = start;
    while (p < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, p, end, box)) return false;
        if (is_box_type(data + p, type)) {
            box_start = p;
            box_len = box.size;
            body_start = p + box.header_size;
            return true;
        }
        p += box.size;
    }
    return false;
}

static bool find_unique_box_local(
    const uint8_t* data, size_t start, size_t end, const char* type,
    size_t& box_start, size_t& box_len, size_t& body_start, bool& duplicate) {
    duplicate = false;
    if (!data || !type || start > end) return false;
    bool found = false;
    size_t p = start;
    while (p < end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, p, end, box)) return false;
        if (is_box_type(data + p, type)) {
            if (found) {
                duplicate = true;
                return false;
            }
            found = true;
            box_start = p;
            box_len = box.size;
            body_start = p + box.header_size;
        }
        p += box.size;
    }
    return found;
}

static bool has_complete_box_sequence(const uint8_t* data, size_t data_size) noexcept
{
    if (!data || data_size < 8) return false;
    size_t position = 0;
    while (position < data_size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, position, data_size, box)) return false;
        position += box.size;
    }
    return position == data_size;
}

static void write_uint(uint8_t* p, uint64_t val, size_t size) {
    for (size_t i = 0; i < size; ++i) {
        p[size - 1 - i] = static_cast<uint8_t>(val & 0xFF);
        val >>= 8;
    }
}

static bool fits_uint(uint64_t value, size_t size) noexcept {
    if (size == 0 || size > 8) return false;
    return size == 8 || value < (uint64_t{1} << (size * 8));
}

static bool try_find_exif_iloc_fields(
    const uint8_t* data, size_t data_size,
    size_t iloc_body, size_t iloc_end,
    uint32_t target_item_id,
    size_t& base_field_pos, size_t& length_field_pos,
    size_t& base_size, size_t& length_size)
{
    if (!data || iloc_body > iloc_end || iloc_end > data_size) return false;
    size_t p = iloc_body;
    const size_t end = iloc_end;
    if (p + 6 > end) return false;
    uint8_t version = data[p];
    if (version > 2) return false;
    p += 4;
    uint8_t offset_size = (data[p] >> 4) & 0x0F;
    length_size = data[p] & 0x0F;
    base_size = (data[p+1] >> 4) & 0x0F;
    uint8_t index_size = data[p+1] & 0x0F;
    p += 2;
    uint32_t count = 0;
    if (version < 2) {
        if (p + 2 > end) return false;
        count = (static_cast<uint16_t>(data[p]) << 8) | data[p+1];
        p += 2;
    } else {
        if (p + 4 > end) return false;
        count = read_be32u(data + p);
        p += 4;
    }
    bool found_target = false;
    for (uint32_t i = 0; i < count; i++) {
        uint32_t item_id;
        if (version < 2) {
            if (p + 2 > end) return false;
            item_id = (static_cast<uint16_t>(data[p]) << 8) | data[p+1];
            p += 2;
        } else {
            if (p + 4 > end) return false;
            item_id = read_be32u(data + p);
            p += 4;
        }
        uint16_t construction_method = 0;
        if (version == 1 || version == 2) {
            if (p + 2 > end) return false;
            construction_method = read_be16u(data + p) & 0x000F;
            p += 2;
        }
        if (p + 2 > end) return false;
        const uint16_t data_reference_index = read_be16u(data + p);
        p += 2;
        size_t current_base_pos = p;
        if (base_size > 0) {
            if (p > end || base_size > end - p) return false;
            p += base_size;
        }
        if (p + 2 > end) return false;
        uint16_t extent_count = (static_cast<uint16_t>(data[p]) << 8) | data[p+1];
        p += 2;
        if (item_id == target_item_id && (found_target || construction_method != 0 || data_reference_index != 0 || extent_count != 1)) return false;
        size_t current_len_pos = 0;
        for (uint16_t e = 0; e < extent_count; e++) {
            if ((version == 1 || version == 2) && index_size > 0) {
                if (p > end || index_size > end - p) return false;
                p += index_size;
            }
            if (p > end || offset_size > end - p) return false;
            p += offset_size;
            if (e == 0) current_len_pos = p;
            if (p > end || length_size > end - p) return false;
            p += length_size;
        }
        if (item_id == target_item_id) {
            if (current_len_pos == 0) return false;
            base_field_pos = current_base_pos;
            length_field_pos = current_len_pos;
            found_target = true;
        }
    }
    return found_target && p == iloc_end;
}

static bool try_relocate_exif_to_mdat_end(
    lpb_context* context,
    const uint8_t* data, size_t data_size,
    uint32_t target_item_id,
    const std::vector<uint8_t>& new_tiff,
    std::vector<uint8_t>& patched)
{
    size_t p = 0;
    ptrdiff_t mdat_start = -1;
    size_t mdat_size = 0;
    size_t mdat_header = 8;
    while (p < data_size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, p, data_size, box)) return false;
        if (is_box_type(data + p, "mdat")) {
            mdat_start = p;
            mdat_size = box.size;
            mdat_header = box.header_size;
            if (p + box.size != data_size) {
                set_error(context, "mdat is not the last box.");
                return false;
            }
            break;
        }
        p += box.size;
    }
    if (mdat_start < 0) return false;

    size_t meta_start, meta_len, meta_body;
    if (!find_box(data, 0, data_size, "meta", meta_start, meta_len, meta_body)) return false;
    size_t iloc_start, iloc_len, iloc_body;
    if (!find_box(data, meta_body + 4, meta_start + meta_len, "iloc", iloc_start, iloc_len, iloc_body)) return false;

    size_t base_field_pos = 0, length_field_pos = 0;
    size_t base_size = 0, length_size = 0;
    if (!try_find_exif_iloc_fields(data, data_size, iloc_body, iloc_start + iloc_len, target_item_id, base_field_pos, length_field_pos, base_size, length_size)) {
        set_error(context, "Failed to find Exif item iloc fields.");
        return false;
    }
    const uint64_t new_base = static_cast<uint64_t>(mdat_start) + mdat_size;
    if (base_size < 4 || !fits_uint(new_base, base_size)) {
        set_error(context, "iloc base_offset size too small.");
        return false;
    }
    if (length_size < 4 || !fits_uint(new_tiff.size(), length_size)) {
        set_error(context, "iloc length size too small.");
        return false;
    }

    if (new_tiff.size() > std::numeric_limits<size_t>::max() - data_size) return false;
    patched.resize(data_size + new_tiff.size());
    std::memcpy(patched.data(), data, data_size);
    std::memcpy(patched.data() + data_size, new_tiff.data(), new_tiff.size());

    if (mdat_header == 8) {
        uint32_t old_size = read_be32u(patched.data() + mdat_start);
        if (old_size == 0) {
            uint64_t explicit_sz = data_size - mdat_start + new_tiff.size();
            if (explicit_sz > 0xFFFFFFFF) return false;
            write_be32(patched.data() + mdat_start, static_cast<int32_t>(explicit_sz));
        } else if (old_size == 1) {
            return false;
        } else {
            if (new_tiff.size() > std::numeric_limits<uint32_t>::max() - old_size) return false;
            write_be32(patched.data() + mdat_start, static_cast<int32_t>(old_size + static_cast<uint32_t>(new_tiff.size())));
        }
    } else {
        uint64_t old_size = static_cast<uint64_t>(read_be64(patched.data() + mdat_start + 8));
        if (old_size > std::numeric_limits<uint64_t>::max() - new_tiff.size()) return false;
        uint64_t new_sz = old_size + new_tiff.size();
        patched[mdat_start + 8] = static_cast<uint8_t>(new_sz >> 56);
        patched[mdat_start + 9] = static_cast<uint8_t>(new_sz >> 48);
        patched[mdat_start + 10] = static_cast<uint8_t>(new_sz >> 40);
        patched[mdat_start + 11] = static_cast<uint8_t>(new_sz >> 32);
        patched[mdat_start + 12] = static_cast<uint8_t>(new_sz >> 24);
        patched[mdat_start + 13] = static_cast<uint8_t>(new_sz >> 16);
        patched[mdat_start + 14] = static_cast<uint8_t>(new_sz >> 8);
        patched[mdat_start + 15] = static_cast<uint8_t>(new_sz & 0xFF);
    }
    write_uint(patched.data() + base_field_pos, new_base, base_size);
    write_uint(patched.data() + length_field_pos, static_cast<uint64_t>(new_tiff.size()), length_size);
    return true;
}

// Convert Exif type to length (0 if inline value)
static int type_to_data_length(uint16_t type, uint32_t count) {
    int unit = 0;
    switch (type) {
        case 1: case 2: case 7: unit = 1; break;
        case 3: case 8: unit = 2; break;
        case 4: case 9: unit = 4; break;
        case 5: case 10: unit = 8; break;
        case 6: case 11: unit = 4; break;
        case 12: unit = 8; break;
        case 13: case 14: unit = 4; break;
        case 16: unit = 8; break;
    }
    if (unit == 0 || count > static_cast<uint64_t>(std::numeric_limits<int>::max()) / static_cast<uint64_t>(unit)) return -1;
    const uint64_t len = static_cast<uint64_t>(unit) * count;
    return len > 4 ? static_cast<int>(len) : 0;
}

struct maker_note_region {
    size_t start{};
    size_t end{};
};

static size_t maker_note_directory_end(const uint8_t* data, size_t size, size_t start, uint16_t count) noexcept {
    const size_t entries_end = start + 16 + static_cast<size_t>(count) * 12;
    // Apple MakerNotes normally carry the four-byte next-IFD field. Some
    // minimal/vendor notes omit it and put the first payload byte here; only
    // consume the field when it is the conventional zero value.
    if (entries_end <= size && size - entries_end >= 4 &&
        data[entries_end] == 0 && data[entries_end + 1] == 0 &&
        data[entries_end + 2] == 0 && data[entries_end + 3] == 0) {
        return entries_end + 4;
    }
    return entries_end;
}

static bool read_tiff_u16(const uint8_t* data, size_t start, size_t end, size_t at, bool little, uint16_t& out) noexcept {
    (void)start;
    if (at > end || end - at < 2) return false;
    out = little ? static_cast<uint16_t>(data[at]) | (static_cast<uint16_t>(data[at + 1]) << 8) : read_be16u(data + at);
    return true;
}

static bool read_tiff_u32(const uint8_t* data, size_t start, size_t end, size_t at, bool little, uint32_t& out) noexcept {
    (void)start;
    if (at > end || end - at < 4) return false;
    out = little ? static_cast<uint32_t>(data[at]) | (static_cast<uint32_t>(data[at + 1]) << 8) |
        (static_cast<uint32_t>(data[at + 2]) << 16) | (static_cast<uint32_t>(data[at + 3]) << 24) : read_be32u(data + at);
    return true;
}

// Mutation entry points must resolve MakerNote ownership through a real
// JPEG/HEIF hierarchy.  This is defined after the formal Exif parser below.
static bool locate_formal_makernote_owner(
    lpb_context* context, const uint8_t* data, size_t data_size,
    maker_note_region& out) noexcept;

static std::vector<uint8_t> build_heif_exif_item(const uint8_t* makernote, size_t makernote_size) {
    // HEIF Exif item: a four-byte TIFF-header offset, the Exif marker, then a
    // minimal big-endian TIFF whose ExifIFD contains one MakerNote tag.
    std::vector<uint8_t> tiff(44 + makernote_size, 0);
    tiff[0] = 'M'; tiff[1] = 'M';
    write_be16(tiff.data() + 2, 42);
    write_be32(tiff.data() + 4, 8);
    write_be16(tiff.data() + 8, 1);
    write_be16(tiff.data() + 10, 0x8769);
    write_be16(tiff.data() + 12, 4);
    write_be32(tiff.data() + 14, 1);
    write_be32(tiff.data() + 18, 26);
    write_be32(tiff.data() + 22, 0);
    write_be16(tiff.data() + 26, 1);
    write_be16(tiff.data() + 28, 0x927C);
    write_be16(tiff.data() + 30, 7);
    write_be32(tiff.data() + 32, static_cast<uint32_t>(makernote_size));
    write_be32(tiff.data() + 36, 44);
    write_be32(tiff.data() + 40, 0);
    if (makernote_size > 0) std::memcpy(tiff.data() + 44, makernote, makernote_size);

    std::vector<uint8_t> item(10 + tiff.size(), 0);
    write_be32(item.data(), 6);
    std::memcpy(item.data() + 4, "Exif\0\0", 6);
    std::memcpy(item.data() + 10, tiff.data(), tiff.size());
    return item;
}

static bool append_heif_exif_cdsc(
    const uint8_t* input, size_t iref_start, size_t iref_len,
    const std::vector<uint32_t>& item_ids, uint32_t primary_item_id,
    uint32_t exif_item_id, std::vector<uint8_t>& output) {
    if (!input || iref_len < 12 || iref_start > std::numeric_limits<size_t>::max() - iref_len) return false;
    const size_t end = iref_start + iref_len;
    const size_t body = iref_start + 8;
    if (input[body] > 1 || input[body + 1] != 0 || input[body + 2] != 0 || input[body + 3] != 0) return false;
    const uint8_t version = input[body];
    const size_t id_width = version == 0 ? 2 : 4;
    if (std::find(item_ids.begin(), item_ids.end(), primary_item_id) == item_ids.end() ||
        std::find(item_ids.begin(), item_ids.end(), exif_item_id) != item_ids.end()) return false;

    size_t p = body + 4;
    std::vector<std::pair<uint32_t, uint32_t>> seen_relations;
    while (p < end) {
        isobmff_box_header reference{};
        if (!try_read_box_header(input, p, end, reference)) return false;
        const size_t reference_end = p + reference.size;
        if (is_box_type(input + p, "cdsc")) {
            size_t rp = p + reference.header_size;
            if (rp > reference_end || reference_end - rp < id_width + 2) return false;
            const uint32_t from = id_width == 2 ? read_be16u(input + rp) : read_be32u(input + rp);
            rp += id_width;
            const uint16_t count = read_be16u(input + rp);
            rp += 2;
            if (count == 0 || std::find(item_ids.begin(), item_ids.end(), from) == item_ids.end()) return false;
            std::vector<uint32_t> targets;
            targets.reserve(count);
            for (uint16_t index = 0; index < count; ++index) {
                if (rp > reference_end || reference_end - rp < id_width) return false;
                const uint32_t to = id_width == 2 ? read_be16u(input + rp) : read_be32u(input + rp);
                rp += id_width;
                if (to == from || std::find(item_ids.begin(), item_ids.end(), to) == item_ids.end() ||
                    std::find(targets.begin(), targets.end(), to) != targets.end()) return false;
                if (std::find(seen_relations.begin(), seen_relations.end(), std::pair<uint32_t, uint32_t>{from, to}) != seen_relations.end()) return false;
                seen_relations.push_back({from, to});
                targets.push_back(to);
            }
            if (rp != reference_end) return false;
            if (from == exif_item_id) return false;
        }
        p = reference_end;
    }
    if (p != end) return false;

    const size_t child_size = 8 + id_width + 2 + id_width;
    if (iref_len > std::numeric_limits<size_t>::max() - child_size ||
        iref_len + child_size > std::numeric_limits<uint32_t>::max()) return false;
    output.assign(input + iref_start, input + end);
    output.resize(iref_len + child_size, 0);
    write_be32(output.data(), static_cast<uint32_t>(output.size()));
    uint8_t* child = output.data() + iref_len;
    write_be32(child, static_cast<uint32_t>(child_size));
    std::memcpy(child + 4, "cdsc", 4);
    if (id_width == 2) {
        write_be16(child + 8, static_cast<uint16_t>(exif_item_id));
        write_be16(child + 10, 1);
        write_be16(child + 12, static_cast<uint16_t>(primary_item_id));
    } else {
        write_be32(child + 8, exif_item_id);
        write_be16(child + 12, 1);
        write_be32(child + 14, primary_item_id);
    }
    return true;
}

static bool add_heif_exif_item(
    lpb_context* context,
    const uint8_t* input, size_t input_size,
    const uint8_t* makernote, size_t makernote_size,
    std::vector<uint8_t>& output)
{
    if (!has_complete_box_sequence(input, input_size)) {
        set_error(context, "Input HEIF contains a malformed top-level box.");
        return false;
    }
    size_t meta_start, meta_len, meta_body;
    if (!find_box(input, 0, input_size, "meta", meta_start, meta_len, meta_body)) {
        set_error(context, "No meta box found while creating HEIF Exif item.");
        return false;
    }
    size_t meta_end = meta_start + meta_len;
    if (meta_body + 4 > meta_end || !has_complete_box_sequence(input + meta_body + 4, meta_end - (meta_body + 4))) {
        set_error(context, "HEIF meta child region is malformed while creating Exif item.");
        return false;
    }
    size_t iinf_start = 0, iinf_len = 0, iinf_body = 0;
    size_t iloc_start = 0, iloc_len = 0, iloc_body = 0;
    size_t pitm_start = 0, pitm_len = 0, pitm_body = 0;
    size_t iref_start = 0, iref_len = 0, iref_body = 0;
    size_t idat_start = 0, idat_len = 0, idat_body = 0;
    bool duplicate = false;
    if (!find_unique_box_local(input, meta_body + 4, meta_end, "iinf", iinf_start, iinf_len, iinf_body, duplicate) || duplicate ||
        !find_unique_box_local(input, meta_body + 4, meta_end, "iloc", iloc_start, iloc_len, iloc_body, duplicate) || duplicate ||
        !find_unique_box_local(input, meta_body + 4, meta_end, "pitm", pitm_start, pitm_len, pitm_body, duplicate) || duplicate) {
        set_error(context, "HEIF meta lacks iinf or iloc for Exif item creation.");
        return false;
    }
    bool have_iref = find_unique_box_local(input, meta_body + 4, meta_end, "iref", iref_start, iref_len, iref_body, duplicate);
    if (duplicate) {
        set_error(context, "HEIF meta contains duplicate iref boxes for Exif item creation.");
        return false;
    }
    const bool have_idat = find_box(input, meta_body + 4, meta_end, "idat", idat_start, idat_len, idat_body);

    const size_t iinf_end = iinf_start + iinf_len;
    if (iinf_body > iinf_end || iinf_end - iinf_body < 6 || input[iinf_body] != 0 ||
        input[iinf_body + 1] != 0 || input[iinf_body + 2] != 0 || input[iinf_body + 3] != 0) {
        set_error(context, "Unsupported HEIF iinf layout for Exif item creation.");
        return false;
    }
    const uint16_t iinf_count = read_be16u(input + iinf_body + 4);
    std::vector<uint32_t> existing_item_ids;
    existing_item_ids.reserve(iinf_count);
    size_t iinf_child = iinf_body + 6;
    for (uint16_t index = 0; index < iinf_count; ++index) {
        isobmff_box_header infe{};
        if (!try_read_box_header(input, iinf_child, iinf_end, infe) || !is_box_type(input + iinf_child, "infe") ||
            infe.size < infe.header_size + 8) {
            set_error(context, "HEIF iinf contains a malformed infe while creating Exif item.");
            return false;
        }
        const size_t infe_body = iinf_child + infe.header_size;
        const uint8_t infe_version = input[infe_body];
        uint32_t item_id = 0;
        size_t item_type_pos = 0;
        if (infe_version == 2) {
            if (infe.size < infe.header_size + 8) return false;
            item_id = read_be16u(input + infe_body + 4);
            item_type_pos = infe_body + 8;
        } else if (infe_version == 3) {
            if (infe.size < infe.header_size + 10) return false;
            item_id = read_be32u(input + infe_body + 4);
            item_type_pos = infe_body + 10;
        } else {
            set_error(context, "Unsupported HEIF infe version while creating Exif item.");
            return false;
        }
        if (item_id == 0 || std::find(existing_item_ids.begin(), existing_item_ids.end(), item_id) != existing_item_ids.end()) {
            set_error(context, "HEIF iinf contains duplicate or zero item ids.");
            return false;
        }
        if (item_type_pos + 4 > iinf_child + infe.size) return false;
        if (std::memcmp(input + item_type_pos, "Exif", 4) == 0) {
            set_error(context, "HEIF already contains an Exif item without a unique new owner graph.");
            return false;
        }
        existing_item_ids.push_back(item_id);
        iinf_child += infe.size;
    }
    if (iinf_child != iinf_end) {
        set_error(context, "HEIF iinf has trailing bytes while creating Exif item.");
        return false;
    }
    if (pitm_body > meta_end || pitm_len < 14) {
        set_error(context, "HEIF pitm is malformed while creating Exif item.");
        return false;
    }
    const uint8_t pitm_version = input[pitm_body];
    if (pitm_version > 1 || input[pitm_body + 1] != 0 || input[pitm_body + 2] != 0 || input[pitm_body + 3] != 0 ||
        (pitm_version == 0 && pitm_len < 14) || (pitm_version == 1 && pitm_len < 16)) {
        set_error(context, "HEIF pitm version or flags are malformed while creating Exif item.");
        return false;
    }
    const uint32_t primary_item_id = pitm_version == 0
        ? read_be16u(input + pitm_body + 4) : read_be32u(input + pitm_body + 4);
    if (primary_item_id == 0 || std::find(existing_item_ids.begin(), existing_item_ids.end(), primary_item_id) == existing_item_ids.end()) {
        set_error(context, "HEIF pitm does not identify an existing primary item.");
        return false;
    }

    const size_t iloc_end = iloc_start + iloc_len;
    if (iloc_body + 8 > iloc_end || input[iloc_body] != 1 ||
        input[iloc_body + 1] != 0 || input[iloc_body + 2] != 0 || input[iloc_body + 3] != 0 ||
        (input[iloc_body + 4] >> 4) != 4 || (input[iloc_body + 4] & 0x0F) != 4 ||
        (input[iloc_body + 5] >> 4) != 0 || (input[iloc_body + 5] & 0x0F) != 0) {
        set_error(context, "Unsupported HEIF iloc layout for Exif item creation.");
        return false;
    }

    uint32_t item_count = (static_cast<uint16_t>(input[iloc_body + 6]) << 8) | input[iloc_body + 7];
    size_t p = iloc_body + 8;
    uint32_t max_item_id = 0;
    for (uint32_t id : existing_item_ids) max_item_id = std::max(max_item_id, id);
    for (uint32_t i = 0; i < item_count; ++i) {
        if (p + 8 > iloc_end) {
            set_error(context, "Truncated HEIF iloc while creating Exif item.");
            return false;
        }
        uint32_t item_id = (static_cast<uint16_t>(input[p]) << 8) | input[p + 1];
        uint16_t construction_method = (static_cast<uint16_t>(input[p + 2]) << 8) | input[p + 3];
        uint16_t extent_count = (static_cast<uint16_t>(input[p + 6]) << 8) | input[p + 7];
        max_item_id = std::max(max_item_id, item_id);
        if (std::find(existing_item_ids.begin(), existing_item_ids.end(), item_id) == existing_item_ids.end()) {
            set_error(context, "HEIF iloc references an unknown item while creating Exif item.");
            return false;
        }
        if (construction_method == 0 || construction_method == 1) {
            size_t extent = p + 8;
            for (uint16_t e = 0; e < extent_count; ++e) {
                if (extent + 8 > iloc_end) {
                    set_error(context, "Truncated HEIF extent while creating Exif item.");
                    return false;
                }
                uint32_t old_offset = read_be32u(input + extent);
                if (old_offset > 0xFFFFFFFFu - (21u + 16u)) {
                    set_error(context, "HEIF iloc offset overflow while creating Exif item.");
                    return false;
                }
                const uint32_t old_length = read_be32u(input + extent + 4);
                const uint64_t owner_size = construction_method == 1 && have_idat
                    ? static_cast<uint64_t>(idat_start + idat_len - idat_body)
                    : static_cast<uint64_t>(input_size);
                if (construction_method == 1 && !have_idat) {
                    set_error(context, "HEIF iloc construction method 1 has no owning idat box.");
                    return false;
                }
                if (static_cast<uint64_t>(old_offset) > owner_size ||
                    static_cast<uint64_t>(old_length) > owner_size - old_offset) {
                    set_error(context, "HEIF iloc extent exceeds its owning data range.");
                    return false;
                }
                extent += 8;
            }
        } else {
            set_error(context, "Unsupported HEIF construction method for Exif item creation.");
            return false;
        }
        p += 8 + static_cast<size_t>(extent_count) * 8;
    }
    if (p != iloc_end || max_item_id >= 0xFFFFu || item_count >= 0xFFFFu) {
        set_error(context, "Unsupported HEIF iloc contents for Exif item creation.");
        return false;
    }

    if (iinf_len > std::numeric_limits<size_t>::max() - 21 ||
        iloc_len > std::numeric_limits<size_t>::max() - 16 ||
        input_size > std::numeric_limits<size_t>::max() - (21 + 16) ||
        makernote_size > std::numeric_limits<size_t>::max() - 32) {
        set_error(context, "HEIF Exif item size overflow.");
        return false;
    }

    const uint32_t exif_item_id = max_item_id + 1;
    if (exif_item_id == 0 || exif_item_id > 0xFFFFu) {
        set_error(context, "HEIF Exif item id cannot be represented by the selected iloc/iinf layout.");
        return false;
    }
    std::vector<uint8_t> new_iref;
    if (have_iref) {
        if (!append_heif_exif_cdsc(input, iref_start, iref_len, existing_item_ids,
            primary_item_id, exif_item_id, new_iref)) {
            set_error(context, "HEIF iref is malformed, duplicated, or cannot own the new Exif item.");
            return false;
        }
    } else {
        constexpr size_t child_size = 14;
        new_iref.assign(8 + 4 + child_size, 0);
        write_be32(new_iref.data(), static_cast<uint32_t>(new_iref.size()));
        std::memcpy(new_iref.data() + 4, "iref", 4);
        write_be32(new_iref.data() + 8, static_cast<uint32_t>(0));
        write_be32(new_iref.data() + 12, static_cast<uint32_t>(child_size));
        std::memcpy(new_iref.data() + 16, "cdsc", 4);
        write_be16(new_iref.data() + 20, static_cast<uint16_t>(exif_item_id));
        write_be16(new_iref.data() + 22, 1);
        write_be16(new_iref.data() + 24, static_cast<uint16_t>(primary_item_id));
    }
    if (new_iref.size() < (have_iref ? iref_len : 0)) return false;
    const size_t iref_delta = new_iref.size() - (have_iref ? iref_len : 0);
    if (iref_delta > std::numeric_limits<size_t>::max() - 37 ||
        input_size > std::numeric_limits<size_t>::max() - (37 + iref_delta)) {
        set_error(context, "HEIF Exif metadata graph size overflows the host size type.");
        return false;
    }
    // The new iinf entry, iloc entry and cdsc owner relation enlarge meta.
    // Existing absolute extents move with the enlarged metadata box.
    const size_t metadata_delta = 21 + 16 + iref_delta;
    if (metadata_delta > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "HEIF Exif metadata graph delta exceeds 32-bit box fields.");
        return false;
    }
    std::vector<uint8_t> new_iinf(input + iinf_start, input + iinf_start + iinf_len);
    new_iinf.resize(iinf_len + 21, 0);
    if (new_iinf.size() > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "HEIF iinf box exceeds its 32-bit size field.");
        return false;
    }
    write_be32(new_iinf.data(), static_cast<uint32_t>(new_iinf.size()));
    uint16_t old_iinf_count = (static_cast<uint16_t>(new_iinf[12]) << 8) | new_iinf[13];
    if (old_iinf_count != item_count) {
        set_error(context, "HEIF iinf/iloc item counts differ.");
        return false;
    }
    write_be16(new_iinf.data() + 12, static_cast<uint16_t>(old_iinf_count + 1));
    uint8_t* infe = new_iinf.data() + iinf_len;
    write_be32(infe, 21);
    std::memcpy(infe + 4, "infe", 4);
    infe[8] = 2;
    write_be16(infe + 12, static_cast<uint16_t>(exif_item_id));
    std::memcpy(infe + 16, "Exif", 4);

    std::vector<uint8_t> new_iloc(input + iloc_start, input + iloc_start + iloc_len);
    new_iloc.resize(iloc_len + 16, 0);
    if (new_iloc.size() > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "HEIF iloc box exceeds its 32-bit size field.");
        return false;
    }
    write_be32(new_iloc.data(), static_cast<uint32_t>(new_iloc.size()));
    // iloc box header is 8 bytes; version/flags are at 8..11, sizes at
    // 12..13, and the version-1 item count is at 14..15.
    write_be16(new_iloc.data() + 14, static_cast<uint16_t>(item_count + 1));
    p = 16;
    for (uint32_t i = 0; i < item_count; ++i) {
        uint16_t extent_count = (static_cast<uint16_t>(new_iloc[p + 6]) << 8) | new_iloc[p + 7];
        uint16_t construction_method = (static_cast<uint16_t>(new_iloc[p + 2]) << 8) | new_iloc[p + 3];
        if (construction_method == 0) {
            size_t extent = p + 8;
            for (uint16_t e = 0; e < extent_count; ++e) {
                uint32_t old_offset = read_be32u(new_iloc.data() + extent);
                if (old_offset > std::numeric_limits<uint32_t>::max() - metadata_delta) {
                    set_error(context, "HEIF iloc offset overflows after metadata owner update.");
                    return false;
                }
                write_be32(new_iloc.data() + extent, old_offset + static_cast<uint32_t>(metadata_delta));
                extent += 8;
            }
        }
        p += 8 + static_cast<size_t>(extent_count) * 8;
    }
    write_be16(new_iloc.data() + p, static_cast<uint16_t>(exif_item_id));
    write_be16(new_iloc.data() + p + 2, 0);
    write_be16(new_iloc.data() + p + 4, 0);
    write_be16(new_iloc.data() + p + 6, 1);
    std::vector<uint8_t> exif_item = build_heif_exif_item(makernote, makernote_size);
    size_t new_mdat_start = input_size + metadata_delta;
    if (exif_item.size() > std::numeric_limits<uint32_t>::max() - 8 ||
        new_mdat_start > std::numeric_limits<uint32_t>::max() - 8 ||
        meta_len > std::numeric_limits<uint32_t>::max() - metadata_delta) {
        set_error(context, "HEIF Exif item cannot be represented by 32-bit fields.");
        return false;
    }
    if (input_size > std::numeric_limits<size_t>::max() - metadata_delta - (8 + exif_item.size())) {
        set_error(context, "HEIF Exif output size overflows the host size type.");
        return false;
    }
    write_be32(new_iloc.data() + p + 8, static_cast<uint32_t>(new_mdat_start + 8));
    write_be32(new_iloc.data() + p + 12, static_cast<uint32_t>(exif_item.size()));

    std::vector<uint8_t> new_meta;
    new_meta.reserve(meta_len + metadata_delta);
    new_meta.resize(12);
    write_be32(new_meta.data(), static_cast<uint32_t>(meta_len + metadata_delta));
    std::memcpy(new_meta.data() + 4, "meta", 4);
    std::memcpy(new_meta.data() + 8, input + meta_body, 4);
    p = meta_body + 4;
    while (p < meta_end) {
        if (p + 8 > meta_end) {
            set_error(context, "Truncated HEIF meta while creating Exif item.");
            return false;
        }
        uint32_t box_size = read_be32u(input + p);
        if (box_size < 8 || p + box_size > meta_end) {
            set_error(context, "Invalid HEIF meta child while creating Exif item.");
            return false;
        }
        const std::vector<uint8_t>* replacement = nullptr;
        if (p == iinf_start) replacement = &new_iinf;
        if (p == iloc_start) replacement = &new_iloc;
        if (have_iref && p == iref_start) replacement = &new_iref;
        if (replacement != nullptr) new_meta.insert(new_meta.end(), replacement->begin(), replacement->end());
        else new_meta.insert(new_meta.end(), input + p, input + p + box_size);
        p += box_size;
    }
    if (!have_iref) new_meta.insert(new_meta.end(), new_iref.begin(), new_iref.end());
    if (new_meta.size() != meta_len + metadata_delta) {
        set_error(context, "HEIF Exif metadata graph size does not match its rewritten children.");
        return false;
    }

    std::vector<uint8_t> new_mdat(8 + exif_item.size(), 0);
    write_be32(new_mdat.data(), static_cast<uint32_t>(new_mdat.size()));
    std::memcpy(new_mdat.data() + 4, "mdat", 4);
    std::memcpy(new_mdat.data() + 8, exif_item.data(), exif_item.size());

    output.reserve(input_size + metadata_delta + new_mdat.size());
    output.insert(output.end(), input, input + meta_start);
    output.insert(output.end(), new_meta.begin(), new_meta.end());
    output.insert(output.end(), input + meta_end, input + input_size);
    output.insert(output.end(), new_mdat.begin(), new_mdat.end());
    return true;
}

} // namespace

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_strip_live_photo_entries_selective(
    lpb_context* context,
    uint8_t* data,
    size_t data_size,
    const uint16_t* authorized_tags,
    size_t authorized_count,
    uint16_t* out_stripped_tags,
    size_t max_stripped_tags,
    size_t* out_stripped_count)
{
    if (out_stripped_count) *out_stripped_count = 0;
    if (!context || !data) return LPB_RESULT_INVALID_ARGUMENT;
#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate: in-place destructive mutation of Apple
    // MakerNote bytes requires an active Native cleanup-plan authority.
    // External callers can never bypass the Cleaner trust chain through this
    // primitive; the test-harness build keeps the raw behavior for contract
    // tests only.
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] In-place Apple MakerNote mutation requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif
    if (!authorized_tags || authorized_count == 0) {
        return LPB_RESULT_OK;
    }

    maker_note_region region{};
    if (!locate_formal_makernote_owner(context, data, data_size, region)) {
        set_error(context, "Apple MakerNote is not uniquely owned by an ExifIFD MakerNote tag.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t mnStart = region.start;
    if (mnStart > region.end || region.end > data_size || region.end - mnStart < 16) {
        set_error(context, "Apple MakerNote ownership range is malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const uint16_t entry_count = read_be16u(data + mnStart + 14);
    if (entry_count == 0 || entry_count > 64 ||
        static_cast<size_t>(entry_count) > (region.end - mnStart - 16) / 12) {
        set_error(context, "Apple MakerNote directory is malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t entries_start = mnStart + 16;
    const size_t entries_len = static_cast<size_t>(entry_count) * 12;
    const size_t directory_end = maker_note_directory_end(data, region.end, mnStart, entry_count);
    if (directory_end < entries_start || directory_end > region.end) {
        set_error(context, "Apple MakerNote directory exceeds its owned range.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<size_t> keep;
    std::vector<std::pair<size_t, size_t>> clear_ranges;
    std::vector<uint16_t> stripped_tags_acc;
    for (uint16_t i = 0; i < entry_count; ++i) {
        const size_t entry = entries_start + static_cast<size_t>(i) * 12;
        const uint16_t tag = read_be16u(data + entry);
        const bool is_live_entry = tag == 0x0011 || tag == 0x0017 || tag == 0x0025 || tag == 0x002b;
        bool is_authorized = false;
        for (size_t a = 0; a < authorized_count; ++a) {
            if (authorized_tags[a] == tag) { is_authorized = true; break; }
        }

        const uint16_t type = read_be16u(data + entry + 2);
        const uint32_t count = read_be32u(data + entry + 4);
        const int data_len = type_to_data_length(type, count);
        if (data_len < 0) {
            set_error(context, "Apple MakerNote entry type or count overflows its value range.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (data_len > 0) {
            const uint32_t offset = read_be32u(data + entry + 8);
            const size_t relative_end = region.end - mnStart;
            const size_t value_size = static_cast<size_t>(data_len);
            if (offset < directory_end - mnStart || offset > relative_end || value_size > relative_end - offset) {
                set_error(context, "Apple MakerNote entry points outside its owned payload range.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (is_live_entry && is_authorized) clear_ranges.emplace_back(
                mnStart + static_cast<size_t>(offset), mnStart + static_cast<size_t>(offset) + value_size);
        }

        if (!is_live_entry || !is_authorized) {
            keep.push_back(i);
        } else if (std::find(stripped_tags_acc.begin(), stripped_tags_acc.end(), tag) == stripped_tags_acc.end()) {
            stripped_tags_acc.push_back(tag);
        }
    }

    // All ownership, bounds, and authorization checks above happen before any
    // mutation.  Only the formally-owned Exif MakerNote is touched below.
    for (const auto& range : clear_ranges) std::memset(data + range.first, 0, range.second - range.first);
    if (keep.size() != entry_count) {
        for (size_t k = 0; k < keep.size(); ++k) {
            const size_t src = entries_start + keep[k] * 12;
            const size_t dst = entries_start + k * 12;
            if (src != dst) std::memmove(data + dst, data + src, 12);
        }
        const size_t new_count = keep.size();
        const size_t new_entries_len = new_count * 12;
        const size_t tail = entries_start + entries_len;
        std::memset(data + entries_start + new_entries_len, 0, tail - (entries_start + new_entries_len));
        write_be16(data + mnStart + 14, static_cast<uint16_t>(new_count));
    }

    if (out_stripped_count) *out_stripped_count = stripped_tags_acc.size();
    if (out_stripped_tags && max_stripped_tags > 0) {
        size_t to_copy = std::min(max_stripped_tags, stripped_tags_acc.size());
        for (size_t i = 0; i < to_copy; ++i) {
            out_stripped_tags[i] = stripped_tags_acc[i];
        }
    }
    return LPB_RESULT_OK;
}

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_strip_live_photo_entries(
    lpb_context* context,
    uint8_t* data,
    size_t data_size)
{
    const uint16_t all_live_tags[] = { 0x0011, 0x0017, 0x0025, 0x002b };
    return lpb_apple_strip_live_photo_entries_selective(context, data, data_size, all_live_tags, 4, nullptr, 0, nullptr);
}

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_write_content_identifier(
    lpb_context* context,
    uint8_t* data,
    size_t data_size,
    const char* content_id)
{
    if (!context || !data || !content_id) return LPB_RESULT_INVALID_ARGUMENT;
#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate: in-place ContentIdentifier writes require an
    // active Native cleanup-plan authority (see strip gate above).
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] In-place Apple MakerNote mutation requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

    maker_note_region region{};
    if (!locate_formal_makernote_owner(context, data, data_size, region)) {
        set_error(context, "No uniquely owned Apple MakerNote found.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::string cid(content_id);
    cid.push_back('\0'); // null-terminated UUID

    const size_t mn_start_size = region.start;
    if (region.end > data_size || region.end < mn_start_size || region.end - mn_start_size < 16) {
        set_error(context, "Apple MakerNote ownership range could not be established.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const uint16_t old_count = read_be16u(data + mn_start_size + 14);
    if (old_count > 64 || static_cast<size_t>(old_count) > (region.end - mn_start_size - 16) / 12) {
        set_error(context, "Apple MakerNote directory is malformed.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    const size_t old_directory_end = maker_note_directory_end(data, region.end, mn_start_size, old_count) - mn_start_size;
    if (old_directory_end > region.end - mn_start_size) {
        set_error(context, "Apple MakerNote directory exceeds its owned range.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::vector<std::array<uint8_t, 12>> kept_entries;
    std::vector<std::pair<size_t, size_t>> occupied;
    for (uint16_t i = 0; i < old_count; ++i) {
        const size_t entry_offset = 16 + static_cast<size_t>(i) * 12;
        const uint8_t* entry = data + mn_start_size + entry_offset;
        const uint16_t tag = read_be16u(entry);
        const bool is_live = tag == 0x0011 || tag == 0x0017 || tag == 0x0025 || tag == 0x002b;
        const uint16_t type = read_be16u(entry + 2);
        const uint32_t value_count = read_be32u(entry + 4);
        const int value_length = type_to_data_length(type, value_count);
        if (value_length < 0) {
            set_error(context, "Apple MakerNote entry type or count overflows its value range.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!is_live) {
            std::array<uint8_t, 12> copy{};
            std::memcpy(copy.data(), entry, copy.size());
            kept_entries.push_back(copy);
        }
        if (value_length > 0) {
            const uint32_t relative = read_be32u(entry + 8);
            if (relative < old_directory_end || relative > region.end - mn_start_size ||
                static_cast<size_t>(value_length) > region.end - mn_start_size - relative) {
                set_error(context, "Apple MakerNote entry points outside its owned payload range.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!is_live) occupied.emplace_back(relative, relative + static_cast<size_t>(value_length));
        }
    }
    if (kept_entries.size() >= 64) {
        set_error(context, "Apple MakerNote has no directory slot for a replacement ContentIdentifier.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    const size_t new_count = kept_entries.size() + 1;
    const size_t new_directory_end = maker_note_directory_end(data, region.end, mn_start_size,
        static_cast<uint16_t>(new_count)) - mn_start_size;
    const size_t owner_size = region.end - mn_start_size;
    if (new_directory_end > owner_size || cid.size() > owner_size - new_directory_end || cid.size() > std::numeric_limits<uint32_t>::max()) {
        set_error(context, "Apple MakerNote region is too small for a preserving rebuild.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    size_t cid_offset = new_directory_end;
    while (cid_offset <= owner_size - cid.size()) {
        bool overlaps = false;
        for (const auto& range : occupied) {
            if (cid_offset < range.second && range.first < cid_offset + cid.size()) { overlaps = true; break; }
        }
        if (!overlaps) break;
        ++cid_offset;
    }
    if (cid_offset > owner_size - cid.size()) {
        set_error(context, "Apple MakerNote has no free owned payload range for ContentIdentifier.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    uint8_t* mn = data + mn_start_size;
    std::memcpy(mn, "Apple iOS\0", 10);
    mn[10] = 0x00; mn[11] = 0x01; mn[12] = 'M'; mn[13] = 'M';
    write_be16(mn + 14, static_cast<uint16_t>(new_count));
    for (size_t i = 0; i < kept_entries.size(); ++i) {
        std::memcpy(mn + 16 + i * 12, kept_entries[i].data(), 12);
    }
    uint8_t* new_entry = mn + 16 + kept_entries.size() * 12;
    write_be16(new_entry, 0x0011);
    write_be16(new_entry + 2, 2);
    write_be32(new_entry + 4, static_cast<uint32_t>(cid.size()));
    write_be32(new_entry + 8, static_cast<uint32_t>(cid_offset));
    write_be32(mn + 16 + new_count * 12, 0);
    if (new_directory_end > old_directory_end) std::memset(mn + old_directory_end, 0, new_directory_end - old_directory_end);
    std::memcpy(mn + cid_offset, cid.data(), cid.size());

    return LPB_RESULT_OK;
}



#include "metadata/exif_rewrite.h"
extern "C" LPB_API lpb_result LPB_CALL lpb_heif_locate_exif_item(
    lpb_context* context, const uint8_t* data, size_t data_size,
    uint64_t* out_offset, uint64_t* out_length);

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_inject_makernote_jpeg(
    lpb_context* context, const uint8_t* input, size_t input_size,
    const uint8_t* makernote, size_t makernote_size,
    uint8_t* output, size_t output_size, size_t* out_written)
{
    if (!context || !input || !out_written) return LPB_RESULT_INVALID_ARGUMENT;
    if (makernote_size > 0 && !makernote) {
        set_error(context, "MakerNote payload pointer is null.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (input_size < 2 || input[0] != 0xFF || input[1] != 0xD8) {
        set_error(context, "Input is not a valid JPEG.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate: MakerNote injection rewrites JPEG bytes and
    // requires an active Native cleanup-plan authority.  External callers can
    // never bypass the Cleaner trust chain through this primitive.
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] Apple MakerNote injection requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

    size_t pos = 2;
    bool found_exif = false;
    while (pos + 4 <= input_size) {
        if (input[pos] != 0xFF) {
            set_error(context, "Input JPEG marker stream is malformed.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const size_t marker_start = pos;
        ++pos;
        while (pos < input_size && input[pos] == 0xFF) ++pos;
        if (pos >= input_size) {
            set_error(context, "Input JPEG ends inside a marker.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        uint8_t marker = input[pos++];
        if (marker == 0xDA) break;
        if (marker == 0xD9) break;
        if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) {
            ++pos;
            continue;
        }
        if (pos + 2 > input_size) {
            set_error(context, "Input JPEG segment header is truncated.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        size_t seg_len = (static_cast<size_t>(input[pos]) << 8) | input[pos + 1];
        if (seg_len < 2 || seg_len > input_size - pos) {
            set_error(context, "Input JPEG segment length is invalid.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        
        if (marker == 0xE1 && seg_len >= 8 && std::memcmp(input + pos + 2, "Exif\0\0", 6) == 0) {
            found_exif = true;
            const size_t tiff = pos + 2 + 6;
            const size_t tiff_len = seg_len - 8;
            if (tiff_len < 8) {
                set_error(context, "EXIF TIFF header is truncated.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            bool big_endian = (input[tiff] == 'M' && input[tiff + 1] == 'M');
            const bool little_endian = input[tiff] == 'I' && input[tiff + 1] == 'I';
            if (!big_endian && !little_endian) {
                set_error(context, "EXIF TIFF byte order is invalid.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            const uint16_t tiff_magic = big_endian ? read_be16u(input + tiff + 2) :
                static_cast<uint16_t>(input[tiff + 2]) | (static_cast<uint16_t>(input[tiff + 3]) << 8);
            if (tiff_magic != 42) {
                set_error(context, "EXIF TIFF magic is invalid.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            uint32_t ifd0 = read_be32u(input + tiff + 4);
            if (!big_endian) {
                ifd0 = ((uint32_t)input[tiff + 7] << 24) | ((uint32_t)input[tiff + 6] << 16) | ((uint32_t)input[tiff + 5] << 8) | input[tiff + 4];
            }
            std::vector<uint8_t> cleaned = lpb_tiff_remove_makernotes(input + tiff, tiff_len, ifd0, big_endian);
            const uint8_t* work_tiff = input + tiff;
            size_t work_tiff_size = tiff_len;
            if (!cleaned.empty()) {
                work_tiff = cleaned.data();
                work_tiff_size = cleaned.size();
            }
            uint32_t exif_ptr = lpb_tiff_find_exif_ptr(work_tiff, work_tiff_size, ifd0, big_endian);
            std::vector<uint8_t> grown = lpb_tiff_insert_makernote(work_tiff, work_tiff_size, ifd0, exif_ptr, makernote, makernote_size, big_endian);
            if (grown.empty()) { char buf[256]; snprintf(buf, sizeof(buf), "TIFF insert failed: ifd0=%u, exif_ptr=%u, tiff_len=%zu, work_tiff_size=%zu", ifd0, exif_ptr, tiff_len, work_tiff_size); set_error(context, buf); return LPB_RESULT_INTERNAL_ERROR; }
            if (grown.size() > std::numeric_limits<size_t>::max() - 8 || grown.size() + 8 > 0xFFFF) {
                set_error(context, "Rebuilt EXIF segment is too large for JPEG APP1.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            const size_t source_segment_size = (pos - marker_start) + seg_len;
            if (source_segment_size > input_size || grown.size() > std::numeric_limits<size_t>::max() - 10) {
                set_error(context, "JPEG EXIF segment size is invalid.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            const size_t required = input_size - source_segment_size + 10 + grown.size();
            if (output && output_size >= required) {
                std::memcpy(output, input, marker_start);
                output[marker_start] = 0xFF;
                output[marker_start + 1] = 0xE1;
                size_t new_seg_len = grown.size() + 8; 
                output[marker_start + 2] = static_cast<uint8_t>(new_seg_len >> 8);
                output[marker_start + 3] = static_cast<uint8_t>(new_seg_len & 0xFF);
                std::memcpy(output + marker_start + 4, "Exif\0\0", 6);
                std::memcpy(output + marker_start + 10, grown.data(), grown.size());
                std::memcpy(output + marker_start + 10 + grown.size(), input + pos + seg_len, input_size - (pos + seg_len));
                *out_written = required;
                return LPB_RESULT_OK;
            } else {
                *out_written = required;
                return LPB_RESULT_BUFFER_TOO_SMALL;
            }
        }
        pos += seg_len;
    }
    if (!found_exif) {
        set_error(context, "EXIF APP1 found but MakerNote could not be inserted.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    return LPB_RESULT_INTERNAL_ERROR;
}

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_inject_makernote_heic(
    lpb_context* context, const uint8_t* input, size_t input_size,
    const uint8_t* makernote, size_t makernote_size,
    uint8_t* output, size_t output_size, size_t* out_written)
{
    if (!context || !input || !out_written) return LPB_RESULT_INVALID_ARGUMENT;
    if (makernote_size > 0 && !makernote) {
        set_error(context, "MakerNote payload pointer is null.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }
    if (!has_complete_box_sequence(input, input_size)) {
        set_error(context, "Input HEIF contains a malformed top-level box.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

#if !defined(LPB_NATIVE_TEST_HARNESS)
    // Production capability gate: MakerNote injection rewrites HEIC bytes and
    // requires an active Native cleanup-plan authority.  External callers can
    // never bypass the Cleaner trust chain through this primitive.
    if (!lpb_has_clean_authority(context))
    {
        set_error(context, "[AuthorityViolation] Apple MakerNote injection requires an active Native cleanup-plan authority.");
        return LPB_RESULT_AUTHORITY_VIOLATION;
    }
#endif

    uint64_t exif_offset, exif_length;
    if (lpb_heif_locate_exif_item(context, input, input_size, &exif_offset, &exif_length) != LPB_RESULT_OK) {
        // WIC can produce a valid HEIC without an Exif item when the source
        // image has no metadata block that it can carry across. Apple still
        // needs a MakerNote for the pairing UUID, so create the smallest
        // standards-shaped Exif item in Native instead of falling back to an
        // external metadata tool.
        std::vector<uint8_t> created;
        if (!add_heif_exif_item(context, input, input_size, makernote, makernote_size, created)) {
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        *out_written = created.size();
        if (!output || output_size < created.size()) return LPB_RESULT_BUFFER_TOO_SMALL;
        std::memcpy(output, created.data(), created.size());
        return LPB_RESULT_OK;
    }

    if (exif_offset > input_size || exif_length > input_size - static_cast<size_t>(exif_offset)) {
        set_error(context, "Exif item out of bounds.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    size_t offset = static_cast<size_t>(exif_offset);
    size_t length = static_cast<size_t>(exif_length);

    if (length < 10) {
        set_error(context, "Exif item too short.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    uint32_t tiff_header_offset = read_be32u(input + offset);
    if (tiff_header_offset > length - 4) {
        set_error(context, "Exif TIFF header offset is out of bounds.");
        return LPB_RESULT_INTERNAL_ERROR;
    }
    size_t tiff = offset + 4 + static_cast<size_t>(tiff_header_offset);

    if (tiff_header_offset == 0x45786966) { // "Exif"
        tiff = offset + 6;
    }

    if (tiff > offset + length || offset + length - tiff < 8) {
        set_error(context, "Truncated Exif TIFF in HEIC.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    bool big_endian = (input[tiff] == 'M' && input[tiff + 1] == 'M');
    uint32_t ifd0 = read_be32u(input + tiff + 4);
    if (!big_endian) {
        ifd0 = ((uint32_t)input[tiff + 7] << 24) | ((uint32_t)input[tiff + 6] << 16) | ((uint32_t)input[tiff + 5] << 8) | input[tiff + 4];
    }

    size_t tiff_len = offset + length - tiff;
    std::vector<uint8_t> cleaned = lpb_tiff_remove_makernotes(input + tiff, tiff_len, ifd0, big_endian);
    
    const uint8_t* work_tiff = input + tiff;
    size_t work_tiff_size = tiff_len;
    if (!cleaned.empty()) {
        work_tiff = cleaned.data();
        work_tiff_size = cleaned.size();
    }
    
    uint32_t exif_ptr = lpb_tiff_find_exif_ptr(work_tiff, work_tiff_size, ifd0, big_endian);
    std::vector<uint8_t> grown = lpb_tiff_insert_makernote(work_tiff, work_tiff_size, ifd0, exif_ptr, makernote, makernote_size, big_endian);
    
    if (grown.empty()) { char buf[256]; snprintf(buf, sizeof(buf), "TIFF insert failed: ifd0=%u, exif_ptr=%u, tiff_len=%zu, work_tiff_size=%zu", ifd0, exif_ptr, tiff_len, work_tiff_size); set_error(context, buf); return LPB_RESULT_INTERNAL_ERROR; }

    size_t tiff_prefix_len = tiff - offset;
    std::vector<uint8_t> new_item(tiff_prefix_len + grown.size());
    std::memcpy(new_item.data(), input + offset, tiff_prefix_len);
    std::memcpy(new_item.data() + tiff_prefix_len, grown.data(), grown.size());
    
    if (new_item.size() <= length) {
        if (output && output_size >= input_size) {
            std::memcpy(output, input, input_size);
            std::memcpy(output + offset, new_item.data(), new_item.size());
            std::memset(output + offset + new_item.size(), 0, length - new_item.size());
            *out_written = input_size;
            return LPB_RESULT_OK;
        } else {
            *out_written = input_size;
            return LPB_RESULT_BUFFER_TOO_SMALL;
        }
    }

    uint32_t target_item_id = 0;
    size_t meta_start, meta_len, meta_body;
    size_t iinf_start, iinf_len, iinf_body;
    if (find_box(input, 0, input_size, "meta", meta_start, meta_len, meta_body) &&
        find_box(input, meta_body + 4, meta_start + meta_len, "iinf", iinf_start, iinf_len, iinf_body)) {
        const size_t iinf_end = iinf_start + iinf_len;
        if (iinf_body > iinf_end || iinf_end - iinf_body < 6) {
            set_error(context, "HEIF iinf box is truncated during Exif relocation.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        size_t p = iinf_body;
        uint8_t version = input[p];
        if (version > 1) {
            set_error(context, "Unsupported HEIF iinf version during Exif relocation.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        p += 4;
        uint32_t count = 0;
        if (version == 0) {
            count = (static_cast<uint16_t>(input[p]) << 8) | input[p+1];
            p += 2;
        } else {
            count = read_be32u(input + p);
            p += 4;
        }
        for (uint32_t i = 0; i < count; i++) {
            isobmff_box_header infe{};
            if (!try_read_box_header(input, p, iinf_end, infe)) {
                set_error(context, "Malformed HEIF infe entry during Exif relocation.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (is_box_type(input + p, "infe") && infe.header_size == 8 && infe.size >= 12) {
                uint8_t infe_version = input[p + 8];
                size_t infe_body = p + 12;
                uint32_t item_id;
                if (infe_version >= 2) {
                    if (infe_version == 2) {
                        if (infe_body + 2 > p + infe.size) return LPB_RESULT_INVALID_ARGUMENT;
                        item_id = (static_cast<uint16_t>(input[infe_body]) << 8) | input[infe_body+1];
                    } else {
                        if (infe_body + 4 > p + infe.size) return LPB_RESULT_INVALID_ARGUMENT;
                        item_id = read_be32u(input + infe_body);
                    }
                    size_t type_pos = infe_version == 2 ? infe_body + 4 : infe_body + 6;
                    if (type_pos + 4 <= p + infe.size && input[type_pos] == 'E' && input[type_pos+1] == 'x' && input[type_pos+2] == 'i' && input[type_pos+3] == 'f') {
                        target_item_id = item_id;
                        break;
                    }
                }
            }
            p += infe.size;
        }
        if (p > iinf_end) return LPB_RESULT_INVALID_ARGUMENT;
    }

    if (target_item_id == 0) {
        set_error(context, "Could not identify Exif item_id for relocation.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    std::vector<uint8_t> patched;
    if (!try_relocate_exif_to_mdat_end(context, input, input_size, target_item_id, new_item, patched)) {
        return LPB_RESULT_INTERNAL_ERROR;
    }
    
    if (output && output_size >= patched.size()) {
        std::memcpy(output, patched.data(), patched.size());
        *out_written = patched.size();
        return LPB_RESULT_OK;
    } else {
        *out_written = patched.size();
        return LPB_RESULT_BUFFER_TOO_SMALL;
    }
}

namespace lpb::protocols::apple {

struct owned_makernote {
    size_t start{};
    size_t end{};
    const uint8_t* tiff{};
    size_t tiff_start{};
    bool little{};
};

static bool find_unique_tiff_tag(const uint8_t* data, size_t tiff_start, size_t tiff_end,
    uint32_t ifd_offset, bool little, uint16_t wanted, uint16_t& type, uint32_t& count,
    uint32_t& value, bool& found) noexcept
{
    found = false;
    if (!data || tiff_start > tiff_end || ifd_offset > tiff_end - tiff_start) return false;
    const size_t ifd = tiff_start + ifd_offset;
    uint16_t entries = 0;
    if (!read_tiff_u16(data, tiff_start, tiff_end, ifd, little, entries) ||
        entries > 4096 || static_cast<size_t>(entries) > (tiff_end - ifd - 2) / 12) return false;
    for (uint16_t i = 0; i < entries; ++i) {
        const size_t entry = ifd + 2 + static_cast<size_t>(i) * 12;
        uint16_t tag = 0;
        if (!read_tiff_u16(data, tiff_start, tiff_end, entry, little, tag)) return false;
        if (tag != wanted) continue;
        if (found) return false;
        if (!read_tiff_u16(data, tiff_start, tiff_end, entry + 2, little, type) ||
            !read_tiff_u32(data, tiff_start, tiff_end, entry + 4, little, count) ||
            !read_tiff_u32(data, tiff_start, tiff_end, entry + 8, little, value)) return false;
        found = true;
    }
    return true;
}

static uint64_t tiff_type_size(uint16_t type) noexcept
{
    switch (type) {
    case 1: case 2: case 6: case 7: return 1;
    case 3: case 8: return 2;
    case 4: case 9: case 11: case 13: case 14: return 4;
    case 5: case 10: case 12: case 16: return 8;
    default: return 0;
    }
}

static bool validate_tiff_ifd_chain(const uint8_t* data, size_t tiff_start, size_t tiff_end,
    uint32_t initial_offset, bool little, std::vector<uint32_t>& visited, size_t& makernote_count) noexcept
{
    uint32_t offset = initial_offset;
    while (offset != 0) {
        if (std::find(visited.begin(), visited.end(), offset) != visited.end() || visited.size() >= 64) return false;
        visited.push_back(offset);
        if (offset > tiff_end - tiff_start) return false;
        const size_t ifd = tiff_start + static_cast<size_t>(offset);
        uint16_t count = 0;
        if (!read_tiff_u16(data, tiff_start, tiff_end, ifd, little, count) || count > 4096 || tiff_end - ifd < 2) return false;
        const size_t table_bytes = static_cast<size_t>(count) * 12;
        if (table_bytes > tiff_end - ifd - 2 || tiff_end - ifd - 2 - table_bytes < 4) return false;
        for (uint16_t i = 0; i < count; ++i) {
            const size_t entry = ifd + 2 + static_cast<size_t>(i) * 12;
            uint16_t tag = 0; uint16_t type = 0; uint32_t value_count = 0; uint32_t value = 0;
            if (!read_tiff_u16(data, tiff_start, tiff_end, entry, little, tag) ||
                !read_tiff_u16(data, tiff_start, tiff_end, entry + 2, little, type) ||
                !read_tiff_u32(data, tiff_start, tiff_end, entry + 4, little, value_count) ||
                !read_tiff_u32(data, tiff_start, tiff_end, entry + 8, little, value)) return false;
            const uint64_t unit = tiff_type_size(type);
            if (unit == 0 || static_cast<uint64_t>(value_count) > std::numeric_limits<uint64_t>::max() / unit) return false;
            const uint64_t value_bytes = unit * value_count;
            if (value_bytes > 4) {
                if (value > tiff_end - tiff_start || value_bytes > tiff_end - tiff_start - value) return false;
            }
            if (tag == 0x927C) ++makernote_count;
        }
        const size_t next_at = ifd + 2 + table_bytes;
        uint32_t next = 0;
        if (!read_tiff_u32(data, tiff_start, tiff_end, next_at, little, next)) return false;
        offset = next;
    }
    return true;
}

static bool locate_owned_makernote(const uint8_t* data, size_t start, size_t end,
    owned_makernote& out) noexcept
{
    if (!data || start >= end) return false;
    size_t tiff_start = start;
    if (end - start >= 10 && data[start] == 0 && data[start + 1] == 0 && data[start + 2] == 0 &&
        data[start + 3] == 6 && std::memcmp(data + start + 4, "Exif\0\0", 6) == 0) {
        tiff_start += 10;
    }
    if (tiff_start + 8 > end) return false;
    const bool little = data[tiff_start] == 'I' && data[tiff_start + 1] == 'I';
    const bool big = data[tiff_start] == 'M' && data[tiff_start + 1] == 'M';
    if ((!little && !big) || (little && (data[tiff_start + 2] != 0x2A || data[tiff_start + 3] != 0)) ||
        (big && read_be16u(data + tiff_start + 2) != 42)) return false;
    uint32_t ifd0 = 0;
    if (!read_tiff_u32(data, tiff_start, end, tiff_start + 4, little, ifd0)) return false;
    if (ifd0 == 0) return false;
    std::vector<uint32_t> ifd0_chain;
    size_t ifd0_makernote_count = 0;
    if (!validate_tiff_ifd_chain(data, tiff_start, end, ifd0, little, ifd0_chain, ifd0_makernote_count) || ifd0_makernote_count != 0) return false;
    uint16_t shadow_type = 0; uint32_t shadow_count = 0; uint32_t shadow_offset = 0; bool shadow_found = false;
    if (!find_unique_tiff_tag(data, tiff_start, end, ifd0, little, 0x927C,
        shadow_type, shadow_count, shadow_offset, shadow_found) || shadow_found) return false;
    uint16_t exif_type = 0; uint32_t exif_count = 0; uint32_t exif_offset = 0; bool exif_found = false;
    if (!find_unique_tiff_tag(data, tiff_start, end, ifd0, little, 0x8769,
        exif_type, exif_count, exif_offset, exif_found) || !exif_found || exif_type != 4 || exif_count != 1 ||
        exif_offset == 0 || std::find(ifd0_chain.begin(), ifd0_chain.end(), exif_offset) != ifd0_chain.end()) return false;
    std::vector<uint32_t> exif_chain;
    size_t exif_makernote_count = 0;
    if (!validate_tiff_ifd_chain(data, tiff_start, end, exif_offset, little, exif_chain, exif_makernote_count) || exif_makernote_count != 1) return false;
    uint16_t note_type = 0; uint32_t note_count = 0; uint32_t note_offset = 0; bool note_found = false;
    if (!find_unique_tiff_tag(data, tiff_start, end, exif_offset, little, 0x927C,
        note_type, note_count, note_offset, note_found) || !note_found || note_type != 7 || note_count < 14) return false;
    if (note_count < 16 || note_offset > end - tiff_start || note_count > end - tiff_start - note_offset) return false;
    const size_t note_start = tiff_start + static_cast<size_t>(note_offset);
    if (note_start + 16 > end || std::memcmp(data + note_start, "Apple iOS\0", 10) != 0) return false;
    const uint16_t directory_count = read_be16u(data + note_start + 14);
    if (directory_count > 64 || directory_count > (end - note_start - 16) / 12) return false;
    std::vector<uint16_t> maker_tags;
    maker_tags.reserve(directory_count);
    for (uint16_t i = 0; i < directory_count; ++i) {
        const uint16_t tag = read_be16u(data + note_start + 16 + static_cast<size_t>(i) * 12);
        if (std::find(maker_tags.begin(), maker_tags.end(), tag) != maker_tags.end()) return false;
        maker_tags.push_back(tag);
    }
    out = { note_start, note_start + static_cast<size_t>(note_count), data, tiff_start, little };
    return true;
}

bool apple_makernote_has_tag(const uint8_t* data, size_t start, size_t end, uint16_t target_tag) {
    owned_makernote note{};
    if (!locate_owned_makernote(data, start, end, note)) return false;
    const size_t entries = note.start + 16;
    const uint16_t count = read_be16u(data + note.start + 14);
    for (uint16_t i = 0; i < count; ++i)
        if (read_be16u(data + entries + static_cast<size_t>(i) * 12) == target_tag) return true;
    return false;
}

bool apple_makernote_get_tag_fingerprint(
    const uint8_t* data, size_t start, size_t end, uint16_t target_tag, std::string& out_fp)
{
    owned_makernote note{};
    if (!locate_owned_makernote(data, start, end, note)) return false;
    const uint16_t entry_count = read_be16u(data + note.start + 14);
    const size_t entries = note.start + 16;
    for (uint16_t i = 0; i < entry_count; ++i) {
            const size_t entry = entries + static_cast<size_t>(i) * 12;
            const uint16_t tag = read_be16u(data + entry);
            if (tag == target_tag) {
                uint16_t type = read_be16u(data + entry + 2);
                uint32_t count = read_be32u(data + entry + 4);
                uint32_t offset = read_be32u(data + entry + 8);
                int unit = 0;
                switch (type) {
                    case 1: case 2: case 7: unit = 1; break;
                    case 3: case 8: unit = 2; break;
                    case 4: case 9: unit = 4; break;
                    case 5: case 10: unit = 8; break;
                    case 6: case 11: unit = 4; break;
                    case 12: unit = 8; break;
                    case 13: case 14: unit = 4; break;
                    case 16: unit = 8; break;
                    default: break;
                }
                if (unit == 0) return false;
                if (count > static_cast<uint64_t>(std::numeric_limits<int>::max()) / static_cast<uint64_t>(unit)) return false;
                size_t val_len = static_cast<size_t>(unit) * count;
                const uint8_t* val_ptr = nullptr;
                if (val_len <= 4) {
                    val_ptr = data + entry + 8;
                } else {
                    if (offset > note.end - note.start || val_len > (note.end - note.start - offset)) return false;
                    val_ptr = data + note.start + offset;
                }
                out_fp = lpb::crypto::compute_apple_makernote_tag_fingerprint(tag, type, count, val_ptr, val_len);
                return true;
            }
    }
    return false;
}

bool apple_image_has_tag(
    lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, uint16_t tag)
{
    if (container == LPB_IMAGE_CONTAINER_JPEG && data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8) {
        size_t p = 2;
        while (p + 2 <= data.size()) {
            if (data[p++] != 0xFF) return false;
            while (p < data.size() && data[p] == 0xFF) ++p;
            if (p >= data.size()) return false;
            const uint8_t marker = data[p++];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data.size()) return false;
            const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
            if (segment_length < 2 || segment_length - 2 > data.size() - (p + 2)) return false;
            const size_t payload = p + 2;
            const size_t payload_size = segment_length - 2;
            if (marker == 0xE1 && payload_size >= 6 && std::memcmp(data.data() + payload, "Exif\0\0", 6) == 0) {
                if (apple_makernote_has_tag(data.data(), payload + 6, payload + payload_size, tag)) {
                    return true;
                }
            }
            p = payload + payload_size;
        }
        return false;
    }
    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        return apple_makernote_has_tag(data.data(), static_cast<size_t>(offset),
            static_cast<size_t>(offset + length), tag);
    }
    return false;
}

bool apple_image_get_tag_fingerprint(
    lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, uint16_t tag, std::string& out_fp)
{
    if (container == LPB_IMAGE_CONTAINER_JPEG && data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8) {
        size_t p = 2;
        while (p + 2 <= data.size()) {
            if (data[p++] != 0xFF) return false;
            while (p < data.size() && data[p] == 0xFF) ++p;
            if (p >= data.size()) return false;
            const uint8_t marker = data[p++];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data.size()) return false;
            const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
            if (segment_length < 2 || segment_length - 2 > data.size() - (p + 2)) return false;
            const size_t payload = p + 2;
            const size_t payload_size = segment_length - 2;
            if (marker == 0xE1 && payload_size >= 6 && std::memcmp(data.data() + payload, "Exif\0\0", 6) == 0) {
                if (apple_makernote_get_tag_fingerprint(data.data(), payload + 6, payload + payload_size, tag, out_fp)) {
                    return true;
                }
            }
            p = payload + payload_size;
        }
        return false;
    }
    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        return apple_makernote_get_tag_fingerprint(data.data(), static_cast<size_t>(offset),
            static_cast<size_t>(offset + length), tag, out_fp);
    }
    return false;
}

} // namespace lpb::protocols::apple

namespace {

static bool locate_formal_makernote_owner(
    lpb_context* context, const uint8_t* data, size_t data_size,
    maker_note_region& out) noexcept
{
    if (!data || data_size < 2) return false;

    // JPEG ownership is the APP1 Exif/TIFF hierarchy. Do not inspect bytes
    // after SOS/EOI or accept a second Exif APP1 as a shadow owner.
    if (data[0] == 0xFF && data[1] == 0xD8) {
        size_t p = 2;
        bool found_owner = false;
        while (p < data_size) {
            if (p + 2 > data_size || data[p] != 0xFF) return false;
            ++p;
            while (p < data_size && data[p] == 0xFF) ++p;
            if (p >= data_size) return false;
            const uint8_t marker = data[p++];
            if (marker == 0xD9) break;
            if (marker == 0xDA) {
                if (p + 2 > data_size) return false;
                const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
                return segment_length >= 2 && segment_length <= data_size - p && found_owner;
            }
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data_size) return false;
            const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
            if (segment_length < 2 || segment_length > data_size - p) return false;
            const size_t payload = p + 2;
            const size_t payload_size = segment_length - 2;
            if (marker == 0xE1 && payload_size >= 6 &&
                std::memcmp(data + payload, "Exif\0\0", 6) == 0) {
                if (found_owner) return false;
                lpb::protocols::apple::owned_makernote note{};
                if (!lpb::protocols::apple::locate_owned_makernote(
                    data, payload + 6, payload + payload_size, note)) return false;
                out = { note.start, note.end };
                found_owner = true;
            }
            p = payload + payload_size;
        }
        return found_owner;
    }

    // HEIF ownership is the unique Exif item returned by the validated item
    // graph. A raw MakerNote signature elsewhere in the file is irrelevant.
    if (!context) return false;
    uint64_t exif_offset = 0;
    uint64_t exif_length = 0;
    if (lpb_heif_locate_exif_item(context, data, data_size, &exif_offset, &exif_length) != LPB_RESULT_OK ||
        exif_offset > data_size || exif_length > data_size - static_cast<size_t>(exif_offset)) return false;
    lpb::protocols::apple::owned_makernote note{};
    if (!lpb::protocols::apple::locate_owned_makernote(
        data, static_cast<size_t>(exif_offset),
        static_cast<size_t>(exif_offset + exif_length), note)) return false;
    out = { note.start, note.end };
    return true;
}

} // namespace
