#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "exif.h"

using namespace lpb;

extern "C" {

bool parse_ifd(const uint8_t* data, size_t data_size, size_t tiff_start, size_t ifd_offset, bool is_big_endian, tiff_ifd* out_ifd) {
    if (!data || !out_ifd) return false;
    
    size_t abs_pos = tiff_start + ifd_offset;
    binary_reader reader(data, data_size);
    if (!reader.try_seek(abs_pos)) return false;

    uint16_t entry_count = 0;
    if (!reader.try_read_u16_endian(entry_count, is_big_endian)) return false;
    
    if (reader.remaining() < static_cast<size_t>(entry_count) * 12 + 4) return false;

    out_ifd->absolute_pos = abs_pos;
    out_ifd->entries.clear();
    out_ifd->entries.reserve(entry_count);

    for (uint16_t i = 0; i < entry_count; i++) {
        tiff_entry entry;
        entry.absolute_pos = reader.position();
        reader.try_read_u16_endian(entry.tag, is_big_endian);
        reader.try_read_u16_endian(entry.type, is_big_endian);
        reader.try_read_u32_endian(entry.count, is_big_endian);
        reader.try_read_u32_endian(entry.value_offset, is_big_endian);
        out_ifd->entries.push_back(entry);
    }

    reader.try_read_u32_endian(out_ifd->next_ifd_offset, is_big_endian);
    return true;
}

}

