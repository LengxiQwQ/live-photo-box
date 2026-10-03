#pragma once

#include "binary/portable_io.h"
#include "livephotobox_native.h"
#include "media/video_sidecar_abi.h"
#include "platform/windows_filesystem.h"

#include <array>

struct lpb_context;

namespace lpb::media
{

struct video_sidecar_bridge_result
{
    lpb_result native_result{LPB_RESULT_INTERNAL_ERROR};
    lpb_video_sidecar_result_v1 sidecar_result{};
    std::array<char, 256> reason{};
};

video_sidecar_bridge_result run_video_sidecar_transcode(
    lpb_context* context,
    const lpb::random_access_reader& input,
    windows_owned_output& output,
    lpb_video_container target_container,
    lpb_video_codec target_codec,
    int32_t crf,
    bool native_transplants_mebx) noexcept;

} // namespace lpb::media
