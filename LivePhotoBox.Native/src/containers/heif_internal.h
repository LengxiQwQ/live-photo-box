#pragma once

#include "livephotobox_native.h"

#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace lpb::containers {

bool attach_gainmap_auxiliary_graph(std::span<const uint8_t> input,
    uint32_t primary_item_id, uint32_t gainmap_item_id,
    std::vector<uint8_t>& output, std::string& error) noexcept;

bool enumerate_xmp_ranges_describing_item(std::span<const uint8_t> input,
    uint32_t described_item_id, std::vector<lpb_media_range>& ranges,
    std::string& error) noexcept;

} // namespace lpb::containers
