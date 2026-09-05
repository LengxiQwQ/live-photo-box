#pragma once

#include "livephotobox_native.h"
#include <string>
#include <vector>

namespace lpb::protocols::clean {

lpb_result clean_samsung_heic(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    const lpb_cleanup_action* actions,
    size_t action_count,
    std::vector<lpb_removed_protocol_fact>& out_facts);

} // namespace lpb::protocols::clean
