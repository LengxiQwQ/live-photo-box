#pragma once

#include "livephotobox_native.h"

namespace lpb::media {

lpb_result extract_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path) noexcept;

} // namespace lpb::media
