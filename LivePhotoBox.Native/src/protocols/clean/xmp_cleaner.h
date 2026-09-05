#pragma once

#include "livephotobox_native.h"
#include <string>
#include <vector>

namespace lpb::protocols::clean {

bool clean_xmp_metadata_with_plan(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::string& output_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts);

bool clean_xmp_metadata(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    std::string& output_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts);

} // namespace lpb::protocols::clean
