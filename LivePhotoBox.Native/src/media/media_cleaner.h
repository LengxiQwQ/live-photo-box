#pragma once

#include "livephotobox_native.h"
#include <string_view>

namespace lpb::media {

const lpb_cleanup_action* find_authorized_action(
    const lpb_cleanup_action* actions,
    size_t action_count,
    lpb_source_protocol expected_protocol,
    std::string_view residue_id,
    lpb_media_artifact_kind expected_role,
    lpb_residue_structure_kind expected_kind,
    std::string_view expected_selector = {},
    std::string_view expected_semantic = {},
    int32_t expected_removal_mode = -1,
    int32_t expected_coordinate_space = LPB_COORD_STRUCTURED_SELECTOR);

lpb_result clean_source_protocol_with_plan(
    lpb_context* context,
    const lpb_source_media_facts* facts,
    const lpb_cleanup_action* actions,
    size_t action_count,
    const lpb_cleanup_artifact_binding* targets,
    size_t target_count,
    const char* input_image_path,
    const char* input_video_path,
    const char* output_image_path,
    const char* output_video_path,
    lpb_removed_protocol_fact* out_facts,
    size_t facts_capacity,
    size_t* out_facts_count);
} // namespace lpb::media
