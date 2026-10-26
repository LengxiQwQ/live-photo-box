#pragma once
#include "livephotobox_native.h"
#include "media/jpeg_observation.h"
#include "media/xmp_observation.h"
#include <span>
#include <string>
#include <vector>

namespace lpb::media {
struct target_primary_auxiliary {
    std::string selector, owner_selector, semantic, mime, relationship, item_type;
    uint32_t selector_kind{}, selector_id{}, item_id{}; // 1=Directory ordinal, 2=HEIF item ID
    lpb_media_range range{};
    std::vector<uint32_t> dependency_ids;
    std::vector<lpb_media_range> dependency_ranges;
};
struct target_primary_observation {
    lpb_image_container container{LPB_IMAGE_CONTAINER_UNKNOWN};
    uint32_t width{}, height{}, orientation{}, primary_item_id{};
    uint64_t encoding_boundary{}, primary_size{}, video_offset{}, video_length{}, video_header_size{};
    std::string primary_selector, item_type, standard_xml, extended_xml, extended_guid;
    std::vector<lpb_media_range> primary_extents;
    std::vector<uint32_t> primary_dependency_ids;
    std::vector<lpb_media_range> primary_dependency_ranges;
    std::vector<uint8_t> exif_payload;
    jpeg_observation jpeg;
    target_xmp_observation xmp;
    std::vector<target_primary_auxiliary> auxiliaries;
    bool singleton_mpf{}, singleton_size_matches{}, has_video{};
};
// Factual reader, independent of writer state. JPEG bytes must cover the full
// prepared primary including tightly packed declared auxiliary JPEGs. HEIF may
// additionally carry exactly one final mpvd (8/16-byte header), when requested.
bool observe_target_primary(std::span<const uint8_t> input,
    target_primary_observation& output, std::string& error,
    bool allow_final_heif_video = false, uint64_t full_file_length = 0) noexcept;
// Streaming output observer: prefix contains every primary/auxiliary byte and
// ends exactly where the actual Directory's sole final MotionPhoto begins.
// The separate full length is obtained from the pinned output reader.
bool observe_target_jpeg_prefix(std::span<const uint8_t> prefix,
    uint64_t full_file_length, target_primary_observation& output,
    std::string& error) noexcept;
// The caller must also apply SourceInspector eligibility. This function tests
// properties/semantic items, never harmless namespace declarations or HDR alone.
bool target_primary_has_live_binding(const target_primary_observation&) noexcept;
// An Original is an immutable image asset, not a candidate main target owner.
// Its historical XMP declarations remain opaque; any actual nested GainMap
// bytes still require a complete bounded JPEG/MPF/directory relationship.
bool observe_original_jpeg_asset(std::span<const uint8_t> input,std::string& error) noexcept;
bool observe_gainmap_jpeg_asset(std::span<const uint8_t> input,std::string& error) noexcept;
} // namespace lpb::media
