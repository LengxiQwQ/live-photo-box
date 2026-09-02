#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

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

void shift_chunk_offsets(
    std::vector<uint8_t>& data,
    size_t moov_start,
    size_t threshold,
    int64_t delta) noexcept;
