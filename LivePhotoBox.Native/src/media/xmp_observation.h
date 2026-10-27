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
 bool micro_claim{},directory_present{},micro_cover_present{};
 bool hdr_claim{},oppo_claim{},vivo_claim{};
 bool oppo_version_present{},oppo_length_present{},primary_cover_present{},edit_flag_present{};
 uint64_t oppo_version{},oppo_video_length{};
 int64_t primary_cover_us{};
 std::string oppo_owner,edit_flag;
 uint64_t motion_photo{},motion_version{};
 uint64_t micro_video{},micro_version{},micro_offset{};
 int64_t cover_us{};
 int64_t micro_cover_us{};
 std::vector<target_xmp_item> items;
 size_t rdf_close_offset{std::string::npos};
 size_t rdf_content_offset{},owner_close_offset{std::string::npos},sequence_close_offset{std::string::npos};
 std::string rdf_name;
 std::string micamera_owner_name;
 size_t micamera_owner_content_offset{},micamera_owner_close_offset{std::string::npos};
 bool extended_reference_present{};
 std::string extended_reference;
 std::vector<std::string> property_inventory;
};
bool observe_target_xmp(std::string_view,target_xmp_observation&,xmp_observation_failure&) noexcept;
// Changes only the uniquely owned Primary Padding property using parsed XML
// ranges. All other bytes, including unknown item attributes, are retained.
bool rewrite_target_primary_padding(std::string_view,uint64_t,std::string&,xmp_observation_failure&,bool generated_target=false) noexcept;
// Extended transport GUID/coverage is established by the JPEG observer. This
// layer checks the logical XML reference and cross-packet property ownership.
bool observe_target_xmp_packets(std::string_view standard,std::string_view extension,
 std::string_view guid,target_xmp_observation&,xmp_observation_failure&) noexcept;
}
