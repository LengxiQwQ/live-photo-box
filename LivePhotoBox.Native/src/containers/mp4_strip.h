#pragma once
#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>
#include <span>
#include <string_view>
#include "livephotobox_native.h"

namespace lpb::containers {

struct Mp4StripSpec {
    lpb_source_protocol expected_protocol = LPB_SOURCE_PROTOCOL_UNKNOWN;
    const uint8_t* strip_uuid_16 = nullptr;
    const char* const* mdta_starts = nullptr;
    size_t mdta_starts_count = 0;
    const char* const* mdta_contains = nullptr;
    size_t mdta_contains_count = 0;
    const char* const* track_patterns = nullptr;
    size_t track_patterns_count = 0;
    const lpb_cleanup_action* actions = nullptr;
    size_t action_count = 0;
};

struct Mp4StripOutcome {
    bool uuid_removed = false;
    bool mdta_removed = false;
    bool track_removed = false;
    std::vector<bool> mdta_starts_matched;
    std::vector<bool> mdta_contains_matched;
    std::vector<bool> track_patterns_matched;
    std::vector<std::string> mdta_starts_fingerprints;
    std::vector<std::string> mdta_contains_fingerprints;
    std::vector<std::string> track_fingerprints;
    std::string uuid_fingerprint;
};

bool mp4_get_mdta_key_fingerprint(
    std::span<const uint8_t> data,
    std::string_view target_key,
    std::string& out_fp);

lpb_result stream_clean_mp4_bytes(
    lpb_context* context,
    std::span<const uint8_t> in_bytes,
    const std::string& out_path,
    const Mp4StripSpec& spec,
    Mp4StripOutcome& outcome);

lpb_result stream_clean_mp4_file(
    lpb_context* context,
    const std::string& in_path,
    const std::string& out_path,
    const Mp4StripSpec& spec,
    Mp4StripOutcome& outcome);

} // namespace lpb::containers
