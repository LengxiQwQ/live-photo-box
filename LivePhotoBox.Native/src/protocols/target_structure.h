#pragma once
#include <cstdint>
namespace lpb::target {
bool profile_accepts(uint32_t target, uint32_t format) noexcept;
bool contained_range(uint64_t size, uint64_t offset, uint64_t length) noexcept;
}
