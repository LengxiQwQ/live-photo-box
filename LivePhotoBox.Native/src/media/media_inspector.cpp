#include "media/media_inspector.h"
#include "media/video_converter.h"
#include "foundation/internal.h"
#include "binary/binary_io.h"
#include "containers/isobmff.h"
#include <fstream>
#include <filesystem>
#include <algorithm>
#include <cctype>
#include <cstring>
#include <string_view>
#include <utility>
#include <vector>
#include <cstdlib>

namespace fs = std::filesystem;

namespace lpb::media {

namespace {

using namespace_binding = std::pair<std::string_view, std::string_view>;

struct xmp_attribute {
    std::string_view name;
    std::string_view value;
};

struct xmp_element {
    std::string_view name;
    std::vector<xmp_attribute> attributes;
    std::vector<namespace_binding> bindings;
};

static constexpr std::string_view google_camera_namespace = "http://ns.google.com/photos/1.0/camera/";
static constexpr std::string_view google_container_namespace = "http://ns.google.com/photos/1.0/container/";
static constexpr std::string_view google_item_namespace = "http://ns.google.com/photos/1.0/container/item/";
static constexpr std::string_view oppo_camera_namespace = "http://ns.oplus.com/photos/1.0/camera/";
static constexpr std::string_view vivo_camera_namespace = "http://ns.vivo.com/photos/1.0/camera/";

static bool is_xml_name_char(char c) noexcept {
    return std::isalnum(static_cast<unsigned char>(c)) != 0 ||
        c == ':' || c == '_' || c == '-' || c == '.';
}

static std::string_view local_name(std::string_view name) noexcept {
    const size_t colon = name.rfind(':');
    return colon == std::string_view::npos ? name : name.substr(colon + 1);
}

static void set_namespace_binding(std::vector<namespace_binding>& bindings,
    std::string_view prefix, std::string_view uri) {
    for (auto& binding : bindings) {
        if (binding.first == prefix) {
            binding.second = uri;
            return;
        }
    }
    bindings.emplace_back(prefix, uri);
}

static std::string_view namespace_uri_for_name(std::string_view name,
    const std::vector<namespace_binding>& bindings, bool attribute_name) noexcept {
    const size_t colon = name.find(':');
    if (colon == std::string_view::npos) {
        return attribute_name ? std::string_view{} : [&]() {
            for (auto it = bindings.rbegin(); it != bindings.rend(); ++it) {
                if (it->first.empty()) return it->second;
            }
            return std::string_view{};
        }();
    }

    const std::string_view prefix = name.substr(0, colon);
    for (auto it = bindings.rbegin(); it != bindings.rend(); ++it) {
        if (it->first == prefix) return it->second;
    }
    return {};
}

static bool find_tag_end(std::string_view xml, size_t start, size_t& end) noexcept {
    char quote = 0;
    for (size_t i = start; i < xml.size(); ++i) {
        const char c = xml[i];
        if (quote != 0) {
            if (c == quote) quote = 0;
        } else if (c == '\'' || c == '"') {
            quote = c;
        } else if (c == '>') {
            end = i + 1;
            return true;
        }
    }
    return false;
}

static bool parse_start_tag(std::string_view xml, size_t start, size_t end,
    std::string_view& tag_name, std::vector<xmp_attribute>& attributes) noexcept {
    if (start >= end || xml[start] != '<' || start + 1 >= end) return false;
    size_t p = start + 1;
    if (xml[p] == '/' || xml[p] == '!' || xml[p] == '?') return false;

    const size_t name_start = p;
    while (p < end && is_xml_name_char(xml[p])) ++p;
    if (p == name_start) return false;
    tag_name = xml.substr(name_start, p - name_start);

    while (p < end) {
        while (p < end && (std::isspace(static_cast<unsigned char>(xml[p])) || xml[p] == '/')) ++p;
        if (p >= end || xml[p] == '>') break;

        const size_t attr_start = p;
        while (p < end && is_xml_name_char(xml[p])) ++p;
        if (p == attr_start) return false;
        const std::string_view attr_name = xml.substr(attr_start, p - attr_start);
        while (p < end && std::isspace(static_cast<unsigned char>(xml[p]))) ++p;
        if (p >= end || xml[p] != '=') return false;
        ++p;
        while (p < end && std::isspace(static_cast<unsigned char>(xml[p]))) ++p;
        if (p >= end || (xml[p] != '\'' && xml[p] != '"')) return false;
        const char quote = xml[p++];
        const size_t value_start = p;
        while (p < end && xml[p] != quote) ++p;
        if (p >= end) return false;
        attributes.push_back({ attr_name, xml.substr(value_start, p - value_start) });
        ++p;
    }
    return true;
}

static bool scan_xmp_elements(std::string_view xml, std::vector<xmp_element>& elements) {
    std::vector<std::vector<namespace_binding>> scopes;
    size_t p = 0;
    while ((p = xml.find('<', p)) != std::string_view::npos) {
        if (xml.substr(p, 4) == "<!--") {
            const size_t comment_end = xml.find("-->", p + 4);
            if (comment_end == std::string_view::npos) return false;
            p = comment_end + 3;
            continue;
        }
        if (p + 1 < xml.size() && (xml[p + 1] == '!' || xml[p + 1] == '?')) {
            size_t tag_end = 0;
            if (!find_tag_end(xml, p, tag_end)) return false;
            p = tag_end;
            continue;
        }

        size_t tag_end = 0;
        if (!find_tag_end(xml, p, tag_end)) return false;
        if (p + 1 < xml.size() && xml[p + 1] == '/') {
            if (scopes.empty()) return false;
            scopes.pop_back();
            p = tag_end;
            continue;
        }

        std::string_view tag_name;
        std::vector<xmp_attribute> attributes;
        if (!parse_start_tag(xml, p, tag_end, tag_name, attributes)) {
            p = tag_end;
            continue;
        }

        std::vector<namespace_binding> bindings = scopes.empty() ?
            std::vector<namespace_binding>{} : scopes.back();
        for (const auto& attribute : attributes) {
            if (attribute.name == "xmlns") {
                set_namespace_binding(bindings, {}, attribute.value);
            } else if (attribute.name.size() > 6 && attribute.name.substr(0, 6) == "xmlns:") {
                set_namespace_binding(bindings, attribute.name.substr(6), attribute.value);
            }
        }

        elements.push_back({ tag_name, std::move(attributes), bindings });
        const bool self_closing = tag_end >= 2 && xml[tag_end - 2] == '/';
        if (!self_closing) scopes.push_back(std::move(bindings));
        p = tag_end;
    }
    return scopes.empty();
}

static bool element_is(const xmp_element& element, std::string_view uri,
    std::string_view local) noexcept {
    return local_name(element.name) == local &&
        namespace_uri_for_name(element.name, element.bindings, false) == uri;
}

static bool get_attribute_u64(const xmp_element& element, std::string_view uri,
    std::string_view local, uint64_t& out_value) {
    for (const auto& attribute : element.attributes) {
        if (local_name(attribute.name) != local ||
            namespace_uri_for_name(attribute.name, element.bindings, true) != uri) continue;
        std::string value(attribute.value);
        char* end = nullptr;
        out_value = std::strtoull(value.c_str(), &end, 10);
        return end != value.c_str();
    }
    return false;
}

static bool get_attribute_string(const xmp_element& element, std::string_view uri,
    std::string_view local, std::string_view expected) noexcept {
    for (const auto& attribute : element.attributes) {
        if (local_name(attribute.name) == local &&
            namespace_uri_for_name(attribute.name, element.bindings, true) == uri &&
            attribute.value == expected) return true;
    }
    return false;
}

static const xmp_element* find_motion_item(const std::vector<xmp_element>& elements,
    uint64_t& out_length, lpb_video_container& out_container) {
    for (const auto& element : elements) {
        if (!element_is(element, google_container_namespace, "Item") ||
            !get_attribute_string(element, google_item_namespace, "Semantic", "MotionPhoto") ||
            !get_attribute_u64(element, google_item_namespace, "Length", out_length)) continue;

        // The container item MIME is authoritative for the embedded video.
        // Google Motion Photo metadata can describe both MP4 and QuickTime/MOV
        // payloads; accepting only video/mp4 made valid rebuilt JPEG+MOV output
        // impossible to inspect and split again.
        if (get_attribute_string(element, google_item_namespace, "Mime", "video/quicktime"))
            out_container = LPB_VIDEO_CONTAINER_MOV;
        else if (get_attribute_string(element, google_item_namespace, "Mime", "video/mp4"))
            out_container = LPB_VIDEO_CONTAINER_MP4;
        else
            continue;

        if (out_length > 0) return &element;
    }
    return nullptr;
}

static bool has_protocol_attribute(const std::vector<xmp_element>& elements,
    std::string_view uri, std::string_view local) {
    for (const auto& element : elements) {
        for (const auto& attribute : element.attributes) {
            if (local_name(attribute.name) == local &&
                namespace_uri_for_name(attribute.name, element.bindings, true) == uri) {
                return true;
            }
        }
    }
    return false;
}

static bool has_attribute_value(const std::vector<xmp_element>& elements,
    std::string_view uri, std::string_view local, std::string_view expected) {
    for (const auto& element : elements) {
        if (get_attribute_string(element, uri, local, expected)) return true;
    }
    return false;
}

static bool get_first_attribute_u64(const std::vector<xmp_element>& elements,
    std::string_view uri, std::string_view local, uint64_t& out_value) {
    for (const auto& element : elements) {
        if (get_attribute_u64(element, uri, local, out_value)) return true;
    }
    return false;
}

} // namespace

static std::vector<uint8_t> read_file_bytes(const char* path, size_t max_bytes = 0) {
    if (!path) return {};
    auto p = utf8_to_path(path);
    std::ifstream file(p, std::ios::binary | std::ios::ate);
    if (!file.is_open()) return {};

    std::streamsize file_size = file.tellg();
    if (file_size <= 0) return {};

    size_t to_read = (max_bytes > 0 && max_bytes < static_cast<size_t>(file_size)) 
        ? max_bytes 
        : static_cast<size_t>(file_size);

    file.seekg(0, std::ios::beg);
    std::vector<uint8_t> buffer(to_read);
    file.read(reinterpret_cast<char*>(buffer.data()), to_read);
    return buffer;
}

static uint64_t get_file_size(const char* path) {
    if (!path) return 0;
    auto p = utf8_to_path(path);
    std::error_code ec;
    auto size = fs::file_size(p, ec);
    return ec ? 0 : size;
}

lpb_image_container detect_image_container(std::span<const uint8_t> header) noexcept {
    if (header.size() >= 2 && header[0] == 0xFF && header[1] == 0xD8) {
        return LPB_IMAGE_CONTAINER_JPEG;
    }
    if (header.size() >= 12 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p') {
        std::string_view brand(reinterpret_cast<const char*>(header.data() + 8), 4);
        if (brand == "heic" || brand == "heix" || brand == "heim" || brand == "heis" ||
            brand == "mif1" || brand == "msf1") {
            return LPB_IMAGE_CONTAINER_HEIC;
        }
    }
    return LPB_IMAGE_CONTAINER_UNKNOWN;
}

lpb_video_container detect_video_container(std::span<const uint8_t> header) noexcept {
    if (header.size() >= 12 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p') {
        std::string_view brand(reinterpret_cast<const char*>(header.data() + 8), 4);
        if (brand == "qt  ") return LPB_VIDEO_CONTAINER_MOV;
        return LPB_VIDEO_CONTAINER_MP4;
    }
    // Some iPhone QuickTime files omit ftyp and begin with the standard
    // 8-byte wide placeholder followed by mdat.  The complete probe later
    // validates moov/mdat and tracks; this header-level classification only
    // needs to recognize the container for dual-file Apple inspection.
    if (header.size() >= 16 &&
        header[4] == 'w' && header[5] == 'i' && header[6] == 'd' && header[7] == 'e' &&
        header[12] == 'm' && header[13] == 'd' && header[14] == 'a' && header[15] == 't') {
        return LPB_VIDEO_CONTAINER_MOV;
    }
    if (header.size() >= 8 && header[4] == 'm' && header[5] == 'o' && header[6] == 'o' && header[7] == 'v') {
        return LPB_VIDEO_CONTAINER_MOV;
    }
    return LPB_VIDEO_CONTAINER_UNKNOWN;
}

static std::string extract_xmp_string(const std::vector<uint8_t>& data) {
    const std::string start_tag = "<x:xmpmeta";
    const std::string end_tag = "</x:xmpmeta>";

    std::string_view sv(reinterpret_cast<const char*>(data.data()), data.size());
    auto start_pos = sv.find(start_tag);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(end_tag, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + end_tag.length() - start_pos));
        }
    }

    const std::string rdf_start = "<rdf:RDF";
    const std::string rdf_end = "</rdf:RDF>";
    start_pos = sv.find(rdf_start);
    if (start_pos != std::string_view::npos) {
        auto end_pos = sv.find(rdf_end, start_pos);
        if (end_pos != std::string_view::npos) {
            return std::string(sv.substr(start_pos, end_pos + rdf_end.length() - start_pos));
        }
    }

    return {};
}

// Returns the first structurally valid JPEG end marker. Bytes after this
// marker belong to an embedded payload/trailer and are not image bytes.
static bool find_jpeg_end(const std::vector<uint8_t>& data, uint64_t& out_end) noexcept
{
    if (data.size() < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;
    size_t pos = 2;
    while (pos + 1 < data.size())
    {
        if (data[pos] != 0xFF) { ++pos; continue; }
        uint8_t marker = data[pos + 1];
        if (marker == 0x00 || marker == 0xFF) { pos += 2; continue; }
        if (marker == 0xD9) { out_end = pos + 2; return true; }
        if (marker == 0xDA)
        {
            if (pos + 4 > data.size()) return false;
            const size_t len = (static_cast<size_t>(data[pos + 2]) << 8) | data[pos + 3];
            if (len < 2 || len > data.size() - pos - 2) return false;
            pos += 2 + len;
            while (pos + 1 < data.size())
            {
                if (data[pos] == 0xFF)
                {
                    const uint8_t scan_marker = data[pos + 1];
                    if (scan_marker == 0xD9) { out_end = pos + 2; return true; }
                    if (scan_marker == 0x00 || (scan_marker >= 0xD0 && scan_marker <= 0xD7))
                    { pos += 2; continue; }
                }
                ++pos;
            }
            return false;
        }
        if (marker >= 0xD0 && marker <= 0xD7) { pos += 2; continue; }
        if (pos + 4 > data.size()) return false;
        const size_t len = (static_cast<size_t>(data[pos + 2]) << 8) | data[pos + 3];
        if (len < 2 || len > data.size() - pos - 2) return false;
        pos += 2 + len;
    }
    return false;
}

static bool check_huawei_moving_photo(
    const std::vector<uint8_t>& data,
    uint64_t file_size,
    uint64_t& out_video_offset,
    uint64_t& out_video_len,
    int64_t& out_cover_time_us,
    bool& is_honor)
{
    if (data.size() < 60) return false;

    size_t scan_start = data.size() > 4096 ? data.size() - 4096 : 0;
    std::string_view tail(reinterpret_cast<const char*>(data.data() + scan_start), data.size() - scan_start);

    auto live_pos = tail.rfind("LIVE_");
    if (live_pos == std::string_view::npos) return false;

    size_t actual_live_pos = scan_start + live_pos;

    // Parse LIVE_NNNNNNN
    std::string_view num_part = tail.substr(live_pos + 5);
    std::string numeric_part(num_part.substr(0, 15));
    char* endptr = nullptr;
    uint64_t mp4_plus_20 = std::strtoull(numeric_part.c_str(), &endptr, 10);
    if (endptr != numeric_part.c_str() && mp4_plus_20 > 20 && mp4_plus_20 <= file_size) {
        out_video_len = mp4_plus_20 - 20;

        size_t trailer_start = (actual_live_pos >= 40) ? (actual_live_pos - 40) : 0;
        if (trailer_start < out_video_len || trailer_start > file_size ||
            out_video_len > file_size - (trailer_start - out_video_len)) return false;
        out_video_offset = trailer_start - out_video_len;
        if (out_video_len > file_size - out_video_offset) return false;

        // Check if Honor (uses v2_f prefix or contains srcDstWh)
        if (tail.find("v2_f") != std::string_view::npos || 
            tail.find("srcDstWh") != std::string_view::npos ||
            tail.find("v1_f") != std::string_view::npos) {
            is_honor = true;
        } else {
            is_honor = false;
        }

        if (trailer_start + 40 <= data.size()) {
            std::string_view time_part(reinterpret_cast<const char*>(data.data() + trailer_start + 20), 20);
            auto colon = time_part.find(':');
            if (colon != std::string_view::npos) {
                char* endp = nullptr;
                uint64_t cover_ms = std::strtoull(std::string(time_part.substr(0, colon)).c_str(), &endp, 10);
                if (endp != time_part.data()) {
                    out_cover_time_us = cover_ms * 1000;
                }
            }
        }
        return true;
    }

    return false;
}

static bool check_samsung_sef_jpeg(
    const std::vector<uint8_t>& data,
    uint64_t file_size,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    (void)file_size;
    if (data.size() < 32) return false;

    size_t len = data.size();
    if (data[len - 4] != 'S' || data[len - 3] != 'E' || data[len - 2] != 'F' || data[len - 1] != 'T') {
        return false;
    }

    std::string_view tail(reinterpret_cast<const char*>(data.data() + (len > 2048 ? len - 2048 : 0)), len > 2048 ? 2048 : len);
    auto sefh_pos = tail.rfind("SEFH");
    if (sefh_pos == std::string_view::npos) return false;

    size_t actual_sefh = (len > 2048 ? len - 2048 : 0) + sefh_pos;
    if (actual_sefh + 12 > len) return false;

    binary_reader reader(data.data() + actual_sefh, len - actual_sefh);
    reader.skip(4); // Skip SEFH

    uint32_t version = 0, count = 0;
    if (!reader.try_read_u32_endian(version, false) || !reader.try_read_u32_endian(count, false)) return false;

    for (uint32_t i = 0; i < count; i++) {
        uint16_t prefix = 0, marker = 0;
        uint32_t offset = 0, size = 0;
        if (!reader.try_read_u16_endian(prefix, false)) break;
        if (!reader.try_read_u16_endian(marker, false)) break;
        if (!reader.try_read_u32_endian(offset, false)) break;
        if (!reader.try_read_u32_endian(size, false)) break;

        if (marker == 0x0A30 && size >= 24 && offset <= actual_sefh) {
            out_video_offset = actual_sefh - offset + 24;
            out_video_len = size - 24;
            return true;
        }
    }

    return false;
}

static bool check_samsung_sef_heic(
    const std::vector<uint8_t>& data,
    uint64_t file_size,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    (void)file_size;
    size_t pos = 0;
    while (pos + 8 <= data.size()) {
        uint32_t box_size = (static_cast<uint32_t>(data[pos]) << 24) |
                            (static_cast<uint32_t>(data[pos + 1]) << 16) |
                            (static_cast<uint32_t>(data[pos + 2]) << 8) |
                            (static_cast<uint32_t>(data[pos + 3]));

        if (box_size < 8) break;

        std::string_view type(reinterpret_cast<const char*>(data.data() + pos + 4), 4);
        if (type == "mpvd") {
            out_video_offset = pos + 8;
            out_video_len = box_size - 8;
            return true;
        }

        pos += box_size;
    }
    return false;
}

static bool check_vivo_x300(
    const std::vector<xmp_element>& elements,
    uint64_t file_size,
    uint64_t& out_primary_len,
    uint64_t& out_gm_offset,
    uint64_t& out_gm_len,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    lpb_video_container video_container = LPB_VIDEO_CONTAINER_MP4;
    if (has_protocol_attribute(elements, vivo_camera_namespace, "VMotionPhotoVersion") &&
        find_motion_item(elements, out_video_len, video_container) != nullptr) {
        // Collect the exact Container/Item lengths in document order.  The
        // last item is the motion video and the preceding item is the vivo
        // X300 gain map.
        std::vector<uint64_t> lengths;
        for (const auto& element : elements) {
            if (!element_is(element, google_container_namespace, "Item")) continue;
            uint64_t len = 0;
            if (get_attribute_u64(element, google_item_namespace, "Length", len) && len > 0) {
                lengths.push_back(len);
            }
        }

        if (lengths.size() >= 2) {
            uint64_t vid_len = lengths.back();
            uint64_t gm_len = lengths[lengths.size() - 2];
            if (vid_len > 0 && gm_len > 0 && vid_len + gm_len < file_size) {
                out_video_len = vid_len;
                out_video_offset = file_size - vid_len;
                out_gm_len = gm_len;
                out_gm_offset = out_video_offset - gm_len;
                out_primary_len = out_gm_offset;
                return true;
            }
        }
    }
    return false;
}

static bool has_apple_live_makernote_tag(const uint8_t* data, size_t size) {
    if (size < 30) return false;
    const uint8_t sig[] = {'A','p','p','l','e',' ','i','O','S','\0'};
    for (size_t i = 0; i <= size - 16; i++) {
        if (data[i] == 'A' && data[i+1] == 'p' && std::memcmp(data + i, sig, 10) == 0 &&
            data[i+10] == 0x00 && data[i+11] == 0x01 && data[i+12] == 'M' && data[i+13] == 'M') {
            size_t mnStart = i;
            if (mnStart + 16 > size) return false;
            uint16_t entry_count = (static_cast<uint16_t>(data[mnStart + 14]) << 8) | data[mnStart + 15];
            if (entry_count == 0 || entry_count > 64) continue;
            size_t entriesStart = mnStart + 16;
            for (uint16_t j = 0; j < entry_count; j++) {
                size_t e = entriesStart + j * 12;
                if (e + 2 > size) break;
                uint16_t tag = (static_cast<uint16_t>(data[e]) << 8) | data[e + 1];
                if (tag == 0x0011 || tag == 0x0017 || tag == 0x0025 || tag == 0x002b) {
                    return true;
                }
            }
        }
    }
    return false;
}

lpb_result inspect_source(
    lpb_context* context,
    const char* primary_path,
    const char* secondary_path,
    lpb_source_media_facts* out_facts) noexcept
{
    if (!context || !primary_path || !out_facts) {
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::memset(out_facts, 0, sizeof(lpb_source_media_facts));
    out_facts->struct_size = sizeof(lpb_source_media_facts);

    uint64_t primary_size = get_file_size(primary_path);
    if (primary_size == 0) {
        set_error(context, "Primary file is empty or does not exist.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    auto primary_data = read_file_bytes(primary_path);
    if (primary_data.empty()) {
        set_error(context, "Failed to read primary file.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    lpb_image_container img_cont = detect_image_container(primary_data);
    lpb_video_container vid_cont = detect_video_container(primary_data);
    uint64_t jpeg_end = 0;
    const bool has_jpeg_end = find_jpeg_end(primary_data, jpeg_end);

    out_facts->primary_image.container = img_cont;
    out_facts->primary_image.is_present = (img_cont != LPB_IMAGE_CONTAINER_UNKNOWN) ? 1 : 0;
    out_facts->primary_image.file_range.offset = 0;
    out_facts->primary_image.file_range.length = primary_size;

    // Dual file check
    if (secondary_path && std::strlen(secondary_path) > 0) {
        uint64_t secondary_size = get_file_size(secondary_path);
        auto sec_data = read_file_bytes(secondary_path, 65536);
        lpb_video_container sec_vid_cont = detect_video_container(sec_data);

        if (sec_vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN && secondary_size > 0) {
            std::string_view pri_sv(reinterpret_cast<const char*>(primary_data.data()), primary_data.size());
            if (pri_sv.find("vivo") != std::string_view::npos && pri_sv.find("cameralbum!") != std::string_view::npos) {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = sec_vid_cont;
                out_facts->motion_video.file_range.offset = 0;
                out_facts->motion_video.file_range.length = secondary_size;
                if (has_jpeg_end && jpeg_end < primary_size) {
                    out_facts->primary_image.file_range.length = jpeg_end;
                    out_facts->protocol_tail_range.offset = jpeg_end;
                    out_facts->protocol_tail_range.length = primary_size - jpeg_end;
                }
                probe_video_file(context, secondary_path, &out_facts->motion_video);
                return LPB_RESULT_OK;
            }

            // Check if Apple ContentIdentifier or live metadata exists in secondary MOV or primary image
            std::string_view sec_sv(reinterpret_cast<const char*>(sec_data.data()), sec_data.size());
            bool has_apple_video = (sec_sv.find("com.apple.quicktime.content.identifier") != std::string_view::npos) ||
                                   (sec_sv.find("com.apple.quicktime.live-photo") != std::string_view::npos) ||
                                   (sec_sv.find("mebx") != std::string_view::npos);

            bool has_apple_image = has_apple_live_makernote_tag(primary_data.data(), primary_data.size()) ||
                                   (pri_sv.find("apple-desktop:ContentIdentifier") != std::string_view::npos) ||
                                   (pri_sv.find("apple-fi:PhotoIdentifier") != std::string_view::npos);

            if (has_apple_video || has_apple_image) {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = sec_vid_cont;
                out_facts->motion_video.file_range.offset = 0;
                out_facts->motion_video.file_range.length = secondary_size;
                probe_video_file(context, secondary_path, &out_facts->motion_video);
                return LPB_RESULT_OK;
            }
        }
    }

    // Single file checks
    std::string xmp = extract_xmp_string(primary_data);
    std::vector<xmp_element> xmp_elements;
    if (!xmp.empty() && !scan_xmp_elements(xmp, xmp_elements)) {
        xmp_elements.clear();
    }

    // 1. Check vivo X300+ 3-item container
    uint64_t pri_len = 0, gm_off = 0, gm_len = 0, vid_off = 0, vid_len = 0;
    if (!xmp_elements.empty() && check_vivo_x300(xmp_elements, primary_size, pri_len, gm_off, gm_len, vid_off, vid_len)) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_X300;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = gm_off > 0 ? gm_off : (has_jpeg_end ? jpeg_end : primary_size);

        out_facts->gain_map.is_present = 1;
        out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
        out_facts->gain_map.file_range.offset = gm_off;
        out_facts->gain_map.file_range.length = gm_len;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        return LPB_RESULT_OK;
    }

    // 2. Check Samsung JPEG (SEF Trailer)
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && check_samsung_sef_jpeg(primary_data, primary_size, vid_off, vid_len)) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG;
        out_facts->primary_image.file_range.offset = 0;
        // Samsung's SEF directory and payload are part of the inspected
        // source artifact.  The SEF cleaner needs the complete JPEG trailer
        // to validate and rebuild it; do not pre-truncate it in extraction.
        out_facts->primary_image.file_range.length = primary_size;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        return LPB_RESULT_OK;
    }

    // 3. Check Samsung HEIC (mpvd box)
    if (img_cont == LPB_IMAGE_CONTAINER_HEIC && check_samsung_sef_heic(primary_data, primary_size, vid_off, vid_len)) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC;
        out_facts->primary_image.file_range.offset = 0;
        // mpvd is an ISOBMFF box in the HEIF source.  Keep the complete
        // container for the structural HEIF cleaner; a byte-range cut would
        // make the remaining meta/item references unverifiable.
        out_facts->primary_image.file_range.length = primary_size;

        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        return LPB_RESULT_OK;
    }

    // 4. Check Huawei / Honor Moving Photo
    int64_t cover_time_us = 0;
    bool is_honor = false;
    if (check_huawei_moving_photo(primary_data, primary_size, vid_off, vid_len, cover_time_us, is_honor)) {
        out_facts->protocol = is_honor ? LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO : LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO;
        out_facts->primary_image.file_range.offset = 0;
        // Huawei/Honor stores [image][MP4][60-byte LIVE trailer].  The image
        // range is therefore the validated start of the embedded MP4.
        out_facts->primary_image.file_range.length = vid_off;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = vid_off;
        out_facts->motion_video.file_range.length = vid_len;
        const uint64_t video_end = vid_off + vid_len;
        if (video_end < primary_size) {
            out_facts->protocol_tail_range.offset = video_end;
            out_facts->protocol_tail_range.length = primary_size - video_end;
        }
        out_facts->timing.cover_timestamp_us = cover_time_us;
        return LPB_RESULT_OK;
    }

    // 5. Check OPPO / OnePlus O-Live (XMP OpCamera:VideoLength)
    uint64_t op_vid_len = 0;
    if (!xmp_elements.empty() &&
        get_first_attribute_u64(xmp_elements, oppo_camera_namespace, "VideoLength", op_vid_len) && op_vid_len > 0) {
        uint64_t item_len = op_vid_len;
        lpb_video_container ignored_container = LPB_VIDEO_CONTAINER_MP4;
        find_motion_item(xmp_elements, item_len, ignored_container);

        out_facts->protocol = LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        if (item_len == 0 || item_len > primary_size || op_vid_len > item_len) {
            set_error(context, "OPPO protocol item length is outside the source file.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        out_facts->motion_video.file_range.offset = primary_size - item_len;
        out_facts->motion_video.file_range.length = op_vid_len;

        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = out_facts->motion_video.file_range.offset;
        const uint64_t video_end = out_facts->motion_video.file_range.offset + op_vid_len;
        if (video_end < primary_size) {
            out_facts->protocol_tail_range.offset = video_end;
            out_facts->protocol_tail_range.length = primary_size - video_end;
        }
        return LPB_RESULT_OK;
    }

    // 6. Check Google Motion Photo V2 / Xiaomi (Container:Directory)
    uint64_t mp_vid_len = 0;
    lpb_video_container motion_video_container = LPB_VIDEO_CONTAINER_MP4;
    if (!xmp_elements.empty() &&
        has_attribute_value(xmp_elements, google_camera_namespace, "MotionPhoto", "1") &&
        find_motion_item(xmp_elements, mp_vid_len, motion_video_container) != nullptr &&
        mp_vid_len < primary_size) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = motion_video_container;
        out_facts->motion_video.file_range.offset = primary_size - mp_vid_len;
        out_facts->motion_video.file_range.length = mp_vid_len;

        // GainMap is only accepted as a second, correctly namespaced item.
        for (const auto& element : xmp_elements) {
            uint64_t g_len = 0;
            if (!element_is(element, google_container_namespace, "Item") ||
                !get_attribute_string(element, google_item_namespace, "Semantic", "GainMap") ||
                !get_attribute_u64(element, google_item_namespace, "Length", g_len) ||
                g_len == 0 || g_len >= out_facts->motion_video.file_range.offset) continue;
            out_facts->gain_map.is_present = 1;
            out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
            out_facts->gain_map.file_range.offset = out_facts->motion_video.file_range.offset - g_len;
            out_facts->gain_map.file_range.length = g_len;
            out_facts->primary_image.file_range.length = out_facts->gain_map.file_range.offset;
            return LPB_RESULT_OK;
        }

        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = out_facts->motion_video.file_range.offset;
        return LPB_RESULT_OK;
    }

    // 7. Check Google MicroVideo V1 (GCamera:MicroVideoOffset)
    uint64_t mv_offset = 0;
    if (!xmp_elements.empty() &&
        get_first_attribute_u64(xmp_elements, google_camera_namespace, "MicroVideoOffset", mv_offset) &&
        mv_offset > 0 && mv_offset < primary_size) {
        out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = primary_size - mv_offset;
        out_facts->motion_video.file_range.length = mv_offset;

        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = out_facts->motion_video.file_range.offset;
        return LPB_RESULT_OK;
    }

    // 8. Non-Live Media fallback
    out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
    if (img_cont != LPB_IMAGE_CONTAINER_UNKNOWN) {
        out_facts->primary_image.is_present = 1;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = primary_size;
    } else if (vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN) {
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = vid_cont;
        out_facts->motion_video.file_range.offset = 0;
        out_facts->motion_video.file_range.length = primary_size;
    }

    return LPB_RESULT_OK;
}

} // namespace lpb::media
