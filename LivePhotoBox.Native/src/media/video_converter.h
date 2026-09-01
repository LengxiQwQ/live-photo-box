#pragma once

#include "livephotobox_native.h"

namespace lpb::media {

lpb_result probe_video_file(
    lpb_context* context,
    const char* video_path,
    lpb_video_item_facts* out_video_facts) noexcept;

lpb_result remux_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container) noexcept;

lpb_result transcode_video_file(
    lpb_context* context,
    const char* input_video_path,
    const char* output_video_path,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    char* out_encoder_used,
    size_t encoder_buf_len) noexcept;

} // namespace lpb::media
