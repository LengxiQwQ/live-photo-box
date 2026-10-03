#pragma once

#include "video_sidecar_abi.h"

namespace lpb::media
{

class VideoSidecarModule final
{
public:
    VideoSidecarModule() noexcept = default;
    ~VideoSidecarModule() noexcept;

    VideoSidecarModule(const VideoSidecarModule&) = delete;
    VideoSidecarModule& operator=(const VideoSidecarModule&) = delete;

    bool load() noexcept;
    const lpb_video_sidecar_abi_info_v1* info() const noexcept;
    lpb_video_sidecar_transcode_fn_v1 transcode() const noexcept;
    const char* failure_reason() const noexcept;

private:
    void* module_{};
    void* pinned_file_{};
    lpb_video_sidecar_query_fn_v1 query_{};
    lpb_video_sidecar_transcode_fn_v1 transcode_{};
    lpb_video_sidecar_abi_info_v1 info_{};
    const char* failure_reason_{"The exact video sidecar has not been loaded."};
};

} // namespace lpb::media
