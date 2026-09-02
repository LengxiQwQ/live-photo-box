#pragma once

#include "livephotobox_native.h"
#include <string>
#include <vector>

namespace lpb::protocols::clean {

lpb_result strip_jpeg_tail_data(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    const char* proto,
    const char* comp,
    const char* desc,
    std::vector<lpb_removed_protocol_fact>& out_facts);

lpb_result clean_huawei_image(
    lpb_context* context,
    const std::string& input_path,
    const std::string& output_path,
    lpb_image_container container,
    std::vector<lpb_removed_protocol_fact>& out_facts);

} // namespace lpb::protocols::clean
