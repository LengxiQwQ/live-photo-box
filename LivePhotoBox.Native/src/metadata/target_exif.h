#pragma once
#include "media/jpeg_observation.h"
#include "media/preserved_exif.h"
#include <span>
#include <vector>
namespace lpb::metadata {
// Internal target-serializer primitive. Caller supplies the policy-approved
// complete UserComment bytes. Existing comments conflict; none are overwritten.
// Appends copied TIFF directories, retaining original TIFF-relative payloads.
// Caller owns MPF relocation and target identity/transaction/publication gates.
bool add_target_user_comment(std::span<const uint8_t> jpeg,std::span<const uint8_t> comment,
 std::vector<uint8_t>& output,media::jpeg_observation_failure& failure) noexcept;
// Private new-target serializer only. The caller has already embedded and
// verified the mapping record. The mutation site independently matches the
// complete expected formal tuple, never a filename or an arbitrary TIFF offset.
bool map_target_user_comment(std::span<const uint8_t> jpeg,std::span<const uint8_t> comment,
 const media::preserved_user_comment& expected,std::vector<uint8_t>& output,
 media::jpeg_observation_failure& failure) noexcept;
}
