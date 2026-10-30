#include "media/heic_backend.h"

#include "media/jpeg_backend.h"

#include "media/media_inspector.h"

#include "media/jpeg_observation.h"

#include "media/xmp_observation.h"

#include "media/hdr_image_metadata.h"

#include "media/hdr_image_exif_mapping.h"

#include "media/pixel_orientation.h"

#include "containers/heif_internal.h"

#include "protocols/samsung_inventory.h"

#include "foundation/internal.h"

#include "foundation/sha256.h"

#include "platform/windows_filesystem.h"



#include <algorithm>

#include <array>

#include <bcrypt.h>

#include <charconv>

#include <cmath>

#include <cstdio>

#include <cstring>

#include <fstream>

#include <limits>

#include <string>

#include <string_view>

#include <vector>



using namespace lpb;

using namespace lpb::media;



namespace {



constexpr std::string_view kHdrGainMapNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";

constexpr std::string_view kContainerNamespace = "http://ns.google.com/photos/1.0/container/";

constexpr std::string_view kContainerItemNamespace = "http://ns.google.com/photos/1.0/container/item/";

constexpr std::array<uint8_t, 4> kMpfSignature = { 'M', 'P', 'F', 0 };

constexpr size_t kMpfTiffOffset = 10; // SOI + APP2 marker/length + "MPF\0".

constexpr size_t kMpfSegmentSize = 90;

constexpr uint16_t kMpfSegmentLength = 88;



class unique_handle {

public:

    explicit unique_handle(HANDLE value = INVALID_HANDLE_VALUE) noexcept : value_(value) {}

    unique_handle(const unique_handle&) = delete;

    unique_handle& operator=(const unique_handle&) = delete;

    ~unique_handle() noexcept { if (value_ != INVALID_HANDLE_VALUE) CloseHandle(value_); }

    HANDLE get() const noexcept { return value_; }

private:

    HANDLE value_;

};



bool fixed_field_equals(const char* field, size_t capacity, const char* expected,

    size_t expected_capacity) noexcept {

    if (!field || !expected) return false;

    const auto* end = static_cast<const char*>(std::memchr(field, '\0', capacity));

    const auto* expected_end = static_cast<const char*>(std::memchr(expected, '\0', expected_capacity));

    if (!end || !expected_end) return false;

    const size_t length = static_cast<size_t>(end - field);

    const size_t expected_length = static_cast<size_t>(expected_end - expected);

    return length > 0 && length == expected_length && std::memcmp(field, expected, length) == 0;

}



bool request_field_valid(const char* field, size_t capacity) noexcept {

    const auto* end = field ? static_cast<const char*>(std::memchr(field, '\0', capacity)) : nullptr;

    return end != nullptr && end != field;

}



bool same_hash(const uint8_t* left, const uint8_t* right) noexcept {

    return left && right && std::memcmp(left, right, 32) == 0;

}



bool valid_file_identity(const lpb_file_identity& identity,

    const lpb_hdr_gainmap_conversion_request_v1& request) noexcept {

    return identity.volume_serial == request.expected_volume_serial &&

        identity.file_index == request.expected_file_index &&

        identity.file_size == request.expected_file_size &&

        identity.link_count == request.expected_link_count;

}



bool read_locked_file(HANDLE handle, uint64_t expected_size, std::vector<uint8_t>& output) noexcept {

    output.clear();

    if (handle == INVALID_HANDLE_VALUE || expected_size == 0 || expected_size > 512ull * 1024ull * 1024ull ||

        expected_size > std::numeric_limits<size_t>::max()) return false;

    try { output.resize(static_cast<size_t>(expected_size)); }

    catch (...) { return false; }

    LARGE_INTEGER start{};

    if (!SetFilePointerEx(handle, start, nullptr, FILE_BEGIN)) { output.clear(); return false; }

    size_t position = 0;

    while (position < output.size()) {

        const DWORD request_size = static_cast<DWORD>(std::min<size_t>(output.size() - position,

            static_cast<size_t>(std::numeric_limits<DWORD>::max())));

        DWORD read = 0;

        if (!ReadFile(handle, output.data() + position, request_size, &read, nullptr) || read == 0) {

            output.clear();

            return false;

        }

        position += read;

    }

    return position == output.size();

}



bool valid_gainmap_graph(lpb_context* context, const lpb_source_media_facts& facts) noexcept {

    if (facts.struct_size < sizeof(lpb_source_media_facts) || facts.auxiliary_count == 0 || facts.auxiliary_count > 8 ||

        facts.gain_map.struct_size < sizeof(lpb_gainmap_item_facts) || !facts.gain_map.is_present ||

        facts.gain_map.file_range.length == 0 || facts.gain_map.container == LPB_IMAGE_CONTAINER_UNKNOWN ||

        facts.gain_map.auxiliary_index >= facts.auxiliary_count || facts.gain_map.relationship[0] == '\0') {

        set_error(context, "GainMap facts do not contain one complete auxiliary identity.");

        return false;

    }

    size_t semantic_count = 0;

    for (uint32_t index = 0; index < facts.auxiliary_count; ++index) {

        const auto& auxiliary = facts.auxiliary_items[index];

        if (auxiliary.struct_size < sizeof(lpb_auxiliary_item_facts) || !auxiliary.is_present ||

            !std::memchr(auxiliary.semantic, '\0', sizeof(auxiliary.semantic)) ||

            !std::memchr(auxiliary.relationship, '\0', sizeof(auxiliary.relationship)) ||

            !std::memchr(auxiliary.stable_identity, '\0', sizeof(auxiliary.stable_identity)) ||

            !std::memchr(auxiliary.owner_identity, '\0', sizeof(auxiliary.owner_identity))) {

            set_error(context, "GainMap graph contains an incomplete auxiliary entry.");

            return false;

        }

        if (std::string_view(auxiliary.semantic) == "GainMap") ++semantic_count;

    }

    const auto& gain_map = facts.gain_map;

    const auto& auxiliary = facts.auxiliary_items[gain_map.auxiliary_index];

    const int32_t expected_owner_role = gain_map.ownership == LPB_AUX_OWNER_PRIMARY

        ? LPB_ARTIFACT_PRIMARY_IMAGE : LPB_ARTIFACT_AUXILIARY_ITEM;

    const bool matches = semantic_count == 1 && gain_map.representation >= LPB_AUX_REPRESENTATION_EMBEDDED &&

        gain_map.representation <= LPB_AUX_REPRESENTATION_MATERIALIZED &&

        gain_map.ownership >= LPB_AUX_OWNER_PRIMARY && gain_map.ownership <= LPB_AUX_OWNER_AUXILIARY &&

        gain_map.owner_artifact_role == expected_owner_role && auxiliary.container == gain_map.container &&

        auxiliary.representation == gain_map.representation && auxiliary.ownership == gain_map.ownership &&

        auxiliary.item_id == gain_map.item_id && auxiliary.file_range.offset == gain_map.file_range.offset &&

        auxiliary.file_range.length == gain_map.file_range.length &&

        std::string_view(auxiliary.relationship) == std::string_view(gain_map.relationship) &&

        std::string_view(auxiliary.semantic) == "GainMap";

    if (!matches) set_error(context, "GainMap facts are duplicate, stale, or inconsistent with their auxiliary entry.");

    return matches;

}



bool representable_heif_gainmap_graph(const lpb_auxiliary_item_facts& gain_map,

    uint64_t source_size) noexcept {

    const auto* item_type_end = static_cast<const char*>(

        std::memchr(gain_map.item_type, '\0', sizeof(gain_map.item_type)));

    if (!item_type_end || item_type_end == gain_map.item_type ||

        gain_map.dependency_count > LPB_HEIF_MAX_DEPENDENCIES ||

        (gain_map.graph_flags & LPB_HEIF_GRAPH_COMPLETE) == 0) return false;

    const std::string_view item_type(gain_map.item_type,

        static_cast<size_t>(item_type_end - gain_map.item_type));

    constexpr uint32_t known_graph_flags = LPB_HEIF_GRAPH_COMPLETE |

        LPB_HEIF_GRAPH_DERIVED | LPB_HEIF_GRAPH_SUPPORTED_DERIVED;

    if ((gain_map.graph_flags & ~known_graph_flags) != 0) return false;



    if (gain_map.dependency_count == 0) {

        if ((gain_map.graph_flags & (LPB_HEIF_GRAPH_DERIVED | LPB_HEIF_GRAPH_SUPPORTED_DERIVED)) != 0)

            return false;

        return (item_type == "hvc1" || item_type == "hev1") &&

            gain_map.codec == LPB_AUX_CODEC_HEVC;

    }



    // The canonical Apple and Samsung HEIC GainMaps are complete `grid`

    // auxiliary items. libheif resolves the grid from this exact item ID;

    // accept only its directly referenced HEVC tiles and reject other derived

    // item types, duplicate edges, unbounded ranges, and overlapping payloads.

    if (item_type != "grid" ||

        (gain_map.graph_flags & (LPB_HEIF_GRAPH_DERIVED | LPB_HEIF_GRAPH_SUPPORTED_DERIVED)) !=

            (LPB_HEIF_GRAPH_DERIVED | LPB_HEIF_GRAPH_SUPPORTED_DERIVED) ||

        gain_map.codec != LPB_AUX_CODEC_HEVC) return false;



    for (uint32_t index = 0; index < gain_map.dependency_count; ++index) {

        const uint32_t item_id = gain_map.dependency_item_ids[index];

        const uint64_t offset = gain_map.dependency_offsets[index];

        const uint64_t length = gain_map.dependency_lengths[index];

        const char* dependency_type = gain_map.dependency_item_types[index];

        const auto* dependency_type_end = static_cast<const char*>(

            std::memchr(dependency_type, '\0', sizeof(gain_map.dependency_item_types[index])));

        if (item_id == 0 || offset > source_size || length == 0 || length > source_size - offset ||

            !dependency_type_end || dependency_type_end == dependency_type) return false;

        const std::string_view type(dependency_type,

            static_cast<size_t>(dependency_type_end - dependency_type));

        if (type != "hvc1" && type != "hev1") return false;

        for (uint32_t previous = 0; previous < index; ++previous) {

            if (gain_map.dependency_item_ids[previous] == item_id) return false;

            const uint64_t previous_offset = gain_map.dependency_offsets[previous];

            const uint64_t previous_length = gain_map.dependency_lengths[previous];

            if (offset < previous_offset + previous_length &&

                previous_offset < offset + length) return false;

        }

    }

    return true;

}



bool safe_rgb_surface(const pixel_surface& pixels, bool require_icc) noexcept {

    if (pixels.width == 0 || pixels.height == 0 || pixels.channels != 3 ||

        pixels.signal_bit_depth != 8 || pixels.storage_bit_depth != 8 ||

        pixels.width > static_cast<uint32_t>(std::numeric_limits<int>::max()) ||

        pixels.height > static_cast<uint32_t>(std::numeric_limits<int>::max()) ||

        pixels.stride != static_cast<uint64_t>(pixels.width) * 3u) return false;

    const uint64_t pixel_count = static_cast<uint64_t>(pixels.width) * pixels.height;

    if (pixel_count > std::numeric_limits<size_t>::max() / 3u) return false;

    const uint64_t byte_count = pixel_count * 3u;

    return

        pixels.pixels.size() == static_cast<size_t>(byte_count) &&

        (!require_icc || (!pixels.color.icc_profile.empty() && pixels.color.icc_profile.size() <= 4u * 1024u * 1024u));

}



bool insert_xmp(std::span<const uint8_t> jpeg, std::string_view xml,

    std::vector<uint8_t>& output) {

    output.clear();

    if (!standalone_jpeg_codestream(jpeg) || xml.empty()) return false;

    constexpr std::string_view signature("http://ns.adobe.com/xap/1.0/\0", 29);

    const size_t payload_size = signature.size() + xml.size();

    if (payload_size > 65533u || payload_size + 2u > std::numeric_limits<uint16_t>::max()) return false;

    try {

        output.reserve(jpeg.size() + payload_size + 4u);

        output.insert(output.end(), jpeg.begin(), jpeg.begin() + 2);

        output.push_back(0xFF);

        output.push_back(0xE1);

        const uint16_t segment_length = static_cast<uint16_t>(payload_size + 2u);

        output.push_back(static_cast<uint8_t>(segment_length >> 8));

        output.push_back(static_cast<uint8_t>(segment_length));

        output.insert(output.end(), signature.begin(), signature.end());

        output.insert(output.end(), xml.begin(), xml.end());

        output.insert(output.end(), jpeg.begin() + 2, jpeg.end());

        return standalone_jpeg_codestream(output);

    } catch (...) {

        output.clear();

        return false;

    }

}



bool merge_ordinary_primary_xmp(std::string_view ordinary,std::string& generated,uint64_t gainmap_length,uint64_t primary_padding) {

    if(ordinary.empty())return true;

    lpb::media::target_xmp_observation source,target;lpb::media::xmp_observation_failure failure{};

    if(!lpb::media::observe_target_xmp(ordinary,source,failure)||source.live_claim||source.micro_claim||

        source.foreign_claim||source.extended_reference_present||

        !lpb::media::observe_target_xmp(generated,target,failure)||

        target.rdf_close_offset==std::string::npos)return false;

    if(source.directory_present)

        return lpb::media::rewrite_heif_gainmap_directory_to_jpeg(ordinary,gainmap_length,primary_padding,generated,failure);

    if(source.hdr_claim)return false;

    // The inserted fragment owns all its namespaces, independent of source prefixes.

    std::string fragment=generated.substr(target.rdf_content_offset,target.rdf_close_offset-target.rdf_content_offset);

    constexpr std::string_view description="<rdf:Description ";

    if(!fragment.starts_with(description))return false;

    fragment.insert(description.size(),"xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" ");

    generated=ordinary;

    if(source.rdf_close_offset!=std::string::npos)generated.insert(source.rdf_close_offset,fragment);

    else {

        if(source.rdf_content_offset<2||source.rdf_name.empty()||

            ordinary.substr(source.rdf_content_offset-2,2)!="/>")return false;

        generated.replace(source.rdf_content_offset-2,2,">"+fragment+"</"+source.rdf_name+">");

    }

    lpb::media::target_xmp_observation final;

    return lpb::media::observe_target_xmp(generated,final,failure)&&!final.live_claim&&!final.micro_claim&&!final.foreign_claim;

}



bool format_double(double value, std::string& output) {

    if (!std::isfinite(value)) return false;

    char buffer[64]{};

    const auto converted = std::to_chars(std::begin(buffer), std::end(buffer), value,

        std::chars_format::general, std::numeric_limits<double>::max_digits10);

    if (converted.ec != std::errc{}) return false;

    output.assign(buffer, converted.ptr);

    return true;

}



bool valid_iso_metadata(const iso_gainmap_metadata& metadata) noexcept {

    if (!std::isfinite(metadata.hdr_capacity_min) || !std::isfinite(metadata.hdr_capacity_max) ||

        metadata.hdr_capacity_min < 0.0 || metadata.hdr_capacity_max <= metadata.hdr_capacity_min ||

        metadata.base_rendition_is_hdr) return false;

    for (size_t channel = 0; channel < 3; ++channel) {

        if (!std::isfinite(metadata.gain_map_min[channel]) ||

            !std::isfinite(metadata.gain_map_max[channel]) ||

            !std::isfinite(metadata.gamma[channel]) ||

            !std::isfinite(metadata.offset_sdr[channel]) ||

            !std::isfinite(metadata.offset_hdr[channel]) ||

            metadata.gain_map_min[channel] > metadata.gain_map_max[channel] ||

            metadata.gamma[channel] <= 0.0 || metadata.offset_sdr[channel] < 0.0 ||

            metadata.offset_hdr[channel] < 0.0) return false;

        if (channel > 0 &&

            (metadata.gain_map_min[channel] != metadata.gain_map_min[0] ||

             metadata.gain_map_max[channel] != metadata.gain_map_max[0] ||

             metadata.gamma[channel] != metadata.gamma[0] ||

             metadata.offset_sdr[channel] != metadata.offset_sdr[0] ||

             metadata.offset_hdr[channel] != metadata.offset_hdr[0])) return false;

    }

    return true;

}



bool create_gainmap_xmp(const iso_gainmap_metadata& metadata, std::string& xml) {

    if (!valid_iso_metadata(metadata)) return false;

    std::string min_value, max_value, gamma, offset_sdr, offset_hdr, capacity_min, capacity_max;

    if (!format_double(metadata.gain_map_min[0], min_value) ||

        !format_double(metadata.gain_map_max[0], max_value) ||

        !format_double(metadata.gamma[0], gamma) ||

        !format_double(metadata.offset_sdr[0], offset_sdr) ||

        !format_double(metadata.offset_hdr[0], offset_hdr) ||

        !format_double(metadata.hdr_capacity_min, capacity_min) ||

        !format_double(metadata.hdr_capacity_max, capacity_max)) return false;

    xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:hdrgm=\"";

    xml.append(kHdrGainMapNamespace);

    xml += "\" hdrgm:Version=\"1.0\" hdrgm:GainMapMin=\"" + min_value +

        "\" hdrgm:GainMapMax=\"" + max_value + "\" hdrgm:Gamma=\"" + gamma +

        "\" hdrgm:OffsetSDR=\"" + offset_sdr + "\" hdrgm:OffsetHDR=\"" + offset_hdr +

        "\" hdrgm:HDRCapacityMin=\"" + capacity_min + "\" hdrgm:HDRCapacityMax=\"" + capacity_max +

        "\" hdrgm:BaseRenditionIsHDR=\"False\"/></rdf:RDF></x:xmpmeta>";

    return true;

}



bool create_primary_xmp(size_t gainmap_size, std::string& xml) {

    if (gainmap_size == 0 || gainmap_size > std::numeric_limits<uint32_t>::max()) return false;

    xml = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" xmlns:hdrgm=\"";

    xml.append(kHdrGainMapNamespace);

    xml += "\" xmlns:Container=\"";

    xml.append(kContainerNamespace);

    xml += "\" xmlns:Item=\"";

    xml.append(kContainerItemNamespace);

    xml += "\" hdrgm:Version=\"1.0\"><Container:Directory><rdf:Seq>"

        "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>"

        "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"" +

        std::to_string(gainmap_size) + "\"/></rdf:li></rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta>";

    return true;

}



void write_be16(uint8_t* p, uint16_t value) noexcept {

    p[0] = static_cast<uint8_t>(value >> 8); p[1] = static_cast<uint8_t>(value);

}

void write_be32(uint8_t* p, uint32_t value) noexcept {

    p[0] = static_cast<uint8_t>(value >> 24); p[1] = static_cast<uint8_t>(value >> 16);

    p[2] = static_cast<uint8_t>(value >> 8); p[3] = static_cast<uint8_t>(value);

}

uint16_t read_be16(const uint8_t* p) noexcept {

    return static_cast<uint16_t>((static_cast<uint16_t>(p[0]) << 8) | p[1]);

}

uint32_t read_be32(const uint8_t* p) noexcept {

    return (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) |

        (static_cast<uint32_t>(p[2]) << 8) | p[3];

}



bool append_mpf(std::span<const uint8_t> primary, size_t gainmap_size,

    std::vector<uint8_t>& output,size_t padding=0) {

    output.clear();

    if (!standalone_jpeg_codestream(primary) || primary.size() > std::numeric_limits<uint32_t>::max() - kMpfSegmentSize ||

        gainmap_size == 0 || gainmap_size > std::numeric_limits<uint32_t>::max()) return false;

    const uint32_t primary_size = static_cast<uint32_t>(primary.size() + kMpfSegmentSize);

    if (primary_size < kMpfTiffOffset || padding>UINT32_MAX-primary_size) return false;

    try {

        std::array<uint8_t, kMpfSegmentSize> segment{};

        segment[0] = 0xFF; segment[1] = 0xE2;

        write_be16(segment.data() + 2, kMpfSegmentLength);

        std::copy(kMpfSignature.begin(), kMpfSignature.end(), segment.begin() + 4);

        uint8_t* tiff = segment.data() + 8;

        tiff[0] = 'M'; tiff[1] = 'M'; write_be16(tiff + 2, 42); write_be32(tiff + 4, 8);

        write_be16(tiff + 8, 3);

        uint8_t* entry = tiff + 10;

        write_be16(entry, 0xB000); write_be16(entry + 2, 7); write_be32(entry + 4, 4);

        entry[8] = '0'; entry[9] = '1'; entry[10] = '0'; entry[11] = '0';

        entry += 12;

        write_be16(entry, 0xB001); write_be16(entry + 2, 4); write_be32(entry + 4, 1); write_be32(entry + 8, 2);

        entry += 12;

        write_be16(entry, 0xB002); write_be16(entry + 2, 7); write_be32(entry + 4, 32); write_be32(entry + 8, 50);

        write_be32(tiff + 46, 0);

        uint8_t* mp_entry = tiff + 50;

        write_be32(mp_entry, 0x20000000); write_be32(mp_entry + 4, primary_size);

        write_be32(mp_entry + 8, 0); write_be16(mp_entry + 12, 0); write_be16(mp_entry + 14, 0);

        mp_entry += 16;

        write_be32(mp_entry, 0); write_be32(mp_entry + 4, static_cast<uint32_t>(gainmap_size));

        write_be32(mp_entry + 8, primary_size + static_cast<uint32_t>(padding) - static_cast<uint32_t>(kMpfTiffOffset));

        write_be16(mp_entry + 12, 0); write_be16(mp_entry + 14, 0);

        output.reserve(primary.size() + segment.size());

        output.insert(output.end(), primary.begin(), primary.begin() + 2);

        output.insert(output.end(), segment.begin(), segment.end());

        output.insert(output.end(), primary.begin() + 2, primary.end());

        return true;

    } catch (...) {

        output.clear();

        return false;

    }

}



bool verify_mpf_layout(std::span<const uint8_t> combined, size_t expected_map_size,

    size_t& primary_size,size_t padding=0) noexcept {

    primary_size = 0;

    if (combined.size() < kMpfSegmentSize + 4 || combined[0] != 0xFF || combined[1] != 0xD8 ||

        combined[2] != 0xFF || combined[3] != 0xE2 || read_be16(combined.data() + 4) != kMpfSegmentLength ||

        !std::equal(kMpfSignature.begin(), kMpfSignature.end(), combined.begin() + 6)) return false;

    const uint8_t* tiff = combined.data() + kMpfTiffOffset;

    if (tiff[0] != 'M' || tiff[1] != 'M' || read_be16(tiff + 2) != 42 || read_be32(tiff + 4) != 8 ||

        read_be16(tiff + 8) != 3) return false;

    const uint8_t* entry = tiff + 10;

    if (read_be16(entry) != 0xB000 || read_be16(entry + 2) != 7 || read_be32(entry + 4) != 4 ||

        std::memcmp(entry + 8, "0100", 4) != 0) return false;

    entry += 12;

    if (read_be16(entry) != 0xB001 || read_be16(entry + 2) != 4 || read_be32(entry + 4) != 1 || read_be32(entry + 8) != 2)

        return false;

    entry += 12;

    if (read_be16(entry) != 0xB002 || read_be16(entry + 2) != 7 || read_be32(entry + 4) != 32 || read_be32(entry + 8) != 50)

        return false;

    if (read_be32(tiff + 46) != 0) return false;

    const uint8_t* mp_entry = tiff + 50;

    const uint32_t first_attributes = read_be32(mp_entry);

    const uint32_t first_size = read_be32(mp_entry + 4);

    if (first_attributes != 0x20000000 || first_size < kMpfSegmentSize + 4 || read_be32(mp_entry + 8) != 0 ||

        read_be16(mp_entry + 12) != 0 || read_be16(mp_entry + 14) != 0) return false;

    mp_entry += 16;

    const uint32_t second_size = read_be32(mp_entry + 4);

    const uint32_t second_offset = read_be32(mp_entry + 8);

    if (read_be32(mp_entry) != 0 || second_size != expected_map_size ||

        padding>UINT32_MAX-first_size||second_offset != first_size +padding- static_cast<uint32_t>(kMpfTiffOffset) ||

        read_be16(mp_entry + 12) != 0 || read_be16(mp_entry + 14) != 0 ||

        first_size > combined.size() || padding>combined.size()-first_size||second_size > combined.size() - first_size-padding ||

        first_size + padding + second_size != combined.size() ||

        !standalone_jpeg_codestream(combined.first(first_size)) ||

        !standalone_jpeg_codestream(combined.subspan(first_size+padding, second_size))) return false;

    primary_size = first_size;

    return true;

}



bool same_metadata(const iso_gainmap_metadata& left, const gainmap_metadata_facts& right) noexcept {

    if (right.kind != gainmap_metadata_kind::iso || !valid_iso_metadata(left) || !valid_iso_metadata(right.iso) ||

        left.hdr_capacity_min != right.iso.hdr_capacity_min || left.hdr_capacity_max != right.iso.hdr_capacity_max ||

        left.base_rendition_is_hdr != right.iso.base_rendition_is_hdr) return false;

    for (size_t channel = 0; channel < 3; ++channel) {

        if (left.gain_map_min[channel] != right.iso.gain_map_min[channel] ||

            left.gain_map_max[channel] != right.iso.gain_map_max[channel] ||

            left.gamma[channel] != right.iso.gamma[channel] ||

            left.offset_sdr[channel] != right.iso.offset_sdr[channel] ||

            left.offset_hdr[channel] != right.iso.offset_hdr[channel]) return false;

    }

    return true;

}



struct hdr_gainmap_stage {

    windows_owned_output output;

    std::filesystem::path destination;

    std::array<uint8_t, 32> expected_sha256{};

    std::array<uint8_t, 32> metadata_source_sha256{};

    uint32_t ordinary_primary_xmp_proof_kind{};

    lpb_image_conversion_proof_v1 image_metadata_proof{};

    std::unique_ptr<windows_readonly_source> metadata_source_lease;

    std::filesystem::path metadata_source_path;

    lpb_file_identity metadata_source_identity{};

    lpb_hdr_gainmap_target_semantic target_semantic{ LPB_HDR_GAINMAP_TARGET_UNKNOWN };

    lpb_image_container actual_container{ LPB_IMAGE_CONTAINER_UNKNOWN };

};



bool register_stage(lpb_context* context, const std::shared_ptr<hdr_gainmap_stage>& stage,

    uint64_t& token) noexcept {

    token = 0;

    if (!context || !stage) return false;

    try {

        std::scoped_lock lock(context->hdr_gainmap_mutex);

        for (int attempt = 0; attempt < 32; ++attempt) {

            uint64_t candidate = 0;

            if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&candidate),

                    static_cast<ULONG>(sizeof(candidate)), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0 || candidate == 0)

                continue;

            const auto duplicate = std::find_if(context->hdr_gainmap_stages.begin(),

                context->hdr_gainmap_stages.end(), [candidate](const auto& entry) { return entry.first == candidate; });

            if (duplicate != context->hdr_gainmap_stages.end()) continue;

            context->hdr_gainmap_stages.emplace_back(candidate, stage);

            token = candidate;

            return true;

        }

    } catch (...) { }

    return false;

}



lpb_result fail(lpb_context* context, const char* message, lpb_result result = LPB_RESULT_INVALID_ARGUMENT) noexcept {

    set_error(context, message);

    return result;

}



lpb_result abort_and_fail(lpb_context* context, windows_owned_output& output,

    const char* message, lpb_result result) noexcept {

    return abort_owned_output_and_report(context, output, message)

        ? result : LPB_RESULT_INTERNAL_ERROR;

}



lpb_result convert_heic_gainmap_to_jpeg_ultrahdr(lpb_context* context,

    const char* source_path, const char* output_path,

    const lpb_hdr_gainmap_conversion_request_v1& request,

    lpb_hdr_gainmap_conversion_result_v1& result) {

    if (!context || !source_path || !output_path || source_path[0] == '\0' || output_path[0] == '\0' ||

        paths_alias(source_path, output_path) || request.struct_size != sizeof(request) || request.api_version != 1 ||

        request.source_container != LPB_IMAGE_CONTAINER_HEIC ||

        request.target_semantic != LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP ||

        request.quality < 1 || request.quality > 100 || request.hdr_output_policy < 0 || request.hdr_output_policy > 1 ||

        request.item_id == 0 || request.expected_file_size == 0 || request.reserved != 0 ||

        request.expected_link_count == 0 || !request_field_valid(request.stable_identity, sizeof(request.stable_identity)) ||

        !request_field_valid(request.inspected_stable_identity, sizeof(request.inspected_stable_identity)) ||

        !request_field_valid(request.semantic, sizeof(request.semantic)) ||

        !request_field_valid(request.owner_identity, sizeof(request.owner_identity)) ||

        !request_field_valid(request.inspected_owner_identity, sizeof(request.inspected_owner_identity)) ||

        !request_field_valid(request.relationship, sizeof(request.relationship)) ||

        request.source_range.length == 0 || request.source_range.offset > request.expected_file_size ||

        request.source_range.length > request.expected_file_size - request.source_range.offset) {

        return fail(context, "HDR/GainMap conversion request is malformed, unsupported, or does not identify a HEIC-to-JPEG semantic route.");

    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED)

        return fail(context, "HDR/GainMap conversion cancelled before source inspection.", LPB_RESULT_CANCELLED);



    const std::filesystem::path source_native = utf8_to_path(source_path);

    const std::filesystem::path output_native = utf8_to_path(output_path);

    if (source_native.empty() || output_native.empty()) return fail(context, "HDR/GainMap source or target path is not valid UTF-8.");

    unique_handle source_handle(CreateFileW(source_native.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,

        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));

    if (source_handle.get() == INVALID_HANDLE_VALUE) return fail(context, "Could not lock the exact HEIC source for semantic conversion.", LPB_RESULT_INTERNAL_ERROR);

    lpb_file_identity source_identity{};

    std::wstring source_final_path;

    if (!capture_file_identity_from_handle(source_handle.get(), source_identity, source_final_path) ||

        !valid_file_identity(source_identity, request)) {

        return fail(context, "HEIC source filesystem identity changed after its GainMap binding was created.");

    }

    uint8_t source_hash[32]{};

    if (!lpb::crypto::sha256_file(source_handle.get(), source_hash) || !same_hash(source_hash, request.source_sha256))

        return fail(context, "HEIC source SHA-256 no longer matches the bound artifact.");



    std::vector<uint8_t> neutral_bytes;

    if(!read_locked_file(source_handle.get(),source_identity.file_size,neutral_bytes))

        return fail(context,"HEIC metadata transfer could not read its pinned source.");

    uint8_t neutral_hash[32]{};lpb::crypto::sha256_buffer(neutral_bytes.data(),neutral_bytes.size(),neutral_hash);

    if(!same_hash(neutral_hash,source_hash))return fail(context,"HEIC metadata source bytes changed.");

    lpb::containers::heif_original_file_resource original_file;std::string original_error;

    if(!lpb::containers::observe_original_file_resource(neutral_bytes,original_file,original_error))

        return fail(context,"HEIC Original file resource graph is malformed.");

    if(original_file.present)return fail(context,"HEIC-to-JPEG conversion has not yet proved complete Original file resource carriage.",LPB_RESULT_UNSUPPORTED);

    lpb::containers::heif_primary_properties primary_properties;

    std::string property_error,ordinary_primary_xmp;uint32_t metadata_primary=0;

    if(!lpb::containers::observe_primary_item_properties(neutral_bytes,primary_properties,property_error)||

        !lpb::containers::read_primary_xmp(neutral_bytes,metadata_primary,ordinary_primary_xmp,property_error)||

        primary_properties.item_id!=metadata_primary)

        return fail(context,"HEIC metadata does not have an unambiguous primary owner.");



    lpb_source_media_facts source_facts{};

    source_facts.struct_size = sizeof(source_facts);

    const lpb_result inspected = inspect_source(context, source_path, nullptr, &source_facts);

    if (inspected != LPB_RESULT_OK) return inspected;

    if (!valid_gainmap_graph(context, source_facts) ||

        source_facts.primary_image.container != LPB_IMAGE_CONTAINER_HEIC ||

        source_facts.gain_map.is_present != 1 ||

        source_facts.gain_map.container != LPB_IMAGE_CONTAINER_HEIC ||

        source_facts.gain_map.item_id != request.item_id ||

        source_facts.gain_map.auxiliary_index != request.auxiliary_index ||

        source_facts.gain_map.file_range.offset != request.source_range.offset ||

        source_facts.gain_map.file_range.length != request.source_range.length ||

        source_facts.gain_map.representation != request.representation ||

        source_facts.gain_map.ownership != request.ownership ||

        source_facts.gain_map.owner_artifact_role != request.owner_artifact_role ||

        !same_hash(source_facts.primary_sha256, request.primary_sha256) ||

        source_facts.gain_map.auxiliary_index >= source_facts.auxiliary_count) {

        return fail(context, "Fresh HEIF item graph no longer matches the bound primary and GainMap identity.");

    }

    size_t gainmap_count = 0;

    const lpb_auxiliary_item_facts* source_gainmap = nullptr;

    for (uint32_t index = 0; index < source_facts.auxiliary_count; ++index) {

        const auto& auxiliary = source_facts.auxiliary_items[index];

        if (std::string_view(auxiliary.semantic) == "GainMap") {

            ++gainmap_count;

            if (index == source_facts.gain_map.auxiliary_index) source_gainmap = &auxiliary;

        }

    }

    if (gainmap_count != 1 || !source_gainmap || !source_gainmap->is_present ||

        source_gainmap->item_id != request.item_id ||

        source_gainmap->file_range.offset != request.source_range.offset ||

        source_gainmap->file_range.length != request.source_range.length ||

        source_gainmap->representation != request.representation || source_gainmap->ownership != request.ownership ||

        !same_hash(source_gainmap->sha256, request.gainmap_sha256) ||

        !fixed_field_equals(request.semantic, sizeof(request.semantic), source_gainmap->semantic, sizeof(source_gainmap->semantic)) ||

        !fixed_field_equals(request.inspected_stable_identity, sizeof(request.inspected_stable_identity),

            source_gainmap->stable_identity, sizeof(source_gainmap->stable_identity)) ||

        !fixed_field_equals(request.inspected_owner_identity, sizeof(request.inspected_owner_identity),

            source_gainmap->owner_identity, sizeof(source_gainmap->owner_identity)) ||

        !fixed_field_equals(request.relationship, sizeof(request.relationship),

            source_gainmap->relationship, sizeof(source_gainmap->relationship)) ||

        !request_field_valid(request.stable_identity, sizeof(request.stable_identity)) ||

        !request_field_valid(request.owner_identity, sizeof(request.owner_identity))) {

        return fail(context, "Fresh HEIF GainMap semantic, owner, relationship, representation, or byte hash is stale or ambiguous.");

    }

    // Materialized/detached items and derived graphs outside the narrowly

    // supported, complete HEVC grid shape fail closed. The source file hash

    // and fresh item graph above bind every grid tile's bytes and relationship.

    if (source_gainmap->representation != LPB_AUX_REPRESENTATION_EMBEDDED ||

        source_gainmap->ownership != LPB_AUX_OWNER_PRIMARY ||

        !representable_heif_gainmap_graph(*source_gainmap, source_identity.file_size)) {

        return fail(context, "HEIF GainMap is not one complete, directly coded or supported HEVC grid item that this target can represent.");

    }



    gainmap_metadata_facts source_metadata{};

    const lpb_result metadata_result = inspect_gainmap_metadata(context, source_path, nullptr, source_metadata);

    if (metadata_result != LPB_RESULT_OK) return metadata_result;

    iso_gainmap_metadata target_metadata{};

    if (source_metadata.kind == gainmap_metadata_kind::iso) {

        target_metadata = source_metadata.iso;

        if (!valid_iso_metadata(target_metadata))

            return fail(context, "Source ISO GainMap metadata is incomplete, non-finite, per-channel, or not representable by this JPEG target.");

    } else if (source_metadata.kind == gainmap_metadata_kind::apple) {

        if (!std::isfinite(source_metadata.apple_maker_note_33) || !std::isfinite(source_metadata.apple_maker_note_48))

            return fail(context, "Apple GainMap MakerNote mapping is not finite.");

    } else {

        return fail(context, "Source GainMap metadata format is unsupported.");

    }



    lpb::containers::heif_primary_properties gainmap_properties;

    if(!lpb::containers::observe_item_properties(neutral_bytes,request.item_id,gainmap_properties,property_error))

        return fail(context,"Bound HEIC GainMap item geometry is ambiguous or invalid.");

    uint32_t exif_orientation=0;exif_observation exif_facts;jpeg_observation_failure exif_error{};

    const bool exact_exif_orientation=!primary_properties.exif_payload.empty()&&

        observe_exif(primary_properties.exif_payload,exif_orientation,exif_facts,exif_error,true)&&

        exif_orientation==primary_properties.orientation;

    const bool stored_geometry=source_facts.protocol==LPB_SOURCE_PROTOCOL_NON_LIVE&&

        primary_properties.orientation>1&&primary_properties.orientation<=8&&exact_exif_orientation&&

        !primary_properties.has_clean_aperture&&!gainmap_properties.has_clean_aperture&&

        primary_properties.stored_geometry_compatible&&gainmap_properties.stored_geometry_compatible&&

        gainmap_properties.orientation==primary_properties.orientation;

    if(primary_properties.orientation>1&&!stored_geometry)

        return fail(context,"Oriented HEIC primary and bound GainMap require identical unambiguous no-crop item transforms and matching Exif orientation.");

    heic_image_facts heic_facts{};

    pixel_surface primary_pixels{};

    const lpb_result primary_decoded = decode_heic_primary_file(context, source_path, primary_pixels, &heic_facts,stored_geometry);

    if (primary_decoded != LPB_RESULT_OK) return primary_decoded;

    if (heic_facts.has_alpha || !safe_rgb_surface(primary_pixels, true) ||

        primary_pixels.width != (stored_geometry?primary_properties.width:heic_facts.width) ||

        primary_pixels.height != (stored_geometry?primary_properties.height:heic_facts.height)) {

        return fail(context, "HEIC primary rendition is not an ICC-bound 8-bit RGB image representable by the JPEG target.");

    }

    const bool exact_metadata_geometry=source_facts.protocol==LPB_SOURCE_PROTOCOL_NON_LIVE&&

        (primary_properties.orientation==1||stored_geometry)&&!primary_properties.has_clean_aperture&&

        primary_properties.width==primary_pixels.width&&primary_properties.height==primary_pixels.height;

    pixel_surface gainmap_pixels{};

    heic_auxiliary_facts gainmap_facts{};

    const lpb_result gainmap_decoded = decode_heic_auxiliary_file(context, source_path,

        request.item_id, gainmap_pixels, &gainmap_facts,stored_geometry);

    if (gainmap_decoded != LPB_RESULT_OK) return gainmap_decoded;

    if (gainmap_facts.item_id != request.item_id || !safe_rgb_surface(gainmap_pixels, false) ||

        gainmap_pixels.width != (stored_geometry?gainmap_properties.width:gainmap_facts.width) ||

        gainmap_pixels.height != (stored_geometry?gainmap_properties.height:gainmap_facts.height)) {

        return fail(context, "Bound HEIF GainMap item did not decode to one representable 8-bit RGB image.");

    }

    if(stored_geometry&&(gainmap_pixels.width!=gainmap_properties.width||gainmap_pixels.height!=gainmap_properties.height))

        return fail(context,"Stored GainMap decode does not match its actual ispe geometry.");

    if (source_metadata.kind == gainmap_metadata_kind::apple) {

        std::vector<uint8_t> converted_gainmap(gainmap_pixels.pixels.size());

        if (!convert_apple_gainmap_rgb_to_iso(gainmap_pixels.pixels,

                source_metadata.apple_maker_note_33, source_metadata.apple_maker_note_48,

                converted_gainmap, target_metadata) || !valid_iso_metadata(target_metadata)) {

            return fail(context, "Apple GainMap samples or metadata cannot be represented by the frozen ISO mapping.");

        }

        gainmap_pixels.pixels = std::move(converted_gainmap);

    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED)

        return fail(context, "HDR/GainMap conversion cancelled before JPEG encoding.", LPB_RESULT_CANCELLED);



    std::vector<uint8_t> primary_jpeg;

    const lpb_result primary_encoded = encode_rgb_jpeg_bytes(context, primary_pixels.width,

        primary_pixels.height, primary_pixels.pixels, request.quality,

        primary_pixels.color.icc_profile, primary_jpeg);

    if (primary_encoded != LPB_RESULT_OK) return primary_encoded;

    std::vector<uint8_t> gainmap_jpeg;

    const lpb_result gainmap_encoded = encode_rgb_jpeg_bytes(context, gainmap_pixels.width,

        gainmap_pixels.height, gainmap_pixels.pixels, request.quality, {}, gainmap_jpeg);

    if (gainmap_encoded != LPB_RESULT_OK) return gainmap_encoded;

    std::string gainmap_xml;

    if (!create_gainmap_xmp(target_metadata, gainmap_xml))

        return fail(context, "Mapped ISO GainMap metadata cannot be serialized without loss.");

    std::vector<uint8_t> gainmap_with_xmp;

    if (!insert_xmp(gainmap_jpeg, gainmap_xml, gainmap_with_xmp))

        return fail(context, "Native could not structurally attach complete ISO metadata to the GainMap JPEG.", LPB_RESULT_INTERNAL_ERROR);

    std::vector<uint8_t> ordinary_padding;

    const auto sef_range=primary_properties.ordinary_sef_range;

    if(sef_range.length) {

        lpb::memory_random_access source_reader{neutral_bytes};lpb::protocols::sef_inventory inventory;

        std::string sef_error;

        if(sef_range.offset>neutral_bytes.size()||sef_range.length!=neutral_bytes.size()-sef_range.offset||

            !lpb::protocols::observe_sef(source_reader.view(),sef_range.offset,neutral_bytes.size(),inventory,sef_error)||

            inventory.begin!=sef_range.offset||

            !lpb::protocols::ordinary_sef(inventory))

            return fail(context,"HDR source SEF camera inventory is malformed or retains a live binding.");

        ordinary_padding.assign(neutral_bytes.begin()+static_cast<ptrdiff_t>(inventory.begin),neutral_bytes.end());

    } else if(neutral_bytes.size()>=4U&&std::memcmp(neutral_bytes.data()+neutral_bytes.size()-4U,"SEFT",4U)==0) {

        return fail(context,"HDR source SEF bytes have no validated final HEIF metadata box owner.");

    }

    std::string primary_xml;

    if (!create_primary_xmp(gainmap_with_xmp.size(), primary_xml))

        return fail(context, "GainMap JPEG length cannot be represented by the target container.");

    if(exact_metadata_geometry&&!merge_ordinary_primary_xmp(ordinary_primary_xmp,primary_xml,gainmap_with_xmp.size(),ordinary_padding.size()))

        return fail(context,"HEIC ordinary primary XMP cannot be merged without losing or shadowing source properties.");

    if(!ordinary_padding.empty()&&ordinary_primary_xmp.empty()) {

        std::string padded;xmp_observation_failure failure{};

        if(!rewrite_target_primary_padding(primary_xml,ordinary_padding.size(),padded,failure))

            return fail(context,"HDR ordinary metadata padding could not be bound in the generated directory.");

        primary_xml=std::move(padded);

    }

    if(exact_metadata_geometry) {

        std::vector<uint8_t> with_exif;

        if(!insert_exact_exif(primary_jpeg,primary_properties.exif_payload,with_exif,primary_properties.orientation))

            return fail(context,"HEIC ordinary Exif cannot be carried exactly in one JPEG APP1 segment.");

        primary_jpeg=std::move(with_exif);

    }

    std::vector<uint8_t> primary_with_xmp;

    if (!standalone_jpeg_codestream(primary_jpeg))

        return fail(context, "Native JPEG backend emitted a primary codestream that failed standalone marker validation.", LPB_RESULT_INTERNAL_ERROR);

    if (!insert_xmp(primary_jpeg, primary_xml, primary_with_xmp))

        return fail(context, "Native could not structurally attach the target GainMap directory to the primary JPEG.", LPB_RESULT_INTERNAL_ERROR);

    std::vector<uint8_t> primary_with_mpf;

    if (!append_mpf(primary_with_xmp, gainmap_with_xmp.size(), primary_with_mpf,ordinary_padding.size()))

        return fail(context, "Native could not encode the JPEG MPF relationship for the target GainMap.", LPB_RESULT_INTERNAL_ERROR);

    std::vector<uint8_t> output_bytes;

    try {

        output_bytes.reserve(primary_with_mpf.size() + ordinary_padding.size()+gainmap_with_xmp.size());

        output_bytes.insert(output_bytes.end(), primary_with_mpf.begin(), primary_with_mpf.end());

        output_bytes.insert(output_bytes.end(),ordinary_padding.begin(),ordinary_padding.end());

        output_bytes.insert(output_bytes.end(), gainmap_with_xmp.begin(), gainmap_with_xmp.end());

    } catch (...) {

        return fail(context, "Native could not allocate the complete JPEG Ultra HDR output.", LPB_RESULT_INTERNAL_ERROR);

    }

    size_t primary_size = 0;

    if (!verify_mpf_layout(output_bytes, gainmap_with_xmp.size(), primary_size,ordinary_padding.size()) ||

        primary_size != primary_with_mpf.size()) {

        return fail(context, "Native JPEG MPF/XMP target structure failed its pre-write validation.", LPB_RESULT_INTERNAL_ERROR);

    }

    if(!ordinary_padding.empty()) {

        lpb::memory_random_access converted_reader{output_bytes};lpb::protocols::sef_inventory converted_sef;std::string sef_error;

        if(!lpb::protocols::observe_sef(converted_reader.view(),primary_size,primary_size+ordinary_padding.size(),converted_sef,sef_error)||

            converted_sef.begin!=primary_size||!lpb::protocols::ordinary_sef(converted_sef)||

            !std::equal(ordinary_padding.begin(),ordinary_padding.end(),output_bytes.begin()+static_cast<ptrdiff_t>(primary_size)))

            return fail(context,"HDR converted SEF camera inventory does not exactly preserve its owned source bytes.");

    }



    auto staged = std::make_shared<hdr_gainmap_stage>();

    // Distinct approved scopes: addition-only kind2 versus existing validated

    // HEIF directory value mapping kind3. Never reinterpret the old proof.

    target_xmp_observation original_xmp_scope;xmp_observation_failure scope_error{};

    const bool addition_only=ordinary_primary_xmp.empty()||

        (observe_target_xmp(ordinary_primary_xmp,original_xmp_scope,scope_error)&&!original_xmp_scope.directory_present);

    staged->ordinary_primary_xmp_proof_kind=exact_metadata_geometry?(addition_only?2U:3U):0U;

    std::copy(std::begin(source_hash),std::end(source_hash),staged->metadata_source_sha256.begin());

    staged->destination = output_native;

    staged->target_semantic = LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP;

    staged->actual_container = LPB_IMAGE_CONTAINER_JPEG;

    if (!staged->output.create(output_native, L"lpb-hdr-jpeg", L".tmp", true) ||

        !staged->output.write_all(output_bytes) || !staged->output.ready_to_consume()) {

        return abort_and_fail(context, staged->output,

            "Native could not stage the complete HDR JPEG without publishing a partial target.", LPB_RESULT_INTERNAL_ERROR);

    }

    const std::string staged_path = path_to_utf8(staged->output.path());

    lpb_source_media_facts output_facts{};

    output_facts.struct_size = sizeof(output_facts);

    const lpb_result output_inspected = staged_path.empty()

        ? LPB_RESULT_INVALID_ARGUMENT : inspect_source(context, staged_path.c_str(), nullptr, &output_facts,

            nullptr, true);

    if (output_inspected != LPB_RESULT_OK) {

        std::string diagnostic = "Staged JPEG inspection failed: ";

        {

            std::scoped_lock lock(context->error_mutex);

            diagnostic += context->last_error;

        }

        return abort_and_fail(context, staged->output, diagnostic.c_str(), output_inspected);

    }

    if (output_facts.primary_image.container != LPB_IMAGE_CONTAINER_JPEG || !output_facts.gain_map.is_present ||

        output_facts.gain_map.container != LPB_IMAGE_CONTAINER_JPEG ||

        output_facts.gain_map.file_range.offset != primary_size+ordinary_padding.size() ||

        output_facts.gain_map.file_range.length != gainmap_with_xmp.size() ||

        output_facts.gain_map.auxiliary_index >= output_facts.auxiliary_count ||

        !valid_gainmap_graph(context, output_facts)) {

        const std::string diagnostic = "Staged JPEG item facts disagree with expected structure (primaryContainer=" +

            std::to_string(output_facts.primary_image.container) + ", gainMapPresent=" +

            std::to_string(output_facts.gain_map.is_present) + ", gainMapContainer=" +

            std::to_string(output_facts.gain_map.container) + ", gainMapOffset=" +

            std::to_string(output_facts.gain_map.file_range.offset) + ", expectedOffset=" +

            std::to_string(primary_size+ordinary_padding.size()) + ", gainMapLength=" +

            std::to_string(output_facts.gain_map.file_range.length) + ", expectedLength=" +

            std::to_string(gainmap_with_xmp.size()) + ", auxiliaryCount=" +

            std::to_string(output_facts.auxiliary_count) + ").";

        return abort_and_fail(context, staged->output, diagnostic.c_str(), LPB_RESULT_INTERNAL_ERROR);

    }

    const auto& output_auxiliary = output_facts.auxiliary_items[output_facts.gain_map.auxiliary_index];

    uint8_t expected_gainmap_hash[32]{};

    lpb::crypto::sha256_ctx map_sha;

    map_sha.update(gainmap_with_xmp.data(), gainmap_with_xmp.size());

    map_sha.finalize(expected_gainmap_hash);

    if (output_auxiliary.item_id != output_facts.gain_map.item_id ||

        std::string_view(output_auxiliary.semantic) != "GainMap" ||

        !same_hash(output_auxiliary.sha256, expected_gainmap_hash)) {

        return abort_and_fail(context, staged->output,

            "Staged JPEG GainMap bytes or item semantic changed during structural inspection.", LPB_RESULT_INTERNAL_ERROR);

    }

    gainmap_metadata_facts output_metadata{};

    const lpb_result output_metadata_result = inspect_gainmap_metadata(context,

        staged_path.c_str(), nullptr, output_metadata, true);

    if (output_metadata_result != LPB_RESULT_OK || !same_metadata(target_metadata, output_metadata)) {

        return abort_and_fail(context, staged->output,

            "Staged JPEG GainMap metadata failed complete Native re-read validation.",

            output_metadata_result == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : output_metadata_result);

    }

    uint8_t output_sha[32]{};

    uint8_t expected_output_hash[32]{};

    lpb::crypto::sha256_ctx output_sha_ctx;

    output_sha_ctx.update(output_bytes.data(), output_bytes.size());

    output_sha_ctx.finalize(expected_output_hash);

    if (!staged->output.ready_to_consume() || !staged->output.compute_sha256(output_sha) ||

        !same_hash(output_sha, expected_output_hash)) {

        return abort_and_fail(context, staged->output,

            "Staged JPEG bytes changed after Native structural validation.", LPB_RESULT_INTERNAL_ERROR);

    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {

        return abort_and_fail(context, staged->output,

            "[Cancelled] HDR/GainMap conversion cancelled before managed semantic post-validation.", LPB_RESULT_CANCELLED);

    }



    result.target_semantic = LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP;

    result.actual_container = LPB_IMAGE_CONTAINER_JPEG;

    result.gainmap_outcome = LPB_HDR_GAINMAP_OUTCOME_REENCODED;

    result.metadata_complete = 1;

    result.primary_width = primary_pixels.width;

    result.primary_height = primary_pixels.height;

    result.gainmap_width = gainmap_pixels.width;

    result.gainmap_height = gainmap_pixels.height;

    result.gainmap_range.offset = primary_size+ordinary_padding.size();

    result.gainmap_range.length = gainmap_with_xmp.size();

    std::copy(std::begin(output_sha), std::end(output_sha), std::begin(result.output_sha256));

    std::copy(std::begin(expected_gainmap_hash), std::end(expected_gainmap_hash), std::begin(result.gainmap_sha256));

    result.hdr_capacity_min = target_metadata.hdr_capacity_min;

    result.hdr_capacity_max = target_metadata.hdr_capacity_max;

    std::copy(target_metadata.gain_map_min.begin(), target_metadata.gain_map_min.end(), result.gain_map_min);

    std::copy(target_metadata.gain_map_max.begin(), target_metadata.gain_map_max.end(), result.gain_map_max);

    std::copy(target_metadata.gamma.begin(), target_metadata.gamma.end(), result.gamma);

    std::copy(target_metadata.offset_sdr.begin(), target_metadata.offset_sdr.end(), result.offset_sdr);

    std::copy(target_metadata.offset_hdr.begin(), target_metadata.offset_hdr.end(), result.offset_hdr);

    const std::string stable_staging_path = path_to_utf8(staged->output.path());

    if (stable_staging_path.empty() || stable_staging_path.size() >= sizeof(result.staging_path)) {

        return abort_and_fail(context, staged->output,

            "Native staging path exceeds the versioned conversion result capacity.", LPB_RESULT_INTERNAL_ERROR);

    }

    std::copy(std::begin(expected_output_hash), std::end(expected_output_hash), staged->expected_sha256.begin());

    if (!register_stage(context, staged, result.transaction_token)) {

        return abort_and_fail(context, staged->output,

            "Native could not register the owned HDR conversion stage for managed post-validation.", LPB_RESULT_INTERNAL_ERROR);

    }

    std::memcpy(result.staging_path, stable_staging_path.data(), stable_staging_path.size());

    return LPB_RESULT_OK;

}



lpb_result convert_jpeg_gainmap_to_heic_auxiliary(lpb_context* context,

    const char* source_path, const char* output_path,

    const lpb_hdr_gainmap_conversion_request_v1& request,

    lpb_hdr_gainmap_conversion_result_v1& result) {

    if (!context || !source_path || !output_path || source_path[0] == '\0' || output_path[0] == '\0' ||

        paths_alias(source_path, output_path) || request.struct_size != sizeof(request) || request.api_version != 1 ||

        request.source_container != LPB_IMAGE_CONTAINER_JPEG ||

        request.target_semantic != LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY ||

        request.quality < 1 || request.quality > 100 || request.hdr_output_policy < 0 || request.hdr_output_policy > 1 ||

        request.item_id == 0 || request.expected_file_size == 0 || request.reserved != 0 ||

        request.expected_link_count == 0 || !request_field_valid(request.stable_identity, sizeof(request.stable_identity)) ||

        !request_field_valid(request.inspected_stable_identity, sizeof(request.inspected_stable_identity)) ||

        !request_field_valid(request.semantic, sizeof(request.semantic)) ||

        !request_field_valid(request.owner_identity, sizeof(request.owner_identity)) ||

        !request_field_valid(request.inspected_owner_identity, sizeof(request.inspected_owner_identity)) ||

        !request_field_valid(request.relationship, sizeof(request.relationship)) ||

        request.source_range.length == 0 || request.source_range.offset > request.expected_file_size ||

        request.source_range.length > request.expected_file_size - request.source_range.offset) {

        return fail(context, "HDR/GainMap conversion request is malformed, unsupported, or does not identify a JPEG-to-HEIC semantic route.");

    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED)

        return fail(context, "HDR/GainMap conversion cancelled before source inspection.", LPB_RESULT_CANCELLED);



    const std::filesystem::path source_native = utf8_to_path(source_path);

    const std::filesystem::path output_native = utf8_to_path(output_path);

    if (source_native.empty() || output_native.empty()) return fail(context, "HDR/GainMap source or target path is not valid UTF-8.");

    unique_handle source_handle(CreateFileW(source_native.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,

        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr));

    if (source_handle.get() == INVALID_HANDLE_VALUE)

        return fail(context, "Could not lock the exact JPEG source for semantic conversion.", LPB_RESULT_INTERNAL_ERROR);

    lpb_file_identity source_identity{};

    std::wstring source_final_path;

    if (!capture_file_identity_from_handle(source_handle.get(), source_identity, source_final_path) ||

        !valid_file_identity(source_identity, request)) {

        return fail(context, "JPEG source filesystem identity changed after its GainMap binding was created.");

    }

    uint8_t source_hash[32]{};

    if (!lpb::crypto::sha256_file(source_handle.get(), source_hash) || !same_hash(source_hash, request.source_sha256))

        return fail(context, "JPEG source SHA-256 no longer matches the bound artifact.");



    lpb_source_media_facts source_facts{};

    source_facts.struct_size = sizeof(source_facts);

    const lpb_result inspected = inspect_source(context, source_path, nullptr, &source_facts);

    if (inspected != LPB_RESULT_OK) return inspected;

    if (!valid_gainmap_graph(context, source_facts) || source_facts.primary_image.container != LPB_IMAGE_CONTAINER_JPEG ||

        source_facts.gain_map.container != LPB_IMAGE_CONTAINER_JPEG || source_facts.auxiliary_count != 1 ||

        source_facts.gain_map.item_id != request.item_id || source_facts.gain_map.auxiliary_index != request.auxiliary_index ||

        source_facts.gain_map.file_range.offset != request.source_range.offset ||

        source_facts.gain_map.file_range.length != request.source_range.length ||

        source_facts.gain_map.representation != request.representation ||

        source_facts.gain_map.ownership != request.ownership ||

        source_facts.gain_map.owner_artifact_role != request.owner_artifact_role ||

        !same_hash(source_facts.primary_sha256, request.primary_sha256)) {

        return fail(context, "Fresh JPEG item graph no longer matches the bound primary and GainMap identity.");

    }

    const auto& source_gainmap = source_facts.auxiliary_items[source_facts.gain_map.auxiliary_index];

    if (!source_gainmap.is_present || source_gainmap.item_id != request.item_id ||

        source_gainmap.container != LPB_IMAGE_CONTAINER_JPEG || source_gainmap.codec != LPB_AUX_CODEC_JPEG ||

        source_gainmap.file_range.offset != request.source_range.offset ||

        source_gainmap.file_range.length != request.source_range.length ||

        source_gainmap.representation != request.representation || source_gainmap.ownership != request.ownership ||

        !same_hash(source_gainmap.sha256, request.gainmap_sha256) ||

        !fixed_field_equals(request.semantic, sizeof(request.semantic), source_gainmap.semantic, sizeof(source_gainmap.semantic)) ||

        !fixed_field_equals(request.inspected_stable_identity, sizeof(request.inspected_stable_identity),

            source_gainmap.stable_identity, sizeof(source_gainmap.stable_identity)) ||

        !fixed_field_equals(request.inspected_owner_identity, sizeof(request.inspected_owner_identity),

            source_gainmap.owner_identity, sizeof(source_gainmap.owner_identity)) ||

        !fixed_field_equals(request.relationship, sizeof(request.relationship),

            source_gainmap.relationship, sizeof(source_gainmap.relationship)) ||

        source_gainmap.representation != LPB_AUX_REPRESENTATION_EMBEDDED ||

        source_gainmap.ownership != LPB_AUX_OWNER_PRIMARY ||

        source_facts.gain_map.owner_artifact_role != LPB_ARTIFACT_PRIMARY_IMAGE) {

        return fail(context, "Fresh JPEG GainMap semantic, owner, relationship, representation, or byte hash is stale or unsupported.");

    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED)

        return fail(context, "HDR/GainMap conversion cancelled before source metadata inspection.", LPB_RESULT_CANCELLED);



    gainmap_metadata_facts source_metadata{};

    const lpb_result metadata_result = inspect_gainmap_metadata(context, source_path, nullptr, source_metadata);

    if (metadata_result != LPB_RESULT_OK) return metadata_result;

    if (source_metadata.kind != gainmap_metadata_kind::iso || !valid_iso_metadata(source_metadata.iso))

        return fail(context, "JPEG source lacks one complete supported ISO GainMap mapping.");

    const iso_gainmap_metadata target_metadata = source_metadata.iso;



    std::vector<uint8_t> source_bytes;

    if (!read_locked_file(source_handle.get(), source_identity.file_size, source_bytes))

        return fail(context, "Bound JPEG source bytes could not be read through the retained file identity.", LPB_RESULT_INTERNAL_ERROR);

    uint8_t locked_bytes_hash[32]{};

    lpb::crypto::sha256_buffer(source_bytes.data(), source_bytes.size(), locked_bytes_hash);

    if (!same_hash(locked_bytes_hash, request.source_sha256) ||

        request.source_range.offset > source_bytes.size() ||

        request.source_range.length > source_bytes.size() - request.source_range.offset) {

        return fail(context, "Locked JPEG bytes or GainMap range changed after source binding validation.");

    }

    uint8_t locked_gainmap_hash[32]{};

    lpb::crypto::sha256_buffer(source_bytes.data() + static_cast<size_t>(request.source_range.offset),

        static_cast<size_t>(request.source_range.length), locked_gainmap_hash);

    if (!same_hash(locked_gainmap_hash, request.gainmap_sha256))

        return fail(context, "Exact JPEG GainMap byte range no longer matches its bound SHA-256.");



    auto metadata_source=std::make_unique<windows_readonly_source>();

    lpb_file_identity metadata_source_identity{};

    if(!metadata_source->open(source_native)||!metadata_source->capture_identity(metadata_source_identity)||

        !valid_file_identity(metadata_source_identity,request)||metadata_source_identity.link_count!=1)

        return fail(context,"Complete HDR image metadata requires a pinned source and every ancestor.",LPB_RESULT_SOURCE_CHANGED);



    jpeg_rgb_image primary_jpeg{};

    const lpb_result primary_decoded = decode_jpeg_rgb_bytes(context, source_bytes, primary_jpeg);

    if (primary_decoded != LPB_RESULT_OK) return primary_decoded;

    if (source_facts.primary_image.file_range.offset != 0 ||

        source_facts.primary_image.file_range.length == 0 ||

        source_facts.primary_image.file_range.length > source_bytes.size()) {

        return fail(context, "JPEG Primary image range is missing or not rooted at the bound artifact.");

    }

    jpeg_rgb_image gainmap_jpeg{};

    const auto gainmap_span = std::span<const uint8_t>(source_bytes).subspan(

        static_cast<size_t>(request.source_range.offset), static_cast<size_t>(request.source_range.length));

    const lpb_result gainmap_decoded = decode_jpeg_rgb_bytes(context, gainmap_span, gainmap_jpeg);

    if (gainmap_decoded != LPB_RESULT_OK) return gainmap_decoded;

    target_primary_observation primary_observation;jpeg_observation gainmap_observation;

    hdr_image_metadata_inventory mapped_metadata;

    std::string image_error;

    if(!capture_hdr_jpeg_image_metadata(source_bytes,request.source_range,primary_jpeg,gainmap_jpeg,

        primary_observation,gainmap_observation,mapped_metadata,image_error))

        return fail(context,"[Unsupported] JPEG HDR ordinary metadata or container padding has no complete mapped image inventory.",LPB_RESULT_UNSUPPORTED);

    if(!map_hdr_image_exif(context,mapped_metadata.primary_exif,primary_observation))

        return fail(context,"[Unsupported] JPEG HDR primary Exif or thumbnail cannot be physically normalized.",LPB_RESULT_UNSUPPORTED);

    // A GainMap belongs to the same image coordinate system by the validated

    // ISO relationship. A separately declared contradictory transform is not

    // an instruction to rotate it twice.

    if(!gainmap_observation.exif_payload.empty()&&gainmap_observation.orientation!=primary_observation.orientation)

        return fail(context,"[Unsupported] JPEG HDR GainMap declares a conflicting image coordinate direction.",LPB_RESULT_UNSUPPORTED);

    target_primary_observation map_observation;map_observation.width=gainmap_jpeg.width;

    map_observation.height=gainmap_jpeg.height;map_observation.orientation=primary_observation.orientation;

    if(!mapped_metadata.gainmap_exif.empty()&&!map_hdr_image_exif(context,mapped_metadata.gainmap_exif,map_observation))

        return fail(context,"[Unsupported] JPEG HDR GainMap Exif or thumbnail cannot be physically normalized.",LPB_RESULT_UNSUPPORTED);

    std::string mapped_primary_xml;xmp_observation_failure xml_failure{};

    if(!rewrite_jpeg_gainmap_primary_xmp_to_heif(mapped_metadata.primary_xmp,request.source_range.length,

        primary_observation.xmp.items[0].padding,primary_observation.orientation,primary_observation.width,

        primary_observation.height,mapped_primary_xml,xml_failure))

        return fail(context,"[Unsupported] JPEG HDR primary metadata cannot map to the exact HEIF owner without loss.",LPB_RESULT_UNSUPPORTED);

    mapped_metadata.primary_xmp=std::move(mapped_primary_xml);

    std::string mapped_gainmap_xml;

    if(!rewrite_hdr_gainmap_owner_xmp(mapped_metadata.gainmap_xmp,primary_observation.orientation,

        gainmap_jpeg.width,gainmap_jpeg.height,mapped_gainmap_xml,xml_failure))

        return fail(context,"[Unsupported] JPEG HDR GainMap XML has an unmapped direction, geometry or active recipe.",LPB_RESULT_UNSUPPORTED);

    mapped_metadata.gainmap_xmp=std::move(mapped_gainmap_xml);

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED)

        return fail(context, "HDR/GainMap conversion cancelled before HEIF encoding.", LPB_RESULT_CANCELLED);



    pixel_surface primary_pixels{};

    primary_pixels.width = primary_jpeg.width;

    primary_pixels.height = primary_jpeg.height;

    primary_pixels.channels = 3;

    primary_pixels.signal_bit_depth = 8;

    primary_pixels.storage_bit_depth = 8;

    primary_pixels.stride = static_cast<uint64_t>(primary_pixels.width) * 3;

    primary_pixels.color.has_icc = !primary_jpeg.icc_profile.empty();

    primary_pixels.color.icc_profile = std::move(primary_jpeg.icc_profile);

    primary_pixels.pixels = std::move(primary_jpeg.pixels);

    pixel_surface gainmap_pixels{};

    gainmap_pixels.width = gainmap_jpeg.width;

    gainmap_pixels.height = gainmap_jpeg.height;

    gainmap_pixels.channels = 3;

    gainmap_pixels.signal_bit_depth = 8;

    gainmap_pixels.storage_bit_depth = 8;

    gainmap_pixels.stride = static_cast<uint64_t>(gainmap_pixels.width) * 3;

    gainmap_pixels.color.has_icc = !gainmap_jpeg.icc_profile.empty();

    gainmap_pixels.color.icc_profile = std::move(gainmap_jpeg.icc_profile);

    gainmap_pixels.pixels = std::move(gainmap_jpeg.pixels);

    if (!safe_rgb_surface(primary_pixels, false) || !safe_rgb_surface(gainmap_pixels, false))

        return fail(context, "Bound JPEG Primary or GainMap did not decode to a representable 8-bit RGB surface.");

    if(!normalize_pixel_orientation(primary_pixels,primary_observation.orientation)||

        !normalize_pixel_orientation(gainmap_pixels,primary_observation.orientation))

        return fail(context,"JPEG HDR primary or GainMap pixels could not be physically normalized.");



    std::vector<uint8_t> codec_heif;

    heic_encoded_image_facts encoded_facts{};

    const auto tiff=[](const auto& exif){return exif.empty()?std::span<const uint8_t>{}:std::span<const uint8_t>(exif).subspan(6);};

    const auto xml_bytes=[](const std::string& text){return std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(text.data()),text.size());};

    const lpb_result heic_encoded = encode_heic_primary_and_gainmap_metadata_bytes(context,

        primary_pixels,gainmap_pixels,request.quality,tiff(mapped_metadata.primary_exif),xml_bytes(mapped_metadata.primary_xmp),

        tiff(mapped_metadata.gainmap_exif),xml_bytes(mapped_metadata.gainmap_xmp),codec_heif,encoded_facts);

    if (heic_encoded != LPB_RESULT_OK) return heic_encoded;

    std::vector<uint8_t> output_bytes;

    std::string graph_error;

    if (!containers::attach_gainmap_auxiliary_graph(codec_heif, encoded_facts.primary_item_id,

            encoded_facts.secondary_item_id, output_bytes, graph_error)) {

        return fail(context, graph_error.empty() ? "Native HEIF GainMap graph assembly failed." : graph_error.c_str(),

            LPB_RESULT_INVALID_ARGUMENT);

    }



    if(!original_jpeg_metadata::attach(output_bytes,mapped_metadata.original_markers)||

        !original_jpeg_padding::attach(output_bytes,mapped_metadata.original_padding_record))

        return fail(context,"Complete HDR image original JPEG metadata context could not be attached.");

    if(!mapped_metadata.ordinary_sef.empty()) {

        const uint64_t size=mapped_metadata.ordinary_sef.size()+8;

        if(size>UINT32_MAX)return fail(context,"Ordinary SEF metadata is too large for its owned HEIF box.");

        for(unsigned i=0;i<4;++i)output_bytes.push_back(static_cast<uint8_t>(size>>((3-i)*8)));

        output_bytes.insert(output_bytes.end(),{'s','e','f','d'});

        output_bytes.insert(output_bytes.end(),mapped_metadata.ordinary_sef.begin(),mapped_metadata.ordinary_sef.end());

    }

    hdr_image_metadata_inventory actual_metadata;

    containers::heif_primary_properties actual_primary,actual_map;

    heic_image_facts actual_images;

    if(!containers::observe_item_properties(output_bytes,encoded_facts.primary_item_id,actual_primary,image_error)||

        !containers::observe_item_properties(output_bytes,encoded_facts.secondary_item_id,actual_map,image_error)||

        !actual_primary.stored_geometry_compatible||!actual_map.stored_geometry_compatible||

        actual_primary.orientation!=1||actual_map.orientation!=1||actual_primary.has_clean_aperture||actual_map.has_clean_aperture||

        actual_primary.width!=primary_pixels.width||actual_primary.height!=primary_pixels.height||

        actual_map.width!=gainmap_pixels.width||actual_map.height!=gainmap_pixels.height||

        !containers::read_image_item_metadata(output_bytes,encoded_facts.primary_item_id,actual_metadata.primary_exif,actual_metadata.primary_xmp,image_error)||

        !containers::read_image_item_metadata(output_bytes,encoded_facts.secondary_item_id,actual_metadata.gainmap_exif,actual_metadata.gainmap_xmp,image_error)||

        !original_jpeg_metadata::observe(output_bytes,actual_metadata.original_markers)||

        inspect_heic_bytes(context,output_bytes,actual_images)!=LPB_RESULT_OK||actual_images.auxiliaries.size()!=1||

        actual_images.auxiliaries[0].item_id!=encoded_facts.secondary_item_id||

        actual_images.color.has_nclx||actual_images.auxiliaries[0].color.has_nclx||

        validate_heic_bytes(context,output_bytes)!=LPB_RESULT_OK)

        return fail(context,"Complete HDR image owner, geometry, color or full decode validation failed.");

    actual_metadata.primary_icc=actual_images.color.icc_profile;

    actual_metadata.gainmap_icc=actual_images.auxiliaries[0].color.icc_profile;

    if(actual_primary.ordinary_sef_range.length) {

        const auto range=actual_primary.ordinary_sef_range;

        actual_metadata.ordinary_sef.assign(output_bytes.begin()+static_cast<ptrdiff_t>(range.offset),

            output_bytes.begin()+static_cast<ptrdiff_t>(range.offset+range.length));

    }

    uint8_t expected_metadata_hash[32]{},observed_metadata_hash[32]{};

    hdr_image_metadata_hash(mapped_metadata,expected_metadata_hash);hdr_image_metadata_hash(actual_metadata,observed_metadata_hash);

    if(!original_jpeg_padding::observe(output_bytes,actual_metadata.original_padding_record)||

        actual_metadata.original_padding_record!=mapped_metadata.original_padding_record)

        return fail(context,"Complete HDR image original source encoding padding was not retained exactly in its own context.");

    if(!same_hash(expected_metadata_hash,observed_metadata_hash))

        return fail(context,"Complete HDR image mapped metadata differs from the actual owner-bound output inventory.");



    auto staged = std::make_shared<hdr_gainmap_stage>();

    staged->metadata_source_lease=std::move(metadata_source);staged->metadata_source_path=source_native;

    staged->metadata_source_identity=metadata_source_identity;

    std::copy(std::begin(source_hash),std::end(source_hash),staged->metadata_source_sha256.begin());

    staged->destination = output_native;

    staged->target_semantic = LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY;

    staged->actual_container = LPB_IMAGE_CONTAINER_HEIC;

    if (!staged->output.create(output_native, L"lpb-hdr-heic", L".tmp", true) ||

        !staged->output.write_all(output_bytes) || !staged->output.ready_to_consume()) {

        return abort_and_fail(context, staged->output,

            "Native could not stage the complete HEIF GainMap without publishing a partial target.", LPB_RESULT_INTERNAL_ERROR);

    }

    const std::string staged_path = path_to_utf8(staged->output.path());

    if (staged_path.empty()) {

        return abort_and_fail(context, staged->output,

            "Native HEIF staging path is not valid UTF-8.", LPB_RESULT_INTERNAL_ERROR);

    }

    lpb_source_media_facts output_facts{};

    output_facts.struct_size = sizeof(output_facts);

    const lpb_result output_inspected = inspect_source(context, staged_path.c_str(), nullptr,

        &output_facts, nullptr, true);

    if (output_inspected != LPB_RESULT_OK) {

        std::string diagnostic = "Staged HEIF inspection failed: ";

        {

            std::scoped_lock lock(context->error_mutex);

            diagnostic += context->last_error;

        }

        return abort_and_fail(context, staged->output, diagnostic.c_str(), output_inspected);

    }

    if (output_facts.primary_image.container != LPB_IMAGE_CONTAINER_HEIC ||

        output_facts.gain_map.container != LPB_IMAGE_CONTAINER_HEIC || !output_facts.gain_map.is_present ||

        output_facts.gain_map.item_id != encoded_facts.secondary_item_id ||

        output_facts.auxiliary_count != 1 || output_facts.gain_map.auxiliary_index >= output_facts.auxiliary_count ||

        output_facts.gain_map.representation != LPB_AUX_REPRESENTATION_EMBEDDED ||

        output_facts.gain_map.ownership != LPB_AUX_OWNER_PRIMARY ||

        output_facts.gain_map.owner_artifact_role != LPB_ARTIFACT_PRIMARY_IMAGE ||

        !valid_gainmap_graph(context, output_facts)) {

        return abort_and_fail(context, staged->output,

            "Staged HEIF does not contain exactly one embedded Primary-owned ISO GainMap item.", LPB_RESULT_INTERNAL_ERROR);

    }

    const auto& output_gainmap = output_facts.auxiliary_items[output_facts.gain_map.auxiliary_index];

    const uint64_t map_offset = output_facts.gain_map.file_range.offset;

    const uint64_t map_length = output_facts.gain_map.file_range.length;

    if (output_gainmap.item_id != encoded_facts.secondary_item_id ||

        std::string_view(output_gainmap.relationship) != "urn:com:photo:aux:hdrgainmap" ||

        std::string_view(output_gainmap.semantic) != "GainMap" || map_length == 0 ||

        map_offset > output_bytes.size() || map_length > output_bytes.size() - map_offset) {

        return abort_and_fail(context, staged->output,

            "Staged HEIF GainMap item identity, relationship, or bounded byte range changed.", LPB_RESULT_INTERNAL_ERROR);

    }

    uint8_t expected_gainmap_hash[32]{};

    lpb::crypto::sha256_buffer(output_bytes.data() + static_cast<size_t>(map_offset),

        static_cast<size_t>(map_length), expected_gainmap_hash);

    if (!same_hash(output_gainmap.sha256, expected_gainmap_hash)) {

        return abort_and_fail(context, staged->output,

            "Staged HEIF GainMap bytes changed during graph inspection.", LPB_RESULT_INTERNAL_ERROR);

    }

    gainmap_metadata_facts output_metadata{};

    const lpb_result output_metadata_result = inspect_gainmap_metadata(context,

        staged_path.c_str(), nullptr, output_metadata, true);

    if (output_metadata_result != LPB_RESULT_OK || !same_metadata(target_metadata, output_metadata)) {

        return abort_and_fail(context, staged->output,

            "Staged HEIF GainMap metadata failed complete ISO relationship-bound re-read validation.",

            output_metadata_result == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : output_metadata_result);

    }

    uint8_t output_sha[32]{};

    uint8_t expected_output_hash[32]{};

    lpb::crypto::sha256_ctx output_sha_ctx;

    output_sha_ctx.update(output_bytes.data(), output_bytes.size());

    output_sha_ctx.finalize(expected_output_hash);

    if (!staged->output.ready_to_consume() || !staged->output.compute_sha256(output_sha) ||

        !same_hash(output_sha, expected_output_hash)) {

        return abort_and_fail(context, staged->output,

            "Staged HEIF bytes changed after Native graph and metadata validation.", LPB_RESULT_INTERNAL_ERROR);

    }

    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {

        return abort_and_fail(context, staged->output,

            "[Cancelled] HDR/GainMap conversion cancelled before managed semantic post-validation.", LPB_RESULT_CANCELLED);

    }



    result.target_semantic = LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY;

    auto& image_proof=staged->image_metadata_proof;

    image_proof.struct_size=sizeof(image_proof);image_proof.api_version=1;

    image_proof.source_length=source_bytes.size();image_proof.output_length=output_bytes.size();

    image_proof.source_orientation=primary_observation.orientation;image_proof.output_orientation=1;

    image_proof.operation=3;image_proof.metadata_status=1;image_proof.orientation_status=1;

    image_proof.icc_status=mapped_metadata.primary_icc.empty()&&mapped_metadata.gainmap_icc.empty()?2U:1U;

    image_proof.source_width=primary_observation.width;image_proof.source_height=primary_observation.height;

    image_proof.output_width=primary_pixels.width;image_proof.output_height=primary_pixels.height;

    for(const auto part:hdr_image_metadata_parts(mapped_metadata))if(!part.empty())++image_proof.metadata_count;

    std::memcpy(image_proof.source_sha256,source_hash,32);std::memcpy(image_proof.output_sha256,output_sha,32);

    std::memcpy(image_proof.mapped_source_metadata_sha256,expected_metadata_hash,32);

    std::memcpy(image_proof.output_metadata_sha256,observed_metadata_hash,32);

    if(!staged->output.capture_identity(image_proof.output_identity)||image_proof.output_identity.link_count!=1||

        image_proof.output_identity.file_size!=image_proof.output_length||

        !staged->metadata_source_lease->verify_same_identity(source_native))

        return abort_and_fail(context,staged->output,"Complete HDR image source or stage identity changed before proof.",LPB_RESULT_SOURCE_CHANGED);

    image_proof.proof_kind=2;image_proof.proof_flags=1;

    result.actual_container = LPB_IMAGE_CONTAINER_HEIC;

    result.gainmap_outcome = LPB_HDR_GAINMAP_OUTCOME_REENCODED;

    result.metadata_complete = 1;

    result.primary_width = encoded_facts.primary_width;

    result.primary_height = encoded_facts.primary_height;

    result.gainmap_width = encoded_facts.secondary_width;

    result.gainmap_height = encoded_facts.secondary_height;

    result.gainmap_range = output_facts.gain_map.file_range;

    std::copy(std::begin(output_sha), std::end(output_sha), std::begin(result.output_sha256));

    std::copy(std::begin(expected_gainmap_hash), std::end(expected_gainmap_hash), std::begin(result.gainmap_sha256));

    result.hdr_capacity_min = target_metadata.hdr_capacity_min;

    result.hdr_capacity_max = target_metadata.hdr_capacity_max;

    std::copy(target_metadata.gain_map_min.begin(), target_metadata.gain_map_min.end(), result.gain_map_min);

    std::copy(target_metadata.gain_map_max.begin(), target_metadata.gain_map_max.end(), result.gain_map_max);

    std::copy(target_metadata.gamma.begin(), target_metadata.gamma.end(), result.gamma);

    std::copy(target_metadata.offset_sdr.begin(), target_metadata.offset_sdr.end(), result.offset_sdr);

    std::copy(target_metadata.offset_hdr.begin(), target_metadata.offset_hdr.end(), result.offset_hdr);

    const std::string stable_staging_path = path_to_utf8(staged->output.path());

    if (stable_staging_path.empty() || stable_staging_path.size() >= sizeof(result.staging_path)) {

        return abort_and_fail(context, staged->output,

            "Native staging path exceeds the versioned conversion result capacity.", LPB_RESULT_INTERNAL_ERROR);

    }

    std::copy(std::begin(expected_output_hash), std::end(expected_output_hash), staged->expected_sha256.begin());

    if (!register_stage(context, staged, result.transaction_token)) {

        return abort_and_fail(context, staged->output,

            "Native could not register the owned HEIF GainMap stage for managed post-validation.", LPB_RESULT_INTERNAL_ERROR);

    }

    std::memcpy(result.staging_path, stable_staging_path.data(), stable_staging_path.size());

    return LPB_RESULT_OK;

}



lpb_result convert_bound_gainmap(lpb_context* context, const char* source_path, const char* output_path,

    const lpb_hdr_gainmap_conversion_request_v1& request,

    lpb_hdr_gainmap_conversion_result_v1& result) {

    if (request.source_container == LPB_IMAGE_CONTAINER_HEIC &&

        request.target_semantic == LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP) {

        return convert_heic_gainmap_to_jpeg_ultrahdr(context, source_path, output_path, request, result);

    }

    if (request.source_container == LPB_IMAGE_CONTAINER_JPEG &&

        request.target_semantic == LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY) {

        return convert_jpeg_gainmap_to_heic_auxiliary(context, source_path, output_path, request, result);

    }

    return fail(context, "HDR/GainMap source and target semantic pair is unsupported.");

}



} // namespace



extern "C" LPB_API lpb_result LPB_CALL lpb_stage_hdr_gainmap_v1(

    lpb_context* context, const char* source_path, const char* output_path,

    const lpb_hdr_gainmap_conversion_request_v1* request,

    lpb_hdr_gainmap_conversion_result_v1* out_result) {

    lpb_context_operation context_operation(context);

    if (!context_operation.acquired() || !request || !out_result ||

        out_result->struct_size != sizeof(*out_result) || out_result->api_version != 1) {

        if (context) set_error(context, "HDR/GainMap conversion v1 received incompatible arguments or result layout.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    const uint32_t struct_size = out_result->struct_size;

    const uint32_t api_version = out_result->api_version;

    std::memset(out_result, 0, sizeof(*out_result));

    out_result->struct_size = struct_size;

    out_result->api_version = api_version;

    try {

        return convert_bound_gainmap(context, source_path, output_path, *request, *out_result);

    } catch (const std::exception& ex) {

        set_error(context, ex.what());

        return LPB_RESULT_INTERNAL_ERROR;

    } catch (...) {

        set_error(context, "HDR/GainMap conversion failed without a typed diagnostic.");

        return LPB_RESULT_INTERNAL_ERROR;

    }

}



extern "C" LPB_API lpb_result LPB_CALL lpb_get_hdr_gainmap_metadata_proof_v2(

    lpb_context* context,uint64_t transaction_token,lpb_hdr_gainmap_metadata_proof_v2* proof) {

    static_assert(sizeof(lpb_hdr_gainmap_metadata_proof_v2)==80);

    lpb_context_operation operation(context);

    const bool compatible=proof&&proof->struct_size==sizeof(*proof)&&proof->api_version==2;

    if(proof&&proof->struct_size==sizeof(*proof)){*proof={};proof->struct_size=sizeof(*proof);proof->api_version=2;}

    if(!operation.acquired()||!transaction_token||!compatible)

        return fail(context,"HDR metadata proof requires a live context/token and exact v2 layout.");

    try {

        std::scoped_lock lock(context->hdr_gainmap_mutex);

        const auto found=std::find_if(context->hdr_gainmap_stages.begin(),context->hdr_gainmap_stages.end(),

            [transaction_token](const auto& entry){return entry.first==transaction_token;});

        if(found==context->hdr_gainmap_stages.end())return fail(context,"HDR metadata proof token is stale or belongs to another context.");

        auto stage=std::static_pointer_cast<hdr_gainmap_stage>(found->second);

        uint8_t actual[32]{};

        if(!stage||!stage->output.ready_to_consume()||!stage->output.compute_sha256(actual)||

            !same_hash(actual,stage->expected_sha256.data()))return fail(context,"HDR metadata proof stage identity changed.");

        if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;

        if(stage->ordinary_primary_xmp_proof_kind&&stage->actual_container==LPB_IMAGE_CONTAINER_JPEG) {

            proof->proof_kind=stage->ordinary_primary_xmp_proof_kind;proof->proof_flags=1;

            std::copy(stage->metadata_source_sha256.begin(),stage->metadata_source_sha256.end(),proof->source_file_sha256);

            std::copy(std::begin(actual),std::end(actual),proof->output_file_sha256);

        }

        return LPB_RESULT_OK;

    }catch(...){return fail(context,"HDR metadata proof could not complete.",LPB_RESULT_INTERNAL_ERROR);}

}



extern "C" LPB_API lpb_result LPB_CALL lpb_get_hdr_image_conversion_proof_v1(

    lpb_context* context,uint64_t transaction_token,lpb_image_conversion_proof_v1* proof) {

    if(!proof)return LPB_RESULT_INVALID_ARGUMENT;

    const auto capacity=proof->struct_size;const auto version=capacity>=8?proof->api_version:0U;

    std::memset(proof,0,std::min<size_t>(capacity,sizeof(*proof)));

    if(capacity>=4)proof->struct_size=capacity;if(capacity>=8)proof->api_version=version;

    if(capacity!=sizeof(*proof)||version!=1)return LPB_RESULT_INVALID_ARGUMENT;

    lpb_context_operation operation(context);

    if(!operation.acquired()||!transaction_token)return fail(context,"Complete HDR image proof requires a live context and stage token.");

    if(lpb_context_check_cancelled(context)!=LPB_RESULT_OK)return LPB_RESULT_CANCELLED;

    try {

        std::scoped_lock lock(context->hdr_gainmap_mutex);

        const auto found=std::find_if(context->hdr_gainmap_stages.begin(),context->hdr_gainmap_stages.end(),

            [transaction_token](const auto& entry){return entry.first==transaction_token;});

        if(found==context->hdr_gainmap_stages.end())return fail(context,"Complete HDR image proof token is stale, finalized or belongs to another context.");

        auto stage=std::static_pointer_cast<hdr_gainmap_stage>(found->second);

        if(!stage||stage->image_metadata_proof.proof_kind!=2||stage->image_metadata_proof.proof_flags!=1)

            return fail(context,"This HDR stage has no complete mapped image inventory proof.",LPB_RESULT_UNSUPPORTED);

        lpb_file_identity source{},output{};uint8_t actual[32]{},actual_source[32]{};

        const auto& expected=stage->image_metadata_proof;

        const bool swap=expected.source_orientation>=5;

        if(stage->actual_container!=LPB_IMAGE_CONTAINER_HEIC||stage->target_semantic!=LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY||

            expected.struct_size!=sizeof(expected)||expected.api_version!=1||expected.operation!=3||expected.reserved||

            !expected.metadata_count||expected.metadata_count>8||expected.metadata_status!=1||

            (expected.icc_status!=1&&expected.icc_status!=2)||expected.orientation_status!=1||

            expected.source_orientation<1||expected.source_orientation>8||expected.output_orientation!=1||

            !expected.source_width||!expected.source_height||

            expected.output_width!=(swap?expected.source_height:expected.source_width)||

            expected.output_height!=(swap?expected.source_width:expected.source_height)||

            !same_hash(expected.mapped_source_metadata_sha256,expected.output_metadata_sha256))

            return fail(context,"Complete HDR image proof has an invalid version, scope, inventory or physical geometry.");

        if(!stage->metadata_source_lease||!stage->metadata_source_lease->capture_identity(source)||

            source.volume_serial!=stage->metadata_source_identity.volume_serial||source.file_index!=stage->metadata_source_identity.file_index||

            source.file_size!=expected.source_length||source.link_count!=1||

            !stage->metadata_source_lease->verify_same_identity(stage->metadata_source_path)||

            !lpb::crypto::sha256_file(stage->metadata_source_lease->handle(),actual_source)||

            !same_hash(actual_source,expected.source_sha256)||!same_hash(actual_source,stage->metadata_source_sha256.data())||

            !stage->output.ready_to_consume()||!stage->output.capture_identity(output)||!stage->output.compute_sha256(actual)||

            !same_hash(actual,stage->expected_sha256.data())||!same_hash(actual,expected.output_sha256)||

            output.volume_serial!=expected.output_identity.volume_serial||output.file_index!=expected.output_identity.file_index||

            output.file_size!=expected.output_length||output.link_count!=1)

            return fail(context,"Complete HDR image proof source or creating-handle stage identity changed.",LPB_RESULT_SOURCE_CHANGED);

        *proof=expected;return LPB_RESULT_OK;

    }catch(...){return fail(context,"Complete HDR image metadata proof could not complete.",LPB_RESULT_INTERNAL_ERROR);}

}



extern "C" LPB_API lpb_result LPB_CALL lpb_inspect_hdr_gainmap_stage_v1(

    lpb_context* context, uint64_t transaction_token,

    lpb_source_media_facts* out_facts, lpb_gainmap_metadata_v1* out_metadata,

    lpb_preservation_observation* out_preservation) {

    lpb_context_operation context_operation(context);

    if (!context_operation.acquired() || transaction_token == 0 || !out_facts || !out_metadata || !out_preservation ||

        out_facts->struct_size < sizeof(*out_facts) || out_metadata->struct_size < sizeof(*out_metadata) ||

        out_metadata->api_version != 1 || out_preservation->struct_size < sizeof(*out_preservation)) {

        if (context) set_error(context, "HDR/GainMap stage inspection received incompatible arguments or output layouts.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    try {

        std::scoped_lock lock(context->hdr_gainmap_mutex);

        const auto found = std::find_if(context->hdr_gainmap_stages.begin(), context->hdr_gainmap_stages.end(),

            [transaction_token](const auto& entry) { return entry.first == transaction_token; });

        if (found == context->hdr_gainmap_stages.end())

            return fail(context, "HDR/GainMap stage inspection token is stale, replayed, or belongs to another Native context.");

        auto stage = std::static_pointer_cast<hdr_gainmap_stage>(found->second);

        uint8_t stage_hash[32]{};

        if (!stage || !stage->output.ready_to_consume() || !stage->output.compute_sha256(stage_hash) ||

            !same_hash(stage_hash, stage->expected_sha256.data()))

            return fail(context, "HDR/GainMap stage changed before token-authorized managed post-validation.", LPB_RESULT_INVALID_ARGUMENT);



        const uint32_t facts_size = out_facts->struct_size;

        const uint32_t metadata_size = out_metadata->struct_size;

        const uint32_t metadata_version = out_metadata->api_version;

        const uint32_t preservation_size = out_preservation->struct_size;

        std::memset(out_facts, 0, sizeof(*out_facts));

        out_facts->struct_size = facts_size;

        std::memset(out_metadata, 0, sizeof(*out_metadata));

        out_metadata->struct_size = metadata_size;

        out_metadata->api_version = metadata_version;

        std::memset(out_preservation, 0, sizeof(*out_preservation));

        out_preservation->struct_size = preservation_size;



        const std::string stage_path = path_to_utf8(stage->output.path());

        if (stage_path.empty()) return fail(context, "HDR/GainMap stage path is not valid UTF-8.", LPB_RESULT_INTERNAL_ERROR);

        const lpb_result inspected = inspect_source(context, stage_path.c_str(), nullptr, out_facts, nullptr, true);

        if (inspected != LPB_RESULT_OK) return inspected;

        if(stage->image_metadata_proof.proof_kind==2) {

            // The general compatibility source projection leaves dimensions

            // unset. Observe this exact owned HEIF's codec/item facts instead

            // of substituting a zero or copying the declared proof dimensions.

            const auto reader=stage->output.reader();std::vector<uint8_t> actual;

            if(!reader.length||reader.length>512ULL*1024*1024)

                return fail(context,"Owned HDR HEIF size cannot be observed within its explicit bound.");

            actual.resize(static_cast<size_t>(reader.length));heic_image_facts pixels;

            if(!reader.read_exact(0,actual)||inspect_heic_bytes(context,actual,pixels)!=LPB_RESULT_OK||

                !pixels.width||!pixels.height||pixels.auxiliaries.size()!=1||

                pixels.auxiliaries[0].item_id!=out_facts->gain_map.item_id)

                return fail(context,"Owned HDR HEIF did not provide actual primary/GainMap pixel geometry.");

            out_facts->primary_image.width=pixels.width;out_facts->primary_image.height=pixels.height;

        }

        if (stage->actual_container == LPB_IMAGE_CONTAINER_UNKNOWN ||

            (stage->actual_container != LPB_IMAGE_CONTAINER_JPEG && stage->actual_container != LPB_IMAGE_CONTAINER_HEIC) ||

            out_facts->primary_image.container != stage->actual_container ||

            !out_facts->gain_map.is_present || out_facts->gain_map.container != stage->actual_container ||

            out_facts->gain_map.auxiliary_index >= out_facts->auxiliary_count ||

            !valid_gainmap_graph(context, *out_facts) || !same_hash(out_facts->primary_sha256, stage_hash))

            return fail(context, "Token-authorized stage inspection did not return the exact target Primary + GainMap graph.",

                LPB_RESULT_INTERNAL_ERROR);

        if ((stage->target_semantic == LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP &&

                stage->actual_container != LPB_IMAGE_CONTAINER_JPEG) ||

            (stage->target_semantic == LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY &&

                (stage->actual_container != LPB_IMAGE_CONTAINER_HEIC ||

                 std::string_view(out_facts->gain_map.relationship) != "urn:com:photo:aux:hdrgainmap")))

            return fail(context, "Token-authorized stage inspection found a target semantic/container mismatch.",

                LPB_RESULT_INTERNAL_ERROR);



        gainmap_metadata_facts metadata{};

        const lpb_result metadata_result = inspect_gainmap_metadata(context, stage_path.c_str(), nullptr, metadata, true);

        if (metadata_result != LPB_RESULT_OK) return metadata_result;

        if (metadata.kind != gainmap_metadata_kind::iso || !valid_iso_metadata(metadata.iso))

            return fail(context, "Token-authorized stage inspection found incomplete ISO GainMap metadata.", LPB_RESULT_INTERNAL_ERROR);



        const lpb_result preservation_result = lpb_media_capture_preservation_observation(

            context, stage_path.c_str(), LPB_SOURCE_PROTOCOL_UNKNOWN, stage->actual_container,

            out_preservation, true);

        if (preservation_result != LPB_RESULT_OK) return preservation_result;



        uint8_t after_hash[32]{};

        if (!stage->output.ready_to_consume() || !stage->output.compute_sha256(after_hash) ||

            !same_hash(after_hash, stage_hash))

            return fail(context, "HDR/GainMap stage changed during managed post-validation.", LPB_RESULT_INVALID_ARGUMENT);



        out_metadata->kind = LPB_GAINMAP_METADATA_ISO;

        std::copy(metadata.iso.gain_map_min.begin(), metadata.iso.gain_map_min.end(), out_metadata->gain_map_min);

        std::copy(metadata.iso.gain_map_max.begin(), metadata.iso.gain_map_max.end(), out_metadata->gain_map_max);

        std::copy(metadata.iso.gamma.begin(), metadata.iso.gamma.end(), out_metadata->gamma);

        std::copy(metadata.iso.offset_sdr.begin(), metadata.iso.offset_sdr.end(), out_metadata->offset_sdr);

        std::copy(metadata.iso.offset_hdr.begin(), metadata.iso.offset_hdr.end(), out_metadata->offset_hdr);

        out_metadata->hdr_capacity_min = metadata.iso.hdr_capacity_min;

        out_metadata->hdr_capacity_max = metadata.iso.hdr_capacity_max;

        out_metadata->base_rendition_is_hdr = metadata.iso.base_rendition_is_hdr ? 1 : 0;

        return LPB_RESULT_OK;

    } catch (const std::exception& ex) {

        set_error(context, ex.what());

        return LPB_RESULT_INTERNAL_ERROR;

    } catch (...) {

        set_error(context, "HDR/GainMap stage inspection failed without a typed diagnostic.");

        return LPB_RESULT_INTERNAL_ERROR;

    }

}



extern "C" LPB_API lpb_result LPB_CALL lpb_commit_hdr_gainmap_v1(

    lpb_context* context, uint64_t transaction_token) {

    lpb_context_operation context_operation(context);

    if (!context_operation.acquired() || transaction_token == 0) {

        if (context) set_error(context, "HDR/GainMap commit received an invalid context or transaction token.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    std::scoped_lock lock(context->hdr_gainmap_mutex);

    const auto found = std::find_if(context->hdr_gainmap_stages.begin(), context->hdr_gainmap_stages.end(),

        [transaction_token](const auto& entry) { return entry.first == transaction_token; });

    if (found == context->hdr_gainmap_stages.end())

        return fail(context, "HDR/GainMap stage token is stale, replayed, or belongs to another Native context.");

    auto stage = std::static_pointer_cast<hdr_gainmap_stage>(found->second);

    uint8_t current_hash[32]{};

    if (!stage || !stage->output.ready_to_consume() || !stage->output.compute_sha256(current_hash) ||

        !same_hash(current_hash, stage->expected_sha256.data()))

        return fail(context, "HDR/GainMap staged artifact changed after managed post-validation.", LPB_RESULT_INVALID_ARGUMENT);

    if(stage->image_metadata_proof.proof_kind==2) {

        lpb_file_identity source{},output{};uint8_t source_hash[32]{};

        const auto& expected=stage->image_metadata_proof;

        if(!stage->metadata_source_lease||!stage->metadata_source_lease->capture_identity(source)||source.link_count!=1||

            source.volume_serial!=stage->metadata_source_identity.volume_serial||source.file_index!=stage->metadata_source_identity.file_index||

            source.file_size!=expected.source_length||!stage->metadata_source_lease->verify_same_identity(stage->metadata_source_path)||

            !lpb::crypto::sha256_file(stage->metadata_source_lease->handle(),source_hash)||!same_hash(source_hash,expected.source_sha256)||

            !same_hash(source_hash,stage->metadata_source_sha256.data())||!stage->output.capture_identity(output)||output.link_count!=1||

            output.volume_serial!=expected.output_identity.volume_serial||output.file_index!=expected.output_identity.file_index||

            output.file_size!=expected.output_length||!same_hash(current_hash,expected.output_sha256))

            return fail(context,"Complete HDR image proof source or creating output identity/bytes changed before commit.",LPB_RESULT_SOURCE_CHANGED);

    }

    const lpb_result cancellation = lpb_context_check_cancelled(context);

    if (cancellation != LPB_RESULT_OK)

        return fail(context, "HDR/GainMap conversion cancelled before final publication.", cancellation);

    if (!stage->output.publish_registered_no_replace(

            context, LPB_ARTIFACT_PRIMARY_IMAGE, stage->destination))

        return fail(context, "Native no-replace commit failed for the fully validated HDR/GainMap artifact.", LPB_RESULT_INTERNAL_ERROR);

    context->hdr_gainmap_stages.erase(found);

    return LPB_RESULT_OK;

}



extern "C" LPB_API lpb_result LPB_CALL lpb_abort_hdr_gainmap_v1(

    lpb_context* context, uint64_t transaction_token) {

    lpb_context_operation context_operation(context);

    if (!context_operation.acquired() || transaction_token == 0) {

        if (context) set_error(context, "HDR/GainMap abort received an invalid context or transaction token.");

        return LPB_RESULT_INVALID_ARGUMENT;

    }

    std::scoped_lock lock(context->hdr_gainmap_mutex);

    const auto found = std::find_if(context->hdr_gainmap_stages.begin(), context->hdr_gainmap_stages.end(),

        [transaction_token](const auto& entry) { return entry.first == transaction_token; });

    if (found == context->hdr_gainmap_stages.end())

        return fail(context, "HDR/GainMap stage token is stale, replayed, or belongs to another Native context.");

    auto stage = std::static_pointer_cast<hdr_gainmap_stage>(found->second);

    if (!stage)

        return fail(context, "Native could not confirm cleanup of the exact owned HDR/GainMap stage.", LPB_RESULT_INTERNAL_ERROR);

    if (!abort_owned_output_and_report(context, stage->output,

            "HDR/GainMap stage abort was requested.", false)) {

        return LPB_RESULT_INTERNAL_ERROR;

    }

    context->hdr_gainmap_stages.erase(found);

    return LPB_RESULT_OK;

}
