#pragma once

#include "livephotobox_native.h"
#include <string>
#include <vector>
#include <span>

namespace lpb::media {

lpb_result inspect_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    std::vector<lpb_confirmed_residue>* out_residues = nullptr) noexcept;

lpb_image_container detect_image_container(std::span<const uint8_t> header) noexcept;
lpb_video_container detect_video_container(std::span<const uint8_t> header) noexcept;

} // namespace lpb::media
