#pragma once

#include <cstdint>
#include <limits>
#include <span>
#include <utility>
#include <vector>

namespace lpb::media {
inline constexpr size_t sei_message_byte_budget = 8U * 1024U * 1024U;
inline constexpr size_t sei_access_unit_byte_budget = 16U * 1024U * 1024U;
inline constexpr size_t sei_pending_byte_budget = 64U * 1024U * 1024U;
inline constexpr size_t sei_access_unit_message_budget = 256U;
inline constexpr size_t sei_configuration_message_budget = 1024U;
inline constexpr size_t sei_pending_message_budget = 4096U;
struct preserved_sei_message {
    uint32_t type{};
    std::vector<uint8_t> payload;
};

inline bool sei_metadata_supports_target(std::span<const preserved_sei_message> messages, bool hevc) noexcept {
    if (hevc) return true;
    for (const auto& message : messages) {
        if (message.type != 4U) continue;
        // Only A/53 captions have a supported registered delivery in AVC.
        // HDR Vivid/HDR10+ cannot depend on optional decoder AVFrame side data
        // to trigger the explicit no-H264-degradation policy.
        const auto& p = message.payload;
        if (p.size() < 8U || p[0] != 0xB5U || p[1] != 0U || p[2] != 0x31U ||
            p[3] != 'G' || p[4] != 'A' || p[5] != '9' || p[6] != '4' || p[7] != 3U) return false;
    }
    return true;
}

inline bool preserved_sei_type(uint32_t type) noexcept {
    // Registered/unregistered user metadata, mastering display and light level.
    // Codec timing, recovery and decoded-picture hashes are encoder-owned.
    return type == 4U || type == 5U || type == 137U || type == 144U;
}

inline bool collect_packet_sei(std::span<const uint8_t> packet, unsigned length_bytes,
    bool hevc, std::vector<preserved_sei_message>& metadata, bool reject_codec_owned = false) {
    metadata.clear();
    if (length_bytes < 1U || length_bytes > 4U) return false;
    size_t position = 0U;
    size_t metadata_bytes = 0U;
    size_t sei_bytes = 0U, messages = 0U;
    while (position < packet.size()) {
        if (packet.size() - position < length_bytes) return false;
        uint32_t length = 0U;
        for (unsigned i = 0U; i < length_bytes; ++i) length = (length << 8U) | packet[position++];
        const size_t header = hevc ? 2U : 1U;
        if (length < header || length > packet.size() - position) return false;
        const auto nal = packet.subspan(position, length);
        position += length;
        if ((nal[0] & 0x80U) != 0U || (hevc && ((nal[1] & 7U) == 0U || (nal[0] & 1U) != 0U || (nal[1] & 0xF8U) != 0U))) return false;
        const unsigned type = hevc ? (nal[0] >> 1U) & 63U : nal[0] & 31U;
        if (hevc && ((type >= 10U && type <= 15U) || (type >= 22U && type <= 31U) || type >= 41U))
            return false; // Reserved/unspecified VCL, extensions and Dolby RPU have no preservation owner.
        if (hevc ? type != 39U && type != 40U : type != 6U) continue;
        if (nal.size() > sei_access_unit_byte_budget - sei_bytes) return false;
        sei_bytes += nal.size();
        std::vector<uint8_t> rbsp;
        unsigned zeros = 0U;
        for (size_t i = header; i < nal.size(); ++i) {
            const uint8_t byte = nal[i];
            if (zeros >= 2U && byte == 3U) {
                if (i + 1U == nal.size() || nal[i + 1U] > 3U) return false;
                zeros = 0U; continue;
            }
            if (zeros >= 2U && byte < 3U) return false;
            rbsp.push_back(byte); zeros = byte == 0U ? zeros + 1U : 0U;
        }
        size_t p = 0U;
        while (p < rbsp.size()) {
            if (rbsp.size() - p == 1U && rbsp[p] == 0x80U) { ++p; break; }
            if (++messages > sei_access_unit_message_budget) return false;
            uint32_t payload_type = 0U, payload_size = 0U;
            for (auto* value : {&payload_type, &payload_size}) {
                uint8_t byte = 0U;
                do {
                    if (p == rbsp.size() || *value > UINT32_MAX - 255U) return false;
                    byte = rbsp[p++]; *value += byte;
                } while (byte == 255U);
            }
            if (payload_size > rbsp.size() - p || payload_size == rbsp.size() - p) return false;
            if (preserved_sei_type(payload_type)) {
                if (payload_size > sei_message_byte_budget ||
                    payload_size > sei_access_unit_byte_budget - metadata_bytes) return false;
                metadata_bytes += payload_size;
                if ((payload_type == 5U && payload_size < 16U) ||
                    (payload_type == 137U && payload_size != 24U) ||
                    (payload_type == 144U && payload_size != 4U)) return false;
                if (payload_type == 4U) {
                    const auto value = std::span<const uint8_t>(rbsp).subspan(p, payload_size);
                    const bool vivid = value.size() >= 7U && value[0] == 0x26U && value[1] == 0U && value[2] == 4U &&
                        value[3] == 0U && value[4] == 5U && value[5] == 1U;
                    const bool hdr10plus = value.size() >= 8U && value[0] == 0xB5U && value[1] == 0U && value[2] == 0x3CU &&
                        value[3] == 0U && value[4] == 1U && value[5] == 4U && value[6] <= 1U;
                    const bool captions = value.size() >= 8U && value[0] == 0xB5U && value[1] == 0U && value[2] == 0x31U &&
                        value[3] == 'G' && value[4] == 'A' && value[5] == '9' && value[6] == '4' && value[7] == 3U;
                    // Other T.35 providers may contain codec-dependent data
                    // such as enhancement layers; never copy those blindly.
                    if (!vivid && !hdr10plus && !captions) return false;
                }
                metadata.push_back({payload_type, {rbsp.begin() + p, rbsp.begin() + p + payload_size}});
            } else if (reject_codec_owned || (payload_type != 0U && payload_type != 3U && payload_type != 6U &&
                payload_type != 128U && payload_type != 129U && payload_type != 130U && payload_type != 132U))
                return false; // Includes pic_timing: field/repeat semantics are not regenerated.
            p += payload_size;
        }
        if (p != rbsp.size() || rbsp.empty() || rbsp.back() != 0x80U) return false;
    }
    return position == packet.size();
}

inline bool make_preserved_sei_nal(std::span<const preserved_sei_message> metadata,
    bool hevc, std::vector<uint8_t>& nal) {
    nal.clear();
    if (metadata.empty()) return true;
    std::vector<uint8_t> rbsp;
    const auto integer = [&](size_t value) {
        while (value >= 255U) { rbsp.push_back(255U); value -= 255U; }
        rbsp.push_back(static_cast<uint8_t>(value));
    };
    for (const auto& item : metadata) {
        if (!preserved_sei_type(item.type) || item.payload.size() > UINT32_MAX) return false;
        integer(item.type); integer(item.payload.size());
        rbsp.insert(rbsp.end(), item.payload.begin(), item.payload.end());
    }
    rbsp.push_back(0x80U);
    nal.push_back(hevc ? 0x4EU : 0x06U);
    if (hevc) nal.push_back(1U);
    unsigned zeros = 0U;
    for (const auto byte : rbsp) {
        if (zeros >= 2U && byte <= 3U) { nal.push_back(3U); zeros = 0U; }
        nal.push_back(byte); zeros = byte == 0U ? zeros + 1U : 0U;
    }
    return true;
}

inline bool collect_hevc_configuration_sei(std::span<const uint8_t> configuration,
    std::vector<preserved_sei_message>& metadata) {
    metadata.clear();
    if (configuration.size() < 23U || configuration.size() > sei_access_unit_byte_budget || configuration[0] != 1U) return false;
    size_t p = 23U;
    for (unsigned array = 0U; array < configuration[22]; ++array) {
        if (configuration.size() - p < 3U || (configuration[p] & 0x40U) != 0U) return false;
        const unsigned type = configuration[p++] & 63U;
        if (type != 32U && type != 33U && type != 34U && type != 39U && type != 40U) return false;
        const unsigned count = (static_cast<unsigned>(configuration[p]) << 8U) | configuration[p + 1U];
        p += 2U;
        for (unsigned i = 0U; i < count; ++i) {
            if (configuration.size() - p < 2U) return false;
            const size_t size = (static_cast<size_t>(configuration[p]) << 8U) | configuration[p + 1U];
            if (size < 2U || configuration.size() - p - 2U < size ||
                ((configuration[p + 2U] >> 1U) & 63U) != type) return false;
            std::vector<preserved_sei_message> entries;
            if (!collect_packet_sei(configuration.subspan(p, size + 2U), 2U, true, entries)) return false;
            if (entries.size() > sei_configuration_message_budget - metadata.size()) return false;
            for (auto& item : entries) metadata.push_back(std::move(item));
            p += size + 2U;
        }
    }
    return p == configuration.size();
}
} // namespace lpb::media
