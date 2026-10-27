#pragma once
#include "containers/isobmff.h"
#include "containers/isobmff_video_profile.h"
#include <string>

namespace lpb::media {
enum class target_video_failure { none, malformed, ambiguous, source_binding, unsupported, io };
enum class target_video_stage { none, metadata, generic_media, profile };
struct target_video_diagnostic {
    target_video_failure failure{target_video_failure::none};
    target_video_stage stage{target_video_stage::none};
    uint64_t offset{UINT64_MAX};
    std::string reason;
};
struct target_video_observation {
    isobmff_reader_observation media;
    isobmff_video_profile profile;
};
// Optional factual capture for a target output. Default Neutral eligibility
// still rejects this source binding. Callers apply independent owner/type/time
// rules to EVERY captured occurrence, including wrong-owner shadows.
struct target_covertime_property {
    std::string owner;
    uint32_t type{}, locale{};
    std::vector<uint8_t> value;
};
struct target_covertime_observation { std::vector<target_covertime_property> properties; };
// Read-only eligibility, not mutation authority or target conformance. Unknown
// metadata stays opaque under N9 and never grants placement/timing authority.
bool observe_target_video_metadata(const lpb::random_access_reader&,
    target_video_diagnostic&, target_covertime_observation* = nullptr) noexcept;
// Generic reader/profile remain independent validators. No full-file snapshot.
// Does not reject preservation_critical: callers decide the supported HDR policy.
bool observe_target_video(const lpb::random_access_reader&, bool require_ftyp,
    target_video_observation&, target_video_diagnostic&, target_covertime_observation* = nullptr) noexcept;
}
