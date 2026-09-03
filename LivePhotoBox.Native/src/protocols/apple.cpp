#include <cstdio>
#include "foundation/internal.h"
#include "binary/endian.h"
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
    size_t p = start;
    while (p + 8 <= end) {
        uint64_t box_sz = read_be32u(data + p);
        size_t header = 8;
        if (box_sz == 1) {
            if (p + 16 > end) break;
            box_sz = read_be64(data + p + 8);
            header = 16;
        } else if (box_sz == 0) {
            box_sz = end - p;
        }
        if (box_sz < header || p + box_sz > end) break;
        if (is_box_type(data + p, type)) {
            box_start = p;
            box_len = static_cast<size_t>(box_sz);
            body_start = p + header;
            return true;
        }
        p += static_cast<size_t>(box_sz);
    }
    return false;
}

static void write_uint(uint8_t* p, uint64_t val, size_t size) {
    for (size_t i = 0; i < size; ++i) {
        p[size - 1 - i] = static_cast<uint8_t>(val & 0xFF);
        val >>= 8;
    }
}

static bool try_find_exif_iloc_fields(
    const uint8_t* data, size_t /*data_size*/,
    size_t iloc_body, size_t iloc_len,
    uint32_t target_item_id,
    size_t& base_field_pos, size_t& length_field_pos,
    size_t& base_size, size_t& length_size)
{
    size_t p = iloc_body;
    size_t end = iloc_body - 8 + iloc_len;
    if (p + 6 > end) return false;
    uint8_t version = data[p];
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
    for (uint32_t i = 0; i < count; i++) {
        uint32_t item_id;
        if (version < 2) {
            if (p + 2 > end) break;
            item_id = (static_cast<uint16_t>(data[p]) << 8) | data[p+1];
            p += 2;
        } else {
            if (p + 4 > end) break;
            item_id = read_be32u(data + p);
            p += 4;
        }
        if (version == 1 || version == 2) {
            if (p + 2 > end) break;
            p += 2;
        }
        if (p + 2 > end) break;
        p += 2;
        size_t current_base_pos = p;
        if (base_size > 0) {
            if (p + base_size > end) break;
            p += base_size;
        }
        if (p + 2 > end) break;
        uint16_t extent_count = (static_cast<uint16_t>(data[p]) << 8) | data[p+1];
        p += 2;
        size_t current_len_pos = 0;
        for (uint16_t e = 0; e < extent_count; e++) {
            if ((version == 1 || version == 2) && index_size > 0) {
                if (p + index_size > end) break;
                p += index_size;
            }
            if (p + offset_size > end || p + length_size > end) break;
            p += offset_size;
            if (e == 0) current_len_pos = p;
            p += length_size;
        }
        if (item_id == target_item_id) {
            base_field_pos = current_base_pos;
            length_field_pos = current_len_pos;
            return true;
        }
    }
    return false;
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
    while (p + 8 <= data_size) {
        uint64_t size = read_be32u(data + p);
        size_t header = 8;
        if (size == 1) {
            if (p + 16 > data_size) return false;
            size = read_be64(data + p + 8);
            header = 16;
        } else if (size == 0) {
            size = data_size - p;
        }
        if (size < header || p + size > data_size) return false;
        if (is_box_type(data + p, "mdat")) {
            mdat_start = p;
            mdat_size = static_cast<size_t>(size);
            mdat_header = header;
            if (p + size != data_size) {
                set_error(context, "mdat is not the last box.");
                return false;
            }
            break;
        }
        p += static_cast<size_t>(size);
    }
    if (mdat_start < 0) return false;

    size_t meta_start, meta_len, meta_body;
    if (!find_box(data, 0, data_size, "meta", meta_start, meta_len, meta_body)) return false;
    size_t iloc_start, iloc_len, iloc_body;
    if (!find_box(data, meta_body + 4, meta_start + meta_len, "iloc", iloc_start, iloc_len, iloc_body)) return false;

    size_t base_field_pos = 0, length_field_pos = 0;
    size_t base_size = 0, length_size = 0;
    if (!try_find_exif_iloc_fields(data, data_size, iloc_body, iloc_len, target_item_id, base_field_pos, length_field_pos, base_size, length_size)) {
        set_error(context, "Failed to find Exif item iloc fields.");
        return false;
    }
    if (base_size < 4) {
        set_error(context, "iloc base_offset size too small.");
        return false;
    }
    if (length_size < 4) {
        set_error(context, "iloc length size too small.");
        return false;
    }

    uint64_t new_base = static_cast<uint64_t>(mdat_start) + mdat_size;
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
            write_be32(patched.data() + mdat_start, static_cast<int32_t>(old_size + static_cast<uint32_t>(new_tiff.size())));
        }
    } else {
        uint64_t old_size = read_be64(patched.data() + mdat_start + 8);
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

// Helper to find Apple MakerNote signature: "Apple iOS\0" + 0x00 0x01 + "MM"
static ptrdiff_t find_apple_makernote(const uint8_t* data, size_t size, size_t search_from = 0) {
    if (size < 14 || search_from > size - 14) return -1;
    const uint8_t sig[] = {'A','p','p','l','e',' ','i','O','S','\0'};
    for (size_t i = search_from; i <= size - 14; i++) {
        if (data[i] == 'A' && data[i+1] == 'p') {
            if (std::memcmp(data + i, sig, 10) == 0 &&
                data[i+10] == 0x00 && data[i+11] == 0x01 &&
                data[i+12] == 'M' && data[i+13] == 'M') {
                return static_cast<ptrdiff_t>(i);
            }
        }
    }
    return -1;
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

static bool find_tiff_tag(const uint8_t* data, size_t tiff_start, size_t tiff_end, uint32_t ifd_offset,
    bool little, uint16_t wanted_tag, uint16_t& out_type, uint32_t& out_count, uint32_t& out_value) noexcept {
    if (ifd_offset > tiff_end - tiff_start || tiff_start + ifd_offset > tiff_end || tiff_end - (tiff_start + ifd_offset) < 2) return false;
    const size_t ifd = tiff_start + ifd_offset;
    uint16_t count = 0;
    if (!read_tiff_u16(data, tiff_start, tiff_end, ifd, little, count) || count > 4096 ||
        static_cast<size_t>(count) > (tiff_end - (ifd + 2)) / 12) return false;
    for (uint16_t i = 0; i < count; ++i) {
        const size_t entry = ifd + 2 + static_cast<size_t>(i) * 12;
        uint16_t tag = 0;
        if (!read_tiff_u16(data, tiff_start, tiff_end, entry, little, tag)) return false;
        if (tag != wanted_tag) continue;
        if (!read_tiff_u16(data, tiff_start, tiff_end, entry + 2, little, out_type) ||
            !read_tiff_u32(data, tiff_start, tiff_end, entry + 4, little, out_count) ||
            !read_tiff_u32(data, tiff_start, tiff_end, entry + 8, little, out_value)) return false;
        return true;
    }
    return false;
}

static bool find_structural_maker_note_region(const uint8_t* data, size_t size, size_t expected_start,
    maker_note_region& out) noexcept {
    if (!data || expected_start >= size) return false;
    for (size_t tiff = 0; tiff + 8 <= expected_start; ++tiff) {
        const bool little = data[tiff] == 'I' && data[tiff + 1] == 'I';
        const bool big = data[tiff] == 'M' && data[tiff + 1] == 'M';
        if ((!little && !big) || (little && data[tiff + 2] != 0x2A) || (little && data[tiff + 3] != 0x00) ||
            (big && read_be16u(data + tiff + 2) != 42)) continue;
        uint32_t ifd0 = 0;
        if (!read_tiff_u32(data, tiff, size, tiff + 4, !big, ifd0)) continue;
        uint16_t type = 0; uint32_t count = 0; uint32_t value = 0;
        bool found = find_tiff_tag(data, tiff, size, ifd0, !big, 0x927C, type, count, value);
        if (!found) {
            uint16_t exif_type = 0; uint32_t exif_count = 0; uint32_t exif_ifd = 0;
            if (find_tiff_tag(data, tiff, size, ifd0, !big, 0x8769, exif_type, exif_count, exif_ifd) &&
                exif_type == 4 && exif_count == 1) {
                found = find_tiff_tag(data, tiff, size, exif_ifd, !big, 0x927C, type, count, value);
            }
        }
        if (!found || type != 7 || count == 0 || value > size - tiff || count > size - tiff - value) continue;
        const size_t note_start = tiff + static_cast<size_t>(value);
        if (note_start == expected_start && count <= size - note_start && count >= 14 &&
            std::memcmp(data + note_start, "Apple iOS\0", 10) == 0) {
            out = { note_start, note_start + static_cast<size_t>(count) };
            return true;
        }
    }
    return false;
}

static bool derive_maker_note_region_from_contents(const uint8_t* data, size_t size, size_t start,
    maker_note_region& out) noexcept {
    if (!data || start > size || size - start < 20 || std::memcmp(data + start, "Apple iOS\0", 10) != 0) return false;
    const uint16_t count = read_be16u(data + start + 14);
    if (count > 64 || static_cast<size_t>(count) > (size - start - 16) / 12) return false;
    const size_t entries = start + 16;
    const size_t directory_end = maker_note_directory_end(data, size, start, count);
    if (directory_end > size) return false;
    size_t end = directory_end;
    if (count == 0) {
        // A previous in-place strip leaves the old value area zeroed. For the
        // unwrapped ABI test/utility buffer, retain that zero-owned region but
        // stop at the first non-zero byte instead of treating the whole file
        // as MakerNote storage.
        while (end < size && data[end] == 0) ++end;
    }
    for (uint16_t i = 0; i < count; ++i) {
        const size_t entry = entries + static_cast<size_t>(i) * 12;
        const uint16_t type = read_be16u(data + entry + 2);
        const uint32_t value_count = read_be32u(data + entry + 4);
        const uint64_t unit = type == 1 || type == 2 || type == 6 || type == 7 ? 1 :
            (type == 3 || type == 8 ? 2 : (type == 4 || type == 9 || type == 11 || type == 13 || type == 14 ? 4 :
            (type == 5 || type == 10 || type == 12 || type == 16 ? 8 : 0)));
        if (unit == 0 || value_count > std::numeric_limits<size_t>::max() / unit) continue;
        const size_t value_size = static_cast<size_t>(unit * value_count);
        if (value_size > 4) {
            const uint32_t relative = read_be32u(data + entry + 8);
            if (relative > size - start || value_size > size - start - relative) return false;
            end = std::max(end, start + static_cast<size_t>(relative) + value_size);
        }
    }
    out = { start, end };
    return true;
}

static bool find_maker_note_region(const uint8_t* data, size_t size, size_t start, maker_note_region& out) noexcept {
    if (find_structural_maker_note_region(data, size, start, out)) return true;
    return derive_maker_note_region_from_contents(data, size, start, out);
}

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

static bool add_heif_exif_item(
    lpb_context* context,
    const uint8_t* input, size_t input_size,
    const uint8_t* makernote, size_t makernote_size,
    std::vector<uint8_t>& output)
{
    size_t meta_start, meta_len, meta_body;
    if (!find_box(input, 0, input_size, "meta", meta_start, meta_len, meta_body)) {
        set_error(context, "No meta box found while creating HEIF Exif item.");
        return false;
    }
    size_t meta_end = meta_start + meta_len;
    size_t iinf_start, iinf_len, iinf_body;
    size_t iloc_start, iloc_len, iloc_body;
    size_t idat_start = 0, idat_len = 0, idat_body = 0;
    if (!find_box(input, meta_body + 4, meta_end, "iinf", iinf_start, iinf_len, iinf_body) ||
        !find_box(input, meta_body + 4, meta_end, "iloc", iloc_start, iloc_len, iloc_body)) {
        set_error(context, "HEIF meta lacks iinf or iloc for Exif item creation.");
        return false;
    }
    const bool have_idat = find_box(input, meta_body + 4, meta_end, "idat", idat_start, idat_len, idat_body);

    const size_t iloc_end = iloc_start + iloc_len;
    if (iloc_body + 8 > iloc_end || input[iloc_body] != 1 ||
        (input[iloc_body + 4] >> 4) != 4 || (input[iloc_body + 4] & 0x0F) != 4 ||
        (input[iloc_body + 5] >> 4) != 0 || (input[iloc_body + 5] & 0x0F) != 0) {
        set_error(context, "Unsupported HEIF iloc layout for Exif item creation.");
        return false;
    }

    uint32_t item_count = (static_cast<uint16_t>(input[iloc_body + 6]) << 8) | input[iloc_body + 7];
    size_t p = iloc_body + 8;
    uint32_t max_item_id = 0;
    for (uint32_t i = 0; i < item_count; ++i) {
        if (p + 8 > iloc_end) {
            set_error(context, "Truncated HEIF iloc while creating Exif item.");
            return false;
        }
        uint32_t item_id = (static_cast<uint16_t>(input[p]) << 8) | input[p + 1];
        uint16_t construction_method = (static_cast<uint16_t>(input[p + 2]) << 8) | input[p + 3];
        uint16_t extent_count = (static_cast<uint16_t>(input[p + 6]) << 8) | input[p + 7];
        max_item_id = std::max(max_item_id, item_id);
        if (construction_method == 0 || construction_method == 1) {
            size_t extent = p + 8;
            for (uint16_t e = 0; e < extent_count; ++e) {
                if (extent + 8 > iloc_end) {
                    set_error(context, "Truncated HEIF extent while creating Exif item.");
                    return false;
                }
                uint32_t old_offset = read_be32u(input + extent);
                if (old_offset > 0xFFFFFFFFu - 37u) {
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
        input_size > std::numeric_limits<size_t>::max() - 37 ||
        makernote_size > std::numeric_limits<size_t>::max() - 32) {
        set_error(context, "HEIF Exif item size overflow.");
        return false;
    }

    // The new iinf entry and iloc entry add 21 + 16 bytes to meta. Existing
    // absolute extents move with the enlarged meta box, so shift them by 37.
    constexpr size_t metadata_delta = 21 + 16;
    std::vector<uint8_t> new_iinf(input + iinf_start, input + iinf_start + iinf_len);
    new_iinf.resize(iinf_len + 21, 0);
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
    write_be16(infe + 12, static_cast<uint16_t>(max_item_id + 1));
    std::memcpy(infe + 16, "Exif", 4);

    std::vector<uint8_t> new_iloc(input + iloc_start, input + iloc_start + iloc_len);
    new_iloc.resize(iloc_len + 16, 0);
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
                write_be32(new_iloc.data() + extent, old_offset + static_cast<uint32_t>(metadata_delta));
                extent += 8;
            }
        }
        p += 8 + static_cast<size_t>(extent_count) * 8;
    }
    write_be16(new_iloc.data() + p, static_cast<uint16_t>(max_item_id + 1));
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
        if (replacement != nullptr) new_meta.insert(new_meta.end(), replacement->begin(), replacement->end());
        else new_meta.insert(new_meta.end(), input + p, input + p + box_size);
        p += box_size;
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

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_strip_live_photo_entries(
    lpb_context* context,
    uint8_t* data,
    size_t data_size)
{
    if (!context || !data) return LPB_RESULT_INVALID_ARGUMENT;

    size_t search_from = 0;
    bool malformed_candidate = false;
    while (true) {
        ptrdiff_t mn_start = find_apple_makernote(data, data_size, search_from);
        if (mn_start < 0) {
            if (malformed_candidate) {
                set_error(context, "Malformed Apple MakerNote candidate.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            return LPB_RESULT_OK;
        }

        size_t mnStart = static_cast<size_t>(mn_start);
        maker_note_region region{};
        if (!find_maker_note_region(data, data_size, mnStart, region)) {
            malformed_candidate = true;
            search_from = mnStart + 14;
            continue;
        }
        // Continue after the signature even when this candidate is malformed, so a later
        // valid MakerNote is never hidden by an unrelated byte sequence.
        search_from = mnStart + 14;
        if (mnStart + 16 > region.end || region.end > data_size) { malformed_candidate = true; continue; }

        uint16_t entry_count = read_be16u(data + mnStart + 14);
        if (entry_count == 0 || entry_count > 64) { malformed_candidate = true; continue; }

        size_t entriesStart = mnStart + 16;
        size_t entriesLen = entry_count * 12;
        const size_t directoryEnd = maker_note_directory_end(data, data_size, mnStart, entry_count);
        if (entriesLen > region.end - entriesStart || directoryEnd > region.end) {
            malformed_candidate = true;
            continue;
        }

        std::vector<size_t> keep;
        for (uint16_t i = 0; i < entry_count; i++) {
            size_t e = entriesStart + i * 12;
            uint16_t tag = read_be16u(data + e);
            bool isLiveEntry = (tag == 0x0011 || tag == 0x0017 || tag == 0x0025 || tag == 0x002b);

            if (!isLiveEntry) {
                keep.push_back(i);
                continue;
            }

            uint16_t type = read_be16u(data + e + 2);
            uint32_t count = read_be32u(data + e + 4);
            uint32_t offset = read_be32u(data + e + 8);
            int dataLen = type_to_data_length(type, count);

            const size_t relative_directory_end = directoryEnd - mnStart;
            if (dataLen < 0 || (dataLen > 0 && (offset < relative_directory_end ||
                offset > region.end - mnStart || static_cast<size_t>(dataLen) > region.end - mnStart - offset))) {
                malformed_candidate = true;
                continue;
            }
            size_t absData = mnStart + offset;
            if (dataLen > 0) {
                std::memset(data + absData, 0, static_cast<size_t>(dataLen));
            }
        }

        if (keep.size() == entry_count) continue;

        for (size_t k = 0; k < keep.size(); k++) {
            size_t src = entriesStart + keep[k] * 12;
            size_t dst = entriesStart + k * 12;
            if (src != dst) {
                std::memmove(data + dst, data + src, 12);
            }
        }

        size_t newCount = keep.size();
        size_t newEntriesLen = newCount * 12;
        size_t tail = entriesStart + entriesLen;
        std::memset(data + entriesStart + newEntriesLen, 0, tail - (entriesStart + newEntriesLen));
        write_be16(data + mnStart + 14, static_cast<uint16_t>(newCount));
    }

}

extern "C" LPB_API lpb_result LPB_CALL lpb_apple_write_content_identifier(
    lpb_context* context,
    uint8_t* data,
    size_t data_size,
    const char* content_id)
{
    if (!context || !data || !content_id) return LPB_RESULT_INVALID_ARGUMENT;

    ptrdiff_t mn_start = find_apple_makernote(data, data_size);
    if (mn_start < 0) {
        set_error(context, "No existing Apple MakerNote found.");
        return LPB_RESULT_INTERNAL_ERROR;
    }

    maker_note_region region{};
    if (!find_maker_note_region(data, data_size, static_cast<size_t>(mn_start), region)) {
        set_error(context, "Apple MakerNote ownership range could not be established.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::string cid(content_id);
    cid.push_back('\0'); // null-terminated UUID

    const size_t mn_start_size = static_cast<size_t>(mn_start);
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
    size_t pos = 2;
    bool found_exif = false;
    while (pos + 4 <= input_size) {
        if (input[pos] != 0xFF) break;
        uint8_t marker = input[pos + 1];
        if (marker == 0xDA) break;
        size_t seg_len = (static_cast<size_t>(input[pos + 2]) << 8) | input[pos + 3];
        if (pos + 2 + seg_len > input_size) break;
        
        if (marker == 0xE1 && seg_len >= 6 && std::memcmp(input + pos + 4, "Exif\0\0", 6) == 0) {
            found_exif = true;
            size_t tiff = pos + 10;
            size_t tiff_len = seg_len - 8;
            bool big_endian = (input[tiff] == 'M' && input[tiff + 1] == 'M');
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
            size_t required = input_size - seg_len - 2 + grown.size() + 8;
            if (output && output_size >= required) {
                std::memcpy(output, input, pos);
                output[pos] = 0xFF;
                output[pos + 1] = 0xE1;
                size_t new_seg_len = grown.size() + 8; 
                output[pos + 2] = static_cast<uint8_t>(new_seg_len >> 8);
                output[pos + 3] = static_cast<uint8_t>(new_seg_len & 0xFF);
                std::memcpy(output + pos + 4, "Exif\0\0", 6);
                std::memcpy(output + pos + 10, grown.data(), grown.size());
                std::memcpy(output + pos + 10 + grown.size(), input + pos + 2 + seg_len, input_size - (pos + 2 + seg_len));
                *out_written = required;
                return LPB_RESULT_OK;
            } else {
                *out_written = required;
                return LPB_RESULT_BUFFER_TOO_SMALL;
            }
        }
        pos += 2 + seg_len;
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
        size_t p = iinf_body;
        uint8_t version = input[p];
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
            
            uint32_t size = read_be32u(input + p);
            if (is_box_type(input + p, "infe")) {
                uint8_t infe_version = input[p+8];
                size_t infe_body = p + 12;
                uint32_t item_id;
                if (infe_version >= 2) {
                    if (infe_version == 2) item_id = (static_cast<uint16_t>(input[infe_body]) << 8) | input[infe_body+1];
                    else item_id = read_be32u(input + infe_body);
                    size_t type_pos = infe_version == 2 ? infe_body + 4 : infe_body + 6;
                    if (input[type_pos] == 'E' && input[type_pos+1] == 'x' && input[type_pos+2] == 'i' && input[type_pos+3] == 'f') {
                        target_item_id = item_id;
                        break;
                    }
                }
            }
            p += size;
        }
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





