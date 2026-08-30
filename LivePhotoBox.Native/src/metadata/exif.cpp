#include "foundation/internal.h"
#include "binary/endian.h"
#include "exif.h"

extern "C" {

static uint16_t read_u16(const uint8_t* p, bool big_endian) {
    if (big_endian) {
        return (uint16_t)((p[0] << 8) | p[1]);
    } else {
        return (uint16_t)((p[1] << 8) | p[0]);
    }
}

static uint32_t read_u32(const uint8_t* p, bool big_endian) {
    if (big_endian) {
        return read_be32u(p);
    } else {
        return ((uint32_t)p[3] << 24) | ((uint32_t)p[2] << 16) | ((uint32_t)p[1] << 8) | p[0];
    }
}

bool parse_ifd(const uint8_t* data, size_t data_size, size_t tiff_start, size_t ifd_offset, bool is_big_endian, tiff_ifd* out_ifd) {
    if (!data || !out_ifd) return false;
    
    size_t abs_pos = tiff_start + ifd_offset;
    if (abs_pos + 2 > data_size) return false;

    uint16_t entry_count = read_u16(data + abs_pos, is_big_endian);
    if (abs_pos + 2 + entry_count * 12 + 4 > data_size) return false;

    out_ifd->absolute_pos = abs_pos;
    out_ifd->entries.clear();
    out_ifd->entries.reserve(entry_count);

    size_t p = abs_pos + 2;
    for (uint16_t i = 0; i < entry_count; i++) {
        tiff_entry entry;
        entry.absolute_pos = p;
        entry.tag = read_u16(data + p, is_big_endian);
        entry.type = read_u16(data + p + 2, is_big_endian);
        entry.count = read_u32(data + p + 4, is_big_endian);
        entry.value_offset = read_u32(data + p + 8, is_big_endian);
        out_ifd->entries.push_back(entry);
        p += 12;
    }

    out_ifd->next_ifd_offset = read_u32(data + p, is_big_endian);
    return true;
}

}
