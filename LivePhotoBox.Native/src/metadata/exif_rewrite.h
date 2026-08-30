#pragma once

#include <cstdint>
#include <cstddef>
#include <vector>


// Returns a new TIFF buffer with MakerNotes removed.
// Returns an empty vector on error or if no MakerNote was found/removed.
std::vector<uint8_t> lpb_tiff_remove_makernotes(const uint8_t* tiff, size_t tiff_size, size_t ifd0_offset, bool is_big_endian);

// Returns a new TIFF buffer with the MakerNote appended and an entry inserted into the target IFD.
// Returns an empty vector on error.
std::vector<uint8_t> lpb_tiff_insert_makernote(const uint8_t* tiff, size_t tiff_size, size_t ifd0_offset, size_t exif_ifd_offset, const uint8_t* makernote, size_t makernote_size, bool is_big_endian);

// Helper to find the EXIF IFD pointer from IFD0
// Returns 0 if not found.
uint32_t lpb_tiff_find_exif_ptr(const uint8_t* tiff, size_t tiff_size, size_t ifd0_offset, bool is_big_endian);


