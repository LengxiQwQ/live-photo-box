#pragma once

#include "livephotobox_native.h"
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace lpb::protocols::clean {

struct neutral_container_item {
    std::string semantic;
    std::string mime;
    uint64_t length{};
    bool has_length{};
    size_t ordinal{};
};

struct neutral_gainmap_directory {
    size_t source_item_count{};
    size_t gainmap_ordinal{};
    std::vector<neutral_container_item> source_items;
};

// Validate the existing XMP Container Directory as Primary + GainMap plus
// zero or more explicitly described non-motion items, then remove only those
// extra rdf:li membership declarations from the final owner. The returned
// source_items describe the pre-canonicalized order for correlating an MPF
// index; unrelated packet bytes remain untouched.
bool canonicalize_neutral_gainmap_directory(
    const std::string& input_xmp,
    uint64_t expected_gainmap_length,
    std::string& output_xmp,
    neutral_gainmap_directory& out_directory);

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
