#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>
namespace lpb::media {
enum class xmp_observation_failure { none,malformed,ambiguous,unsupported };
struct target_xmp_item {
 std::string semantic,mime;
 uint64_t length{},padding{};
 bool has_length{},has_padding{};
};
struct target_xmp_observation {
 bool live_claim{},foreign_claim{},cover_present{};
 uint64_t motion_photo{},motion_version{};
 int64_t cover_us{};
 std::vector<target_xmp_item> items;
 size_t rdf_close_offset{std::string::npos};
};
bool observe_target_xmp(std::string_view,target_xmp_observation&,xmp_observation_failure&) noexcept;
}
