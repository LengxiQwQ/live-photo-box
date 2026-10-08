#pragma once

#include <cstdint>
#include <cstddef>
#include <span>
#include <string>
#include <vector>
#include "livephotobox_native.h"

#ifdef __cplusplus
extern "C" {
#endif

// Add internal helper declarations here if needed across metadata components

#ifdef __cplusplus
}

namespace lpb::metadata {

// Internal C++ helpers shared by Native pipeline components. These do not
// export symbols or grant Cleaner authority to external callers.
bool extract_standard_jpeg_xmp(
    std::span<const uint8_t> input,
    std::string& out_xml) noexcept;

lpb_result replace_standard_jpeg_xmp_unchecked(
    std::span<const uint8_t> input,
    std::span<const uint8_t> replacement_xmp,
    std::vector<uint8_t>& out_bytes,
    std::string& out_error) noexcept;

} // namespace lpb::metadata
#endif
