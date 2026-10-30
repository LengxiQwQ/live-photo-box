#pragma once

#include <cstdint>
#include <span>
#include <vector>
#include "containers/isobmff.h"

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

// Multi-track extension of the same strict graph and byte-preservation rules.
// Every mebx track must reference the unique video. IDs remain unique and
// ordered; Stage A must contain no deferred metadata track.
bool inspect_isobmff_mebx_tracks(std::span<const uint8_t> data,
    std::vector<isobmff_mebx_track_facts>& facts) noexcept;
bool transplant_isobmff_mebx_tracks(std::span<const uint8_t> source,
    std::span<const uint8_t> stage_a, std::vector<uint8_t>& stage_b,
    std::vector<isobmff_mebx_track_facts>& facts) noexcept;
bool validate_isobmff_mebx_transplants(std::span<const uint8_t> source,
    std::span<const uint8_t> output,
    std::span<const isobmff_mebx_track_facts> expected) noexcept;

// Preserve scoped UUID assets and offset-free QuickTime/ISO metadata at
// movie, video-track and unique audio-track scope.
// Unknown metadata graphs, duplicate keys/boxes and malformed aperture boxes
// fail closed. Encoded sample tables remain target-owned and are relocated.
bool preserve_isobmff_static_metadata(std::span<const uint8_t> source,
    std::span<const uint8_t> stage, std::vector<uint8_t>& output,
    bool& changed) noexcept;
bool validate_isobmff_static_metadata(std::span<const uint8_t> source,
    std::span<const uint8_t> output) noexcept;
// Same-size, ftyp-directed QuickTime Pascal <-> ISO NUL handler-name mapping.
// Only owned metadata bytes are returned; no mdat is materialized or rewritten.
bool map_isobmff_handler_names(std::span<const uint8_t> source, std::span<const uint8_t> target,
    std::vector<uint8_t>& output, std::span<const isobmff_media_data_range> source_mdats = {},
    std::span<const isobmff_media_data_range> target_mdats = {}) noexcept;

struct isobmff_static_metadata_evidence {
    uint32_t box_count{};
    uint32_t opaque_count{};
    uint32_t audio_count{};
    uint8_t source_sha256[32]{};
    uint8_t output_sha256[32]{};
};
// Exact scoped source inventory versus actual staged output, including top,
// movie, unique video/audio metadata. Digests cover scope + complete box bytes.
// auxiliary_copy permits additional locally validated auxv / opaque mett tracks only for
// original Copy callers. It never extends public v1 digest scopes or counts;
// full additional track/payload transport remains a separate required publication gate.
bool capture_isobmff_static_metadata_evidence(std::span<const uint8_t> source,
    std::span<const uint8_t> output, isobmff_static_metadata_evidence& evidence,
    std::span<const isobmff_media_data_range> source_mdats = {},
    std::span<const isobmff_media_data_range> output_mdats = {},
    const char** rejection_reason = nullptr, bool auxiliary_copy = false) noexcept;

// Streaming Copy proof: validate actual MEBX/auxv/opaque mett tables/references against original
// file mdat ranges, normalize only relocated chunk offsets, compare complete
// tracks and referenced payload with bounded buffers before publication.
bool validate_isobmff_mebx_stream_remux(std::span<const uint8_t> source_metadata,
    std::span<const isobmff_media_data_range> source_mdats, const lpb::random_access_reader& source,
    std::span<const uint8_t> output_metadata, std::span<const isobmff_media_data_range> output_mdats,
    const lpb::random_access_reader& output, bool (*cancelled)(void*), void* state) noexcept;

} // namespace lpb::media
