#pragma once
#include "binary/portable_io.h"
#include <string>
#include <vector>
namespace lpb::media {
enum class jpeg_observation_failure { none,malformed,unsupported,io };
// Defaults retain the R2 restricted input contract. Extended XMP is transport
// only here: callers must inventory both XML packets and verify the unique
// namespace-resolved HasExtendedXMP reference before accepting target semantics.
struct jpeg_observation_options { bool allow_opaque_exif{},allow_extended_xmp{},allow_mpf{},allow_unknown_orientation{}; };
struct exif_observation {
 bool big_endian{},opaque_metadata{},has_user_comment{};
 uint32_t ifd0_offset{},exif_ifd_offset{};
 size_t exif_pointer_entry{},user_comment_entry{},user_comment_offset{};
 std::vector<uint8_t> user_comment;
};
struct extended_xmp_chunk { uint64_t marker_start{},payload_start{}; uint32_t offset{},length{}; };
struct jpeg_observation {
 uint64_t eoi_end{},xmp_offset{};
 uint32_t width{},height{},orientation{},standard_packets{};
 bool extended{},mpf{},saw_scan{},metadata_after_scan{};
 bool singleton_mpf{},mpf_index{};
 uint32_t mpf_primary_size{};
 std::string xmp;
 std::string extended_xmp,extended_guid;
 std::vector<extended_xmp_chunk> extended_chunks;
 std::vector<uint8_t> exif_payload;
 exif_observation exif;
 uint64_t exif_marker_start{},exif_segment_end{};
};
bool observe_jpeg(const lpb::random_access_reader&,jpeg_observation&,jpeg_observation_failure&,uint64_t&,jpeg_observation_options = {}) noexcept;
bool observe_plain_exif(std::span<const uint8_t>,uint32_t&,jpeg_observation_failure&) noexcept;
bool observe_exif(std::span<const uint8_t>,uint32_t&,exif_observation&,jpeg_observation_failure&,bool allow_opaque,bool allow_unknown_orientation=false) noexcept;
}
