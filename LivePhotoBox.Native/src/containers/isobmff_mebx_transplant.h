#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace lpb::media {

struct isobmff_mebx_track_facts {
    uint32_t track_id{};
    uint32_t referenced_video_track_id{};
    uint32_t time_scale{};
    uint64_t media_duration{};
    uint32_t sample_count{};
    uint32_t chunk_count{};
    uint64_t payload_bytes{};
};

// Returns true for a well-formed file with either zero or one supported
// metadata track. A present track must use the unique `meta` handler / `mebx`
// sample-entry relationship and a single `cdsc` reference to its video track.
// Multiple, malformed, or unsupported mebx tracks fail closed.
bool inspect_isobmff_mebx_track(
    std::span<const uint8_t> data,
    bool& present,
    isobmff_mebx_track_facts& facts) noexcept;

// Add the exact source MEBX track to a newly encoded MP4 Stage A output. Sample
// payload bytes and media-time tables are copied verbatim; chunk offsets and a
// single cdsc video-track reference are relocated, and track/edit-list movie
// durations are rebased to Stage A's movie timescale. Stage B is re-read and
// its MEBX samples are compared with the source.
bool transplant_isobmff_mebx_track(
    std::span<const uint8_t> source,
    std::span<const uint8_t> stage_a,
    std::vector<uint8_t>& stage_b,
    isobmff_mebx_track_facts& facts) noexcept;

// Verify a materialized Stage B against the exact source track, including its
// track-local metadata/timing, source-to-output video reference, chunk map,
// and byte-for-byte sample payloads.
bool validate_isobmff_mebx_transplant(
    std::span<const uint8_t> source,
    std::span<const uint8_t> output,
    const isobmff_mebx_track_facts& expected) noexcept;

} // namespace lpb::media
