#pragma once

#include <cstdint>
#include <cstddef>
#include <vector>

#ifdef __cplusplus
extern "C" {
#endif

// Represents an IFD entry in TIFF
struct tiff_entry {
    uint16_t tag;
    uint16_t type;
    uint32_t count;
    uint32_t value_offset; // Raw 4 bytes, might be the value itself if size <= 4
    size_t absolute_pos;   // Absolute offset in the TIFF stream
};

// Represents an IFD directory
struct tiff_ifd {
    size_t absolute_pos;
    uint32_t next_ifd_offset;
    std::vector<tiff_entry> entries;
};

// Parses a TIFF IFD
// tiff_start: absolute position of TIFF header in the input buffer
// ifd_offset: relative offset from tiff_start to the IFD
bool parse_ifd(const uint8_t* data, size_t data_size, size_t tiff_start, size_t ifd_offset, bool is_big_endian, tiff_ifd* out_ifd);

#ifdef __cplusplus
}
#endif
