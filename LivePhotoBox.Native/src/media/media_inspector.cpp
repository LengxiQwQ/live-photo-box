#include "media/media_inspector.h"
#include "media/video_converter.h"
#include "foundation/internal.h"
#include "foundation/sha256.h"
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
    std::string_view resolved_uri;
};

static constexpr size_t k_no_parent = std::numeric_limits<size_t>::max();

struct xmp_node {
    size_t node_index{0};
    size_t parent_index{k_no_parent};
    std::string_view tag_name;
    std::string_view resolved_uri;
    std::vector<xmp_attribute> attributes;
    std::vector<size_t> children;
};

static constexpr std::string_view google_camera_namespace = "http://ns.google.com/photos/1.0/camera/";
static constexpr std::string_view google_container_namespace = "http://ns.google.com/photos/1.0/container/";
static constexpr std::string_view google_item_namespace = "http://ns.google.com/photos/1.0/container/item/";
static constexpr std::string_view oppo_camera_namespace = "http://ns.oplus.com/photos/1.0/camera/";
static constexpr std::string_view vivo_camera_namespace = "http://ns.vivo.com/photos/1.0/camera/";
static constexpr std::string_view rdf_namespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

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
        for (const auto& existing : attributes) {
            if (existing.name == attr_name) return false; // duplicate attribute rejected
        }
        attributes.push_back({ attr_name, xml.substr(value_start, p - value_start), {} });
        ++p;
    }
    return true;
}

static bool parse_close_tag(std::string_view xml, size_t start, size_t end, std::string_view& tag_name) noexcept {
    if (start >= end || xml[start] != '<' || start + 2 >= end || xml[start + 1] != '/') return false;
    size_t p = start + 2;
    while (p < end && std::isspace(static_cast<unsigned char>(xml[p]))) ++p;
    const size_t name_start = p;
    while (p < end && is_xml_name_char(xml[p])) ++p;
    if (p == name_start) return false;
    tag_name = xml.substr(name_start, p - name_start);
    while (p < end && std::isspace(static_cast<unsigned char>(xml[p]))) ++p;
    return p < end && xml[p] == '>';
}

static bool scan_xmp_tree(std::string_view xml, std::vector<xmp_node>& nodes) {
    std::vector<std::vector<namespace_binding>> scopes;
    std::vector<size_t> open_stack;
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
            std::string_view closing_name;
            if (!parse_close_tag(xml, p, tag_end, closing_name)) return false;
            if (open_stack.empty()) return false;
            const size_t top_idx = open_stack.back();
            if (nodes[top_idx].tag_name != closing_name) return false; // Mismatched closing tag
            open_stack.pop_back();
            scopes.pop_back();
            p = tag_end;
            continue;
        }

        std::string_view tag_name;
        std::vector<xmp_attribute> attributes;
        if (!parse_start_tag(xml, p, tag_end, tag_name, attributes)) return false;

        std::vector<namespace_binding> bindings = scopes.empty() ?
            std::vector<namespace_binding>{} : scopes.back();
        for (const auto& attribute : attributes) {
            if (attribute.name == "xmlns") {
                set_namespace_binding(bindings, {}, attribute.value);
            } else if (attribute.name.size() > 6 && attribute.name.substr(0, 6) == "xmlns:") {
                set_namespace_binding(bindings, attribute.name.substr(6), attribute.value);
            }
        }

        const std::string_view tag_uri = namespace_uri_for_name(tag_name, bindings, false);
        for (auto& attr : attributes) {
            attr.resolved_uri = namespace_uri_for_name(attr.name, bindings, true);
        }

        const size_t new_idx = nodes.size();
        const size_t parent_idx = open_stack.empty() ? k_no_parent : open_stack.back();
        nodes.push_back({ new_idx, parent_idx, tag_name, tag_uri, std::move(attributes), {} });
        if (parent_idx != k_no_parent) {
            nodes[parent_idx].children.push_back(new_idx);
        }

        const bool self_closing = tag_end >= 2 && xml[tag_end - 2] == '/';
        if (!self_closing) {
            open_stack.push_back(new_idx);
            scopes.push_back(std::move(bindings));
        }
        p = tag_end;
    }
    return open_stack.empty() && scopes.empty();
}

static bool parse_u64_exact(std::string_view sv, uint64_t& out_val) noexcept {
    if (sv.empty()) return false;
    const char* first = sv.data();
    const char* last = first + sv.size();
    auto res = std::from_chars(first, last, out_val, 10);
    return res.ec == std::errc{} && res.ptr == last;
}

static bool parse_i64_exact(std::string_view sv, int64_t& out_val) noexcept {
    if (sv.empty()) return false;
    const char* first = sv.data();
    const char* last = first + sv.size();
    auto res = std::from_chars(first, last, out_val, 10);
    return res.ec == std::errc{} && res.ptr == last;
}

static bool node_is(const xmp_node& node, std::string_view uri, std::string_view local) noexcept {
    return local_name(node.tag_name) == local && node.resolved_uri == uri;
}

static bool get_node_attribute_value(const xmp_node& node, std::string_view uri,
    std::string_view local, std::string_view& out_value) noexcept {
    for (const auto& attr : node.attributes) {
        if (local_name(attr.name) == local && attr.resolved_uri == uri) {
            out_value = attr.value;
            return true;
        }
    }
    return false;
}

static bool has_attribute_name_in_nodes(const std::vector<xmp_node>& nodes,
    std::string_view uri, std::string_view local) noexcept {
    for (const auto& node : nodes) {
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) return true;
        }
    }
    return false;
}

static int get_global_attribute_u64(const std::vector<xmp_node>& nodes,
    std::string_view uri, std::string_view local, uint64_t& out_value) noexcept {
    bool found = false;
    uint64_t current = 0;
    for (const auto& node : nodes) {
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) {
                uint64_t val = 0;
                if (!parse_u64_exact(attr.value, val)) return -1;
                if (!found) {
                    found = true;
                    current = val;
                } else if (current != val) {
                    return -1;
                }
            }
        }
    }
    if (found) {
        out_value = current;
        return 1;
    }
    return 0;
}

static int get_global_attribute_i64(const std::vector<xmp_node>& nodes,
    std::string_view uri, std::string_view local, int64_t& out_value) noexcept {
    bool found = false;
    int64_t current = 0;
    for (const auto& node : nodes) {
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) {
                int64_t val = 0;
                if (!parse_i64_exact(attr.value, val)) return -1;
                if (!found) {
                    found = true;
                    current = val;
                } else if (current != val) {
                    return -1;
                }
            }
        }
    }
    if (found) {
        out_value = current;
        return 1;
    }
    return 0;
}

static int get_global_attribute_string(const std::vector<xmp_node>& nodes,
    std::string_view uri, std::string_view local, std::string_view& out_value) noexcept {
    bool found = false;
    std::string_view current;
    for (const auto& node : nodes) {
        for (const auto& attr : node.attributes) {
            if (local_name(attr.name) == local && attr.resolved_uri == uri) {
                if (!found) {
                    found = true;
                    current = attr.value;
                } else if (current != attr.value) {
                    return -1;
                }
            }
        }
    }
    if (found) {
        out_value = current;
        return 1;
    }
    return 0;
}

struct container_item_info {
    std::string_view semantic;
    std::string_view mime;
    uint64_t length{0};
    uint64_t padding{0};
    bool has_length{false};
    bool has_padding{false};
    bool malformed_length{false};
    bool malformed_padding{false};
    size_t node_index{0};
};

struct container_directory_info {
    size_t directory_node_index{k_no_parent};
    size_t seq_node_index{k_no_parent};
    std::vector<container_item_info> items;
};

static bool find_container_directory(const std::vector<xmp_node>& nodes,
    container_directory_info& out_dir) {
    out_dir = {};
    const xmp_node* dir_node = nullptr;
    for (const auto& node : nodes) {
        if (node_is(node, google_container_namespace, "Directory")) {
            if (dir_node != nullptr) return false;
            dir_node = &node;
        }
    }
    if (!dir_node) return false;
    out_dir.directory_node_index = dir_node->node_index;

    const xmp_node* seq_node = nullptr;
    for (size_t child_idx : dir_node->children) {
        if (child_idx < nodes.size() && node_is(nodes[child_idx], rdf_namespace, "Seq")) {
            if (seq_node != nullptr) return false;
            seq_node = &nodes[child_idx];
        }
    }
    if (!seq_node) return false;
    out_dir.seq_node_index = seq_node->node_index;

    for (size_t seq_item_idx : seq_node->children) {
        if (seq_item_idx >= nodes.size()) continue;
        const xmp_node& seq_item = nodes[seq_item_idx];

        const xmp_node* item_node = nullptr;
        if (node_is(seq_item, google_container_namespace, "Item")) {
            item_node = &seq_item;
        } else if (node_is(seq_item, rdf_namespace, "li")) {
            for (size_t li_child_idx : seq_item.children) {
                if (li_child_idx < nodes.size() && node_is(nodes[li_child_idx], google_container_namespace, "Item")) {
                    item_node = &nodes[li_child_idx];
                    break;
                }
            }
            if (!item_node) {
                if (has_attribute_name_in_nodes({ seq_item }, google_item_namespace, "Semantic") ||
                    has_attribute_name_in_nodes({ seq_item }, google_item_namespace, "Mime")) {
                    item_node = &seq_item;
                }
            }
        }
        if (!item_node) continue;

        container_item_info info{};
        info.node_index = item_node->node_index;
        if (!get_node_attribute_value(*item_node, google_item_namespace, "Semantic", info.semantic) && item_node != &seq_item) {
            get_node_attribute_value(seq_item, google_item_namespace, "Semantic", info.semantic);
        }
        if (!get_node_attribute_value(*item_node, google_item_namespace, "Mime", info.mime) && item_node != &seq_item) {
            get_node_attribute_value(seq_item, google_item_namespace, "Mime", info.mime);
        }
        std::string_view len_str;
        if (get_node_attribute_value(*item_node, google_item_namespace, "Length", len_str) ||
            (item_node != &seq_item && get_node_attribute_value(seq_item, google_item_namespace, "Length", len_str))) {
            if (parse_u64_exact(len_str, info.length)) {
                info.has_length = true;
            } else {
                info.malformed_length = true;
            }
        }
        std::string_view pad_str;
        if (get_node_attribute_value(*item_node, google_item_namespace, "Padding", pad_str) ||
            (item_node != &seq_item && get_node_attribute_value(seq_item, google_item_namespace, "Padding", pad_str))) {
            if (parse_u64_exact(pad_str, info.padding)) {
                info.has_padding = true;
            } else {
                info.malformed_padding = true;
            }
        }
        if (info.semantic.empty()) continue;
        out_dir.items.push_back(info);
    }

    return !out_dir.items.empty();
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
    if (file.gcount() != static_cast<std::streamsize>(to_read)) return {};
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
        if (data[pos] != 0xFF) return false;
        while (pos < data.size() && data[pos] == 0xFF) ++pos;
        if (pos >= data.size()) return false;
        uint8_t marker = data[pos++];
        if (marker == 0x00 || marker == 0xFF || (marker >= 0xD0 && marker <= 0xD7)) return false;
        if (marker == 0xD9) { out_end = pos; return true; }
        if (marker == 0xDA)
        {
            if (pos + 2 > data.size()) return false;
            const size_t len = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
            if (len < 2 || len > data.size() - pos) return false;
            pos += len;
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
        if (pos + 2 > data.size()) return false;
        const size_t len = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
        if (len < 2 || len > data.size() - pos) return false;
        pos += len;
    }
    return false;
}

static bool is_valid_jpeg_media_range(
    const uint8_t* data,
    size_t total_size,
    uint64_t offset,
    uint64_t length) noexcept
{
    if (!data || offset > total_size || length < 4 || length > total_size - static_cast<size_t>(offset)) return false;
    const size_t start = static_cast<size_t>(offset);
    const size_t end = start + static_cast<size_t>(length);
    if (data[start] != 0xFF || data[start + 1] != 0xD8) return false;

    size_t pos = start + 2;
    while (pos + 1 < end)
    {
        if (data[pos] != 0xFF) return false;
        while (pos < end && data[pos] == 0xFF) ++pos;
        if (pos >= end) return false;
        uint8_t marker = data[pos++];
        if (marker == 0x00 || marker == 0xFF || (marker >= 0xD0 && marker <= 0xD7)) return false;
        if (marker == 0xD9)
        {
            while (pos < end && (data[pos] == 0x00 || data[pos] == 0xFF)) ++pos;
            return pos == end;
        }
        if (marker == 0xDA)
        {
            if (pos + 2 > end) return false;
            const size_t len = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
            if (len < 2 || len > end - pos) return false;
            pos += len;
            while (pos + 1 < end)
            {
                if (data[pos] == 0xFF)
                {
                    const uint8_t scan_marker = data[pos + 1];
                    if (scan_marker == 0xD9)
                    {
                        pos += 2;
                        while (pos < end && (data[pos] == 0x00 || data[pos] == 0xFF)) ++pos;
                        return pos == end;
                    }
                    if (scan_marker == 0x00 || (scan_marker >= 0xD0 && scan_marker <= 0xD7))
                    {
                        pos += 2;
                        continue;
                    }
                }
                ++pos;
            }
            return false;
        }
        if (pos + 2 > end) return false;
        const size_t len = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
        if (len < 2 || len > end - pos) return false;
        pos += len;
    }
    return false;
}

static bool is_valid_jpeg_or_composite_media_range(
    const uint8_t* data,
    size_t total_size,
    uint64_t offset,
    uint64_t length) noexcept
{
    if (!data || offset > total_size || length < 4 || length > total_size - static_cast<size_t>(offset)) return false;
    size_t cur = static_cast<size_t>(offset);
    const size_t end = cur + static_cast<size_t>(length);
    while (cur < end) {
        if (cur + 4 > end || data[cur] != 0xFF || data[cur + 1] != 0xD8) return false;
        size_t pos = cur + 2;
        bool found_eoi = false;
        while (pos + 1 < end) {
            if (data[pos] != 0xFF) return false;
            while (pos < end && data[pos] == 0xFF) ++pos;
            if (pos >= end) return false;
            uint8_t marker = data[pos++];
            if (marker == 0x00 || marker == 0xFF || (marker >= 0xD0 && marker <= 0xD7)) return false;
            if (marker == 0xD9) {
                while (pos + 1 < end && data[pos] == 0xFF && data[pos + 1] == 0xFF) ++pos;
                found_eoi = true;
                cur = pos;
                break;
            }
            if (marker == 0xDA) {
                if (pos + 2 > end) return false;
                const size_t len = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
                if (len < 2 || len > end - pos) return false;
                pos += len;
                while (pos + 1 < end) {
                    if (data[pos] == 0xFF) {
                        const uint8_t scan_marker = data[pos + 1];
                        if (scan_marker == 0xD9) {
                            pos += 2;
                            while (pos + 1 < end && data[pos] == 0xFF && data[pos + 1] == 0xFF) ++pos;
                            found_eoi = true;
                            cur = pos;
                            break;
                        }
                        if (scan_marker == 0x00 || (scan_marker >= 0xD0 && scan_marker <= 0xD7)) {
                            pos += 2;
                            continue;
                        }
                    }
                    ++pos;
                }
                break;
            }
            if (pos + 2 > end) return false;
            const size_t len = (static_cast<size_t>(data[pos]) << 8) | data[pos + 1];
            if (len < 2 || len > end - pos) return false;
            pos += len;
        }
        if (!found_eoi) return false;
    }
    while (cur < end && (data[cur] == 0x00 || data[cur] == 0xFF)) ++cur;
    return cur == end;
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

static int check_samsung_sef_jpeg(
    lpb_context* context,
    const std::vector<uint8_t>& data,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    lpb_result res = lpb_samsung_sef_parse(context, data.data(), data.size(), &out_video_offset, &out_video_len);
    if (res == LPB_RESULT_OK) {
        return 1;
    }
    if (context != nullptr) {
        std::scoped_lock lock(context->error_mutex);
        if (context->last_error == "MotionPhoto_Data entry not found in SEF.") {
            context->last_error.clear();
            return 0;
        }
    }
    return -1;
}

static bool check_samsung_sef_heic(
    const std::vector<uint8_t>& data,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    size_t pos = 0;
    bool found_mpvd = false;
    bool found_sefd = false;
    isobmff_box_header sefd_box{};
    uint64_t video_offset = 0;
    uint64_t video_length = 0;
    while (pos + 8 <= data.size()) {
        isobmff_box_header box{};
        if (!try_read_box_header(data.data(), pos, data.size(), box)) return false;
        if (std::memcmp(data.data() + pos + 4, "mpvd", 4) == 0) {
            if (found_mpvd) return false;
            found_mpvd = true;
            const size_t candidate_video_start = pos + box.header_size;
            size_t candidate_video_end = pos + box.size;
            size_t nested_pos = candidate_video_start;
            while (nested_pos < candidate_video_end) {
                isobmff_box_header nested{};
                if (!try_read_box_header(data.data(), nested_pos, candidate_video_end, nested)) return false;
                if (std::memcmp(data.data() + nested_pos + 4, "sefd", 4) == 0) {
                    if (found_sefd) return false;
                    if (nested_pos + nested.size != pos + box.size) return false;
                    found_sefd = true;
                    sefd_box = nested;
                    candidate_video_end = nested_pos;
                    break;
                }
                nested_pos += nested.size;
            }
            if (candidate_video_end <= candidate_video_start ||
                !is_valid_isobmff_media_range(data.data(), data.size(), candidate_video_start,
                    candidate_video_end - candidate_video_start)) return false;
            video_offset = candidate_video_start;
            video_length = candidate_video_end - candidate_video_start;
        } else if (std::memcmp(data.data() + pos + 4, "sefd", 4) == 0) {
            if (found_sefd || box.header_size != 8) return false;
            found_sefd = true;
            sefd_box = box;
        }
        pos += box.size;
    }
    if (!found_mpvd || !found_sefd || pos != data.size() || sefd_box.size < 16) return false;

    const auto le16 = [&](size_t at) noexcept -> uint16_t {
        return static_cast<uint16_t>(data[at]) | (static_cast<uint16_t>(data[at + 1]) << 8);
    };
    const auto le32 = [&](size_t at) noexcept -> uint32_t {
        return static_cast<uint32_t>(data[at]) | (static_cast<uint32_t>(data[at + 1]) << 8) |
            (static_cast<uint32_t>(data[at + 2]) << 16) | (static_cast<uint32_t>(data[at + 3]) << 24);
    };
    const size_t sefd_end = sefd_box.start + sefd_box.size;
    const size_t footer = sefd_end - 8;
    if (std::memcmp(data.data() + footer + 4, "SEFT", 4) != 0) return false;
    const uint32_t total_size = le32(footer);
    if (total_size < 12 || static_cast<uint64_t>(total_size) > sefd_box.size - 8) return false;
    const size_t sefh = footer - static_cast<size_t>(total_size);
    if (sefh < sefd_box.start + sefd_box.header_size || sefh + 12 > footer ||
        std::memcmp(data.data() + sefh, "SEFH", 4) != 0) return false;
    const uint32_t count = le32(sefh + 8);
    if (count > (footer - (sefh + 12)) / 12 || sefh + 12 + static_cast<size_t>(count) * 12 != footer) return false;

    bool found_motion = false;
    std::vector<std::pair<size_t, size_t>> payloads;
    for (uint32_t i = 0; i < count; ++i) {
        const size_t entry = sefh + 12 + static_cast<size_t>(i) * 12;
        const uint16_t prefix = le16(entry);
        const uint16_t marker = le16(entry + 2);
        const uint32_t offset = le32(entry + 4);
        const uint32_t size = le32(entry + 8);
        if (size < 8 || static_cast<uint64_t>(offset) > sefh || size > offset) return false;
        const size_t payload = sefh - static_cast<size_t>(offset);
        const size_t payload_end = payload + static_cast<size_t>(size);
        if (payload < sefd_box.start + sefd_box.header_size || payload_end > sefh ||
            le16(payload) != prefix || le16(payload + 2) != marker) return false;
        for (const auto& range : payloads) {
            if (payload < range.second && range.first < payload_end) return false;
        }
        payloads.emplace_back(payload, payload_end);
        const uint32_t name_size = le32(payload + 4);
        if (name_size > size - 8) return false;
        if (marker == 0x0A30) {
            if (found_motion || prefix != 0 || name_size != 16 || size != 36 ||
                std::memcmp(data.data() + payload + 8, "MotionPhoto_Data", 16) != 0 ||
                std::memcmp(data.data() + payload + 24, "mpv2", 4) != 0 ||
                read_be32u(data.data() + payload + 28) != video_offset ||
                read_be32u(data.data() + payload + 32) != video_length) return false;
            found_motion = true;
        }
    }
    if (found_motion) {
        out_video_offset = video_offset;
        out_video_len = video_length;
    }
    return found_motion;
}

static bool check_vivo_x300(
    const std::vector<xmp_node>& nodes,
    uint64_t file_size,
    uint64_t& out_primary_len,
    uint64_t& out_gm_offset,
    uint64_t& out_gm_len,
    uint64_t& out_video_offset,
    uint64_t& out_video_len)
{
    container_directory_info dir;
    if (!find_container_directory(nodes, dir) || dir.items.size() != 3) {
        return false;
    }

    const auto& item0 = dir.items[0];
    const auto& item1 = dir.items[1];
    const auto& item2 = dir.items[2];

    if (item0.semantic != "Primary" || (!item0.mime.empty() && item0.mime != "image/jpeg")) {
        return false;
    }
    if (item1.semantic != "GainMap" || item1.mime != "image/jpeg" || !item1.has_length || item1.length == 0) {
        return false;
    }
    if (item2.semantic != "MotionPhoto" || item2.mime != "video/mp4" || !item2.has_length || item2.length == 0) {
        return false;
    }

    const uint64_t gainmap_length = item1.length;
    const uint64_t motion_length = item2.length;

    if (gainmap_length >= file_size || motion_length >= file_size ||
        gainmap_length + motion_length >= file_size) {
        return false;
    }

    out_video_len = motion_length;
    out_video_offset = file_size - motion_length;
    out_gm_len = gainmap_length;
    out_gm_offset = out_video_offset - gainmap_length;
    out_primary_len = out_gm_offset;
    return true;
}

static bool looks_like_uuid(std::string_view value) noexcept {
    if (value.size() != 36) return false;
    for (size_t i = 0; i < value.size(); ++i) {
        const bool hex = std::isxdigit(static_cast<unsigned char>(value[i])) != 0;
        if ((i == 8 || i == 13 || i == 18 || i == 23) ? value[i] != '-' : !hex) return false;
    }
    return true;
}

static bool extract_apple_cid_from_makernote(const uint8_t* data, size_t start, size_t end, std::string& out, bool& out_has_conflict) {
    out_has_conflict = false;
    if (!data || start > end || end - start < 30) return false;
    const char signature[] = "Apple iOS\0";
    std::string found_cid;
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
            if (looks_like_uuid(id)) {
                if (!found_cid.empty() && found_cid != id) {
                    out_has_conflict = true;
                    return false;
                }
                found_cid.assign(id);
            }
        }
    }
    if (!found_cid.empty()) {
        out = found_cid;
        return true;
    }
    return false;
}

static bool extract_apple_cid_from_image(lpb_context* context, const std::vector<uint8_t>& data,
    lpb_image_container container, std::string& out, bool& out_has_conflict) {
    out_has_conflict = false;
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
            if (marker == 0xE1 && payload_size >= 6 && std::memcmp(data.data() + payload, "Exif\0\0", 6) == 0) {
                if (extract_apple_cid_from_makernote(data.data(), payload + 6, payload + payload_size, out, out_has_conflict)) {
                    return true;
                }
                if (out_has_conflict) return false;
            }
            p = payload + payload_size;
        }
        return false;
    }
    if (container == LPB_IMAGE_CONTAINER_HEIC) {
        uint64_t offset = 0, length = 0;
        if (lpb_heif_locate_exif_item(context, data.data(), data.size(), &offset, &length) != LPB_RESULT_OK ||
            offset > data.size() || length > data.size() - static_cast<size_t>(offset)) return false;
        return extract_apple_cid_from_makernote(data.data(), static_cast<size_t>(offset),
            static_cast<size_t>(offset + length), out, out_has_conflict);
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
    std::string found_id;
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
            if (!found_id.empty() && found_id != value) {
                set_error(context, "Apple MOV contains conflicting content identifiers.");
                return false;
            }
            found_id.assign(value);
        }
        p += item.size;
    }
    if (!found_id.empty()) {
        out = found_id;
        return true;
    }
    char diagnostic[160]{};
    snprintf(diagnostic, sizeof(diagnostic), "Apple MOV content identifier ilst value was not found (key=%u, items=%u).", content_key_index, item_count_seen);
    set_error(context, diagnostic);
    return false;
}

static bool is_valid_heic_container(const uint8_t* data, size_t size) noexcept {
    if (!data || size < 12) return false;
    size_t pos = 0;
    bool saw_ftyp = false;
    while (pos + 8 <= size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, pos, size, box)) return false;
        if (pos == 0) {
            if (std::memcmp(data + pos + 4, "ftyp", 4) != 0) return false;
            saw_ftyp = true;
        }
        pos += box.size;
    }
    return pos == size && saw_ftyp;
}

static bool is_valid_mov_container(const uint8_t* data, size_t size) noexcept {
    if (!data || size < 8) return false;
    size_t pos = 0;
    while (pos + 8 <= size) {
        isobmff_box_header box{};
        if (!try_read_box_header(data, pos, size, box)) return false;
        pos += box.size;
    }
    return pos == size;
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

    if (out_facts->struct_size < sizeof(lpb_source_media_facts)) {
        set_error(context, "out_facts struct_size is smaller than expected lpb_source_media_facts size.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    std::memset(out_facts, 0, sizeof(lpb_source_media_facts));
    out_facts->struct_size = sizeof(lpb_source_media_facts);
    out_facts->primary_image.struct_size = sizeof(lpb_image_item_facts);
    out_facts->motion_video.struct_size = sizeof(lpb_video_item_facts);
    out_facts->gain_map.struct_size = sizeof(lpb_gainmap_item_facts);
    out_facts->timing.struct_size = sizeof(lpb_timing_facts);

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
    primary_size = primary_data.size();
    lpb::crypto::sha256_buffer(primary_data.data(), primary_data.size(), out_facts->primary_sha256);

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
        auto sec_data = read_file_bytes(secondary_path);
        uint64_t secondary_size = sec_data.size();
        if (secondary_size == 0) {
            set_error(context, "Secondary file is empty or does not exist.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        lpb::crypto::sha256_buffer(sec_data.data(), sec_data.size(), out_facts->secondary_sha256);
        out_facts->has_secondary_source = 1;

        lpb_video_container sec_vid_cont = detect_video_container(sec_data);
        if (sec_vid_cont == LPB_VIDEO_CONTAINER_UNKNOWN) {
            set_error(context, "Secondary file is not a supported video container.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // 1. Check Apple Live Photo Dual-File
        std::string image_content_id;
        std::string video_content_id;
        bool image_has_conflict = false;
        const bool image_id_ok = extract_apple_cid_from_image(context, primary_data, img_cont, image_content_id, image_has_conflict);
        if (image_has_conflict) {
            set_error(context, "Apple image contains conflicting ContentIdentifiers in MakerNote.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        const bool video_id_ok = extract_apple_cid_from_video(context, sec_data, video_content_id);

        if (image_id_ok && video_id_ok) {
            if (image_content_id == video_content_id) {
                if (img_cont == LPB_IMAGE_CONTAINER_JPEG) {
                    if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, primary_size)) {
                        set_error(context, "Apple Live Photo primary JPEG image is structurally malformed or truncated.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
                    if (!is_valid_heic_container(primary_data.data(), primary_data.size())) {
                        set_error(context, "Apple Live Photo primary HEIC image is structurally malformed.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else {
                    set_error(context, "Apple Live Photo primary image container is unsupported.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }

                if (sec_vid_cont == LPB_VIDEO_CONTAINER_MOV) {
                    if (!is_valid_mov_container(sec_data.data(), sec_data.size())) {
                        set_error(context, "Apple Live Photo secondary MOV video is structurally malformed.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else if (sec_vid_cont == LPB_VIDEO_CONTAINER_MP4) {
                    if (!is_valid_isobmff_media_range(sec_data.data(), sec_data.size(), 0, sec_data.size())) {
                        set_error(context, "Apple Live Photo secondary MP4 video is structurally malformed.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else {
                    set_error(context, "Apple Live Photo secondary video container is unsupported.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }

                out_facts->protocol = LPB_SOURCE_PROTOCOL_APPLE_LIVE_PHOTO;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = sec_vid_cont;
                out_facts->motion_video.file_range.offset = 0;
                out_facts->motion_video.file_range.length = secondary_size;
                out_facts->motion_video.source_index = 1;
                strncpy_s(out_facts->pairing_identifier, image_content_id.c_str(), _TRUNCATE);
                return LPB_RESULT_OK;
            } else {
                set_error(context, "Apple Live Photo dual-file pairing identifier mismatch.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        if (image_id_ok && !video_id_ok) {
            set_error(context, "Apple Live Photo dual-file pairing mismatch: missing ContentIdentifier on secondary video.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!image_id_ok && video_id_ok) {
            set_error(context, "Apple Live Photo dual-file pairing mismatch: missing ContentIdentifier on primary image.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // 2. Check Vivo Legacy Dual-File
        std::string vivo_image_id;
        std::string vivo_video_id;
        const bool vivo_img_ok = has_jpeg_end && extract_vivo_id_from_image(primary_data, jpeg_end, vivo_image_id);
        const bool vivo_vid_ok = extract_vivo_id_from_video(sec_data, vivo_video_id);

        if (vivo_img_ok && vivo_vid_ok) {
            const bool secondary_structurally_valid = sec_vid_cont != LPB_VIDEO_CONTAINER_MP4 ||
                is_valid_isobmff_media_range(sec_data.data(), sec_data.size(), 0, sec_data.size());
            if (!secondary_structurally_valid) {
                set_error(context, "Vivo legacy secondary video is not a valid ISO-BMFF container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (vivo_image_id == vivo_video_id) {
                out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_LEGACY_DUAL;
                out_facts->motion_video.is_present = 1;
                out_facts->motion_video.container = sec_vid_cont;
                out_facts->motion_video.file_range.offset = 0;
                out_facts->motion_video.file_range.length = secondary_size;
                out_facts->motion_video.source_index = 1;
                if (has_jpeg_end && jpeg_end < primary_size) {
                    out_facts->primary_image.file_range.length = jpeg_end;
                    out_facts->protocol_tail_range.offset = jpeg_end;
                    out_facts->protocol_tail_range.length = primary_size - jpeg_end;
                }
                strncpy_s(out_facts->pairing_identifier, vivo_image_id.c_str(), _TRUNCATE);
                return LPB_RESULT_OK;
            } else {
                set_error(context, "Vivo legacy dual-file pairing identifier mismatch.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        if (vivo_img_ok && !vivo_vid_ok) {
            set_error(context, "Vivo legacy dual-file pairing mismatch: missing pairing identifier on secondary video.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (!vivo_img_ok && vivo_vid_ok) {
            set_error(context, "Vivo legacy dual-file pairing mismatch: missing pairing identifier on primary image.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        // If neither file contains Apple or Vivo pairing identity,
        // and both containers are structurally valid, this is a non-live pair.
        if (!image_id_ok && !video_id_ok && !vivo_img_ok && !vivo_vid_ok) {
            if (img_cont == LPB_IMAGE_CONTAINER_JPEG && !has_jpeg_end) {
                set_error(context, "Primary image is a malformed or truncated JPEG.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (img_cont == LPB_IMAGE_CONTAINER_HEIC && !is_valid_heic_container(primary_data.data(), primary_data.size())) {
                set_error(context, "Primary image is a malformed HEIC container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (sec_vid_cont == LPB_VIDEO_CONTAINER_MP4 && !is_valid_isobmff_media_range(sec_data.data(), sec_data.size(), 0, sec_data.size())) {
                set_error(context, "Secondary video is a malformed MP4 video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (sec_vid_cont == LPB_VIDEO_CONTAINER_MOV && !is_valid_mov_container(sec_data.data(), sec_data.size())) {
                set_error(context, "Secondary video is a malformed MOV video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
            out_facts->primary_image.is_present = 1;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = primary_size;
            out_facts->protocol_tail_range.offset = 0;
            out_facts->protocol_tail_range.length = 0;
            return LPB_RESULT_OK;
        }

        set_error(context, "Dual-file inputs do not form a recognized live photo pair.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // Single file checks
    // 1. Huawei / Honor Moving Photo (ends with LIVE_)
    int64_t huawei_cover_time_us = 0;
    bool is_honor = false;
    uint64_t huawei_vid_off = 0, huawei_vid_len = 0;
    if (check_huawei_moving_photo(primary_data, primary_size, huawei_vid_off, huawei_vid_len, huawei_cover_time_us, is_honor)) {
        out_facts->protocol = is_honor ? LPB_SOURCE_PROTOCOL_HONOR_MOVING_PHOTO : LPB_SOURCE_PROTOCOL_HUAWEI_MOVING_PHOTO;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = huawei_vid_off;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
        out_facts->motion_video.file_range.offset = huawei_vid_off;
        out_facts->motion_video.file_range.length = huawei_vid_len;
        const uint64_t video_end = huawei_vid_off + huawei_vid_len;
        if (video_end < primary_size) {
            out_facts->protocol_tail_range.offset = video_end;
            out_facts->protocol_tail_range.length = primary_size - video_end;
        }
        out_facts->timing.cover_timestamp_us = huawei_cover_time_us;
        return LPB_RESULT_OK;
    } else if (primary_data.size() >= 20 &&
               std::memcmp(primary_data.data() + primary_data.size() - 20, "LIVE_", 5) == 0) {
        set_error(context, "Huawei/Honor Moving Photo trailer is malformed or video range is corrupt.");
        return LPB_RESULT_INVALID_ARGUMENT;
    }

    // 2. Samsung JPEG (SEFT Trailer)
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG && primary_data.size() >= 8 &&
        std::memcmp(primary_data.data() + primary_data.size() - 4, "SEFT", 4) == 0) {
        uint64_t sef_vid_off = 0, sef_vid_len = 0;
        int sef_res = check_samsung_sef_jpeg(context, primary_data, sef_vid_off, sef_vid_len);
        if (sef_res < 0) {
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        if (sef_res > 0) {
            out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_JPEG;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = primary_size;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = sef_vid_off;
            out_facts->motion_video.file_range.length = sef_vid_len;
            return LPB_RESULT_OK;
        }
    }

    // 3. Samsung HEIC (sefd box)
    if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
        bool has_sefd_box = false;
        size_t bpos = 0;
        while (bpos + 8 <= primary_data.size()) {
            isobmff_box_header bh{};
            if (!try_read_box_header(primary_data.data(), bpos, primary_data.size(), bh)) break;
            if (std::memcmp(primary_data.data() + bpos + 4, "sefd", 4) == 0) {
                has_sefd_box = true;
                break;
            }
            bpos += bh.size;
        }
        if (has_sefd_box) {
            uint64_t heic_vid_off = 0, heic_vid_len = 0;
            if (!check_samsung_sef_heic(primary_data, heic_vid_off, heic_vid_len)) {
                set_error(context, "Samsung HEIC sefd box or SEF directory is malformed.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_SAMSUNG_HEIC;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = primary_size;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = heic_vid_off;
            out_facts->motion_video.file_range.length = heic_vid_len;
            return LPB_RESULT_OK;
        }
    }

    // 4. XMP-based protocols
    std::string xmp = extract_xmp_string(context, primary_data, img_cont);
    std::vector<xmp_node> nodes;
    bool xmp_parsed = false;
    if (!xmp.empty()) {
        xmp_parsed = scan_xmp_tree(xmp, nodes);
        if (!xmp_parsed) {
            if (xmp.find("MotionPhoto") != std::string::npos ||
                xmp.find("VideoLength") != std::string::npos ||
                xmp.find("MicroVideoOffset") != std::string::npos ||
                xmp.find("VMotionPhotoVersion") != std::string::npos) {
                set_error(context, "Source image contains malformed or unparseable Live Photo XMP.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
    }

    if (xmp_parsed && !nodes.empty()) {
        // Vivo X300+
        const bool is_vivo_candidate = has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoVersion") ||
            has_attribute_name_in_nodes(nodes, vivo_camera_namespace, "VMotionPhotoFlags");
        if (is_vivo_candidate) {
            uint64_t v_ver = 0;
            int v_res = get_global_attribute_u64(nodes, vivo_camera_namespace, "VMotionPhotoVersion", v_ver);
            if (v_res < 0) {
                set_error(context, "Conflicting or malformed VCamera:VMotionPhotoVersion attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (v_res == 0) {
                set_error(context, "Vivo X300+ Live Photo candidate missing required VCamera:VMotionPhotoVersion.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (v_ver != 1) {
                set_error(context, "Vivo X300+ Live Photo candidate has unsupported VCamera:VMotionPhotoVersion (must be 1).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t pri_len = 0, gm_off = 0, gm_len = 0, vid_off = 0, vid_len = 0;
            if (!check_vivo_x300(nodes, primary_size, pri_len, gm_off, gm_len, vid_off, vid_len)) {
                set_error(context, "Vivo X300+ XMP contains invalid or missing container directory or malformed items.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), gm_off, gm_len)) {
                set_error(context, "Vivo X300+ GainMap range is not a valid JPEG.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), vid_off, vid_len)) {
                set_error(context, "Vivo X300+ video range is not a valid ISO-BMFF MP4.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_VIVO_X300;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = pri_len;
            out_facts->gain_map.is_present = 1;
            out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
            out_facts->gain_map.file_range.offset = gm_off;
            out_facts->gain_map.file_range.length = gm_len;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = vid_off;
            out_facts->motion_video.file_range.length = vid_len;
            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }
            return LPB_RESULT_OK;
        }

        // OPPO / OnePlus Live Photo
        const bool is_oppo_candidate = has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "VideoLength") ||
            has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "MotionPhotoOwner") ||
            has_attribute_name_in_nodes(nodes, oppo_camera_namespace, "OLivePhotoVersion");
        if (is_oppo_candidate) {
            std::string_view g_mp;
            int g_mp_res = get_global_attribute_string(nodes, google_camera_namespace, "MotionPhoto", g_mp);
            if (g_mp_res <= 0 || g_mp != "1") {
                set_error(context, "OPPO Live Photo candidate missing required GCamera:MotionPhoto=\"1\".");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t g_mp_ver = 0;
            int g_mp_ver_res = get_global_attribute_u64(nodes, google_camera_namespace, "MotionPhotoVersion", g_mp_ver);
            if (g_mp_ver_res <= 0 || g_mp_ver != 1) {
                set_error(context, "OPPO Live Photo candidate missing required GCamera:MotionPhotoVersion=1.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            std::string_view op_owner;
            int op_owner_res = get_global_attribute_string(nodes, oppo_camera_namespace, "MotionPhotoOwner", op_owner);
            if (op_owner_res <= 0 || op_owner != "oplus") {
                set_error(context, "OPPO Live Photo candidate missing or invalid OpCamera:MotionPhotoOwner (must be \"oplus\").");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t olive_ver = 0;
            int olive_res = get_global_attribute_u64(nodes, oppo_camera_namespace, "OLivePhotoVersion", olive_ver);
            if (olive_res < 0) {
                set_error(context, "Conflicting or malformed OpCamera:OLivePhotoVersion attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (olive_res == 0 || (olive_ver != 1 && olive_ver != 2)) {
                set_error(context, "OPPO Live Photo candidate missing or unsupported OpCamera:OLivePhotoVersion (must be 1 or 2).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t op_vid_len = 0;
            int op_res = get_global_attribute_u64(nodes, oppo_camera_namespace, "VideoLength", op_vid_len);
            if (op_res <= 0 || op_vid_len == 0) {
                set_error(context, "OPPO VideoLength attribute is missing, malformed, conflicting, or zero.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            container_directory_info dir;
            if (!find_container_directory(nodes, dir) || dir.items.empty()) {
                set_error(context, "OPPO Live Photo candidate missing or malformed Container:Directory.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            const auto& pri_item = dir.items[0];
            if (pri_item.semantic != "Primary") {
                set_error(context, "OPPO Live Photo Container:Directory Primary item must be first.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (pri_item.mime.empty()) {
                set_error(context, "OPPO Live Photo Primary item missing required Mime attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (img_cont != LPB_IMAGE_CONTAINER_JPEG || pri_item.mime != "image/jpeg") {
                set_error(context, "OPPO Live Photo Primary item MIME must be image/jpeg for JPEG container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            const container_item_info* motion_item = nullptr;
            const container_item_info* gainmap_item = nullptr;
            const container_item_info* original_item = nullptr;
            size_t primary_count = 0, motion_count = 0, gainmap_count = 0, original_count = 0;
            for (const auto& item : dir.items) {
                if (item.semantic == "Primary") {
                    ++primary_count;
                } else if (item.semantic == "MotionPhoto") {
                    ++motion_count;
                    motion_item = &item;
                } else if (item.semantic == "GainMap") {
                    ++gainmap_count;
                    gainmap_item = &item;
                } else if (item.semantic == "Original") {
                    ++original_count;
                    original_item = &item;
                } else {
                    set_error(context, "OPPO Live Photo Container:Directory contains unrecognized item.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            if (primary_count != 1 || motion_count != 1 || dir.items.back().semantic != "MotionPhoto") {
                set_error(context, "OPPO Live Photo Container:Directory must have exactly 1 Primary and 1 MotionPhoto (last).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (gainmap_count > 1) {
                set_error(context, "OPPO Live Photo Container:Directory contains duplicate GainMap items.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (original_count > 1) {
                set_error(context, "OPPO Live Photo Container:Directory contains duplicate Original items.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (motion_item->mime != "video/mp4") {
                set_error(context, "OPPO Live Photo MotionPhoto item MIME must be video/mp4.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!motion_item->has_length || motion_item->length < op_vid_len || motion_item->malformed_length) {
                set_error(context, "OPPO MotionPhoto item length is smaller than VideoLength, missing, or malformed.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (gainmap_item != nullptr) {
                if (gainmap_item->mime != "image/jpeg") {
                    set_error(context, "OPPO Live Photo GainMap item MIME must be image/jpeg.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (!gainmap_item->has_length || gainmap_item->length == 0 || gainmap_item->malformed_length) {
                    set_error(context, "OPPO Live Photo GainMap item length is missing, malformed, or zero.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            if (original_item != nullptr) {
                if (original_item->mime != "image/jpeg") {
                    set_error(context, "OPPO Live Photo Original item MIME must be image/jpeg.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (!original_item->has_length || original_item->length == 0 || original_item->malformed_length) {
                    set_error(context, "OPPO Live Photo Original item length is missing, malformed, or zero.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            const uint64_t motion_item_len = motion_item->length;
            if (motion_item_len >= primary_size) {
                set_error(context, "OPPO MotionPhoto item length exceeds file size.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            const uint64_t motion_item_offset = primary_size - motion_item_len;
            const uint64_t video_offset = motion_item_offset;
            const uint64_t pure_vid_len = op_vid_len;
            const uint64_t tail_len = motion_item_len - pure_vid_len;
            const uint64_t tail_offset = video_offset + pure_vid_len;

            if (!is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), video_offset, pure_vid_len)) {
                set_error(context, "OPPO video range is not a valid ISO-BMFF MP4.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            out_facts->protocol = LPB_SOURCE_PROTOCOL_OPPO_LIVE_PHOTO;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = video_offset;
            out_facts->motion_video.file_range.length = pure_vid_len;

            if (tail_len > 0) {
                out_facts->protocol_tail_range.offset = tail_offset;
                out_facts->protocol_tail_range.length = tail_len;
            }

            uint64_t next_res_offset = motion_item_offset;
            if (original_item != nullptr) {
                const uint64_t orig_len = original_item->length;
                if (orig_len >= next_res_offset) {
                    set_error(context, "OPPO Original length exceeds image boundary.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                const uint64_t orig_off = next_res_offset - orig_len;
                if (!is_valid_jpeg_or_composite_media_range(primary_data.data(), primary_data.size(), orig_off, orig_len)) {
                    set_error(context, "OPPO Original range is not a valid JPEG.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                next_res_offset = orig_off;
            }

            if (gainmap_item != nullptr) {
                const uint64_t gm_len = gainmap_item->length;
                if (gm_len >= next_res_offset) {
                    set_error(context, "OPPO GainMap length exceeds image boundary.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                const uint64_t gm_off = next_res_offset - gm_len;
                if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), gm_off, gm_len)) {
                    set_error(context, "OPPO GainMap range is not a valid JPEG.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                out_facts->gain_map.is_present = 1;
                out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
                out_facts->gain_map.file_range.offset = gm_off;
                out_facts->gain_map.file_range.length = gm_len;
                next_res_offset = gm_off;
            }

            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = next_res_offset;

            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, oppo_camera_namespace, "MotionPhotoPrimaryPresentationTimestampUs", cover_time) > 0 ||
                get_global_attribute_i64(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }
            return LPB_RESULT_OK;
        }

        // Google Motion Photo V2 / Xiaomi
        const bool is_google_v2_candidate = has_attribute_name_in_nodes(nodes, google_camera_namespace, "MotionPhoto");
        if (is_google_v2_candidate) {
            if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
                set_error(context, "Google Motion Photo V2 HEIC container is currently unsupported.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (img_cont != LPB_IMAGE_CONTAINER_JPEG) {
                set_error(context, "Google Motion Photo V2 requires JPEG container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            std::string_view mp_val;
            int mp_res = get_global_attribute_string(nodes, google_camera_namespace, "MotionPhoto", mp_val);
            if (mp_res < 0) {
                set_error(context, "Conflicting or malformed MotionPhoto attributes in XMP.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mp_res == 0 || mp_val != "1") {
                set_error(context, "Google Motion Photo is not enabled (MotionPhoto must be 1).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t mp_ver = 0;
            int mp_ver_res = get_global_attribute_u64(nodes, google_camera_namespace, "MotionPhotoVersion", mp_ver);
            if (mp_ver_res < 0) {
                set_error(context, "Conflicting or malformed MotionPhotoVersion attribute in XMP.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mp_ver_res == 0) {
                set_error(context, "Google Motion Photo V2 missing MotionPhotoVersion attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mp_ver != 1) {
                set_error(context, "Unsupported Google Motion Photo version (must be 1).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            container_directory_info dir;
            if (!find_container_directory(nodes, dir) || dir.items.empty()) {
                set_error(context, "Google Motion Photo V2 has MotionPhoto=1 but missing or malformed Container:Directory.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            const auto& pri_item = dir.items[0];
            if (pri_item.semantic != "Primary") {
                set_error(context, "Google Motion Photo V2 Container:Directory Primary item must be first.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (pri_item.mime.empty()) {
                set_error(context, "Google Motion Photo V2 Primary item missing required Mime attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (pri_item.mime != "image/jpeg") {
                set_error(context, "Google Motion Photo V2 Primary item MIME must be image/jpeg for JPEG container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (pri_item.malformed_length) {
                set_error(context, "Google Motion Photo V2 Primary item Length attribute is malformed.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (pri_item.malformed_padding) {
                set_error(context, "Google Motion Photo V2 Primary item Padding attribute is malformed.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            const container_item_info* motion_item = nullptr;
            const container_item_info* gainmap_item = nullptr;
            size_t primary_count = 0, motion_count = 0, gainmap_count = 0;
            for (const auto& item : dir.items) {
                if (item.semantic == "Primary") {
                    ++primary_count;
                } else if (item.semantic == "MotionPhoto") {
                    ++motion_count;
                    motion_item = &item;
                } else if (item.semantic == "GainMap") {
                    ++gainmap_count;
                    gainmap_item = &item;
                } else {
                    set_error(context, "Google Motion Photo V2 Container:Directory contains unrecognized item.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            if (primary_count != 1) {
                set_error(context, "Google Motion Photo V2 must have exactly one Primary item.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (motion_count != 1 || dir.items.back().semantic != "MotionPhoto") {
                set_error(context, "Google Motion Photo V2 must have exactly one MotionPhoto item as the last item.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (gainmap_count > 1) {
                set_error(context, "Google Motion Photo V2 Container:Directory contains duplicate GainMap items.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (motion_item->mime != "video/mp4" && motion_item->mime != "video/quicktime") {
                set_error(context, "Google Motion Photo V2 MotionPhoto item MIME must be video/mp4 or video/quicktime.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (!motion_item->has_length || motion_item->length == 0 || motion_item->malformed_length) {
                set_error(context, "Google Motion Photo V2 missing or malformed MotionPhoto item length.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            if (gainmap_item != nullptr) {
                if (gainmap_item->mime != "image/jpeg") {
                    set_error(context, "Google Motion Photo V2 GainMap item MIME must be image/jpeg.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (!gainmap_item->has_length || gainmap_item->length == 0 || gainmap_item->malformed_length) {
                    set_error(context, "Google Motion Photo V2 GainMap item length must be greater than zero.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            }

            const uint64_t mp_vid_len = motion_item->length;
            if (mp_vid_len >= primary_size) {
                set_error(context, "Google Motion Photo V2 video length exceeds file size.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            const uint64_t vid_offset = primary_size - mp_vid_len;
            if (!is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), vid_offset, mp_vid_len)) {
                set_error(context, "Google Motion Photo V2 video range is not a valid ISO-BMFF container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t next_resource_offset = vid_offset;
            uint64_t gm_offset = 0;
            uint64_t gm_len = 0;
            if (gainmap_item != nullptr) {
                gm_len = gainmap_item->length;
                if (gm_len >= vid_offset) {
                    set_error(context, "Google Motion Photo V2 GainMap length exceeds image offset.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                gm_offset = vid_offset - gm_len;
                if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), gm_offset, gm_len)) {
                    set_error(context, "Google Motion Photo V2 GainMap range is not a valid JPEG.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                next_resource_offset = gm_offset;
            }

            uint64_t pri_len = 0;
            if (pri_item.has_length && pri_item.length > 0) {
                pri_len = pri_item.length;
                if (pri_len > next_resource_offset) {
                    set_error(context, "Google Motion Photo V2 Primary item length exceeds next resource offset.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                if (pri_item.has_padding) {
                    if (pri_len + pri_item.padding != next_resource_offset) {
                        set_error(context, "Google Motion Photo V2 Primary length + padding does not equal next resource offset.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else {
                    if (pri_len != next_resource_offset) {
                        set_error(context, "Google Motion Photo V2 Primary length does not equal next resource offset.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                }
                if (!is_valid_jpeg_media_range(primary_data.data(), primary_data.size(), 0, pri_len)) {
                    set_error(context, "Google Motion Photo V2 Primary JPEG range is not a valid JPEG.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
            } else {
                if (!has_jpeg_end || jpeg_end > next_resource_offset) {
                    set_error(context, "Google Motion Photo V2 cannot determine valid Primary JPEG boundary.");
                    return LPB_RESULT_INVALID_ARGUMENT;
                }
                pri_len = jpeg_end;
                if (pri_item.has_padding) {
                    if (pri_len + pri_item.padding != next_resource_offset) {
                        set_error(context, "Google Motion Photo V2 Primary jpeg_end + padding does not equal next resource offset.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                } else {
                    if (pri_len != next_resource_offset) {
                        set_error(context, "Google Motion Photo V2 contains undeclared bytes between Primary JPEG and next resource.");
                        return LPB_RESULT_INVALID_ARGUMENT;
                    }
                }
            }

            out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MOTION_PHOTO_V2;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = (motion_item->mime == "video/quicktime") ? LPB_VIDEO_CONTAINER_MOV : LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = vid_offset;
            out_facts->motion_video.file_range.length = mp_vid_len;

            if (gainmap_item != nullptr) {
                out_facts->gain_map.is_present = 1;
                out_facts->gain_map.container = LPB_IMAGE_CONTAINER_JPEG;
                out_facts->gain_map.file_range.offset = gm_offset;
                out_facts->gain_map.file_range.length = gm_len;
            }

            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = pri_len;

            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, google_camera_namespace, "MotionPhotoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }
            return LPB_RESULT_OK;
        }

        // Google MicroVideo V1
        const bool is_google_v1_candidate = has_attribute_name_in_nodes(nodes, google_camera_namespace, "MicroVideo") ||
            has_attribute_name_in_nodes(nodes, google_camera_namespace, "MicroVideoOffset");
        if (is_google_v1_candidate) {
            uint64_t mv_val = 0;
            int mv_val_res = get_global_attribute_u64(nodes, google_camera_namespace, "MicroVideo", mv_val);
            if (mv_val_res < 0) {
                set_error(context, "Conflicting or malformed MicroVideo attribute in XMP.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mv_val_res == 0) {
                set_error(context, "Google MicroVideo candidate missing required MicroVideo attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mv_val != 1) {
                set_error(context, "Google MicroVideo is not enabled (MicroVideo must be 1).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t mv_ver = 0;
            int mv_ver_res = get_global_attribute_u64(nodes, google_camera_namespace, "MicroVideoVersion", mv_ver);
            if (mv_ver_res < 0) {
                set_error(context, "Conflicting or malformed MicroVideoVersion attribute in XMP.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mv_ver_res == 0) {
                set_error(context, "Google MicroVideo candidate missing MicroVideoVersion attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mv_ver != 1) {
                set_error(context, "Unsupported Google MicroVideo version (must be 1).");
                return LPB_RESULT_INVALID_ARGUMENT;
            }

            uint64_t mv_offset = 0;
            int mv_res = get_global_attribute_u64(nodes, google_camera_namespace, "MicroVideoOffset", mv_offset);
            if (mv_res < 0) {
                set_error(context, "Conflicting or malformed MicroVideoOffset attribute in XMP.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mv_res == 0) {
                set_error(context, "Google MicroVideo candidate missing MicroVideoOffset attribute.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            if (mv_offset == 0 || mv_offset >= primary_size) {
                set_error(context, "Google MicroVideo V1 offset is zero or exceeds file size.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            const uint64_t vid_offset = primary_size - mv_offset;
            if (!is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), vid_offset, mv_offset)) {
                set_error(context, "Google MicroVideo V1 video range is not a valid ISO-BMFF container.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
            out_facts->protocol = LPB_SOURCE_PROTOCOL_GOOGLE_MICRO_VIDEO_V1;
            out_facts->motion_video.is_present = 1;
            out_facts->motion_video.container = LPB_VIDEO_CONTAINER_MP4;
            out_facts->motion_video.file_range.offset = vid_offset;
            out_facts->motion_video.file_range.length = mv_offset;
            out_facts->primary_image.file_range.offset = 0;
            out_facts->primary_image.file_range.length = vid_offset;
            int64_t cover_time = 0;
            if (get_global_attribute_i64(nodes, google_camera_namespace, "MicroVideoPresentationTimestampUs", cover_time) > 0) {
                out_facts->timing.cover_timestamp_us = cover_time;
            }
            return LPB_RESULT_OK;
        }
    }

    // 5. Single-member Apple CID or Vivo ID check
    if (img_cont != LPB_IMAGE_CONTAINER_UNKNOWN) {
        std::string apple_cid;
        bool has_mn_conflict = false;
        if (extract_apple_cid_from_image(context, primary_data, img_cont, apple_cid, has_mn_conflict)) {
            strncpy_s(out_facts->pairing_identifier, apple_cid.c_str(), _TRUNCATE);
        } else if (has_mn_conflict) {
            set_error(context, "Apple image contains conflicting ContentIdentifiers in MakerNote.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }

        std::string vivo_id;
        if (has_jpeg_end && extract_vivo_id_from_image(primary_data, jpeg_end, vivo_id)) {
            strncpy_s(out_facts->pairing_identifier, vivo_id.c_str(), _TRUNCATE);
        }
    } else if (vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN) {
        std::string apple_cid;
        if (extract_apple_cid_from_video(context, primary_data, apple_cid)) {
            strncpy_s(out_facts->pairing_identifier, apple_cid.c_str(), _TRUNCATE);
        }
        std::string vivo_id;
        if (extract_vivo_id_from_video(primary_data, vivo_id)) {
            strncpy_s(out_facts->pairing_identifier, vivo_id.c_str(), _TRUNCATE);
        }
    }

    // 6. Non-Live validation
    if (img_cont == LPB_IMAGE_CONTAINER_JPEG) {
        if (!has_jpeg_end) {
            set_error(context, "Primary file is a malformed or truncated JPEG.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
        out_facts->primary_image.is_present = 1;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = primary_size;
        out_facts->protocol_tail_range.offset = 0;
        out_facts->protocol_tail_range.length = 0;
        return LPB_RESULT_OK;
    }

    if (img_cont == LPB_IMAGE_CONTAINER_HEIC) {
        if (!is_valid_heic_container(primary_data.data(), primary_data.size())) {
            set_error(context, "Primary file is a malformed HEIC container.");
            return LPB_RESULT_INVALID_ARGUMENT;
        }
        out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
        out_facts->primary_image.is_present = 1;
        out_facts->primary_image.file_range.offset = 0;
        out_facts->primary_image.file_range.length = primary_size;
        return LPB_RESULT_OK;
    }

    if (vid_cont != LPB_VIDEO_CONTAINER_UNKNOWN) {
        if (vid_cont == LPB_VIDEO_CONTAINER_MP4) {
            if (!is_valid_isobmff_media_range(primary_data.data(), primary_data.size(), 0, primary_size)) {
                set_error(context, "Primary file is a malformed MP4 video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        } else if (vid_cont == LPB_VIDEO_CONTAINER_MOV) {
            if (!is_valid_mov_container(primary_data.data(), primary_data.size())) {
                set_error(context, "Primary file is a malformed MOV video.");
                return LPB_RESULT_INVALID_ARGUMENT;
            }
        }
        out_facts->protocol = LPB_SOURCE_PROTOCOL_NON_LIVE;
        out_facts->motion_video.is_present = 1;
        out_facts->motion_video.container = vid_cont;
        out_facts->motion_video.file_range.offset = 0;
        out_facts->motion_video.file_range.length = primary_size;
        return LPB_RESULT_OK;
    }

    set_error(context, "Primary file format is unrecognized or unsupported.");
    out_facts->protocol = LPB_SOURCE_PROTOCOL_UNKNOWN;
    return LPB_RESULT_INVALID_ARGUMENT;
}

} // namespace lpb::media
