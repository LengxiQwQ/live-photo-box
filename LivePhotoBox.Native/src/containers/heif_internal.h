#pragma once

#include "livephotobox_native.h"

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace lpb::containers {

// Internal parser result only; diagnostic text never determines inspection status.
// Keep this out of the public C ABI and successful item-facts layout.
enum class heif_graph_error {
    none,
    malformed,
    duplicate_identity,
    missing_reference,
    reference_cycle,
    unsupported_layout,
    capacity_exceeded,
    internal_failure
};

struct heif_graph_diagnostic {
    heif_graph_error code{heif_graph_error::none};
    std::string message;
};

lpb_inspection_failure_category inspection_category(heif_graph_error error) noexcept;

bool enumerate_auxiliary_items(std::span<const uint8_t> input,
    std::vector<lpb_auxiliary_item_facts>& items, heif_graph_diagnostic& diagnostic) noexcept;

bool attach_gainmap_auxiliary_graph(std::span<const uint8_t> input,
    uint32_t primary_item_id, uint32_t gainmap_item_id,
    std::vector<uint8_t>& output, std::string& error) noexcept;

bool enumerate_xmp_ranges_describing_item(std::span<const uint8_t> input,
    uint32_t described_item_id, std::vector<lpb_media_range>& ranges,
    std::string& error) noexcept;

// Project-owned item-graph XMP transport. XML semantics and ordinary-field merge
// belong to the caller; the replacement is the complete merged primary packet.
// Success preserves every non-selected item payload and its item identity.
// input and output must use separate storage. No public C ABI is added.
bool read_primary_xmp(std::span<const uint8_t> input, uint32_t& primary_item_id,
    std::string& xml, std::string& error) noexcept;
// Owner-specific ordinary metadata read. Legitimate metadata on a different
// image is not a shadow of this owner; duplicate/unowned/shared metadata fails.
// The older public Apple single-Exif locator retains its strict ABI semantics.
bool read_image_item_metadata(std::span<const uint8_t>,uint32_t image_item_id,
    std::vector<uint8_t>& exif_payload,std::string& xmp,std::string& error) noexcept;

inline constexpr std::string_view original_jpeg_resource_uri="urn:livephotobox:asset:original-jpeg:1";
inline constexpr std::string_view original_jpeg_resource_name="LivePhotoBox Original JPEG File";
struct heif_original_file_resource {
    bool present{};
    uint32_t item_id{},owner_item_id{};
    lpb_media_range range{};
};
// File-resource structure only. Callers must additionally validate the complete
// payload with the existing Original JPEG observer before granting image-file
// semantics. This is separate from the public coded auxiliary inventory.
bool observe_original_file_resource(std::span<const uint8_t>,heif_original_file_resource&,std::string&) noexcept;
// Pure in-memory graph transport. The caller must first bind the full Original
// JPEG file identity and validate its payload; no publish/file authority here.
bool append_original_file_resource(std::span<const uint8_t> input,std::span<const uint8_t> original,
    std::vector<uint8_t>& output,uint32_t& item_id,std::string& error) noexcept;

bool rewrite_primary_xmp(std::span<const uint8_t> input, std::string_view replacement_xml,
    std::vector<uint8_t>& output, std::string& error) noexcept;

struct heif_primary_properties {
    uint32_t item_id{}, width{}, height{}, orientation{};
    bool has_clean_aperture{};
    bool stored_geometry_compatible{};
    lpb_media_range ordinary_sef_range{}; // Validated final top-level sefd body, never an EOF magic match.
    std::string item_type;
    std::vector<lpb_media_range> encoding_extents;
    std::vector<uint8_t> exif_payload; // JPEG-compatible Exif\0\0 + TIFF bytes
};
bool observe_primary_item_properties(std::span<const uint8_t> input,
    heif_primary_properties& output, std::string& error) noexcept;
  // Internal read-only item geometry. Non-primary items do not inherit the
  // primary Exif owner; callers separately prove their auxiliary relationship.
  bool observe_item_properties(std::span<const uint8_t> input, uint32_t item_id,
      heif_primary_properties& output, std::string& error) noexcept;

} // namespace lpb::containers
