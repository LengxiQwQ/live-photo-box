#pragma once

#include "livephotobox_native.h"
#include <span>
namespace lpb::media {
// Source fact projection only. Never interprets the historical footer field
// as a timestamp, and never mints writer authority from source provenance.
bool observe_huawei_source_timing(std::span<const uint8_t> movie,
    uint32_t legacy_frame, int64_t& cover_us, uint32_t& total_frames) noexcept;
}

// Public declaration and POD live in livephotobox_native.h. The implementation
// deliberately observes fixed source/final bytes and does not reuse the legacy
// timing DTO, whose absent numeric fields do not carry presence information.
