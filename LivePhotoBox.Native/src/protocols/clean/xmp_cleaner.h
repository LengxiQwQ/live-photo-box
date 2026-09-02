#pragma once

#include "livephotobox_native.h"
#include <string>
#include <vector>

namespace lpb::protocols::clean {

bool clean_xmp_metadata(
    const std::string& input_xmp,
    lpb_source_protocol protocol,
    std::string& output_xmp,
    std::vector<lpb_removed_protocol_fact>& out_facts);

} // namespace lpb::protocols::clean
