#pragma once
#include "binary/portable_io.h"
#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>
namespace lpb::media {
struct isobmff_metadata_key {
 uint32_t description_index{},key_id{};
 std::string name_space,name;
 size_t keyd_offset{},keyd_length{};
};
struct isobmff_metadata_key_track {
 uint32_t track_id{},description_count{};
 size_t offset{},length{};
 size_t sample_table_begin{},sample_table_end{};
 size_t sample_table_offset{};
 size_t media_info_begin{},media_info_end{};
 uint16_t data_reference_index{};
 bool all_descriptions_mebx{};
 bool whole_track_owned_shape{true};
 std::vector<isobmff_metadata_key> keys;
};
// Read-only declaration inventory. Accepts a complete file or an isolated moov.
// Only actual meta/stsd/mebx/keys/numeric-item/keyd hierarchy owns a key. This
// does not validate sample payload/timing or grant track-removal authority.
bool inspect_isobmff_metadata_keys(std::span<const uint8_t>,
 std::vector<isobmff_metadata_key_track>&) noexcept;
bool has_isobmff_metadata_key(std::span<const isobmff_metadata_key_track>,
 std::string_view name_space,std::string_view name) noexcept;
bool is_source_live_metadata_key(std::string_view name_space,std::string_view name) noexcept;
enum class metadata_sample_scope { keyed_atoms, single_track_value };
enum class metadata_validation_purpose { whole_track_removal, read_only_media };
struct metadata_sample_diagnostic {bool io{};uint64_t offset{UINT64_MAX};};
metadata_sample_scope declared_metadata_sample_scope(std::string_view name_space,std::string_view name) noexcept;
// Separate complete-value check for a single MEBX description. Keyed samples
// must reference declared keys. A source protocol may instead define each whole
// packet as the sole track key's opaque value; the caller must establish that
// semantic and complete cleanup authority before selecting single_track_value.
// Both forms require the same strict local-reference/table/extent checks.
bool validate_isobmff_metadata_key_samples(std::span<const uint8_t> metadata,
 const isobmff_metadata_key_track&,std::span<const uint8_t> source,
 metadata_sample_scope scope=metadata_sample_scope::keyed_atoms,
 metadata_validation_purpose purpose=metadata_validation_purpose::whole_track_removal) noexcept;
bool validate_isobmff_metadata_key_samples_reader(std::span<const uint8_t> metadata,
 const isobmff_metadata_key_track&,const lpb::random_access_reader& source,
 metadata_sample_scope scope,metadata_validation_purpose purpose,
 metadata_sample_diagnostic* diagnostic=nullptr) noexcept;
}
