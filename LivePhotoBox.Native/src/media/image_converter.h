#pragma once

#include "livephotobox_native.h"

namespace lpb::media {
lpb_result convert_image_file_v2(lpb_context*,const char*,const char*,
    const lpb_image_conversion_request_v2*,lpb_image_conversion_proof_v1*) noexcept;
lpb_result resolve_unspecified_jpeg_orientation_file(lpb_context*,const char*,const char*,
    const lpb_unspecified_jpeg_orientation_request_v1*,lpb_image_conversion_proof_v1*) noexcept;

lpb_result convert_image_file(
    lpb_context* context,
    const char* input_image_path,
    const char* output_image_path,
    lpb_image_container target_container,
    int32_t quality,
    int32_t* out_reencoded) noexcept;

} // namespace lpb::media
