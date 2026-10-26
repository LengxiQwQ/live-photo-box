#pragma once
#include "livephotobox_native.h"
#include <vector>
#include <span>
namespace lpb::media {
// Read-only observations on bytes obtained through retained Writer leases.
// Does not classify a source vendor or mint mutation authority.
lpb_result observe_target_writer_primary(lpb_context*, const std::vector<uint8_t>&) noexcept;
lpb_result observe_target_neutral_exif(std::span<const uint8_t>) noexcept;
}
