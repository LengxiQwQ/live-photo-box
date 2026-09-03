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
#include <charconv>

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
        const char* first = attribute.value.data();
        const char* last = first + attribute.value.size();
        auto parsed = std::from_chars(first, last, out_value, 10);
        return parsed.ec == std::errc{} && parsed.ptr == last;
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
    size_t item_count = 0;
    size_t primary_count = 0;
    size_t primary_index = 0;
    size_t motion_count = 0;
    size_t motion_index = 0;
    const xmp_element* motion = nullptr;
    for (const auto& element : elements) {
        if (!element_is(element, google_container_namespace, "Item")) continue;
        const size_t current_index = item_count++;
        if (get_attribute_string(element, google_item_namespace, "Semantic", "Primary")) {
            ++primary_count;
            primary_index = current_index;
        }
        if (!get_attribute_string(element, google_item_namespace, "Semantic", "MotionPhoto")) continue;
        ++motion_count;
        motion_index = current_index;
        motion = &element;
        if (!get_attribute_u64(element, google_item_namespace, "Length", out_length) || out_length == 0) return nullptr;
        if (get_attribute_string(element, google_item_namespace, "Mime", "video/quicktime"))
            out_container = LPB_VIDEO_CONTAINER_MOV;
        else if (get_attribute_string(element, google_item_namespace, "Mime", "video/mp4"))
            out_container = LPB_VIDEO_CONTAINER_MP4;
        else
            return nullptr;
    }
    // Auxiliary Container:Item records are legal after the motion item in
    // vendor variants.  The decisive structural checks are one Primary at
    // index zero and exactly one semantically named MotionPhoto item.
    if (motion == nullptr || primary_count != 1 || primary_index != 0 || motion_count != 1)
        return nullptr;
    return motion;
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

static std::string extract_xml_fragment(std::string_view sv) {
    const std::string start_tag = "<x:xmpmeta";
    const std::string end_tag = "</x:xmpmeta>";

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

static std::string extract_xmp_string(lpb_context* context, const std::vector<uint8_t>& data, lpb_image_container container) {
    if (container == LPB_IMAGE_CONTAINER_JPEG) {
        if (data.size() < 2 || data[0] != 0xFF || data[1] != 0xD8) return {};
        constexpr char xmp_header[] = "http://ns.adobe.com/xap/1.0/\0";
        // The literal has an explicit separator NUL plus the compiler-added
        // terminator; only the former belongs to the APP1 XMP header.
        constexpr size_t xmp_header_size = sizeof(xmp_header) - 1;
        size_t p = 2;
        while (p + 2 <= data.size()) {
            if (data[p] != 0xFF) return {};
            while (p < data.size() && data[p] == 0xFF) ++p;
            if (p >= data.size()) return {};
            const uint8_t marker = data[p++];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data.size()) return {};
            const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
            if (segment_length < 2 || segment_length - 2 > data.size() - (p + 2)) return {};
            const size_t payload = p + 2;
            const size_t payload_size = segment_length - 2;
            if (marker == 0xE1 && payload_size >= xmp_header_size &&
                std::memcmp(data.data() + payload, xmp_header, xmp_header_size) == 0) {
                return extract_xml_fragment(std::string_view(
                    reinterpret_cast<const char*>(data.data() + payload + xmp_header_size),
                    payload_size - xmp_header_size));
            }
            p = payload + payload_size;
        }
        return {};
    }

    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_xmp_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return {};
        return extract_xml_fragment(std::string_view(
            reinterpret_cast<const char*>(data.data() + static_cast<size_t>(offset)),
            static_cast<size_t>(length)));
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
    // LIVE_ is the final 20-byte field of the fixed 60-byte footer.  A marker
    // in an MP4 sample or a stale value in the middle of the file must not
    // become a destructive range candidate.
    if (actual_live_pos != data.size() - 20) return false;
    const std::string_view num_part = tail.substr(live_pos + 5, 15);
    uint64_t mp4_plus_20 = 0;
    const char* first = num_part.data();
    const char* last = first + num_part.size();
    const char* digit_end = first;
    while (digit_end < last && *digit_end >= '0' && *digit_end <= '9') ++digit_end;
    auto parsed = std::from_chars(first, digit_end, mp4_plus_20, 10);
    bool padding_ok = true;
    for (const char* p = digit_end; p < last; ++p) padding_ok = padding_ok && (*p == ' ' || *p == '\0');
    if (parsed.ec == std::errc{} && parsed.ptr == digit_end && digit_end != first && padding_ok &&
        mp4_plus_20 > 20 && mp4_plus_20 <= file_size) {
        out_video_len = mp4_plus_20 - 20;

        size_t trailer_start = (actual_live_pos >= 40) ? (actual_live_pos - 40) : 0;
        if (trailer_start > file_size || out_video_len > trailer_start) return false;
        out_video_offset = trailer_start - out_video_len;
        if (out_video_offset > file_size || out_video_len > file_size - out_video_offset ||
            !is_valid_isobmff_media_range(data.data(), data.size(), out_video_offset, out_video_len)) return false;

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
                uint64_t cover_ms = 0;
                const auto time_value = time_part.substr(0, colon);
                auto time_parsed = std::from_chars(time_value.data(), time_value.data() + time_value.size(), cover_ms, 10);
                if (time_parsed.ec == std::errc{} && time_parsed.ptr == time_value.data() + time_value.size() &&
                    cover_ms <= static_cast<uint64_t>(std::numeric_limits<int64_t>::max() / 1000)) {
                    out_cover_time_us = static_cast<int64_t>(cover_ms * 1000);
                }
            }
        }
        return true;
    }

    return false;
}

static bool check_samsung_sef_jpeg(
    lpb_context* context,
    const std::vector<uint8_t>& data,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    return lpb_samsung_sef_parse(context, data.data(), data.size(), &out_video_offset, &out_video_len) == LPB_RESULT_OK;
}

static bool check_samsung_sef_heic(
    const std::vector<uint8_t>& data,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    size_t pos = 0;
    bool found_mpvd = false;
    while (pos + 8 <= data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), pos, data.size(), box)) return false;
        if (std::memcmp(data.data() + pos + 4, "mpvd", 4) == 0) {
            if (found_mpvd) return false;
            found_mpvd = true;
            out_video_offset = pos + box.header_size;
            out_video_len = box.size - box.header_size;
            if (!is_valid_isobmff_media_range(data.data(), data.size(), out_video_offset, out_video_len)) return false;
        }
        pos += box.size;
    }
    return found_mpvd && pos == data.size();
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
        // Resolve by semantic name, not merely by the last two positive
        // lengths.  Auxiliary items may be inserted by camera firmware.
        uint64_t motion_length = 0;
        uint64_t gainmap_length = 0;
        size_t item_index = 0;
        size_t motion_index = 0;
        size_t gainmap_index = 0;
        size_t primary_count = 0;
        for (const auto& element : elements) {
            if (!element_is(element, google_container_namespace, "Item")) continue;
            if (get_attribute_string(element, google_item_namespace, "Semantic", "Primary")) ++primary_count;
            uint64_t len = 0;
            if (get_attribute_string(element, google_item_namespace, "Semantic", "GainMap")) {
                if (!get_attribute_u64(element, google_item_namespace, "Length", len)) return false;
                gainmap_length = len; gainmap_index = item_index;
            } else if (get_attribute_string(element, google_item_namespace, "Semantic", "MotionPhoto")) {
                if (!get_attribute_u64(element, google_item_namespace, "Length", len)) return false;
                motion_length = len; motion_index = item_index;
            }
            ++item_index;
        }

        if (primary_count == 1 && motion_length > 0 && gainmap_length > 0 &&
            motion_index == item_index - 1 && gainmap_index + 1 == motion_index &&
            gainmap_length <= file_size && motion_length <= file_size - gainmap_length &&
            gainmap_length + motion_length < file_size) {
                out_video_len = motion_length;
                out_video_offset = file_size - motion_length;
                out_gm_len = gainmap_length;
                out_gm_offset = out_video_offset - gainmap_length;
                out_primary_len = out_gm_offset;
                return true;
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

static bool looks_like_uuid(std::string_view value) noexcept {
    if (value.size() != 36) return false;
    for (size_t i = 0; i < value.size(); ++i) {
        const bool hex = std::isxdigit(static_cast<unsigned char>(value[i])) != 0;
        if ((i == 8 || i == 13 || i == 18 || i == 23) ? value[i] != '-' : !hex) return false;
    }
    return true;
}

static bool extract_apple_cid_from_makernote(const uint8_t* data, size_t start, size_t end, std::string& out) {
    if (!data || start > end || end - start < 30) return false;
    const char signature[] = "Apple iOS\0";
    for (size_t p = start; p + 16 <= end; ++p) {
        if (std::memcmp(data + p, signature, 10) != 0 || data[p + 10] != 0 || data[p + 11] != 1 ||
            data[p + 12] != 'M' || data[p + 13] != 'M') continue;
        const uint16_t count = read_be16u(data + p + 14);
        if (count == 0 || count > 64 || count > (end - p - 16) / 12) continue;
        const size_t entries = p + 16;
        const size_t note_end = end;
        for (uint16_t i = 0; i < count; ++i) {
            const size_t entry = entries + static_cast<size_t>(i) * 12;
            const uint16_t tag = read_be16u(data + entry);
            if (tag != 0x0011 || read_be16u(data + entry + 2) != 2) continue;
            const uint32_t length = read_be32u(data + entry + 4);
            const uint32_t relative = read_be32u(data + entry + 8);
            if (length == 0 || relative > note_end - p || length > note_end - p - relative) continue;
            std::string_view id(reinterpret_cast<const char*>(data + p + relative), length);
            if (!id.empty() && id.back() == '\0') id.remove_suffix(1);
            if (looks_like_uuid(id)) { out.assign(id); return true; }
        }
    }
    return false;
}

static bool extract_apple_cid_from_image(lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, std::string& out) {
    if (container == LPB_IMAGE_CONTAINER_JPEG && data.size() >= 2 && data[0] == 0xFF && data[1] == 0xD8) {
        size_t p = 2;
        while (p + 2 <= data.size()) {
            if (data[p++] != 0xFF) return false;
            while (p < data.size() && data[p] == 0xFF) ++p;
            if (p >= data.size()) return false;
            const uint8_t marker = data[p++];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data.size()) return false;
            const size_t segment_length = (static_cast<size_t>(data[p]) << 8) | data[p + 1];
            if (segment_length < 2 || segment_length - 2 > data.size() - (p + 2)) return false;
            const size_t payload = p + 2;
            const size_t payload_size = segment_length - 2;
            if (marker == 0xE1 && payload_size >= 6 && std::memcmp(data.data() + payload, "Exif\0\0", 6) == 0 &&
                extract_apple_cid_from_makernote(data.data(), payload + 6, payload + payload_size, out)) return true;
            p = payload + payload_size;
        }
        return false;
    }
    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        return extract_apple_cid_from_makernote(data.data(), static_cast<size_t>(offset),
            static_cast<size_t>(offset + length), out);
    }
    return false;
}

static bool extract_vivo_id_from_bytes(const uint8_t* data, size_t start, size_t end, std::string& out) {
    constexpr std::string_view key = "\"com.android.camera.livephoto\":\"";
    if (!data || start > end || end - start < key.size()) return false;
    const std::string_view bytes(reinterpret_cast<const char*>(data), end);
    const size_t key_pos = bytes.find(key, start);
    if (key_pos == std::string_view::npos || key_pos + key.size() > end) return false;
    const size_t value_start = key_pos + key.size();
    const size_t value_end = bytes.find('"', value_start);
    if (value_end == std::string_view::npos || value_end > end) return false;
    const std::string_view id = bytes.substr(value_start, value_end - value_start);
    if (id.empty() || id.size() > 127) return false;
    out.assign(id);
    return true;
}

static bool extract_vivo_id_from_image(const std::vector<uint8_t>& data, uint64_t jpeg_end, std::string& out) {
    constexpr std::string_view marker = "cameralbum!";
    if (jpeg_end > data.size()) return false;
    const size_t marker_pos = std::string_view(reinterpret_cast<const char*>(data.data()), data.size()).rfind(marker);
    if (marker_pos == std::string_view::npos || marker_pos < jpeg_end) return false;
    const auto bytes = std::string_view(reinterpret_cast<const char*>(data.data()), data.size());
    const size_t vivo_pos = bytes.rfind("vivo{", marker_pos);
    return vivo_pos != std::string_view::npos && extract_vivo_id_from_bytes(data.data(), vivo_pos, data.size(), out);
}

static bool extract_vivo_id_from_video(const std::vector<uint8_t>& data, std::string& out) {
    constexpr std::string_view user_type = "vivoMediaExtInfo";
    size_t p = 0;
    while (p < data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, data.size(), box)) return false;
        if (box.size >= box.header_size + 16 && std::memcmp(data.data() + p + 4, "uuid", 4) == 0 &&
            std::memcmp(data.data() + p + box.header_size, user_type.data(), user_type.size()) == 0) {
            return extract_vivo_id_from_bytes(data.data(), p + box.header_size + 16, p + box.size, out);
        }
        p += box.size;
    }
    return false;
}

static bool extract_apple_cid_from_video(lpb_context* context, const std::vector<uint8_t>& data, std::string& out) {
    size_t moov_start = 0;
    isobmff_box_header moov{};
    size_t p = 0;
    while (p < data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, data.size(), box)) { set_error(context, "Apple MOV top-level box is malformed."); return false; }
        if (std::memcmp(data.data() + p + 4, "moov", 4) == 0) { moov_start = p; moov = box; break; }
        p += box.size;
    }
    if (moov.size == 0) { set_error(context, "Apple MOV moov box was not found."); return false; }
    const size_t moov_end = moov_start + moov.size;

    // Apple stores the key names in `keys` and their values in indexed `ilst`
    // entries. Resolve the key index and then read only the corresponding
    // bounded `data` box; a text hit in mdat or another metadata value is not
    // sufficient evidence of pairing.
    size_t meta_start = 0;
    isobmff_box_header meta{};
    p = moov_start + moov.header_size;
    while (p < moov_end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, moov_end, box)) { set_error(context, "Apple MOV moov child is malformed."); return false; }
        if (std::memcmp(data.data() + p + 4, "meta", 4) == 0) { meta_start = p; meta = box; break; }
        p += box.size;
    }
    if (meta.size == 0) { set_error(context, "Apple MOV metadata box was not found."); return false; }
    const size_t meta_end = meta_start + meta.size;
    size_t child_start = meta_start + meta.header_size;
    isobmff_box_header first_child{};
    if (!try_read_box_header(data.data(), child_start, meta_end, first_child)) {
        if (child_start > meta_end - 4 || !try_read_box_header(data.data(), child_start + 4, meta_end, first_child)) { set_error(context, "Apple MOV metadata header is malformed."); return false; }
        child_start += 4;
    }

    size_t keys_start = 0, ilst_start = 0;
    isobmff_box_header keys{}, ilst{};
    p = child_start;
    while (p < meta_end) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), p, meta_end, box)) { set_error(context, "Apple MOV metadata child is malformed."); return false; }
        if (std::memcmp(data.data() + p + 4, "keys", 4) == 0) { keys_start = p; keys = box; }
        if (std::memcmp(data.data() + p + 4, "ilst", 4) == 0) { ilst_start = p; ilst = box; }
        p += box.size;
    }
    if (keys.size == 0 || ilst.size == 0 || keys.size < keys.header_size + 8) { set_error(context, "Apple MOV mdta keys/ilst boxes were not found."); return false; }

    const size_t keys_body = keys_start + keys.header_size;
    const uint32_t key_count = read_be32u(data.data() + keys_body + 4);
    if (key_count == 0 || key_count > 1024) return false;
    size_t key_pos = keys_body + 8;
    uint32_t content_key_index = 0;
    for (uint32_t index = 1; index <= key_count; ++index) {
        if (key_pos > keys_start + keys.size || keys_start + keys.size - key_pos < 8) return false;
        const uint32_t key_size = read_be32u(data.data() + key_pos);
        if (key_size < 8 || key_size > keys_start + keys.size - key_pos) return false;
        if (std::memcmp(data.data() + key_pos + 4, "mdta", 4) == 0 &&
            key_size - 8 == std::strlen("com.apple.quicktime.content.identifier") &&
            std::memcmp(data.data() + key_pos + 8, "com.apple.quicktime.content.identifier", key_size - 8) == 0) {
            content_key_index = index;
        }
        key_pos += key_size;
    }
    if (content_key_index == 0 || key_pos != keys_start + keys.size) { set_error(context, "Apple MOV content identifier key was not found."); return false; }

    p = ilst_start + ilst.header_size;
    const size_t ilst_end = ilst_start + ilst.size;
    uint32_t item_count_seen = 0;
    while (p < ilst_end) {
        isobmff_box_header item{};
        if (!try_read_box_header(data.data(), p, ilst_end, item) || item.size < item.header_size + 8) { set_error(context, "Apple MOV ilst entry is malformed."); return false; }
        // ilst item type is the 32-bit mdta key index, not a FourCC.
        const uint32_t item_index = read_be32u(data.data() + p + 4);
        ++item_count_seen;
        if (item_index == content_key_index) {
            const size_t data_pos = p + item.header_size;
            isobmff_box_header value_box{};
            if (!try_read_box_header(data.data(), data_pos, p + item.size, value_box) ||
                std::memcmp(data.data() + data_pos + 4, "data", 4) != 0 || value_box.size < value_box.header_size + 8) { set_error(context, "Apple MOV content identifier data box is malformed."); return false; }
            const size_t value_start = data_pos + value_box.header_size + 8;
            const size_t value_end = data_pos + value_box.size;
            std::string_view value(reinterpret_cast<const char*>(data.data() + value_start), value_end - value_start);
            while (!value.empty() && value.back() == '\0') value.remove_suffix(1);
            if (!looks_like_uuid(value)) { set_error(context, "Apple MOV content identifier value is not a UUID."); return false; }
            out.assign(value);
            return true;
        }
        p += item.size;
    }
    char diagnostic[160]{};
    snprintf(diagnostic, sizeof(diagnostic), "Apple MOV content identifier ilst value was not found (key=%u, items=%u).", content_key_index, item_count_seen);
    set_error(context, diagnostic);
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
        auto sec_data = read_file_bytes(secondary_path);
        lpb_video_container sec_vid_cont = detect_video_container(sec_data);

        if (sec_vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN && secondary_size > 0) {
            std::string vivo_image_id;
            std::string vivo_video_id;
            const bool secondary_structurally_valid = sec_vid_cont != LPB_VIDEO_CONTAINER_MP4 ||
                is_valid_isobmff_media_range(sec_data.data(), sec_data.size(), 0, sec_data.size());
            if (secondary_structurally_valid && has_jpeg_end && extract_vivo_id_from_image(primary_data, jpeg_end, vivo_image_id) &&
                extract_vivo_id_from_video(sec_data, vivo_video_id) && vivo_image_id == vivo_video_id) {
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
                // Pairing has already validated the complete secondary box
                // range and matched both IDs. Do not make recognition depend
                // on the optional deep stream probe (vendor vivo MOV/MP4
                // metadata can be valid but outside the probe's scope).
                strncpy_s(out_facts->pairing_identifier, vivo_image_id.c_str(), _TRUNCATE);
                return LPB_RESULT_OK;
            }

            std::string image_content_id;
            std::string video_content_id;
            const bool image_id_ok = extract_apple_cid_from_image(context, primary_data, img_cont, image_content_id);
            const bool video_id_ok = extract_apple_cid_from_video(context, sec_data, video_content_id);
            const bool same_named_legacy_candidate = !image_id_ok && !video_id_ok &&
                fs::path(primary_path).stem() == fs::path(secondary_path).stem() &&
                has_apple_live_makernote_tag(primary_data.data(), primary_data.size()) &&
                std::string_view(reinterpret_cast<const char*>(sec_data.data()), sec_data.size()).find("mebx") != std::string_view::npos;
            if ((image_id_ok && video_id_ok && image_content_id == video_content_id) || same_named_legacy_candidate) {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = sec_vid_cont;
                out_facts->motion_video.file_range.offset = 0;
                out_facts->motion_video.file_range.length = secondary_size;
                // Apple pairing is established from the bounded ContentIdentifier
                // metadata above; stream details are populated by the caller when
                // needed and are not part of pairing validation.
                if (image_id_ok && video_id_ok) strncpy_s(out_facts->pairing_identifier, image_content_id.c_str(), _TRUNCATE);
                return LPB_RESULT_OK;
            }
        }
    }

    // Single file checks
    std::string xmp = extract_xmp_string(context, primary_data, img_cont);
    std::vector<xmp_element> xmp_elements;
    if (!xmp.empty() && !scan_xmp_elements(xmp, xmp_elements)) {
        xmp_elements.clear();
    }

    // 1. Check vivo X300+ 3-item container
    uint64_t pri_len = 0, gm_off = 0, gm_len = 0, vid_off = 0, vid_len = 0;
    if (!xmp_elements.empty() && check_vivo_x300(xmp_elements, primary_size, pri_len, gm_off, gm_len, vid_off, vid_len) &&
        is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), vid_off, vid_len) &&
        gm_off <= primary_data.size() && gm_len <= primary_data.size() - static_cast<size_t>(gm_off) &&
        gm_len >= 2 && primary_data[static_cast<size_t>(gm_off)] == 0xFF &&
        primary_data[static_cast<size_t>(gm_off) + 1] == 0xD8) {
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
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && check_samsung_sef_jpeg(context, primary_data, vid_off, vid_len)) {
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
    if (img_cont == LPB_IMAGE_CONTAINER_HEIC && check_samsung_sef_heic(primary_data, vid_off, vid_len)) {
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
        // OPPO's VideoLength describes the trailing video range directly;
        // unlike Google/Vivo it does not require a Container:Directory item.
        const uint64_t item_len = op_vid_len;
        uint64_t video_offset = item_len <= primary_size ? primary_size - item_len : 0;
        if (item_len > primary_size || !is_valid_isobmff_media_range(
                primary_data.data(), primary_data.size(), video_offset, item_len)) {
            // OPPO files may append a private trailer after the MP4 while
            // VideoLength still describes only the complete MP4. Resolve the
            // real ftyp boundary by structural validation, never by a bare
            // string hit or by accepting a mid-box offset.
            video_offset = 0;
            bool found = false;
            const uint64_t latest_start = item_len <= primary_size ? primary_size - item_len : 0;
            for (size_t candidate = 0; item_len <= primary_size && candidate + 8 <= primary_data.size() &&
                static_cast<uint64_t>(candidate) <= latest_start; ++candidate) {
                if (primary_data[candidate + 4] != 'f' || primary_data[candidate + 5] != 't' ||
                    primary_data[candidate + 6] != 'y' || primary_data[candidate + 7] != 'p') continue;
                if (is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), candidate, item_len)) {
                    video_offset = candidate;
                    found = true;
                    break;
                }
            }
            if (!found) {
                set_error(context, "OPPO video range does not contain a structurally valid ISO-BMFF video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }

        out_facts->protocol = LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        if (item_len == 0 || item_len > primary_size || op_vid_len > item_len) {
            set_error(context, "OPPO protocol item length is outside the source file.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        out_facts->motion_video.file_range.offset = video_offset;
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
        mp_vid_len < primary_size &&
        is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), primary_size - mp_vid_len, mp_vid_len)) {
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
        mv_offset > 0 && mv_offset < primary_size &&
        is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), primary_size - mv_offset, mv_offset)) {
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
