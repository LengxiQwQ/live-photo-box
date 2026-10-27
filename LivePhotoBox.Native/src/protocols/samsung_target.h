#pragma once
#include "media/target_primary_observation.h"
#include "protocols/google_v2_writer.h"
namespace lpb::target {
bool prepare_samsung_target(lpb_context*,std::span<const uint8_t>,const media::target_primary_observation&,
 uint64_t movie_length,uint32_t version,bool cover_present,int64_t cover,
 std::vector<uint8_t>& prefix,std::vector<uint8_t>& suffix,std::string& error) noexcept;
}
