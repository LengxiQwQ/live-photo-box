#pragma once

#include "livephotobox_native.h"

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <string_view>
#include <vector>

namespace lpb::containers {

// Internal parser result only; diagnostic text never determines inspection status.
// Keep this out of the public C ABI and successful item-facts layout.
enum class heif_graph_error {
    none,
    malformed,
    duplicate_identity,
    missing_reference,
    reference_cycle,
    unsupported_layout,
    capacity_exceeded,
    internal_failure
};

struct heif_graph_diagnostic {
    heif_graph_error code{heif_graph_error::none};
    std::string message;
};

lpb_inspection_failure_category inspection_category(heif_graph_error error) noexcept;

bool enumerate_auxiliary_items(std::span<const uint8_t> input,
    std::vector<lpb_auxiliary_item_facts>& items, heif_graph_diagnostic& diagnostic) noexcept;

bool attach_gainmap_auxiliary_graph(std::span<const uint8_t> input,
    uint32_t primary_item_id, uint32_t gainmap_item_id,
    std::vector<uint8_t>& output, std::string& error) noexcept;

bool enumerate_xmp_ranges_describing_item(std::span<const uint8_t> input,
    uint32_t described_item_id, std::vector<lpb_media_range>& ranges,
    std::string& error) noexcept;

// Project-owned item-graph XMP transport. XML semantics and ordinary-field merge
// belong to the caller; the replacement is the complete merged primary packet.
// Success preserves every non-selected item payload and its item identity.
// input and output must use separate storage. No public C ABI is added.
bool read_primary_xmp(std::span<const uint8_t> input, uint32_t& primary_item_id,
    std::string& xml, std::string& error) noexcept;

bool rewrite_primary_xmp(std::span<const uint8_t> input, std::string_view replacement_xml,
    std::vector<uint8_t>& output, std::string& error) noexcept;

struct heif_primary_properties {
    uint32_t item_id{}, width{}, height{}, orientation{};
    std::string item_type;
    std::vector<lpb_media_range> encoding_extents;
    std::vector<uint8_t> exif_payload; // JPEG-compatible Exif\0\0 + TIFF bytes
};
bool observe_primary_item_properties(std::span<const uint8_t> input,
    heif_primary_properties& output, std::string& error) noexcept;

} // namespace lpb::containers
