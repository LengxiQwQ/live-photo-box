#pragma once

#include <cstdint>
#include <cstddef>
#include <string>

namespace lpb::protocols {

bool samsung_sef_get_entry_fingerprint(
    const uint8_t* input,
    size_t input_size,
    uint16_t target_marker,
    std::string& out_fp);

} // namespace lpb::protocols
