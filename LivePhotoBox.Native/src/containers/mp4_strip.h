#pragma once
#include <cstdint>
#include <cstddef>
#include <string>
#include "livephotobox_native.h"

namespace lpb::containers {

struct Mp4StripSpec {
    const uint8_t* strip_uuid_16 = nullptr;
    const char* const* mdta_starts = nullptr;
    size_t mdta_starts_count = 0;
    const char* const* mdta_contains = nullptr;
    size_t mdta_contains_count = 0;
    const char* const* track_patterns = nullptr;
    size_t track_patterns_count = 0;
};

struct Mp4StripOutcome {
    bool uuid_removed = false;
    bool mdta_removed = false;
    bool track_removed = false;
};

lpb_result stream_clean_mp4_file(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    const Mp4StripSpec& spec,
    Mp4StripOutcome& outcome);

} // namespace lpb::containers
