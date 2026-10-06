#pragma once

#include <cstddef>
#include <cstdint>
#include <span>

namespace lpb::media::detail {

inline uint32_t read_icc_be32(const uint8_t* data) noexcept {
    return (static_cast<uint32_t>(data[0]) << 24)
        | (static_cast<uint32_t>(data[1]) << 16)
        | (static_cast<uint32_t>(data[2]) << 8)
        | static_cast<uint32_t>(data[3]);
}

// Validates the ICC header and complete tag directory, then returns the
// profile's declared length. Bytes after that boundary are container payload,
// not part of the ICC profile.
inline bool get_declared_icc_profile_size(
    std::span<const uint8_t> payload,
    size_t& declared_size) noexcept {
    declared_size = 0;
    constexpr size_t kIccHeaderSize = 132;
    if (payload.size() < kIccHeaderSize) return false;

    const uint32_t declared = read_icc_be32(payload.data());
    if (declared < kIccHeaderSize || declared > payload.size()) return false;
    if (payload[36] != 'a' || payload[37] != 'c' ||
        payload[38] != 's' || payload[39] != 'p') return false;

    const uint32_t tag_count = read_icc_be32(payload.data() + 128);
    const size_t declared_length = static_cast<size_t>(declared);
    if (tag_count > (declared_length - kIccHeaderSize) / 12) return false;
    const size_t tag_table_end = kIccHeaderSize + static_cast<size_t>(tag_count) * 12;

    for (uint32_t index = 0; index < tag_count; ++index) {
        const size_t entry = kIccHeaderSize + static_cast<size_t>(index) * 12;
        const size_t offset = static_cast<size_t>(read_icc_be32(payload.data() + entry + 4));
        const size_t length = static_cast<size_t>(read_icc_be32(payload.data() + entry + 8));
        if (offset < tag_table_end || offset > declared_length ||
            length > declared_length - offset) return false;
    }

    declared_size = declared_length;
    return true;
}

} // namespace lpb::media::detail
