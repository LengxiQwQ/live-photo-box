#include "media/heic_backend.h"
#include "media/jpeg_backend.h"
#include "media/media_inspector.h"
#include "containers/heif_internal.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
#include "platform/windows_filesystem.h"

#include <algorithm>
#include <array>
#include <bcrypt.h>
#include <charconv>
#include <cmath>
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

bool standalone_jpeg_codestream(std::span<const uint8_t> bytes) noexcept {
    if (bytes.size() < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
    size_t cursor = 2;
    bool saw_scan = false;
    while (cursor < bytes.size()) {
        if (bytes[cursor] != 0xFF) return false;
        while (cursor < bytes.size() && bytes[cursor] == 0xFF) ++cursor;
        if (cursor >= bytes.size()) return false;
        const uint8_t marker = bytes[cursor++];
        if (marker == 0xD9) return !saw_scan && cursor == bytes.size();
        if (marker == 0x00 || marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
            return false;
        if (cursor + 2 > bytes.size()) return false;
        const size_t segment_length = (static_cast<size_t>(bytes[cursor]) << 8) | bytes[cursor + 1];
        if (segment_length < 2 || segment_length > bytes.size() - cursor) return false;
        cursor += segment_length;
        if (marker != 0xDA) continue;
        if (saw_scan) return false;
        saw_scan = true;
        while (cursor + 1 < bytes.size()) {
            if (bytes[cursor] != 0xFF) { ++cursor; continue; }
            size_t marker_start = cursor++;
            while (cursor < bytes.size() && bytes[cursor] == 0xFF) ++cursor;
            if (cursor >= bytes.size()) return false;
            const uint8_t scan_marker = bytes[cursor++];
            if (scan_marker == 0x00 || (scan_marker >= 0xD0 && scan_marker <= 0xD7)) continue;
            if (scan_marker == 0xD9) return cursor == bytes.size();
            static_cast<void>(marker_start);
            return false; // The frozen encoder emits one baseline scan.
        }
        return false;
    }
    return false;
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
    std::vector<uint8_t>& output) {
    output.clear();
    if (!standalone_jpeg_codestream(primary) || primary.size() > std::numeric_limits<uint32_t>::max() - kMpfSegmentSize ||
        gainmap_size == 0 || gainmap_size > std::numeric_limits<uint32_t>::max()) return false;
    const uint32_t primary_size = static_cast<uint32_t>(primary.size() + kMpfSegmentSize);
    if (primary_size < kMpfTiffOffset || primary_size - kMpfTiffOffset > std::numeric_limits<uint32_t>::max()) return false;
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
        write_be32(mp_entry + 8, primary_size - static_cast<uint32_t>(kMpfTiffOffset));
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
    size_t& primary_size) noexcept {
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
        second_offset != first_size - static_cast<uint32_t>(kMpfTiffOffset) ||
        read_be16(mp_entry + 12) != 0 || read_be16(mp_entry + 14) != 0 ||
        first_size > combined.size() || second_size > combined.size() - first_size ||
        first_size + second_size != combined.size() ||
        !standalone_jpeg_codestream(combined.first(first_size)) ||
        !standalone_jpeg_codestream(combined.subspan(first_size, second_size))) return false;
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

    heic_image_facts heic_facts{};
    pixel_surface primary_pixels{};
    const lpb_result primary_decoded = decode_heic_primary_file(context, source_path, primary_pixels, &heic_facts);
    if (primary_decoded != LPB_RESULT_OK) return primary_decoded;
    if (heic_facts.has_alpha || !safe_rgb_surface(primary_pixels, true) ||
        primary_pixels.width != heic_facts.width || primary_pixels.height != heic_facts.height) {
        return fail(context, "HEIC primary rendition is not an ICC-bound 8-bit RGB image representable by the JPEG target.");
    }
    pixel_surface gainmap_pixels{};
    heic_auxiliary_facts gainmap_facts{};
    const lpb_result gainmap_decoded = decode_heic_auxiliary_file(context, source_path,
        request.item_id, gainmap_pixels, &gainmap_facts);
    if (gainmap_decoded != LPB_RESULT_OK) return gainmap_decoded;
    if (gainmap_facts.item_id != request.item_id || !safe_rgb_surface(gainmap_pixels, false) ||
        gainmap_pixels.width != gainmap_facts.width || gainmap_pixels.height != gainmap_facts.height) {
        return fail(context, "Bound HEIF GainMap item did not decode to one representable 8-bit RGB image.");
    }
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
    std::string primary_xml;
    if (!create_primary_xmp(gainmap_with_xmp.size(), primary_xml))
        return fail(context, "GainMap JPEG length cannot be represented by the target container.");
    std::vector<uint8_t> primary_with_xmp;
    if (!standalone_jpeg_codestream(primary_jpeg))
        return fail(context, "Native JPEG backend emitted a primary codestream that failed standalone marker validation.", LPB_RESULT_INTERNAL_ERROR);
    if (!insert_xmp(primary_jpeg, primary_xml, primary_with_xmp))
        return fail(context, "Native could not structurally attach the target GainMap directory to the primary JPEG.", LPB_RESULT_INTERNAL_ERROR);
    std::vector<uint8_t> primary_with_mpf;
    if (!append_mpf(primary_with_xmp, gainmap_with_xmp.size(), primary_with_mpf))
        return fail(context, "Native could not encode the JPEG MPF relationship for the target GainMap.", LPB_RESULT_INTERNAL_ERROR);
    std::vector<uint8_t> output_bytes;
    try {
        output_bytes.reserve(primary_with_mpf.size() + gainmap_with_xmp.size());
        output_bytes.insert(output_bytes.end(), primary_with_mpf.begin(), primary_with_mpf.end());
        output_bytes.insert(output_bytes.end(), gainmap_with_xmp.begin(), gainmap_with_xmp.end());
    } catch (...) {
        return fail(context, "Native could not allocate the complete JPEG Ultra HDR output.", LPB_RESULT_INTERNAL_ERROR);
    }
    size_t primary_size = 0;
    if (!verify_mpf_layout(output_bytes, gainmap_with_xmp.size(), primary_size) ||
        primary_size != primary_with_mpf.size()) {
        return fail(context, "Native JPEG MPF/XMP target structure failed its pre-write validation.", LPB_RESULT_INTERNAL_ERROR);
    }

    auto staged = std::make_shared<hdr_gainmap_stage>();
    staged->destination = output_native;
    staged->target_semantic = LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP;
    staged->actual_container = LPB_IMAGE_CONTAINER_JPEG;
    if (!staged->output.create(output_native, L"lpb-hdr-jpeg", L".tmp", true) ||
        !staged->output.write_all(output_bytes) || !staged->output.ready_to_consume()) {
        staged->output.abort();
        return fail(context, "Native could not stage the complete HDR JPEG without publishing a partial target.", LPB_RESULT_INTERNAL_ERROR);
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
        staged->output.abort();
        return fail(context, diagnostic.c_str(), output_inspected);
    }
    if (output_facts.primary_image.container != LPB_IMAGE_CONTAINER_JPEG || !output_facts.gain_map.is_present ||
        output_facts.gain_map.container != LPB_IMAGE_CONTAINER_JPEG ||
        output_facts.gain_map.file_range.offset != primary_size ||
        output_facts.gain_map.file_range.length != gainmap_with_xmp.size() ||
        output_facts.gain_map.auxiliary_index >= output_facts.auxiliary_count ||
        !valid_gainmap_graph(context, output_facts)) {
        const std::string diagnostic = "Staged JPEG item facts disagree with expected structure (primaryContainer=" +
            std::to_string(output_facts.primary_image.container) + ", gainMapPresent=" +
            std::to_string(output_facts.gain_map.is_present) + ", gainMapContainer=" +
            std::to_string(output_facts.gain_map.container) + ", gainMapOffset=" +
            std::to_string(output_facts.gain_map.file_range.offset) + ", expectedOffset=" +
            std::to_string(primary_size) + ", gainMapLength=" +
            std::to_string(output_facts.gain_map.file_range.length) + ", expectedLength=" +
            std::to_string(gainmap_with_xmp.size()) + ", auxiliaryCount=" +
            std::to_string(output_facts.auxiliary_count) + ").";
        staged->output.abort();
        return fail(context, diagnostic.c_str(), LPB_RESULT_INTERNAL_ERROR);
    }
    const auto& output_auxiliary = output_facts.auxiliary_items[output_facts.gain_map.auxiliary_index];
    uint8_t expected_gainmap_hash[32]{};
    lpb::crypto::sha256_ctx map_sha;
    map_sha.update(gainmap_with_xmp.data(), gainmap_with_xmp.size());
    map_sha.finalize(expected_gainmap_hash);
    if (output_auxiliary.item_id != output_facts.gain_map.item_id ||
        std::string_view(output_auxiliary.semantic) != "GainMap" ||
        !same_hash(output_auxiliary.sha256, expected_gainmap_hash)) {
        staged->output.abort();
        return fail(context, "Staged JPEG GainMap bytes or item semantic changed during structural inspection.", LPB_RESULT_INTERNAL_ERROR);
    }
    gainmap_metadata_facts output_metadata{};
    const lpb_result output_metadata_result = inspect_gainmap_metadata(context,
        staged_path.c_str(), nullptr, output_metadata, true);
    if (output_metadata_result != LPB_RESULT_OK || !same_metadata(target_metadata, output_metadata)) {
        staged->output.abort();
        return fail(context, "Staged JPEG GainMap metadata failed complete Native re-read validation.",
            output_metadata_result == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : output_metadata_result);
    }
    uint8_t output_sha[32]{};
    uint8_t expected_output_hash[32]{};
    lpb::crypto::sha256_ctx output_sha_ctx;
    output_sha_ctx.update(output_bytes.data(), output_bytes.size());
    output_sha_ctx.finalize(expected_output_hash);
    if (!staged->output.ready_to_consume() || !staged->output.compute_sha256(output_sha) ||
        !same_hash(output_sha, expected_output_hash)) {
        staged->output.abort();
        return fail(context, "Staged JPEG bytes changed after Native structural validation.", LPB_RESULT_INTERNAL_ERROR);
    }
    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
        staged->output.abort();
        return fail(context, "HDR/GainMap conversion cancelled before managed semantic post-validation.", LPB_RESULT_CANCELLED);
    }

    result.target_semantic = LPB_HDR_GAINMAP_TARGET_JPEG_ISO_GAINMAP;
    result.actual_container = LPB_IMAGE_CONTAINER_JPEG;
    result.gainmap_outcome = LPB_HDR_GAINMAP_OUTCOME_REENCODED;
    result.metadata_complete = 1;
    result.primary_width = primary_pixels.width;
    result.primary_height = primary_pixels.height;
    result.gainmap_width = gainmap_pixels.width;
    result.gainmap_height = gainmap_pixels.height;
    result.gainmap_range.offset = primary_size;
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
        staged->output.abort();
        return fail(context, "Native staging path exceeds the versioned conversion result capacity.", LPB_RESULT_INTERNAL_ERROR);
    }
    std::copy(std::begin(expected_output_hash), std::end(expected_output_hash), staged->expected_sha256.begin());
    if (!register_stage(context, staged, result.transaction_token)) {
        staged->output.abort();
        return fail(context, "Native could not register the owned HDR conversion stage for managed post-validation.", LPB_RESULT_INTERNAL_ERROR);
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

    std::string gainmap_xml;
    if (!create_gainmap_xmp(target_metadata, gainmap_xml))
        return fail(context, "Bound ISO GainMap metadata cannot be serialized without loss.");
    std::vector<uint8_t> codec_heif;
    heic_encoded_image_facts encoded_facts{};
    const lpb_result heic_encoded = encode_heic_primary_and_gainmap_bytes(context,
        primary_pixels, gainmap_pixels, request.quality,
        std::span<const uint8_t>(reinterpret_cast<const uint8_t*>(gainmap_xml.data()), gainmap_xml.size()),
        codec_heif, encoded_facts);
    if (heic_encoded != LPB_RESULT_OK) return heic_encoded;
    std::vector<uint8_t> output_bytes;
    std::string graph_error;
    if (!containers::attach_gainmap_auxiliary_graph(codec_heif, encoded_facts.primary_item_id,
            encoded_facts.secondary_item_id, output_bytes, graph_error)) {
        return fail(context, graph_error.empty() ? "Native HEIF GainMap graph assembly failed." : graph_error.c_str(),
            LPB_RESULT_INVALID_ARGUMENT);
    }

    auto staged = std::make_shared<hdr_gainmap_stage>();
    staged->destination = output_native;
    staged->target_semantic = LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY;
    staged->actual_container = LPB_IMAGE_CONTAINER_HEIC;
    if (!staged->output.create(output_native, L"lpb-hdr-heic", L".tmp", true) ||
        !staged->output.write_all(output_bytes) || !staged->output.ready_to_consume()) {
        staged->output.abort();
        return fail(context, "Native could not stage the complete HEIF GainMap without publishing a partial target.", LPB_RESULT_INTERNAL_ERROR);
    }
    const std::string staged_path = path_to_utf8(staged->output.path());
    if (staged_path.empty()) {
        staged->output.abort();
        return fail(context, "Native HEIF staging path is not valid UTF-8.", LPB_RESULT_INTERNAL_ERROR);
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
        staged->output.abort();
        return fail(context, diagnostic.c_str(), output_inspected);
    }
    if (output_facts.primary_image.container != LPB_IMAGE_CONTAINER_HEIC ||
        output_facts.gain_map.container != LPB_IMAGE_CONTAINER_HEIC || !output_facts.gain_map.is_present ||
        output_facts.gain_map.item_id != encoded_facts.secondary_item_id ||
        output_facts.auxiliary_count != 1 || output_facts.gain_map.auxiliary_index >= output_facts.auxiliary_count ||
        output_facts.gain_map.representation != LPB_AUX_REPRESENTATION_EMBEDDED ||
        output_facts.gain_map.ownership != LPB_AUX_OWNER_PRIMARY ||
        output_facts.gain_map.owner_artifact_role != LPB_ARTIFACT_PRIMARY_IMAGE ||
        !valid_gainmap_graph(context, output_facts)) {
        staged->output.abort();
        return fail(context, "Staged HEIF does not contain exactly one embedded Primary-owned ISO GainMap item.", LPB_RESULT_INTERNAL_ERROR);
    }
    const auto& output_gainmap = output_facts.auxiliary_items[output_facts.gain_map.auxiliary_index];
    const uint64_t map_offset = output_facts.gain_map.file_range.offset;
    const uint64_t map_length = output_facts.gain_map.file_range.length;
    if (output_gainmap.item_id != encoded_facts.secondary_item_id ||
        std::string_view(output_gainmap.relationship) != "urn:com:photo:aux:hdrgainmap" ||
        std::string_view(output_gainmap.semantic) != "GainMap" || map_length == 0 ||
        map_offset > output_bytes.size() || map_length > output_bytes.size() - map_offset) {
        staged->output.abort();
        return fail(context, "Staged HEIF GainMap item identity, relationship, or bounded byte range changed.", LPB_RESULT_INTERNAL_ERROR);
    }
    uint8_t expected_gainmap_hash[32]{};
    lpb::crypto::sha256_buffer(output_bytes.data() + static_cast<size_t>(map_offset),
        static_cast<size_t>(map_length), expected_gainmap_hash);
    if (!same_hash(output_gainmap.sha256, expected_gainmap_hash)) {
        staged->output.abort();
        return fail(context, "Staged HEIF GainMap bytes changed during graph inspection.", LPB_RESULT_INTERNAL_ERROR);
    }
    gainmap_metadata_facts output_metadata{};
    const lpb_result output_metadata_result = inspect_gainmap_metadata(context,
        staged_path.c_str(), nullptr, output_metadata, true);
    if (output_metadata_result != LPB_RESULT_OK || !same_metadata(target_metadata, output_metadata)) {
        staged->output.abort();
        return fail(context, "Staged HEIF GainMap metadata failed complete ISO relationship-bound re-read validation.",
            output_metadata_result == LPB_RESULT_OK ? LPB_RESULT_INTERNAL_ERROR : output_metadata_result);
    }
    uint8_t output_sha[32]{};
    uint8_t expected_output_hash[32]{};
    lpb::crypto::sha256_ctx output_sha_ctx;
    output_sha_ctx.update(output_bytes.data(), output_bytes.size());
    output_sha_ctx.finalize(expected_output_hash);
    if (!staged->output.ready_to_consume() || !staged->output.compute_sha256(output_sha) ||
        !same_hash(output_sha, expected_output_hash)) {
        staged->output.abort();
        return fail(context, "Staged HEIF bytes changed after Native graph and metadata validation.", LPB_RESULT_INTERNAL_ERROR);
    }
    if (lpb_context_check_cancelled(context) == LPB_RESULT_CANCELLED) {
        staged->output.abort();
        return fail(context, "HDR/GainMap conversion cancelled before managed semantic post-validation.", LPB_RESULT_CANCELLED);
    }

    result.target_semantic = LPB_HDR_GAINMAP_TARGET_HEIC_GAINMAP_AUXILIARY;
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
        staged->output.abort();
        return fail(context, "Native staging path exceeds the versioned conversion result capacity.", LPB_RESULT_INTERNAL_ERROR);
    }
    std::copy(std::begin(expected_output_hash), std::end(expected_output_hash), staged->expected_sha256.begin());
    if (!register_stage(context, staged, result.transaction_token)) {
        staged->output.abort();
        return fail(context, "Native could not register the owned HEIF GainMap stage for managed post-validation.", LPB_RESULT_INTERNAL_ERROR);
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
    if (!stage->output.publish_no_replace(stage->destination))
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
    if (!stage || !stage->output.abort_and_confirm())
        return fail(context, "Native could not confirm cleanup of the exact owned HDR/GainMap stage.", LPB_RESULT_INTERNAL_ERROR);
    context->hdr_gainmap_stages.erase(found);
    return LPB_RESULT_OK;
}
