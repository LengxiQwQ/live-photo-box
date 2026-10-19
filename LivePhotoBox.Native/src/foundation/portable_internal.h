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

template <size_t N>
inline void copy_fixed_string(char (&dest)[N], const std::string& src) noexcept {
    const size_t len = std::min(src.size(), N - 1);
    std::memcpy(dest, src.data(), len);
    dest[len] = '\0';
}

template <size_t N>
inline void copy_fixed_string(char (&dest)[N], const char* src) noexcept {
    if (!src) {
        dest[0] = '\0';
        return;
    }
    const size_t len = std::min(std::strlen(src), N - 1);
    std::memcpy(dest, src, len);
    dest[len] = '\0';
}

inline size_t safe_strnlen(const char* s, size_t max_len) noexcept {
    if (!s) return 0;
    size_t len = 0;
    while (len < max_len && s[len] != '\0') {
        ++len;
    }
    return len;
}


