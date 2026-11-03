#pragma once
#include "livephotobox_native.h"
#include <cstdint>
#include <string>
#include <vector>

// Internal serialization only. The caller must establish Neutral authority,
// pin the inputs and validate the complete output before publication.
namespace lpb::target {
lpb_result serialize_dual_file(lpb_context*, uint32_t target,
    const std::vector<uint8_t>& image, const std::vector<uint8_t>& video,
    bool heif, int64_t cover_us, uint32_t cover_frame, const std::string& pairing_id,
    std::vector<uint8_t>& image_out, std::vector<uint8_t>& video_out);
}
