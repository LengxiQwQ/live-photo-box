#pragma once

#include "livephotobox_native.h"

namespace lpb::media {

lpb_result convert_image_file(
    lpb_context* context,
    const char* input_image_path,
    const char* output_image_path,
    lpb_image_container target_container,
    int32_t quality,
    int32_t* out_reencoded) noexcept;

} // namespace lpb::media
