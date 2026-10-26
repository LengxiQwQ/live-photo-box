#pragma once
#include "binary/portable_io.h"
#include <string>
#include <vector>
namespace lpb::media {
enum class jpeg_observation_failure { none,malformed,unsupported,io };
struct jpeg_observation {
 uint64_t eoi_end{},xmp_offset{};
 uint32_t width{},height{},orientation{},standard_packets{};
 bool extended{},mpf{},saw_scan{},metadata_after_scan{};
 std::string xmp;
};
bool observe_jpeg(const lpb::random_access_reader&,jpeg_observation&,jpeg_observation_failure&,uint64_t&) noexcept;
bool observe_plain_exif(std::span<const uint8_t>,uint32_t&,jpeg_observation_failure&) noexcept;
}
