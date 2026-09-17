#pragma once

// Protocol/container truth is allowed to depend on the opaque C ABI context
// and its error/authority callbacks, but never on a Win32 handle or path.
#include "livephotobox_native.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <utility>
#include <vector>

void set_error(lpb_context* context, const char* message) noexcept;
bool lpb_has_clean_authority(const lpb_context* context) noexcept;
lpb_result copy_output(
    lpb_context* context,
    const std::vector<uint8_t>& value,
    uint8_t* output,
    size_t output_size,
    size_t* required_size) noexcept;
