#pragma once

#include "containers/isobmff.h"

#include <algorithm>
#include <cstdint>

namespace lpb::media {

inline bool find_next_visible_presentation_sample(
    const isobmff_video_presentation_timeline& timeline,
    size_t start,
    size_t& found) noexcept {
    for (size_t index = start; index < timeline.presentation_order.size(); ++index) {
        if (timeline.presentation_order[index].visible) {
            found = index;
            return true;
        }
    }
    return false;
}

// Frozen Native video post-validation: output sample count must match and each
// output presentation timestamp must stay within the corresponding source
// presentation interval. Kept private so the direct-MFT proof uses the exact
// production acceptance rule.
inline bool presentation_timing_within_source_frame_intervals(
    const isobmff_video_presentation_timeline& source,
    const isobmff_video_presentation_timeline& output,
    uint32_t& failing_frame) noexcept {
    failing_frame = 0U;
    if (source.visible_sample_count == 0U ||
        output.visible_sample_count != source.visible_sample_count ||
        source.presentation_order.size() != source.sample_count ||
        output.presentation_order.size() != output.sample_count) return false;

    const auto one_tick_100ns = [](uint32_t timescale) noexcept -> uint64_t {
        if (timescale == 0U) return 0U;
        constexpr uint64_t units_per_second = 10000000ULL;
        return (units_per_second + static_cast<uint64_t>(timescale) - 1ULL) /
            static_cast<uint64_t>(timescale);
    };
    const uint64_t single_frame_tolerance = std::max(
        one_tick_100ns(source.media_timescale), one_tick_100ns(output.media_timescale));

    size_t source_cursor = 0U;
    size_t output_cursor = 0U;
    size_t previous_source_index = 0U;
    for (uint32_t frame = 0U; frame < source.visible_sample_count; ++frame) {
        size_t source_index = 0U;
        size_t output_index = 0U;
        if (!find_next_visible_presentation_sample(source, source_cursor, source_index) ||
            !find_next_visible_presentation_sample(output, output_cursor, output_index)) {
            failing_frame = frame;
            return false;
        }

        const int64_t source_time = source.presentation_order[source_index].normalized_time_100ns;
        const int64_t output_time = output.presentation_order[output_index].normalized_time_100ns;
        if (source_time < 0 || output_time < 0) {
            failing_frame = frame;
            return false;
        }

        uint64_t source_interval = single_frame_tolerance;
        if (frame + 1U < source.visible_sample_count) {
            size_t next_source_index = 0U;
            if (!find_next_visible_presentation_sample(source, source_index + 1U, next_source_index)) {
                failing_frame = frame;
                return false;
            }
            const int64_t next_source_time = source.presentation_order[next_source_index].normalized_time_100ns;
            if (next_source_time <= source_time) {
                failing_frame = frame;
                return false;
            }
            source_interval = static_cast<uint64_t>(next_source_time - source_time);
        } else if (frame > 0U) {
            const int64_t previous_source_time = source.presentation_order[previous_source_index].normalized_time_100ns;
            if (source_time <= previous_source_time) {
                failing_frame = frame;
                return false;
            }
            source_interval = static_cast<uint64_t>(source_time - previous_source_time);
        }

        const uint64_t delta = source_time >= output_time
            ? static_cast<uint64_t>(source_time - output_time)
            : static_cast<uint64_t>(output_time - source_time);
        if (delta > source_interval) {
            failing_frame = frame;
            return false;
        }

        previous_source_index = source_index;
        source_cursor = source_index + 1U;
        output_cursor = output_index + 1U;
    }

    size_t extra_index = 0U;
    return !find_next_visible_presentation_sample(source, source_cursor, extra_index) &&
        !find_next_visible_presentation_sample(output, output_cursor, extra_index);
}

} // namespace lpb::media
