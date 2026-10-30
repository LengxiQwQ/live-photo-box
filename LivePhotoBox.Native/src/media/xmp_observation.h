#pragma once
#include "media/preserved_exif.h"
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
 preserved_user_comment preserved_comment;
 bool live_claim{},foreign_claim{},cover_present{};
 bool micro_claim{},directory_present{},micro_cover_present{};
 bool hdr_claim{},oppo_claim{},vivo_claim{};
 bool oppo_version_present{},oppo_length_present{},primary_cover_present{},edit_flag_present{};
 uint64_t oppo_version{},oppo_video_length{};
 bool vivo_version_present{},vivo_source_present{},vivo_mediakit_present{},vivo_flags_present{};
 uint64_t vivo_version{},vivo_source{};
 std::string vivo_mediakit;
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
bool rewrite_target_vivo_primary_shape(std::string_view,std::string&,xmp_observation_failure&) noexcept;
bool remove_target_container_directory(std::string_view,std::string&,xmp_observation_failure&) noexcept;
// Bound image converter only: map an observed HEIC Primary+embedded GainMap
// directory to the actual JPEG lengths. Ordinary XML bytes remain untouched.
bool rewrite_heif_gainmap_directory_to_jpeg(std::string_view,uint64_t,uint64_t,std::string&,xmp_observation_failure&) noexcept;
// Target Writer only, after its real HEIF auxC/auxl inventory is bound. Remove
// one fully known redundant zero-length embedded GainMap layout entry; retain
// every other XML byte and every actual image item. No coordinate mapping.
bool reduce_embedded_heif_gainmap_layout(std::string_view,std::string&,xmp_observation_failure&) noexcept;
// Read-only conflict check for an explicit primary stored-pixel choice. Other
// metadata and every HDR/auxiliary layout byte remain untouched.
bool validate_stored_pixel_xmp_direction(std::string_view) noexcept;
// Ordinary bound-image conversion only. Adjust the uniquely owned primary
// container MIME and known primary orientation/dimension scalar values, keeping
// all other XML bytes. No live/auxiliary/HDR layout is admitted by this seam.
bool rewrite_neutral_image_xmp(std::string_view,std::string_view source_mime,std::string_view target_mime,
 uint32_t source_orientation,uint32_t source_width,uint32_t source_height,
 std::string&,xmp_observation_failure&) noexcept;
// Bound HDR converter only. A validated JPEG GainMap layout entry moves to an
// actual HEIF auxl/auxC owner; unknown item content cannot be erased by this seam.
bool rewrite_hdr_gainmap_owner_xmp(std::string_view,uint32_t orientation,uint32_t width,uint32_t height,
    std::string&,xmp_observation_failure&) noexcept;
bool rewrite_jpeg_gainmap_primary_xmp_to_heif(std::string_view,uint64_t source_gainmap_length,uint64_t source_primary_padding,
 uint32_t source_orientation,uint32_t source_width,uint32_t source_height,
 std::string&,xmp_observation_failure&) noexcept;
// Extended transport GUID/coverage is established by the JPEG observer. This
// layer checks the logical XML reference and cross-packet property ownership.
bool observe_target_xmp_packets(std::string_view standard,std::string_view extension,
 std::string_view guid,target_xmp_observation&,xmp_observation_failure&) noexcept;
}
