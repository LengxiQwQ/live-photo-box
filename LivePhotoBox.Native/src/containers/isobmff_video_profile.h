#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>
#include "binary/portable_io.h"

namespace lpb::media {

enum class video_profile_class : uint8_t {
    unknown = 0,
    ordinary_sdr = 1,
    preservation_critical = 2
};

enum class video_profile_failure : uint8_t {
    none = 0,
    malformed = 1,
    ambiguous = 2,
    unsupported = 3,
    io = 4
};

enum class video_profile_diagnostic : uint8_t {
    none = 0,
    source_layout = 1,
    moov_layout = 2,
    track_layout = 3,
    sample_description = 4,
    sample_to_chunk = 5,
    sample_entry = 6,
    codec_configuration = 7,
    avc_sps = 8,
    hevc_sps = 9,
    color_description = 10,
    classification = 11,
    cicp_normalization = 12
};

enum class requested_video_codec : uint8_t {
    unknown = 0,
    copy = 1,
    h264 = 2,
    hevc = 3
};

enum class video_conversion_owner : uint8_t {
    reject = 0,
    project_remux = 1,
    media_foundation_software = 2,
    minimal_libav_sidecar = 3
};

enum class media_foundation_cicp_patch_mode : uint8_t {
    reject = 0,
    unchanged = 1,
    colr_only = 2,
    colr_and_bitstream = 3
};

struct isobmff_video_profile {
    uint32_t codec_fourcc{};
    uint16_t color_primaries{};
    uint16_t transfer_characteristics{};
    uint16_t matrix_coefficients{};
    uint8_t bit_depth_luma{};
    uint8_t bit_depth_chroma{};
    uint8_t full_range{};
    uint8_t full_range_known{};
    uint8_t chroma_format{};
    uint8_t bitstream_color_description_present{};
    uint8_t bitstream_full_range_present{};
    uint8_t bitstream_full_range{};
    uint16_t bitstream_color_primaries{};
    uint16_t bitstream_transfer_characteristics{};
    uint16_t bitstream_matrix_coefficients{};
    video_profile_class classification{video_profile_class::unknown};
};

// A private Native-only replacement for one already-validated moov box. The
// caller may apply it only to its Native-owned staging object, then must
// re-read that object with inspect_isobmff_video_profile before publishing.
struct media_foundation_cicp_patch {
    uint64_t moov_offset{};
    std::size_t codec_config_box_offset{};
    std::size_t codec_config_offset{};
    std::size_t color_payload_offset{};
    std::vector<uint8_t> moov_bytes;
    bool changed{};
};

bool inspect_isobmff_video_profile(
    const lpb::random_access_reader& source,
    isobmff_video_profile& output,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic = nullptr) noexcept;

// Internal Native decision seam. This is not part of the exported C ABI.
media_foundation_cicp_patch_mode select_media_foundation_cicp_patch_mode(
    const isobmff_video_profile& actual,
    const isobmff_video_profile& source_profile) noexcept;

// Prepare a size-preserving correction only for the CICP distinctions lost
// by the frozen MF color enums (transfer 1/6 and matrix 5/6). Every other
// profile fact must already match the authoritative source profile.
bool prepare_media_foundation_cicp_patch(
    const lpb::random_access_reader& staged_output,
    const isobmff_video_profile& source_profile,
    media_foundation_cicp_patch& patch,
    video_profile_failure& failure,
    video_profile_diagnostic* diagnostic = nullptr) noexcept;

video_conversion_owner select_video_conversion_owner(
    const isobmff_video_profile& source,
    requested_video_codec target) noexcept;

} // namespace lpb::media
