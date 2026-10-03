#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>
#include "binary/portable_io.h"

struct isobmff_media_data_range {
    uint64_t start{};
    uint64_t end{};
};

struct isobmff_sample_chunk {
    uint64_t offset{};
    uint64_t size{};
    uint32_t first_sample{};
    uint32_t sample_count{};
};

struct isobmff_sample_table_layout {
    size_t chunk_offsets_entries_start{};
    size_t chunk_offset_width{};
    uint32_t sample_count{};
    uint32_t sample_description_count{};
    std::vector<isobmff_sample_chunk> chunks;
};

struct top_level_box {
    uint64_t offset{};
    uint64_t size{};
    char type[4]{};
    uint32_t header_size{};
};

std::vector<top_level_box> scan_top_level_boxes(
    const lpb::random_access_reader& source) noexcept;

struct isobmff_box_header
{
    size_t start{};
    size_t size{};
    size_t header_size{};
    uint32_t size32{};
    bool extends_to_end{};
};

bool try_read_box_header(
    const uint8_t* data,
    size_t start,
    size_t end,
    isobmff_box_header& out) noexcept;

bool is_valid_isobmff_media_range(
    const uint8_t* data,
    size_t data_size,
    uint64_t offset,
    uint64_t length) noexcept;

// MOV permits a QuickTime top-level layout without an ftyp box.  Keep the
// stricter MP4 validator above (Google/vendor payloads require ftyp), while
// exposing the same hierarchy/sample-table validation for QuickTime sources.
bool is_valid_mov_media_range(
    const uint8_t* data,
    size_t data_size,
    uint64_t offset,
    uint64_t length) noexcept;

// Parse one stbl using the same bounded table and mdat-range checks used by
// the Native media-range validator. Chunk entries are returned in sample
// order, while malformed, overlapping, out-of-range, or ambiguous offset
// coordinate systems fail closed.
bool inspect_isobmff_sample_table(
    std::span<const uint8_t> data,
    size_t stbl_start,
    size_t stbl_end,
    std::span<const isobmff_media_data_range> mdats,
    uint64_t coordinate_origin,
    isobmff_sample_table_layout& output) noexcept;

// Private Native conversion facts. Entries are in presentation order, with
// source timing retained in mdhd ticks and the normalized schedule expressed
// in Media Foundation's 100-nanosecond units. Hidden edit-list preroll remains
// represented so the decoder pump can consume one entry per decoded sample.
struct isobmff_video_presentation_sample {
    uint32_t sample_index{};
    int64_t presentation_time_ticks{};
    uint64_t presentation_duration_ticks{};
    bool visible{};
    int64_t normalized_time_100ns{};
    int64_t duration_100ns{};
};

struct isobmff_video_presentation_timeline {
    uint32_t movie_timescale{};
    uint32_t media_timescale{};
    uint32_t sample_count{};
    uint32_t visible_sample_count{};
    int64_t media_edit_start_ticks{};
    uint64_t leading_empty_duration_ticks{};
    std::vector<isobmff_video_presentation_sample> presentation_order;
};

// Parse one unambiguous video track's mdhd/stts/ctts/edit-list schedule.
// Unsupported edits, timing ambiguity, malformed sample coverage, and checked
// arithmetic overflow fail closed.
bool inspect_isobmff_video_presentation_timeline(
    std::span<const uint8_t> data,
    isobmff_video_presentation_timeline& output) noexcept;

bool is_type(std::span<const uint8_t> data, size_t offset, const char* type) noexcept;

size_t find_child_box(
    std::span<const uint8_t> data,
    size_t start,
    size_t end,
    const char* type) noexcept;

size_t find_top_level_box(std::span<const uint8_t> data, const char* type) noexcept;

bool adjust_trak_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t trak_start,
    size_t trak_end,
    size_t threshold,
    size_t removed_bytes) noexcept;

bool adjust_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    size_t removed_bytes) noexcept;

bool shift_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    int64_t delta) noexcept;
