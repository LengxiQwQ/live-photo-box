#include "residue_fingerprint.h"
#include "sha256.h"

#include <cstring>
#include <vector>

namespace lpb::crypto {

std::string to_hex_fingerprint(const uint8_t hash[32]) {
    static const char hex_chars[] = "0123456789abcdef";
    std::string s;
    s.reserve(32);
    for (size_t i = 0; i < 16; ++i) {
        s.push_back(hex_chars[(hash[i] >> 4) & 0x0F]);
        s.push_back(hex_chars[hash[i] & 0x0F]);
    }
    return s;
}

std::string compute_xmp_property_fingerprint(
    std::string_view uri,
    std::string_view local_name,
    std::string_view value)
{
    std::string canonical;
    canonical.reserve(uri.size() + 1 + local_name.size() + 1 + value.size());
    canonical.append(uri);
    canonical.push_back(':');
    canonical.append(local_name);
    canonical.push_back('=');
    canonical.append(value);

    uint8_t hash[32];
    sha256_buffer(reinterpret_cast<const uint8_t*>(canonical.data()), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_xmp_container_item_fingerprint(
    std::string_view semantic,
    std::string_view mime,
    uint64_t length,
    uint64_t padding,
    bool has_padding)
{
    std::string canonical = "Item:Semantic=";
    canonical.append(semantic);
    canonical.append(";Mime=");
    canonical.append(mime);
    canonical.append(";Length=");
    canonical.append(std::to_string(length));
    if (has_padding) {
        canonical.append(";Padding=");
        canonical.append(std::to_string(padding));
    }

    uint8_t hash[32];
    sha256_buffer(reinterpret_cast<const uint8_t*>(canonical.data()), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_apple_makernote_tag_fingerprint(
    uint16_t tag,
    uint16_t type,
    uint32_t count,
    const uint8_t* val_bytes,
    size_t val_len)
{
    std::vector<uint8_t> canonical;
    canonical.resize(8 + val_len);
    canonical[0] = static_cast<uint8_t>((tag >> 8) & 0xFF);
    canonical[1] = static_cast<uint8_t>(tag & 0xFF);
    canonical[2] = static_cast<uint8_t>((type >> 8) & 0xFF);
    canonical[3] = static_cast<uint8_t>(type & 0xFF);
    canonical[4] = static_cast<uint8_t>((count >> 24) & 0xFF);
    canonical[5] = static_cast<uint8_t>((count >> 16) & 0xFF);
    canonical[6] = static_cast<uint8_t>((count >> 8) & 0xFF);
    canonical[7] = static_cast<uint8_t>(count & 0xFF);
    if (val_bytes && val_len > 0) {
        std::memcpy(canonical.data() + 8, val_bytes, val_len);
    }

    uint8_t hash[32];
    sha256_buffer(canonical.data(), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_samsung_sef_entry_fingerprint(
    uint16_t marker,
    std::string_view name,
    uint32_t payload_len,
    const uint8_t* payload_data,
    size_t payload_size)
{
    uint8_t payload_hash[32]{};
    if (payload_data && payload_size > 0) {
        sha256_buffer(payload_data, payload_size, payload_hash);
    }

    std::vector<uint8_t> canonical;
    canonical.reserve(2 + name.size() + 4 + 32);
    canonical.push_back(static_cast<uint8_t>((marker >> 8) & 0xFF));
    canonical.push_back(static_cast<uint8_t>(marker & 0xFF));
    canonical.insert(canonical.end(), name.begin(), name.end());
    canonical.push_back(static_cast<uint8_t>((payload_len >> 24) & 0xFF));
    canonical.push_back(static_cast<uint8_t>((payload_len >> 16) & 0xFF));
    canonical.push_back(static_cast<uint8_t>((payload_len >> 8) & 0xFF));
    canonical.push_back(static_cast<uint8_t>(payload_len & 0xFF));
    canonical.insert(canonical.end(), payload_hash, payload_hash + 32);

    uint8_t hash[32];
    sha256_buffer(canonical.data(), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_isobmff_box_fingerprint(
    const char box_type[4],
    uint64_t box_size,
    const uint8_t* payload,
    size_t payload_len)
{
    uint8_t payload_hash[32]{};
    if (payload && payload_len > 0) {
        sha256_buffer(payload, payload_len, payload_hash);
    }

    std::vector<uint8_t> canonical;
    canonical.reserve(4 + 8 + 32);
    canonical.insert(canonical.end(), box_type, box_type + 4);
    for (int i = 7; i >= 0; --i) {
        canonical.push_back(static_cast<uint8_t>((box_size >> (i * 8)) & 0xFF));
    }
    canonical.insert(canonical.end(), payload_hash, payload_hash + 32);

    uint8_t hash[32];
    sha256_buffer(canonical.data(), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_mdta_key_fingerprint(
    std::string_view key_name,
    const uint8_t* val_bytes,
    size_t val_len)
{
    uint8_t val_hash[32]{};
    if (val_bytes && val_len > 0) {
        sha256_buffer(val_bytes, val_len, val_hash);
    }

    std::string canonical(key_name);
    canonical.append(":");
    canonical.append(reinterpret_cast<const char*>(val_hash), sizeof(val_hash));

    uint8_t hash[32];
    sha256_buffer(reinterpret_cast<const uint8_t*>(canonical.data()), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_metadata_track_fingerprint(
    std::string_view handler_type,
    std::string_view pattern_name)
{
    std::string canonical(handler_type);
    canonical.append(":");
    canonical.append(pattern_name);

    uint8_t hash[32];
    sha256_buffer(reinterpret_cast<const uint8_t*>(canonical.data()), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

std::string compute_tail_range_fingerprint(
    std::string_view marker,
    const uint8_t* tail_data,
    size_t tail_len)
{
    uint8_t tail_hash[32]{};
    if (tail_data && tail_len > 0) {
        sha256_buffer(tail_data, tail_len, tail_hash);
    }

    std::string canonical(marker);
    canonical.append(":");
    canonical.append(std::to_string(tail_len));
    canonical.append(":");
    canonical.append(reinterpret_cast<const char*>(tail_hash), sizeof(tail_hash));

    uint8_t hash[32];
    sha256_buffer(reinterpret_cast<const uint8_t*>(canonical.data()), canonical.size(), hash);
    return to_hex_fingerprint(hash);
}

} // namespace lpb::crypto
