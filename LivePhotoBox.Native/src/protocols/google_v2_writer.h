#pragma once
#include "livephotobox_native.h"
#include "media/xmp_observation.h"
#include <span>
#include <vector>
#include <string>
#include <string_view>
namespace lpb::target {
struct google_xmp_request {
 uint32_t target{2},format{};
 uint64_t video_length{};
 bool cover_present{};
 int64_t cover_us{};
 bool primary_cover_present{};
 int64_t primary_cover_us{};
 std::string primary_mime{"image/jpeg"};
 uint64_t primary_padding{};
 // Validated Neutral assets, in final byte order; excludes Primary/MotionPhoto.
 std::vector<lpb::media::target_xmp_item> auxiliary_items;
 bool hdr_relationship_validated{};
};
lpb_result build_google_xmp(std::string_view,const google_xmp_request&,std::string&) noexcept;
lpb_result build_google_primary(lpb_context*,std::span<const uint8_t>,const google_xmp_request&,std::vector<uint8_t>&,
 lpb::media::user_comment_mapping* mapping=nullptr) noexcept;
lpb_result build_google_v2_primary(lpb_context*,std::span<const uint8_t>,uint64_t video_length,
 uint32_t format,bool cover_present,int64_t cover,std::vector<uint8_t>&,uint32_t target=2) noexcept;
}
