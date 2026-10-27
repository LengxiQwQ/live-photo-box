#pragma once
#include "media/target_video_observation.h"
#include <span>
namespace lpb::media {
bool project_huawei_legacy_cover(const isobmff_video_presentation_timeline&,uint32_t frame,
    int64_t& us,uint32_t& count) noexcept;
struct huawei_footer_observation {uint32_t variant{},frame{};uint64_t movie_length{};std::string history;};
bool observe_huawei_footer(std::span<const uint8_t>,huawei_footer_observation&) noexcept;
bool observe_huawei_covertime(const target_covertime_observation&,int64_t& microseconds) noexcept;
bool select_huawei_frame(const isobmff_video_presentation_timeline&,int64_t microseconds,uint32_t& frame,uint32_t& count) noexcept;
}
