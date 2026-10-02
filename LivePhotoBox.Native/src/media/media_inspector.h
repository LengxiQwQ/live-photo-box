#pragma once

#include "livephotobox_native.h"
#include "media/gainmap_math.h"
#include <string>
#include <vector>
#include <span>

namespace lpb::media {

enum class gainmap_metadata_kind : int32_t { unknown = 0, apple = 1, iso = 2 };

struct gainmap_metadata_facts {
    gainmap_metadata_kind kind{gainmap_metadata_kind::unknown};
    iso_gainmap_metadata iso{};
    double apple_maker_note_33{};
    double apple_maker_note_48{};
};

lpb_result inspect_gainmap_metadata(
    lpb_context* context,
    const char* primary_path,
    const char* materialized_gainmap_path,
    gainmap_metadata_facts& out_metadata,
    bool share_existing_writer = false) noexcept;

lpb_result inspect_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    std::vector<lpb_confirmed_residue>* out_residues = nullptr,
    bool share_existing_writer = false) noexcept;

lpb_result inspect_source_with_plan(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts,
    lpb_extraction_plan** out_plan,
    std::vector<lpb_confirmed_residue>* out_residues = nullptr,
    uint64_t* out_generation = nullptr) noexcept;

lpb_image_container detect_image_container(std::span<const uint8_t> header) noexcept;
lpb_video_container detect_video_container(std::span<const uint8_t> header) noexcept;

} // namespace lpb::media
