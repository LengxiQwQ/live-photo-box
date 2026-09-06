#pragma once

#include <string>
#include <string_view>
#include <cstdint>
#include <cstddef>

namespace lpb::crypto {

// Formats first 16 bytes (128 bits) of SHA-256 hash as a 32-character lowercase hex string.
// Guaranteed to fit safely in char[64] without truncation.
std::string to_hex_fingerprint(const uint8_t hash[32]);

// XMP Property: namespace URI + local name + canonical value
std::string compute_xmp_property_fingerprint(
    std::string_view uri,
    std::string_view local_name,
    std::string_view value);

// XMP Container Item: Semantic + Mime + Length + Padding
std::string compute_xmp_container_item_fingerprint(
    std::string_view semantic,
    std::string_view mime,
    uint64_t length,
    uint64_t padding,
    bool has_padding = false);

// Apple MakerNote Tag: tag id + type + count + raw value bytes
std::string compute_apple_makernote_tag_fingerprint(
    uint16_t tag,
    uint16_t type,
    uint32_t count,
    const uint8_t* val_bytes,
    size_t val_len);

// Samsung SEF Entry: marker/tag + name + payload length + payload SHA-256
std::string compute_samsung_sef_entry_fingerprint(
    uint16_t marker,
    std::string_view name,
    uint32_t payload_len,
    const uint8_t* payload_data,
    size_t payload_size);

// ISOBMFF box: box type + structural identity + relevant payload fingerprint
std::string compute_isobmff_box_fingerprint(
    const char box_type[4],
    uint64_t box_size,
    const uint8_t* payload,
    size_t payload_len);

// QuickTime mdta key: key name + value bytes
std::string compute_mdta_key_fingerprint(
    std::string_view key_name,
    const uint8_t* val_bytes,
    size_t val_len);

// Metadata track: handler type + pattern name
std::string compute_metadata_track_fingerprint(
    std::string_view handler_type,
    std::string_view pattern_name);

// Protocol tail range: marker + length + tail SHA-256
std::string compute_tail_range_fingerprint(
    std::string_view marker,
    const uint8_t* tail_data,
    size_t tail_len);

} // namespace lpb::crypto
