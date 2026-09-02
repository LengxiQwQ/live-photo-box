#pragma once

#include "livephotobox_native.h"
#include <string>
#include <vector>

namespace lpb::protocols::clean {

lpb_result clean_samsung_sef_jpeg(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    std::vector<lpb_removed_protocol_fact>& out_facts);

} // namespace lpb::protocols::clean
