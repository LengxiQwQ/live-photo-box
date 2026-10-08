#pragma once

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <span>

namespace lpb::media::jpeg_gainmap_mpf {

// The Native facts contract admits at most eight auxiliaries in addition to
// the primary image. Keep the parser's MPF record storage bounded to that
// same maximum rather than allocating from untrusted TIFF counts.
inline constexpr size_t max_mpf_image_count = 9;

enum class status : uint8_t {
    absent,
    present,
    malformed,
};

struct mp_entry {
    uint32_t attributes{};
    uint32_t size{};
    uint32_t relative_offset{};
    uint64_t absolute_offset{};
    uint16_t dependent_image_1{};
    uint16_t dependent_image_2{};
    size_t size_field_offset{};
    size_t offset_field_offset{};
};

struct relationship {
    size_t tiff_header_offset{};
    bool little_endian{};
    uint32_t image_count{};
    uint32_t entries_byte_count{};
    size_t image_count_value_field_offset{};
    size_t entries_byte_count_field_offset{};
    size_t entries_value_field_offset{};
    size_t entries_array_offset{};
    std::array<mp_entry, max_mpf_image_count> entries{};
};

struct jpeg_info {
    size_t eoi_end{};
    status mpf_status{status::absent};
    relationship mpf{};
};

namespace detail {

inline uint16_t read_u16(std::span<const uint8_t> bytes, size_t offset, bool little_endian) noexcept {
    return little_endian
        ? static_cast<uint16_t>(bytes[offset] | (static_cast<uint16_t>(bytes[offset + 1]) << 8))
        : static_cast<uint16_t>((static_cast<uint16_t>(bytes[offset]) << 8) | bytes[offset + 1]);
}

inline uint32_t read_u32(std::span<const uint8_t> bytes, size_t offset, bool little_endian) noexcept {
    if (little_endian) {
        return static_cast<uint32_t>(bytes[offset]) |
            (static_cast<uint32_t>(bytes[offset + 1]) << 8) |
            (static_cast<uint32_t>(bytes[offset + 2]) << 16) |
            (static_cast<uint32_t>(bytes[offset + 3]) << 24);
    }
    return (static_cast<uint32_t>(bytes[offset]) << 24) |
        (static_cast<uint32_t>(bytes[offset + 1]) << 16) |
        (static_cast<uint32_t>(bytes[offset + 2]) << 8) |
        static_cast<uint32_t>(bytes[offset + 3]);
}

inline bool tiff_type_size(uint16_t type, uint64_t& size) noexcept {
    switch (type) {
    case 1: case 2: case 6: case 7: size = 1; return true;
    case 3: case 8: size = 2; return true;
    case 4: case 9: case 11: size = 4; return true;
    case 5: case 10: case 12: size = 8; return true;
    default: return false;
    }
}

inline bool parse_mpf_segment(
    std::span<const uint8_t> bytes,
    size_t payload_start,
    size_t payload_end,
    relationship& out,
    bool& out_is_index) noexcept {
    out_is_index = false;
    constexpr uint8_t signature[] = { 'M', 'P', 'F', 0 };
    if (payload_start > payload_end || payload_end > bytes.size() ||
        payload_end - payload_start < sizeof(signature) + 8 ||
        bytes[payload_start] != signature[0] || bytes[payload_start + 1] != signature[1] ||
        bytes[payload_start + 2] != signature[2] || bytes[payload_start + 3] != signature[3]) return false;

    const size_t tiff = payload_start + sizeof(signature);
    if (payload_end - tiff < 8) return false;
    bool little_endian = false;
    if (bytes[tiff] == 'I' && bytes[tiff + 1] == 'I') little_endian = true;
    else if (bytes[tiff] == 'M' && bytes[tiff + 1] == 'M') little_endian = false;
    else return false;
    if (read_u16(bytes, tiff + 2, little_endian) != 42) return false;

    const uint32_t ifd_offset = read_u32(bytes, tiff + 4, little_endian);
    if (ifd_offset < 8 || ifd_offset > payload_end - tiff ||
        payload_end - tiff - ifd_offset < 2) return false;
    const size_t ifd = tiff + static_cast<size_t>(ifd_offset);
    const uint16_t entry_count = read_u16(bytes, ifd, little_endian);
    const size_t after_count = ifd + 2;
    if (payload_end - after_count < 4 ||
        static_cast<size_t>(entry_count) > (payload_end - after_count - 4) / 12) return false;
    const size_t ifd_entries_end = after_count + static_cast<size_t>(entry_count) * 12;
    if (read_u32(bytes, ifd_entries_end, little_endian) != 0) return false;

    bool found_image_count = false;
    bool found_mp_entries = false;
    bool found_version = false;
    uint32_t image_count = 0;
    size_t image_count_value_field_offset = 0;
    size_t mp_entries_start = 0;
    uint32_t mp_entries_byte_count = 0;
    size_t mp_entries_count_field_offset = 0;
    size_t mp_entries_value_field_offset = 0;

    for (uint16_t index = 0; index < entry_count; ++index) {
        const size_t field = after_count + static_cast<size_t>(index) * 12;
        const uint16_t tag = read_u16(bytes, field, little_endian);
        const uint16_t type = read_u16(bytes, field + 2, little_endian);
        const uint32_t count = read_u32(bytes, field + 4, little_endian);
        uint64_t element_size = 0;
        if (!tiff_type_size(type, element_size) || count == 0 ||
            static_cast<uint64_t>(count) > std::numeric_limits<uint64_t>::max() / element_size) return false;
        const uint64_t value_bytes = static_cast<uint64_t>(count) * element_size;
        if (value_bytes > 4) {
            const uint32_t value_offset = read_u32(bytes, field + 8, little_endian);
            if (value_offset > payload_end - tiff ||
                value_bytes > static_cast<uint64_t>(payload_end - tiff - value_offset)) return false;
            const size_t value_start = tiff + static_cast<size_t>(value_offset);
            if (value_start < ifd_entries_end + 4) return false;
        }

        if (tag == 0xB000) {
            constexpr uint8_t expected_version[] = { '0', '1', '0', '0' };
            if (found_version || type != 7 || count != sizeof(expected_version) || value_bytes != 4 ||
                std::memcmp(bytes.data() + field + 8, expected_version, sizeof(expected_version)) != 0) return false;
            found_version = true;
        } else if (tag == 0xB001) {
            if (found_image_count || type != 4 || count != 1 || value_bytes != 4) return false;
            found_image_count = true;
            image_count = read_u32(bytes, field + 8, little_endian);
            image_count_value_field_offset = field + 8;
        } else if (tag == 0xB002) {
            if (found_mp_entries || type != 7 || value_bytes != count || count < 32 || count % 16 != 0) return false;
            const uint32_t value_offset = read_u32(bytes, field + 8, little_endian);
            if (value_offset > payload_end - tiff || count > payload_end - tiff - value_offset) return false;
            mp_entries_start = tiff + static_cast<size_t>(value_offset);
            if (mp_entries_start < ifd_entries_end + 4) return false;
            mp_entries_byte_count = count;
            mp_entries_count_field_offset = field + 4;
            mp_entries_value_field_offset = field + 8;
            found_mp_entries = true;
        }
    }

    if (!found_image_count && !found_mp_entries) {
        // Individual MPF images may carry a valid MPFVersion attribute IFD
        // without the two-entry MP Index used to bind a primary and GainMap.
        // It is not an image-range relationship and must not be mistaken for
        // one (or rejected as a malformed index).
        return found_version;
    }
    if (!found_image_count || !found_mp_entries || image_count < 2 || image_count > max_mpf_image_count ||
        image_count > std::numeric_limits<uint32_t>::max() / 16 ||
        mp_entries_byte_count != image_count * 16 ||
        mp_entries_start > payload_end || payload_end - mp_entries_start < mp_entries_byte_count) return false;

    relationship parsed{};
    parsed.tiff_header_offset = tiff;
    parsed.little_endian = little_endian;
    parsed.image_count = image_count;
    parsed.entries_byte_count = mp_entries_byte_count;
    parsed.image_count_value_field_offset = image_count_value_field_offset;
    parsed.entries_byte_count_field_offset = mp_entries_count_field_offset;
    parsed.entries_value_field_offset = mp_entries_value_field_offset;
    parsed.entries_array_offset = mp_entries_start;
    for (size_t index = 0; index < parsed.image_count; ++index) {
        const size_t entry_offset = mp_entries_start + index * 16;
        auto& entry = parsed.entries[index];
        entry.attributes = read_u32(bytes, entry_offset, little_endian);
        entry.size = read_u32(bytes, entry_offset + 4, little_endian);
        entry.relative_offset = read_u32(bytes, entry_offset + 8, little_endian);
        entry.dependent_image_1 = read_u16(bytes, entry_offset + 12, little_endian);
        entry.dependent_image_2 = read_u16(bytes, entry_offset + 14, little_endian);
        entry.size_field_offset = entry_offset + 4;
        entry.offset_field_offset = entry_offset + 8;
        if (entry.size == 0) return false;
        if (index == 0) {
            if (entry.relative_offset != 0) return false;
            entry.absolute_offset = 0;
        } else {
            if (parsed.tiff_header_offset > std::numeric_limits<uint64_t>::max() - entry.relative_offset) return false;
            entry.absolute_offset = static_cast<uint64_t>(parsed.tiff_header_offset) + entry.relative_offset;
        }
    }
    out = parsed;
    out_is_index = true;
    return true;
}

} // namespace detail

// Walks the actual JPEG marker stream, including entropy-coded scans, and
// parses MPF only from APP2 segments in that stream. Bytes following EOI are
// intentionally left for the caller to classify as a separate bounded range.
inline bool parse_jpeg(
    std::span<const uint8_t> bytes,
    jpeg_info& out) noexcept {
    out = {};
    if (bytes.size() < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;

    bool inside_scan = false;
    bool saw_mpf_marker = false;
    bool saw_mpf_index = false;
    size_t cursor = 2;
    while (cursor < bytes.size()) {
        if (inside_scan) {
            bool found_marker = false;
            while (cursor < bytes.size()) {
                if (bytes[cursor] != 0xFF) {
                    ++cursor;
                    continue;
                }
                const size_t marker_start = cursor;
                size_t code_position = cursor + 1;
                while (code_position < bytes.size() && bytes[code_position] == 0xFF) ++code_position;
                if (code_position >= bytes.size()) return false;
                const uint8_t scan_code = bytes[code_position];
                if (scan_code == 0x00 || (scan_code >= 0xD0 && scan_code <= 0xD7)) {
                    cursor = code_position + 1;
                    continue;
                }
                cursor = marker_start;
                found_marker = true;
                break;
            }
            if (!found_marker) return false;
            inside_scan = false;
        }

        if (cursor + 1 >= bytes.size() || bytes[cursor] != 0xFF) return false;
        size_t code_position = cursor + 1;
        while (code_position < bytes.size() && bytes[code_position] == 0xFF) ++code_position;
        if (code_position >= bytes.size()) return false;
        const uint8_t marker = bytes[code_position];
        const size_t marker_start = cursor;
        cursor = code_position + 1;

        if (marker == 0xD9) {
            out.eoi_end = cursor;
            out.mpf_status = saw_mpf_index ? status::present : status::absent;
            return true;
        }
        if (marker == 0x00 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) return false;
        if (marker == 0x01) continue;
        if (cursor + 2 > bytes.size()) return false;
        const uint16_t segment_length = static_cast<uint16_t>(
            (static_cast<uint16_t>(bytes[cursor]) << 8) | bytes[cursor + 1]);
        if (segment_length < 2 || segment_length > bytes.size() - cursor) return false;
        const size_t payload_start = cursor + 2;
        const size_t payload_end = cursor + segment_length;

        if (marker == 0xE2 && payload_end - payload_start >= 3 &&
            bytes[payload_start] == 'M' && bytes[payload_start + 1] == 'P' && bytes[payload_start + 2] == 'F') {
            if (saw_mpf_marker) {
                out.mpf_status = status::malformed;
                return false;
            }
            saw_mpf_marker = true;
            bool is_index = false;
            relationship parsed{};
            if (payload_end - payload_start < 4 || bytes[payload_start + 3] != 0 ||
                !detail::parse_mpf_segment(bytes, payload_start, payload_end, parsed, is_index)) {
                out.mpf_status = status::malformed;
                return false;
            }
            if (is_index) {
                out.mpf = parsed;
                saw_mpf_index = true;
            }
        }

        cursor = payload_end;
        if (marker == 0xDA) inside_scan = true;
        static_cast<void>(marker_start);
    }
    return false;
}

inline void write_u32(std::span<uint8_t> bytes, size_t offset, uint32_t value, bool little_endian) noexcept {
    if (little_endian) {
        bytes[offset] = static_cast<uint8_t>(value);
        bytes[offset + 1] = static_cast<uint8_t>(value >> 8);
        bytes[offset + 2] = static_cast<uint8_t>(value >> 16);
        bytes[offset + 3] = static_cast<uint8_t>(value >> 24);
    } else {
        bytes[offset] = static_cast<uint8_t>(value >> 24);
        bytes[offset + 1] = static_cast<uint8_t>(value >> 16);
        bytes[offset + 2] = static_cast<uint8_t>(value >> 8);
        bytes[offset + 3] = static_cast<uint8_t>(value);
    }
}

inline bool patch_layout(
    std::span<uint8_t> bytes,
    const relationship& mpf,
    size_t primary_eoi_end,
    size_t gainmap_offset,
    size_t gainmap_length) noexcept {
    if (mpf.image_count < 2 || mpf.image_count > max_mpf_image_count ||
        mpf.entries_byte_count != mpf.image_count * 16 ||
        primary_eoi_end == 0 || gainmap_offset < primary_eoi_end ||
        gainmap_offset < mpf.tiff_header_offset || primary_eoi_end > std::numeric_limits<uint32_t>::max() ||
        gainmap_length == 0 || gainmap_length > std::numeric_limits<uint32_t>::max() ||
        gainmap_offset - mpf.tiff_header_offset > std::numeric_limits<uint32_t>::max() ||
        mpf.image_count_value_field_offset > bytes.size() || bytes.size() - mpf.image_count_value_field_offset < 4 ||
        mpf.entries_byte_count_field_offset > bytes.size() || bytes.size() - mpf.entries_byte_count_field_offset < 4 ||
        mpf.entries_value_field_offset > bytes.size() || bytes.size() - mpf.entries_value_field_offset < 4 ||
        mpf.entries_array_offset > bytes.size() || bytes.size() - mpf.entries_array_offset < mpf.entries_byte_count) return false;

    const auto& first = mpf.entries[0];
    const auto& second = mpf.entries[1];
    if (first.relative_offset != 0 || first.size_field_offset > bytes.size() ||
        bytes.size() - first.size_field_offset < 4 || second.size_field_offset > bytes.size() ||
        bytes.size() - second.size_field_offset < 4 || second.offset_field_offset > bytes.size() ||
        bytes.size() - second.offset_field_offset < 4) return false;

    // Convert any bounded source index (including Primary/GainMap/Original)
    // into the two active entries of the final Neutral owner. Clearing the
    // old trailing records prevents stale external references from lingering.
    write_u32(bytes, mpf.image_count_value_field_offset, 2, mpf.little_endian);
    write_u32(bytes, mpf.entries_byte_count_field_offset, 32, mpf.little_endian);
    write_u32(bytes, mpf.entries_value_field_offset,
        static_cast<uint32_t>(mpf.entries_array_offset - mpf.tiff_header_offset), mpf.little_endian);
    write_u32(bytes, first.size_field_offset, static_cast<uint32_t>(primary_eoi_end), mpf.little_endian);
    write_u32(bytes, second.size_field_offset, static_cast<uint32_t>(gainmap_length), mpf.little_endian);
    write_u32(bytes, second.offset_field_offset,
        static_cast<uint32_t>(gainmap_offset - mpf.tiff_header_offset), mpf.little_endian);
    std::fill(bytes.begin() + static_cast<std::ptrdiff_t>(mpf.entries_array_offset + 32),
        bytes.begin() + static_cast<std::ptrdiff_t>(mpf.entries_array_offset + mpf.entries_byte_count), 0);
    return true;
}

inline bool layout_matches(
    const jpeg_info& jpeg,
    size_t total_size,
    size_t primary_eoi_end,
    size_t gainmap_offset,
    size_t gainmap_length) noexcept {
    if (jpeg.mpf_status != status::present || jpeg.mpf.image_count != 2 ||
        jpeg.mpf.entries_byte_count != 32 || jpeg.eoi_end != primary_eoi_end ||
        jpeg.mpf.entries[0].relative_offset != 0 || jpeg.mpf.entries[0].size != primary_eoi_end ||
        jpeg.mpf.entries[1].absolute_offset != gainmap_offset ||
        jpeg.mpf.entries[1].size != gainmap_length || gainmap_offset < primary_eoi_end ||
        gainmap_offset > total_size || gainmap_length > total_size - gainmap_offset) return false;
    const uint64_t primary_end = jpeg.mpf.entries[0].size;
    return primary_end <= gainmap_offset;
}

inline bool valid_jpeg_media_range(
    std::span<const uint8_t> bytes,
    size_t offset,
    size_t length) noexcept {
    if (offset > bytes.size() || length < 4 || length > bytes.size() - offset) return false;
    jpeg_info range{};
    const auto slice = bytes.subspan(offset, length);
    if (!parse_jpeg(slice, range) || range.mpf_status == status::malformed || range.eoi_end == 0) return false;
    for (size_t index = range.eoi_end; index < slice.size(); ++index) {
        if (slice[index] != 0x00 && slice[index] != 0xFF) return false;
    }
    return true;
}

} // namespace lpb::media::jpeg_gainmap_mpf
