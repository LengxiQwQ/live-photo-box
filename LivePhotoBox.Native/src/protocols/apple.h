#pragma once

#include "livephotobox_native.h"
#include <cstdint>
#include <cstddef>
#include <string>
#include <vector>

namespace lpb::protocols::apple {

bool apple_makernote_has_tag(const uint8_t* data, size_t start, size_t end, uint16_t target_tag);

bool apple_makernote_get_tag_fingerprint(
    const uint8_t* data, size_t start, size_t end, uint16_t target_tag, std::string& out_fp);

bool apple_image_has_tag(
    lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, uint16_t tag);

bool apple_image_get_tag_fingerprint(
    lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, uint16_t tag, std::string& out_fp);

} // namespace lpb::protocols::apple
