#pragma once

#include "foundation/internal.h"

namespace lpb::media {

lpb_result extract_source_with_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    const char* primary_path,
    const char* secondary_path,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const char* cleanup_source_path = nullptr,
    const lpb_extraction_output* auxiliary_outputs = nullptr,
    size_t auxiliary_output_count = 0) noexcept;

lpb_result extract_source_internal(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    const lpb_source_media_facts* facts,
    const char* output_image_path,
    const char* output_video_path,
    const char* output_gainmap_path,
    const lpb_file_identity* expected_primary_identity = nullptr,
    const lpb_file_identity* expected_secondary_identity = nullptr,
    const lpb_extraction_output* auxiliary_outputs = nullptr,
    size_t auxiliary_output_count = 0,
    const char* cleanup_source_path = nullptr,
    std::vector<lpb_published_artifact_record>* out_published_artifacts = nullptr) noexcept;

lpb_result rollback_extraction_outputs_with_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    uint64_t generation) noexcept;

lpb_result verify_extraction_outputs_with_plan(
    lpb_context* context,
    lpb_extraction_plan* plan,
    uint64_t generation) noexcept;

} // namespace lpb::media
