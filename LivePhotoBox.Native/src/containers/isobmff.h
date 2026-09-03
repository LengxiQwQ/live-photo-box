#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

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

bool is_type(const std::vector<uint8_t>& data, size_t offset, const char* type) noexcept;

size_t find_child_box(
    const std::vector<uint8_t>& data,
    size_t start,
    size_t end,
    const char* type) noexcept;

size_t find_top_level_box(const std::vector<uint8_t>& data, const char* type) noexcept;

void adjust_trak_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t trak_start,
    size_t trak_end,
    size_t threshold,
    size_t removed_bytes) noexcept;

void adjust_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    size_t removed_bytes) noexcept;

bool shift_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    int64_t delta) noexcept;
