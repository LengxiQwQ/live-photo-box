#pragma once
#include "media/target_video_observation.h"
#include <array>
#include <string>
#include <vector>

namespace lpb::target {
// A read plan over a retained Neutral movie lease. Only metadata is snapshotted;
// original sample bytes stay in their original mdat and are streamed unchanged.
struct huawei_movie_plan {
    uint64_t input_length{}, output_length{}, moov_offset{}, old_moov_length{};
    std::vector<uint8_t> moov, ftyp;
    uint32_t cover_frame{}, total_frames{};
    bool read(const random_access_reader&, uint64_t, std::span<uint8_t>) const noexcept;
};
bool prepare_huawei_movie(const random_access_reader&, int64_t cover_us,
    huawei_movie_plan&, std::string&) noexcept;
// Each third of the fixed footer is a bounded ASCII field. There is never
// truncation, and historical fields are never converted to time authority.
bool make_huawei_footer(uint32_t variant, uint32_t frame, uint32_t count,
    uint64_t movie_length, std::array<uint8_t,60>&, std::string&) noexcept;
} // namespace lpb::target
