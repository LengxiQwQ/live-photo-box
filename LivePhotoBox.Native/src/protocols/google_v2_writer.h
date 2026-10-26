#pragma once
#include "livephotobox_native.h"
#include <span>
#include <vector>
namespace lpb::target {
lpb_result build_google_v2_primary(lpb_context*,std::span<const uint8_t>,uint64_t video_length,
 uint32_t format,bool cover_present,int64_t cover,std::vector<uint8_t>&) noexcept;
}
